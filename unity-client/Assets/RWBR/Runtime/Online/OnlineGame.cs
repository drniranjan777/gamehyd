using System;
using System.Collections.Generic;
using RWBR.Client.Audio;
using RWBR.Client.Greybox;
using RWBR.Client.Player;
using RWBR.Client.Settings;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Online
{
    /// <summary>
    /// Online match client: sends inputs at the server tick rate, predicts the local player with the shared rules,
    /// reconciles against server snapshots, and interpolates everyone else. The server decides everything that
    /// matters (BRD §7.2); local tracers and recoil are cosmetic.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class OnlineGame : MonoBehaviour, ICameraTarget
    {
        public struct FeedEntry
        {
            public string Text;
            public float Time;
        }

        public const string DefaultServer = "127.0.0.1:7777";
        public const string MenuScene = "MainMenu";
        /// <summary>Remote players render this many ticks behind the newest snapshot (133 ms at 30 Hz).</summary>
        public const float InterpolationDelayTicks = 4f;
        private const string ServerKey = "rwbr.server";
        private const string PlatformKey = "rwbr.platform";
        public const string DefaultPlatform = "http://127.0.0.1:3001/api/v1";
        public const string Region = "IN-SOUTH";
        private const string NameKey = "rwbr.name";
        private const float MaxAimDistance = 150f;
        private const double SilenceBeforeReconnect = 3.0;

        private readonly DeviceInputSource _device = new DeviceInputSource();
        private readonly Dictionary<ushort, byte> _lastShotCounter = new Dictionary<ushort, byte>();
        private bool _switchLatched;
        private readonly FootstepTracker _footsteps = new FootstepTracker();
        private float _nextZoneWarning;

        /// <summary>Balance of the weapon in the local player's hand (from the server's snapshot).</summary>
        public WeaponDefinition SelfWeapon =>
            Self is { } me && WeaponCatalog.IsValid(me.Weapon) ? WeaponCatalog.Get(me.Weapon) : WeaponCatalog.Get(WeaponCatalog.Rifle);
        private GreyboxWorld _world;
        private OnlineViews _views;
        private int _tickRate = 30;
        private float _accumulator;
        private uint _sequence;
        private long _localTick;
        private long _nextLocalShotTick;
        private bool _crouchToggle;
        private bool _jumpLatched, _reloadLatched, _interactLatched, _healLatched;
        private int _recoilSign = 1;
        private bool _mapReady;
        private double _lastReconnectAttempt;
        private float _lastShotTime = -10f;
        private readonly Dictionary<ushort, byte> _lastHealth = new Dictionary<ushort, byte>();

        public NetClient Net { get; } = new NetClient();
        public ClientPrediction Prediction { get; private set; }
        public RemoteInterpolator Interp { get; } = new RemoteInterpolator(InterpolationDelayTicks);
        public MapLayout Map { get; private set; }
        public TouchControls Touch { get; private set; }
        public IPlayerInputSource InputOverride { get; set; }
        public string ServerAddress { get; set; } = DefaultServer;
        public string PlayerName { get; set; } = "Player";
        public string Error { get; private set; }

        // Platform quick play (guest sign-in -> matchmaking -> signed seat).
        public string PlatformUrl { get; set; } = DefaultPlatform;
        public PlatformApi Platform { get; private set; }
        public bool QuickPlayActive { get; private set; }
        public string QuickPlayStatus { get; private set; }
        public ProfileDto ProfileBefore { get; private set; }
        public ProfileDto ProfileAfter { get; private set; }
        private bool _quickPlayCancelled;
        private bool _profileRequested;

        public float ViewYaw { get; private set; }
        public float ViewPitch { get; private set; } = 8f;
        public bool Paused { get; set; }
        public float CurrentSpreadDegrees { get; private set; }
        public float LastHitTime { get; private set; } = -10f;
        public bool LastHitWasKill { get; private set; }
        public float LastDamagedTime { get; private set; } = -10f;
        public int Reconnects { get; private set; }
        public List<FeedEntry> Feed { get; } = new List<FeedEntry>();
        public OnlineViews Views => _views;

        public SnapshotEntry? Self
        {
            get
            {
                SnapshotPacket s = Net.Latest;
                if (s == null) return null;
                foreach (SnapshotEntry e in s.Players)
                    if (e.PlayerId == Net.PlayerId) return e;
                return null;
            }
        }

        public bool SelfAlive => Self is { } s && (s.Flags & PlayerFlags.Alive) != 0;
        public bool MenusOpen => Paused || Net.State != ConnectionState.Connected || Net.Result != null || Error != null;

        public Vector3 CameraPivot
        {
            get
            {
                Transform t = _views?.SelfTransform;
                Vector3 feet = t != null ? t.position : Vector3.zero;
                bool crouched = Prediction != null && Prediction.Predicted.Crouched;
                return feet + Vector3.up * (crouched ? 1.15f : 1.7f);
            }
        }

        public float YawDegrees => ViewYaw;
        public float PitchDegrees => ViewPitch;

        private void Awake()
        {
            GameSettings.Load();
            _world = FindAnyObjectByType<GreyboxWorld>();
            Touch = new TouchControls();
            _views = new OnlineViews(transform);
            ServerAddress = CommandLineServer() ?? Load(ServerKey, DefaultServer);
            PlayerName = Load(NameKey, "Player");
            PlatformUrl = CommandLineArg("-platform") ?? Load(PlatformKey, DefaultPlatform);
        }

        private void Start()
        {
            if (QuickPlayActive) return; // already started by code before the first frame
            if (CommandLineArg("-platform") != null) StartQuickPlay();
            else Connect(ServerAddress);
        }

        /// <summary>Direct connection to a dev server (open mode, no ticket).</summary>
        public void Connect(string address)
        {
            QuickPlayActive = false;
            Platform = null;
            ConnectTo(address, null, null);
        }

        /// <summary>
        /// Platform flow: sign in as a guest (account created on first run), queue, wait for a seat, then join the
        /// assigned server with the signed ticket and session key.
        /// </summary>
        public async void StartQuickPlay()
        {
            Error = null;
            _quickPlayCancelled = false;
            QuickPlayActive = true;
            ProfileBefore = ProfileAfter = null;
            _profileRequested = false;
            Save(PlatformKey, PlatformUrl);
            Save(NameKey, PlayerName);
            Net.Leave();
            // This attempt owns `api`; if anything replaces Platform meanwhile (direct connect, a newer quick play),
            // the attempt stops quietly instead of acting on a stale session.
            var api = new PlatformApi(PlatformUrl);
            Platform = api;
            try
            {
                QuickPlayStatus = "Signing in…";
                await api.SignInAsync(PlayerName);
                if (Platform != api) return;
                ProfileBefore = await api.ProfileAsync();

                QuickPlayStatus = "Finding a match…";
                QueueStatusDto status = await api.QueueAsync(Region, Application.version);
                float started = Time.realtimeSinceStartup;
                while (status.state != "MATCHED")
                {
                    if (_quickPlayCancelled || Platform != api) return;
                    QuickPlayStatus = $"Finding a match… {Time.realtimeSinceStartup - started:0}s";
                    float until = Time.realtimeSinceStartup + 1f;
                    while (Time.realtimeSinceStartup < until) await System.Threading.Tasks.Task.Yield();
                    status = await api.StatusAsync();
                }
                if (Platform != api) return;

                QuickPlayStatus = $"Joining {status.seat.server}…";
                ConnectTo(status.seat.server, Convert.FromBase64String(status.seat.ticket), Convert.FromBase64String(status.seat.sessionKey));
            }
            catch (ApiException e)
            {
                if (Platform != api) return;
                Error = e.Code == "NETWORK" ? e.Message : $"Platform error {e.Code}: {e.Message}";
                QuickPlayActive = false;
            }
        }

        public async void CancelQuickPlay()
        {
            _quickPlayCancelled = true;
            QuickPlayActive = false;
            QuickPlayStatus = null;
            try { if (Platform != null && Platform.SignedIn) await Platform.LeaveQueueAsync(); }
            catch (ApiException) { }
        }

        private void ConnectTo(string address, byte[] ticket, byte[] sessionKey)
        {
            Error = null;
            ServerAddress = address.Trim();
            if (ticket == null) Save(ServerKey, ServerAddress);
            Save(NameKey, PlayerName);
            if (!TryParseAddress(ServerAddress, out string host, out int port))
            {
                Error = $"Server address must look like host:port (got \"{ServerAddress}\")";
                return;
            }
            try
            {
                Net.Connect(host, port, PlayerName, ticket, sessionKey);
            }
            catch (Exception e)
            {
                Error = $"Cannot connect: {e.Message}";
                return;
            }
            _mapReady = false;
            Prediction = null;
            Interp.Clear();
            _views.Clear();
            _lastHealth.Clear();
            Feed.Clear();
            _sequence = 0;
            _localTick = 0;
            _nextLocalShotTick = 0;
            _accumulator = 0;
            ViewYaw = 0;
            ViewPitch = 8f;
            Paused = false;
        }

        public void LeaveToMenu()
        {
            Net.Leave();
            Cursor.lockState = CursorLockMode.None;
            SceneManager.LoadScene(MenuScene);
        }

        private void OnDestroy() => Net.Dispose();

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            Net.Pump(now);

            if (!_mapReady && Net.State == ConnectionState.Connected && Net.Welcome != null) PrepareMap();

            foreach (SnapshotPacket s in Net.NewSnapshots)
            {
                Interp.Add(s);
                Prediction?.Reconcile(s, Net.PlayerId);
                TrackHealth(s);
                RemoteShots(s);
            }
            foreach (EliminatedPacket e in Net.NewEliminations) OnEliminated(e);

            // Mobile networks drop out (tunnels, Wi-Fi to 4G): after a silence, rejoin from a new socket.
            if (Net.State == ConnectionState.Connected && Net.Result == null && now - Net.LastReceiveTime > SilenceBeforeReconnect &&
                now - _lastReconnectAttempt > SilenceBeforeReconnect)
            {
                _lastReconnectAttempt = now;
                Reconnects++;
                Net.Reconnect();
            }

            PlayerInputFrame frame = ReadInput();
            if (frame.PausePressed) Paused = !Paused;
            UpdateCursor();

            if (!Paused)
            {
                float sens = GameSettings.Sensitivity;
                ViewYaw = Mathf.Repeat(ViewYaw + frame.Look.x * sens, 360f);
                ViewPitch = Mathf.Clamp(ViewPitch - frame.Look.y * sens, -70f, 75f);
                _jumpLatched |= frame.JumpPressed;
                _reloadLatched |= frame.ReloadPressed;
                _interactLatched |= frame.InteractPressed;
                _healLatched |= frame.HealPressed;
                int activeSlot = Net.Latest?.SelfActiveSlot ?? 0;
                _switchLatched |= frame.SwitchPressed || (frame.SelectSlot >= 0 && frame.SelectSlot != activeSlot);
                if (frame.CrouchToggled) _crouchToggle = !_crouchToggle;
            }

            Interp.Advance(Time.deltaTime, _tickRate);
            if (_mapReady && Net.State == ConnectionState.Connected && Net.Result == null)
            {
                float dt = 1f / _tickRate;
                _accumulator += Mathf.Min(Time.deltaTime, 0.25f);
                while (_accumulator >= dt)
                {
                    _accumulator -= dt;
                    Tick(frame, now);
                }
            }

            if (Prediction != null)
            {
                Prediction.DecayOffset(Time.deltaTime);
                Touch.InteractAvailable = SelfAlive && NearestLoot() >= 0;
                _views.Render(Net.PlayerId, Prediction.RenderPosition, ViewYaw, Prediction.Predicted, Interp, Net.Latest, Time.time);
                MatchSounds();
            }
            Feed.RemoveAll(f => Time.time - f.Time > 5f);

            // After a platform match, read back the progression the server's signed result produced.
            if (Net.Result != null && Platform != null && Platform.SignedIn && !_profileRequested)
            {
                _profileRequested = true;
                RefreshProfileAfterMatch();
            }
        }

        private async void RefreshProfileAfterMatch()
        {
            // The server submits its result right after the match; give it a moment, then retry a few times.
            for (int attempt = 0; attempt < 5 && Platform != null; attempt++)
            {
                float until = Time.realtimeSinceStartup + 1.5f;
                while (Time.realtimeSinceStartup < until) await System.Threading.Tasks.Task.Yield();
                try
                {
                    ProfileDto p = await Platform.ProfileAsync();
                    if (ProfileBefore == null || p.xp != ProfileBefore.xp)
                    {
                        ProfileAfter = p;
                        return;
                    }
                }
                catch (ApiException) { }
            }
        }

        /// <summary>Footsteps for everyone we can see, and a periodic warning while outside the zone.</summary>
        private void MatchSounds()
        {
            GameAudio audio = GameAudio.Instance;
            foreach (ushort id in Interp.KnownPlayers())
            {
                SVector3 p;
                bool grounded, crouched, alive;
                if (id == Net.PlayerId)
                {
                    p = Prediction.Predicted.Position;
                    grounded = Prediction.Predicted.Grounded;
                    crouched = Prediction.Predicted.Crouched;
                    alive = SelfAlive;
                }
                else
                {
                    if (!Interp.TrySample(id, out p, out _, out SnapshotEntry e)) continue;
                    grounded = (e.Flags & PlayerFlags.Grounded) != 0;
                    crouched = (e.Flags & PlayerFlags.Crouched) != 0;
                    alive = (e.Flags & PlayerFlags.Alive) != 0;
                }
                if (alive && _footsteps.Step(id, new Vector3(p.X, p.Y, p.Z), grounded, crouched))
                    audio.PlayAt(Sfx.Footstep, new Vector3(p.X, p.Y, p.Z), id == Net.PlayerId ? 0.45f : 0.7f, 25f);
            }

            if (Self is { } me && Net.Latest is { } s && s.Phase == MatchPhase.Active && (me.Flags & PlayerFlags.Alive) != 0 &&
                new Vector2(me.X - s.ZoneX, me.Z - s.ZoneZ).magnitude > s.ZoneRadius && Time.time >= _nextZoneWarning)
            {
                _nextZoneWarning = Time.time + 2f;
                audio.PlayUi(Sfx.ZoneWarning);
            }
        }

        private void PrepareMap()
        {
            if (_world == null || !_world.MatchServerMap(Net.Welcome.MapVersion))
            {
                Error = $"This client cannot play server map \"{Net.Welcome.MapVersion}\"";
                Net.Leave();
                return;
            }
            Map = _world.Layout;
            _tickRate = Math.Max(1, (int)Net.Welcome.TickRate);
            Prediction = new ClientPrediction(Map, _tickRate);
            _views.Build(Map);
            MapLayout map = Map;
            GameAudio audio = GameAudio.Instance;
            audio.LineOfSight = (a, b) =>
            {
                Vector3 d = b - a;
                float len = d.magnitude;
                return len < 0.01f || Simulation.WallDistance(new SVector3(a.x, a.y, a.z), new SVector3(d.x / len, d.y / len, d.z / len), len, map) >= len - 0.05f;
            };
            audio.Indoors = p => Simulation.WallDistance(new SVector3(p.x, p.y, p.z), SVector3.UnitY, 12f, map) < 12f;
            RemoteShotFired += (id, at, w) => GameAudio.Instance.Gunshot(w, new Vector3(at.X, at.Y, at.Z), local: false);
            _mapReady = true;
        }

        private void Tick(PlayerInputFrame frame, double now)
        {
            _localTick++;
            InputCommand cmd = BuildCommand(frame);
            if (SelfAlive) Prediction.Apply(cmd);
            Net.SendInput(cmd, now);
            CosmeticShot(cmd);
        }

        private InputCommand BuildCommand(PlayerInputFrame frame)
        {
            float yaw = ViewYaw * Mathf.Deg2Rad, pitch = -ViewPitch * Mathf.Deg2Rad;
            if (Prediction != null && Prediction.Initialized) AimAngles(out yaw, out pitch);

            var buttons = InputButtons.None;
            if (frame.Fire && !Paused) buttons |= InputButtons.Fire;
            if (frame.Sprint) buttons |= InputButtons.Sprint;
            if (_crouchToggle) buttons |= InputButtons.Crouch;
            if (_jumpLatched) buttons |= InputButtons.Jump;
            if (_reloadLatched) buttons |= InputButtons.Reload;
            if (_interactLatched) buttons |= InputButtons.Interact;
            if (_healLatched) buttons |= InputButtons.Heal;
            if (_switchLatched) buttons |= InputButtons.SwitchWeapon;
            _switchLatched = false;
            _jumpLatched = _reloadLatched = _interactLatched = _healLatched = false;
            if ((buttons & InputButtons.Jump) != 0) _crouchToggle = false;

            // Same trick as practice mode: rotate the stick into the aim frame so movement follows the camera.
            Vector2 move = Paused ? Vector2.zero : frame.Move;
            float delta = ViewYaw * Mathf.Deg2Rad - yaw;
            float cos = Mathf.Cos(delta), sin = Mathf.Sin(delta);
            return new InputCommand
            {
                Sequence = ++_sequence,
                AckTick = Interp.ViewedTick, // what we see: lag-compensation target and delta baseline
                MoveX = move.x * cos + move.y * sin,
                MoveZ = -move.x * sin + move.y * cos,
                Yaw = yaw,
                Pitch = pitch,
                Buttons = buttons,
            };
        }

        private void AimAngles(out float yaw, out float pitch)
        {
            Quaternion view = Quaternion.Euler(ViewPitch, ViewYaw, 0);
            Vector3 camForward = view * Vector3.forward;
            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : CameraPivot - camForward * 3.6f;

            var origin = new SVector3(camPos.x, camPos.y, camPos.z);
            var dir = new SVector3(camForward.x, camForward.y, camForward.z);
            float t = Simulation.WallDistance(origin, dir, MaxAimDistance, Map);
            if (dir.Y < -1e-4f) t = Mathf.Min(t, -origin.Y / dir.Y);
            float? hit = RemoteHit(origin, dir, MaxAimDistance, out _);
            if (hit.HasValue && hit.Value < t) t = hit.Value;

            SVector3 d = origin + dir * Mathf.Max(t, 2f) - Prediction.Predicted.Eye;
            yaw = Mathf.Atan2(d.X, d.Z);
            pitch = Mathf.Atan2(d.Y, Mathf.Sqrt(d.X * d.X + d.Z * d.Z));
        }

        /// <summary>Ray against other players where we currently see them (interpolated).</summary>
        private float? RemoteHit(SVector3 origin, SVector3 dir, float range, out ushort id)
        {
            id = ProtocolInfo.NoPlayer;
            float? best = null;
            foreach (ushort other in Interp.KnownPlayers())
            {
                if (other == Net.PlayerId || !Interp.TrySample(other, out SVector3 p, out _, out SnapshotEntry e)) continue;
                if ((e.Flags & PlayerFlags.Alive) == 0) continue;
                float h = (e.Flags & PlayerFlags.Crouched) != 0 ? GameRules.CrouchHeight : GameRules.PlayerHeight;
                float? t = Simulation.RayCapsule(origin, dir, p, h, range);
                if (t.HasValue && (!best.HasValue || t.Value < best.Value))
                {
                    best = t;
                    id = other;
                }
            }
            return best;
        }

        /// <summary>Local tracer and recoil at the weapon's cadence; the server's result arrives in snapshots.</summary>
        private void CosmeticShot(InputCommand cmd)
        {
            if (!SelfAlive || (cmd.Buttons & InputButtons.Fire) == 0 || !(Self is { } me)) return;
            if (Net.Latest == null || Net.Latest.Phase != MatchPhase.Active) return; // the server only allows fire in play
            if ((me.Flags & PlayerFlags.HasWeapon) == 0 || (me.Flags & (PlayerFlags.Reloading | PlayerFlags.Healing)) != 0 || me.Ammo == 0) return;
            if (_localTick < _nextLocalShotTick) return;
            WeaponDefinition weapon = SelfWeapon;
            _nextLocalShotTick = _localTick + Math.Max(1, Combatant.Ticks(weapon.FireIntervalSeconds, _tickRate));

            MotorState m = Prediction.Predicted;
            bool moving = cmd.MoveX * cmd.MoveX + cmd.MoveZ * cmd.MoveZ > 0.01f;
            CurrentSpreadDegrees = weapon.SpreadFor(moving, m.Crouched, !m.Grounded);
            SVector3 origin = m.Eye;
            uint seed = Simulation.ShotSeed(Net.PlayerId, cmd.Sequence);
            for (int p = 0; p < Math.Max(1, weapon.Pellets); p++)
            {
                // Same pellet seeds as Ballistics.Fire, so the tracers follow the server's rays.
                SVector3 dir = Simulation.SpreadDirection(cmd.Yaw, cmd.Pitch, CurrentSpreadDegrees, p == 0 ? seed : seed + (uint)p * 0x9E3779B9u);
                float t = Simulation.WallDistance(origin, dir, weapon.Range, Map);
                float? hit = RemoteHit(origin, dir, weapon.Range, out _);
                if (hit.HasValue && hit.Value < t) t = hit.Value;
                _views.AddTracer(origin, origin + dir * t, Time.time);
            }
            _lastShotTime = Time.time;
            ShotsFiredLocally++;
            GameAudio.Instance.Gunshot(weapon, new Vector3(origin.X, origin.Y, origin.Z), local: true);
            ViewPitch -= weapon.RecoilPitchDegrees;
            ViewYaw += weapon.RecoilYawDegrees * (_recoilSign = -_recoilSign);
        }

        public int ShotsFiredLocally { get; private set; }
        public int RemoteShotsSeen { get; private set; }

        /// <summary>
        /// Other players' shots: a changed shot counter means they fired since the last snapshot. Drawn from where we
        /// see them (interpolated) along their facing, out to the wall or the weapon's range.
        /// </summary>
        private void RemoteShots(SnapshotPacket s)
        {
            foreach (SnapshotEntry e in s.Players)
            {
                if (_lastShotCounter.TryGetValue(e.PlayerId, out byte before) && before != e.ShotCounter && e.PlayerId != Net.PlayerId &&
                    WeaponCatalog.IsValid(e.Weapon) && Interp.TrySample(e.PlayerId, out SVector3 pos, out float yaw, out SnapshotEntry seen))
                {
                    int shots = Math.Min(3, (int)(byte)(e.ShotCounter - before)); // counter wraps at 256
                    WeaponDefinition w = WeaponCatalog.Get(e.Weapon);
                    float eye = (seen.Flags & PlayerFlags.Crouched) != 0 ? GameRules.CrouchEyeHeight : GameRules.EyeHeight;
                    SVector3 origin = pos + new SVector3(0, eye, 0);
                    for (int i = 0; i < shots; i++)
                    {
                        SVector3 dir = Simulation.SpreadDirection(yaw, 0, w.SpreadDegrees + 1f, (uint)(e.ShotCounter * 31 + i));
                        float range = Simulation.WallDistance(origin, dir, Math.Min(w.Range, 150f), Map);
                        _views.AddTracer(origin, origin + dir * range, Time.time);
                        RemoteShotsSeen++;
                        RemoteShotFired?.Invoke(e.PlayerId, origin, w);
                    }
                }
                _lastShotCounter[e.PlayerId] = e.ShotCounter;
            }
        }

        /// <summary>Raised for each remote shot (position, weapon): hooks sound and effects.</summary>
        public event Action<ushort, SVector3, WeaponDefinition> RemoteShotFired;

        private void TrackHealth(SnapshotPacket s)
        {
            foreach (SnapshotEntry e in s.Players)
            {
                if (_lastHealth.TryGetValue(e.PlayerId, out byte before) && e.Health < before)
                {
                    if (e.PlayerId == Net.PlayerId) LastDamagedTime = Time.time;
                    else if (Time.time - _lastShotTime < 0.4f)
                    {
                        LastHitTime = Time.time;
                        LastHitWasKill = false;
                        GameAudio.Instance.PlayUi(Sfx.HitMarker);
                    }
                }
                _lastHealth[e.PlayerId] = e.Health;
            }
        }

        private void OnEliminated(EliminatedPacket e)
        {
            string victim = e.VictimId == Net.PlayerId ? "You" : $"Player {e.VictimId}";
            string text = e.KillerId == ProtocolInfo.NoPlayer
                ? $"{victim} fell to the zone (#{e.Placement})"
                : e.KillerId == Net.PlayerId
                    ? $"You eliminated {victim} (#{e.Placement})"
                    : $"Player {e.KillerId} eliminated {victim} (#{e.Placement})";
            if (e.KillerId == Net.PlayerId)
            {
                LastHitTime = Time.time;
                LastHitWasKill = true;
                GameAudio.Instance.PlayUi(Sfx.Kill);
            }
            Feed.Add(new FeedEntry { Text = text, Time = Time.time });
        }

        public int NearestLoot()
        {
            if (Map == null || Net.Latest == null || Prediction == null) return -1;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Map.Loot.Count; i++)
            {
                if (Net.Latest.IsLootTaken(i) || !Simulation.CanReach(Prediction.Predicted, Map.Loot[i].Position)) continue;
                float d = SVector3.DistanceSquared(Prediction.Predicted.Position, Map.Loot[i].Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
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

        public static bool TryParseAddress(string address, out string host, out int port)
        {
            host = null;
            port = 0;
            int colon = address.LastIndexOf(':');
            if (colon <= 0) return false;
            host = address.Substring(0, colon);
            return int.TryParse(address.Substring(colon + 1), out port) && port > 0 && port < 65536;
        }

        private static string CommandLineServer() => CommandLineArg("-server");

        private static string CommandLineArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        private static string Load(string key, string fallback)
        {
            try { return PlayerPrefs.GetString(key, fallback); }
            catch (Exception) { return fallback; }
        }

        private static void Save(string key, string value)
        {
            try
            {
                PlayerPrefs.SetString(key, value);
                PlayerPrefs.Save();
            }
            catch (Exception)
            {
                // Storage unavailable: setting lasts for this session only.
            }
        }
    }
}
