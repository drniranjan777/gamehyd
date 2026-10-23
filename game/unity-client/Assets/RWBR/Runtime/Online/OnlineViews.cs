using System.Collections.Generic;
using RWBR.Client.Character;
using RWBR.Client.Combat;
using RWBR.Client.Practice;
using RWBR.Shared;
using RWBR.Shared.Protocol;
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
        private const int RingSegments = 96;

        private readonly Transform _root;
        private readonly Dictionary<ushort, GameObject> _bodies = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<ushort, CharacterBody> _characters = new Dictionary<ushort, CharacterBody>();
        private readonly Dictionary<ushort, float> _remoteAim = new Dictionary<ushort, float>();
        private readonly List<GameObject> _loot = new List<GameObject>();
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

        public void Build(MapLayout map)
        {
            Clear();
            _map = map;
            _lineMaterial = new Material(Shader.Find("Sprites/Default"));
            Effects = new CombatEffects(_root);
            for (int i = 0; i < map.Loot.Count; i++)
            {
                LootSpawn item = map.Loot[i];
                GameObject go = PracticeViews.CreatePrimitive(PrimitiveType.Cube, _root, $"Loot_{i:00}_{item.Type}", PracticeViews.LootColor(item.Type));
                go.transform.localScale = item.Type == LootType.Rifle ? new Vector3(0.9f, 0.18f, 0.18f) : Vector3.one * 0.4f;
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
        }

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

                    if (e.PlayerId == selfId)
                    {
                        root.transform.SetPositionAndRotation(ToUnity(selfPosition), Quaternion.Euler(0, selfYawDegrees, 0));
                        SelfTransform = root.transform;
                        _characters[e.PlayerId].SetHidden(_selfHidden);
                        Pose(e, selfMotor.Height, selfYawDegrees, _selfPitch, _selfAim, selfMotor.Grounded, selfMotor.Crouched);
                        UpdateWeapon(e, selfMotor.EyeHeight, _selfPitch, _selfAim, _selfHidden, time);
                        continue;
                    }

                    if (!interp.TrySample(e.PlayerId, out SVector3 p, out float yaw, out SnapshotEntry sampled)) continue;
                    root.transform.SetPositionAndRotation(ToUnity(p), Quaternion.Euler(0, yaw * Mathf.Rad2Deg, 0));
                    bool crouched = (sampled.Flags & PlayerFlags.Crouched) != 0;
                    float h = crouched ? GameRules.CrouchHeight : GameRules.PlayerHeight;
                    float pitch = RemotePitchDegrees(sampled);
                    float aim = RemoteAim(e.PlayerId, sampled);
                    Pose(e, h, yaw * Mathf.Rad2Deg, pitch, aim, (sampled.Flags & PlayerFlags.Grounded) != 0, crouched);
                    UpdateWeapon(sampled, crouched ? GameRules.CrouchEyeHeight : GameRules.EyeHeight, pitch, aim, false, time);
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
            }

            if (_map != null)
            {
                for (int i = 0; i < _loot.Count; i++)
                {
                    bool visible = latest == null || !latest.IsLootTaken(i);
                    if (_loot[i].activeSelf != visible) _loot[i].SetActive(visible);
                    if (!visible) continue;
                    Vector3 lp = ToUnity(_map.Loot[i].Position);
                    _loot[i].transform.SetPositionAndRotation(lp + Vector3.up * (0.45f + Mathf.Sin(time * 2f + i) * 0.08f),
                        Quaternion.Euler(0, time * 60f + i * 30f, 0));
                }
            }

            if (_zoneRing != null && latest != null)
            {
                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i * Mathf.PI * 2 / RingSegments;
                    _zoneRing.SetPosition(i, new Vector3(latest.ZoneX + Mathf.Sin(a) * latest.ZoneRadius, 0.6f,
                        latest.ZoneZ + Mathf.Cos(a) * latest.ZoneRadius));
                }
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
            foreach ((LineRenderer line, float _) in _tracers) Object.Destroy(line.gameObject);
            if (_zoneRing != null) Object.Destroy(_zoneRing.gameObject);
            if (Effects != null) Effects.Destroy();
            Effects = null;
            _bodies.Clear();
            _weapons.Clear();
            _characters.Clear();
            _remoteAim.Clear();
            _loot.Clear();
            _tracers.Clear();
            SelfTransform = null;
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

        private void Pose(SnapshotEntry e, float height, float yawDegrees, float pitch, float aim, bool grounded, bool crouched)
        {
            _characters[e.PlayerId].Pose(new CharacterMotion
            {
                ViewYawDegrees = yawDegrees,
                PitchDegrees = pitch,
                Grounded = grounded,
                Crouched = crouched,
                Alive = (e.Flags & PlayerFlags.Alive) != 0,
                Aim = aim,
                Reloading = (e.Flags & PlayerFlags.Reloading) != 0,
                Weapon = (e.Flags & PlayerFlags.HasWeapon) != 0 && WeaponCatalog.IsValid(e.Weapon) ? WeaponCatalog.Get(e.Weapon).Class : (WeaponClass?)null,
            }, height, _bodies[e.PlayerId].transform.eulerAngles.y, Time.deltaTime);
        }

        private void UpdateWeapon(SnapshotEntry e, float eyeHeight, float pitch, float aim, bool hidden, float time)
        {
            float reload = -1;
            if ((e.Flags & PlayerFlags.Reloading) != 0)
            {
                // Snapshots say "reloading" but not how far; animate a loop at the weapon's reload length.
                float length = WeaponCatalog.IsValid(e.Weapon) ? WeaponCatalog.Get(e.Weapon).ReloadSeconds : 2f;
                reload = Mathf.Repeat(time / length, 1f);
            }
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
