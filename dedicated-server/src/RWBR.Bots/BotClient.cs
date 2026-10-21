using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;

namespace RWBR.Bots;

/// <summary>
/// Headless UDP client that plays like a simple player: heads for the safe zone, aims at the nearest
/// enemy from the latest snapshot and fires in range. Used for load tests and automated match tests.
/// </summary>
public sealed class BotClient : IDisposable
{
    private readonly IPEndPoint _server;
    private readonly Random _rng;
    private readonly List<InputCommand> _recent = new(); // newest first, resent for loss tolerance
    private readonly byte[] _buffer = new byte[2048];
    private Socket _socket;
    private uint _sequence;
    private double _lastHello = double.NegativeInfinity;

    public BotClient(IPEndPoint server, string name, int seed)
    {
        _server = server;
        Name = name;
        _rng = new Random(seed);
        _socket = CreateSocket();
    }

    public string Name { get; }
    public ushort PlayerId { get; private set; } = ProtocolInfo.NoPlayer;
    public Guid SessionToken { get; private set; }
    public bool Welcomed { get; private set; }
    public RejectReason? Rejected { get; private set; }
    public SnapshotPacket? LastSnapshot { get; private set; }
    public MatchCompletedPacket? Result { get; private set; }
    public int SnapshotsReceived { get; private set; }

    /// <summary>When true the bot stops sending entirely (simulates a dropped mobile connection).</summary>
    public bool Silent { get; set; }

    /// <summary>Drops the socket and rejoins from a new local port using the last session token.</summary>
    public void Reconnect()
    {
        _socket.Dispose();
        _socket = CreateSocket();
        Welcomed = false;
        Silent = false;
        _lastHello = double.NegativeInfinity;
    }

    /// <summary>Call once per client frame: reads everything received, then sends one input (or Hello).</summary>
    public void Pump(double timeSeconds)
    {
        ReceiveAll();
        if (Silent || Result != null || Rejected != null) return;

        if (!Welcomed)
        {
            if (timeSeconds - _lastHello < 0.25) return;
            _lastHello = timeSeconds;
            Send(PacketCodec.Encode(new HelloPacket { SessionToken = SessionToken, Name = Name }));
            return;
        }

        InputCommand cmd = Decide(timeSeconds);
        cmd.Sequence = ++_sequence;
        cmd.AckTick = LastSnapshot?.Tick ?? 0;
        _recent.Insert(0, cmd);
        if (_recent.Count > ProtocolInfo.MaxInputsPerPacket) _recent.RemoveAt(_recent.Count - 1);
        Send(PacketCodec.Encode(_recent));
    }

    public void Leave() => Send(PacketCodec.EncodeLeave());

    private InputCommand Decide(double time)
    {
        SnapshotPacket? snap = LastSnapshot;
        if (snap == null) return default;
        SnapshotEntry? selfOrNull = snap.Players.Cast<SnapshotEntry?>().FirstOrDefault(p => p!.Value.PlayerId == PlayerId);
        if (selfOrNull is not { } self || (self.Flags & PlayerFlags.Alive) == 0) return default;

        var me = new Vector3(self.X, self.Y, self.Z);
        SnapshotEntry? target = null;
        float best = float.MaxValue;
        foreach (SnapshotEntry p in snap.Players)
        {
            if (p.PlayerId == PlayerId || (p.Flags & PlayerFlags.Alive) == 0) continue;
            float d = Vector3.Distance(me, new Vector3(p.X, p.Y, p.Z));
            if (d < best)
            {
                best = d;
                target = p;
            }
        }

        float yaw = self.Yaw, pitch = 0;
        if (target is { } t)
        {
            float dx = t.X - me.X, dz = t.Z - me.Z;
            float dy = (t.Y + 1.2f) - (me.Y + GameRules.EyeHeight);
            yaw = MathF.Atan2(dx, dz) + (float)(_rng.NextDouble() - 0.5) * 0.04f;
            pitch = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
        }

        // Head for the zone centre when near the edge; otherwise close in and strafe.
        Vector3 worldMove;
        float fromCenter = new Vector2(me.X - snap.ZoneX, me.Z - snap.ZoneZ).Length();
        if (fromCenter > snap.ZoneRadius * 0.7f)
            worldMove = Vector3.Normalize(new Vector3(snap.ZoneX - me.X, 0, snap.ZoneZ - me.Z));
        else
            worldMove = (target != null && best > 12 ? Simulation.Forward(yaw) : Vector3.Zero)
                        + Simulation.Right(yaw) * MathF.Sin((float)time * 2 + PlayerId);

        var buttons = InputButtons.None;
        if (target != null && best < 45) buttons |= InputButtons.Fire;
        float mz = Vector3.Dot(worldMove, Simulation.Forward(yaw));
        if (mz > 0.7f) buttons |= InputButtons.Sprint;

        return new InputCommand
        {
            MoveX = Vector3.Dot(worldMove, Simulation.Right(yaw)),
            MoveZ = mz,
            Yaw = yaw,
            Pitch = pitch,
            Buttons = buttons,
        };
    }

    private void ReceiveAll()
    {
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int n;
            try
            {
                if (_socket.Available == 0) return;
                n = _socket.ReceiveFrom(_buffer, ref from);
            }
            catch (SocketException)
            {
                return;
            }
            try
            {
                Handle(new PacketReader(_buffer, n));
            }
            catch (PacketFormatException)
            {
                // Ignore malformed server packets in the bot.
            }
        }
    }

    private void Handle(PacketReader r)
    {
        switch (r.Type)
        {
            case PacketType.Welcome:
                WelcomePacket w = PacketCodec.DecodeWelcome(r);
                PlayerId = w.PlayerId;
                SessionToken = w.SessionToken;
                Welcomed = true;
                break;
            case PacketType.Reject:
                Rejected = PacketCodec.DecodeReject(r);
                break;
            case PacketType.Snapshot:
                SnapshotPacket s = PacketCodec.DecodeSnapshot(r);
                if (LastSnapshot == null || s.Tick > LastSnapshot.Tick) LastSnapshot = s;
                SnapshotsReceived++;
                break;
            case PacketType.MatchCompleted:
                Result ??= PacketCodec.DecodeMatchCompleted(r);
                break;
        }
    }

    private void Send(byte[] data)
    {
        try { _socket.SendTo(data, _server); }
        catch (SocketException) { }
    }

    private static Socket CreateSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) s.IOControl(unchecked((int)0x9800000C), new byte[4], null);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return s;
    }

    public void Dispose() => _socket.Dispose();
}
