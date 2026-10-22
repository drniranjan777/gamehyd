#nullable enable
using System;
using System.Collections.Generic;

namespace RWBR.Shared.Protocol
{
    /// <summary>Raised when a delta snapshot refers to a baseline the receiver no longer has.</summary>
    public sealed class MissingBaselineException : Exception
    {
        public MissingBaselineException(uint tick) : base($"baseline {tick} not available") => Tick = tick;

        public uint Tick { get; }
    }

    /// <summary>Wire quantisation: positions in centimetres, yaw in 1/65536 turns.</summary>
    public static class Quantize
    {
        public static int Position(float metres) => (int)Math.Round(metres * 100f);
        public static float Position(int centimetres) => centimetres / 100f;

        public static ushort Yaw(float radians)
        {
            double w = Simulation.WrapYaw(radians);
            int q = (int)Math.Round((w + Math.PI) / (2 * Math.PI) * 65536.0);
            return (ushort)(q & 0xFFFF);
        }

        public static float Yaw(ushort q) => (float)(q / 65536.0 * 2 * Math.PI - Math.PI);

        /// <summary>The entry as a receiver will see it. The server stores normalised entries as delta baselines.</summary>
        public static SnapshotEntry Normalize(SnapshotEntry e)
        {
            e.X = Position(Position(e.X));
            e.Y = Position(Position(e.Y));
            e.Z = Position(Position(e.Z));
            e.Yaw = Yaw(Yaw(e.Yaw));
            return e;
        }
    }

    /// <summary>Ring of recent snapshots by tick: delta baselines on both ends of the connection.</summary>
    public sealed class SnapshotHistory
    {
        public const int Size = 64;
        private readonly SnapshotPacket?[] _ring = new SnapshotPacket?[Size];

        public void Add(SnapshotPacket p) => _ring[p.Tick % Size] = p;

        public SnapshotPacket? Get(uint tick)
        {
            if (tick == 0) return null;
            SnapshotPacket? p = _ring[tick % Size];
            return p != null && p.Tick == tick ? p : null;
        }

        public void Clear() => Array.Clear(_ring, 0, _ring.Length);
    }

    /// <summary>
    /// Snapshot wire format (protocol v3). Entries are delta-encoded against a baseline snapshot the receiver
    /// acknowledged (the AckTick in its inputs); unchanged players cost 3 bytes, moving players about 11.
    /// </summary>
    public static class SnapshotCodec
    {
        [Flags]
        private enum Field : byte
        {
            PositionDelta = 1,
            PositionFull = 2,
            Yaw = 4,
            Health = 8,
            Armor = 16,
            Ammo = 32,
            Flags = 64,
            /// <summary>Weapon in hand and shot counter (change together often enough to share a bit).</summary>
            Weapon = 128,
            All = PositionFull | Yaw | Health | Armor | Ammo | Flags | Weapon,
        }

        public static byte[] Encode(SnapshotPacket p, SnapshotPacket? baseline)
        {
            var w = new PacketWriter(PacketType.Snapshot);
            w.U32(p.Tick);
            w.U32(baseline?.Tick ?? 0);
            w.U8((byte)p.Phase);
            w.U16(p.PhaseTicksLeft);
            w.F32(p.ZoneX);
            w.F32(p.ZoneZ);
            w.F32(p.ZoneRadius);
            w.U32(p.AckSequence);
            w.U8(p.AliveCount);
            w.U16(p.SelfReserveAmmo);
            w.U8(p.SelfMedkits);
            w.F32(p.SelfVerticalVelocity);
            w.F32(p.SelfX);
            w.F32(p.SelfY);
            w.F32(p.SelfZ);
            w.U8(p.SelfActiveSlot);
            for (int i = 0; i < 2; i++)
            {
                w.U8(p.SelfSlotWeapon[i]);
                w.U8(p.SelfSlotAmmo[i]);
            }

            Dictionary<ushort, SnapshotEntry>? baseById = Index(baseline);
            w.U8((byte)p.Players.Count);
            foreach (SnapshotEntry e in p.Players)
            {
                SnapshotEntry b = default;
                bool hasBase = baseById != null && baseById.TryGetValue(e.PlayerId, out b);
                WriteEntry(w, e, hasBase ? b : (SnapshotEntry?)null);
            }

            w.U8((byte)p.LootTaken.Length);
            foreach (byte x in p.LootTaken) w.U8(x);
            return w.ToArray();
        }

        public static SnapshotPacket Decode(PacketReader r, SnapshotHistory? history)
        {
            var p = new SnapshotPacket { Tick = r.U32() };
            uint baselineTick = r.U32();
            p.BaselineTick = baselineTick;
            SnapshotPacket? baseline = null;
            if (baselineTick != 0)
            {
                baseline = history?.Get(baselineTick);
                if (baseline == null) throw new MissingBaselineException(baselineTick);
            }

            p.Phase = (MatchPhase)r.U8();
            p.PhaseTicksLeft = r.U16();
            p.ZoneX = r.F32();
            p.ZoneZ = r.F32();
            p.ZoneRadius = r.F32();
            p.AckSequence = r.U32();
            p.AliveCount = r.U8();
            p.SelfReserveAmmo = r.U16();
            p.SelfMedkits = r.U8();
            p.SelfVerticalVelocity = r.F32();
            p.SelfX = r.F32();
            p.SelfY = r.F32();
            p.SelfZ = r.F32();
            p.SelfActiveSlot = r.U8();
            for (int i = 0; i < 2; i++)
            {
                p.SelfSlotWeapon[i] = r.U8();
                p.SelfSlotAmmo[i] = r.U8();
            }

            Dictionary<ushort, SnapshotEntry>? baseById = Index(baseline);
            int n = r.U8();
            for (int i = 0; i < n; i++) p.Players.Add(ReadEntry(r, baseById));

            p.LootTaken = new byte[r.U8()];
            for (int i = 0; i < p.LootTaken.Length; i++) p.LootTaken[i] = r.U8();
            return p;
        }

