#nullable enable
using System;
using System.Numerics;

namespace RWBR.Shared.Server
{
    /// <summary>
    /// The plane drop: when play starts everyone is aboard a plane crossing the map in a straight line at
    /// <see cref="GameRules.PlaneAltitude"/>. Jump leaves the plane; falling and the parachute are ordinary movement
    /// (<see cref="Simulation.Step"/>, so clients predict them). Anyone still aboard is dropped at the end of the line.
    /// The line is different every match (from the zone seed), lies wholly over the map, and passes near the first
    /// circle.
    /// </summary>
    public sealed partial class Match
    {
        private Vector3 _planeFrom, _planeTo;
        private long _planeStartTick, _planeEndTick;
        private float _planeYaw;

        public bool PlaneFlying { get; private set; }

        /// <summary>Where the plane is now (feet height of anyone aboard).</summary>
        public Vector3 PlanePosition
        {
            get
            {
                if (_planeEndTick <= _planeStartTick) return _planeFrom;
                float t = Math.Clamp((Tick - _planeStartTick) / (float)(_planeEndTick - _planeStartTick), 0, 1);
                return Vector3.Lerp(_planeFrom, _planeTo, t);
            }
        }

        private void PlanFlight()
        {
            uint state = (uint)(_cfg.ZoneSeed * 2654435761u) | 1u;
            float Next()
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return (state & 0xFFFFFF) / (float)0x1000000;
            }
            // Through a point between the map centre and the first circle's centre, in a random direction.
            Vector2 first = _zoneCenters.Length > 1 ? _zoneCenters[1] : Vector2.Zero;
            Vector2 through = first * 0.5f + new Vector2(Next() - 0.5f, Next() - 0.5f) * _cfg.Map.HalfSize * 0.3f;
            double angle = Next() * Math.PI * 2;
            var dir = new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle));
            float edge = _cfg.Map.HalfSize - 15f;
            float back = Reach(through, -dir, edge), ahead = Reach(through, dir, edge);
            Vector2 a = through - dir * back, b = through + dir * ahead;
            _planeFrom = new Vector3(a.X, GameRules.PlaneAltitude, a.Y);
            _planeTo = new Vector3(b.X, GameRules.PlaneAltitude, b.Y);
            _planeYaw = (float)Math.Atan2(dir.X, dir.Y);
            _planeStartTick = Tick;
            _planeEndTick = Tick + Math.Max(1, _cfg.Ticks(Vector3.Distance(_planeFrom, _planeTo) / GameRules.PlaneSpeed));
            PlaneFlying = true;
        }

        /// <summary>How far from <paramref name="p"/> along <paramref name="dir"/> before leaving the square of half-size <paramref name="edge"/>.</summary>
        private static float Reach(Vector2 p, Vector2 dir, float edge)
        {
            float t = float.MaxValue;
            if (Math.Abs(dir.X) > 1e-5f) t = Math.Min(t, ((dir.X > 0 ? edge : -edge) - p.X) / dir.X);
            if (Math.Abs(dir.Y) > 1e-5f) t = Math.Min(t, ((dir.Y > 0 ? edge : -edge) - p.Y) / dir.Y);
            return Math.Max(0, t);
        }

        private void Board(PlayerState p)
        {
            p.Motor = new MotorState { Position = PlanePosition, Drop = DropPhase.Plane };
            Array.Fill(p.History, p.Motor);
        }

        private void JumpOut(PlayerState p)
        {
            p.Motor.Drop = DropPhase.Freefall;
            p.Motor.Grounded = false;
            p.Motor.Position = PlanePosition - new Vector3(0, 4f, 0); // clear of the plane's belly
        }

        /// <summary>Moves the plane and everyone aboard; at the end of the line, everyone left jumps.</summary>
        private void StepPlane()
        {
            if (!PlaneFlying) return;
            Vector3 at = PlanePosition;
            bool last = Tick >= _planeEndTick;
            foreach (PlayerState p in _players)
            {
                if (p.Motor.Drop != DropPhase.Plane) continue;
                p.Motor.Position = at;
                if (last && p.Alive) JumpOut(p);
            }
            if (last) PlaneFlying = false;
        }
    }
}
