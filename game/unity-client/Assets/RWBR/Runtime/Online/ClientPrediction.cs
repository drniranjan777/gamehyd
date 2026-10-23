using System.Collections.Generic;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Online
{
    /// <summary>
    /// Client-side prediction with server reconciliation for the local player (BRD §5.2). Inputs are applied
    /// immediately with the shared rules; when a snapshot arrives, the server's state for the last processed input
    /// is taken as truth and the still-unacknowledged inputs are replayed on top of it. Corrections are smoothed
    /// visually so small mismatches never snap the camera.
    /// </summary>
    public sealed class ClientPrediction
    {
        /// <summary>Corrections larger than this snap instead of smoothing (teleport, respawn, big desync).</summary>
        public const float SnapDistance = 2f;
        private const float SmoothingPerSecond = 12f;

        private readonly List<InputCommand> _pending = new List<InputCommand>();
        private readonly MapLayout _map;
        private readonly float _dt;
        private uint _lastReconciledTick;

        public ClientPrediction(MapLayout map, int tickRate)
        {
            _map = map;
            _dt = 1f / tickRate;
        }

        public MotorState Predicted;
        public bool Initialized { get; private set; }
        /// <summary>Offset added when rendering so corrections blend in; decays towards zero.</summary>
        public SVector3 VisualOffset { get; private set; }
        public int Corrections { get; private set; }
        public float LastError { get; private set; }
        public float MaxError { get; private set; }
        public int PendingCount => _pending.Count;

        /// <summary>
        /// In a car the server moves you (the car is not predicted yet): inputs are not replayed on foot, and every
        /// snapshot simply sets your position.
        /// </summary>
        public bool Frozen { get; set; }

        public void Apply(InputCommand cmd)
        {
            if (!Initialized || Frozen) return;
            Simulation.Step(ref Predicted, cmd, _dt, _map);
            _pending.Add(cmd);
            if (_pending.Count > 120) _pending.RemoveAt(0); // ~4 s without acks: stop growing
        }

        /// <summary>Rebase on the server's authoritative state and replay unacknowledged inputs.</summary>
        public void Reconcile(SnapshotPacket s, ushort selfId)
        {
            if (s.Tick <= _lastReconciledTick) return;
            _lastReconciledTick = s.Tick;

            SnapshotEntry? self = null;
            foreach (SnapshotEntry e in s.Players)
                if (e.PlayerId == selfId) self = e;
            if (self == null) return;
            SnapshotEntry me = self.Value;

            var server = new MotorState
            {
                Position = new SVector3(s.SelfX, s.SelfY, s.SelfZ), // exact, not the quantised entry
                VerticalVelocity = s.SelfVerticalVelocity,
                Grounded = (me.Flags & PlayerFlags.Grounded) != 0,
                Crouched = (me.Flags & PlayerFlags.Crouched) != 0,
                Drop = (me.Flags & PlayerFlags.InPlane) != 0 ? DropPhase.Plane
                    : (me.Flags & PlayerFlags.Freefall) != 0 ? DropPhase.Freefall
                    : (me.Flags & PlayerFlags.Parachute) != 0 ? DropPhase.Parachute : DropPhase.None,
            };

            _pending.RemoveAll(c => c.Sequence <= s.AckSequence);
            if (!Initialized || Frozen)
            {
                Predicted = server;
                Initialized = true;
                if (Frozen)
                {
                    _pending.Clear();
                    VisualOffset = SVector3.Zero;
                }
                return;
            }

            MotorState replay = server;
            foreach (InputCommand c in _pending) Simulation.Step(ref replay, c, _dt, _map);

            SVector3 error = replay.Position - Predicted.Position;
            LastError = error.Length();
            if (LastError > MaxError) MaxError = LastError;
            if (LastError > 0.01f) Corrections++;

            // Keep the rendered position where it was and blend the difference out over the next frames.
            VisualOffset = LastError > SnapDistance ? SVector3.Zero : VisualOffset - error;
            Predicted = replay;
        }

        public SVector3 RenderPosition => Predicted.Position + VisualOffset;

        public void DecayOffset(float deltaTime)
        {
            float k = 1f - (float)System.Math.Exp(-SmoothingPerSecond * deltaTime);
            VisualOffset -= VisualOffset * k;
        }

        public void Reset()
        {
            _pending.Clear();
            Initialized = false;
            VisualOffset = SVector3.Zero;
            _lastReconciledTick = 0;
            Corrections = 0;
            LastError = MaxError = 0;
        }
    }
}
