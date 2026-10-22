using System.Collections.Generic;
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

        private const float MaxAimDistance = 150f;

        private readonly DeviceInputSource _device = new DeviceInputSource();
        private PracticeViews _views;
        private float _accumulator;
        private uint _sequence;
        private bool _crouchToggle;
        private bool _jumpLatched, _reloadLatched, _interactLatched, _healLatched;
        private int _recoilSign = 1;

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

        private void Awake()
        {
            GameSettings.Load();
            GreyboxWorld world = FindAnyObjectByType<GreyboxWorld>();
            Sim = new PracticeSimulation(world != null ? world.Layout : MapLayout.GreyboxBlock(100f));
            Touch = new TouchControls();
            _views = new PracticeViews(transform);
            _views.Build(Sim);
            Restart();
        }

        public void Restart()
        {
            Sim.Reset();
            _views.ResetColors(Sim);
            _views.Sync(Sim);
            ViewYaw = 0;
            ViewPitch = 8f;
            _crouchToggle = false;
            _accumulator = 0;
            Kills = 0;
            Feed.Clear();
            Paused = false;
            InventoryOpen = false;
            RunStartTime = Time.time;
            Feed.Add(new FeedEntry { Text = "Find a rifle in the building ahead", Time = Time.time });
        }

        private void Update()
        {
            PlayerInputFrame frame = ReadInput();
            if (frame.PausePressed) Paused = !Paused;
            if (frame.InventoryToggled && Sim.Outcome == PracticeOutcome.InProgress) InventoryOpen = !InventoryOpen;
            UpdateCursor();

            bool playing = !Paused && Sim.Outcome == PracticeOutcome.InProgress;
            if (playing)
            {
                float sens = GameSettings.Sensitivity;
                ViewYaw = Mathf.Repeat(ViewYaw + frame.Look.x * sens, 360f);
                ViewPitch = Mathf.Clamp(ViewPitch - frame.Look.y * sens, -70f, 75f);
                _jumpLatched |= frame.JumpPressed;
                _reloadLatched |= frame.ReloadPressed;
                _interactLatched |= frame.InteractPressed;
                _healLatched |= frame.HealPressed;
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
                    _accumulator -= PracticeSimulation.Dt;
                    if (Sim.Outcome != PracticeOutcome.InProgress) RunEndTime = Time.time;
                }
            }

            Touch.InteractAvailable = Sim.NearestPickup(Sim.Player) >= 0;
            _views.Render(Sim, Mathf.Clamp01(_accumulator / PracticeSimulation.Dt), Time.time);
            Feed.RemoveAll(e => Time.time - e.Time > 5f);
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
            _jumpLatched = _reloadLatched = _interactLatched = _healLatched = false;
            if (buttons.HasFlag(InputButtons.Jump)) _crouchToggle = false;

            // The command has one yaw for both aim and movement. Aim yaw differs slightly from the camera yaw
            // (shoulder offset), so rotate the stick into the aim frame: movement follows the camera, shots
            // follow the crosshair, and nothing drifts.
            Vector2 move = InventoryOpen ? Vector2.zero : frame.Move;
            float delta = ViewYaw * Mathf.Deg2Rad - yaw;
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
        /// that point. The shared rules shoot from the eye, so this keeps shots on the crosshair.
        /// </summary>
        private void AimAngles(Fighter p, out float yaw, out float pitch)
        {
            Quaternion view = Quaternion.Euler(ViewPitch, ViewYaw, 0);
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

        private void ProcessEvents()
        {
            foreach (SimEvent e in Sim.Events)
            {
                bool byPlayer = e.Actor == PracticeSimulation.PlayerId;
                switch (e.Type)
                {
                    case SimEventType.Shot:
                        _views.AddTracer(e.From, e.To, byPlayer, Time.time);
                        if (byPlayer)
                        {
                            ViewPitch -= Sim.PlayerWeapon.RecoilPitchDegrees;
                            ViewYaw += Sim.PlayerWeapon.RecoilYawDegrees * (_recoilSign = -_recoilSign);
                        }
                        break;
                    case SimEventType.Hit:
                        if (byPlayer)
                        {
                            LastHitTime = Time.time;
                            LastHitWasKill = false;
                        }
                        else if (e.Target == PracticeSimulation.PlayerId)
                        {
                            LastDamagedTime = Time.time;
                        }
                        break;
                    case SimEventType.Kill:
                        if (byPlayer)
                        {
                            LastHitWasKill = true;
                            Kills++;
                            Feed.Add(new FeedEntry { Text = $"You eliminated {NameOf(e.Target)}", Time = Time.time });
                        }
                        else
                        {
                            Feed.Add(new FeedEntry { Text = $"{NameOf(e.Actor)} eliminated you", Time = Time.time });
                        }
                        break;
                    case SimEventType.Pickup:
                        if (byPlayer) Feed.Add(new FeedEntry { Text = $"Picked up {e.Loot}", Time = Time.time });
                        break;
                }
            }
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
