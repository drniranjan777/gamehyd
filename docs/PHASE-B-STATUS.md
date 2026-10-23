# Phase B status: camera, aiming, weapon feel, mobile controls, training range

Date: 22 October 2026. Plan and audit: [PHASE-A-CHARACTER-AUDIT.md](PHASE-A-CHARACTER-AUDIT.md).

Part 1 (below) is everything that does not need the licensed character and animations. Part 2 is the character
animation plumbing, built and tested on a procedural skeleton. **The characters on screen are still placeholder
capsules** until the Mixamo files are in `game/unity-client/Assets/ThirdParty/Mixamo/` and `./unity-batch.sh characters`
has been run.

## What was built

### Aiming down sights (server-validated)

- New input `InputButtons.Aim`. The button field is now 16 bits, and the **protocol version is 5** (was 4), so old
  clients and servers refuse each other with the existing version check.
- Aiming multiplies standing and moving spread by the weapon's `AimSpreadMultiplier` (airborne spread is not reduced),
  slows movement to 70 % (`GameRules.AimMoveSpeedFactor`) and blocks sprint. Client prediction uses the same shared
  rules, so there is no correction when you aim.
- **Weapon balance v4**: v3 plus `aimSpreadMultiplier` and `aimZoom` per weapon. Damage, fire rate and hip-fire spread
  are unchanged. Mirrored in migration `0010_weapon_balance_v4.sql`, and a test checks it against the catalog value by
  value. `docs/balance/TTK.md` was regenerated (only its version line changed).

| Weapon         | Aim spread × | Zoom       |
| -------------- | ------------ | ---------- |
| P9 Sidearm     | 0.60         | 1.25×      |
| Kestrel SMG    | 0.65         | 1.25×      |
| Standard Rifle | 0.50         | 1.5×       |
| Pump-12        | 0.85         | 1.15×      |
| Marksman DMR   | 0.35         | 3× (scope) |
| Longshot       | 0.20         | 6× (scope) |

### Camera

- `ThirdPersonCamera`: moves in from 3.6 m to 1.7 m and narrows the field of view by the weapon's zoom while aiming
  (0.18 s blend). For 3× and above it moves to the eye and the HUD draws a scope (lens mask and reticle), and the
  player's body and weapon are hidden.
- Wall collision now snaps in and eases back out instead of jumping.
- Look speed while aiming is divided by the zoom, times a new **Aim sensitivity** setting (main menu Settings and both
  pause menus).

### Recoil and weapon feel

- `AimState` splits each shot's recoil into a permanent climb (45 %) and a kick (80 %) that recovers on its own within
  a few tenths of a second; aimed recoil is 25 % lower. Shots follow the kicked view, as the crosshair does.
- `WeaponModels`: original low-poly 3D weapon models built at runtime, one per weapon class (pistol, SMG, rifle,
  shotgun, DMR with scope, sniper with scope), with grip, muzzle and ejection points. Everyone holds their current
  weapon, including remote players online.
- `WeaponHolder`: procedural weapon motion: hip and aimed positions, pitch with the view, a dip and roll through reloads,
  lower and raise on switch, kick back on each shot. This is placeholder motion for the capsules; with rigged characters
  the hands will follow the weapon through IK.
- `CombatEffects`: pooled particle effects, with no allocation per shot. Muzzle flash, plus a 50 ms light for your own
  shots on Medium and High quality. Spent cases (not the shotgun), dust on world hits, sparks on player hits (no blood).
  "Reduce flashing" shrinks the flash and turns off the light.
- Tracers now start at the muzzle.

### Mobile controls

- **AIM** button (toggle), lit while aiming. A test checks that no two touch buttons overlap on 16:9 and 20:9 screens.
- Drag-to-look while holding FIRE already existed and is unchanged.
- Right mouse button and the gamepad left trigger aim on desktop and controllers.

### Training range

- Main menu → **Firing range** (offline; map `training-range-v1`, built from `MapLayout.TrainingRange()`).
- Bench with all six weapons (the rack never empties) and armour. Endless ammo. You start with the rifle.
- Five lanes with a target and a distance post at 10, 25, 50, 100 and 200 m, plus two strafing targets (28 m and 53 m)
  behind low cover. Targets do not shoot and stand up 2 s after going down.
- HUD: accuracy (trigger pulls that hit / trigger pulls), distance of the last hit, floating damage numbers (gold for
  headshots), distance labels over the posts.

## Tests (22 October 2026)

