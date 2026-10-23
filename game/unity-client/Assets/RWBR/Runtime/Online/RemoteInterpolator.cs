using System;
using System.Collections.Generic;
using RWBR.Shared.Protocol;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Online
{
    /// <summary>
    /// Renders other players a fixed delay behind the newest snapshot and interpolates between the two snapshots
    /// around that time, so movement stays smooth through jitter and the odd lost packet.
    /// </summary>
    public sealed class RemoteInterpolator
    {
        private readonly List<SnapshotPacket> _buffer = new List<SnapshotPacket>();
        private readonly float _delayTicks;
        private double _renderTick;

        /// <param name="delayTicks">Interpolation delay in server ticks (e.g. 3 ticks = 100 ms at 30 Hz).</param>
        public RemoteInterpolator(float delayTicks) => _delayTicks = delayTicks;

        public double RenderTick => _renderTick;

        /// <summary>The newest snapshot at or before the render time: what the player is looking at.</summary>
        public uint ViewedTick { get; private set; }

        public void Add(SnapshotPacket s)
        {
            int i = _buffer.Count;
            while (i > 0 && _buffer[i - 1].Tick > s.Tick) i--;
            if (i > 0 && _buffer[i - 1].Tick == s.Tick) return;
            _buffer.Insert(i, s);
            if (_buffer.Count > 32) _buffer.RemoveAt(0);
            if (_renderTick <= 0) _renderTick = s.Tick - _delayTicks;
        }

        /// <summary>Advance render time by real time, gently pulled towards newest - delay to absorb clock drift.</summary>
        public void Advance(float deltaSeconds, int tickRate)
        {
            if (_buffer.Count == 0) return;
            double target = _buffer[_buffer.Count - 1].Tick - _delayTicks;
            _renderTick += deltaSeconds * tickRate;
            double drift = target - _renderTick;
            if (Math.Abs(drift) > tickRate) _renderTick = target;      // way off (stall, reconnect): jump
            else _renderTick += drift * Math.Min(1, deltaSeconds * 2); // otherwise converge smoothly

            ViewedTick = _buffer[0].Tick;
            foreach (SnapshotPacket s in _buffer)
                if (s.Tick <= _renderTick) ViewedTick = s.Tick;
        }

        public bool TrySample(ushort playerId, out SVector3 position, out float yaw, out SnapshotEntry entry)
        {
            position = default;
            yaw = 0;
            entry = default;
            if (_buffer.Count == 0) return false;

            SnapshotPacket a = null, b = null;
            foreach (SnapshotPacket s in _buffer)
            {
                if (s.Tick <= _renderTick) a = s;
                else
                {
                    b = s;
                    break;
                }
            }
            if (a == null) a = b;
            if (b == null) b = a;

            bool hasA = Find(a, playerId, out SnapshotEntry ea);
            bool hasB = Find(b, playerId, out SnapshotEntry eb);
            if (!hasA && !hasB) return false;
            if (!hasA) ea = eb;
            if (!hasB) eb = ea;

            float t = b.Tick == a.Tick ? 1f : (float)((_renderTick - a.Tick) / (b.Tick - a.Tick));
            t = Math.Max(0f, Math.Min(1f, t));
            position = SVector3.Lerp(new SVector3(ea.X, ea.Y, ea.Z), new SVector3(eb.X, eb.Y, eb.Z), t);
            yaw = LerpAngle(ea.Yaw, eb.Yaw, t);
            entry = t < 0.5f ? ea : eb;
            entry.Pitch = ea.Pitch + (eb.Pitch - ea.Pitch) * t; // smooth weapon pitch between snapshots
            return true;
        }

        public IEnumerable<ushort> KnownPlayers()
        {
            if (_buffer.Count == 0) yield break;
            foreach (SnapshotEntry e in _buffer[_buffer.Count - 1].Players) yield return e.PlayerId;
        }

        public void Clear()
        {
            _buffer.Clear();
            _renderTick = 0;
        }

        private static bool Find(SnapshotPacket s, ushort id, out SnapshotEntry e)
        {
            foreach (SnapshotEntry x in s.Players)
            {
                if (x.PlayerId != id) continue;
                e = x;
                return true;
            }
            e = default;
            return false;
        }

        private static float LerpAngle(float a, float b, float t)
        {
            double d = (b - a) % (2 * Math.PI);
            if (d > Math.PI) d -= 2 * Math.PI;
            if (d < -Math.PI) d += 2 * Math.PI;
            return (float)(a + d * t);
        }
    }
}
