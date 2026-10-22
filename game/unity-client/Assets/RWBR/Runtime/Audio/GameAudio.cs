using System;
using System.Collections.Generic;
using RWBR.Client.Settings;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Audio
{
    public enum Sfx
    {
        Footstep,
        Reload,
        Pickup,
        Switch,
        HitMarker,
        Headshot,
        Kill,
        UiClick,
        ZoneWarning,
    }

    /// <summary>
    /// Placeholder audio (BRD 5.3 audio requirements) until licensed/original sound design arrives: clips are
    /// synthesised once, played from a pool of 3D sources with logarithmic distance attenuation, muffled through walls
    /// (occlusion via the game's line-of-sight test) and given room reverb when the listener is indoors.
    /// </summary>
    public sealed class GameAudio : MonoBehaviour
    {
        private const int PoolSize = 32;
        private const int SampleRate = 22050;

        private static GameAudio _instance;
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();

        private readonly List<(AudioSource Source, AudioLowPassFilter Filter)> _pool = new List<(AudioSource, AudioLowPassFilter)>();
        private int _next;
        private AudioReverbFilter _reverb;

        /// <summary>(listener, source) → true when nothing blocks the straight line. Set by the game.</summary>
        public Func<Vector3, Vector3, bool> LineOfSight { get; set; }
        /// <summary>Listener position → true when a roof or ceiling is overhead. Set by the game.</summary>
        public Func<Vector3, bool> Indoors { get; set; }
        public int PlayedCount { get; private set; }
        public bool ListenerIndoors { get; private set; }

        public static GameAudio Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindAnyObjectByType<GameAudio>();
                    if (_instance == null) _instance = new GameObject("GameAudio").AddComponent<GameAudio>();
                }
                return _instance;
            }
        }

        private void Awake()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"Voice{i}");
                go.transform.SetParent(transform, false);
                AudioSource s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.dopplerLevel = 0;
                AudioLowPassFilter f = go.AddComponent<AudioLowPassFilter>();
                f.enabled = false;
                _pool.Add((s, f));
            }
        }

        private void LateUpdate()
        {
            AudioListener.volume = GameSettings.MasterVolume;
            AudioListener listener = FindListener();
            if (listener == null) return;
            if (_reverb == null)
            {
                // Not `GetComponent() ?? AddComponent()`: Unity's "fake null" defeats the ?? operator.
                if (!listener.TryGetComponent(out _reverb)) _reverb = listener.gameObject.AddComponent<AudioReverbFilter>();
                _reverb.reverbPreset = AudioReverbPreset.Off;
            }
            ListenerIndoors = Indoors != null && Indoors(listener.transform.position);
            _reverb.reverbPreset = ListenerIndoors ? AudioReverbPreset.Room : AudioReverbPreset.Off;
        }

        private static AudioListener _listener;

        private static AudioListener FindListener()
        {
            if (_listener == null) _listener = FindAnyObjectByType<AudioListener>();
            return _listener;
        }

        /// <summary>A gunshot at a world position, audible out to a distance that depends on the weapon class.</summary>
        public void Gunshot(WeaponDefinition weapon, Vector3 position, bool local)
        {
            float audible = weapon.Class switch
            {
                WeaponClass.Pistol => 120f,
                WeaponClass.Smg => 140f,
                WeaponClass.Shotgun => 160f,
                WeaponClass.Marksman => 300f,
                WeaponClass.Sniper => 400f,
                _ => 220f,
            };
            Play(GunshotClip(weapon.Class), position, local ? 0.8f : 1f, audible, local);
            if (!local) RWBR.Client.Accessibility.SoundCues.Add(position, audible);
        }

        public void PlayAt(Sfx sfx, Vector3 position, float volume = 1f, float audible = 30f) =>
            Play(Clip(sfx), position, volume, audible, false);

        /// <summary>Non-positional (HUD) sound on the UI volume.</summary>
        public void PlayUi(Sfx sfx, float volume = 1f)
        {
            (AudioSource s, AudioLowPassFilter f) = NextVoice();
            f.enabled = false;
            s.spatialBlend = 0;
            s.volume = volume * GameSettings.UiVolume;
            s.pitch = 1;
            s.clip = Clip(sfx);
            s.Play();
            PlayedCount++;
        }

        private void Play(AudioClip clip, Vector3 position, float volume, float audible, bool local)
        {
            AudioListener listener = FindListener();
            Vector3 ear = listener != null ? listener.transform.position : position;
            if (!local && Vector3.Distance(ear, position) > audible) return;

            (AudioSource s, AudioLowPassFilter f) = NextVoice();
            s.transform.position = position;
            s.spatialBlend = local ? 0.3f : 1f;
            s.minDistance = 2f;
            s.maxDistance = audible;
            bool occluded = !local && LineOfSight != null && !LineOfSight(ear, position);
            f.enabled = occluded;
            f.cutoffFrequency = 1200f;
            s.volume = volume * GameSettings.SfxVolume * (occluded ? 0.6f : 1f);
            s.pitch = 1f + UnityEngine.Random.Range(-0.04f, 0.04f);
            s.clip = clip;
            s.Play();
            PlayedCount++;
        }

        private (AudioSource, AudioLowPassFilter) NextVoice()
        {
            var v = _pool[_next];
            _next = (_next + 1) % _pool.Count;
            v.Item1.Stop();
            return v;
        }

        // ---- Synthesis (placeholder sound design) ----

        public static AudioClip GunshotClip(WeaponClass c)
        {
            switch (c)
            {
                case WeaponClass.Pistol: return Synth("shot_pistol", 0.25f, 0.05f, 0.35f, 900f, 1f);
                case WeaponClass.Smg: return Synth("shot_smg", 0.18f, 0.04f, 0.25f, 1200f, 0.9f);
                case WeaponClass.Shotgun: return Synth("shot_shotgun", 0.6f, 0.12f, 0.6f, 500f, 1f);
                case WeaponClass.Marksman: return Synth("shot_dmr", 0.5f, 0.1f, 0.5f, 700f, 1f);
                case WeaponClass.Sniper: return Synth("shot_sniper", 0.9f, 0.18f, 0.8f, 400f, 1f);
                default: return Synth("shot_rifle", 0.35f, 0.07f, 0.4f, 800f, 1f);
            }
        }

        public static AudioClip Clip(Sfx sfx)
        {
            switch (sfx)
            {
                case Sfx.Footstep: return Synth("step", 0.09f, 0.02f, 0.3f, 300f, 0.5f);
                case Sfx.Reload: return Tone("reload", 0.25f, new[] { (0.0f, 1800f), (0.15f, 1400f) }, 0.04f, 0.5f);
                case Sfx.Pickup: return Tone("pickup", 0.22f, new[] { (0.0f, 660f), (0.08f, 880f), (0.15f, 1320f) }, 0.07f, 0.4f);
                case Sfx.Switch: return Tone("switch", 0.12f, new[] { (0.0f, 1200f), (0.06f, 900f) }, 0.04f, 0.4f);
                case Sfx.HitMarker: return Tone("hit", 0.05f, new[] { (0.0f, 2600f) }, 0.05f, 0.35f);
                case Sfx.Headshot: return Tone("headshot", 0.12f, new[] { (0.0f, 3200f), (0.05f, 4200f) }, 0.05f, 0.4f);
                case Sfx.Kill: return Tone("kill", 0.3f, new[] { (0.0f, 880f), (0.1f, 1100f), (0.2f, 1760f) }, 0.1f, 0.4f);
                case Sfx.ZoneWarning: return Tone("zone", 0.6f, new[] { (0.0f, 220f), (0.3f, 196f) }, 0.3f, 0.3f);
                default: return Tone("click", 0.04f, new[] { (0.0f, 1500f) }, 0.04f, 0.3f);
            }
        }

        /// <summary>Filtered noise burst with a fast attack and exponential decay (gunshots, footsteps).</summary>
        private static AudioClip Synth(string name, float seconds, float decay, float body, float cutoff, float gain)
        {
            if (Clips.TryGetValue(name, out AudioClip cached)) return cached;
            int n = Mathf.CeilToInt(seconds * SampleRate);
            var data = new float[n];
            uint state = (uint)name.GetHashCode() | 1u;
            float lp = 0, a = Mathf.Exp(-2f * Mathf.PI * cutoff / SampleRate);
            for (int i = 0; i < n; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                float noise = (state & 0xFFFF) / 32768f - 1f;
                lp = a * lp + (1 - a) * noise; // one-pole low-pass: lower cutoff = boomier
                float t = i / (float)SampleRate;
                float env = Mathf.Exp(-t / decay) * (t < 0.002f ? t / 0.002f : 1f);
                float thump = Mathf.Sin(2 * Mathf.PI * 70f * t) * Mathf.Exp(-t / (decay * 1.5f)) * body;
                data[i] = Mathf.Clamp((lp * 3f + thump) * env * gain, -1f, 1f);
            }
            return Store(name, data);
        }

        /// <summary>Short sine blips at given times (UI and mechanical cues).</summary>
        private static AudioClip Tone(string name, float seconds, (float At, float Hz)[] notes, float noteLength, float gain)
        {
            if (Clips.TryGetValue(name, out AudioClip cached)) return cached;
            int n = Mathf.CeilToInt(seconds * SampleRate);
            var data = new float[n];
            foreach ((float at, float hz) in notes)
            {
                int start = (int)(at * SampleRate), len = (int)(noteLength * SampleRate);
                for (int i = 0; i < len && start + i < n; i++)
                {
                    float t = i / (float)SampleRate;
                    float env = Mathf.Min(1f, t / 0.003f) * Mathf.Exp(-t / (noteLength * 0.4f));
                    data[start + i] += Mathf.Sin(2 * Mathf.PI * hz * t) * env * gain;
                }
            }
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(data[i], -1f, 1f);
            return Store(name, data);
        }

        private static AudioClip Store(string name, float[] data)
        {
            AudioClip clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            Clips[name] = clip;
            return clip;
        }

        /// <summary>Peak absolute sample of a generated clip (tests).</summary>
        public static float Peak(AudioClip clip)
        {
            var d = new float[clip.samples];
            clip.GetData(d, 0);
            float peak = 0;
            foreach (float v in d) peak = Mathf.Max(peak, Mathf.Abs(v));
            return peak;
        }
    }

    /// <summary>Footstep cadence from distance walked (shared by the local player, enemies and remote players).</summary>
    public sealed class FootstepTracker
    {
        private readonly Dictionary<int, (Vector3 Last, float Walked)> _state = new Dictionary<int, (Vector3, float)>();

        /// <summary>Returns true when <paramref name="id"/> has covered another stride on the ground.</summary>
        public bool Step(int id, Vector3 position, bool grounded, bool crouched)
        {
            if (!_state.TryGetValue(id, out var s)) s = (position, 0f);
            Vector3 d = position - s.Last;
            d.y = 0;
            float walked = s.Walked + (grounded ? Mathf.Min(d.magnitude, 2f) : 0f);
            float stride = crouched ? 3.0f : 2.2f; // crouch-walking is quieter: fewer steps
            bool step = walked >= stride;
            _state[id] = (position, step ? walked - stride : walked);
            return step;
        }
    }
}