| Suite                                | Result                                                                                                                                                                                                                                                                                                                                                                                                      |
| ------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET (match server and shared rules) | 80/80. New: aim bit through the packet, spread with aim for every weapon, aimed movement speed and no sprint, balance rows match migration 0010                                                                                                                                                                                                                                                             |
| Unity EditMode                       | 37/37. New: aim blend and zoom, scoped sensitivity, recoil recovery, range setup, target respawn and movement, endless ammo and rack, aimed practice spread, touch layout overlap                                                                                                                                                                                                                           |
| Unity PlayMode                       | 11 tests. New, all passing: **aim, fire and reload on the range** (rifle model held, zoom reaches the weapon's FOV, hits at 10 m, effects emitted, reload refills, no aiming while reloading); **pick up, switch and scope a sniper** (scope moves the camera to the eye and hides the weapon; switch back changes the model); **aiming slows movement**. The full-stack Quick-play and store tests passed. |
| API                                  | 126/126 (11 suites)                                                                                                                                                                                                                                                                                                                                                                                         |
| Database                             | 10/10, with migration 0010 applied                                                                                                                                                                                                                                                                                                                                                                          |

Known flaky test: `OnlineMatchTests.Client_joins_predicts_movement_and_receives_the_server_result` timed out once
waiting for the match result: the bot did not eliminate the idle test player within 70 s. It passed when rerun alone,
and nothing in the bot or in unaimed combat changed. It needs a longer wait or a bot spawned closer.

## Part 2 (23 October 2026): character animation plumbing

Everything the Mixamo character needs, built and tested **without the art**. When the files are in, run
`./unity-batch.sh characters` (or RWBR > Characters > Build): the game switches from capsules to the rigged character on
its own. Until then nothing changes on screen.

- **Import pipeline** (`Editor/CharacterImport.cs`). Files go under `Assets/ThirdParty/Mixamo/Characters` (With Skin)
  and `Assets/ThirdParty/Mixamo/Animations` (Without Skin, In Place). The import step makes them humanoid, takes the
  animations' avatar from the character, and bakes root motion into the pose (the simulation moves characters). It
  loops locomotion clips, skips materials on animation files, and compresses meshes, animation and textures (ASTC,
  1024 px cap on Android and iOS).
- **Clip matching**: Mixamo names every clip "mixamo.com", so file names pick the slot ("Rifle Run Forward",
  "walk backwards", "Pistol Idle"…). There are 21 slots, and files with "pistol" fill the pistol set. Missing required
  clips fail the batch build with a list.
- **Animator controller** (generated into `Resources/Characters/RWBR_Humanoid.controller`):
  - **Locomotion layer:** rifle and pistol 2D blend trees on body-frame velocity: idle, walk and run in 4 directions, and
    sprint. A missing strafe side is mirrored, and a missing sprint plays the run faster. Also crouch, jump, fall, land,
    turn-in-place (only when there is a turn clip) and death, which clears when a range target stands up.
  - **Upper-body layer** (legs untouched): aim and reload.
  - **Reactions layer:** hit flinch.
  - **Every layer runs the IK pass.**
  - **Neutral pose:** a generated relaxed standing clip fills idle, crouch idle and death until real clips exist, so a
    character never shows its T-pose.
- **Character prefab**: built from the character model, scaled to the simulation's 1.8 m. It uses two-bone skinning,
  and animation is culled when off screen.
- **`CharacterAnimator`** (runtime driver), fed by the practice simulation, your prediction and other players'
  snapshots:
  - **Animation parameters:** damped forward and strafe speed, crouch, grounded, aim, pitch, pistol or rifle set, and
    reloading.
  - **Events:** jump and land on take-off and touch-down, switch on a weapon change, hit on damage, death.
  - **Turn in place:** the feet stay put for looks under 60°, then the body turns to the view in 0.3 s.
  - **IK:** both hands go to grip points on the weapon model. They let go while reloading, so the reload clip moves
    them. The head and spine aim along the view, and when standing still the feet are planted on the ground (stairs,
    kerbs) by raycast.
- **`CharacterBody`**: one interface for both bodies. The rigged one is used when the prefab exists, otherwise the
  capsule. A rigged body holds the weapon where a real arm reaches. Range targets now also get their colour back when
  they stand up again (a capsule bug fixed on the way).
- **Licensed files stay off the public repository**: `.gitignore` excludes `Assets/ThirdParty/` and the built prefab,
  until the repository is private.

Part 2 tests (all passing):

| Suite          | Result                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Unity EditMode | 44/44. New: Mixamo file names to slots (rifle and pistol), missing-clip report, controller parameters, layers, IK pass, upper-body mask and states, mirrored strafe, committed controller present, body-frame velocity, turn in place                                                                                                                                                                                                      |
| Unity PlayMode | 15/15, including the full-stack Quick-play, store and online match tests. New: on a **procedurally built humanoid skeleton** driving the real controller, both hands land on the weapon grips (0.00 m), the feet are planted on the ground, the hands let go during reload, run and strafe reach the blend parameters, crouch and death states play, the body turns in place after a 100° look, and without the prefab the capsule is used |

## Part 3 (23 October 2026): other players' aim, touch layout editor, haptics

- **Other players' aim and view.** Snapshots now carry each player's view pitch (1° on the wire, sent together with yaw
  only when either changes) and an **Aiming** flag. This is **protocol 6**. A full player entry grows from 23 to 24
  bytes; unchanged and moving-only players cost the same as before. Other players' weapons now tilt with their view and
  rise when they aim (eased like your own), and the pitch is interpolated between snapshots. Their tracers use their
  real pitch and aimed spread, and their jumps and landings play from the existing Grounded flag. (An earlier note said
  grounded state was not sent; it already was.) The server keeps a player aiming through brief input loss, like crouch.
- **Touch layout editor**: Settings → Touch layout. Drag any button to move it, set a button size from 0.7× to 1.4×,
  or reset. Overlapping buttons turn red and are named. The layout is saved in PlayerPrefs, and broken or unknown saved
  entries fall back to the defaults.
- **Haptics** (Settings → Accessibility → Vibration, on by default): a short tick for each trigger pull that hits, a
  stronger pulse for an elimination, and one when you take damage, in practice, on the range and online. Android uses
  the system vibrator with one-shot effects of 12, 40 and 28 ms. iOS has no short vibration without a native plugin, so
  there only eliminations vibrate.

Part 3 tests (all passing): .NET 82/82 (pitch and aim through full and delta snapshots, pitch clamp and precision,
server tracks aim and keeps it through input loss, updated byte budget), Unity EditMode 48/48 (layout save and restore,
on-screen clamping, overlap report, broken saved layouts, vibration off), Unity PlayMode 16/16 (another player's weapon
follows their pitch and their aim blend from snapshots; the online match runs against the protocol 6 server).

## Part 4 (23 October 2026): mobile performance (task 9)

- **Character detail by distance**, until a character pack with mesh LODs is in. Up to 15 m from the camera a character
  gets everything. From 15 m the foot IK and its raycasts stop. From 45 m the character uses one-bone skinning, casts no
  shadow, and runs no IK. A 2 m margin stops characters on a boundary from flickering between tiers. Low quality
  switches at 0.6× those distances and High at 1.5×. Capsules stop casting shadows at the far tier too.
- **GPU skinning** is on (`meshDeformation = GPU`, set by the project setup and the Android build). Unity skins on the
  CPU on devices without compute support.
- **Budget in telemetry.** Each `perf.jsonl` line now includes `targetFps` (30 on Low and Medium, 60 on High),
  average and peak `batches`, `setPass` and `triangles`, `autoQuality` and `withinBudget`. A window meets the budget
  when the average frame rate is within 10 % of the target, the 95th-percentile frame takes at most 1.5 target frames,
  and average batches stay under 120, 180 or 250 for Low, Medium and High. Draw-call counters exist in development
  builds only; release builds report them as `null`. The practice overlay shows batches and "over budget".
- **Automatic quality** (Settings → "Lower quality automatically if the game runs slowly", on by default): after two
  missed windows in a row the preset drops one step and is saved. It never goes back up on its own. The first window
  after loading never counts. Picking a preset by hand turns it off.
- Fixed: running the EditMode tests no longer rewrites the editor's `QualitySettings.asset`. Applying a quality preset
  outside play mode now does nothing.

Part 4 tests: .NET 82/82 (no server change). Unity EditMode 55/55 (tier distances and hysteresis, per-quality
distances, budget rules, automatic step-down to Low and stop, picking a preset turns it off, no edit-mode quality
writes). Unity PlayMode 17 passed and 2 skipped: the two full-stack platform tests need `TEST_ADMIN_DATABASE_URL` and
the built API, which were not set for this run. New PlayMode checks on the procedural skeleton: feet planted when near,
no foot IK at mid range with hands still on the grips, no IK far away, and full detail again up close. Capsules drop
their shadow when far. In the practice scene the camera feeds character detail and the telemetry line carries the
budget fields. Batch counts read `null` in these headless runs; they need a rendered development build.

## Before players get this build

The production server at game.tallygen.com runs protocol 4 and schema 0009. An APK from this code (protocol 6) **cannot
join production matches** until the server side is deployed:

1. Apply migration `0010_weapon_balance_v4.sql` to the production database.
2. Deploy the match server built from this commit.
3. Ship the new APK. Installed older apps will be refused by the new server and asked to update.

The firing range and practice work offline either way.

## Not done yet (honest list)

- **On screen the characters are still capsules.** The realistic character and animations need the Mixamo files.
  All the plumbing (part 2) is built and tested, but how the real clips look, hand-grip rotations and foot sliding at
  each speed can only be tuned once they are in.
- Outfit, helmet and backpack variants, and customisation (which needs the platform to store the choice), are not
  built.
- Other players' reload progress is not sent (only "reloading"), so their reload animation loops at the weapon's reload
  length rather than matching their exact progress.
- The touch layout editor is in the main menu only (not in the in-match pause menu).
- Gamepad rumble is not used.
- The weapon models are simple original shapes; a licensed weapon pack would look better.
- **The frame-rate budget has not been measured on a phone.** The checks and telemetry are in, but whether a
  mid-range Android holds 30 FPS needs an APK on the device and `perf.jsonl` pulled with adb.
- Character mesh LODs: the Mixamo characters have a single detail level, so distance detail stands in for now. Real
  LODs need a pack that includes them, or decimated
  copies.
- No iOS build has been made (no Mac or Apple account in this setup).
- Not yet checked on the phone.
