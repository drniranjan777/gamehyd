using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;

namespace RWBR.MatchServer;

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
public sealed class Match
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

    public Match(MatchConfig config, Func<DateTimeOffset>? clock = null)
    {
        _cfg = config;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _dt = 1f / config.TickRate;
        _historyLength = config.Ticks(config.MaxRewindSeconds) + 2;
        _lootTaken = new bool[config.Map.Loot.Count];
        WithdrawDisabledLoot();
    }

    /// <summary>Loot of disabled weapons counts as already taken, so it never spawns for anyone (clients hide it too).</summary>
    private void WithdrawDisabledLoot()
    {
        if (_cfg.DisabledWeapons.Count == 0) return;
        IReadOnlyList<LootSpawn> loot = _cfg.Map.Loot;
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
    public bool IsFinished => Phase == MatchPhase.Completed && Tick >= _finishTick;
    public Vector2 ZoneCenter => Vector2.Zero;
    public float ZoneRadius => CurrentZoneRadius();
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

    /// <summary>Advances the simulation by exactly one tick.</summary>
    public void Advance()
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
                }
                break;
            case MatchPhase.Countdown:
                SimulatePlayers(allowFire: false);
                if (ConnectedCount() < _cfg.MinPlayers) Phase = MatchPhase.WaitingForPlayers;
                else if (Tick >= _phaseEndTick) StartActive();
                break;
            case MatchPhase.Active:
                SimulatePlayers(allowFire: true);
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

        var player = new PlayerState(_nextId++, ticket?.DisplayName ?? hello.Name, from, _historyLength) { LastHeardTick = Tick };
        if (ticket != null)
        {
            player.AccountId = ticket.AccountId;
            player.Auth = new PacketAuth(JoinTicket.DeriveSessionKey(_cfg.TicketSecret!, ticket.TicketId));
        }
        player.Motor = MotorState.StandingAt(_cfg.Map.SpawnPoint(_players.Count, _cfg.MaxPlayers));
        player.Combat.Reset(_cfg.Weapon, _cfg.StartWithWeapon, _cfg.StartingReserveAmmo, _cfg.StartingWeaponIndex);
        _players.Add(player);
        _byEndpoint[from] = player;
        Stats.PeakPlayers = Math.Max(Stats.PeakPlayers, _players.Count);
        SendWelcome(player);
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
        Array.Clear(_lootTaken);
        WithdrawDisabledLoot();
        for (int i = 0; i < _players.Count; i++)
        {
            PlayerState p = _players[i];
            p.Motor = MotorState.StandingAt(_cfg.Map.SpawnPoint(i, _players.Count));
            p.Combat.Reset(_cfg.Weapon, _cfg.StartWithWeapon, _cfg.StartingReserveAmmo, _cfg.StartingWeaponIndex);
            p.Alive = true;
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
            p.Aiming = cmd.Has(InputButtons.Aim) && p.Combat.HasWeapon && p.Alive;
            if (!p.Alive) continue;

            Simulation.Step(ref p.Motor, cmd, _dt, _cfg.Map);
            p.Combat.Update(Tick, ActiveWeapon(p));
            if (!allowFire) continue;

            if (cmd.Has(InputButtons.Interact)) TryPickUp(p);
            if (cmd.Has(InputButtons.SwitchWeapon))
                p.Combat.TrySwitch(Tick, _cfg.WeaponDef(p.Combat.SlotWeapon(1 - p.Combat.ActiveSlot)), _cfg.TickRate);
            if (cmd.Has(InputButtons.Heal)) p.Combat.TryStartHeal(Tick, _cfg.TickRate);
            if (cmd.Has(InputButtons.Reload)) p.Combat.TryStartReload(Tick, ActiveWeapon(p), _cfg.TickRate);
            if (!cmd.Has(InputButtons.Fire)) continue;

            FireResult result = p.Combat.TryFire(Tick, ActiveWeapon(p), _cfg.TickRate);
            if (result == FireResult.Fired) Fire(p, cmd);
            else Stats.FireBlocked++;
        }
    }

    /// <summary>Server-validated pickup of the nearest untaken, useful loot within reach.</summary>
    private void TryPickUp(PlayerState p)
    {
        IReadOnlyList<LootSpawn> loot = _cfg.Map.Loot;
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < loot.Count; i++)
        {
            if (_lootTaken[i] || !Simulation.CanReach(p.Motor, loot[i].Position) || !p.Combat.CanPickUp(loot[i])) continue;
            float d = Vector3.DistanceSquared(p.Position, loot[i].Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        if (best < 0)
        {
            Stats.PickupsRejected++;
            return;
        }
        _lootTaken[best] = true;
        p.Combat.PickUp(loot[best], _cfg.WeaponDef(loot[best].WeaponIndex));
        Stats.Pickups++;
    }

    private WeaponDefinition ActiveWeapon(PlayerState p) => _cfg.WeaponDef(p.Combat.ActiveWeaponIndex);

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
            victim.Combat.ApplyDamage(pellet.Damage);
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

    private float CurrentZoneRadius()
    {
        if (Phase < MatchPhase.Active) return _cfg.Map.HalfSize * 1.4143f;
        float elapsed = (Tick - _activeStartTick) * _dt;
        float radius = _zoneStartRadius;
        foreach (ZoneStage s in _cfg.ZoneStages)
        {
            if (elapsed < s.HoldSeconds) return radius;
            elapsed -= s.HoldSeconds;
            if (elapsed < s.ShrinkSeconds) return radius + (s.EndRadius - radius) * (elapsed / s.ShrinkSeconds);
            elapsed -= s.ShrinkSeconds;
            radius = s.EndRadius;
        }
        return radius;
    }

    private void ApplyZoneDamage()
    {
        float radius = CurrentZoneRadius();
        float damage = _cfg.ZoneDamagePerSecond * _dt;
        foreach (PlayerState p in _players)
        {
            if (!p.Alive) continue;
            float dist = new Vector2(p.Position.X, p.Position.Z).Length();
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
        var snapshot = new SnapshotPacket
        {
            Tick = (uint)Tick,
            Phase = Phase,
            PhaseTicksLeft = Phase == MatchPhase.Countdown ? (ushort)Math.Max(0, _phaseEndTick - Tick) : (ushort)0,
            ZoneX = 0,
            ZoneZ = 0,
            ZoneRadius = CurrentZoneRadius(),
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
            Send(p.EndPoint, SnapshotCodec.Encode(snapshot, baseline));
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
