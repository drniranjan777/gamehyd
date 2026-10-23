using RWBR.Client.Settings;
using UnityEngine;

namespace RWBR.Client.Combat
{
    /// <summary>
    /// Pooled shot effects: muzzle flashes (with a brief light for your own shots on Medium and High quality), spent
    /// cases, dust where shots hit the world, and sparks where they hit a player (no blood). One particle system per
    /// effect, fed with Emit, so firing allocates nothing.
    /// "Reduce flashing" (accessibility) shrinks the flash and turns the light off.
    /// </summary>
    public sealed class CombatEffects
    {
        private readonly ParticleSystem _flash, _sparks, _dust, _shells;
        private readonly Light _light;
        private readonly GameObject _root;
        private float _lightUntil;
        private ParticleSystem.EmitParams _emit;

        public CombatEffects(Transform parent)
        {
            _root = new GameObject("CombatEffects");
            Transform root = _root.transform;
            root.SetParent(parent, false);
            var material = new Material(Shader.Find("Sprites/Default")) { name = "Effects", mainTexture = SoftDot(), hideFlags = HideFlags.DontSave };
            _flash = Make(root, "MuzzleFlash", material, 0f, 64);
            _sparks = Make(root, "HitSparks", material, 1.5f, 256);
            _dust = Make(root, "ImpactDust", material, 0.4f, 256);
            _shells = Make(root, "Shells", material, 3f, 64);

            var lightGo = new GameObject("MuzzleLight");
            lightGo.transform.SetParent(root, false);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.range = 6f;
            _light.intensity = 2.5f;
            _light.color = new Color(1f, 0.8f, 0.45f);
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        /// <summary>Particles emitted since creation (diagnostics and tests).</summary>
        public int Emitted { get; private set; }

        public void MuzzleFlash(Vector3 position, Vector3 direction, bool local, float time)
        {
            bool reduce = GameSettings.ReduceFlashing;
            float size = (local ? 0.35f : 0.45f) * (reduce ? 0.5f : 1f);
            Emit(_flash, position, direction * 0.5f, size, 0.05f, new Color(1f, 0.85f, 0.45f, reduce ? 0.6f : 0.95f));
            Emit(_flash, position + direction * 0.12f, direction * 1.5f, size * 0.6f, 0.04f, new Color(1f, 0.95f, 0.7f, 0.8f));
            if (local && !reduce && GameSettings.Quality != QualityPreset.Low)
            {
                _light.transform.position = position;
                _light.enabled = true;
                _lightUntil = time + 0.05f;
            }
        }

        public void Shell(Vector3 position, Vector3 right, Vector3 forward)
        {
            Vector3 v = right * Random.Range(1.2f, 1.8f) + Vector3.up * Random.Range(1f, 1.6f) - forward * 0.3f;
            Emit(_shells, position, v, 0.03f, 0.8f, new Color(0.85f, 0.65f, 0.25f, 1f));
        }

        /// <summary>A shot hit the world (dust) or a player (sparks). <paramref name="back"/> points towards the shooter.</summary>
        public void Impact(Vector3 position, Vector3 back, bool player)
        {
            ParticleSystem ps = player ? _sparks : _dust;
            int count = player ? 7 : 5;
            Color color = player ? new Color(1f, 0.9f, 0.5f, 1f) : new Color(0.62f, 0.58f, 0.5f, 0.8f);
            for (int i = 0; i < count; i++)
            {
                Vector3 v = (back + Random.insideUnitSphere * 0.8f).normalized * (player ? Random.Range(2f, 4f) : Random.Range(0.6f, 1.6f));
                Emit(ps, position, v, player ? 0.05f : Random.Range(0.12f, 0.25f), player ? 0.18f : 0.6f, color);
            }
        }

        public void Destroy() => Object.Destroy(_root);

        public void Update(float time)
        {
            if (_light.enabled && time >= _lightUntil) _light.enabled = false;
        }

        private void Emit(ParticleSystem ps, Vector3 position, Vector3 velocity, float size, float lifetime, Color color)
        {
            _emit.position = position;
            _emit.velocity = velocity;
            _emit.startSize = size;
            _emit.startLifetime = lifetime;
            _emit.startColor = color;
            _emit.applyShapeToPosition = false;
            ps.Emit(_emit, 1);
            Emitted++;
        }

        private static ParticleSystem Make(Transform parent, string name, Material material, float gravity, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = gravity;
            main.maxParticles = max;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;
            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
            fade.color = gradient;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ps.Play();
            return ps;
        }

        /// <summary>A small soft round sprite, so particles are not drawn as squares.</summary>
        private static Texture2D SoftDot()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "SoftDot", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
