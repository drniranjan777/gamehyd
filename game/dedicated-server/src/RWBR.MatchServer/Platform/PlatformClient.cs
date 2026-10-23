using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RWBR.Shared;

namespace RWBR.MatchServer.Platform;

public sealed record HeartbeatRequest(
    string Address,
    string Region,
    string BuildVersion,
    string[] MapVersions,
    string State,
    Guid? MatchId,
    string? MatchPhase,
    int ConnectedPlayers,
    double? TickMsP99 = null,
    bool Draining = false);

public sealed record MatchAssignment(Guid MatchId, string MapVersion, int Capacity, int MinPlayers, int FillWaitSeconds, string[]? DisabledWeapons = null,
    LootTableConfig? LootTable = null, bool FillBots = false, string? BotDifficulty = null, int TeamSize = 1);

public sealed record HeartbeatResponse(Guid ServerId, MatchAssignment? Assignment);

public sealed record ResultParticipant(Guid AccountId, int Placement, int Kills, int SurvivalSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Team = null);

public sealed record ResultSubmission(
    string MapVersion,
    string Reason,
    Guid? WinnerAccountId,
    int DurationSeconds,
    ResultParticipant[] Participants,
    int Bots = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Teams = null);

/// <summary>
/// Signed HTTP calls to the platform API's /internal endpoints (packages/api-contracts/openapi.yaml,
/// serverSignature): HMAC-SHA256 over "timestamp.METHOD.path.sha256(body)".
/// </summary>
public sealed class PlatformClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly byte[] _secret;
    private readonly string _address;

    public PlatformClient(Uri apiBase, string sharedSecret, string serverAddress, HttpMessageHandler? handler = null)
    {
        _base = apiBase.AbsoluteUri.EndsWith('/') ? apiBase : new Uri(apiBase.AbsoluteUri + "/");
        _secret = Encoding.UTF8.GetBytes(sharedSecret);
        _address = serverAddress;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<HeartbeatResponse?> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        using HttpResponseMessage res = await SendAsync("internal/game-servers/heartbeat", request, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<HeartbeatResponse>(Json, ct);
    }

    public async Task<(HttpStatusCode Status, string Body)> PostResultAsync(Guid matchId, ResultSubmission result, CancellationToken ct)
    {
        using HttpResponseMessage res = await SendAsync($"internal/matches/{matchId}/result", result, ct);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    private Task<HttpResponseMessage> SendAsync<T>(string relative, T body, CancellationToken ct)
    {
        var uri = new Uri(_base, relative);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string payload = $"{ts}.POST.{uri.AbsolutePath}.{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
        string signature = Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var msg = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(bytes) };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        msg.Headers.Add("X-RWBR-Server", _address);
        msg.Headers.Add("X-RWBR-Timestamp", ts);
        msg.Headers.Add("X-RWBR-Signature", signature);
        return _http.SendAsync(msg, ct);
    }

    public void Dispose() => _http.Dispose();
}
