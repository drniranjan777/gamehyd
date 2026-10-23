using System.Collections.Generic;
using RWBR.Client.Audio;
using RWBR.Client.Combat;
using RWBR.Client.Greybox;
using RWBR.Client.Player;
using RWBR.Client.Settings;
using RWBR.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Practice
{
    /// <summary>
    /// Phase 1 vertical slice: runs <see cref="PracticeSimulation"/> at 30 Hz from player input, renders it,
    /// and exposes state for the HUD. Loop: find a rifle, clear the five enemies, restart.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class PracticeGame : MonoBehaviour, ICameraTarget
    {
        public struct FeedEntry
        {
            public string Text;
            public float Time;
        }

        /// <summary>Damage shown where a shot landed (training range and practice).</summary>
        public struct DamageNumber
        {
            public Vector3 Position;
            public float Amount;
            public bool Headshot;
            public float Time;
        }

        private const float MaxAimDistance = 150f;

        private readonly DeviceInputSource _device = new DeviceInputSource();
        private PracticeViews _views;
        private float _accumulator;
        private uint _sequence;
        private bool _crouchToggle;
        private bool _jumpLatched, _reloadLatched, _interactLatched, _healLatched, _switchLatched;

        /// <summary>Map for the next practice run (set by the main menu); null keeps the scene's map.</summary>
        public static string RequestedMap { get; set; }
        /// <summary>Run the onboarding steps on the next training-ground run (set by the main menu).</summary>
        public static bool TutorialRequested { get; set; }
        public TutorialDirector Tutorial { get; private set; }
        private bool _aimToggle;
        private readonly List<SVector3> _hitPoints = new List<SVector3>();
        private readonly FootstepTracker _footsteps = new FootstepTracker();
        private readonly HashSet<ushort> _shotThisTick = new HashSet<ushort>();
        private bool _wasReloading;

        public PracticeSimulation Sim { get; private set; }
        public TouchControls Touch { get; private set; }
        /// <summary>Replaces device and touch input (automated tests).</summary>
        public IPlayerInputSource InputOverride { get; set; }

        public float ViewYaw { get; private set; }
        public float ViewPitch { get; private set; } = 8f;
        public bool Paused { get; set; }
        public bool InventoryOpen { get; set; }
        public float CurrentSpreadDegrees { get; private set; }
        public float LastHitTime { get; private set; } = -10f;
        public bool LastHitWasKill { get; private set; }
        public float LastDamagedTime { get; private set; } = -10f;
        public float RunStartTime { get; private set; }
        public float RunEndTime { get; private set; }
        public int Kills { get; private set; }
        public List<FeedEntry> Feed { get; } = new List<FeedEntry>();
        public PracticeViews Views => _views;
        public bool MenusOpen => Paused || InventoryOpen || Sim.Outcome != PracticeOutcome.InProgress;

        public Vector3 CameraPivot
        {
            get
            {
                Transform t = _views?.PlayerTransform;
                Vector3 feet = t != null ? t.position : Vector3.zero;
                return feet + Vector3.up * (Sim.Player.Motor.Crouched ? 1.15f : 1.7f);
            }
        }

        public float YawDegrees => ViewYaw;
        public float PitchDegrees => ViewPitch;
        public AimState Aim { get; } = new AimState();

        /// <summary>Trigger pulls this run, and pulls where at least one pellet hit (accuracy readout).</summary>
        public int ShotsFired { get; private set; }
        public int ShotsHit { get; private set; }
        /// <summary>Distance of the last target hit (range: from the firing line), in metres.</summary>
        public float LastHitDistance { get; private set; }
        public List<DamageNumber> DamageNumbers { get; } = new List<DamageNumber>();

        private void Awake()
        {
            GameSettings.Load();
            GreyboxWorld world = FindAnyObjectByType<GreyboxWorld>();
            if (world != null && RequestedMap != null) world.UseMap(RequestedMap);
            Sim = new PracticeSimulation(world != null ? world.Layout : MapLayout.GreyboxBlock(100f));
            Touch = new TouchControls();
            _views = new PracticeViews(transform);
            _views.Build(Sim);
            GameAudio audio = GameAudio.Instance;
            audio.LineOfSight = (a, b) => Sim.HasLineOfSight(a.ToShared(), b.ToShared());
            audio.Indoors = p => Simulation.WallDistance(p.ToShared(), System.Numerics.Vector3.UnitY, 12f, Sim.Map) < 12f;
            Restart();
        }

        public void Restart()
        {
            Sim.Reset();
            _views.ResetColors(Sim);
            _views.Sync(Sim);
            ViewPitch = 8f;
            _crouchToggle = false;
            _accumulator = 0;
            Kills = 0;
            Feed.Clear();
            Paused = false;
            InventoryOpen = false;
            RunStartTime = Time.time;
            ViewYaw = Sim.Player.Yaw * Mathf.Rad2Deg;
            Aim.Reset();
            _aimToggle = false;
            ShotsFired = ShotsHit = 0;
            LastHitDistance = 0;
            DamageNumbers.Clear();
            Tutorial = TutorialRequested && !Sim.IsCity && !Sim.IsRange ? new TutorialDirector(Sim) : null;
            string hint = Sim.IsRange ? "Training range: every weapon is on the bench. Targets at 10 to 200 m stand up again"
                : Sim.IsCity ? "Find a weapon: shops and houses have loot inside" : "Find a rifle in the building ahead";
            Feed.Add(new FeedEntry { Text = hint, Time = Time.time });
        }

        private void Update()
        {
            PlayerInputFrame frame = ReadInput();
            if (frame.PausePressed) Paused = !Paused;
            if (frame.InventoryToggled && Sim.Outcome == PracticeOutcome.InProgress) InventoryOpen = !InventoryOpen;
            UpdateCursor();

            bool playing = !Paused && Sim.Outcome == PracticeOutcome.InProgress;
            Combatant combat = Sim.Player.Combat;
            if (playing && frame.AimToggled) _aimToggle = !_aimToggle;
            if (!combat.HasWeapon || !combat.Alive) _aimToggle = false;
            bool aiming = playing && (frame.Aim || _aimToggle) && combat.HasWeapon && combat.Alive && !combat.Reloading && !InventoryOpen;
            Aim.Update(aiming, combat.HasWeapon ? Sim.PlayerWeapon.AimZoom : 1f, Time.deltaTime);
            if (playing)
            {
                float sens = GameSettings.Sensitivity * Aim.SensitivityScale(GameSettings.AimSensitivity);
                ViewYaw = Mathf.Repeat(ViewYaw + frame.Look.x * sens, 360f);
                ViewPitch = Mathf.Clamp(ViewPitch - frame.Look.y * sens, -70f, 75f);
                _jumpLatched |= frame.JumpPressed;
                _reloadLatched |= frame.ReloadPressed;
                _interactLatched |= frame.InteractPressed;
                _healLatched |= frame.HealPressed;
                _switchLatched |= frame.SwitchPressed || (frame.SelectSlot >= 0 && frame.SelectSlot != Sim.Player.Combat.ActiveSlot);
                if (frame.CrouchToggled) _crouchToggle = !_crouchToggle;

                _accumulator += Mathf.Min(Time.deltaTime, 0.25f);
                while (_accumulator >= PracticeSimulation.Dt && Sim.Outcome == PracticeOutcome.InProgress)
                {
                    InputCommand cmd = BuildCommand(frame);
                    CurrentSpreadDegrees = Sim.Player.Combat.HasWeapon ? Sim.CurrentSpread(Sim.Player, cmd, Sim.PlayerWeapon) : 0;
                    _views.BeforeStep(Sim);
                    Sim.Step(cmd);
                    _views.AfterStep(Sim);
                    ProcessEvents();
                    TickSounds();
                    _accumulator -= PracticeSimulation.Dt;
                    if (Sim.Outcome != PracticeOutcome.InProgress) RunEndTime = Time.time;
                }
            }

            Touch.InteractAvailable = Sim.NearestPickup(Sim.Player) >= 0;
            Tutorial?.Update(Sim, ViewYaw);
            _views.SetPlayerView(ViewPitch + Aim.KickPitch, Aim.Eased, Aim.Scoped);
            _views.Render(Sim, Mathf.Clamp01(_accumulator / PracticeSimulation.Dt), Time.time);
            Feed.RemoveAll(e => Time.time - e.Time > 5f);
            DamageNumbers.RemoveAll(d => Time.time - d.Time > 1.2f);
        }

        private PlayerInputFrame ReadInput()
        {
            if (InputOverride != null) return InputOverride.Read();
            PlayerInputFrame frame = _device.Read();
            if (Touch.Visible) frame.Merge(Touch.Read());
            return frame;
        }

        private void UpdateCursor()
        {
            if (Touch.Visible || InputOverride != null) return;
            bool wantLocked = !MenusOpen;
            if (wantLocked && Cursor.lockState != CursorLockMode.Locked)
            {
                // Lock on the first click into the game view, so the editor and desktop stay usable.
                Mouse mouse = Mouse.current;
                if (mouse != null && mouse.leftButton.wasPressedThisFrame) Cursor.lockState = CursorLockMode.Locked;
            }
            else if (!wantLocked && Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
            }
            Cursor.visible = Cursor.lockState != CursorLockMode.Locked;
            _device.MouseLookEnabled = Cursor.lockState == CursorLockMode.Locked;
        }

        private InputCommand BuildCommand(PlayerInputFrame frame)
        {
            Fighter p = Sim.Player;
            AimAngles(p, out float yaw, out float pitch);

            var buttons = InputButtons.None;
            if (frame.Fire && !InventoryOpen) buttons |= InputButtons.Fire;
            if (frame.Sprint) buttons |= InputButtons.Sprint;
            if (_crouchToggle) buttons |= InputButtons.Crouch;
            if (_jumpLatched) buttons |= InputButtons.Jump;
            if (_reloadLatched) buttons |= InputButtons.Reload;
            if (_interactLatched) buttons |= InputButtons.Interact;
            if (_healLatched) buttons |= InputButtons.Heal;
            if (_switchLatched) buttons |= InputButtons.SwitchWeapon;
            if (Aim.Aiming) buttons |= InputButtons.Aim;
            _jumpLatched = _reloadLatched = _interactLatched = _healLatched = _switchLatched = false;
            if (buttons.HasFlag(InputButtons.Jump)) _crouchToggle = false;

            // The command has one yaw for both aim and movement. Aim yaw differs slightly from the camera yaw
            // (shoulder offset), so rotate the stick into the aim frame: movement follows the camera, shots
            // follow the crosshair, and nothing drifts.
            Vector2 move = InventoryOpen ? Vector2.zero : frame.Move;
            float delta = (ViewYaw + Aim.KickYaw) * Mathf.Deg2Rad - yaw;
            float cos = Mathf.Cos(delta), sin = Mathf.Sin(delta);

            return new InputCommand
            {
                Sequence = ++_sequence,
                AckTick = (uint)Sim.Tick,
                MoveX = move.x * cos + move.y * sin,
                MoveZ = -move.x * sin + move.y * cos,
                Yaw = yaw,
                Pitch = pitch,
                Buttons = buttons,
            };
        }

        /// <summary>
        /// Third-person aim: find what the crosshair (camera centre) points at, then aim from the character's eye at
        /// that point. The shared rules shoot from the eye, so this keeps shots on the crosshair (including recoil kick).
        /// </summary>
        private void AimAngles(Fighter p, out float yaw, out float pitch)
        {
            Quaternion view = Quaternion.Euler(ViewPitch + Aim.KickPitch, ViewYaw + Aim.KickYaw, 0);
            Vector3 camForward = view * Vector3.forward;
            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : CameraPivot - camForward * 3.6f;

            SVector3 origin = camPos.ToShared(), dir = camForward.ToShared();
            float t = Simulation.WallDistance(origin, dir, MaxAimDistance, Sim.Map);
            if (dir.Y < -1e-4f) t = Mathf.Min(t, (0 - origin.Y) / dir.Y); // ground plane
            foreach (Fighter e in Sim.Enemies)
            {
                if (!e.Combat.Alive) continue;
                float? hit = Simulation.RayCapsule(origin, dir, e.Motor.Position, e.Motor.Height, MaxAimDistance);
                if (hit.HasValue && hit.Value < t) t = hit.Value;
            }
            SVector3 aimPoint = origin + dir * Mathf.Max(t, 2f);
            SVector3 d = aimPoint - p.Motor.Eye;
            yaw = Mathf.Atan2(d.X, d.Z);
            pitch = Mathf.Atan2(d.Y, Mathf.Sqrt(d.X * d.X + d.Z * d.Z));
        }

        private void TickSounds()
        {
            GameAudio audio = GameAudio.Instance;
            foreach (Fighter f in Sim.AllFighters())
            {
                if (!f.Combat.Alive) continue;
                if (_footsteps.Step(f.Id, f.Motor.Position.ToUnity(), f.Motor.Grounded, f.Motor.Crouched))
                    audio.PlayAt(Sfx.Footstep, f.Motor.Position.ToUnity(), f.IsPlayer ? 0.45f : 0.7f, 25f);
            }
            bool reloading = Sim.Player.Combat.Reloading;
            if (reloading && !_wasReloading) audio.PlayAt(Sfx.Reload, Sim.Player.Motor.Position.ToUnity(), 0.8f, 15f);
            _wasReloading = reloading;
        }

        private void ProcessEvents()
        {
            _shotThisTick.Clear();
            _hitPoints.Clear();
            GameAudio audio = GameAudio.Instance;
            CombatEffects effects = _views.Effects;
            bool playerHit = false;
            foreach (SimEvent e in Sim.Events)
                if (e.Type == SimEventType.Hit) _hitPoints.Add(e.To);

            foreach (SimEvent e in Sim.Events)
            {
                bool byPlayer = e.Actor == PracticeSimulation.PlayerId;
                switch (e.Type)
                {
                    case SimEventType.Shot:
                        WeaponDefinition weapon = Sim.WeaponOf(FighterOf(e.Actor));
                        WeaponHolder holder = _views.WeaponOf(e.Actor);
                        Vector3? muzzle = holder?.Current != null ? holder.MuzzlePosition : (Vector3?)null;
                        _views.AddTracer(e.From, e.To, byPlayer, Time.time, muzzle);
                        if (SVector3.Distance(e.From, e.To) < weapon.Range - 0.05f && !_hitPoints.Contains(e.To))
                            effects.Impact(e.To.ToUnity(), (e.From - e.To).ToUnity().normalized, player: false);
                        if (!_shotThisTick.Add(e.Actor)) break; // one sound, flash and kick per trigger pull, not per pellet
                        audio.Gunshot(weapon, e.From.ToUnity(), byPlayer);
                        if (holder?.Current != null)
                        {
                            holder.Fired(weapon.RecoilPitchDegrees);
                            _views.CharacterOf(e.Actor)?.Fired();
                            effects.MuzzleFlash(holder.MuzzlePosition, holder.Forward, byPlayer, Time.time);
                            if (weapon.Class != WeaponClass.Shotgun) effects.Shell(holder.EjectorPosition, holder.Right, holder.Forward);
                        }
                        if (byPlayer)
                        {
                            ShotsFired++;
                            Vector2 climb = Aim.Recoil(weapon.RecoilPitchDegrees, weapon.RecoilYawDegrees);
                            ViewPitch = Mathf.Clamp(ViewPitch + climb.x, -70f, 75f);
                            ViewYaw = Mathf.Repeat(ViewYaw + climb.y, 360f);
                        }
                        break;
                    case SimEventType.Hit:
                        effects.Impact(e.To.ToUnity(), (e.From - e.To).ToUnity().normalized, player: true);
                        _views.CharacterOf(e.Target)?.Hit();
                        if (byPlayer)
                        {
                            LastHitTime = Time.time;
                            LastHitWasKill = false;
                            if (!playerHit) Haptics.Hit(); // once per trigger pull, not per pellet
                            playerHit = true;
                            LastHitDistance = Sim.TargetDistance(FighterOf(e.Target));
                            DamageNumbers.Add(new DamageNumber { Position = e.To.ToUnity(), Amount = e.Damage, Headshot = e.Headshot, Time = Time.time });
                            audio.PlayUi(e.Headshot ? Sfx.Headshot : Sfx.HitMarker);
                            if (Tutorial != null) Tutorial.EnemyHit = true;
                        }
                        else if (e.Target == PracticeSimulation.PlayerId)
                        {
                            LastDamagedTime = Time.time;
                            Haptics.Damaged();
                        }
                        break;
                    case SimEventType.Kill:
                        if (byPlayer)
                        {
                            LastHitWasKill = true;
                            Haptics.Kill();
                            Kills++;
                            audio.PlayUi(Sfx.Kill);
                            Feed.Add(new FeedEntry { Text = Sim.IsRange
                                ? $"{NameOf(e.Target)} down{(e.Headshot ? " (headshot)" : "")}: {Sim.PlayerWeapon.DisplayName}"
                                : $"You eliminated {NameOf(e.Target)}{(e.Headshot ? " (headshot)" : "")} with the {Sim.PlayerWeapon.DisplayName}", Time = Time.time });
                        }
                        else
                        {
                            Feed.Add(new FeedEntry { Text = $"{NameOf(e.Actor)} eliminated you", Time = Time.time });
                        }
                        break;
                    case SimEventType.Switch:
                        if (byPlayer) audio.PlayUi(Sfx.Switch);
                        break;
                    case SimEventType.Pickup:
                        if (byPlayer) audio.PlayUi(Sfx.Pickup);
                        if (byPlayer) Feed.Add(new FeedEntry { Text = $"Picked up {(e.Loot == LootType.Weapon || e.Loot == LootType.Rifle ? Sim.PlayerWeapon.DisplayName : e.Loot.ToString())}", Time = Time.time });
                        break;
                }
            }
            if (playerHit) ShotsHit++;
        }

        private Fighter FighterOf(ushort id)
        {
            foreach (Fighter f in Sim.AllFighters())
                if (f.Id == id) return f;
            return Sim.Player;
        }

        private string NameOf(ushort id)
        {
            foreach (Fighter f in Sim.AllFighters())
                if (f.Id == id) return f.Name;
            return "?";
        }

        /// <summary>Queue a medkit use for the next tick (inventory button).</summary>
        public void RequestHeal() => _healLatched = true;

        /// <summary>Test/debug hook: move the player (e.g. next to loot).</summary>
        public void TeleportPlayer(Vector3 feet)
        {
            Sim.Player.Motor = MotorState.StandingAt(feet.ToShared());
            _views.Sync(Sim);
        }

        public void SetView(float yawDegrees, float pitchDegrees)
        {
            ViewYaw = yawDegrees;
            ViewPitch = pitchDegrees;
        }
    }
}
