using System.Collections.Generic;
using RWBR.Client.Character;
using RWBR.Client.Combat;
using RWBR.Client.Practice;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using RWBR.Shared.Server;
using RWBR.Client.Audio;
using RWBR.Client.Settings;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Online
{
    /// <summary>
    /// Presentation for an online match: predicted self, interpolated others (the rigged character once built, else
    /// placeholder capsules, holding 3D weapon models), loot, tracers, shot effects and the zone ring.
    /// </summary>
    public sealed class OnlineViews
    {
        private static readonly Color SelfColor = new Color(0.2f, 0.5f, 0.95f);
        private static readonly Color OtherColor = new Color(0.85f, 0.2f, 0.2f);
        private static readonly Color TeammateColor = new Color(0.2f, 0.8f, 0.45f);
        private readonly HashSet<ushort> _coloredMates = new HashSet<ushort>();
        private const int RingSegments = 96;

        private readonly Transform _root;
        private readonly Dictionary<ushort, GameObject> _bodies = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<ushort, CharacterBody> _characters = new Dictionary<ushort, CharacterBody>();
        private readonly Dictionary<ushort, float> _remoteAim = new Dictionary<ushort, float>();
        private readonly ReloadTracker _reloads = new ReloadTracker();
        private readonly List<GameObject> _loot = new List<GameObject>();
        private readonly Dictionary<ushort, GameObject> _ground = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<ushort, (GameObject Go, ProjectileState State)> _projectiles = new Dictionary<ushort, (GameObject, ProjectileState)>();
        private readonly List<(GameObject Go, float Born)> _blasts = new List<(GameObject, float)>();
        private readonly List<ushort> _gone = new List<ushort>();
        private Material _fxMaterial;

        /// <summary>Explosions shown so far (tests and diagnostics).</summary>
        public int ExplosionsShown { get; private set; }
        public int GroundItemsShown => _ground.Count;
        public int SmokeCloudsShown
        {
            get
            {
                int n = 0;
                foreach (var p in _projectiles.Values) n += p.State == ProjectileState.Cloud ? 1 : 0;
                return n;
            }
        }
        private readonly List<(LineRenderer Line, float Until)> _tracers = new List<(LineRenderer, float)>();
        private readonly HashSet<ushort> _seen = new HashSet<ushort>();
        private Material _lineMaterial;
        private LineRenderer _zoneRing;
        private MapLayout _map;
        private readonly Dictionary<ushort, WeaponHolder> _weapons = new Dictionary<ushort, WeaponHolder>();
        private float _selfPitch, _selfAim;
        private bool _selfHidden;
        private int _tickRate = 30;

        public CombatEffects Effects { get; private set; }

        public WeaponHolder WeaponOf(ushort id) => _weapons.TryGetValue(id, out WeaponHolder h) ? h : null;
        public CharacterBody CharacterOf(ushort id) => _characters.TryGetValue(id, out CharacterBody b) ? b : null;

        /// <summary>The local player's view pitch (degrees), aim blend, and whether the body is hidden (scoped).</summary>
        public void SetSelfView(float pitchDegrees, float aim, bool hidden, int tickRate)
        {
            _selfPitch = pitchDegrees;
            _selfAim = aim;
            _selfHidden = hidden;
            _tickRate = tickRate;
        }

        public OnlineViews(Transform root) => _root = root;

        public Transform SelfTransform { get; private set; }

        public bool TryGetBody(ushort id, out Transform t)
        {
            t = _bodies.TryGetValue(id, out GameObject go) ? go.transform : null;
            return t != null;
        }

        public void Build(MapLayout map) => Build(map, map.Loot);

        /// <summary>Builds the views for a map and the match's loot spots (its loot table).</summary>
        public void Build(MapLayout map, IReadOnlyList<LootSpawn> loot)
        {
            Clear();
            _map = map;
            _lineMaterial = new Material(Shader.Find("Sprites/Default"));
            _fxMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100 };
            Effects = new CombatEffects(_root);
            _lootSpots = loot;
            for (int i = 0; i < loot.Count; i++)
            {
                LootSpawn item = loot[i];
                GameObject go = PracticeViews.CreatePrimitive(PrimitiveType.Cube, _root, $"Loot_{i:00}_{item.Kind}", PracticeViews.LootColor(item.Kind));
                go.transform.localScale = PracticeViews.LootScale(item.Kind);
                _loot.Add(go);
            }

            var ringGo = new GameObject("ZoneRing");
            ringGo.transform.SetParent(_root, false);
            _zoneRing = ringGo.AddComponent<LineRenderer>();
            _zoneRing.sharedMaterial = _lineMaterial;
            _zoneRing.loop = true;
            _zoneRing.positionCount = RingSegments;
            _zoneRing.widthMultiplier = 0.5f;
            _zoneRing.startColor = _zoneRing.endColor = new Color(0.3f, 0.6f, 1f, 0.8f);
            _zoneRing.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Where the zone is closing to: a thin white ring.
            var nextGo = new GameObject("NextZoneRing");
            nextGo.transform.SetParent(_root, false);
            _nextRing = nextGo.AddComponent<LineRenderer>();
            _nextRing.sharedMaterial = _lineMaterial;
            _nextRing.loop = true;
            _nextRing.positionCount = RingSegments;
            _nextRing.widthMultiplier = 0.3f;
            _nextRing.startColor = _nextRing.endColor = new Color(1f, 1f, 1f, 0.7f);
            _nextRing.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private LineRenderer _nextRing;
        private GameObject _beacon;
        private readonly Dictionary<ushort, GameObject> _chutes = new Dictionary<ushort, GameObject>();
        private Transform _plane;
        private AudioSource _planeEngine;

        public bool PlaneShown => _plane != null && _plane.gameObject.activeSelf;
        public int ParachutesShown
        {
            get
            {
                int n = 0;
                foreach (GameObject c in _chutes.Values) n += c.activeSelf ? 1 : 0;
                return n;
            }
        }

        /// <summary>A canopy over someone under a parachute (placeholder shape until the art exists).</summary>
        private void ShowParachute(ushort id, Transform body, bool open)
        {
            _chutes.TryGetValue(id, out GameObject chute);
            if (open && chute == null)
            {
                chute = PracticeViews.CreatePrimitive(PrimitiveType.Sphere, body, "Parachute", new Color(0.95f, 0.55f, 0.15f));
                chute.transform.localPosition = new Vector3(0, 5.2f, 0);
                chute.transform.localScale = new Vector3(5f, 1.2f, 4f);
                chute.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _chutes[id] = chute;
            }
            if (chute != null && chute.activeSelf != open) chute.SetActive(open);
        }

        /// <summary>The drop plane while it flies: a placeholder airframe with an engine drone.</summary>
        private void SyncPlane(RemoteInterpolator interp)
        {
            bool flying = interp.TrySamplePlane(out SVector3 at, out float yaw);
            if (flying && _plane == null)
            {
                _plane = new GameObject("DropPlane").transform;
                _plane.SetParent(_root, false);
                var grey = new Color(0.55f, 0.58f, 0.6f);
                GameObject body = PracticeViews.CreatePrimitive(PrimitiveType.Cube, _plane, "Fuselage", grey);
                body.transform.localScale = new Vector3(4f, 4f, 26f);
                GameObject wings = PracticeViews.CreatePrimitive(PrimitiveType.Cube, _plane, "Wings", grey);
                wings.transform.localScale = new Vector3(30f, 0.5f, 5f);
                GameObject tail = PracticeViews.CreatePrimitive(PrimitiveType.Cube, _plane, "Tail", grey);
                tail.transform.localPosition = new Vector3(0, 3f, -11f);
                tail.transform.localScale = new Vector3(0.5f, 5f, 4f);
                _planeEngine = _plane.gameObject.AddComponent<AudioSource>();
                _planeEngine.clip = GameAudio.EngineClip();
                _planeEngine.loop = true;
                _planeEngine.pitch = 0.45f;
                _planeEngine.spatialBlend = 1f;
                _planeEngine.minDistance = 30f;
                _planeEngine.maxDistance = 600f;
                _planeEngine.dopplerLevel = 0;
            }
            if (_plane == null) return;
            if (_plane.gameObject.activeSelf != flying) _plane.gameObject.SetActive(flying);
            if (!flying) return;
            _plane.SetPositionAndRotation(ToUnity(at) + Vector3.up * 2f, Quaternion.Euler(0, yaw * Mathf.Rad2Deg, 0));
            _planeEngine.volume = 0.6f * GameSettings.SfxVolume;
            if (!_planeEngine.isPlaying) _planeEngine.Play();
        }
        private readonly Dictionary<byte, CarView> _cars = new Dictionary<byte, CarView>();

        public int CarsShown => _cars.Count;
        public Transform CarTransform(byte id) => _cars.TryGetValue(id, out CarView c) ? c.Root : null;

        /// <summary>A car: placeholder box body, cabin and wheels until a licensed or original car model exists.</summary>
        private sealed class CarView
        {
            public Transform Root;
            public Transform[] Wheels;
            public Renderer[] Paint;
            public AudioSource Engine;
            public bool Wrecked;
            public float WheelSpin;
        }

        private static readonly Color[] CarPaint =
        {
            new Color(0.85f, 0.75f, 0.2f), new Color(0.2f, 0.45f, 0.75f), new Color(0.8f, 0.8f, 0.82f), new Color(0.15f, 0.55f, 0.35f),
            new Color(0.7f, 0.2f, 0.2f),
        };

        private CarView NewCar(byte id)
        {
            var root = new GameObject($"Car_{id}").transform;
            root.SetParent(_root, false);
            Color paint = CarPaint[id % CarPaint.Length];
            GameObject body = PracticeViews.CreatePrimitive(PrimitiveType.Cube, root, "Body", paint);
            body.transform.localPosition = new Vector3(0, 0.75f, 0);
            body.transform.localScale = new Vector3(VehicleSim.Width, 0.7f, VehicleSim.Length);
            GameObject cabin = PracticeViews.CreatePrimitive(PrimitiveType.Cube, root, "Cabin", new Color(0.25f, 0.3f, 0.35f));
            cabin.transform.localPosition = new Vector3(0, 1.3f, -0.2f);
            cabin.transform.localScale = new Vector3(VehicleSim.Width * 0.9f, 0.5f, VehicleSim.Length * 0.5f);
            var wheels = new Transform[4];
            for (int i = 0; i < 4; i++)
            {
                GameObject w = PracticeViews.CreatePrimitive(PrimitiveType.Cylinder, root, $"Wheel{i}", new Color(0.1f, 0.1f, 0.1f));
                w.transform.localPosition = new Vector3(i % 2 == 0 ? -0.95f : 0.95f, 0.38f, i < 2 ? 1.4f : -1.4f);
                w.transform.localRotation = Quaternion.Euler(0, 0, 90);
                w.transform.localScale = new Vector3(0.76f, 0.15f, 0.76f);
                wheels[i] = w.transform;
            }
            AudioSource engine = root.gameObject.AddComponent<AudioSource>();
            engine.clip = GameAudio.EngineClip();
            engine.loop = true;
            engine.spatialBlend = 1f;
            engine.rolloffMode = AudioRolloffMode.Logarithmic;
            engine.minDistance = 4f;
            engine.maxDistance = 120f;
            engine.dopplerLevel = 0;
            engine.volume = 0;
            return new CarView { Root = root, Wheels = wheels, Paint = new[] { body.GetComponent<Renderer>() }, Engine = engine };
        }

        /// <summary>Cars at their interpolated positions; wheels turn with speed; engine pitch and volume follow speed.</summary>
        private void SyncCars(RemoteInterpolator interp, SnapshotPacket latest, float dt)
        {
            foreach (VehicleEntry known in latest.Vehicles)
            {
                if (!_cars.TryGetValue(known.Id, out CarView car)) _cars[known.Id] = car = NewCar(known.Id);
                if (!interp.TrySampleVehicle(known.Id, out SVector3 p, out float yaw, out VehicleEntry v)) continue;
                car.Root.SetPositionAndRotation(ToUnity(p), Quaternion.Euler(0, yaw * Mathf.Rad2Deg, 0));
                car.WheelSpin += v.Speed / 0.38f * Mathf.Rad2Deg * dt;
                foreach (Transform w in car.Wheels) w.localRotation = Quaternion.Euler(car.WheelSpin, 0, 90);
                bool wrecked = v.Health == 0;
                if (wrecked != car.Wrecked)
                {
                    car.Wrecked = wrecked;
                    PracticeViews.SetColor(car.Paint[0].gameObject, wrecked ? new Color(0.15f, 0.13f, 0.12f) : CarPaint[known.Id % CarPaint.Length]);
                }
                bool running = !wrecked && (v.Driver != ProtocolInfo.NoPlayer || Mathf.Abs(v.Speed) > 0.3f);
                float load = Mathf.Clamp01(Mathf.Abs(v.Speed) / VehicleSim.MaxForwardSpeed);
                car.Engine.pitch = 0.6f + load * 1.2f;
                car.Engine.volume = running ? (0.25f + 0.45f * load) * GameSettings.SfxVolume : 0f;
                if (running && !car.Engine.isPlaying) car.Engine.Play();
                else if (!running && car.Engine.isPlaying) car.Engine.Stop();
            }
        }

        /// <summary>Your map marker as a tall yellow beacon in the world (null removes it).</summary>
        public void SetMarker(Vector3? at)
        {
            if (!at.HasValue)
            {
                if (_beacon != null) Object.Destroy(_beacon);
                _beacon = null;
                return;
            }
            if (_beacon == null)
            {
                _beacon = PracticeViews.CreatePrimitive(PrimitiveType.Cylinder, _root, "MarkerBeacon", new Color(1f, 0.85f, 0.1f, 0.45f));
                var r = _beacon.GetComponent<Renderer>();
                r.sharedMaterial = _fxMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                PracticeViews.SetColor(_beacon, new Color(1f, 0.85f, 0.1f, 0.45f));
                _beacon.transform.localScale = new Vector3(0.8f, 60f, 0.8f);
            }
            _beacon.transform.position = at.Value + Vector3.up * 60f;
        }

        public bool MarkerShown => _beacon != null;
        private IReadOnlyList<LootSpawn> _lootSpots;

        public void Render(ushort selfId, SVector3 selfPosition, float selfYawDegrees, MotorState selfMotor,
            RemoteInterpolator interp, SnapshotPacket latest, float time)
        {
            _seen.Clear();
            if (latest != null)
            {
                foreach (SnapshotEntry e in latest.Players)
                {
                    _seen.Add(e.PlayerId);
                    GameObject root = Body(e.PlayerId, e.PlayerId == selfId);
                    // Teammates in their own colour (set once, when they're first known to be on your team).
                    if (latest.IsTeammate(e.PlayerId) && _coloredMates.Add(e.PlayerId)) _characters[e.PlayerId].SetColor(TeammateColor);

                    if (e.PlayerId == selfId)
                    {
                        root.transform.SetPositionAndRotation(ToUnity(selfPosition), Quaternion.Euler(0, selfYawDegrees, 0));
                        SelfTransform = root.transform;
                        _characters[e.PlayerId].SetHidden(_selfHidden || (e.Flags & PlayerFlags.InPlane) != 0);
                        ShowParachute(e.PlayerId, root.transform, (e.Flags & PlayerFlags.Parachute) != 0);
                        float selfReload = ReloadProgress(e, time);
                        Pose(e, selfMotor.Height, selfYawDegrees, _selfPitch, _selfAim, selfReload, selfMotor.Grounded, selfMotor.Crouched);
                        UpdateWeapon(e, selfMotor.EyeHeight, _selfPitch, _selfAim, selfReload, _selfHidden, time);
                        continue;
                    }

                    if (!interp.TrySample(e.PlayerId, out SVector3 p, out float yaw, out SnapshotEntry sampled)) continue;
                    root.transform.SetPositionAndRotation(ToUnity(p), Quaternion.Euler(0, yaw * Mathf.Rad2Deg, 0));
                    _characters[e.PlayerId].SetHidden((sampled.Flags & PlayerFlags.InPlane) != 0);
                    ShowParachute(e.PlayerId, root.transform, (sampled.Flags & PlayerFlags.Parachute) != 0);
                    bool crouched = (sampled.Flags & PlayerFlags.Crouched) != 0;
                    float h = crouched ? GameRules.CrouchHeight : GameRules.PlayerHeight;
                    float pitch = RemotePitchDegrees(sampled);
                    float aim = RemoteAim(e.PlayerId, sampled);
                    float reload = ReloadProgress(sampled, time);
                    Pose(e, h, yaw * Mathf.Rad2Deg, pitch, aim, reload, (sampled.Flags & PlayerFlags.Grounded) != 0, crouched);
                    UpdateWeapon(sampled, crouched ? GameRules.CrouchEyeHeight : GameRules.EyeHeight, pitch, aim, reload, false, time);
                }
            }

            // Players that left the snapshot (removed before the match started).
            var gone = new List<ushort>();
            foreach (ushort id in _bodies.Keys)
                if (!_seen.Contains(id)) gone.Add(id);
            foreach (ushort id in gone)
            {
                Object.Destroy(_bodies[id]);
                _bodies.Remove(id);
                _weapons.Remove(id);
                _characters.Remove(id);
                _remoteAim.Remove(id);
                _reloads.Forget(id);
            }

            if (_map != null)
            {
                for (int i = 0; i < _loot.Count; i++)
                {
                    bool visible = latest == null || !latest.IsLootTaken(i);
                    if (_loot[i].activeSelf != visible) _loot[i].SetActive(visible);
                    if (!visible) continue;
                    Vector3 lp = ToUnity(_lootSpots[i].Position);
                    _loot[i].transform.SetPositionAndRotation(lp + Vector3.up * (0.45f + Mathf.Sin(time * 2f + i) * 0.08f),
                        Quaternion.Euler(0, time * 60f + i * 30f, 0));
                }
            }

            if (latest != null)
            {
                SyncGround(latest, time);
                SyncProjectiles(latest, time);
                SyncCars(interp, latest, Time.deltaTime);
                SyncPlane(interp);
            }
            UpdateBlasts(time);

            if (_zoneRing != null && latest != null)
            {
                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i * Mathf.PI * 2 / RingSegments;
                    _zoneRing.SetPosition(i, new Vector3(latest.ZoneX + Mathf.Sin(a) * latest.ZoneRadius, 0.6f,
                        latest.ZoneZ + Mathf.Cos(a) * latest.ZoneRadius));
                    _nextRing.SetPosition(i, new Vector3(latest.NextZoneX + Mathf.Sin(a) * latest.NextZoneRadius, 0.5f,
                        latest.NextZoneZ + Mathf.Cos(a) * latest.NextZoneRadius));
                }
                bool closing = latest.NextZoneRadius < latest.ZoneRadius - 0.5f;
                if (_nextRing.enabled != closing) _nextRing.enabled = closing;
            }

            Effects?.Update(time);
            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                if (time < _tracers[i].Until) continue;
                Object.Destroy(_tracers[i].Line.gameObject);
                _tracers.RemoveAt(i);
            }
        }

        /// <summary>A shot tracer from <paramref name="start"/> (the muzzle) or, without one, from just ahead of the eye.</summary>
        public void AddTracer(SVector3 from, SVector3 to, float time, Vector3? start = null)
        {
            Vector3 a = ToUnity(from), b = ToUnity(to);
            Vector3 dir = (b - a).normalized;
            if (start.HasValue)
            {
                a = start.Value;
                dir = Vector3.zero;
            }
            var go = new GameObject("Tracer");
            go.transform.SetParent(_root, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = _lineMaterial;
            line.positionCount = 2;
            line.SetPosition(0, a + dir * 0.6f);
            line.SetPosition(1, b);
            line.widthMultiplier = 0.03f;
            line.startColor = new Color(1f, 0.9f, 0.4f, 0.9f);
            line.endColor = new Color(1f, 0.9f, 0.4f, 0.2f);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _tracers.Add((line, time + 0.07f));
        }

        public void Clear()
        {
            foreach (GameObject go in _bodies.Values) Object.Destroy(go);
            foreach (GameObject go in _loot) Object.Destroy(go);
            foreach (GameObject go in _ground.Values) Object.Destroy(go);
            foreach (var p in _projectiles.Values) Object.Destroy(p.Go);
            foreach (var b in _blasts) Object.Destroy(b.Go);
            _ground.Clear();
            _projectiles.Clear();
            _blasts.Clear();
            foreach ((LineRenderer line, float _) in _tracers) Object.Destroy(line.gameObject);
            if (_zoneRing != null) Object.Destroy(_zoneRing.gameObject);
            if (_nextRing != null) Object.Destroy(_nextRing.gameObject);
            if (_beacon != null) Object.Destroy(_beacon);
            _beacon = null;
            foreach (CarView c in _cars.Values) Object.Destroy(c.Root.gameObject);
            _cars.Clear();
            _chutes.Clear(); // children of the bodies, destroyed with them
            if (_plane != null) Object.Destroy(_plane.gameObject);
            _plane = null;
            if (Effects != null) Effects.Destroy();
            Effects = null;
            _bodies.Clear();
            _weapons.Clear();
            _characters.Clear();
            _remoteAim.Clear();
            _reloads.Clear();
            _coloredMates.Clear();
            _loot.Clear();
            _tracers.Clear();
            SelfTransform = null;
        }

        /// <summary>Dropped items near you, as floating boxes like map loot.</summary>
        private void SyncGround(SnapshotPacket latest, float time)
        {
            _seenItems.Clear();
            foreach (GroundItemEntry g in latest.Ground)
            {
                _seenItems.Add(g.Id);
                if (!_ground.TryGetValue(g.Id, out GameObject go))
                {
                    go = PracticeViews.CreatePrimitive(PrimitiveType.Cube, _root, $"Ground_{g.Id}_{g.Kind}", PracticeViews.LootColor(g.Kind));
                    go.transform.localScale = PracticeViews.LootScale(g.Kind) * 0.85f;
                    _ground[g.Id] = go;
                }
                go.transform.SetPositionAndRotation(new Vector3(g.X, g.Y + 0.35f + Mathf.Sin(time * 2f + g.Id) * 0.05f, g.Z),
                    Quaternion.Euler(0, time * 45f + g.Id * 37f, 0));
            }
            _gone.Clear();
            foreach (ushort id in _ground.Keys)
                if (!_seenItems.Contains(id)) _gone.Add(id);
            foreach (ushort id in _gone)
            {
                Object.Destroy(_ground[id]);
                _ground.Remove(id);
            }
        }

        private readonly HashSet<ushort> _seenItems = new HashSet<ushort>();

        /// <summary>Grenades in flight, the flash and sound when a frag goes off, and smoke clouds.</summary>
        private void SyncProjectiles(SnapshotPacket latest, float time)
        {
            _seenItems.Clear();
            foreach (ProjectileEntry p in latest.Projectiles)
            {
                _seenItems.Add(p.Id);
                var at = new Vector3(p.X, p.Y, p.Z);
                if (!_projectiles.TryGetValue(p.Id, out var known))
                {
                    GameObject g = PracticeViews.CreatePrimitive(PrimitiveType.Sphere, _root, $"Grenade_{p.Id}", PracticeViews.LootColor(p.Kind));
                    g.transform.localScale = Vector3.one * 0.18f;
                    g.transform.position = at;
                    known = (g, ProjectileState.Flying);
                }
                if (p.State != known.State)
                {
                    if (p.State == ProjectileState.Exploded) Blast(at, time);
                    if (p.State == ProjectileState.Cloud) MakeCloud(known.Go);
                    known.State = p.State;
                }
                if (p.State == ProjectileState.Flying)
                    known.Go.transform.position = Vector3.Lerp(known.Go.transform.position, at, Mathf.Clamp01(Time.deltaTime * 20f));
                else if (p.State == ProjectileState.Cloud)
                    known.Go.transform.position = at + Vector3.up * 1.5f;
                else if (known.Go.activeSelf) known.Go.SetActive(false);
                _projectiles[p.Id] = known;
            }
            _gone.Clear();
            foreach (ushort id in _projectiles.Keys)
                if (!_seenItems.Contains(id)) _gone.Add(id);
            foreach (ushort id in _gone)
            {
                Object.Destroy(_projectiles[id].Go);
                _projectiles.Remove(id);
            }
        }

        private void MakeCloud(GameObject g)
        {
            var r = g.GetComponent<Renderer>();
            r.sharedMaterial = _fxMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            PracticeViews.SetColor(g, new Color(0.78f, 0.78f, 0.8f, 0.92f));
            g.transform.localScale = Vector3.one * Match.SmokeRadius * 2f;
        }

        private void Blast(Vector3 at, float time)
        {
            ExplosionsShown++;
            GameObject flash = PracticeViews.CreatePrimitive(PrimitiveType.Sphere, _root, "Explosion", new Color(1f, 0.6f, 0.2f, 0.8f));
            var r = flash.GetComponent<Renderer>();
            r.sharedMaterial = _fxMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            PracticeViews.SetColor(flash, new Color(1f, 0.6f, 0.2f, 0.8f));
            flash.transform.position = at + Vector3.up * 0.4f;
            _blasts.Add((flash, time));
            GameAudio.Instance.PlayAt(Sfx.Explosion, at, 1f, 250f);
        }

        private void UpdateBlasts(float time)
        {
            for (int i = _blasts.Count - 1; i >= 0; i--)
            {
                float t = (time - _blasts[i].Born) / 0.45f;
                if (t >= 1f)
                {
                    Object.Destroy(_blasts[i].Go);
                    _blasts.RemoveAt(i);
                    continue;
                }
                _blasts[i].Go.transform.localScale = Vector3.one * Mathf.Lerp(1f, Match.FragRadius * 2f, Mathf.Sqrt(t));
                PracticeViews.SetColor(_blasts[i].Go, new Color(1f, 0.6f - 0.4f * t, 0.2f, 0.8f * (1f - t)));
            }
        }

        private GameObject Body(ushort id, bool self)
        {
            if (_bodies.TryGetValue(id, out GameObject root)) return root;
            root = new GameObject(self ? "You" : $"Player {id}");
            root.transform.SetParent(_root, false);
            CharacterBody body = CharacterBody.Create(root.transform, self ? SelfColor : OtherColor);
            var weapon = new WeaponHolder(root.transform);
            body.Bind(weapon);
            _bodies[id] = root;
            _weapons[id] = weapon;
            _characters[id] = body;
            return root;
        }

        /// <summary>Another player's view pitch for display: degrees, positive down (the wire is radians, positive up).</summary>
        public static float RemotePitchDegrees(SnapshotEntry e) => -e.Pitch * Mathf.Rad2Deg;

        /// <summary>Another player's aim blend, eased like your own (the wire only says aiming or not).</summary>
        private float RemoteAim(ushort id, SnapshotEntry e)
        {
            _remoteAim.TryGetValue(id, out float aim);
            float target = (e.Flags & PlayerFlags.Aiming) != 0 ? 1f : 0f;
            aim = Mathf.MoveTowards(aim, target, Time.deltaTime / RWBR.Client.Player.AimState.BlendSeconds);
            _remoteAim[id] = aim;
            return aim;
        }

        /// <summary>Eased aim blend last shown for a player (tests and diagnostics).</summary>
        public float AimShownFor(ushort id) => _remoteAim.TryGetValue(id, out float a) ? a : 0f;

        /// <summary>Reload progress 0..1 for a player's entry, -1 when not reloading.</summary>
        private float ReloadProgress(SnapshotEntry e, float time)
        {
            float length = WeaponCatalog.IsValid(e.Weapon) ? WeaponCatalog.Get(e.Weapon).ReloadSeconds : 2f;
            return _reloads.Progress(e.PlayerId, (e.Flags & PlayerFlags.Reloading) != 0, length, time);
        }

        private void Pose(SnapshotEntry e, float height, float yawDegrees, float pitch, float aim, float reload, bool grounded, bool crouched)
        {
            _characters[e.PlayerId].Pose(new CharacterMotion
            {
                ViewYawDegrees = yawDegrees,
                PitchDegrees = pitch,
                Grounded = grounded,
                Crouched = crouched,
                Alive = (e.Flags & PlayerFlags.Alive) != 0,
                Aim = aim,
                Reloading = reload >= 0,
                ReloadProgress = Mathf.Max(reload, 0f),
                Weapon = (e.Flags & PlayerFlags.HasWeapon) != 0 && WeaponCatalog.IsValid(e.Weapon) ? WeaponCatalog.Get(e.Weapon).Class : (WeaponClass?)null,
            }, height, _bodies[e.PlayerId].transform.eulerAngles.y, Time.deltaTime);
        }

        private void UpdateWeapon(SnapshotEntry e, float eyeHeight, float pitch, float aim, float reload, bool hidden, float time)
        {
            _weapons[e.PlayerId].Update(new WeaponPose
            {
                Weapon = (e.Flags & PlayerFlags.HasWeapon) != 0 ? e.Weapon : -1,
                PitchDegrees = pitch,
                Aim = aim,
                Reload = reload,
                Alive = (e.Flags & PlayerFlags.Alive) != 0,
                Crouched = (e.Flags & PlayerFlags.Crouched) != 0,
                Hidden = hidden,
            }, eyeHeight, time, Time.deltaTime);
        }

        private static Vector3 ToUnity(SVector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
