using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace RWBR.Client.Online
{
    // DTOs mirror packages/api-contracts/openapi.yaml (JsonUtility needs public fields and [Serializable]).
    [Serializable] public sealed class GuestCredentials { public string deviceId; public string deviceSecret; }
    [Serializable] public sealed class AuthResponse
    {
        public string userId;
        public string accessToken;
        public int accessTokenExpiresIn;
        public string refreshToken;
        public string refreshTokenExpiresAt;
        public GuestCredentials guest;
    }
    [Serializable] public sealed class SeatDto { public string matchId; public string server; public string ticket; public string sessionKey; public string expiresAt; public string mapVersion; }
    [Serializable] public sealed class QueueStatusDto { public string state; public string enqueuedAt; public string region; public SeatDto seat; }
    [Serializable] public sealed class ProfileDto { public string playerId; public string displayName; public int level; public int xp; public int xpForNextLevel; }
    [Serializable] public sealed class StatsDto { public int matchesPlayed; public int wins; public int kills; public float winRate; public int level; public int xp; }
    [Serializable] public sealed class InventoryItemDto { public string cosmeticKey; public string kind; public string displayName; public string rarity; public string acquiredAt; }
    [Serializable] public sealed class LoadoutDto { public string WEAPON_SKIN; public string OUTFIT; public string EMOTE; public string PARACHUTE; public string BANNER; }
    [Serializable] public sealed class WalletsDto { public long COIN; public long GEM; }
    [Serializable] public sealed class InventoryDto { public InventoryItemDto[] items; public LoadoutDto loadout; public WalletsDto wallets; }
    [Serializable] public sealed class MatchHistoryItemDto
    {
        public string matchId; public string mapVersion; public string completedAt; public int placement; public int players;
        public int kills; public int survivalSeconds; public int xpAwarded; public int coinsAwarded;
    }
    [Serializable] public sealed class MatchHistoryPageDto { public MatchHistoryItemDto[] items; public string nextCursor; }
    [Serializable] public sealed class ErrorDto { public ErrorBody error; }
    [Serializable] public sealed class ErrorBody { public string code; public string message; public string requestId; }

    public sealed class ApiException : Exception
    {
        public ApiException(long status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }

        public long Status { get; }
        public string Code { get; }
    }

    /// <summary>
    /// Minimal platform API client over UnityWebRequest (async on the main thread). Guest credentials are stored in
    /// PlayerPrefs for this prototype; production must use the Android Keystore / iOS Keychain.
    /// </summary>
    public sealed class PlatformApi
    {
        private const string DeviceIdKey = "rwbr.guest.deviceId";
        private const string DeviceSecretKey = "rwbr.guest.deviceSecret";
        private readonly string _base;
        private string _accessToken;

        public PlatformApi(string baseUrl) => _base = baseUrl.TrimEnd('/');

        public string UserId { get; private set; }
        public bool SignedIn => _accessToken != null;

        /// <summary>Signs in with stored guest credentials, or creates a guest account on first run.</summary>
        public async Task SignInAsync(string displayName)
        {
            string id = PlayerPrefs.GetString(DeviceIdKey, ""), secret = PlayerPrefs.GetString(DeviceSecretKey, "");
            AuthResponse auth;
            if (id.Length > 0 && secret.Length > 0)
            {
                try
                {
                    auth = await Send<AuthResponse>("POST", "/auth/guest", JsonUtility.ToJson(new GuestCredentials { deviceId = id, deviceSecret = secret }));
                }
                catch (ApiException e) when (e.Status == 401)
                {
                    auth = await CreateGuest(displayName); // stored credentials are for another environment
                }
            }
            else
            {
                auth = await CreateGuest(displayName);
            }
            _accessToken = auth.accessToken;
            UserId = auth.userId;
        }

        private async Task<AuthResponse> CreateGuest(string displayName)
        {
            string body = IsValidName(displayName) ? $"{{\"displayName\":\"{displayName}\"}}" : "{}";
            AuthResponse auth = await Send<AuthResponse>("POST", "/auth/guest", body);
            if (auth.guest != null && !string.IsNullOrEmpty(auth.guest.deviceId))
            {
                PlayerPrefs.SetString(DeviceIdKey, auth.guest.deviceId);
                PlayerPrefs.SetString(DeviceSecretKey, auth.guest.deviceSecret);
                PlayerPrefs.Save();
            }
            return auth;
        }

        public Task<QueueStatusDto> QueueAsync(string region, string clientVersion) =>
            Send<QueueStatusDto>("POST", "/matchmaking/queue", $"{{\"mode\":\"SOLO\",\"region\":\"{region}\",\"clientVersion\":\"{clientVersion}\"}}");

        public Task<QueueStatusDto> StatusAsync() => Send<QueueStatusDto>("GET", "/matchmaking/status", null);

        public Task LeaveQueueAsync() => Send<QueueStatusDto>("DELETE", "/matchmaking/queue", null);

        public Task<ProfileDto> ProfileAsync() => Send<ProfileDto>("GET", "/player/profile", null);

        public Task<StatsDto> StatsAsync() => Send<StatsDto>("GET", "/player/stats", null);

        public Task<InventoryDto> InventoryAsync() => Send<InventoryDto>("GET", "/inventory", null);

        public Task<InventoryDto> EquipAsync(string slot, string cosmeticKey) =>
            Send<InventoryDto>("PUT", "/inventory/loadout/" + slot, $"{{\"cosmeticKey\":\"{cosmeticKey}\"}}");

        public Task<MatchHistoryPageDto> MatchesAsync(string cursor = null, int limit = 10) =>
            Send<MatchHistoryPageDto>("GET", $"/player/matches?limit={limit}" + (cursor != null ? "&cursor=" + UnityWebRequest.EscapeURL(cursor) : ""), null);

        public static bool IsValidName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length >= 3 && name.Length <= 24 &&
            System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_. -]+$");

        private async Task<T> Send<T>(string method, string path, string json) where T : class
        {
            using var req = new UnityWebRequest(_base + path, method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 10,
            };
            if (json != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            if (_accessToken != null) req.SetRequestHeader("Authorization", "Bearer " + _accessToken);

            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            string text = req.downloadHandler?.text ?? "";
            if (req.result == UnityWebRequest.Result.ConnectionError)
                throw new ApiException(0, "NETWORK", $"Cannot reach the platform: {req.error}");
            if (req.responseCode >= 400)
            {
                ErrorDto err = null;
                try { err = JsonUtility.FromJson<ErrorDto>(text); }
                catch (ArgumentException) { }
                throw new ApiException(req.responseCode, err?.error?.code ?? "HTTP_" + req.responseCode, err?.error?.message ?? req.error);
            }
            return string.IsNullOrEmpty(text) ? null : JsonUtility.FromJson<T>(text);
        }
    }
}
