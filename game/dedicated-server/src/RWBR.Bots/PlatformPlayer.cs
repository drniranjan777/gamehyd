using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RWBR.Bots;

public sealed record Seat(Guid MatchId, string Server, string Ticket, string SessionKey, DateTimeOffset ExpiresAt, string MapVersion)
{
    public IPEndPoint Endpoint
    {
        get
        {
            int colon = Server.LastIndexOf(':');
            string host = Server[..colon];
            IPAddress ip = IPAddress.TryParse(host, out IPAddress? parsed) ? parsed : Dns.GetHostAddresses(host)[0];
            return new IPEndPoint(ip, int.Parse(Server[(colon + 1)..]));
        }
    }
}

public sealed record Stats(int MatchesPlayed, int Wins, int Kills, double WinRate, int Level, int Xp);

/// <summary>A bot's account on the platform API: guest sign-up, matchmaking, and reading back its stats.</summary>
public sealed class PlatformPlayer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public PlatformPlayer(Uri apiBase)
    {
        _http = new HttpClient { BaseAddress = apiBase.AbsoluteUri.EndsWith('/') ? apiBase : new Uri(apiBase.AbsoluteUri + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public Guid UserId { get; private set; }

    public async Task SignUpAsync(string displayName, CancellationToken ct)
    {
        using HttpResponseMessage res = await _http.PostAsJsonAsync("auth/guest", new { displayName }, Json, ct);
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct));
        UserId = auth.GetProperty("userId").GetGuid();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString());
    }

    /// <summary>Queues and polls until the platform hands out a seat.</summary>
    public async Task<Seat> FindMatchAsync(string region, TimeSpan timeout, CancellationToken ct)
    {
        using (HttpResponseMessage q = await _http.PostAsJsonAsync("matchmaking/queue", new { mode = "SOLO", region, clientVersion = "0.1.0" }, Json, ct))
            q.EnsureSuccessStatusCode();
        DateTime until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            JsonElement status = await _http.GetFromJsonAsync<JsonElement>("matchmaking/status", Json, ct);
            if (status.GetProperty("state").GetString() == "MATCHED")
                return status.GetProperty("seat").Deserialize<Seat>(Json)!;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("no seat within the timeout");
    }

    public Task<Stats?> StatsAsync(CancellationToken ct) => _http.GetFromJsonAsync<Stats>("player/stats", Json, ct);

    public void Dispose() => _http.Dispose();
}
