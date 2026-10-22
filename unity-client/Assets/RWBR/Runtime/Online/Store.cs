using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RWBR.Client.Online
{
    /// <summary>
    /// A platform store (Google Play Billing, StoreKit) seen from the game: buy a SKU for an order, return the proof
    /// the server verifies. Unity IAP implements this once the developer accounts exist (Phase 5 status).
    /// </summary>
    public interface IStorePurchaser
    {
        /// <summary>The order platform the server expects: ANDROID, IOS or DEV.</summary>
        string Platform { get; }

        /// <summary>Runs the platform purchase for <paramref name="order"/>; returns the proof, or null if cancelled.</summary>
        Task<string> PurchaseAsync(OrderDto order);
    }

    /// <summary>
    /// Development stand-in for a platform store: signs a receipt the API accepts when DEV_PURCHASES_ENABLED is on.
    /// Only in the editor and development builds, and only when RWBR_DEV_PURCHASE_SECRET is set.
    /// </summary>
    public sealed class DevStorePurchaser : IStorePurchaser
    {
        private readonly byte[] _secret;

        public DevStorePurchaser(string secret) => _secret = Encoding.UTF8.GetBytes(secret);

        public string Platform => "DEV";

        public static DevStorePurchaser FromEnvironment()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string secret = Environment.GetEnvironmentVariable("RWBR_DEV_PURCHASE_SECRET");
            return string.IsNullOrEmpty(secret) ? null : new DevStorePurchaser(secret);
#else
            return null;
#endif
        }

        public Task<string> PurchaseAsync(OrderDto order) =>
            Task.FromResult(Receipt(order.orderId, order.sku, "dev-" + Guid.NewGuid().ToString("N")));

        [Serializable]
        private sealed class Body
        {
            public string orderId;
            public string sku;
            public string transactionId;
        }

        /// <summary>Same format as devReceipt() in apps/api/src/store/verifiers.ts: base64url(json).base64url(hmac).</summary>
        public string Receipt(string orderId, string sku, string transactionId)
        {
            string body = Base64Url(Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Body { orderId = orderId, sku = sku, transactionId = transactionId })));
            using var hmac = new HMACSHA256(_secret);
            return body + "." + Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(body)));
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Purchase flow: coin and gem products are bought in one server call; real-money products create an order, go
    /// through the platform store, then the server verifies the proof. Network failures retry with the same
    /// idempotency key, so a purchase is never charged or granted twice.
    /// </summary>
    public sealed class StoreFlow
    {
        private readonly PlatformApi _api;
        private readonly IStorePurchaser _store;

        public StoreFlow(PlatformApi api, IStorePurchaser store)
        {
            _api = api;
            _store = store;
        }

        /// <summary>Whether this build can sell real-money products.</summary>
        public bool CanBuyWithMoney => _store != null;

        public static bool IsInGameCurrency(CatalogItemDto item) => item.price.currency == "COIN" || item.price.currency == "GEM";

        public async Task<OrderDto> BuyAsync(CatalogItemDto item)
        {
            string key = "rwbr-" + Guid.NewGuid().ToString("N");
            if (IsInGameCurrency(item)) return await Retry(() => _api.CreateOrderAsync(item.productKey, "IN_GAME", key));
            if (_store == null) throw new ApiException(0, "STORE_UNAVAILABLE", "Purchases with money are not available in this build yet.");

            OrderDto order = await Retry(() => _api.CreateOrderAsync(item.productKey, _store.Platform, key));
            if (order.status == "FULFILLED") return order;
            string proof = await _store.PurchaseAsync(order);
            if (proof == null) throw new ApiException(0, "CANCELLED", "Purchase cancelled.");
            return await Retry(() => _api.VerifyAsync(order.orderId, proof));
        }

        private static async Task<T> Retry<T>(Func<Task<T>> call)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await call();
                }
                catch (ApiException e) when (e.Code == "NETWORK" && attempt < 3)
                {
                    await Task.Delay(500 * attempt);
                }
            }
        }

        public static string Price(MoneyDto m) => m.currency switch
        {
            "INR" => "₹" + (m.amountMinor / 100m).ToString("0.00"),
            "COIN" => m.amountMinor + " coins",
            _ => m.amountMinor + " gems",
        };
    }
}
