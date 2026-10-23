# Phase A: character, animation and combat audit

Date: 22 October 2026. This is the audit asked for before any of the AAA character and combat work starts. Nothing in
this document has been built yet; it records what the game is today, and the plan for Phase B.

## 1. What the game runs on

| Area                     | What is there today                                                                                                                                                                                                                                                                                                         |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Engine                   | Unity 6000.6.7f1, built-in render pipeline (no URP or HDRP)                                                                                                                                                                                                                                                                 |
| Input                    | Input System 1.20.1. `DeviceInputSource` (keyboard, mouse, gamepad) and `TouchControls` (on-screen stick and buttons)                                                                                                                                                                                                       |
| Simulation               | `RWBR.Shared` (netstandard2.1), shared by the Unity client and the .NET 10 match server. Movement, weapons, damage and loot are server-authoritative; the client predicts and reconciles                                                                                                                                    |
| Movement rules           | `GameRules`: walk 5 m/s, sprint 7, crouch 2.5, jump 5.5 m/s, gravity 15, capsule 0.4 m × 1.8 m (1.2 m crouched)                                                                                                                                                                                                             |
| Weapons                  | `WeaponCatalog`: 6 weapons (P9 pistol, Kestrel SMG, Standard Rifle, Pump-12 shotgun, Marksman DMR, Longshot sniper). Each has damage, pellets, falloff, fire interval, magazine, reload and switch times, spread (moving, crouched, airborne) and recoil pitch/yaw. Values are data, overridable from the admin game config |
| Input sent to the server | `InputCommand`: move, yaw, pitch and an 8-bit `InputButtons` (fire, sprint, reload, jump, crouch, interact, heal, switch). **All 8 bits are used.**                                                                                                                                                                         |
| Camera                   | `ThirdPersonCamera`: over-the-shoulder, 3.6 m back, 0.6 m shoulder offset, sphere-cast collision. No aim zoom, no FOV change, no smoothing between states                                                                                                                                                                   |
| Characters               | A capsule primitive and a cube "visor" (`PracticeViews`, `OnlineViews`). No mesh, skeleton, `Animator` or animation clip anywhere                                                                                                                                                                                           |
| World                    | Code-generated meshes (`GreyboxWorld`), one vertex-colour shader                                                                                                                                                                                                                                                            |
| Effects                  | Line-renderer tracers only. No muzzle flash, impact or hit-reaction effects                                                                                                                                                                                                                                                 |
| HUD                      | IMGUI                                                                                                                                                                                                                                                                                                                       |
| Training                 | Practice mode (`Greybox` scene) with bots and a tutorial (`TutorialDirector`). No firing range or target dummies                                                                                                                                                                                                            |
| Art on disk              | 38 C# files, 4 assembly definitions, 3 scenes, 1 shader. **No models, textures, materials, prefabs or animations.**                                                                                                                                                                                                         |

## 2. Can the engine do it?

Yes, and no engine migration is needed. Unity 6 has everything the brief asks for: Mecanim humanoid retargeting,
blend trees, layers and avatar masks, foot and hand IK (`OnAnimatorIK`, and the Animation Rigging package), skinned-mesh
LODs, and IL2CPP builds for Android and iOS. The built-in pipeline is enough for mobile; URP would give better
lighting and performance on phones but is optional and can come later, separately.

The blocker is not code. It is **assets**: the project has no 3D characters or animations, and realistic ones cannot
be written by hand in code. Anything that looks like a real person needs a sculpted, rigged, textured model and
motion-captured or hand-keyed animation from a legal source.

## 3. Gap against the ten tasks

