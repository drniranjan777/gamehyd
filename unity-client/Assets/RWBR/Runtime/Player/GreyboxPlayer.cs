using RWBR.Client.Greybox;
using RWBR.Shared;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Player
{
    /// <summary>
    /// Third-person character for the greybox. Movement runs the shared <see cref="Simulation.Move"/> at the
    /// server tick rate, so it is exactly what the authoritative server will compute (the client-prediction path
    /// in Phase 2); rendering interpolates between ticks.
    /// </summary>
    public sealed class GreyboxPlayer : MonoBehaviour
    {
        public const int TickRate = 30;
        private const float Dt = 1f / TickRate;

        private MapLayout _map;
        private SVector3 _previous;
        private SVector3 _current;
        private float _accumulator;

        public IPlayerInputSource InputSource { get; set; } = new DeviceInputSource();
        public float YawDegrees { get; private set; }
        public float PitchDegrees { get; private set; }
        public int TicksSimulated { get; private set; }

        private void Start()
        {
            GreyboxWorld world = FindAnyObjectByType<GreyboxWorld>();
            _map = world != null ? world.Layout : MapLayout.GreyboxBlock(100f);
            Vector3 p = transform.position;
            _current = _previous = Simulation.ResolveCollisions(new SVector3(p.x, 0, p.z), _map);
            YawDegrees = transform.eulerAngles.y;
        }

        private void Update()
        {
            PlayerInputFrame input = InputSource.Read();
            YawDegrees = Mathf.Repeat(YawDegrees + input.Look.x, 360f);
            PitchDegrees = Mathf.Clamp(PitchDegrees - input.Look.y, -60f, 70f);

            var cmd = new InputCommand
            {
                MoveX = input.Move.x,
                MoveZ = input.Move.y,
                Yaw = YawDegrees * Mathf.Deg2Rad,
                Pitch = -PitchDegrees * Mathf.Deg2Rad,
                Buttons = (input.Sprint ? InputButtons.Sprint : 0) | (input.Fire ? InputButtons.Fire : 0),
            };

            _accumulator += Mathf.Min(Time.deltaTime, 0.25f);
            while (_accumulator >= Dt)
            {
                _previous = _current;
                _current = Simulation.Move(_current, cmd, Dt, _map);
                _accumulator -= Dt;
                TicksSimulated++;
            }

            SVector3 pos = SVector3.Lerp(_previous, _current, _accumulator / Dt);
            transform.SetPositionAndRotation(new Vector3(pos.X, pos.Y, pos.Z), Quaternion.Euler(0, YawDegrees, 0));
        }
    }
}
