using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using RWBR.Client.Character;
using RWBR.Client.Combat;
using RWBR.Shared;
using UnityEngine;
using UnityEngine.TestTools;

namespace RWBR.Tests
{
    /// <summary>
    /// Drives the real animator controller and <see cref="CharacterAnimator"/> on a procedurally built humanoid skeleton
    /// (no art: a T-pose rig made of empty transforms with a humanoid avatar). This checks the plumbing the Mixamo
    /// character will use: parameters from movement, death, crouch, turn in place, hands on the weapon and feet on the
    /// ground through IK. It does not judge how the real clips look.
    /// </summary>
    public class RiggedCharacterTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void Clean()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.Destroy(go);
            _spawned.Clear();
        }

        /// <summary>A 1.8 m humanoid in T-pose facing +Z, with Unity's humanoid bone names.</summary>
        private static GameObject BuildSkeleton()
        {
            var root = new GameObject("TestHumanoid");
            var bones = new Dictionary<string, Transform>();
            Transform Bone(string name, Transform parent, Vector3 local)
            {
                var t = new GameObject(name).transform;
                t.SetParent(parent, false);
                t.localPosition = local;
                bones[name] = t;
                return t;
            }
            Transform hips = Bone("Hips", root.transform, new Vector3(0, 1.0f, 0));
            Transform spine = Bone("Spine", hips, new Vector3(0, 0.1f, 0));
            Transform chest = Bone("Chest", spine, new Vector3(0, 0.15f, 0));
            Transform neck = Bone("Neck", chest, new Vector3(0, 0.25f, 0));
            Bone("Head", neck, new Vector3(0, 0.1f, 0));
            foreach (int side in new[] { -1, 1 })
            {
                string s = side < 0 ? "Left" : "Right";
                Transform upperArm = Bone(s + "UpperArm", chest, new Vector3(0.18f * side, 0.2f, 0));
                Transform lowerArm = Bone(s + "LowerArm", upperArm, new Vector3(0.32f * side, 0, 0));
                Bone(s + "Hand", lowerArm, new Vector3(0.27f * side, 0, 0));
                Transform upperLeg = Bone(s + "UpperLeg", hips, new Vector3(0.1f * side, -0.05f, 0));
                Transform lowerLeg = Bone(s + "LowerLeg", upperLeg, new Vector3(0, -0.45f, 0));
                Transform foot = Bone(s + "Foot", lowerLeg, new Vector3(0, -0.42f, 0.02f));
                Bone(s + "Toes", foot, new Vector3(0, -0.05f, 0.12f));
            }

            var human = new List<HumanBone>();
            foreach (string name in bones.Keys)
                human.Add(new HumanBone { boneName = name, humanName = HumanName(name), limit = new HumanLimit { useDefaultValues = true } });
            var skeleton = new List<SkeletonBone> { new SkeletonBone { name = root.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one } };
            foreach (Transform t in bones.Values)
                skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            var description = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
            };
            Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            Assert.IsTrue(avatar.isValid && avatar.isHuman, "procedural humanoid avatar");

            Animator animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = Resources.Load<RuntimeAnimatorController>("Characters/RWBR_Humanoid");
            Assert.IsNotNull(animator.runtimeAnimatorController, "the committed controller is in Resources");
            root.AddComponent<CharacterAnimator>();
            return root;
        }

        // Unity's human names use spaces ("Left Upper Arm"); map from the bone names above.
        private static string HumanName(string bone)
        {
            foreach (string human in HumanTrait.BoneName)
                if (human.Replace(" ", "") == bone) return human;
            throw new System.ArgumentException(bone);
        }

        private sealed class Rig
        {
            public Transform Root;
            public CharacterBody Body;
            public WeaponHolder Weapon;
            public CharacterAnimator Driver;
            public Animator Animator;
        }

        private Rig Spawn()
        {
            GameObject template = BuildSkeleton();
            template.transform.position = new Vector3(0, -50, 0);
            _spawned.Add(template);
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane); // keeps its collider: feet IK raycasts hit it
            ground.transform.localScale = Vector3.one * 5;
            _spawned.Add(ground);

            var root = new GameObject("Character").transform;
            _spawned.Add(root.gameObject);
            var weapon = new WeaponHolder(root);
            CharacterBody body = CharacterBody.Create(root, Color.blue, template);
            body.Bind(weapon);
            CharacterAnimator driver = root.GetComponentInChildren<CharacterAnimator>();
            // The skeleton has no renderers, so it is never visible: switch off the mobile culling for the test.
            driver.GetComponent<Animator>().cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return new Rig { Root = root, Body = body, Weapon = weapon, Driver = driver, Animator = driver.GetComponent<Animator>() };
        }

        private static IEnumerator Frames(Rig rig, float seconds, CharacterMotion m, Vector3 move = default)
        {
            float until = Time.time + seconds;
            while (Time.time < until)
            {
                rig.Root.position += move * Time.deltaTime;
                rig.Root.rotation = Quaternion.Euler(0, m.ViewYawDegrees, 0);
                rig.Weapon.Update(new WeaponPose { Weapon = m.Weapon.HasValue ? WeaponCatalog.Rifle : -1, Alive = m.Alive, Reload = -1, PitchDegrees = m.PitchDegrees }, 1.6f, Time.time, Time.deltaTime);
                rig.Body.Pose(m, 1.8f, m.ViewYawDegrees, Time.deltaTime);
                yield return null;
            }
        }

        private static CharacterMotion Standing => new CharacterMotion { Alive = true, Grounded = true, Weapon = WeaponClass.AssaultRifle };

        [UnityTest]
        public IEnumerator Rigged_body_is_used_and_hands_reach_the_weapon_grips()
        {
            Rig rig = Spawn();
            Assert.IsTrue(rig.Body.Rigged);
            Transform leftHand = rig.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform rightHand = rig.Animator.GetBoneTransform(HumanBodyBones.RightHand);

            yield return Frames(rig, 0.6f, Standing);
            WeaponModel w = rig.Weapon.Current;
            Assert.IsNotNull(w);
            Assert.AreEqual(1f, rig.Driver.HandWeight, 1e-3f);
            float left = Vector3.Distance(leftHand.position, w.LeftHandGrip.position);
            float right = Vector3.Distance(rightHand.position, w.RightHandGrip.position);
            Debug.Log($"RWBR rig test: left hand {left:0.000} m, right hand {right:0.000} m from the grips");
            Assert.Less(right, 0.08f, "trigger hand on the grip");
            Assert.Less(left, 0.15f, "support hand on the handguard (as far as the arm reaches)");
            Assert.AreEqual(2, rig.Driver.FeetPlanted, "standing: both feet planted by IK");
            float leftFoot = rig.Animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
            float rightFoot = rig.Animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y;
            Assert.AreEqual(rig.Animator.leftFeetBottomHeight, leftFoot, 0.05f, "left sole on the ground");
            Assert.AreEqual(rig.Animator.rightFeetBottomHeight, rightFoot, 0.05f, "right sole on the ground");

            // Reloading takes the hands off the weapon (the reload clip animates them).
            CharacterMotion reloading = Standing;
            reloading.Reloading = true;
            yield return Frames(rig, 0.4f, reloading);
            Assert.AreEqual(0f, rig.Driver.HandWeight, 1e-3f);
            Assert.IsTrue(rig.Animator.GetBool(AnimatorIds.Reloading));
        }

        [UnityTest]
        public IEnumerator Movement_crouch_and_death_reach_the_animator()
        {
            Rig rig = Spawn();
            yield return Frames(rig, 0.8f, Standing, move: new Vector3(0, 0, 5));
            Assert.Greater(rig.Animator.GetFloat(AnimatorIds.Forward), 3.5f, "running forward");
            Assert.AreEqual(0f, rig.Animator.GetFloat(AnimatorIds.Strafe), 0.3f);
            Assert.AreEqual(0, rig.Driver.FeetPlanted, "running clips place their own feet");

            yield return Frames(rig, 0.8f, Standing, move: new Vector3(5, 0, 0));
            Assert.Greater(rig.Animator.GetFloat(AnimatorIds.Strafe), 3.5f, "strafing right");

            CharacterMotion crouched = Standing;
            crouched.Crouched = true;
            yield return Frames(rig, 0.2f, crouched);
            Assert.IsTrue(rig.Animator.GetBool(AnimatorIds.Crouch));
            Assert.IsTrue(rig.Animator.GetCurrentAnimatorStateInfo(0).IsName("Crouch") || rig.Animator.IsInTransition(0));

            CharacterMotion dead = Standing;
            dead.Alive = false;
            yield return Frames(rig, 0.3f, dead);
            Assert.IsTrue(rig.Animator.GetBool(AnimatorIds.Dead));
            Assert.IsTrue(rig.Animator.GetCurrentAnimatorStateInfo(0).IsName("Death"));
        }

        [UnityTest]
        public IEnumerator Body_turns_in_place_after_a_big_look()
        {
            Rig rig = Spawn();
            yield return Frames(rig, 0.2f, Standing);
            Transform model = rig.Driver.transform;

            CharacterMotion small = Standing;
            small.ViewYawDegrees = 40;
            yield return Frames(rig, 0.2f, small);
            Assert.AreEqual(-40f, Mathf.DeltaAngle(0, model.localEulerAngles.y), 2f, "feet stay put for a small look");

            CharacterMotion big = Standing;
            big.ViewYawDegrees = 100;
            yield return Frames(rig, 0.6f, big);
            Assert.AreEqual(0f, Mathf.DeltaAngle(0, model.localEulerAngles.y), 2f, "the body has turned to the view");
            Assert.AreEqual(100f, rig.Driver.Turn.LastTurnAngle, 1f);
        }

        [UnityTest]
        public IEnumerator Without_the_built_prefab_characters_are_capsules()
        {
            yield return null;
            if (CharacterBody.RiggedPrefab != null) Assert.Ignore("the rigged prefab is built on this machine");
            var root = new GameObject("Capsule").transform;
            _spawned.Add(root.gameObject);
            CharacterBody body = CharacterBody.Create(root, Color.red);
            Assert.IsFalse(body.Rigged);
            Assert.IsNotNull(root.Find("Body"));
        }
    }
}
