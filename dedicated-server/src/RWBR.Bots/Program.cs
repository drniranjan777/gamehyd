using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using System.Net;
using RWBR.Bots;
using RWBR.Shared.Protocol;

// Usage: RWBR.Bots [--host 127.0.0.1] [--port 7777] [--count 8] [--timeout 120]
//                  [--api http://localhost:3001/api/v1 [--region IN-SOUTH]]   (sign up, matchmake, play with tickets)
//                  [--loss 0.05] [--latency 60] [--jitter 20]
//                  [--ticket-secret-hex <hex> --match-id <guid>]   (dev only: bots mint their own tickets)

string host = StrArg("--host", "127.0.0.1");
int port = IntArg("--port", 7777);
int count = IntArg("--count", 8);
int timeout = IntArg("--timeout", 120);
double loss = double.TryParse(StrArg("--loss", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double l) ? l : 0;
int latency = IntArg("--latency", 0);
int jitter = IntArg("--jitter", 0);
string secretHex = StrArg("--ticket-secret-hex", "");
Guid matchId = Guid.TryParse(StrArg("--match-id", ""), out Guid m) ? m : Guid.Empty;

string apiUrl = StrArg("--api", "");
List<BotClient> bots;
List<PlatformPlayer> accounts = new();
if (apiUrl.Length > 0)
{
    // Full platform path: guest account -> matchmaking -> seat (signed ticket) -> play.
    accounts = Enumerable.Range(0, count).Select(_ => new PlatformPlayer(new Uri(apiUrl))).ToList();
    var seats = await Task.WhenAll(accounts.Select(async (a, i) =>
    {
        await a.SignUpAsync($"bot-{i + 1:00}", CancellationToken.None);
        return await a.FindMatchAsync(StrArg("--region", "IN-SOUTH"), TimeSpan.FromSeconds(timeout), CancellationToken.None);
    }));
    Console.WriteLine($"{seats.Length} seats in {seats.Select(s => s.MatchId).Distinct().Count()} match(es)");
    bots = seats.Select((s, i) => new BotClient(s.Endpoint, $"bot-{i + 1:00}", 1000 + i, new BotOptions
    {
        Ticket = Convert.FromBase64String(s.Ticket),
        SessionKey = Convert.FromBase64String(s.SessionKey),
        LossRate = loss,
        LatencyMs = latency,
        JitterMs = jitter,
    })).ToList();
    bots = BotSwarm.Run(bots, TimeSpan.FromSeconds(timeout));
}
else
{
var server = new IPEndPoint(Dns.GetHostAddresses(host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), port);
bots = BotSwarm.Run(server, count, TimeSpan.FromSeconds(timeout), options: i =>
{
    byte[]? ticket = null, key = null;
    if (secretHex.Length > 0)
        ticket = JoinTicket.Issue(Convert.FromHexString(secretHex), matchId, Guid.NewGuid(), $"bot-{i + 1:00}",
            DateTimeOffset.UtcNow.AddMinutes(5), out key);
    return new BotOptions { LossRate = loss, LatencyMs = latency, JitterMs = jitter, Ticket = ticket, SessionKey = key };
});
}

int completed = bots.Count(b => b.Result != null);
MatchCompletedPacket? result = bots.Select(b => b.Result).FirstOrDefault(r => r != null);
Console.WriteLine($"{completed}/{count} bots received the match result");
var rejected = bots.Where(b => b.Rejected != null).Select(b => b.Rejected).Distinct().ToList();
if (rejected.Count > 0) Console.WriteLine($"rejected: {string.Join(", ", rejected)}");
Console.WriteLine($"snapshots: {bots.Sum(b => b.SnapshotsReceived)} decoded ({bots.Sum(b => b.DeltaSnapshotsReceived)} delta), " +
                  $"{bots.Sum(b => b.MissingBaselines)} missing baselines");
if (result != null)
{
    Console.WriteLine($"reason={result.Reason} winner={result.WinnerId}");
    foreach (PlacementEntry p in result.Placements) Console.WriteLine($"  #{p.Placement} player {p.PlayerId} kills {p.Kills}");
}
if (accounts.Count > 0)
{
    // The platform side: the server's signed result should now show up in every account.
    await Task.Delay(TimeSpan.FromSeconds(3));
    foreach (PlatformPlayer a in accounts)
    {
        Stats? st = await a.StatsAsync(CancellationToken.None);
        Console.WriteLine($"  account {a.UserId}: matches {st?.MatchesPlayed} wins {st?.Wins} kills {st?.Kills} xp {st?.Xp}");
    }
    foreach (PlatformPlayer a in accounts) a.Dispose();
}
foreach (BotClient b in bots) b.Dispose();
return completed == count ? 0 : 1;

string StrArg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

int IntArg(string name, int fallback) => int.TryParse(StrArg(name, ""), out int v) ? v : fallback;
