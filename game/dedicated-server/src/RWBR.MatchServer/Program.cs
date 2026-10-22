using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using RWBR.MatchServer;
using RWBR.MatchServer.Platform;
using RWBR.Shared;

// Single match (dev, load tests):
//   RWBR.MatchServer [--port 7777] [--players 8] [--tick 30] [--snapshot-every 1] [--fast]
//                    [--ticket-secret-hex <hex> --match-id <guid>]
//   Runs one match to completion, prints a JSON report and exits (0 = completed with a winner).
// Managed by the platform API:
//   RWBR.MatchServer --api-url http://localhost:3001/api/v1 --address 127.0.0.1:7777 [--region IN-SOUTH]
//                    [--port 7777] [--fast] [--max-matches N]
//   Secrets come from the environment: RWBR_SERVER_SHARED_SECRET and RWBR_TICKET_SECRET_HEX.

int port = IntArg("--port", 7777);
int tick = IntArg("--tick", 30);
using var cts = new CancellationTokenSource();
// First Ctrl+C / SIGTERM: graceful stop where supported (managed mode drains); second: stop now.
Action? gracefulStop = null;
void Stop()
{
    Action? graceful = Interlocked.Exchange(ref gracefulStop, null);
    if (graceful != null) graceful();
    else cts.Cancel();
}
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Stop();
};
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        Stop();
    });

// In-process balance simulation: RWBR.MatchServer --simulate 3 [--map hyd-city-v1] [--players 30] [--loot-start]
int simulate = IntArg("--simulate", 0);
if (simulate > 0)
{
    string mapVersion = StrArg("--map") ?? CityMap.Version;
    if (!MapLayout.TryFromVersion(mapVersion, out MapLayout simMap)) throw new ArgumentException($"unknown map {mapVersion}");
    int simPlayers = IntArg("--players", 30);
    var results = new System.Collections.Generic.List<RWBR.MatchServer.Sim.SimulatedMatch>();
    for (int i = 1; i <= simulate; i++)
    {
        var simCfg = new MatchConfig { MinPlayers = simPlayers, MaxPlayers = simPlayers, Map = simMap, ZoneStages = MatchConfig.ZonesFor(simMap), CountdownSeconds = 3 };
        if (args.Contains("--loot-start")) simCfg.StartWithWeapon = false; // design option: everyone starts unarmed and loots
        var r = RWBR.MatchServer.Sim.MatchSimulator.Run(simCfg, i);
        results.Add(r);
        Console.WriteLine(JsonSerializer.Serialize(r));
    }
    Console.WriteLine($"average active {results.Average(r => r.ActiveMinutes):0.00} min, player kills {results.Average(r => r.PlayerKills):0.0}, " +
                      $"zone eliminations {results.Average(r => r.ZoneEliminations):0.0}, speed {results.Average(r => r.SimulatedSecondsPerWallSecond):0}x real time");
    return 0;
}

string? apiUrl = StrArg("--api-url");
if (apiUrl != null)
{
    string shared = Environment.GetEnvironmentVariable("RWBR_SERVER_SHARED_SECRET") ?? throw new ArgumentException("RWBR_SERVER_SHARED_SECRET is not set");
    string ticketHex = Environment.GetEnvironmentVariable("RWBR_TICKET_SECRET_HEX") ?? throw new ArgumentException("RWBR_TICKET_SECRET_HEX is not set");
    string address = StrArg("--address") ?? $"127.0.0.1:{port}";
    using var api = new PlatformClient(new Uri(apiUrl), shared, address);
    var server = new ManagedServer(api, new ManagedOptions
    {
        Port = port,
        Address = address,
        Region = StrArg("--region") ?? "IN-SOUTH",
        TicketSecret = Convert.FromHexString(ticketHex),
        BuildVersion = typeof(Match).Assembly.GetName().Version?.ToString() ?? "dev",
        TickRate = tick,
        SnapshotEveryTicks = IntArg("--snapshot-every", 1),
        Fast = args.Contains("--fast"),
        MaxMatches = IntArg("--max-matches", 0),
    });
    gracefulStop = server.Drain;
    await server.RunAsync(cts.Token);
    return 0;
}

int players = IntArg("--players", 8);
MatchConfig config = args.Contains("--fast") ? MatchConfig.Fast(players) : new MatchConfig { MinPlayers = players, MaxPlayers = players };
config.TickRate = tick;
config.SnapshotEveryTicks = IntArg("--snapshot-every", 1);
string? secretHex = StrArg("--ticket-secret-hex");
if (secretHex != null)
{
    config.TicketSecret = Convert.FromHexString(secretHex);
    config.MatchId = Guid.Parse(StrArg("--match-id") ?? throw new ArgumentException("--match-id is required with --ticket-secret-hex"));
}

var match = new Match(config);
using var host = new UdpMatchHost(match, port);
Console.Error.WriteLine($"match server listening on udp/{host.Port}: {players} players, {tick} Hz, snapshots every {config.SnapshotEveryTicks} tick(s), " +
                        $"map {config.Map.Version}, tickets {(config.TicketSecret != null ? "required" : "off (dev)")}");
HostReport report = await host.RunAsync(cts.Token);
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
return report.Reason == RWBR.Shared.Protocol.CompletionReason.Winner ? 0 : 1;

string? StrArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int IntArg(string name, int fallback) => int.TryParse(StrArg(name), out int v) ? v : fallback;
