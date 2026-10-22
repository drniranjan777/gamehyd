using NUnit.Framework;
using RWBR.Client.Online;
using UnityEngine;

namespace RWBR.Tests
{
    /// <summary>The DTOs must read the API's JSON (shapes from packages/api-contracts/openapi.yaml).</summary>
    public class PlatformApiTests
    {
        [Test]
        public void Reads_a_matched_queue_status_with_its_seat()
        {
            const string json = "{\"state\":\"MATCHED\",\"seat\":{\"matchId\":\"9b0c\",\"server\":\"10.0.0.5:7777\"," +
                                "\"ticket\":\"AQID\",\"sessionKey\":\"BAUG\",\"expiresAt\":\"2026-10-22T10:00:00.000Z\",\"mapVersion\":\"greybox-block-40-v2\"}}";
            QueueStatusDto s = JsonUtility.FromJson<QueueStatusDto>(json);
            Assert.AreEqual("MATCHED", s.state);
            Assert.AreEqual("10.0.0.5:7777", s.seat.server);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, System.Convert.FromBase64String(s.seat.ticket));
        }

        [Test]
        public void Reads_a_new_guest_account_once_and_later_sign_ins_without_credentials()
        {
            AuthResponse created = JsonUtility.FromJson<AuthResponse>(
                "{\"userId\":\"u1\",\"accessToken\":\"a\",\"accessTokenExpiresIn\":900,\"refreshToken\":\"r\",\"refreshTokenExpiresAt\":\"x\"," +
                "\"guest\":{\"deviceId\":\"d1\",\"deviceSecret\":\"s1\"}}");
            Assert.AreEqual("d1", created.guest.deviceId);

            AuthResponse again = JsonUtility.FromJson<AuthResponse>(
                "{\"userId\":\"u1\",\"accessToken\":\"a\",\"accessTokenExpiresIn\":900,\"refreshToken\":\"r\",\"refreshTokenExpiresAt\":\"x\"}");
            Assert.IsTrue(again.guest == null || string.IsNullOrEmpty(again.guest.deviceId));
        }

        [Test]
        public void Validates_display_names_like_the_api()
        {
            Assert.IsTrue(PlatformApi.IsValidName("Ravi_07"));
            Assert.IsFalse(PlatformApi.IsValidName("ab"));
            Assert.IsFalse(PlatformApi.IsValidName("<script>"));
        }
    }
}
