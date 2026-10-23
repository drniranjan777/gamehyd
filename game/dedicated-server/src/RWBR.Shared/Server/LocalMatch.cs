#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using RWBR.Shared.Bots;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Server
{
    /// <summary>
    /// A bot that plays through real packets: it decodes the snapshots the match sends it and answers with input
    /// packets chosen by <see cref="BotBrain"/>. Used by the match simulator and by offline bot matches.
    /// </summary>
    public sealed class InProcessBot
    {
        private readonly BotBrain _brain;
        private readonly SnapshotHistory _history = new SnapshotHistory();
        private readonly List<InputCommand> _recent = new List<InputCommand>();
        private SnapshotPacket? _last;
        private uint _seq;

        public InProcessBot(IPEndPoint endPoint, int seed, MapLayout map, string name)
        {
            EndPoint = endPoint;
            Name = name;
            _brain = new BotBrain(seed, map);
        }

        public IPEndPoint EndPoint { get; }
        public string Name { get; }
        public ushort PlayerId { get; private set; } = ProtocolInfo.NoPlayer;

        public void Join(Match match)
        {
            _brain.Loot = match.Loot;
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = Name });
            match.Receive(EndPoint, hello, hello.Length);
        }

        public void Receive(byte[] data)
        {
            var r = new PacketReader(data, data.Length);
            if (r.Type == PacketType.Welcome) PlayerId = PacketCodec.DecodeWelcome(r).PlayerId;
            else if (r.Type == PacketType.Snapshot)
            {
                SnapshotPacket s = SnapshotCodec.Decode(r, _history);
                _history.Add(s);
                if (_last == null || s.Tick > _last.Tick) _last = s;
            }
        }

        public void Act(Match match, double time)
        {
            if (PlayerId == ProtocolInfo.NoPlayer) return;
            InputCommand cmd = _brain.Decide(_last, PlayerId, time);
            cmd.Sequence = ++_seq;
            cmd.AckTick = _last?.Tick ?? 0;
            _recent.Insert(0, cmd);
            if (_recent.Count > ProtocolInfo.MaxInputsPerPacket) _recent.RemoveAt(_recent.Count - 1);
            byte[] packet = PacketCodec.Encode(_recent);
            match.Receive(EndPoint, packet, packet.Length);
        }
    }

    /// <summary>
    /// The authoritative <see cref="Match"/> hosted inside the game for offline bot matches: the same rules, zone,
    /// loot and results as the dedicated server, with <see cref="InProcessBot"/>s as the other players. The local
    /// player talks to it through <see cref="SendFromPlayer"/> and <see cref="TryReceiveForPlayer"/> instead of UDP.
    /// Call <see cref="Advance"/> with the frame time; it runs whole ticks and never more than a few per call, so a
    /// slow frame slows the match down rather than making it jump.
    /// </summary>
    public sealed class LocalMatch
    {
        public const int MaxTicksPerAdvance = 4;
        public static readonly IPEndPoint PlayerEndPoint = new IPEndPoint(IPAddress.Loopback, 1);

        private readonly Queue<byte[]> _fromPlayer = new Queue<byte[]>();
        private readonly Queue<byte[]> _toPlayer = new Queue<byte[]>();
        private readonly Dictionary<IPEndPoint, InProcessBot> _botsByEndPoint = new Dictionary<IPEndPoint, InProcessBot>();
        private readonly double _tickSeconds;
        private double _pending;

        public LocalMatch(MatchConfig config, int bots, int seed)
        {
            if (bots < 1 || bots + 1 > config.MaxPlayers) throw new ArgumentOutOfRangeException(nameof(bots));
            Match = new Match(config);
            _tickSeconds = 1.0 / config.TickRate;
            for (int i = 0; i < bots; i++)
            {
                var bot = new InProcessBot(new IPEndPoint(IPAddress.Loopback, 10_000 + i), seed * 100 + i, config.Map, $"Bot {i + 1}");
                Bots.Add(bot);
                _botsByEndPoint[bot.EndPoint] = bot;
                bot.Join(Match);
            }
        }

        public Match Match { get; }
        public List<InProcessBot> Bots { get; } = new List<InProcessBot>();
        /// <summary>A paused match does not tick (the pause menu offline).</summary>
        public bool Paused { get; set; }

        /// <summary>
        /// Offline bot match settings for a map: the map's production zone schedule, everyone starting unarmed so the
        /// match opens with looting, and a short countdown once everyone is in.
        /// </summary>
        public static MatchConfig BotMatchConfig(MapLayout map, int bots) => new MatchConfig
        {
            Map = map,
            MinPlayers = bots + 1,
            MaxPlayers = bots + 1,
            ZoneStages = MatchConfig.ZonesFor(map),
            CountdownSeconds = 5f,
            StartWithWeapon = false,
            ResultBroadcastSeconds = 1f,
            ZoneSeed = 1, // moving circles (the game picks a new seed per match)
        };

        public void SendFromPlayer(byte[] data)
        {
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            _fromPlayer.Enqueue(copy);
        }

        public bool TryReceiveForPlayer(out byte[] data)
        {
            if (_toPlayer.Count > 0)
            {
                data = _toPlayer.Dequeue();
                return true;
            }
            data = Array.Empty<byte>();
            return false;
        }

        /// <summary>Runs the ticks due after <paramref name="seconds"/> more real time; returns how many ran.</summary>
        public int Advance(double seconds)
        {
            if (Paused || Match.IsFinished) return 0;
            _pending = Math.Min(_pending + seconds, _tickSeconds * MaxTicksPerAdvance);
            int ran = 0;
            while (_pending >= _tickSeconds)
            {
                _pending -= _tickSeconds;
                Tick();
                ran++;
            }
            return ran;
        }

        /// <summary>Exactly one tick: player packets in, bots decide, the match advances, packets out.</summary>
        public void Tick()
        {
            while (_fromPlayer.Count > 0)
            {
                byte[] p = _fromPlayer.Dequeue();
                Match.Receive(PlayerEndPoint, p, p.Length);
            }
            double time = Match.Tick * _tickSeconds;
            foreach (InProcessBot b in Bots) b.Act(Match, time);
            Match.Advance();
            foreach (var (to, data) in Match.Outbox)
            {
                if (to.Equals(PlayerEndPoint)) _toPlayer.Enqueue(data);
                else if (_botsByEndPoint.TryGetValue(to, out InProcessBot? bot)) bot.Receive(data);
            }
            Match.Outbox.Clear();
        }
    }
}
