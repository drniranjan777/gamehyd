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
