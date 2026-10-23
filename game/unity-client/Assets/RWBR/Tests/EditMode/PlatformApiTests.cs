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
        public void Reads_the_current_party_or_none()
        {
            CurrentPartyDto some = JsonUtility.FromJson<CurrentPartyDto>(
                "{\"party\":{\"id\":\"p1\",\"code\":\"K7QW2M\",\"mode\":\"DUO\",\"leaderId\":\"u1\"," +
                "\"members\":[{\"accountId\":\"u1\",\"displayName\":\"Asha\",\"leader\":true},{\"accountId\":\"u2\",\"displayName\":\"Ravi\",\"leader\":false}]}}");
            Assert.IsTrue(some.party.Exists);
            Assert.AreEqual("K7QW2M", some.party.code);
            Assert.AreEqual(2, some.party.members.Length);
            Assert.IsTrue(some.party.members[0].leader);

            CurrentPartyDto none = JsonUtility.FromJson<CurrentPartyDto>("{\"party\":null}");
            Assert.IsTrue(none.party == null || !none.party.Exists);
        }

        [Test]
        public void Names_modes_and_checks_party_codes_like_the_api()
        {
            Assert.AreEqual("SOLO", OnlineGame.ModeKey(1));
            Assert.AreEqual("DUO", OnlineGame.ModeKey(2));
            Assert.AreEqual("SQUAD", OnlineGame.ModeKey(4));
            Assert.IsTrue(PlatformApi.IsValidPartyCode("k7qw2m"));
            Assert.IsFalse(PlatformApi.IsValidPartyCode("K7QW2"));
            Assert.IsFalse(PlatformApi.IsValidPartyCode("K7QW2\""));
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
