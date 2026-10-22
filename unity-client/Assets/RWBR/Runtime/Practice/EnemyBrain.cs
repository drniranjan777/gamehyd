using System;
using RWBR.Shared;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Practice
{
    /// <summary>
    /// Deterministic practice-enemy behaviour: patrol near home, and once the player is visible and in range,
    /// turn to face them and fire after a reaction delay while strafing. Inputs only; the shared rules decide
    /// what actually happens.
    /// </summary>
    public sealed class EnemyBrain
    {
        public const float SightRange = 40f;
        public const int ReactionTicks = 18; // 0.6 s
        private const float PatrolSpeed = 0.6f;

        private readonly SVector3 _home;
        private readonly float _phase;
        private long _seenSince = -1;
        private uint _sequence;

        public EnemyBrain(SVector3 home, float phase)
        {
            _home = home;
            _phase = phase;
        }

        public InputCommand Decide(Fighter self, Fighter player, PracticeSimulation sim)
        {
            var cmd = new InputCommand { Sequence = ++_sequence, Yaw = self.Yaw, Pitch = self.Pitch };
            float t = sim.Tick * PracticeSimulation.Dt;

            SVector3 eye = self.Motor.Eye;
            SVector3 chest = player.Chest;
            SVector3 toPlayer = chest - eye;
            float dist = toPlayer.Length();
            bool sees = player.Combat.Alive && dist < SightRange && sim.HasLineOfSight(eye, chest);

            if (sees)
            {
                if (_seenSince < 0) _seenSince = sim.Tick;
                cmd.Yaw = (float)Math.Atan2(toPlayer.X, toPlayer.Z);
                cmd.Pitch = (float)Math.Atan2(toPlayer.Y, Math.Sqrt(toPlayer.X * toPlayer.X + toPlayer.Z * toPlayer.Z));
                cmd.MoveX = (float)Math.Sin(t * 1.8f + _phase) * 0.8f;
                if (sim.Tick - _seenSince >= ReactionTicks) cmd.Buttons |= InputButtons.Fire;
                return cmd;
            }

            _seenSince = -1;
            // Patrol: drift back and forth around home, pulled back if pushed away by walls or other events.
            SVector3 fromHome = self.Motor.Position - _home;
            fromHome.Y = 0;
            if (fromHome.Length() > 4f)
            {
                SVector3 back = -SVector3.Normalize(fromHome);
                cmd.Yaw = (float)Math.Atan2(back.X, back.Z);
                cmd.MoveZ = PatrolSpeed;
            }
            else
            {
                cmd.MoveX = (float)Math.Sin(t * 0.9f + _phase) * PatrolSpeed;
            }
            cmd.Pitch = 0;
            return cmd;
        }
    }
}
