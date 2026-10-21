using System;
using System.Linq;
using System.Net;
using RWBR.Bots;
using RWBR.Shared.Protocol;

// Usage: RWBR.Bots [--host 127.0.0.1] [--port 7777] [--count 8] [--timeout 120]

string host = StrArg("--host", "127.0.0.1");
int port = IntArg("--port", 7777);
int count = IntArg("--count", 8);
int timeout = IntArg("--timeout", 120);

var server = new IPEndPoint(Dns.GetHostAddresses(host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), port);
var bots = BotSwarm.Run(server, count, TimeSpan.FromSeconds(timeout));

int completed = bots.Count(b => b.Result != null);
MatchCompletedPacket? result = bots.Select(b => b.Result).FirstOrDefault(r => r != null);
Console.WriteLine($"{completed}/{count} bots received the match result");
if (result != null)
{
    Console.WriteLine($"reason={result.Reason} winner={result.WinnerId}");
    foreach (PlacementEntry p in result.Placements) Console.WriteLine($"  #{p.Placement} player {p.PlayerId} kills {p.Kills}");
}
foreach (BotClient b in bots) b.Dispose();
return completed == count ? 0 : 1;

string StrArg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

int IntArg(string name, int fallback) => int.TryParse(StrArg(name, ""), out int v) ? v : fallback;