| #   | Task                                                                                      | Today                                              | What Phase B needs                                                                                                                         | Needs assets?                                        |
| --- | ----------------------------------------------------------------------------------------- | -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------- |
| 1   | Audit                                                                                     | Done (this document)                               | —                                                                                                                                          | —                                                    |
| 2   | Realistic 3D characters, outfits, helmet, backpack, customisation                         | Capsule                                            | Licensed humanoid model(s) with outfit, helmet and backpack parts; a loader that swaps parts; customisation saved per player               | **Yes**                                              |
| 3   | Animations (idle, walk, run, sprint, crouch, jump, land, aim, reload, switch, hit, death) | None                                               | A humanoid clip set covering those states, per weapon type where poses differ (rifle, pistol)                                              | **Yes**                                              |
| 4   | Blending, IK, smooth turning, grounding, weapon placement                                 | None                                               | Animator controller (locomotion blend tree, upper-body aim layer, additive hit layer), foot IK, left-hand IK on the weapon, turn-in-place  | Code, but needs task 2 and 3 to show                 |
| 5   | Camera: collision, aim, sensitivity, smooth transitions                                   | Collision only                                     | Aim (ADS) camera with FOV zoom, per-weapon zoom (sniper scope), smoothing, sensitivity separate for hip and aim, settings UI               | No                                                   |
| 6   | AR, pistol, shotgun, sniper with ammo, recoil, reload, switch, muzzle flash, hit effects  | Rules done server-side; visuals minimal            | Weapon models, view recoil (camera kick and recovery), muzzle flash, impact and blood-free hit marker effects, shell ejection, audio hooks | Weapon models: **yes**. Effects: can be made in code |
| 7   | Mobile controls                                                                           | Stick, fire, jump, crouch, reload, heal, bag, swap | Aim button, drag-to-look while firing, button layout editor, haptics                                                                       | No                                                   |
| 8   | Training mode                                                                             | Practice with bots                                 | Firing range: static and moving targets at marked distances, damage numbers, weapon rack to try every gun                                  | No                                                   |
| 9   | Android and iOS optimisation                                                              | Quality presets, FPS telemetry                     | Character LODs, GPU skinning, animator culling, texture compression (ASTC), draw-call budget, iOS build profile                            | Partly (LODs come with the models)                   |
| 10  | Keep the platform working                                                                 | —                                                  | No API or database change unless customisation is saved on the server                                                                      | —                                                    |

One protocol change is unavoidable: **aiming down sights**. It has to reach the server because it should tighten spread
for everyone, and `InputButtons` has no spare bit. Phase B widens it to 16 bits, bumps the protocol version, and updates
the match server and the shared tests together. Old clients will be told to update, as the version check already does.

## 4. Assets: what is needed and where it can legally come from

Needed for the acceptance test: one humanoid character, a rifle and a pistol model, and the twelve animation states
for rifle and pistol. For the full brief, add outfit, helmet and backpack variants, shotgun and sniper models, and
shotgun and sniper poses.

| Source                                                                          | Look                                                                               | Cost                                               | Licence notes                                                                  |
| ------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------- | -------------------------------------------------- | ------------------------------------------------------------------------------ |
| **Mixamo** (Adobe)                                                              | Realistic-ish humans, very large animation library including rifle and pistol sets | Free; needs an Adobe account, and **you** download | May be used in commercial games. Must not be redistributed as raw files        |
| **Unity Asset Store** packs (realistic soldiers, weapon packs, animation packs) | Realistic, varies by pack                                                          | Paid, typically USD 20–150 per pack                | Standard EULA: fine inside a shipped game; **raw files must not be published** |
| **Unity Starter Assets – Third Person**                                         | Stylised robot, with idle, walk, run, jump and land                                | Free                                               | Unity Companion Licence. Not realistic, no weapons or aim poses                |
| CC0 packs (Quaternius, Kenney)                                                  | Low-poly, stylised                                                                 | Free                                               | No restrictions, but not realistic                                             |
| Commissioned artist                                                             | Exactly what you want, original                                                    | Highest                                            | You own it under the contract                                                  |

**The GitHub repository is public.** Mixamo and Asset Store licences forbid publishing the raw model and animation files.
Before any licensed asset is added, one of these has to happen:

- make https://github.com/drniranjan777/gamehyd private (simplest), or
- keep licensed assets out of git (a git-ignored `Assets/ThirdParty` folder that each developer fills from the store), or
- use only CC0 or owned art in the public repository.

PUBG or any other game's models, animations, sounds or UI will not be used.

### Decision (22 October 2026)

- Source: **Mixamo**.
- Repository: **make gamehyd private** before any Mixamo file is committed.