        private static Dictionary<ushort, SnapshotEntry>? Index(SnapshotPacket? p)
        {
            if (p == null) return null;
            var d = new Dictionary<ushort, SnapshotEntry>(p.Players.Count);
            foreach (SnapshotEntry e in p.Players) d[e.PlayerId] = e;
            return d;
        }

        private static void WriteEntry(PacketWriter w, SnapshotEntry e, SnapshotEntry? b)
        {
            int x = Quantize.Position(e.X), y = Quantize.Position(e.Y), z = Quantize.Position(e.Z);
            ushort yaw = Quantize.Yaw(e.Yaw);
            Field mask;
            int dx = 0, dy = 0, dz = 0;
            if (b is not { } bb)
            {
                mask = Field.All;
            }
            else
            {
                mask = 0;
                dx = x - Quantize.Position(bb.X);
                dy = y - Quantize.Position(bb.Y);
                dz = z - Quantize.Position(bb.Z);
                if (dx != 0 || dy != 0 || dz != 0)
                    mask |= FitsShort(dx) && FitsShort(dy) && FitsShort(dz) ? Field.PositionDelta : Field.PositionFull;
                if (yaw != Quantize.Yaw(bb.Yaw)) mask |= Field.Yaw;
                if (e.Health != bb.Health) mask |= Field.Health;
                if (e.Armor != bb.Armor) mask |= Field.Armor;
                if (e.Ammo != bb.Ammo) mask |= Field.Ammo;
                if (e.Flags != bb.Flags) mask |= Field.Flags;
                if (e.Weapon != bb.Weapon || e.ShotCounter != bb.ShotCounter) mask |= Field.Weapon;
            }

            w.U16(e.PlayerId);
            w.U8((byte)mask);
            if ((mask & Field.PositionDelta) != 0)
            {
                w.I16((short)dx);
                w.I16((short)dy);
                w.I16((short)dz);
            }
            if ((mask & Field.PositionFull) != 0)
            {
                w.I32(x);
                w.I32(y);
                w.I32(z);
            }
            if ((mask & Field.Yaw) != 0) w.U16(yaw);
            if ((mask & Field.Health) != 0) w.U8(e.Health);
            if ((mask & Field.Armor) != 0) w.U8(e.Armor);
            if ((mask & Field.Ammo) != 0) w.U8(e.Ammo);
            if ((mask & Field.Flags) != 0) w.U8((byte)e.Flags);
            if ((mask & Field.Weapon) != 0)
            {
                w.U8(e.Weapon);
                w.U8(e.ShotCounter);
            }
        }

        private static SnapshotEntry ReadEntry(PacketReader r, Dictionary<ushort, SnapshotEntry>? baseById)
        {
            ushort id = r.U16();
            var mask = (Field)r.U8();
            SnapshotEntry b = default;
            bool hasBase = baseById != null && baseById.TryGetValue(id, out b);
            if (!hasBase && (mask & Field.All) != Field.All) throw new PacketFormatException("delta entry without baseline");

            int x = Quantize.Position(b.X), y = Quantize.Position(b.Y), z = Quantize.Position(b.Z);
            ushort yaw = Quantize.Yaw(b.Yaw);
            var e = new SnapshotEntry
            {
                PlayerId = id, Health = b.Health, Armor = b.Armor, Ammo = b.Ammo, Flags = b.Flags, Weapon = b.Weapon, ShotCounter = b.ShotCounter,
            };

            if ((mask & Field.PositionDelta) != 0)
            {
                x += r.I16();
                y += r.I16();
                z += r.I16();
            }
            if ((mask & Field.PositionFull) != 0)
            {
                x = r.I32();
                y = r.I32();
                z = r.I32();
            }
            if ((mask & Field.Yaw) != 0) yaw = r.U16();
            if ((mask & Field.Health) != 0) e.Health = r.U8();
            if ((mask & Field.Armor) != 0) e.Armor = r.U8();
            if ((mask & Field.Ammo) != 0) e.Ammo = r.U8();
            if ((mask & Field.Flags) != 0) e.Flags = (PlayerFlags)r.U8();
            if ((mask & Field.Weapon) != 0)
            {
                e.Weapon = r.U8();
                e.ShotCounter = r.U8();
            }

            e.X = Quantize.Position(x);
            e.Y = Quantize.Position(y);
            e.Z = Quantize.Position(z);
            e.Yaw = Quantize.Yaw(yaw);
            return e;
        }

        private static bool FitsShort(int v) => v >= short.MinValue && v <= short.MaxValue;
    }
}
