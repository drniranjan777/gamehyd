using System.Collections;
using NUnit.Framework;
using RWBR.Client.Combat;
using RWBR.Client.Online;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using UnityEngine;
using UnityEngine.TestTools;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    /// <summary>Phase B part 3: other players' aim and view pitch from snapshots reach their weapons on screen.</summary>
    public class RemotePoseTests
    {
        private GameObject _root;

        [TearDown]
        public void Clean()
        {
            if (_root != null) Object.Destroy(_root);
        }

        private static SnapshotPacket Snap(uint tick, float pitchRadians, bool aiming)
        {
            var s = new SnapshotPacket { Tick = tick, Phase = MatchPhase.Active };
            PlayerFlags common = PlayerFlags.Alive | PlayerFlags.Connected | PlayerFlags.HasWeapon | PlayerFlags.Grounded;
            s.Players.Add(new SnapshotEntry { PlayerId = 1, X = 0, Z = 0, Health = 100, Flags = common, Weapon = WeaponCatalog.Rifle });
            s.Players.Add(Quantize.Normalize(new SnapshotEntry
            {
                PlayerId = 2, X = 5, Z = 5, Yaw = 0, Pitch = pitchRadians, Health = 100, Weapon = WeaponCatalog.Rifle,
                Flags = common | (aiming ? PlayerFlags.Aiming : PlayerFlags.None),
            }));
            return s;
        }

        [UnityTest]
        public IEnumerator Remote_weapon_follows_their_pitch_and_rises_when_they_aim()
        {
            _root = new GameObject("RemotePoseTest");
            var views = new OnlineViews(_root.transform);
            views.Build(MapLayout.GreyboxBlock(40));
            var interp = new RemoteInterpolator(0);

            uint tick = 0;
            SnapshotPacket latest = null;
            IEnumerator Run(float seconds, float pitch, bool aiming)
            {
                float until = Time.time + seconds;
                while (Time.time < until)
                {
                    // Snapshots at the server's 30 Hz, whatever the test's frame rate.
                    uint now = 1 + (uint)(Time.time * 30f);
                    if (latest == null || now > tick)
                    {
                        tick = now;
                        latest = Snap(tick, pitch, aiming);
                        interp.Add(latest);
                    }
                    interp.Advance(Time.deltaTime, 30);
                    views.Render(1, SVector3.Zero, 0, MotorState.StandingAt(SVector3.Zero), interp, latest, Time.time);
                    yield return null;
                }
            }

            yield return Run(0.3f, 0, false);
            WeaponHolder remote = views.WeaponOf(2);
            Assert.IsNotNull(remote?.Current, "the other player holds a weapon model");
            Assert.AreEqual(0f, views.AimShownFor(2), 1e-3f);
            Assert.AreEqual(0f, remote.Forward.y, 0.05f, "level while they look level");

            // They look 20 degrees down and aim.
            yield return Run(0.4f, -20f * Mathf.Deg2Rad, true);
            Assert.AreEqual(1f, views.AimShownFor(2), 1e-3f, "aim blend eased in");
            Assert.AreEqual(-Mathf.Sin(20f * Mathf.Deg2Rad), remote.Forward.y, 0.03f, "weapon pitched down with their view");

            yield return Run(0.4f, 0, false);
            Assert.AreEqual(0f, views.AimShownFor(2), 1e-3f);
            views.Clear();
        }
    }
}
