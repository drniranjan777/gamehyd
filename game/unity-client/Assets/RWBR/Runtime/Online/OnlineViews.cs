using System.Collections.Generic;
using RWBR.Client.Combat;
using RWBR.Client.Practice;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Online
{
    /// <summary>
    /// Presentation for an online match: predicted self, interpolated others (placeholder capsules holding 3D weapon
    /// models), loot, tracers, shot effects and the zone ring.
    /// </summary>
    public sealed class OnlineViews
    {
        private static readonly Color SelfColor = new Color(0.2f, 0.5f, 0.95f);
        private static readonly Color OtherColor = new Color(0.85f, 0.2f, 0.2f);
        private static readonly Color DeadColor = new Color(0.25f, 0.25f, 0.25f);
        private const int RingSegments = 96;

        private readonly Transform _root;
        private readonly Dictionary<ushort, GameObject> _bodies = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<ushort, bool> _deadShown = new Dictionary<ushort, bool>();
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
        private Renderer[] _selfRenderers;

        public CombatEffects Effects { get; private set; }

        public WeaponHolder WeaponOf(ushort id) => _weapons.TryGetValue(id, out WeaponHolder h) ? h : null;

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
                    bool alive = (e.Flags & PlayerFlags.Alive) != 0;

                    if (e.PlayerId == selfId)
                    {
                        root.transform.SetPositionAndRotation(ToUnity(selfPosition), Quaternion.Euler(0, selfYawDegrees, 0));
                        Pose(root, alive, selfMotor.Height, SelfColor, e.PlayerId);
                        SelfTransform = root.transform;
                        _selfRenderers ??= root.transform.GetChild(0).GetComponentsInChildren<Renderer>();
                        foreach (Renderer r in _selfRenderers)
                            if (r.enabled == _selfHidden) r.enabled = !_selfHidden;
                        UpdateWeapon(e, selfMotor.EyeHeight, _selfPitch, _selfAim, _selfHidden, time);
                        continue;
                    }

                    if (!interp.TrySample(e.PlayerId, out SVector3 p, out float yaw, out SnapshotEntry sampled)) continue;
                    root.transform.SetPositionAndRotation(ToUnity(p), Quaternion.Euler(0, yaw * Mathf.Rad2Deg, 0));
                    float h = (sampled.Flags & PlayerFlags.Crouched) != 0 ? GameRules.CrouchHeight : GameRules.PlayerHeight;
                    Pose(root, alive, h, OtherColor, e.PlayerId);
                    UpdateWeapon(sampled, (sampled.Flags & PlayerFlags.Crouched) != 0 ? GameRules.CrouchEyeHeight : GameRules.EyeHeight, 0, 0, false, time);
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
                _deadShown.Remove(id);
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
            _selfRenderers = null;
            _deadShown.Clear();
            _loot.Clear();
            _tracers.Clear();
            SelfTransform = null;
        }

        private GameObject Body(ushort id, bool self)
        {
            if (_bodies.TryGetValue(id, out GameObject root)) return root;
            root = new GameObject(self ? "You" : $"Player {id}");
            root.transform.SetParent(_root, false);
            GameObject body = PracticeViews.CreatePrimitive(PrimitiveType.Capsule, root.transform, "Body", self ? SelfColor : OtherColor);
            GameObject visor = PracticeViews.CreatePrimitive(PrimitiveType.Cube, body.transform, "Visor", Color.black);
            visor.transform.localPosition = new Vector3(0, 0.55f, 0.42f);
            visor.transform.localScale = new Vector3(0.7f, 0.12f, 0.2f);
            _bodies[id] = root;
            _weapons[id] = new WeaponHolder(root.transform);
            return root;
        }

        private void Pose(GameObject root, bool alive, float height, Color color, ushort id)
        {
            Transform body = root.transform.GetChild(0);
            _deadShown.TryGetValue(id, out bool shownDead);
            if (alive)
            {
                body.localPosition = new Vector3(0, height / 2, 0);
                body.localScale = new Vector3(0.8f, height / 2, 0.8f);
                body.localRotation = Quaternion.identity;
                if (shownDead) PracticeViews.SetColor(body.gameObject, color);
                _deadShown[id] = false;
            }
            else
            {
                body.localPosition = new Vector3(0, 0.4f, 0);
                body.localRotation = Quaternion.Euler(90, 0, 0);
                if (!shownDead) PracticeViews.SetColor(body.gameObject, DeadColor);
                _deadShown[id] = true;
            }
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
