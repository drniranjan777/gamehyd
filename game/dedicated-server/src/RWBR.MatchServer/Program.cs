using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using RWBR.MatchServer;
using RWBR.Shared;

// Usage: RWBR.MatchServer [--port 7777] [--players 8] [--tick 30] [--fast]
// Runs one match to completion, then prints a JSON report and exits (0 = completed with a winner).

int port = IntArg("--port", 7777);
int players = IntArg("--players", 8);
int tick = IntArg("--tick", 30);
MatchConfig config = args.Contains("--fast") ? MatchConfig.Fast(players) : new MatchConfig { MinPlayers = players, MaxPlayers = players };
config.TickRate = tick;

var match = new Match(config);
using var host = new UdpMatchHost(match, port);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.Error.WriteLine($"match server listening on udp/{host.Port}: {players} players, {tick} Hz, map {config.Map.Version}");
HostReport report = await host.RunAsync(cts.Token);
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
return report.Reason == RWBR.Shared.Protocol.CompletionReason.Winner ? 0 : 1;

int IntArg(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
}
