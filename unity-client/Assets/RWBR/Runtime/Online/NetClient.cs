using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RWBR.Shared;
using RWBR.Shared.Protocol;

namespace RWBR.Client.Online
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Rejected,
    }

    /// <summary>
    /// UDP connection to the authoritative match server (protocol v3). Engine-agnostic: call <see cref="Pump"/>
    /// every frame and read the queued snapshots/events. Optional join ticket + session key for ticket mode.
    /// </summary>
    public sealed class NetClient : IDisposable
    {
        private const double HelloInterval = 0.25;

        private readonly byte[] _buffer = new byte[2048];
        private readonly List<InputCommand> _recent = new List<InputCommand>(); // newest first
        private readonly Dictionary<uint, double> _sentAt = new Dictionary<uint, double>();
        private IPEndPoint _server;
        private Socket _socket;
        private PacketAuth _auth;
        private byte[] _ticket = Array.Empty<byte>();
        private string _name = "Player";
        private double _lastHello = double.NegativeInfinity;
        private long _bytesWindow;
        private double _windowStart;

        public ConnectionState State { get; private set; }
        public ushort PlayerId { get; private set; } = ProtocolInfo.NoPlayer;
        public Guid SessionToken { get; private set; }
        public WelcomePacket Welcome { get; private set; }
        public RejectReason? RejectedBecause { get; private set; }
        public SnapshotHistory History { get; } = new SnapshotHistory();
        public SnapshotPacket Latest { get; private set; }
        public MatchCompletedPacket Result { get; private set; }
        public double LastReceiveTime { get; private set; }

        /// <summary>New since the last <see cref="Pump"/>, oldest first.</summary>
        public List<SnapshotPacket> NewSnapshots { get; } = new List<SnapshotPacket>();
        public List<EliminatedPacket> NewEliminations { get; } = new List<EliminatedPacket>();

        public int SnapshotsDecoded { get; private set; }
        public int DeltaSnapshots { get; private set; }
        public int MissingBaselines { get; private set; }
        public double RoundTripMs { get; private set; }
        public double KbpsIn { get; private set; }
        public string ServerAddress => _server?.ToString() ?? "";

        public void Connect(string host, int port, string name, byte[] ticket = null, byte[] sessionKey = null)
        {
            Close();
            IPAddress address = Resolve(host);
            _server = new IPEndPoint(address, port);
            _name = name;
            _ticket = ticket ?? Array.Empty<byte>();
            _auth?.Dispose();
            _auth = sessionKey != null ? new PacketAuth(sessionKey) : null;
            SessionToken = Guid.Empty;
            PlayerId = ProtocolInfo.NoPlayer;
            Welcome = null;
            Result = null;
            Latest = null;
            RejectedBecause = null;
            History.Clear();
            _recent.Clear();
            _sentAt.Clear();
            _socket = CreateSocket();
            State = ConnectionState.Connecting;
            _lastHello = double.NegativeInfinity;
        }

        /// <summary>New socket (new local port) and a reconnect Hello with the session token: survives network changes.</summary>
        public void Reconnect()
        {
            if (_server == null) return;
            _socket?.Dispose();
            _socket = CreateSocket();
            State = ConnectionState.Connecting;
            _lastHello = double.NegativeInfinity;
        }

        public void Pump(double now)
        {
            NewSnapshots.Clear();
            NewEliminations.Clear();
            if (_socket == null) return;

            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                int n;
                try
                {
                    if (_socket.Available == 0) break;
                    n = _socket.ReceiveFrom(_buffer, ref from);
                }
                catch (SocketException)
                {
                    break;
                }
                _bytesWindow += n;
                LastReceiveTime = now;
                Handle(n, now);
            }

            if (now - _windowStart >= 1.0)
            {
                KbpsIn = _bytesWindow * 8 / 1000.0 / (now - _windowStart);
                _bytesWindow = 0;
                _windowStart = now;
            }

            if (State == ConnectionState.Connecting && now - _lastHello >= HelloInterval)
            {
                _lastHello = now;
                bool reconnect = SessionToken != Guid.Empty;
                byte[] hello = PacketCodec.Encode(new HelloPacket
                {
                    SessionToken = SessionToken,
                    Name = _name,
                    Ticket = reconnect ? Array.Empty<byte>() : _ticket,
                });
                Send(reconnect && _auth != null ? _auth.Sign(hello) : hello);
            }
        }

        /// <summary>Sends this tick's input plus the two previous ones (loss tolerance).</summary>
        public void SendInput(InputCommand cmd, double now)
        {
            if (State != ConnectionState.Connected) return;
            _recent.Insert(0, cmd);
            if (_recent.Count > ProtocolInfo.MaxInputsPerPacket) _recent.RemoveAt(_recent.Count - 1);
            _sentAt[cmd.Sequence] = now;
            byte[] packet = PacketCodec.Encode(_recent);
            Send(_auth != null ? _auth.Sign(packet) : packet);
        }

        public void Leave()
        {
            if (_socket == null || State != ConnectionState.Connected) return;
            byte[] leave = PacketCodec.EncodeLeave();
            Send(_auth != null ? _auth.Sign(leave) : leave);
            State = ConnectionState.Disconnected;
        }

        private void Handle(int length, double now)
        {
            try
            {
                var r = new PacketReader(_buffer, length);
                switch (r.Type)
                {
                    case PacketType.Welcome:
                        Welcome = PacketCodec.DecodeWelcome(r);
                        PlayerId = Welcome.PlayerId;
                        SessionToken = Welcome.SessionToken;
                        State = ConnectionState.Connected;
                        break;
                    case PacketType.Reject:
                        RejectedBecause = PacketCodec.DecodeReject(r);
                        State = ConnectionState.Rejected;
                        break;
                    case PacketType.Snapshot:
                        SnapshotPacket s = SnapshotCodec.Decode(r, History);
                        History.Add(s);
                        SnapshotsDecoded++;
                        if (s.BaselineTick != 0) DeltaSnapshots++;
                        if (Latest == null || s.Tick > Latest.Tick)
                        {
                            Latest = s;
                            NewSnapshots.Add(s);
                            if (_sentAt.TryGetValue(s.AckSequence, out double sent))
                            {
                                double rtt = (now - sent) * 1000.0;
                                RoundTripMs = RoundTripMs <= 0 ? rtt : RoundTripMs * 0.9 + rtt * 0.1;
                            }
                            PruneSent(s.AckSequence);
                        }
                        break;
                    case PacketType.Eliminated:
                        NewEliminations.Add(PacketCodec.DecodeEliminated(r));
                        break;
                    case PacketType.MatchCompleted:
                        if (Result == null) Result = PacketCodec.DecodeMatchCompleted(r);
                        break;
                }
            }
            catch (MissingBaselineException)
            {
                MissingBaselines++;
            }
            catch (PacketFormatException)
            {
                // Malformed or foreign datagram: ignore.
            }
        }

        private void PruneSent(uint acked)
        {
            if (_sentAt.Count < 128) return;
            var old = new List<uint>();
            foreach (uint seq in _sentAt.Keys)
                if (seq + 64 < acked) old.Add(seq);
            foreach (uint seq in old) _sentAt.Remove(seq);
        }

        private void Send(byte[] data)
        {
            try
            {
                _socket?.SendTo(data, _server);
            }
            catch (SocketException)
            {
                // Network unreachable (e.g. switching Wi-Fi to mobile): the reconnect path recovers.
            }
        }

        private static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress ip)) return ip;
            foreach (IPAddress a in Dns.GetHostAddresses(host))
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            throw new ArgumentException($"Cannot resolve {host}");
        }

        private static Socket CreateSocket()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } // ignore ICMP port-unreachable resets
                catch (Exception) { }
            }
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            return s;
        }

        private void Close()
        {
            _socket?.Dispose();
            _socket = null;
            State = ConnectionState.Disconnected;
        }

        public void Dispose()
        {
            Close();
            _auth?.Dispose();
        }
    }
}
