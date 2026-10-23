using RWBR.Client.Combat;
using RWBR.Client.Practice;
using UnityEngine;

namespace RWBR.Client.Character
{
    /// <summary>
    /// The visible body of one character, under a root that the views move and turn to face the view. When the rigged
    /// character prefab exists (built by RWBR > Characters > Build from the Mixamo files) it is used; otherwise the
    /// placeholder capsule is. Gameplay collision is always the shared simulation's capsule, whichever body is shown.
    /// </summary>
    public abstract class CharacterBody
    {
        /// <summary>Resources path of the rigged character prefab written by the character builder.</summary>
        public const string PrefabResource = "Characters/Soldier";

        private Vector3 _lastPosition;
        private Vector3 _velocity;
        private bool _hasLast;

        protected CharacterBody(Transform root) => Root = root;

        public Transform Root { get; }
        public abstract bool Rigged { get; }

        /// <summary>The rigged prefab if it has been built, else null (cached after the first look).</summary>
        public static GameObject RiggedPrefab
        {
            get
            {
                if (!_looked)
                {
                    _prefab = Resources.Load<GameObject>(PrefabResource);
                    _looked = true;
                }
                return _prefab;
            }
        }

        private static GameObject _prefab;
        private static bool _looked;

        /// <summary>Forget the cached prefab lookup (tests, after building characters).</summary>
        public static void ResetPrefabCache() => _looked = false;

        public static CharacterBody Create(Transform root, Color color, GameObject riggedPrefab = null)
        {
            GameObject prefab = riggedPrefab != null ? riggedPrefab : RiggedPrefab;
            return prefab != null ? new RiggedBody(root, prefab) : (CharacterBody)new CapsuleBody(root, color);
        }

        /// <summary>
        /// Poses the body for this frame. <paramref name="m"/>.Velocity is filled from how far the root moved since the
        /// last frame (smoothed), so callers only pass state flags and view angles.
        /// </summary>
        public void Pose(CharacterMotion m, float height, float rootYawDegrees, float dt)
        {
            Vector3 p = Root.position;
            if (_hasLast && dt > 1e-5f)
            {
                Vector3 v = (p - _lastPosition) / dt;
                if (v.sqrMagnitude > 400f) v = Vector3.zero; // teleport or respawn
                _velocity = Vector3.Lerp(_velocity, v, 1f - Mathf.Exp(-12f * dt));
            }
            _lastPosition = p;
            _hasLast = true;
            m.Velocity = _velocity;
            OnPose(m, height, rootYawDegrees, dt);
        }

        public Vector3 EstimatedVelocity => _velocity;

        protected abstract void OnPose(CharacterMotion m, float height, float rootYawDegrees, float dt);
        public abstract void SetHidden(bool hidden);
        public virtual void SetColor(Color color) { }
        public virtual void Bind(WeaponHolder weapon) { }
        public virtual void Fired() { }
        public virtual void Hit() { }
    }

    /// <summary>The placeholder: a capsule with a visor that falls over and greys out when down.</summary>
    public sealed class CapsuleBody : CharacterBody
    {
        private static readonly Color DeadColor = new Color(0.25f, 0.25f, 0.25f);
        private readonly Transform _body;
        private readonly Renderer[] _renderers;
        private Color _color;
        private bool _shownDead;

        public CapsuleBody(Transform root, Color color) : base(root)
        {
            _color = color;
            GameObject body = PracticeViews.CreatePrimitive(PrimitiveType.Capsule, root, "Body", color);
            GameObject visor = PracticeViews.CreatePrimitive(PrimitiveType.Cube, body.transform, "Visor", Color.black);
            visor.transform.localPosition = new Vector3(0, 0.55f, 0.42f);
            visor.transform.localScale = new Vector3(0.7f, 0.12f, 0.2f);
            _body = body.transform;
            _renderers = body.GetComponentsInChildren<Renderer>();
        }

        public override bool Rigged => false;

        protected override void OnPose(CharacterMotion m, float height, float rootYawDegrees, float dt)
        {
            if (m.Alive)
            {
                _body.localPosition = new Vector3(0, height / 2, 0);
                _body.localScale = new Vector3(0.8f, height / 2, 0.8f);
                _body.localRotation = Quaternion.identity;
                if (_shownDead) PracticeViews.SetColor(_body.gameObject, _color);
                _shownDead = false;
            }
            else
            {
                _body.localPosition = new Vector3(0, 0.4f, 0);
                _body.localRotation = Quaternion.Euler(90, 0, 0);
                if (!_shownDead) PracticeViews.SetColor(_body.gameObject, DeadColor);
                _shownDead = true;
            }
        }

        public override void SetHidden(bool hidden)
        {
            foreach (Renderer r in _renderers)
                if (r.enabled == hidden) r.enabled = !hidden;
        }

        public override void SetColor(Color color)
        {
            _color = color;
            if (!_shownDead) PracticeViews.SetColor(_body.gameObject, color);
        }
    }

    /// <summary>The licensed humanoid character with its animator (see <see cref="CharacterAnimator"/>).</summary>
    public sealed class RiggedBody : CharacterBody
    {
        private readonly CharacterAnimator _animator;
        private readonly Renderer[] _renderers;

        public RiggedBody(Transform root, GameObject prefab) : base(root)
        {
            GameObject model = Object.Instantiate(prefab, root, false);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero; // the root is the character's feet
            model.transform.localRotation = Quaternion.identity;
            _animator = model.GetComponent<CharacterAnimator>() ?? model.AddComponent<CharacterAnimator>();
            _renderers = model.GetComponentsInChildren<Renderer>();
        }

        public override bool Rigged => true;
        public CharacterAnimator Animator => _animator;

        protected override void OnPose(CharacterMotion m, float height, float rootYawDegrees, float dt) =>
            _animator.Drive(m, rootYawDegrees, dt);

        public override void SetHidden(bool hidden)
        {
            foreach (Renderer r in _renderers)
                if (r.enabled == hidden) r.enabled = !hidden;
        }

        public override void Bind(WeaponHolder weapon)
        {
            weapon.SetHoldOffsets(WeaponHolder.RiggedHip, WeaponHolder.RiggedAim);
            _animator.Bind(weapon);
        }
        public override void Fired() => _animator.Fired();
        public override void Hit() => _animator.Hit();
    }
}
