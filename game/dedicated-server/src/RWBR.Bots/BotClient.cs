using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Bots;
using RWBR.Shared.Protocol;

namespace RWBR.Bots;

/// <summary>Per-bot settings: optional join ticket, and simulated network impairment for both directions.</summary>
public sealed class BotOptions
{
    public byte[]? Ticket { get; init; }
    public byte[]? SessionKey { get; init; }
    /// <summary>Probability (0..1) that any datagram, in either direction, is lost.</summary>
    public double LossRate { get; init; }
    /// <summary>One-way added delay in milliseconds (applied to both directions).</summary>
    public int LatencyMs { get; init; }
    /// <summary>Random extra one-way delay, 0..JitterMs.</summary>
    public int JitterMs { get; init; }
}

/// <summary>
/// Headless UDP client that plays like a simple player: heads for the safe zone, aims at the nearest
/// enemy from the latest snapshot and fires in range. Used for load tests and automated match tests.
/// </summary>
public sealed class BotClient : IDisposable
{
    private readonly IPEndPoint _server;
    private readonly Random _rng;
    private readonly BotBrain _brain;
    private readonly BotOptions _options;
    private readonly PacketAuth? _auth;
    private readonly List<InputCommand> _recent = new(); // newest first, resent for loss tolerance
    private readonly byte[] _buffer = new byte[2048];
    private readonly SnapshotHistory _history = new();
    private readonly List<(double Due, byte[] Data)> _outgoing = new();
    private readonly List<(double Due, byte[] Data)> _incoming = new();
    private Socket _socket;
    private uint _sequence;
    private double _lastHello = double.NegativeInfinity;
    private double _now;

    public BotClient(IPEndPoint server, string name, int seed, BotOptions? options = null)
    {
        _server = server;
        Name = name;
        _rng = new Random(seed);
        _brain = new BotBrain(seed);
        _options = options ?? new BotOptions();
        if (_options.SessionKey != null) _auth = new PacketAuth(_options.SessionKey);
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
    public int DeltaSnapshotsReceived { get; private set; }
    public int MissingBaselines { get; private set; }
    public long BytesReceived { get; private set; }

    /// <summary>When true the bot stops sending entirely (simulates a dropped mobile connection).</summary>
    public bool Silent { get; set; }

    /// <summary>Drops the socket and rejoins from a new local port using the last session token.</summary>
    public void Reconnect()
    {
        _socket.Dispose();
        _socket = CreateSocket();
        _outgoing.Clear();
        _incoming.Clear();
        Welcomed = false;
        Silent = false;
        _lastHello = double.NegativeInfinity;
    }

    /// <summary>Call once per client frame: reads everything received, then sends one input (or Hello).</summary>
    public void Pump(double timeSeconds)
    {
        _now = timeSeconds;
        ReceiveAll();
        FlushDelayed(_incoming, Handle);
        if (!Silent && Result == null && Rejected == null)
        {
            if (!Welcomed)
            {
                if (timeSeconds - _lastHello >= 0.25)
                {
                    _lastHello = timeSeconds;
                    byte[] hello = PacketCodec.Encode(new HelloPacket
                    {
                        SessionToken = SessionToken,
                        Name = Name,
                        Ticket = SessionToken == Guid.Empty ? _options.Ticket ?? Array.Empty<byte>() : Array.Empty<byte>(),
                    });
                    // A reconnect in ticket mode proves possession of the session key.
                    Send(SessionToken != Guid.Empty && _auth != null ? _auth.Sign(hello) : hello);
                }
            }
            else
            {
                InputCommand cmd = Decide(timeSeconds);
                cmd.Sequence = ++_sequence;
                cmd.AckTick = LastSnapshot?.Tick ?? 0;
                _recent.Insert(0, cmd);
                if (_recent.Count > ProtocolInfo.MaxInputsPerPacket) _recent.RemoveAt(_recent.Count - 1);
                byte[] input = PacketCodec.Encode(_recent);
                Send(_auth != null ? _auth.Sign(input) : input);
            }
        }
        FlushDelayed(_outgoing, SendNow);
    }

    public void Leave()
    {
        byte[] leave = PacketCodec.EncodeLeave();
        SendNow(_auth != null ? _auth.Sign(leave) : leave);
    }

    private InputCommand Decide(double time) => _brain.Decide(LastSnapshot, PlayerId, time);

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
            BytesReceived += n;
            if (Lost()) continue;
            _incoming.Add((_now + Delay(), _buffer.AsSpan(0, n).ToArray()));
        }
    }

    private void Handle(byte[] data)
    {
        try
        {
            var r = new PacketReader(data, data.Length);
            switch (r.Type)
            {
                case PacketType.Welcome:
                    WelcomePacket w = PacketCodec.DecodeWelcome(r);
                    if (w.PlayerId != PlayerId) _history.Clear();
                    PlayerId = w.PlayerId;
                    SessionToken = w.SessionToken;
                    Welcomed = true;
                    break;
                case PacketType.Reject:
                    Rejected = PacketCodec.DecodeReject(r);
                    break;
                case PacketType.Snapshot:
                    SnapshotPacket s = SnapshotCodec.Decode(r, _history);
                    _history.Add(s);
                    if (LastSnapshot == null || s.Tick > LastSnapshot.Tick) LastSnapshot = s;
                    SnapshotsReceived++;
                    if (s.BaselineTick != 0) DeltaSnapshotsReceived++;
                    break;
                case PacketType.MatchCompleted:
                    Result ??= PacketCodec.DecodeMatchCompleted(r);
                    break;
            }
        }
        catch (MissingBaselineException)
        {
            MissingBaselines++;
        }
        catch (PacketFormatException)
        {
            // Ignore malformed server packets in the bot.
        }
    }

    private void Send(byte[] data)
    {
        if (Lost()) return;
        _outgoing.Add((_now + Delay(), data));
    }

    private void SendNow(byte[] data)
    {
        try { _socket.SendTo(data, _server); }
        catch (SocketException) { }
    }

    private void FlushDelayed(List<(double Due, byte[] Data)> queue, Action<byte[]> deliver)
    {
        // Delivered in due-time order, so jitter can reorder datagrams like a real network.
        if (queue.Count == 0) return;
        queue.Sort((a, b) => a.Due.CompareTo(b.Due));
        int i = 0;
        while (i < queue.Count && queue[i].Due <= _now) deliver(queue[i++].Data);
        queue.RemoveRange(0, i);
    }

    private bool Lost() => _options.LossRate > 0 && _rng.NextDouble() < _options.LossRate;

    private double Delay() => (_options.LatencyMs + (_options.JitterMs > 0 ? _rng.Next(_options.JitterMs + 1) : 0)) / 1000.0;

    private static Socket CreateSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) s.IOControl(unchecked((int)0x9800000C), new byte[4], null);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return s;
    }

    public void Dispose()
    {
        _socket.Dispose();
        _auth?.Dispose();
    }
}
