#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Server
{
    public sealed class MatchStats
    {
        public long PacketsIn;
        public long BytesIn;
        public long PacketsOut;
        public long BytesOut;
        public long MalformedPackets;
        public long UnknownSenderPackets;
        public long DuplicateInputs;
        public long DroppedBacklogInputs;
        public long ShotsFired;
        public long Hits;
        public long FireBlocked;
        public long Headshots;
        public long ZoneEliminations;
        public long BadTags;
        public long TicketsRejected;
        public long DeltaSnapshots;
        public long FullSnapshots;
        public long Pickups;
        public long PickupsRejected;
        public int PeakPlayers;
    }

    /// <summary>
    /// One authoritative match (BRD §5.1, §11). Transport-agnostic: the host feeds received datagrams to
    /// <see cref="Receive"/>, calls <see cref="Advance"/> at the configured rate and sends <see cref="Outbox"/>.
    /// Clients only ever contribute inputs; movement, hits, damage, eliminations and the winner are decided here.
    /// </summary>
    public sealed partial class Match
    {
        // Keep at most this many unprocessed inputs per player; one is consumed per tick.
        private const int MaxPendingInputs = 4;

        private readonly MatchConfig _cfg;
        private readonly List<PlayerState> _players = new();
        private readonly Dictionary<IPEndPoint, PlayerState> _byEndpoint = new();
        private readonly int _historyLength;
        private readonly bool[] _lootTaken;
        private readonly SnapshotHistory _sentSnapshots = new();
        private readonly HashSet<Guid> _usedTickets = new();
        private readonly Func<DateTimeOffset> _clock;
        private readonly float _dt;
        private ushort _nextId = 1;
        private long _phaseEndTick;
        private long _firstJoinTick = -1;
        private long _endTick;
        private volatile int _publicPhase;
        private volatile int _publicConnected;
        private long _activeStartTick;
        private long _finishTick;
        private float _zoneStartRadius;
        private Vector2[] _zoneCenters = { Vector2.Zero };

        public Match(MatchConfig config, Func<DateTimeOffset>? clock = null)
        {
            _cfg = config;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _dt = 1f / config.TickRate;
            _historyLength = config.Ticks(config.MaxRewindSeconds) + 2;
            _lootTaken = new bool[config.Map.Loot.Count];
            Loot = LootTable.Apply(config.Map, config.LootTable);
            SpawnVehicles();
            WithdrawDisabledLoot();
        }

        /// <summary>Loot of disabled weapons counts as already taken, so it never spawns for anyone (clients hide it too).</summary>
        private void WithdrawDisabledLoot()
        {
            if (_cfg.DisabledWeapons.Count == 0) return;
            IReadOnlyList<LootSpawn> loot = Loot;
            for (int i = 0; i < loot.Count; i++)
                if (loot[i].WeaponIndex >= 0 && _cfg.DisabledWeapons.Contains(loot[i].WeaponIndex)) _lootTaken[i] = true;
        }

        public long Tick { get; private set; }
        public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForPlayers;
        public MatchStats Stats { get; } = new();
        public MatchCompletedPacket? Result { get; private set; }
        public List<(IPEndPoint To, byte[] Data)> Outbox { get; } = new();
        public IReadOnlyList<PlayerState> Players => _players;
        public MatchConfig Config => _cfg;
        /// <summary>Whether loot spawn <paramref name="index"/> can still be picked up.</summary>
        public bool LootAvailable(int index) => !_lootTaken[index];
        /// <summary>The map's loot spots with this match's items (<see cref="MatchConfig.LootTable"/>).</summary>
        public IReadOnlyList<LootSpawn> Loot { get; }
        public bool IsFinished => Phase == MatchPhase.Completed && Tick >= _finishTick;
        public Vector2 ZoneCenter => Zone().Center;
        public float ZoneRadius => Zone().Radius;
        /// <summary>The circle the zone is closing to now.</summary>
        public (Vector2 Center, float Radius) NextZone => (Zone().NextCenter, Zone().NextRadius);
        /// <summary>Phase and connected count as of the last tick; safe to read from other threads (heartbeats).</summary>
        public (MatchPhase Phase, int Connected) PublicStatus => ((MatchPhase)_publicPhase, _publicConnected);
        public double ActiveSeconds => Phase >= MatchPhase.Active ? (Tick - _activeStartTick) * (double)_dt : 0;

        public void Receive(IPEndPoint from, byte[] data, int length)
        {
            Stats.PacketsIn++;
            Stats.BytesIn += length;
            try
            {
                var r = new PacketReader(data, length);
                if (r.Type == PacketType.Hello)
                {
                    HandleHello(from, PacketCodec.DecodeHello(r), data, length);
                    return;
                }
                if (r.Type != PacketType.Input && r.Type != PacketType.Leave)
                {
                    Stats.MalformedPackets++;
                    return;
                }
                if (!_byEndpoint.TryGetValue(from, out PlayerState? p))
                {
                    Stats.UnknownSenderPackets++;
                    return;
                }
                // Authenticated sessions: every input/leave must carry a valid tag from the session key.
                if (p.Auth != null)
                {
                    if (!p.Auth.Verify(data, length))
                    {
                        Stats.BadTags++;
                        return;
                    }
                    r = new PacketReader(data, length - PacketAuth.TagBytes);
                }
                if (r.Type == PacketType.Input) HandleInput(p, PacketCodec.DecodeInput(r));
                else HandleLeave(p);
            }
            catch (PacketFormatException)
            {
                Stats.MalformedPackets++;
            }
        }

        /// <summary>Advances the simulation by exactly one tick (and runs the match's own fill bots).</summary>
        public void Advance()
        {
            ActFillBots();
            AdvanceCore();
            RouteFillBotPackets();
        }

        private void AdvanceCore()
        {
            Tick++;
            UpdateConnections();

            switch (Phase)
            {
                case MatchPhase.WaitingForPlayers:
                    SimulatePlayers(allowFire: false);
                    if (ReadyToCountDown())
                    {
                        Phase = MatchPhase.Countdown;
                        _phaseEndTick = Tick + _cfg.Ticks(_cfg.CountdownSeconds);
                        AddFillBots();
                    }
                    break;
                case MatchPhase.Countdown:
                    SimulatePlayers(allowFire: false);
                    if (ConnectedCount() < _cfg.MinPlayers) Phase = MatchPhase.WaitingForPlayers;
                    else if (Tick >= _phaseEndTick) StartActive();
                    break;
                case MatchPhase.Active:
                    SimulatePlayers(allowFire: true);
                    StepVehicles();
                    StepProjectiles();
                    ApplyZoneDamage();
                    CheckForEnd();
                    break;
                case MatchPhase.Completed:
                    if (Result != null) Broadcast(PacketCodec.Encode(Result));
                    PublishStatus();
                    return;
            }
            PublishStatus();

            RecordHistory();
            BroadcastSnapshots();
        }

        private void PublishStatus()
        {
            _publicPhase = (int)Phase;
            _publicConnected = ConnectedCount();
        }

        private bool ReadyToCountDown()
        {
            int connected = ConnectedCount();
            if (connected == 0) _firstJoinTick = -1;
            else if (_firstJoinTick < 0) _firstJoinTick = Tick;
            if (connected >= _cfg.MaxPlayers) return true;
            if (connected < _cfg.MinPlayers) return false;
            return _cfg.FillWaitSeconds <= 0 || Tick - _firstJoinTick >= _cfg.Ticks(_cfg.FillWaitSeconds);
        }

        /// <summary>Survival in whole seconds from the start of play to elimination (or the end).</summary>
        public int SurvivalSeconds(PlayerState p)
        {
            long until = p.EliminatedAtTick > 0 ? p.EliminatedAtTick : (_endTick > 0 ? _endTick : Tick);
            return (int)Math.Max(0, (until - _activeStartTick) / _cfg.TickRate);
        }

        public int DurationSeconds => (int)Math.Max(0, ((_endTick > 0 ? _endTick : Tick) - _activeStartTick) / _cfg.TickRate);

        // ---- Connection handling ----

        private void HandleHello(IPEndPoint from, HelloPacket hello, byte[] data, int length)
        {
            if (hello.ProtocolVersion != ProtocolInfo.Version)
            {
                Send(from, PacketCodec.EncodeReject(RejectReason.ProtocolMismatch));
                return;
            }

            // Retransmitted Hello from a bound endpoint: answer idempotently without rotating the token.
            if (_byEndpoint.TryGetValue(from, out PlayerState? bound) && hello.Ticket.Length == 0 &&
                (hello.SessionToken == Guid.Empty || hello.SessionToken == bound.SessionToken))
            {
                bound.LastHeardTick = Tick;
                SendWelcome(bound);
                return;
            }

            if (hello.SessionToken != Guid.Empty)
            {
                PlayerState? existing = _players.FirstOrDefault(p => p.SessionToken == hello.SessionToken);
                // In ticket mode a reconnect must also be signed with the session key: a sniffed token is not enough.
                if (existing == null || (existing.Auth != null && !existing.Auth.Verify(data, length)))
                {
                    Send(from, PacketCodec.EncodeReject(RejectReason.InvalidSessionToken));
                    return;
                }
                Rebind(existing, from);
                return;
            }

            JoinTicket? ticket = null;
            if (_cfg.TicketSecret != null)
            {
                RejectReason? why = CheckTicket(hello.Ticket, out ticket);
                if (why.HasValue)
                {
                    Stats.TicketsRejected++;
                    Send(from, PacketCodec.EncodeReject(why.Value));
                    return;
                }
                _usedTickets.Add(ticket!.TicketId);

                // Same account with a fresh ticket (app restarted, new device): take over the existing player.
                PlayerState? same = _players.FirstOrDefault(p => p.AccountId == ticket.AccountId);
                if (same != null)
                {
                    same.Auth?.Dispose();
                    same.Auth = new PacketAuth(JoinTicket.DeriveSessionKey(_cfg.TicketSecret, ticket.TicketId));
                    Rebind(same, from);
                    return;
                }
            }

            if (Phase >= MatchPhase.Active)
            {
                Send(from, PacketCodec.EncodeReject(RejectReason.MatchInProgress));
                return;
            }
            if (_players.Count >= _cfg.MaxPlayers)
            {
                Send(from, PacketCodec.EncodeReject(RejectReason.MatchFull));
                return;
            }

            PlayerState player = AddPlayer(from, ticket?.DisplayName ?? hello.Name);
            if (ticket != null)
            {
                player.AccountId = ticket.AccountId;
                player.Auth = new PacketAuth(JoinTicket.DeriveSessionKey(_cfg.TicketSecret!, ticket.TicketId));
            }
            SendWelcome(player);
        }

        private PlayerState AddPlayer(IPEndPoint from, string name)
        {
            var player = new PlayerState(_nextId++, name, from, _historyLength) { LastHeardTick = Tick };
            player.Motor = MotorState.StandingAt(_cfg.Map.SpawnPoint(_players.Count, _cfg.MaxPlayers));
            player.Combat.Balance = _cfg.WeaponDef;
            player.Combat.Reset(_cfg.Weapon, _cfg.StartWithWeapon, _cfg.StartingReserveAmmo, _cfg.StartingWeaponIndex);
            _players.Add(player);
            _byEndpoint[from] = player;
            Stats.PeakPlayers = Math.Max(Stats.PeakPlayers, _players.Count);
            return player;
        }

        // ---- Fill bots ----

        private readonly Dictionary<IPEndPoint, InProcessBot> _fillBots = new();

        public int FillBotCount => _fillBots.Count;

        /// <summary>Tops the match up to <see cref="MatchConfig.FillBotsTo"/> with bots, without tickets.</summary>
        private void AddFillBots()
        {
            int wanted = Math.Min(_cfg.FillBotsTo, _cfg.MaxPlayers) - _players.Count;
            for (int i = 0; i < wanted; i++)
            {
                // A loopback address real clients never use, so bot traffic can't be confused with a player's.
                var endPoint = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 40_000 + _fillBots.Count);
                var bot = new InProcessBot(endPoint, _cfg.ZoneSeed * 31 + _fillBots.Count, _cfg.Map, $"Bot {_fillBots.Count + 1}",
                    Bots.BotSkill.For(_cfg.BotDifficulty));
                bot.Attach(this);
                PlayerState p = AddPlayer(endPoint, bot.Name);
                p.IsBot = true;
                _fillBots[endPoint] = bot;
                SendWelcome(p);
            }
        }

        private void ActFillBots()
        {
            if (_fillBots.Count == 0 || Phase == MatchPhase.Completed) return;
            double time = Tick * (double)_dt;
            foreach (InProcessBot bot in _fillBots.Values) bot.Act(this, time);
        }

        /// <summary>Hands the fill bots their packets and keeps them out of the outbox the network host sends.</summary>
        private void RouteFillBotPackets()
        {
            if (_fillBots.Count == 0) return;
            Outbox.RemoveAll(o =>
            {
                if (!_fillBots.TryGetValue(o.To, out InProcessBot? bot)) return false;
                bot.Receive(o.Data);
                return true;
            });
        }

        private RejectReason? CheckTicket(byte[] bytes, out JoinTicket? ticket)
        {
            if (!JoinTicket.TryVerify(bytes, _cfg.TicketSecret!, out ticket) || ticket == null) return RejectReason.InvalidTicket;
            if (ticket.ExpiresAt < _clock()) return RejectReason.TicketExpired;
            if (ticket.MatchId != _cfg.MatchId) return RejectReason.WrongMatch;
            if (_usedTickets.Contains(ticket.TicketId)) return RejectReason.TicketReused;
            return null;
        }

        /// <summary>Moves a player to a new address and rotates its token so the old one cannot be reused.</summary>
        private void Rebind(PlayerState p, IPEndPoint from)
        {
            _byEndpoint.Remove(p.EndPoint);
            p.EndPoint = from;
            p.Connected = true;
            p.LastHeardTick = Tick;
            p.SessionToken = Guid.NewGuid();
            p.PendingInputs.Clear();
            p.LastAckedSnapshot = 0; // the client may have lost its snapshot history: start from a full snapshot
            _byEndpoint[from] = p;
            SendWelcome(p);
        }

        private void SendWelcome(PlayerState p) =>
            Send(p.EndPoint, PacketCodec.Encode(new WelcomePacket
            {
                PlayerId = p.Id,
                SessionToken = p.SessionToken,
                TickRate = (ushort)_cfg.TickRate,
                WeaponBalanceVersion = (ushort)WeaponCatalog.BalanceVersion,
                MaxPlayers = (byte)_cfg.MaxPlayers,
                MapVersion = _cfg.Map.Version,
                LootTable = _cfg.LootTable,
            }));

        private void HandleInput(PlayerState p, List<InputCommand> inputs)
        {
            p.LastHeardTick = Tick;
            p.Connected = true;

            // The newest input says which snapshot the client last decoded: the next delta baseline.
            InputCommand newest = inputs[0];
            foreach (InputCommand c in inputs)
                if (c.Sequence > newest.Sequence) newest = c;
            if (newest.Sequence > p.LastProcessedSequence && newest.AckTick <= Tick) p.LastAckedSnapshot = newest.AckTick;

            foreach (InputCommand cmd in inputs)
            {
                // Replay / duplicate protection: each sequence number is processed at most once, in order.
                if (cmd.Sequence <= p.LastProcessedSequence || p.PendingInputs.Any(i => i.Sequence == cmd.Sequence))
                {
                    Stats.DuplicateInputs++;
                    continue;
                }
                p.PendingInputs.Add(cmd);
            }
            p.PendingInputs.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

            // A burst of inputs cannot buy extra movement: drop the oldest beyond the backlog limit.
            while (p.PendingInputs.Count > MaxPendingInputs)
            {
                p.LastProcessedSequence = p.PendingInputs[0].Sequence;
                p.PendingInputs.RemoveAt(0);
                Stats.DroppedBacklogInputs++;
            }
        }

        private void HandleLeave(PlayerState p)
        {
            if (Phase == MatchPhase.Active && p.Alive) Eliminate(p, null);
            if (Phase < MatchPhase.Active) RemovePlayer(p);
            else p.Connected = false;
        }

        private void UpdateConnections()
        {
            int timeout = _cfg.Ticks(_cfg.DisconnectTimeoutSeconds);
            int grace = _cfg.Ticks(_cfg.ReconnectGraceSeconds);
            foreach (PlayerState p in _players.ToList())
            {
                long silent = Tick - p.LastHeardTick;
                if (p.Connected && silent > timeout) p.Connected = false;
                if (p.Connected || silent <= grace) continue;

                if (Phase < MatchPhase.Active) RemovePlayer(p);
                else if (Phase == MatchPhase.Active && p.Alive) Eliminate(p, null);
            }
        }

        private void RemovePlayer(PlayerState p)
        {
            _players.Remove(p);
            _byEndpoint.Remove(p.EndPoint);
        }

        private int ConnectedCount() => _players.Count(p => p.Connected);

        private int AliveCount() => _players.Count(p => p.Alive);

        // ---- Simulation ----

        private void StartActive()
        {
            Phase = MatchPhase.Active;
            _activeStartTick = Tick;
            _zoneStartRadius = _cfg.Map.HalfSize * 1.4143f; // covers the map corners
            _zoneCenters = PlanZoneCenters();
            Array.Clear(_lootTaken, 0, _lootTaken.Length);
            WithdrawDisabledLoot();
            ClearItems();
            SpawnVehicles();
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerState p = _players[i];
                p.Motor = MotorState.StandingAt(_cfg.Map.SpawnPoint(i, _players.Count));
                p.Combat.Reset(_cfg.Weapon, _cfg.StartWithWeapon, _cfg.StartingReserveAmmo, _cfg.StartingWeaponIndex);
                p.Alive = true;
                p.Vehicle = null;
                Array.Fill(p.History, p.Motor);
            }
        }

        private void SimulatePlayers(bool allowFire)
        {
            foreach (PlayerState p in _players)
            {
                InputCommand cmd;
                if (p.PendingInputs.Count > 0)
                {
                    cmd = p.PendingInputs[0];
                    p.PendingInputs.RemoveAt(0);
                    p.LastProcessedSequence = cmd.Sequence;
                }
                else
                {
                    // No input this tick (loss or idle): stand still, keep facing and stance.
                    cmd = new InputCommand
                    {
                        Yaw = p.Yaw,
                        Pitch = p.Pitch,
                        AckTick = (uint)Tick,
                        Buttons = (p.Motor.Crouched ? InputButtons.Crouch : InputButtons.None) | (p.Aiming ? InputButtons.Aim : InputButtons.None),
                    };
                }

                p.Yaw = Simulation.WrapYaw(cmd.Yaw);
                p.Pitch = Simulation.SanitizeAngle(cmd.Pitch, GameRules.MaxPitchRadians);
                p.Aiming = cmd.Has(InputButtons.Aim) && p.Combat.HasWeapon && p.Alive && (p.Vehicle == null || p.Seat > 0);
                if (!p.Alive) continue;

                if (p.Vehicle != null)
                {
                    InCar(p, cmd, allowFire);
                    continue;
                }
                Simulation.Step(ref p.Motor, cmd, _dt, _cfg.Map);
                p.Combat.Update(Tick, ActiveWeapon(p));
                if (!allowFire) continue;

                if (cmd.Has(InputButtons.Vehicle))
                {
                    EnterOrExit(p);
                    if (p.Vehicle != null) continue;
                }
                if (cmd.Has(InputButtons.Interact)) TryPickUp(p, cmd.Target);
                if (cmd.Has(InputButtons.DropItem) && p.Combat.Drop(cmd.Item, cmd.Target)) PlaceDrops(p);
                if (cmd.Has(InputButtons.SwitchWeapon))
                    p.Combat.TrySwitch(Tick, p.Combat.WeaponIn(1 - p.Combat.ActiveSlot) ?? _cfg.Weapon, _cfg.TickRate);
                if (cmd.Has(InputButtons.Heal)) p.Combat.TryStartHeal(Tick, _cfg.TickRate);
                if (cmd.Has(InputButtons.UseItem)) p.Combat.TryStartHeal(Tick, _cfg.TickRate, cmd.Item);
                if (cmd.Has(InputButtons.Throw)) Throw(p, cmd.Item);
                if (cmd.Has(InputButtons.Reload)) p.Combat.TryStartReload(Tick, ActiveWeapon(p), _cfg.TickRate);
                if (!cmd.Has(InputButtons.Fire)) continue;

                FireResult result = p.Combat.TryFire(Tick, ActiveWeapon(p), _cfg.TickRate);
                if (result == FireResult.Fired) Fire(p, cmd);
                else Stats.FireBlocked++;
            }
        }

        private WeaponDefinition ActiveWeapon(PlayerState p) => p.Combat.ActiveWeapon ?? _cfg.Weapon;

        /// <summary>
        /// A player in a car: the driver's stick drives it (Jump is the handbrake) and the driver can't use weapons; the
        /// passenger can shoot, reload, switch and heal, but not pick up, drop or throw.
        /// </summary>
        private void InCar(PlayerState p, InputCommand cmd, bool allowFire)
        {
            Vehicle v = p.Vehicle!;
            p.Combat.Update(Tick, ActiveWeapon(p));
            if (p.Seat == 0)
            {
                v.Throttle = cmd.MoveZ;
                v.Steer = cmd.MoveX;
                v.Handbrake = cmd.Has(InputButtons.Jump);
            }
            if (!allowFire) return;
            if (cmd.Has(InputButtons.Vehicle))
            {
                EnterOrExit(p);
                return;
            }
            if (cmd.Has(InputButtons.Heal)) p.Combat.TryStartHeal(Tick, _cfg.TickRate);
            if (cmd.Has(InputButtons.UseItem)) p.Combat.TryStartHeal(Tick, _cfg.TickRate, cmd.Item);
            if (p.Seat == 0) return;
            if (cmd.Has(InputButtons.SwitchWeapon))
                p.Combat.TrySwitch(Tick, p.Combat.WeaponIn(1 - p.Combat.ActiveSlot) ?? _cfg.Weapon, _cfg.TickRate);
            if (cmd.Has(InputButtons.Reload)) p.Combat.TryStartReload(Tick, ActiveWeapon(p), _cfg.TickRate);
            if (!cmd.Has(InputButtons.Fire)) return;
            FireResult result = p.Combat.TryFire(Tick, ActiveWeapon(p), _cfg.TickRate);
            if (result == FireResult.Fired) Fire(p, cmd);
            else Stats.FireBlocked++;
        }

        private readonly List<TargetCapsule> _targets = new();
        private readonly List<PlayerState> _targetPlayers = new();
        private readonly List<PelletResult> _pellets = new();

        private void Fire(PlayerState shooter, InputCommand cmd)
        {
            WeaponDefinition w = ActiveWeapon(shooter);
            Stats.ShotsFired++;
            shooter.ShotCounter++;

            MotorState m = shooter.Motor;
            bool moving = cmd.MoveX * cmd.MoveX + cmd.MoveZ * cmd.MoveZ > 0.01f;
            float spread = w.SpreadFor(moving, m.Crouched, !m.Grounded, cmd.Has(InputButtons.Aim));

            // Lag compensation: test targets where the shooter saw them, but never further back than MaxRewind.
            long maxRewind = _historyLength - 2;
            long rewindTick = Math.Clamp((long)cmd.AckTick, Tick - maxRewind, Tick);
            _targets.Clear();
            _targetPlayers.Clear();
            foreach (PlayerState target in _players)
            {
                if (target == shooter || !target.Alive) continue;
                MotorState then = MotorAt(target, rewindTick);
                _targets.Add(new TargetCapsule { Feet = then.Position, Height = then.Height });
                _targetPlayers.Add(target);
            }

            Ballistics.Fire(m.Eye, shooter.Yaw, shooter.Pitch, spread, Simulation.ShotSeed(shooter.Id, cmd.Sequence), w, _cfg.Map, _targets, _pellets);
            foreach (PelletResult pellet in _pellets)
            {
                if (pellet.Target < 0) continue;
                PlayerState victim = _targetPlayers[pellet.Target];
                if (!victim.Alive) continue;
                Stats.Hits++;
                if (pellet.Headshot) Stats.Headshots++;
                victim.Combat.ApplyDamage(pellet.Damage, pellet.Headshot);
                victim.Combat.CancelHeal();
                if (!victim.Combat.Alive) Eliminate(victim, shooter);
            }
        }

        private MotorState MotorAt(PlayerState p, long tick)
        {
            if (tick >= Tick || Tick - tick >= _historyLength) return p.Motor;
            return p.History[tick % _historyLength];
        }

        private void RecordHistory()
        {
            foreach (PlayerState p in _players) p.History[Tick % _historyLength] = p.Motor;
        }

        private float CurrentZoneRadius() => Zone().Radius;

        /// <summary>
        /// Where each circle closes: the first anywhere its whole circle fits on the map, each later one at a random
        /// point inside the one before (so it is always reachable from it). All at the centre when ZoneSeed is 0.
        /// </summary>
        private Vector2[] PlanZoneCenters()
        {
            var centers = new Vector2[_cfg.ZoneStages.Count + 1];
            uint state = (uint)_cfg.ZoneSeed;
            float Next()
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return (state & 0xFFFFFF) / (float)0x1000000;
            }
            float prevRadius = _zoneStartRadius;
            for (int i = 0; i < _cfg.ZoneStages.Count; i++)
            {
                float end = _cfg.ZoneStages[i].EndRadius;
                Vector2 c = centers[i];
                if (_cfg.ZoneSeed != 0)
                {
                    float reach = i == 0 ? _cfg.Map.HalfSize - end : prevRadius - end;
                    double angle = Next() * Math.PI * 2, dist = Math.Sqrt(Next()) * Math.Max(0, reach) * 0.85;
                    c = centers[i] + new Vector2((float)(Math.Sin(angle) * dist), (float)(Math.Cos(angle) * dist));
                    float limit = Math.Max(0, _cfg.Map.HalfSize - end);
                    c = new Vector2(Math.Clamp(c.X, -limit, limit), Math.Clamp(c.Y, -limit, limit));
                }
                centers[i + 1] = c;
                prevRadius = end;
            }
            return centers;
        }

        /// <summary>The zone now: current circle, the circle it is closing to, and the stage (0-based).</summary>
        private (Vector2 Center, float Radius, Vector2 NextCenter, float NextRadius, int Stage) Zone()
        {
            float start = _cfg.Map.HalfSize * 1.4143f;
            if (Phase < MatchPhase.Active || _cfg.ZoneStages.Count == 0) return (Vector2.Zero, start, Vector2.Zero, start, 0);
            float elapsed = (Tick - _activeStartTick) * _dt;
            float radius = _zoneStartRadius;
            for (int i = 0; i < _cfg.ZoneStages.Count; i++)
            {
                ZoneStage s = _cfg.ZoneStages[i];
                Vector2 from = _zoneCenters[i], to = _zoneCenters[i + 1];
                if (elapsed < s.HoldSeconds) return (from, radius, to, s.EndRadius, i);
                elapsed -= s.HoldSeconds;
                if (elapsed < s.ShrinkSeconds)
                {
                    float t = elapsed / s.ShrinkSeconds;
                    return (Vector2.Lerp(from, to, t), radius + (s.EndRadius - radius) * t, to, s.EndRadius, i);
                }
                elapsed -= s.ShrinkSeconds;
                radius = s.EndRadius;
            }
            Vector2 last = _zoneCenters[_cfg.ZoneStages.Count];
            return (last, radius, last, radius, _cfg.ZoneStages.Count - 1);
        }

        private void ApplyZoneDamage()
        {
            var zone = Zone();
            float radius = zone.Radius;
            float damage = _cfg.ZoneDamagePerSecond * (1f + _cfg.ZoneDamageGrowth * zone.Stage) * _dt;
            foreach (PlayerState p in _players)
            {
                if (!p.Alive) continue;
                float dist = Vector2.Distance(new Vector2(p.Position.X, p.Position.Z), zone.Center);
                if (dist <= radius) continue;
                p.Health -= damage;
                if (p.Health > 0) continue;
                Stats.ZoneEliminations++;
                Eliminate(p, null);
            }
        }

        private void Eliminate(PlayerState victim, PlayerState? killer)
        {
            if (!victim.Alive) return;
            victim.Alive = false;
            victim.Health = 0;
            victim.EliminatedAtTick = Tick;
            victim.Placement = AliveCount() + 1;
            if (victim.Vehicle != null) Exit(victim, hurt: false); // out of the car first, so the loot lands beside it
            victim.Combat.DropAll(); // everything they carried is left where they fell
            PlaceDrops(victim);
            if (killer != null) killer.Kills++;
            Broadcast(PacketCodec.Encode(new EliminatedPacket
            {
                VictimId = victim.Id,
                KillerId = killer?.Id ?? ProtocolInfo.NoPlayer,
                Placement = (byte)victim.Placement,
            }));
        }

        private void CheckForEnd()
        {
            int alive = AliveCount();
            if (alive <= 1)
            {
                // With one survivor they win; if the last players died on the same tick, the last eliminated
                // already holds placement 1.
                PlayerState? winner = _players.FirstOrDefault(p => p.Alive) ?? _players.FirstOrDefault(p => p.Placement == 1);
                if (winner != null) winner.Placement = 1;
                Complete(winner != null ? CompletionReason.Winner : CompletionReason.Cancelled, winner);
                return;
            }
            if (Tick - _activeStartTick >= _cfg.Ticks(_cfg.MaxMatchSeconds))
            {
                int place = 1;
                foreach (PlayerState p in _players.Where(p => p.Alive).OrderByDescending(p => p.Health)) p.Placement = place++;
                Complete(CompletionReason.Timeout, null);
            }
        }

        private void Complete(CompletionReason reason, PlayerState? winner)
        {
            Phase = MatchPhase.Completed;
            _endTick = Tick;
            _finishTick = Tick + _cfg.Ticks(_cfg.ResultBroadcastSeconds);
            Result = new MatchCompletedPacket
            {
                Reason = reason,
                WinnerId = winner?.Id ?? ProtocolInfo.NoPlayer,
                Placements = _players
                    .OrderBy(p => p.Placement)
                    .Select(p => new PlacementEntry { PlayerId = p.Id, Placement = (byte)p.Placement, Kills = (byte)Math.Min(255, p.Kills) })
                    .ToList(),
            };
            Broadcast(PacketCodec.Encode(Result));
        }

        // ---- Output ----

        private void BroadcastSnapshots()
        {
            if (Tick % Math.Max(1, _cfg.SnapshotEveryTicks) != 0) return;
            var zone = Zone();
            var snapshot = new SnapshotPacket
            {
                Tick = (uint)Tick,
                Phase = Phase,
                PhaseTicksLeft = Phase == MatchPhase.Countdown ? (ushort)Math.Max(0, _phaseEndTick - Tick) : (ushort)0,
                ZoneX = zone.Center.X,
                ZoneZ = zone.Center.Y,
                ZoneRadius = zone.Radius,
                NextZoneX = zone.NextCenter.X,
                NextZoneZ = zone.NextCenter.Y,
                NextZoneRadius = zone.NextRadius,
                AliveCount = (byte)AliveCount(),
                LootTaken = PackLootTaken(),
            };
            foreach (PlayerState p in _players)
            {
                var flags = PlayerFlags.None;
                if (p.Alive) flags |= PlayerFlags.Alive;
                if (p.Connected) flags |= PlayerFlags.Connected;
                if (p.Combat.Reloading) flags |= PlayerFlags.Reloading;
                if (p.Combat.Healing) flags |= PlayerFlags.Healing;
                if (p.Combat.HasWeapon) flags |= PlayerFlags.HasWeapon;
                if (p.Motor.Crouched) flags |= PlayerFlags.Crouched;
                if (p.Motor.Grounded) flags |= PlayerFlags.Grounded;
                if (p.Aiming) flags |= PlayerFlags.Aiming;
                snapshot.Players.Add(Quantize.Normalize(new SnapshotEntry
                {
                    PlayerId = p.Id,
                    X = p.Position.X,
                    Y = p.Position.Y,
                    Z = p.Position.Z,
                    Yaw = p.Yaw,
                    Pitch = p.Pitch,
                    Health = (byte)Math.Clamp(Math.Ceiling(p.Health), 0, 255),
                    Armor = (byte)Math.Clamp(Math.Ceiling(p.Combat.Armor), 0, 255),
                    Ammo = (byte)Math.Clamp(p.Ammo, 0, 255),
                    Flags = flags,
                    Weapon = p.Combat.HasWeapon ? (byte)p.Combat.ActiveWeaponIndex : WeaponCatalog.None,
                    ShotCounter = p.ShotCounter,
                }));
            }
            FillVehicles(snapshot);
            _sentSnapshots.Add(snapshot);

            foreach (PlayerState p in _players)
            {
                if (!p.Connected) continue;
                snapshot.AckSequence = p.LastProcessedSequence;
                snapshot.SelfReserveAmmo = (ushort)Math.Clamp(p.Combat.ReserveAmmo, 0, ushort.MaxValue);
                snapshot.SelfMedkits = (byte)Math.Clamp(p.Combat.Medkits, 0, 255);
                snapshot.SelfActiveSlot = (byte)p.Combat.ActiveSlot;
                for (int slot = 0; slot < Combatant.SlotCount; slot++)
                {
                    int w = p.Combat.SlotWeapon(slot);
                    snapshot.SelfSlotWeapon[slot] = w >= 0 ? (byte)w : WeaponCatalog.None;
                    snapshot.SelfSlotAmmo[slot] = (byte)Math.Clamp(p.Combat.SlotAmmo(slot), 0, 255);
                }
                snapshot.SelfVerticalVelocity = p.Motor.VerticalVelocity;
                snapshot.SelfX = p.Position.X;
                snapshot.SelfY = p.Position.Y;
                snapshot.SelfZ = p.Position.Z;

                SnapshotPacket? baseline = Tick - p.LastAckedSnapshot < SnapshotHistory.Size ? _sentSnapshots.Get(p.LastAckedSnapshot) : null;
                if (baseline == snapshot) baseline = null;
                if (baseline != null) Stats.DeltaSnapshots++;
                else Stats.FullSnapshots++;
                bool sendGround = FillSelf(snapshot, p, baseline);
                Send(p.EndPoint, SnapshotCodec.Encode(snapshot, baseline, sendGround));
            }
        }

        private byte[] PackLootTaken()
        {
            var bits = new byte[(_lootTaken.Length + 7) / 8];
            for (int i = 0; i < _lootTaken.Length; i++)
                if (_lootTaken[i]) bits[i / 8] |= (byte)(1 << (i % 8));
            return bits;
        }

        private void Broadcast(byte[] data)
        {
            foreach (PlayerState p in _players)
                if (p.Connected) Send(p.EndPoint, data);
        }

        private void Send(IPEndPoint to, byte[] data) => Outbox.Add((to, data));
    }
}
