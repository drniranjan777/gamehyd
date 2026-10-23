using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RWBR.Shared;
using RWBR.Shared.Server;
using RWBR.Shared.Protocol;

namespace RWBR.MatchServer.Platform;

public sealed class ManagedOptions
{
    public required int Port { get; init; }
    /// <summary>Public host:port players connect to (what the API hands out in seats).</summary>
    public required string Address { get; init; }
    public required string Region { get; init; }
    public required byte[] TicketSecret { get; init; }
    public string BuildVersion { get; init; } = "dev";
    public int TickRate { get; init; } = 30;
    public int SnapshotEveryTicks { get; init; } = 1;
    /// <summary>Short test timings (zone, countdown) instead of production ones.</summary>
    public bool Fast { get; init; }
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Stop after this many matches (0 = run until cancelled).</summary>
    public int MaxMatches { get; init; }
}

/// <summary>
/// Long-running match server under platform control: heartbeats as IDLE, hosts each match the API assigns
/// (tickets required), reports progress while HOSTING, then submits the signed result and goes idle again.
/// </summary>
public sealed class ManagedServer
{
    private static readonly string[] MapVersions = { CityMap.Version, "greybox-block-100-v2", "greybox-block-40-v2" };
    private readonly PlatformClient _api;
    private readonly ManagedOptions _o;

    public ManagedServer(PlatformClient api, ManagedOptions options)
    {
        _api = api;
        _o = options;
    }

    public int MatchesHosted { get; private set; }

    /// <summary>Set by <see cref="Drain"/>: take no new matches, finish the current one, then stop.</summary>
    public bool Draining => _draining.IsCancellationRequested;
    private readonly CancellationTokenSource _draining = new();

