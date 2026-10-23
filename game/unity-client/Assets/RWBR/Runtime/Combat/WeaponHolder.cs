using System.Collections.Generic;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Combat
{
    /// <summary>What a character is doing with its weapon this frame (from the simulation or a snapshot).</summary>
    public struct WeaponPose
    {
        /// <summary>Catalog index, or -1 for none.</summary>
        public int Weapon;
        /// <summary>Weapon class when the weapon is not in the catalog (practice enemies).</summary>
        public WeaponClass? ClassOverride;
        /// <summary>View pitch in degrees, positive down (0 for remote players, whose pitch is not sent).</summary>
        public float PitchDegrees;
        /// <summary>0 = hip, 1 = aimed.</summary>
        public float Aim;
        /// <summary>0..1 through a reload, or -1 when not reloading.</summary>
        public float Reload;
        public bool Alive;
        public bool Crouched;
        public bool Hidden;
    }

    /// <summary>
    /// Holds the weapon model in front of a character and moves it procedurally: hip and aimed positions, pitch with
    /// the view, a dip and roll through reloads, a lower-and-raise on weapon switch, and a kick back on each shot.
    /// This is placeholder motion for the capsule characters; with rigged characters the hands will follow the weapon
    /// through IK instead (Phase B, once the Mixamo files are imported).
    /// </summary>
    public sealed class WeaponHolder
    {
        private const float SwitchSeconds = 0.25f;
        private static readonly Vector3 HipOffset = new Vector3(0.26f, -0.42f, 0.42f);
        private static readonly Vector3 AimOffset = new Vector3(0.14f, -0.22f, 0.45f);

        private readonly Transform _anchor;
        private readonly Dictionary<WeaponClass, WeaponModel> _models = new Dictionary<WeaponClass, WeaponModel>();
        private WeaponModel _current;
        private WeaponClass? _currentClass;
        private float _switchStarted = -10f;
        private float _kick;

        public WeaponHolder(Transform character)
        {
            _anchor = new GameObject("WeaponAnchor").transform;
            _anchor.SetParent(character, false);
        }

        public WeaponModel Current => _current;
        public Vector3 MuzzlePosition => _current != null ? _current.Muzzle.position : _anchor.position;
        public Vector3 Forward => _anchor.forward;
        public Vector3 Right => _anchor.right;
        public Vector3 EjectorPosition => _current != null ? _current.Ejector.position : _anchor.position;

        /// <summary>Kick the weapon back for one shot.</summary>
        public void Fired(float recoilPitchDegrees) => _kick = Mathf.Min(1f, _kick + 0.35f + recoilPitchDegrees * 0.1f);

        public void Update(WeaponPose pose, float eyeHeight, float time, float dt)
        {
            WeaponClass? wanted = pose.ClassOverride ?? (WeaponCatalog.IsValid(pose.Weapon) ? WeaponCatalog.Get(pose.Weapon).Class : (WeaponClass?)null);
            if (wanted != _currentClass)
            {
                if (_current != null) _current.Root.SetActive(false);
                _current = null;
                if (wanted.HasValue)
                {
                    if (!_models.TryGetValue(wanted.Value, out _current))
                        _models[wanted.Value] = _current = WeaponModels.Build(wanted.Value, _anchor);
                    _current.Root.SetActive(true);
                    if (_currentClass.HasValue) _switchStarted = time; // raise the new weapon (not on first pickup)
                }
                _currentClass = wanted;
            }
            if (_current == null) return;

            bool visible = pose.Alive && !pose.Hidden;
            if (_current.Root.activeSelf != visible) _current.Root.SetActive(visible);
            if (!visible) return;

            _kick = Mathf.MoveTowards(_kick, 0, dt * 6f);
            float aim = Mathf.Clamp01(pose.Aim);
            Vector3 offset = Vector3.Lerp(HipOffset, AimOffset, aim * aim * (3 - 2 * aim));
            float pitch = pose.PitchDegrees;
            float roll = 0;

            // Switch: start low and come up over SwitchSeconds.
            float sinceSwitch = time - _switchStarted;
            if (sinceSwitch < SwitchSeconds)
            {
                float down = 1f - sinceSwitch / SwitchSeconds;
                offset.y -= 0.25f * down;
                pitch += 50f * down;
            }

            // Reload: tip the weapon down and roll it towards the body, then bring it back.
            if (pose.Reload >= 0)
            {
                float k = Mathf.Sin(Mathf.Clamp01(pose.Reload) * Mathf.PI);
                pitch += 28f * k;
                roll = -35f * k;
                offset.y -= 0.08f * k;
            }

            offset.z -= 0.07f * _kick;
            pitch -= 6f * _kick;

            _anchor.localPosition = new Vector3(0, eyeHeight, 0);
            _anchor.localRotation = Quaternion.Euler(Mathf.Clamp(pose.PitchDegrees, -70f, 75f), 0, 0);
            _current.Root.transform.localPosition = offset;
            _current.Root.transform.localRotation = Quaternion.Euler(pitch - pose.PitchDegrees, 0, roll);
        }
    }
}
