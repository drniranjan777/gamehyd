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
        public void Bot_practice_mode_is_remembered_and_only_solo_duo_or_squad()
        {
            int saved = OnlineGame.BotTeamSizeSetting;
            try
            {
                OnlineGame.BotTeamSizeSetting = 4;
                Assert.AreEqual(4, OnlineGame.BotTeamSizeSetting);
                Assert.AreEqual("Squad", OnlineGame.ModeName(4));
                OnlineGame.BotTeamSizeSetting = 3; // not a mode: Solo
                Assert.AreEqual(1, OnlineGame.BotTeamSizeSetting);
            }
            finally
            {
                OnlineGame.BotTeamSizeSetting = saved;
            }
        }

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
