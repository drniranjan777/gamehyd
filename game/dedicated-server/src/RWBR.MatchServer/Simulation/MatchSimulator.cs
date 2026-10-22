using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using RWBR.Shared;
using RWBR.Shared.Bots;
using RWBR.Shared.Protocol;

namespace RWBR.MatchServer.Sim;

public sealed record SimulatedMatch(
    int Seed,
    int Players,
    CompletionReason Reason,
    double ActiveMinutes,
    int PlayerKills,
    int ZoneEliminations,
    int DisconnectEliminations,
    long ShotsFired,
    long Hits,
    long Headshots,
    double SimulatedSecondsPerWallSecond);

/// <summary>
/// Plays whole matches in-process, as fast as the CPU allows: the real <see cref="Match"/> with bots that decode real
/// snapshot packets and send real input packets (no sockets, no sleeping). Used to tune match length and zone timing
/// per map (BRD Phase 4 "tune safe zone ... match duration").
/// </summary>
public static class MatchSimulator
{
    public static SimulatedMatch Run(MatchConfig cfg, int seed)
    {
        var match = new Match(cfg);
        int n = cfg.MaxPlayers;
        var bots = new List<SimBot>();
        for (int i = 0; i < n; i++) bots.Add(new SimBot(new IPEndPoint(IPAddress.Loopback, 10_000 + i), seed * 100 + i));

        foreach (SimBot b in bots)
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = $"sim{b.EndPoint.Port}" });
            match.Receive(b.EndPoint, hello, hello.Length);
        }
        var index = bots.ToDictionary(b => b.EndPoint);
        var wall = Stopwatch.StartNew();
        long maxTicks = cfg.Ticks(cfg.MaxMatchSeconds + cfg.CountdownSeconds + cfg.FillWaitSeconds + 30);

        while (!match.IsFinished && match.Tick < maxTicks)
        {
            double time = match.Tick / (double)cfg.TickRate;
            foreach (SimBot b in bots) b.Act(match, time);
            match.Advance();
            foreach (var (to, data) in match.Outbox)
                if (index.TryGetValue(to, out SimBot? b)) b.Receive(data);
            match.Outbox.Clear();
        }

        int kills = match.Players.Sum(p => p.Kills);
        int eliminated = match.Players.Count(p => p.EliminatedAtTick > 0);
        double simSeconds = match.Tick / (double)cfg.TickRate;
        return new SimulatedMatch(
            seed,
            n,
            match.Result?.Reason ?? CompletionReason.Cancelled,
            Math.Round(match.ActiveSeconds / 60, 2),
            kills,
            (int)match.Stats.ZoneEliminations,
            eliminated - kills - (int)match.Stats.ZoneEliminations,
            match.Stats.ShotsFired,
            match.Stats.Hits,
            match.Stats.Headshots,
            Math.Round(simSeconds / Math.Max(0.001, wall.Elapsed.TotalSeconds), 1));
    }

    private sealed class SimBot
    {
        private readonly BotBrain _brain;
        private readonly SnapshotHistory _history = new();
        private readonly List<InputCommand> _recent = new();
        private SnapshotPacket? _last;
        private ushort _id = ProtocolInfo.NoPlayer;
        private uint _seq;

        public SimBot(IPEndPoint endPoint, int seed)
        {
            EndPoint = endPoint;
            _brain = new BotBrain(seed);
        }

        public IPEndPoint EndPoint { get; }

        public void Receive(byte[] data)
        {
            var r = new PacketReader(data, data.Length);
            if (r.Type == PacketType.Welcome) _id = PacketCodec.DecodeWelcome(r).PlayerId;
            else if (r.Type == PacketType.Snapshot)
            {
                SnapshotPacket s = SnapshotCodec.Decode(r, _history);
                _history.Add(s);
                if (_last == null || s.Tick > _last.Tick) _last = s;
            }
        }

        public void Act(Match match, double time)
        {
            if (_id == ProtocolInfo.NoPlayer) return;
            InputCommand cmd = _brain.Decide(_last, _id, time);
            cmd.Sequence = ++_seq;
            cmd.AckTick = _last?.Tick ?? 0;
            _recent.Insert(0, cmd);
            if (_recent.Count > ProtocolInfo.MaxInputsPerPacket) _recent.RemoveAt(_recent.Count - 1);
            byte[] packet = PacketCodec.Encode(_recent);
            match.Receive(EndPoint, packet, packet.Length);
        }
    }
}
