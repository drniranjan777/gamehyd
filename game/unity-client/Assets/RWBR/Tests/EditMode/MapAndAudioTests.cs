using NUnit.Framework;
using RWBR.Client.Audio;
using RWBR.Client.Online;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Tests
{
    /// <summary>Phase C part 3 on the client: the map picture for the minimap and full map, and district ambience.</summary>
    public class MapAndAudioTests
    {
        [Test]
        public void Map_picture_is_built_once_per_map_version()
        {
            Texture2D v2 = MapHud.Picture(CityMap.BuildV2());
            Assert.AreEqual(1024, v2.width);
            Assert.AreEqual(1024, v2.height);
            Assert.AreSame(v2, MapHud.Picture(CityMap.BuildV2()));
            Assert.AreNotSame(v2, MapHud.Picture(CityMap.Build()));
        }

        [Test]
        public void Every_district_has_a_soundscape_and_each_loop_is_audible_and_unclipped()
        {
            foreach (District d in CityMap.BuildV2().Districts)
                Assert.AreNotEqual(Ambience.None, GameAudio.AmbienceFor(d.Name), d.Name);
            Assert.AreEqual(Ambience.Market, GameAudio.AmbienceFor("Old City Bazaar"));
            Assert.AreEqual(Ambience.Water, GameAudio.AmbienceFor("Musi Riverside"));
            Assert.AreEqual(Ambience.Industrial, GameAudio.AmbienceFor("Industrial Estate"));
            Assert.AreEqual(Ambience.City, GameAudio.AmbienceFor(""), "the avenues between districts are city streets");
            foreach (Ambience a in new[] { Ambience.City, Ambience.Market, Ambience.Industrial, Ambience.Water, Ambience.Countryside })
            {
                AudioClip clip = GameAudio.AmbienceClip(a);
                Assert.AreEqual(6f, clip.length, 0.01f);
                float peak = GameAudio.Peak(clip);
                Assert.Greater(peak, 0.02f, $"{a} is silent");
                Assert.LessOrEqual(peak, 1f);
            }
        }
    }
}
