using System.Collections.Generic;
using RWBR.Client.Combat;
using RWBR.Shared;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Practice
{
    public static class VectorConversions
    {
        public static Vector3 ToUnity(this SVector3 v) => new Vector3(v.X, v.Y, v.Z);
        public static SVector3 ToShared(this Vector3 v) => new SVector3(v.x, v.y, v.z);
    }

    /// <summary>
    /// Presentation for the practice simulation: placeholder capsule characters holding 3D weapon models, loot,
    /// shot tracers and pooled shot effects.
    /// </summary>
    public sealed class PracticeViews
    {
        private static readonly Color PlayerColor = new Color(0.2f, 0.5f, 0.95f);
        private static readonly Color EnemyColor = new Color(0.85f, 0.2f, 0.2f);
        private static readonly Color DeadColor = new Color(0.25f, 0.25f, 0.25f);

        private readonly Transform _root;
        private readonly Dictionary<ushort, GameObject> _bodies = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<ushort, (SVector3 Prev, SVector3 Cur)> _positions = new Dictionary<ushort, (SVector3, SVector3)>();
        private readonly List<GameObject> _loot = new List<GameObject>();
        private readonly List<(LineRenderer Line, float Until)> _tracers = new List<(LineRenderer, float)>();
        private readonly Dictionary<ushort, WeaponHolder> _weapons = new Dictionary<ushort, WeaponHolder>();
        private Material _tracerMaterial;
        private float _playerPitch, _playerAim;
        private bool _playerHidden;
        private Renderer[] _playerRenderers;

        public PracticeViews(Transform root) => _root = root;

        /// <summary>Tracers created since the views were built (diagnostics and tests).</summary>
        public int TracersCreated { get; private set; }

        public CombatEffects Effects { get; private set; }

        public WeaponHolder WeaponOf(ushort id) => _weapons.TryGetValue(id, out WeaponHolder h) ? h : null;

        /// <summary>The local player's view pitch (degrees), aim blend, and whether the body is hidden (scoped).</summary>
        public void SetPlayerView(float pitchDegrees, float aim, bool hidden)
        {
            _playerPitch = pitchDegrees;
            _playerAim = aim;
            _playerHidden = hidden;
        }

        public Transform PlayerTransform => _bodies.TryGetValue(PracticeSimulation.PlayerId, out GameObject go) ? go.transform : null;

        public bool TryGetBody(ushort id, out Transform t)
        {
            t = _bodies.TryGetValue(id, out GameObject go) ? go.transform : null;
            return t != null;
        }

        public void Build(PracticeSimulation sim)
        {
            foreach (Fighter f in sim.AllFighters())
            {
                GameObject root = new GameObject(f.Name);
                root.transform.SetParent(_root, false);
                GameObject body = CreatePrimitive(PrimitiveType.Capsule, root.transform, "Body", f.IsPlayer ? PlayerColor : EnemyColor);
                GameObject visor = CreatePrimitive(PrimitiveType.Cube, body.transform, "Visor", Color.black);
                visor.transform.localPosition = new Vector3(0, 0.55f, 0.42f);
                visor.transform.localScale = new Vector3(0.7f, 0.12f, 0.2f);
                _bodies[f.Id] = root;
                _weapons[f.Id] = new WeaponHolder(root.transform);
            }

            for (int i = 0; i < sim.Map.Loot.Count; i++)
            {
                LootSpawn item = sim.Map.Loot[i];
                GameObject go = CreatePrimitive(PrimitiveType.Cube, _root, $"Loot_{i:00}_{item.Type}", LootColor(item.Type));
                go.transform.localScale = item.Type == LootType.Rifle ? new Vector3(0.9f, 0.18f, 0.18f) : Vector3.one * 0.4f;
                _loot.Add(go);
            }

            _tracerMaterial = new Material(Shader.Find("Sprites/Default"));
            Effects = new CombatEffects(_root);
            Sync(sim);
        }

        /// <summary>Snap all views to the simulation (after Reset or on build).</summary>
        public void Sync(PracticeSimulation sim)
        {
            _positions.Clear();
            foreach (Fighter f in sim.AllFighters()) _positions[f.Id] = (f.Motor.Position, f.Motor.Position);
            Render(sim, 1f, 0f);
        }

        /// <summary>Call before each simulation step so rendering can interpolate between ticks.</summary>
        public void BeforeStep(PracticeSimulation sim)
        {
            foreach (Fighter f in sim.AllFighters()) _positions[f.Id] = (f.Motor.Position, f.Motor.Position);
        }

        public void AfterStep(PracticeSimulation sim)
        {
            foreach (Fighter f in sim.AllFighters())
                _positions[f.Id] = (_positions[f.Id].Prev, f.Motor.Position);
        }

        public void Render(PracticeSimulation sim, float alpha, float time)
        {
            foreach (Fighter f in sim.AllFighters())
            {
                Transform root = _bodies[f.Id].transform;
                (SVector3 prev, SVector3 cur) = _positions[f.Id];
                root.position = SVector3.Lerp(prev, cur, alpha).ToUnity();

                Transform body = root.GetChild(0);
                float h = f.Motor.Height;
                if (f.Combat.Alive)
                {
                    root.rotation = Quaternion.Euler(0, f.Yaw * Mathf.Rad2Deg, 0);
                    body.localPosition = new Vector3(0, h / 2, 0);
                    body.localScale = new Vector3(0.8f, h / 2, 0.8f);
                    body.localRotation = Quaternion.identity;
                }
                else
                {
                    // Fallen over.
                    body.localPosition = new Vector3(0, 0.4f, 0);
                    body.localRotation = Quaternion.Euler(90, 0, 0);
                    SetColor(body.gameObject, DeadColor);
                }

                bool hidden = f.IsPlayer && _playerHidden;
                if (f.IsPlayer)
                {
                    _playerRenderers ??= body.GetComponentsInChildren<Renderer>();
                    foreach (Renderer r in _playerRenderers)
                        if (r.enabled == hidden) r.enabled = !hidden;
                }
                WeaponDefinition weapon = sim.WeaponOf(f);
                float reload = -1;
                if (f.Combat.Reloading)
                {
                    float total = Combatant.Ticks(weapon.ReloadSeconds, PracticeSimulation.TickRate);
                    reload = 1f - (f.Combat.ReloadDoneTick - sim.Tick) / total;
                }
                _weapons[f.Id].Update(new WeaponPose
                {
                    Weapon = f.Combat.ActiveWeaponIndex,
                    PitchDegrees = f.IsPlayer ? _playerPitch : -f.Pitch * Mathf.Rad2Deg,
                    Aim = f.IsPlayer ? _playerAim : 0,
                    Reload = reload,
                    Alive = f.Combat.Alive,
                    Crouched = f.Motor.Crouched,
                    Hidden = hidden,
                }, f.Motor.EyeHeight, time, Time.deltaTime);
            }
            Effects?.Update(time);

            for (int i = 0; i < _loot.Count; i++)
            {
                bool visible = !sim.LootTaken[i];
                if (_loot[i].activeSelf != visible) _loot[i].SetActive(visible);
                if (!visible) continue;
                Vector3 p = sim.Map.Loot[i].Position.ToUnity();
                _loot[i].transform.SetPositionAndRotation(
                    p + Vector3.up * (0.45f + Mathf.Sin(time * 2f + i) * 0.08f),
                    Quaternion.Euler(0, time * 60f + i * 30f, 0));
            }

            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                if (time < _tracers[i].Until) continue;
                Object.Destroy(_tracers[i].Line.gameObject);
                _tracers.RemoveAt(i);
            }
        }

        public void ResetColors(PracticeSimulation sim)
        {
            foreach (Fighter f in sim.AllFighters())
                SetColor(_bodies[f.Id].transform.GetChild(0).gameObject, f.IsPlayer ? PlayerColor : EnemyColor);
        }

        /// <summary>A shot tracer from <paramref name="start"/> (the muzzle) or, without one, from just ahead of the eye.</summary>
        public void AddTracer(SVector3 from, SVector3 to, bool byPlayer, float time, Vector3? start = null)
        {
            Vector3 a = from.ToUnity(), b = to.ToUnity();
            Vector3 dir = (b - a).normalized;
            if (start.HasValue)
            {
                a = start.Value;
                dir = Vector3.zero;
            }
            var go = new GameObject("Tracer");
            go.transform.SetParent(_root, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = _tracerMaterial;
            line.positionCount = 2;
            line.SetPosition(0, a + dir * 0.6f);
            line.SetPosition(1, b);
            line.widthMultiplier = byPlayer ? 0.03f : 0.05f;
            Color c = byPlayer ? new Color(1f, 0.9f, 0.4f, 0.9f) : new Color(1f, 0.3f, 0.2f, 0.9f);
            line.startColor = c;
            line.endColor = new Color(c.r, c.g, c.b, 0.2f);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _tracers.Add((line, time + 0.07f));
            TracersCreated++;
        }

        public static Color LootColor(LootType type)
        {
            switch (type)
            {
                case LootType.Rifle: return new Color(0.95f, 0.75f, 0.2f);
                case LootType.Ammo: return new Color(0.55f, 0.75f, 0.3f);
                case LootType.Medkit: return new Color(0.95f, 0.95f, 0.95f);
                case LootType.Armor: return new Color(0.25f, 0.55f, 0.95f);
                default: return Color.magenta;
            }
        }

        public static GameObject CreatePrimitive(PrimitiveType type, Transform parent, string name, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>()); // gameplay collision is the shared simulation, not PhysX
            go.transform.SetParent(parent, false);
            SetColor(go, color);
            return go;
        }

        public static void SetColor(GameObject go, Color color)
        {
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", color);
            block.SetColor("_BaseColor", color);
            go.GetComponent<Renderer>().SetPropertyBlock(block);
        }
    }
}
