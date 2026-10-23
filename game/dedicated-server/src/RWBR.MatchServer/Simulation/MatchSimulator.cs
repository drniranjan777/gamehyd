using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using RWBR.Shared;
using RWBR.Shared.Server;
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
        var bots = new List<InProcessBot>();
        for (int i = 0; i < n; i++) bots.Add(new InProcessBot(new IPEndPoint(IPAddress.Loopback, 10_000 + i), seed * 100 + i, cfg.Map, $"sim{10_000 + i}"));
        foreach (InProcessBot b in bots) b.Join(match);
        var index = bots.ToDictionary(b => b.EndPoint);
        var wall = Stopwatch.StartNew();
        long maxTicks = cfg.Ticks(cfg.MaxMatchSeconds + cfg.CountdownSeconds + cfg.FillWaitSeconds + 30);

        while (!match.IsFinished && match.Tick < maxTicks)
        {
            double time = match.Tick / (double)cfg.TickRate;
            foreach (InProcessBot b in bots) b.Act(match, time);
            match.Advance();
            foreach (var (to, data) in match.Outbox)
                if (index.TryGetValue(to, out InProcessBot? b)) b.Receive(data);
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
}
