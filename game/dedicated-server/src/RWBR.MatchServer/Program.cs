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
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

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
