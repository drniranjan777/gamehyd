#nullable enable
using System;
using System.Numerics;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Bots
{
    /// <summary>
    /// Simple test-player behaviour from server snapshots: head for the safe zone, aim at the nearest visible
    /// enemy, fire in range, strafe. Used by the UDP bot client and the in-process match simulator. No
    /// navigation: bots can get stuck on buildings (see docs/PHASE-4-STATUS.md).
    /// </summary>
    public sealed class BotBrain
    {
        private readonly Random _rng;

        public BotBrain(int seed) => _rng = new Random(seed);

        public InputCommand Decide(SnapshotPacket? snap, ushort selfId, double time)
        {
            if (snap == null) return default;
            SnapshotEntry self = default;
            bool found = false;
            foreach (SnapshotEntry e in snap.Players)
            {
                if (e.PlayerId != selfId) continue;
                self = e;
                found = true;
            }
            if (!found || (self.Flags & PlayerFlags.Alive) == 0) return default;

            var me = new Vector3(self.X, self.Y, self.Z);
            SnapshotEntry target = default;
            bool hasTarget = false;
            float best = float.MaxValue;
            foreach (SnapshotEntry p in snap.Players)
            {
                if (p.PlayerId == selfId || (p.Flags & PlayerFlags.Alive) == 0) continue;
                float d = Vector3.Distance(me, new Vector3(p.X, p.Y, p.Z));
                if (d < best)
                {
                    best = d;
                    target = p;
                    hasTarget = true;
                }
            }

            float yaw = self.Yaw, pitch = 0;
            if (hasTarget)
            {
                float dx = target.X - me.X, dz = target.Z - me.Z;
                float dy = (target.Y + 1.2f) - (me.Y + GameRules.EyeHeight);
                yaw = (float)Math.Atan2(dx, dz) + (float)(_rng.NextDouble() - 0.5) * 0.04f;
                pitch = (float)Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz));
            }

            // Head for the zone centre when near the edge; otherwise close in and strafe.
            Vector3 worldMove;
            float fromCenter = new Vector2(me.X - snap.ZoneX, me.Z - snap.ZoneZ).Length();
            if (fromCenter > snap.ZoneRadius * 0.7f)
                worldMove = Vector3.Normalize(new Vector3(snap.ZoneX - me.X, 0, snap.ZoneZ - me.Z));
            else
                worldMove = (hasTarget && best > 12 ? Simulation.Forward(yaw) : Vector3.Zero)
                            + Simulation.Right(yaw) * (float)Math.Sin(time * 2 + selfId);

            var buttons = InputButtons.None;
            if (hasTarget && best < 45) buttons |= InputButtons.Fire;
            float mz = Vector3.Dot(worldMove, Simulation.Forward(yaw));
            if (mz > 0.7f) buttons |= InputButtons.Sprint;

            return new InputCommand
            {
                MoveX = Vector3.Dot(worldMove, Simulation.Right(yaw)),
                MoveZ = mz,
                Yaw = yaw,
                Pitch = pitch,
                Buttons = buttons,
            };
        }
    }
}