    /// <summary>
    /// Graceful stop for rolling deploys: tells the platform this server is DRAINING (no new assignments; matches
    /// assigned but not yet hosted are cancelled and their players requeued), lets a running match finish and submit
    /// its result, then <see cref="RunAsync"/> returns.
    /// </summary>
    public void Drain()
    {
        if (Draining) return;
        Log("draining", new { address = _o.Address });
        _draining.Cancel();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log("managed server started", new { address = _o.Address, region = _o.Region });
        while (!ct.IsCancellationRequested && (_o.MaxMatches == 0 || MatchesHosted < _o.MaxMatches))
        {
            HeartbeatResponse? hb = await Heartbeat("IDLE", null, null, 0, ct);
            if (Draining) break;
            if (hb?.Assignment is { } a) await HostAsync(a, ct);
            else
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _draining.Token);
                await Delay(_o.HeartbeatInterval, wait.Token);
            }
            if (Draining) break;
        }
        if (Draining && !ct.IsCancellationRequested)
        {
            await Heartbeat("IDLE", null, null, 0, ct); // final heartbeat: DRAINING and idle
            Log("drained", new { MatchesHosted });
        }
    }

    private async Task HostAsync(MatchAssignment a, CancellationToken ct)
    {
        if (!MapLayout.TryFromVersion(a.MapVersion, out MapLayout map))
        {
            Log("assignment refused: unknown map", new { a.MatchId, a.MapVersion });
            return;
        }

        MatchConfig cfg = _o.Fast ? MatchConfig.Fast(a.Capacity) : new MatchConfig();
        cfg.MaxPlayers = a.Capacity;
        cfg.MinPlayers = Math.Min(a.MinPlayers, a.Capacity);
        cfg.FillWaitSeconds = a.FillWaitSeconds;
        cfg.Map = map;
        if (!_o.Fast) cfg.ZoneStages = MatchConfig.ZonesFor(map);
        cfg.TickRate = _o.TickRate;
        cfg.SnapshotEveryTicks = _o.SnapshotEveryTicks;
        cfg.TicketSecret = _o.TicketSecret;
        cfg.MatchId = a.MatchId;
        if (a.DisabledWeapons is { Length: > 0 } off)
        {
            var unknown = cfg.DisableWeapons(off);
            if (unknown.Count > 0) Log("unknown weapons in kill switch ignored", new { a.MatchId, unknown });
        }

        var match = new Match(cfg);
        using var host = new UdpMatchHost(match, _o.Port);
        Log("hosting match", new { a.MatchId, a.MapVersion, a.Capacity, a.MinPlayers });
        Task<HostReport> run = host.RunAsync(ct);

        while (!run.IsCompleted)
        {
            (MatchPhase phase, int connected) = match.PublicStatus;
            await Heartbeat("HOSTING", a.MatchId, PhaseName(phase), connected, ct, host.RecentTickMsP99());
            await Task.WhenAny(run, Task.Delay(_o.HeartbeatInterval, ct));
        }
        HostReport report = await run;
        MatchesHosted++;
        Log("match finished", new { a.MatchId, report.Reason, report.Players, report.ActiveSeconds, report.KbpsOutPerPlayer, report.TickMsP99 });

        ResultSubmission? result = BuildResult(match, a.MapVersion);
        if (result == null)
        {
            Log("no result to submit (no ticketed players)", new { a.MatchId });
            return;
        }
        await SubmitAsync(a.MatchId, result, ct);
    }

    /// <summary>The server's authoritative outcome for every ticketed player who played.</summary>
    public static ResultSubmission? BuildResult(Match match, string mapVersion)
    {
        MatchCompletedPacket? r = match.Result;
        if (r == null) return null;
        var players = match.Players.Where(p => p.AccountId != Guid.Empty && p.Placement > 0).OrderBy(p => p.Placement).ToList();
        if (players.Count == 0) return null;

        // Renumber 1..n in case anyone left the roster without a placement (e.g. removed before the start).
        var participants = players
            .Select((p, i) => new ResultParticipant(p.AccountId, i + 1, p.Kills, match.SurvivalSeconds(p)))
            .ToArray();
        PlayerState? winner = r.Reason == CompletionReason.Winner ? players.FirstOrDefault(p => p.Id == r.WinnerId) : null;
        string reason = winner != null ? "WINNER" : r.Reason == CompletionReason.Timeout ? "TIMEOUT" : "CANCELLED";
        return new ResultSubmission(mapVersion, reason, winner?.AccountId, match.DurationSeconds, participants);
    }

    private async Task SubmitAsync(Guid matchId, ResultSubmission result, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 6 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                (HttpStatusCode status, string body) = await _api.PostResultAsync(matchId, result, ct);
                Log("result submitted", new { matchId, status = (int)status, attempt });
                // 2xx done; 4xx will not get better by retrying (rejected, conflict): keep the evidence in the log.
                if ((int)status < 500)
                {
                    if ((int)status >= 400) Log("result refused by the platform", new { matchId, body });
                    return;
                }
            }
            catch (HttpRequestException e)
            {
                Log("result submission failed", new { matchId, attempt, error = e.Message });
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Log("result submission timed out", new { matchId, attempt });
            }
            await Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), ct);
        }
    }

    private async Task<HeartbeatResponse?> Heartbeat(string state, Guid? matchId, string? phase, int connected, CancellationToken ct, double? tickMsP99 = null)
    {
        try
        {
            return await _api.HeartbeatAsync(
                new HeartbeatRequest(_o.Address, _o.Region, _o.BuildVersion, MapVersions, state, matchId, phase, connected, tickMsP99, Draining), ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log("heartbeat failed", new { error = e.Message });
            return null;
        }
    }

    private static string PhaseName(MatchPhase phase) => phase switch
    {
        MatchPhase.WaitingForPlayers => "WAITING",
        MatchPhase.Countdown => "COUNTDOWN",
        MatchPhase.Active => "ACTIVE",
        _ => "COMPLETED",
    };

    private static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); }
        catch (TaskCanceledException) { }
    }

    private static void Log(string msg, object fields) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new { level = "info", msg, fields }, PlatformClient.Json));
}
