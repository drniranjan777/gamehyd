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
    private readonly float _dt;
    private ushort _nextId = 1;
    private long _phaseEndTick;
    private long _activeStartTick;
    private long _finishTick;
    private float _zoneStartRadius;

    public Match(MatchConfig config)
    {
        _cfg = config;
        _dt = 1f / config.TickRate;
        _historyLength = config.Ticks(config.MaxRewindSeconds) + 2;
    }

    public long Tick { get; private set; }
    public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForPlayers;
    public MatchStats Stats { get; } = new();
    public MatchCompletedPacket? Result { get; private set; }
    public List<(IPEndPoint To, byte[] Data)> Outbox { get; } = new();
    public IReadOnlyList<PlayerState> Players => _players;
    public MatchConfig Config => _cfg;
    public bool IsFinished => Phase == MatchPhase.Completed && Tick >= _finishTick;
    public Vector2 ZoneCenter => Vector2.Zero;
    public float ZoneRadius => CurrentZoneRadius();
    public double ActiveSeconds => Phase >= MatchPhase.Active ? (Tick - _activeStartTick) * (double)_dt : 0;

    public void Receive(IPEndPoint from, byte[] data, int length)
    {
        Stats.PacketsIn++;
        Stats.BytesIn += length;
        try
        {
            var r = new PacketReader(data, length);
            switch (r.Type)
            {
                case PacketType.Hello:
                    HandleHello(from, PacketCodec.DecodeHello(r));
                    break;
                case PacketType.Input:
                    HandleInput(from, PacketCodec.DecodeInput(r));
                    break;
                case PacketType.Leave:
                    HandleLeave(from);
                    break;
                default:
                    Stats.MalformedPackets++;
                    break;
            }
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
                if (ConnectedCount() >= _cfg.MinPlayers)
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
                return;
        }

        RecordHistory();
        BroadcastSnapshots();
    }

    // ---- Connection handling ----

    private void HandleHello(IPEndPoint from, HelloPacket hello)
    {
        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            Send(from, PacketCodec.EncodeReject(RejectReason.ProtocolMismatch));
            return;
        }

        // Retransmitted Hello from a bound endpoint: answer idempotently without rotating the token.
        if (_byEndpoint.TryGetValue(from, out PlayerState? bound))
        {
            if (hello.SessionToken == Guid.Empty || hello.SessionToken == bound.SessionToken)
            {
                bound.LastHeardTick = Tick;
                SendWelcome(bound);
                return;
            }
        }

        if (hello.SessionToken != Guid.Empty)
        {
            PlayerState? existing = _players.FirstOrDefault(p => p.SessionToken == hello.SessionToken);
            if (existing == null)
            {
                Send(from, PacketCodec.EncodeReject(RejectReason.InvalidSessionToken));
                return;
            }
            // Reconnect: rebind to the new endpoint and rotate the token so the old one cannot be reused.
            _byEndpoint.Remove(existing.EndPoint);
            existing.EndPoint = from;
            existing.Connected = true;
            existing.LastHeardTick = Tick;
            existing.SessionToken = Guid.NewGuid();
            existing.PendingInputs.Clear();
            _byEndpoint[from] = existing;
            SendWelcome(existing);
            return;
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

        var player = new PlayerState(_nextId++, hello.Name, from, _historyLength)
        {
            LastHeardTick = Tick,
            Ammo = _cfg.Weapon.MagazineSize,
        };
        player.Position = _cfg.Map.SpawnPoint(_players.Count, _cfg.MaxPlayers);
        _players.Add(player);
        _byEndpoint[from] = player;
        Stats.PeakPlayers = Math.Max(Stats.PeakPlayers, _players.Count);
        SendWelcome(player);
    }

    private void SendWelcome(PlayerState p) =>
        Send(p.EndPoint, PacketCodec.Encode(new WelcomePacket
        {
            PlayerId = p.Id,
            SessionToken = p.SessionToken,
            TickRate = (ushort)_cfg.TickRate,
            MaxPlayers = (byte)_cfg.MaxPlayers,
            MapVersion = _cfg.Map.Version,
        }));

    private void HandleInput(IPEndPoint from, List<InputCommand> inputs)
    {
        if (!_byEndpoint.TryGetValue(from, out PlayerState? p))
        {
            Stats.UnknownSenderPackets++;
            return;
        }
        p.LastHeardTick = Tick;
        p.Connected = true;

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

    private void HandleLeave(IPEndPoint from)
    {
        if (!_byEndpoint.TryGetValue(from, out PlayerState? p)) return;
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
        for (int i = 0; i < _players.Count; i++)
        {
            PlayerState p = _players[i];
            p.Position = _cfg.Map.SpawnPoint(i, _players.Count);
            p.Health = GameRules.MaxHealth;
            p.Alive = true;
            p.Ammo = _cfg.Weapon.MagazineSize;
            p.ReloadDoneTick = -1;
            p.NextFireTick = 0;
            Array.Fill(p.History, p.Position);
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
                // No input this tick (loss or idle): stand still, keep facing.
                cmd = new InputCommand { Yaw = p.Yaw, Pitch = p.Pitch, AckTick = (uint)Tick };
            }

            p.Yaw = Simulation.WrapYaw(cmd.Yaw);
            p.Pitch = Simulation.SanitizeAngle(cmd.Pitch, GameRules.MaxPitchRadians);
            if (!p.Alive) continue;

            p.Position = Simulation.Move(p.Position, cmd, _dt, _cfg.Map);

            if (p.Reloading && Tick >= p.ReloadDoneTick)
            {
                p.Ammo = _cfg.Weapon.MagazineSize;
                p.ReloadDoneTick = -1;
            }
            if (!allowFire) continue;

            if (cmd.Has(InputButtons.Reload) && !p.Reloading && p.Ammo < _cfg.Weapon.MagazineSize) StartReload(p);
            if (!cmd.Has(InputButtons.Fire)) continue;

            if (p.Reloading || Tick < p.NextFireTick) Stats.FireBlocked++;
            else if (p.Ammo <= 0) StartReload(p);
            else Fire(p, cmd);
        }
    }

    private void StartReload(PlayerState p) => p.ReloadDoneTick = Tick + _cfg.Ticks(_cfg.Weapon.ReloadSeconds);

    private void Fire(PlayerState shooter, InputCommand cmd)
    {
        WeaponDefinition w = _cfg.Weapon;
        shooter.Ammo--;
        shooter.NextFireTick = Tick + Math.Max(1, _cfg.Ticks(w.FireIntervalSeconds));
        Stats.ShotsFired++;
        if (shooter.Ammo == 0) StartReload(shooter);

        Vector3 origin = shooter.Position + new Vector3(0, GameRules.EyeHeight, 0);
        Vector3 dir = Simulation.AimDirection(shooter.Yaw, shooter.Pitch);

        float wall = w.Range;
        foreach (Aabb box in _cfg.Map.Obstacles)
        {
            float? t = Simulation.RayAabb(origin, dir, box, w.Range);
            if (t.HasValue && t.Value < wall) wall = t.Value;
        }

        // Lag compensation: test targets where the shooter saw them, but never further back than MaxRewind.
        long maxRewind = _historyLength - 2;
        long rewindTick = Math.Clamp((long)cmd.AckTick, Tick - maxRewind, Tick);

        PlayerState? best = null;
        float bestT = wall;
        foreach (PlayerState target in _players)
        {
            if (target == shooter || !target.Alive) continue;
            float? t = Simulation.RayCapsule(origin, dir, PositionAt(target, rewindTick), w.Range);
            if (t.HasValue && t.Value < bestT)
            {
                bestT = t.Value;
                best = target;
            }
        }
        if (best == null) return;

        Stats.Hits++;
        best.Health -= w.Damage;
        if (best.Health <= 0) Eliminate(best, shooter);
    }

    private Vector3 PositionAt(PlayerState p, long tick)
    {
        if (tick >= Tick || Tick - tick >= _historyLength) return p.Position;
        return p.History[tick % _historyLength];
    }

    private void RecordHistory()
    {
        foreach (PlayerState p in _players) p.History[Tick % _historyLength] = p.Position;
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
            if (p.Health <= 0) Eliminate(p, null);
        }
    }

    private void Eliminate(PlayerState victim, PlayerState? killer)
    {
        if (!victim.Alive) return;
        victim.Alive = false;
        victim.Health = 0;
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
        var snapshot = new SnapshotPacket
        {
            Tick = (uint)Tick,
            Phase = Phase,
            PhaseTicksLeft = Phase == MatchPhase.Countdown ? (ushort)Math.Max(0, _phaseEndTick - Tick) : (ushort)0,
            ZoneX = 0,
            ZoneZ = 0,
            ZoneRadius = CurrentZoneRadius(),
            AliveCount = (byte)AliveCount(),
        };
        foreach (PlayerState p in _players)
        {
            var flags = PlayerFlags.None;
            if (p.Alive) flags |= PlayerFlags.Alive;
            if (p.Connected) flags |= PlayerFlags.Connected;
            if (p.Reloading) flags |= PlayerFlags.Reloading;
            snapshot.Players.Add(new SnapshotEntry
            {
                PlayerId = p.Id,
                X = p.Position.X,
                Y = p.Position.Y,
                Z = p.Position.Z,
                Yaw = p.Yaw,
                Health = (byte)Math.Clamp(Math.Ceiling(p.Health), 0, 255),
                Ammo = (byte)Math.Clamp(p.Ammo, 0, 255),
                Flags = flags,
            });
        }

        foreach (PlayerState p in _players)
        {
            if (!p.Connected) continue;
            snapshot.AckSequence = p.LastProcessedSequence;
            Send(p.EndPoint, PacketCodec.Encode(snapshot));
        }
    }

    private void Broadcast(byte[] data)
    {
        foreach (PlayerState p in _players)
            if (p.Connected) Send(p.EndPoint, data);
    }

    private void Send(IPEndPoint to, byte[] data) => Outbox.Add((to, data));
}
