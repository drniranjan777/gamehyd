using NUnit.Framework;
using RWBR.Client.Online;
using UnityEngine;

namespace RWBR.Tests
{
    public class StoreTests
    {
        [Test]
        public void Dev_receipts_match_the_api_byte_for_byte()
        {
            // Reference from apps/api: devReceipt(secret, orderId, 'outfit_lakeside_runner', 'unity-tx-1').
            const string expected =
                "eyJvcmRlcklkIjoiMGQ5ZjZhM2UtOGMxYi00ZjdhLTllMmQtNWI2YzdhOGQ5ZTBmIiwic2t1Ijoib3V0Zml0X2xha2VzaWRlX3J1bm5lciIsInRyYW5zYWN0aW9uSWQiOiJ1bml0eS10eC0xIn0" +
                ".SNhYmKWEMuM8yT4Nf0ydFUNVlvVK5fIWAkuJo7hpCQI";
            var dev = new DevStorePurchaser("unity-dev-secret-0123456789abcdef0123");
            Assert.AreEqual(expected, dev.Receipt("0d9f6a3e-8c1b-4f7a-9e2d-5b6c7a8d9e0f", "outfit_lakeside_runner", "unity-tx-1"));
        }

        [Test]
        public void Reads_the_catalogue_and_formats_prices()
        {
            const string json = "{\"storeEnabled\":true,\"items\":[{\"productKey\":\"gems_100\",\"displayName\":\"100 Gems\",\"description\":\"d\"," +
                                "\"price\":{\"currency\":\"INR\",\"amountMinor\":8900},\"grants\":[{\"type\":\"CURRENCY\",\"currency\":\"GEM\",\"amount\":100}]," +
                                "\"skus\":{\"ANDROID\":\"rwbr.gems_100\",\"IOS\":\"rwbr.gems_100\"},\"owned\":false}," +
                                "{\"productKey\":\"banner_irani_chai\",\"displayName\":\"Irani Chai banner\",\"description\":\"d\"," +
                                "\"price\":{\"currency\":\"COIN\",\"amountMinor\":150},\"grants\":[{\"type\":\"COSMETIC\",\"cosmeticKey\":\"banner_irani_chai\",\"kind\":\"BANNER\"}]," +
                                "\"skus\":{},\"owned\":true}]}";
            CatalogDto c = JsonUtility.FromJson<CatalogDto>(json);
            Assert.AreEqual(2, c.items.Length);
            Assert.AreEqual("rwbr.gems_100", c.items[0].skus.ANDROID);
            Assert.AreEqual(100, c.items[0].grants[0].amount);
            Assert.IsTrue(c.items[1].owned);
            Assert.AreEqual("₹89.00", StoreFlow.Price(c.items[0].price));
            Assert.AreEqual("150 coins", StoreFlow.Price(c.items[1].price));
            Assert.IsFalse(StoreFlow.IsInGameCurrency(c.items[0]));
            Assert.IsTrue(StoreFlow.IsInGameCurrency(c.items[1]));
        }

        [Test]
        public void Reads_the_season_pass_and_the_gap_between_seasons()
        {
            const string json = "{\"season\":{\"key\":\"monsoon-1\",\"title\":\"Monsoon\",\"startsAt\":\"2026-10-01T00:00:00.000Z\"," +
                                "\"endsAt\":\"2026-11-01T00:00:00.000Z\",\"xpPerTier\":100,\"maxTier\":2,\"premiumProductKey\":\"season_pass_monsoon\"}," +
                                "\"progress\":{\"xp\":150,\"tier\":1,\"premium\":false,\"xpToNextTier\":50}," +
                                "\"rewards\":[{\"tier\":1,\"track\":\"FREE\",\"reward\":{\"type\":\"CURRENCY\",\"currency\":\"COIN\",\"amount\":50},\"unlocked\":true,\"claimed\":false}," +
                                "{\"tier\":1,\"track\":\"PREMIUM\",\"reward\":{\"type\":\"COSMETIC\",\"cosmeticKey\":\"skin_rifle_neon_01\"},\"unlocked\":false,\"claimed\":false}]}";
            SeasonPassDto pass = JsonUtility.FromJson<SeasonPassDto>(json);
            Assert.IsTrue(pass.Active);
            Assert.AreEqual(1, pass.progress.tier);
            Assert.AreEqual(50, pass.rewards[0].reward.amount);
            Assert.AreEqual("skin_rifle_neon_01", pass.rewards[1].reward.cosmeticKey);
            Assert.IsFalse(JsonUtility.FromJson<SeasonPassDto>("{\"season\":null}").Active);
        }

        [Test]
        public void Explains_platform_errors_players_can_act_on()
        {
            StringAssert.Contains("paused", OnlineGame.FriendlyError(new ApiException(503, "MATCHMAKING_DISABLED", "x")));
            StringAssert.Contains("Update", OnlineGame.FriendlyError(new ApiException(426, "CLIENT_OUTDATED", "x")));
            StringAssert.Contains("support", OnlineGame.FriendlyError(new ApiException(403, "ACCOUNT_RESTRICTED", "x")));
            Assert.AreEqual("Platform error X: y", OnlineGame.FriendlyError(new ApiException(500, "X", "y")));
        }

        [Test]
        public void Reads_news_and_match_results()
        {
            EventListDto news = JsonUtility.FromJson<EventListDto>(
                "{\"items\":[{\"key\":\"bonfire\",\"title\":\"Bonfire Night\",\"body\":\"Double XP\",\"startsAt\":\"2026-10-22T10:00:00Z\",\"endsAt\":\"2026-10-23T10:00:00Z\",\"live\":true}]}");
            Assert.IsTrue(news.items[0].live);
            MatchResultDto result = JsonUtility.FromJson<MatchResultDto>(
                "{\"matchId\":\"m1\",\"status\":\"COMPLETED\",\"reason\":\"WINNER\",\"winnerPlayerId\":\"p1\",\"durationSeconds\":300,\"mapVersion\":\"hyd-city-v1\"," +
                "\"participants\":[{\"playerId\":\"p1\",\"displayName\":\"Ravi\",\"placement\":1,\"kills\":3},{\"playerId\":\"p2\",\"displayName\":\"Meera\",\"placement\":2,\"kills\":0}]}");
            Assert.AreEqual(2, result.participants.Length);
            Assert.AreEqual("Meera", result.participants[1].displayName);
        }

        [Test]
        public void Real_money_products_need_a_store_in_this_build()
        {
            var flow = new StoreFlow(new PlatformApi("http://127.0.0.1:1/api/v1"), null);
            Assert.IsFalse(flow.CanBuyWithMoney);
            var item = new CatalogItemDto { productKey = "gems_100", price = new MoneyDto { currency = "INR", amountMinor = 8900 } };
            System.Threading.Tasks.Task<OrderDto> t = flow.BuyAsync(item); // fails before any network call
            Assert.IsTrue(t.IsFaulted);
            Assert.AreEqual("STORE_UNAVAILABLE", ((ApiException)t.Exception.InnerException).Code);
        }
    }
}
