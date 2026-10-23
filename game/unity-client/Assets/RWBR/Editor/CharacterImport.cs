using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RWBR.Client.Character;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace RWBR.EditorTools
{
    /// <summary>The animation states the character controller needs, each filled from one Mixamo file.</summary>
    public enum AnimSlot
    {
        Idle,
        WalkForward,
        WalkBack,
        WalkLeft,
        WalkRight,
        RunForward,
        RunBack,
        RunLeft,
        RunRight,
        Sprint,
        CrouchIdle,
        CrouchWalk,
        JumpStart,
        Fall,
        Land,
        TurnLeft,
        TurnRight,
        AimIdle,
        Reload,
        Hit,
        Death,
    }

    /// <summary>
    /// Matches Mixamo file names to animation slots. Mixamo names every clip "mixamo.com", so the file name is what
    /// identifies it ("Rifle Run Forward.fbx", "walking backwards.fbx", "Pistol Idle.fbx"). Files with "pistol" in the
    /// name fill the pistol set; everything else fills the rifle set.
    /// </summary>
    public static class AnimSlotMatcher
    {
        private sealed class Rule
        {
            public AnimSlot Slot;
            public string[][] Patterns; // any pattern; every word of a pattern must appear (as a word prefix)
            public string[] Exclude = Array.Empty<string>();
            public bool Required;
        }

        private static readonly string[] Directional = { "forward", "back", "left", "right", "strafe" };

        private static readonly Rule[] Rules =
        {
            new Rule { Slot = AnimSlot.Idle, Patterns = new[] { new[] { "idle" } }, Exclude = new[] { "crouch", "aim", "turn", "reload" }, Required = true },
            new Rule { Slot = AnimSlot.WalkForward, Patterns = new[] { new[] { "walk", "forward" }, new[] { "walking" }, new[] { "walk" } }, Exclude = new[] { "back", "left", "right", "crouch", "strafe" }, Required = true },
            new Rule { Slot = AnimSlot.WalkBack, Patterns = new[] { new[] { "walk", "back" } }, Exclude = new[] { "crouch", "left", "right" } },
            new Rule { Slot = AnimSlot.WalkLeft, Patterns = new[] { new[] { "walk", "left" }, new[] { "strafe", "walk", "left" } }, Exclude = new[] { "crouch", "forward", "back" } },
            new Rule { Slot = AnimSlot.WalkRight, Patterns = new[] { new[] { "walk", "right" }, new[] { "strafe", "walk", "right" } }, Exclude = new[] { "crouch", "forward", "back" } },
            new Rule { Slot = AnimSlot.RunForward, Patterns = new[] { new[] { "run", "forward" }, new[] { "running" }, new[] { "run" } }, Exclude = new[] { "back", "left", "right", "strafe" }, Required = true },
            new Rule { Slot = AnimSlot.RunBack, Patterns = new[] { new[] { "run", "back" } }, Exclude = new[] { "left", "right" } },
            new Rule { Slot = AnimSlot.RunLeft, Patterns = new[] { new[] { "run", "left" }, new[] { "strafe", "left" } }, Exclude = new[] { "forward", "back", "walk" } },
            new Rule { Slot = AnimSlot.RunRight, Patterns = new[] { new[] { "run", "right" }, new[] { "strafe", "right" } }, Exclude = new[] { "forward", "back", "walk" } },
            new Rule { Slot = AnimSlot.Sprint, Patterns = new[] { new[] { "sprint" } }, Exclude = new[] { "back", "left", "right" } },
            new Rule { Slot = AnimSlot.CrouchIdle, Patterns = new[] { new[] { "crouch", "idle" }, new[] { "crouching", "idle" } }, Required = true },
            new Rule { Slot = AnimSlot.CrouchWalk, Patterns = new[] { new[] { "crouch", "walk" }, new[] { "crouched", "walk" } }, Exclude = new[] { "back", "left", "right" }, Required = true },
            new Rule { Slot = AnimSlot.JumpStart, Patterns = new[] { new[] { "jump", "up" }, new[] { "jump" } }, Exclude = new[] { "loop", "down", "land" }, Required = true },
            new Rule { Slot = AnimSlot.Fall, Patterns = new[] { new[] { "jump", "loop" }, new[] { "fall" }, new[] { "falling" } } },
            new Rule { Slot = AnimSlot.Land, Patterns = new[] { new[] { "jump", "down" }, new[] { "land" }, new[] { "landing" } } },
            new Rule { Slot = AnimSlot.TurnLeft, Patterns = new[] { new[] { "turn", "left" } } },
            new Rule { Slot = AnimSlot.TurnRight, Patterns = new[] { new[] { "turn", "right" } } },
            new Rule { Slot = AnimSlot.AimIdle, Patterns = new[] { new[] { "aim", "idle" }, new[] { "aiming", "idle" }, new[] { "aiming" }, new[] { "aim" } }, Exclude = Directional, Required = true },
            new Rule { Slot = AnimSlot.Reload, Patterns = new[] { new[] { "reload" }, new[] { "reloading" } }, Required = true },
            new Rule { Slot = AnimSlot.Hit, Patterns = new[] { new[] { "hit", "reaction" }, new[] { "hit" } }, Required = true },
            new Rule { Slot = AnimSlot.Death, Patterns = new[] { new[] { "death" }, new[] { "dying" }, new[] { "die" } }, Required = true },
        };

        public static bool IsRequired(AnimSlot slot) => Rules.First(r => r.Slot == slot).Required;

        public static string[] Words(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            return sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool Has(string[] words, string keyword) => words.Any(w => w.StartsWith(keyword, StringComparison.Ordinal));

        /// <summary>Best file for each slot: the first pattern that matches, then the fewest extra words.</summary>
        public static Dictionary<AnimSlot, T> Match<T>(IEnumerable<T> items, Func<T, string> fileName, bool pistol)
        {
            var candidates = items.Select(i => (Item: i, Words: Words(fileName(i))))
                .Where(c => Has(c.Words, "pistol") == pistol)
                .ToList();
            var result = new Dictionary<AnimSlot, T>();
            foreach (Rule rule in Rules)
            {
                for (int p = 0; p < rule.Patterns.Length && !result.ContainsKey(rule.Slot); p++)
                {
                    string[] pattern = rule.Patterns[p];
                    var hit = candidates
                        .Where(c => pattern.All(k => Has(c.Words, k)) && !rule.Exclude.Any(x => Has(c.Words, x)))
                        .OrderBy(c => c.Words.Length)
                        .ThenBy(c => fileName(c.Item), StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (hit.Words != null) result[rule.Slot] = hit.Item;
                }
            }
            return result;
        }

        public static IEnumerable<AnimSlot> MissingRequired<T>(Dictionary<AnimSlot, T> matched) =>
            Rules.Where(r => r.Required && !matched.ContainsKey(r.Slot)).Select(r => r.Slot);
    }

    /// <summary>
    /// Import settings for everything under Assets/ThirdParty/Mixamo: humanoid rigs, in-place clips (root motion baked
    /// into the pose, since the simulation moves characters), looping for locomotion, no materials on animation files,
    /// compressed meshes and animation, and ASTC textures capped at 1024 px on Android and iOS.
    /// </summary>
    public sealed class MixamoPostprocessor : AssetPostprocessor
    {
        private bool InMixamo => assetPath.StartsWith(CharacterImport.MixamoFolder + "/", StringComparison.OrdinalIgnoreCase);
        private bool IsCharacter => assetPath.StartsWith(CharacterImport.CharactersFolder + "/", StringComparison.OrdinalIgnoreCase);

        private void OnPreprocessModel()
        {
            if (!InMixamo || !(assetImporter is ModelImporter mi)) return;
            mi.animationType = ModelImporterAnimationType.Human;
            mi.isReadable = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.meshCompression = ModelImporterMeshCompression.Medium;
            mi.animationCompression = ModelImporterAnimationCompression.Optimal;
            if (IsCharacter)
            {
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                mi.importAnimation = false;
                mi.optimizeGameObjects = false; // foot IK reads the animated foot bones
            }
            else
            {
                mi.importAnimation = true;
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
                Avatar avatar = CharacterImport.CharacterAvatar();
                if (avatar != null)
                {
                    mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                    mi.sourceAvatar = avatar;
                }
                else
                {
                    mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                }
            }
        }

        private void OnPreprocessAnimation()
        {
            if (!InMixamo || IsCharacter || !(assetImporter is ModelImporter mi)) return;
            string file = Path.GetFileNameWithoutExtension(assetPath);
            string[] words = AnimSlotMatcher.Words(file);
            bool once = words.Any(w => w.StartsWith("jump") || w.StartsWith("land") || w.StartsWith("death") || w.StartsWith("dying") ||
                                       w.StartsWith("hit") || w.StartsWith("reload") || w.StartsWith("turn") || w.StartsWith("die"));
            bool airborne = words.Any(w => w.StartsWith("jump") || w.StartsWith("fall"));
            ModelImporterClipAnimation[] clips = mi.defaultClipAnimations;
            foreach (ModelImporterClipAnimation clip in clips)
            {
                clip.name = file;
                clip.loopTime = !once;
                clip.lockRootRotation = true;
                clip.keepOriginalOrientation = true;
                clip.lockRootHeightY = !airborne;
                clip.keepOriginalPositionY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalPositionXZ = true;
            }
            mi.clipAnimations = clips;
        }

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/ThirdParty/", StringComparison.OrdinalIgnoreCase) || !(assetImporter is TextureImporter ti)) return;
            ti.mipmapEnabled = true;
            foreach (string platform in new[] { "Android", "iPhone" })
            {
                TextureImporterPlatformSettings s = ti.GetPlatformTextureSettings(platform);
                s.overridden = true;
                s.maxTextureSize = Math.Min(1024, s.maxTextureSize > 0 ? s.maxTextureSize : 1024);
                s.format = TextureImporterFormat.ASTC_6x6;
                ti.SetPlatformTextureSettings(s);
            }
        }
    }

    public sealed class CharacterReport
    {
        public string CharacterPath;
        public float MeasuredHeight;
        public Dictionary<AnimSlot, AnimationClip> Rifle = new Dictionary<AnimSlot, AnimationClip>();
        public Dictionary<AnimSlot, AnimationClip> Pistol = new Dictionary<AnimSlot, AnimationClip>();
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public bool PrefabWritten;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder("RWBR characters: ");
            sb.Append(CharacterPath != null ? $"character {CharacterPath} ({MeasuredHeight:0.00} m)" : "no character model");
            sb.Append($", rifle clips {Rifle.Count}, pistol clips {Pistol.Count}, prefab {(PrefabWritten ? "written" : "not written")}");
            foreach (string w in Warnings) sb.Append("\n  warning: ").Append(w);
            foreach (string e in Errors) sb.Append("\n  error: ").Append(e);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Builds the humanoid animator controller and the rigged character prefab from the Mixamo files.
    /// Put the character (downloaded With Skin) in Assets/ThirdParty/Mixamo/Characters and the animations (Without
    /// Skin, In Place) in Assets/ThirdParty/Mixamo/Animations, then run RWBR > Characters > Build (or
    /// ./unity-batch.sh characters). Without a character the controller is still written and the game keeps the
    /// placeholder capsules.
    /// </summary>
    public static class CharacterImport
    {
        public const string MixamoFolder = "Assets/ThirdParty/Mixamo";
        public const string CharactersFolder = MixamoFolder + "/Characters";
        public const string AnimationsFolder = MixamoFolder + "/Animations";
        public const string OutputFolder = "Assets/RWBR/Resources/Characters";
        public const string ControllerPath = OutputFolder + "/RWBR_Humanoid.controller";
        public const string PrefabPath = OutputFolder + "/Soldier.prefab";
        public const float TargetHeight = 1.8f;

        // Blend tree positions are game speeds (m/s): walking while aiming, running, sprinting (GameRules).
        public const float WalkPosition = 3.5f;
        public const float RunPosition = 5f;
        public const float SprintPosition = 7f;
        public const float CrouchPosition = 2.5f;

        public static string CharacterModelPath() =>
            AssetDatabase.IsValidFolder(CharactersFolder)
                ? AssetDatabase.FindAssets("t:Model", new[] { CharactersFolder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
                : null;

        public static Avatar CharacterAvatar()
        {
            string path = CharacterModelPath();
            return path == null ? null : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault(a => a.isHuman);
        }

        [MenuItem("RWBR/Characters/Build")]
        public static void BuildMenu() => Debug.Log(Build());

        /// <summary>Batch entry (unity-batch.sh characters). Exits 1 when a character is present but required clips are missing.</summary>
        public static void BuildFromCommandLine()
        {
            CharacterReport report = Build();
            Debug.Log(report);
            EditorApplication.Exit(report.Errors.Count > 0 ? 1 : 0);
        }

        public static CharacterReport Build(string controllerPath = ControllerPath, string prefabPath = PrefabPath)
        {
            var report = new CharacterReport { CharacterPath = CharacterModelPath() };
            EnsureFolder(Path.GetDirectoryName(controllerPath)?.Replace('\\', '/'));

            // Animations take their avatar from the character: re-import them once the character exists.
            Avatar avatar = CharacterAvatar();
            var clipFiles = new List<string>();
            if (AssetDatabase.IsValidFolder(AnimationsFolder))
            {
                clipFiles = AssetDatabase.FindAssets("t:Model", new[] { AnimationsFolder }).Select(AssetDatabase.GUIDToAssetPath).ToList();
                foreach (string path in clipFiles)
                {
                    if (avatar != null && AssetImporter.GetAtPath(path) is ModelImporter mi && mi.sourceAvatar != avatar)
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
            }
            var clips = clipFiles
                .Select(p => (Path: p, Clip: AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"))))
                .Where(c => c.Clip != null)
                .ToList();
            report.Rifle = AnimSlotMatcher.Match(clips, c => c.Path, pistol: false).ToDictionary(kv => kv.Key, kv => kv.Value.Clip);
            report.Pistol = AnimSlotMatcher.Match(clips, c => c.Path, pistol: true).ToDictionary(kv => kv.Key, kv => kv.Value.Clip);

            if (report.CharacterPath != null)
            {
                if (avatar == null) report.Errors.Add($"{report.CharacterPath} did not import as a humanoid (check the rig)");
                foreach (AnimSlot missing in AnimSlotMatcher.MissingRequired(report.Rifle))
                    report.Errors.Add($"no rifle clip for {missing} (see docs/PHASE-A-CHARACTER-AUDIT.md for the download list)");
                if (report.Pistol.Count == 0) report.Warnings.Add("no pistol clips: pistols use the rifle poses");
            }

            AnimatorController controller = BuildController(controllerPath, report.Rifle, report.Pistol);

            if (report.CharacterPath != null && avatar != null)
            {
                report.PrefabWritten = WritePrefab(report, controller, prefabPath);
            }
            else if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
            {
                AssetDatabase.DeleteAsset(prefabPath); // no character any more: back to the capsules
            }
            AssetDatabase.SaveAssets();
            CharacterBody.ResetPrefabCache();
            return report;
        }

        private static bool WritePrefab(CharacterReport report, AnimatorController controller, string prefabPath)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(report.CharacterPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                instance.name = "Soldier";
                Animator animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                if (instance.GetComponent<CharacterAnimator>() == null) instance.AddComponent<CharacterAnimator>();

                // Mixamo exports vary in scale: make the character the simulation's 1.8 m.
                Bounds? bounds = null;
                foreach (Renderer r in instance.GetComponentsInChildren<Renderer>())
                {
                    if (bounds == null) bounds = r.bounds;
                    else
                    {
                        Bounds b = bounds.Value;
                        b.Encapsulate(r.bounds);
                        bounds = b;
                    }
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    if (r is SkinnedMeshRenderer skinned)
                    {
                        skinned.quality = SkinQuality.Bone2; // mobile: two bones per vertex
                        skinned.updateWhenOffscreen = false;
                    }
                }
                report.MeasuredHeight = bounds?.size.y ?? 0f;
                if (report.MeasuredHeight > 0.1f && Mathf.Abs(report.MeasuredHeight - TargetHeight) > 0.1f)
                {
                    float scale = TargetHeight / report.MeasuredHeight;
                    instance.transform.localScale = Vector3.one * scale;
                    report.Warnings.Add($"character was {report.MeasuredHeight:0.00} m tall; scaled by {scale:0.###} to {TargetHeight} m");
                }
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Writes the controller: a locomotion base layer (rifle and pistol 2D blend trees, crouch, jump, fall, land,
        /// turn in place, death) with the IK pass on, an upper-body layer for aiming and reloading, and an upper-body
        /// reactions layer for hits. Missing clips leave empty motions (and mirrored strafes where one side exists).
        /// </summary>
        public static AnimatorController BuildController(string path, IReadOnlyDictionary<AnimSlot, AnimationClip> rifle, IReadOnlyDictionary<AnimSlot, AnimationClip> pistol)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null) AssetDatabase.DeleteAsset(path);
            string maskPath = Path.ChangeExtension(path, null) + "_UpperBody.mask";
            if (AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath) != null) AssetDatabase.DeleteAsset(maskPath);

            // Fallback pose for slots without a clip, so a character never shows its bind (T) pose and the humanoid
            // pass (and IK) always runs.
            AnimationClip neutral = NeutralClip(Path.ChangeExtension(path, null) + "_Neutral.anim");

            AnimatorController c = AnimatorController.CreateAnimatorControllerAtPath(path);
            foreach (string f in new[] { AnimatorIds.ForwardName, AnimatorIds.StrafeName, AnimatorIds.SpeedName, AnimatorIds.AimName, AnimatorIds.PitchName, AnimatorIds.PistolName, AnimatorIds.TurnAngleName, AnimatorIds.ReloadProgressName })
                c.AddParameter(f, AnimatorControllerParameterType.Float);
            foreach (string b in new[] { AnimatorIds.CrouchName, AnimatorIds.GroundedName, AnimatorIds.ArmedName, AnimatorIds.ReloadingName, AnimatorIds.DeadName })
                c.AddParameter(b, AnimatorControllerParameterType.Bool);
            foreach (string t in new[] { AnimatorIds.JumpName, AnimatorIds.LandName, AnimatorIds.HitName, AnimatorIds.FireName, AnimatorIds.SwitchName, AnimatorIds.TurnName })
                c.AddParameter(t, AnimatorControllerParameterType.Trigger);
            AnimatorControllerParameter[] ps = c.parameters;
            foreach (AnimatorControllerParameter p in ps)
                if (p.name == AnimatorIds.GroundedName) p.defaultBool = true;
            c.parameters = ps;

            AnimationClip Pick(AnimSlot slot, bool pistolSet)
            {
                IReadOnlyDictionary<AnimSlot, AnimationClip> set = pistolSet ? pistol : rifle;
                if (set.TryGetValue(slot, out AnimationClip clip)) return clip;
                if (pistolSet && rifle.TryGetValue(slot, out clip)) return clip; // pistol falls back to rifle
                return slot == AnimSlot.Idle || slot == AnimSlot.CrouchIdle || slot == AnimSlot.Death ? neutral : null;
            }

            // ---- Base layer: locomotion ----
            AnimatorControllerLayer[] layers = c.layers;
            layers[0].name = "Locomotion";
            layers[0].iKPass = true;
            c.layers = layers;
            AnimatorStateMachine sm = c.layers[0].stateMachine;

            AnimatorState move = c.CreateBlendTreeInController("Move", out BlendTree moveTree, 0);
            moveTree.blendType = BlendTreeType.Simple1D;
            moveTree.blendParameter = AnimatorIds.PistolName;
            moveTree.useAutomaticThresholds = false;
            FillLocomotion(moveTree.CreateBlendTreeChild(0f), s => Pick(s, false));
            FillLocomotion(moveTree.CreateBlendTreeChild(1f), s => Pick(s, true));
            sm.defaultState = move;

            AnimatorState crouch = c.CreateBlendTreeInController("Crouch", out BlendTree crouchTree, 0);
            crouchTree.blendType = BlendTreeType.SimpleDirectional2D;
            crouchTree.blendParameter = AnimatorIds.StrafeName;
            crouchTree.blendParameterY = AnimatorIds.ForwardName;
            Add2D(crouchTree, Pick(AnimSlot.CrouchIdle, false), Vector2.zero);
            Add2D(crouchTree, Pick(AnimSlot.CrouchWalk, false), new Vector2(0, CrouchPosition));

            AnimatorState jump = State(sm, "Jump", Pick(AnimSlot.JumpStart, false));
            AnimatorState fall = State(sm, "Fall", Pick(AnimSlot.Fall, false) ?? Pick(AnimSlot.JumpStart, false));
            AnimationClip landClip = Pick(AnimSlot.Land, false);
            AnimatorState land = landClip != null ? State(sm, "Land", landClip) : null;
            AnimatorState death = State(sm, "Death", Pick(AnimSlot.Death, false));
            move.writeDefaultValues = crouch.writeDefaultValues = false;

            Link(move, crouch, 0.15f, (AnimatorConditionMode.If, 0, AnimatorIds.CrouchName));
            Link(crouch, move, 0.15f, (AnimatorConditionMode.IfNot, 0, AnimatorIds.CrouchName));

            // Turn in place only when there is a turn clip (an empty state would flash the bind pose).
            AnimationClip turnLeft = Pick(AnimSlot.TurnLeft, false), turnRight = Pick(AnimSlot.TurnRight, false);
            if (turnLeft != null || turnRight != null)
            {
                AnimatorState turn = c.CreateBlendTreeInController("Turn", out BlendTree turnTree, 0);
                turnTree.blendType = BlendTreeType.Simple1D;
                turnTree.blendParameter = AnimatorIds.TurnAngleName;
                turnTree.useAutomaticThresholds = false;
                AddChild1D(turnTree, turnLeft ?? turnRight, -90f, mirror: turnLeft == null);
                AddChild1D(turnTree, turnRight ?? turnLeft, 90f, mirror: turnRight == null);
                turn.writeDefaultValues = false;
                Link(move, turn, 0.1f, (AnimatorConditionMode.If, 0, AnimatorIds.TurnName));
                Exit(turn, move, 0.85f, 0.15f);
            }
            Link(move, fall, 0.2f, (AnimatorConditionMode.IfNot, 0, AnimatorIds.GroundedName));
            Link(crouch, fall, 0.2f, (AnimatorConditionMode.IfNot, 0, AnimatorIds.GroundedName));
            AnimatorStateTransition toJump = sm.AddAnyStateTransition(jump);
            toJump.AddCondition(AnimatorConditionMode.If, 0, AnimatorIds.JumpName);
            toJump.AddCondition(AnimatorConditionMode.IfNot, 0, AnimatorIds.DeadName);
            toJump.duration = 0.1f;
            toJump.canTransitionToSelf = false;
            Exit(jump, fall, 0.85f, 0.1f);
            AnimatorState afterAir = land ?? move;
            Link(jump, afterAir, 0.1f, (AnimatorConditionMode.If, 0, AnimatorIds.GroundedName));
            Link(fall, afterAir, 0.1f, (AnimatorConditionMode.If, 0, AnimatorIds.GroundedName));
            if (land != null) Exit(land, move, 0.6f, 0.2f);
            AnimatorStateTransition toDeath = sm.AddAnyStateTransition(death);
            toDeath.AddCondition(AnimatorConditionMode.If, 0, AnimatorIds.DeadName);
            toDeath.duration = 0.1f;
            toDeath.canTransitionToSelf = false;
            Link(death, move, 0.2f, (AnimatorConditionMode.IfNot, 0, AnimatorIds.DeadName)); // respawn (range targets)

            // ---- Upper body: aim and reload ----
            AvatarMask mask = UpperBodyMask();
            AssetDatabase.CreateAsset(mask, maskPath);
            AnimatorStateMachine upper = AddLayer(c, "UpperBody", mask);
            AnimatorState empty = State(upper, "Empty", null);
            AnimatorState aim = State(upper, "Aim", Pick(AnimSlot.AimIdle, false));
            AnimatorState reload = State(upper, "Reload", Pick(AnimSlot.Reload, false));
            // The clip follows the reload's progress, so it ends exactly when the magazine is in, whatever the weapon.
            reload.timeParameterActive = true;
            reload.timeParameter = AnimatorIds.ReloadProgressName;
            upper.defaultState = empty;
            Link(empty, aim, 0.12f, (AnimatorConditionMode.Greater, 0.5f, AnimatorIds.AimName), (AnimatorConditionMode.If, 0, AnimatorIds.ArmedName));
            Link(aim, empty, 0.12f, (AnimatorConditionMode.Less, 0.5f, AnimatorIds.AimName));
            Link(empty, reload, 0.1f, (AnimatorConditionMode.If, 0, AnimatorIds.ReloadingName));
            Link(aim, reload, 0.1f, (AnimatorConditionMode.If, 0, AnimatorIds.ReloadingName));
            Link(reload, empty, 0.15f, (AnimatorConditionMode.IfNot, 0, AnimatorIds.ReloadingName));

            // ---- Reactions: hit flinch on the upper body ----
            AnimatorStateMachine reactions = AddLayer(c, "Reactions", mask);
            AnimatorState calm = State(reactions, "Empty", null);
            AnimatorState hit = State(reactions, "Hit", Pick(AnimSlot.Hit, false));
            reactions.defaultState = calm;
            AnimatorStateTransition toHit = reactions.AddAnyStateTransition(hit);
            toHit.AddCondition(AnimatorConditionMode.If, 0, AnimatorIds.HitName);
            toHit.duration = 0.05f;
            toHit.canTransitionToSelf = true;
            Exit(hit, calm, 0.7f, 0.2f);

            EditorUtility.SetDirty(c);
            return c;
        }

        /// <summary>A looping humanoid clip holding every body muscle at 0: a relaxed standing pose.</summary>
        public static AnimationClip NeutralClip(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null) return existing;
            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path) };
            string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
            foreach (string muscle in HumanTrait.MuscleName)
            {
                if (fingers.Any(muscle.Contains)) continue; // finger curves use different binding names; not needed
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), AnimationCurve.Constant(0, 1, 0));
            }
            // Body centre at the avatar's normal standing height (humanoid clips store it scaled by the avatar), upright.
            foreach ((string property, float value) in new[] { ("RootT.x", 0f), ("RootT.y", 1f), ("RootT.z", 0f), ("RootQ.x", 0f), ("RootQ.y", 0f), ("RootQ.z", 0f), ("RootQ.w", 1f) })
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), property), AnimationCurve.Constant(0, 1, value));
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        private static void FillLocomotion(BlendTree tree, Func<AnimSlot, AnimationClip> pick)
        {
            tree.name = "Locomotion";
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = AnimatorIds.StrafeName;
            tree.blendParameterY = AnimatorIds.ForwardName;
            Add2D(tree, pick(AnimSlot.Idle), Vector2.zero);
            AddDirectional(tree, pick, AnimSlot.WalkForward, AnimSlot.WalkBack, AnimSlot.WalkLeft, AnimSlot.WalkRight, WalkPosition);
            AddDirectional(tree, pick, AnimSlot.RunForward, AnimSlot.RunBack, AnimSlot.RunLeft, AnimSlot.RunRight, RunPosition);
            AnimationClip sprint = pick(AnimSlot.Sprint);
            if (sprint != null) Add2D(tree, sprint, new Vector2(0, SprintPosition));
            else if (pick(AnimSlot.RunForward) != null) Add2D(tree, pick(AnimSlot.RunForward), new Vector2(0, SprintPosition), timeScale: 1.3f);
        }

        private static void AddDirectional(BlendTree tree, Func<AnimSlot, AnimationClip> pick, AnimSlot fwd, AnimSlot back, AnimSlot left, AnimSlot right, float speed)
        {
            AnimationClip f = pick(fwd), b = pick(back), l = pick(left), r = pick(right);
            if (f != null) Add2D(tree, f, new Vector2(0, speed));
            if (b != null) Add2D(tree, b, new Vector2(0, -speed));
            // Humanoid clips can be mirrored, so one strafe direction covers both.
            if (l != null) Add2D(tree, l, new Vector2(-speed, 0));
            else if (r != null) Add2D(tree, r, new Vector2(-speed, 0), mirror: true);
            if (r != null) Add2D(tree, r, new Vector2(speed, 0));
            else if (l != null) Add2D(tree, l, new Vector2(speed, 0), mirror: true);
        }

        private static void Add2D(BlendTree tree, Motion motion, Vector2 position, bool mirror = false, float timeScale = 1f)
        {
            if (motion == null) return;
            tree.AddChild(motion, position);
            if (!mirror && Mathf.Approximately(timeScale, 1f)) return;
            ChildMotion[] children = tree.children;
            children[children.Length - 1].mirror = mirror;
            children[children.Length - 1].timeScale = timeScale;
            tree.children = children;
        }

        private static void AddChild1D(BlendTree tree, Motion motion, float threshold, bool mirror)
        {
            if (motion == null) return;
            tree.AddChild(motion, threshold);
            if (!mirror) return;
            ChildMotion[] children = tree.children;
            children[children.Length - 1].mirror = true;
            tree.children = children;
        }

        private static AnimatorState State(AnimatorStateMachine sm, string name, Motion motion)
        {
            AnimatorState s = sm.AddState(name);
            s.motion = motion;
            s.writeDefaultValues = false;
            return s;
        }

        private static void Link(AnimatorState from, AnimatorState to, float duration, params (AnimatorConditionMode Mode, float Threshold, string Parameter)[] conditions)
        {
            AnimatorStateTransition t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = duration;
            foreach (var (mode, threshold, parameter) in conditions) t.AddCondition(mode, threshold, parameter);
        }

        private static void Exit(AnimatorState from, AnimatorState to, float exitTime, float duration)
        {
            AnimatorStateTransition t = from.AddTransition(to);
            t.hasExitTime = true;
            t.exitTime = exitTime;
            t.duration = duration;
        }

        private static AnimatorStateMachine AddLayer(AnimatorController c, string name, AvatarMask mask)
        {
            c.AddLayer(name);
            AnimatorControllerLayer[] layers = c.layers;
            AnimatorControllerLayer layer = layers[layers.Length - 1];
            layer.avatarMask = mask;
            layer.defaultWeight = 1f;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.iKPass = true;
            c.layers = layers;
            return layer.stateMachine;
        }

        private static AvatarMask UpperBodyMask()
        {
            var mask = new AvatarMask { name = "UpperBody" };
            for (AvatarMaskBodyPart part = 0; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                bool upper = part == AvatarMaskBodyPart.Body || part == AvatarMaskBodyPart.Head || part == AvatarMaskBodyPart.LeftArm ||
                             part == AvatarMaskBodyPart.RightArm || part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers ||
                             part == AvatarMaskBodyPart.LeftHandIK || part == AvatarMaskBodyPart.RightHandIK;
                mask.SetHumanoidBodyPartActive(part, upper);
            }
            return mask;
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
