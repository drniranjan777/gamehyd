using NUnit.Framework;
using RWBR.Client.Online;
using RWBR.Shared.Bots;
using UnityEngine;

namespace RWBR.Tests
{
    /// <summary>Phase C part 5: the offline bot match's difficulty is remembered.</summary>
    public class BotSettingTests
    {
        [Test]
        public void Bot_difficulty_is_remembered()
        {
            BotDifficulty saved = OnlineGame.BotDifficultySetting;
            try
            {
                OnlineGame.BotDifficultySetting = BotDifficulty.Hard;
                Assert.AreEqual(BotDifficulty.Hard, OnlineGame.BotDifficultySetting);
                PlayerPrefs.SetInt("rwbr.botDifficulty", 9); // garbage: clamped
                Assert.AreEqual(BotDifficulty.Hard, OnlineGame.BotDifficultySetting);
            }
            finally
            {
                OnlineGame.BotDifficultySetting = saved;
            }
        }
    }
}
