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
        public void Engine_sound_loops_on_whole_cycles_and_is_unclipped()
        {
            AudioClip engine = GameAudio.EngineClip();
            Assert.AreSame(engine, GameAudio.EngineClip());
            Assert.Greater(GameAudio.Peak(engine), 0.2f);
            Assert.LessOrEqual(GameAudio.Peak(engine), 1f);
            var d = new float[engine.samples];
            engine.GetData(d, 0);
            float largestStep = 0;
            for (int i = 1; i < d.Length; i++) largestStep = Mathf.Max(largestStep, Mathf.Abs(d[i] - d[i - 1]));
            Assert.LessOrEqual(Mathf.Abs(d[0] - d[d.Length - 1]), largestStep, "the loop point is no bigger a jump than the waveform's own");
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
