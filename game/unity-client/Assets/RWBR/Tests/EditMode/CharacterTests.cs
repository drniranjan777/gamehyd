using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RWBR.Client.Character;
using RWBR.EditorTools;
using RWBR.Shared;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace RWBR.Tests
{
    /// <summary>Phase B part 2: Mixamo file matching, the generated animator controller, locomotion maths, turn in place.</summary>
    public class CharacterTests
    {
        private const string TempFolder = "Assets/RWBR/Tests/TempCharacters";

        [TearDown]
        public void Clean()
        {
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
        }

        // Names as Mixamo downloads them (packs and single animations).
        private static readonly string[] Files =
        {
            "Rifle Idle.fbx", "Rifle Walk Forward.fbx", "walk backwards.fbx", "walk left.fbx", "Rifle Run Forward.fbx", "run backward.fbx",
            "run left.fbx", "run right.fbx", "Sprint Forward.fbx", "Crouch Idle.fbx", "Crouch Walk Forward.fbx", "jump up.fbx", "jump loop.fbx",
            "jump down.fbx", "Rifle Aiming Idle.fbx", "Reloading.fbx", "Hit Reaction.fbx", "Rifle Death.fbx", "Turn Left.fbx",
            "Pistol Idle.fbx", "Pistol Walk.fbx", "Pistol Run.fbx", "Pistol Reload.fbx",
        };

        [Test]
        public void Mixamo_file_names_fill_the_rifle_and_pistol_slots()
        {
            Dictionary<AnimSlot, string> rifle = AnimSlotMatcher.Match(Files, f => f, pistol: false);
            Assert.AreEqual("Rifle Idle.fbx", rifle[AnimSlot.Idle]);
            Assert.AreEqual("Rifle Walk Forward.fbx", rifle[AnimSlot.WalkForward]);
            Assert.AreEqual("walk backwards.fbx", rifle[AnimSlot.WalkBack]);
            Assert.AreEqual("walk left.fbx", rifle[AnimSlot.WalkLeft]);
            Assert.IsFalse(rifle.ContainsKey(AnimSlot.WalkRight), "missing sides are mirrored in the blend tree, not guessed");
            Assert.AreEqual("Rifle Run Forward.fbx", rifle[AnimSlot.RunForward]);
            Assert.AreEqual("Sprint Forward.fbx", rifle[AnimSlot.Sprint]);
            Assert.AreEqual("Crouch Idle.fbx", rifle[AnimSlot.CrouchIdle]);
            Assert.AreEqual("Crouch Walk Forward.fbx", rifle[AnimSlot.CrouchWalk]);
            Assert.AreEqual("jump up.fbx", rifle[AnimSlot.JumpStart]);
            Assert.AreEqual("jump loop.fbx", rifle[AnimSlot.Fall]);
            Assert.AreEqual("jump down.fbx", rifle[AnimSlot.Land]);
            Assert.AreEqual("Rifle Aiming Idle.fbx", rifle[AnimSlot.AimIdle]);
            Assert.AreEqual("Reloading.fbx", rifle[AnimSlot.Reload]);
            Assert.AreEqual("Hit Reaction.fbx", rifle[AnimSlot.Hit]);
            Assert.AreEqual("Rifle Death.fbx", rifle[AnimSlot.Death]);
            Assert.AreEqual("Turn Left.fbx", rifle[AnimSlot.TurnLeft]);
            CollectionAssert.IsEmpty(AnimSlotMatcher.MissingRequired(rifle).ToList());
            Assert.IsFalse(rifle.Values.Any(v => v.StartsWith("Pistol")), "pistol files stay out of the rifle set");

            Dictionary<AnimSlot, string> pistol = AnimSlotMatcher.Match(Files, f => f, pistol: true);
            Assert.AreEqual("Pistol Idle.fbx", pistol[AnimSlot.Idle]);
            Assert.AreEqual("Pistol Walk.fbx", pistol[AnimSlot.WalkForward]);
            Assert.AreEqual("Pistol Run.fbx", pistol[AnimSlot.RunForward]);
            Assert.AreEqual("Pistol Reload.fbx", pistol[AnimSlot.Reload]);
        }

        [Test]
        public void Missing_required_clips_are_reported()
        {
            Dictionary<AnimSlot, string> rifle = AnimSlotMatcher.Match(new[] { "Rifle Idle.fbx", "walking.fbx" }, f => f, pistol: false);
            List<AnimSlot> missing = AnimSlotMatcher.MissingRequired(rifle).ToList();
            CollectionAssert.Contains(missing, AnimSlot.Reload);
            CollectionAssert.Contains(missing, AnimSlot.Death);
            CollectionAssert.DoesNotContain(missing, AnimSlot.Idle);
            CollectionAssert.DoesNotContain(missing, AnimSlot.WalkForward);
        }

        [Test]
        public void Controller_has_the_parameters_layers_and_states_the_driver_uses()
        {
            AssetDatabase.CreateFolder("Assets/RWBR/Tests", "TempCharacters");
            AnimatorController c = CharacterImport.BuildController(TempFolder + "/Test.controller",
                new Dictionary<AnimSlot, AnimationClip>(), new Dictionary<AnimSlot, AnimationClip>());

            string[] names = c.parameters.Select(p => p.name).ToArray();
            foreach (string required in new[]
                     {
                         AnimatorIds.ForwardName, AnimatorIds.StrafeName, AnimatorIds.SpeedName, AnimatorIds.CrouchName, AnimatorIds.GroundedName,
                         AnimatorIds.AimName, AnimatorIds.PitchName, AnimatorIds.PistolName, AnimatorIds.ArmedName, AnimatorIds.ReloadingName,
                         AnimatorIds.DeadName, AnimatorIds.JumpName, AnimatorIds.LandName, AnimatorIds.HitName, AnimatorIds.FireName,
                         AnimatorIds.SwitchName, AnimatorIds.TurnName, AnimatorIds.TurnAngleName, AnimatorIds.ReloadProgressName,
                     })
                CollectionAssert.Contains(names, required);
            Assert.IsTrue(c.parameters.First(p => p.name == AnimatorIds.GroundedName).defaultBool);

            Assert.AreEqual(new[] { "Locomotion", "UpperBody", "Reactions" }, c.layers.Select(l => l.name).ToArray());
            Assert.IsTrue(c.layers.All(l => l.iKPass), "every layer runs the IK pass (hands on the weapon, feet on the ground)");
            Assert.IsNotNull(c.layers[1].avatarMask);
            Assert.IsFalse(c.layers[1].avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg), "aiming does not touch the legs");
            Assert.IsTrue(c.layers[1].avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm));

            string[] baseStates = c.layers[0].stateMachine.states.Select(s => s.state.name).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "Move", "Crouch", "Jump", "Fall", "Death" }, baseStates);
            CollectionAssert.DoesNotContain(baseStates, "Turn", "no turn clip, no turn state");
            Assert.AreEqual("Move", c.layers[0].stateMachine.defaultState.name);
            var moveTree = (BlendTree)c.layers[0].stateMachine.states.First(s => s.state.name == "Move").state.motion;
            Assert.AreEqual(AnimatorIds.PistolName, moveTree.blendParameter);
            Assert.AreEqual(2, moveTree.children.Length, "rifle and pistol locomotion trees");
            CollectionAssert.AreEquivalent(new[] { "Empty", "Aim", "Reload" }, c.layers[1].stateMachine.states.Select(s => s.state.name).ToArray());
            AnimatorState reload = c.layers[1].stateMachine.states.First(s => s.state.name == "Reload").state;
            Assert.IsTrue(reload.timeParameterActive, "the reload clip follows reload progress");
            Assert.AreEqual(AnimatorIds.ReloadProgressName, reload.timeParameter);
        }

        [Test]
        public void Controller_mirrors_a_missing_strafe_side()
        {
            AssetDatabase.CreateFolder("Assets/RWBR/Tests", "TempCharacters");
            var clip = new AnimationClip { name = "walk left" };
            AssetDatabase.CreateAsset(clip, TempFolder + "/walk left.anim");
            var rifle = new Dictionary<AnimSlot, AnimationClip> { [AnimSlot.WalkLeft] = clip };
            AnimatorController c = CharacterImport.BuildController(TempFolder + "/Mirror.controller", rifle, new Dictionary<AnimSlot, AnimationClip>());
            var moveTree = (BlendTree)c.layers[0].stateMachine.states.First(s => s.state.name == "Move").state.motion;
            var rifleTree = (BlendTree)moveTree.children[0].motion;
            ChildMotion[] children = rifleTree.children.Where(ch => ch.position != Vector2.zero).ToArray();
            Assert.AreEqual(2, children.Length);
            Assert.IsTrue(rifleTree.children.Any(ch => ch.position == Vector2.zero && ch.motion != null && ch.motion.name.EndsWith("_Neutral")),
                "missing idle falls back to the neutral pose");
            ChildMotion right = children.First(ch => ch.position.x > 0);
            Assert.IsTrue(right.mirror, "walk right is walk left, mirrored");
            Assert.AreEqual(CharacterImport.WalkPosition, right.position.x, 1e-4f);
        }

        [Test]
        public void Committed_controller_exists_in_resources()
        {
            Assert.IsNotNull(Resources.Load<RuntimeAnimatorController>("Characters/RWBR_Humanoid"),
                "run ./unity-batch.sh characters to (re)generate the controller");
        }

        [Test]
        public void Local_planar_velocity_is_in_the_body_frame()
        {
            Vector2 v = LocomotionMath.LocalPlanar(new Vector3(0, -3, 5), 0);
            Assert.AreEqual(0, v.x, 1e-4f);
            Assert.AreEqual(5, v.y, 1e-4f);
            v = LocomotionMath.LocalPlanar(new Vector3(5, 0, 0), 90); // facing east, moving east
            Assert.AreEqual(0, v.x, 1e-4f);
            Assert.AreEqual(5, v.y, 1e-4f);
            v = LocomotionMath.LocalPlanar(new Vector3(0, 0, 5), 90); // facing east, moving north = strafing left
            Assert.AreEqual(-5, v.x, 1e-4f);
            Assert.IsTrue(LocomotionMath.UsesPistolPoses(WeaponClass.Pistol));
            Assert.IsFalse(LocomotionMath.UsesPistolPoses(WeaponClass.Smg));
            Assert.IsFalse(LocomotionMath.UsesPistolPoses(null));
        }

        [Test]
        public void Turn_in_place_keeps_the_feet_until_the_view_is_far_enough_then_turns()
        {
            var turn = new TurnInPlace();
            turn.Snap(0);
            Assert.IsFalse(turn.Update(40, moving: false, aiming: false, 0.016f));
            Assert.AreEqual(0, turn.BodyYaw, 1e-3f, "small looks move the head, not the feet");

            Assert.IsTrue(turn.Update(90, false, false, 0.016f), "past the threshold a turn starts");
            Assert.AreEqual(90, turn.LastTurnAngle, 1e-3f);
            for (int i = 0; i < 40; i++) turn.Update(90, false, false, 0.016f);
            Assert.AreEqual(90, turn.BodyYaw, 1f);
            Assert.IsFalse(turn.Turning);

            turn.Update(200, moving: true, aiming: false, 1f);
            Assert.AreEqual(200, Mathf.Repeat(turn.BodyYaw, 360f), 1f, "moving: the body faces the view");
        }
    }
}
