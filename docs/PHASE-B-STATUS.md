# Phase B status: camera, aiming, weapon feel, mobile controls, training range

Date: 22 October 2026. Plan and audit: [PHASE-A-CHARACTER-AUDIT.md](PHASE-A-CHARACTER-AUDIT.md).

This is the first part of Phase B: everything that does not need the licensed character and animations. **The
characters are still placeholder capsules.** Rigged characters, the twelve animation states, blending and IK start when
the Mixamo files are in `game/unity-client/Assets/ThirdParty/Mixamo/` (and the repository is private).

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

## Before players get this build

The production server at game.tallygen.com runs protocol 4 and schema 0009. An APK from this code (protocol 5) **cannot
join production matches** until the server side is deployed:

1. Apply migration `0010_weapon_balance_v4.sql` to the production database.
2. Deploy the match server built from this commit.
3. Ship the new APK. Installed older apps will be refused by the new server and asked to update.

The firing range and practice work offline either way.

## Not done yet (honest list)

- Realistic characters, outfits, helmets, backpacks, customisation: need the Mixamo files.
- Animations, blending, foot and hand IK, turn-in-place, hit reactions and death animations: need the Mixamo files.
- Remote players' aim and pitch are not sent to other clients, so their weapons stay level.
- Touch layout editor and haptics were not built.
- The weapon models are simple original shapes; a licensed weapon pack would look better.
- Character LODs, animator culling and ASTC texture settings come with the character import.
- No iOS build has been made (no Mac or Apple account in this setup).
- Not yet checked on the phone.