### Mixamo download list

Sign in at https://www.mixamo.com and download each item as **FBX for Unity (.fbx)** at **30 fps**. Tick **In Place**
wherever Mixamo offers it. Save the character (With Skin) to `game/unity-client/Assets/ThirdParty/Mixamo/Characters/`
and every animation (Without Skin) to `game/unity-client/Assets/ThirdParty/Mixamo/Animations/`. Keep Mixamo's file
names (they pick the animation slot) and download packs unzipped. Then run `./unity-batch.sh characters` in
`game/unity-client`: it lists anything missing.

| What              | Mixamo search                                                                              | Skin          |
| ----------------- | ------------------------------------------------------------------------------------------ | ------------- |
| Character         | "SWAT" (helmet and tactical vest); optionally one or two more characters for variety       | **With Skin** |
| Rifle locomotion  | "Rifle 8-Way Locomotion Pack"                                                              | Without Skin  |
| Pistol locomotion | "Pistol Handgun Locomotion Pack"                                                           | Without Skin  |
| Rifle extras      | "Rifle Aiming Idle", "Reloading", "Rifle Jump", "Hit Reaction", "Rifle Death" (or "Dying") | Without Skin  |
| Pistol extras     | "Pistol Idle", "Pistol Reload" (or "Reloading" on the pistol pose), "Pistol Death"         | Without Skin  |
| Crouch            | "Crouch Walk" (rifle) and "Crouch Idle"                                                    | Without Skin  |

Mixamo has no weapon models. The rifle, pistol, shotgun and sniper will start as simple modelled shapes with the right
grip and muzzle points, built in the project and owned outright. They can be replaced later by a weapon pack.

## 5. Phase B plan

Built in this order, so every step is playable and testable on its own.

1. **Asset pipeline.** A `CharacterRig` prefab path, humanoid import settings, a check that fails the build if a
   character has no avatar or missing clips, and the `Assets/ThirdParty` rule above.
2. **Character view.** Replace the capsule with the licensed character in Practice and Online. Server collision stays
   the same capsule, so gameplay and anti-cheat do not change.
3. **Animator.** Locomotion blend tree driven by the predicted velocity (local players) and the interpolated snapshot
   velocity (remote players); crouch, jump, fall and land states; upper-body aim layer driven by pitch; reload, switch,
   hit and death states triggered by the existing simulation events.
4. **IK and turning.** Foot IK on slopes and stairs, left-hand IK on the weapon grip, turn-in-place when the camera turns
   past 60° while standing.
5. **Aim and camera.** ADS input (protocol bump), aim camera with FOV zoom and sniper scope, view recoil and recovery,
   separate hip and aim sensitivity in Settings.
6. **Weapon feel.** Weapon models on the hand bone, muzzle flash, impact effect, shell ejection and hit markers, all
   pooled so they do not allocate during a fight.
7. **Mobile controls.** Aim button, fire-and-look on the right half of the screen, layout editor, haptics.
8. **Training range.** New mode from the main menu: targets at 10, 25, 50, 100 and 200 m, moving targets, a weapon rack
   with every gun, damage numbers and an accuracy readout.
9. **Mobile performance.** Character LODs, animator culling, ASTC textures, an FPS budget checked on the phone with the
   existing telemetry (target: 30 FPS sustained on mid-range Android, 60 on recent iPhones).
10. **Tests.** EditMode tests for the animator parameter mapping and weapon config; PlayMode tests that drive a character
    through move, aim, fire, reload and switch and assert the animator states; the existing API, .NET and Unity suites
    must stay green.

Tasks 5, 7 and 8, and the effects in task 6, do not depend on assets and can start as soon as you say go. Tasks 2, 3
and 4 only look right once a licensed character and animations are in the project.

## 6. Acceptance test (from the brief)

"The player can control a 3D character, move smoothly, aim, fire, reload, switch weapons, and see correct animations in a
playable test environment." That needs, at minimum, one licensed humanoid with rifle and pistol animations. Without it,
everything else can be built and tested, but the character on screen would still be a placeholder, and this report will
say so rather than call it done.
