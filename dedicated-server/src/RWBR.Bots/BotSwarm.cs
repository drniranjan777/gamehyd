using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;

namespace RWBR.Bots;

/// <summary>Drives many bots from one thread at the client frame rate until every bot has a result.</summary>
public static class BotSwarm
{
    public static List<BotClient> Run(IPEndPoint server, int count, TimeSpan timeout, int framesPerSecond = 30,
        CancellationToken ct = default, Func<int, BotOptions>? options = null) =>
        Run(Enumerable.Range(0, count)
            .Select(i => new BotClient(server, $"bot-{i + 1:00}", seed: 1000 + i, options?.Invoke(i)))
            .ToList(), timeout, framesPerSecond, ct);

    /// <summary>Drives already-created bots (e.g. each with its own platform seat).</summary>
    public static List<BotClient> Run(List<BotClient> bots, TimeSpan timeout, int framesPerSecond = 30, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        double frame = 1.0 / framesPerSecond, next = 0;
        while (!ct.IsCancellationRequested && clock.Elapsed < timeout)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now < next)
            {
                Thread.Sleep(1);
                continue;
            }
            next += frame;
            foreach (BotClient b in bots) b.Pump(now);
            if (bots.All(b => b.Result != null || b.Rejected != null)) break;
        }
        return bots;
    }
}
