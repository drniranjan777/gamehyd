using System.Collections;
using NUnit.Framework;
using RWBR.Client.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    public class GreyboxSceneTests
    {
        private sealed class ScriptedInput : IPlayerInputSource
        {
            public PlayerInputFrame Frame;
            public PlayerInputFrame Read() => Frame;
        }

        [UnityTest]
        public IEnumerator Player_walks_through_the_doorway_into_the_building()
        {
            yield return SceneManager.LoadSceneAsync("Greybox", LoadSceneMode.Single);
            GreyboxPlayer player = Object.FindAnyObjectByType<GreyboxPlayer>();
            Assert.IsNotNull(player, "Greybox scene has no player");

            var input = new ScriptedInput { Frame = new PlayerInputFrame { Move = Vector2.up } };
            player.InputSource = input;
            float startZ = player.transform.position.z;

            // 3 s at walk speed covers ~15 m: from z=-20 through the south doorway (z=-6) into the room.
            float until = Time.time + 3f;
            while (Time.time < until) yield return null;
            input.Frame = default;
            yield return null;

            Vector3 p = player.transform.position;
            Assert.Greater(p.z, startZ + 10f, "player did not move forward");
            Assert.That(p.z, Is.InRange(-6f, 6f), "player is not inside the building");
            Assert.Greater(player.TicksSimulated, 60);

            Camera cam = Camera.main;
            Assert.IsNotNull(cam);
            Assert.Less(Vector3.Distance(cam.transform.position, p), 6f, "camera is not following the player");
        }
    }
}
