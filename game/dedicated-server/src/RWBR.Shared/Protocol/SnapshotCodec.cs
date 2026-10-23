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

    /// <summary>Wire quantisation: positions in centimetres, yaw in 1/65536 turns, pitch in whole degrees.</summary>
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

        public static byte Pitch(float radians) =>
            (byte)(sbyte)Math.Clamp((int)Math.Round(radians * 180.0 / Math.PI), -90, 90);

        public static float Pitch(byte q) => (float)((sbyte)q * Math.PI / 180.0);

        /// <summary>The entry as a receiver will see it. The server stores normalised entries as delta baselines.</summary>
        public static SnapshotEntry Normalize(SnapshotEntry e)
        {
            e.X = Position(Position(e.X));
            e.Y = Position(Position(e.Y));
            e.Z = Position(Position(e.Z));
            e.Yaw = Yaw(Yaw(e.Yaw));
            e.Pitch = Pitch(Pitch(e.Pitch));
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
    /// Snapshot wire format (protocol v9: v8 plus cars). Entries are delta-encoded against a baseline snapshot the receiver
    /// acknowledged (the AckTick in its inputs); unchanged players cost 3 bytes, moving players about 12.
    /// The recipient's backpack and equipment follow the header. Ground items near the recipient are sent only when they
    /// differ from the baseline (one byte otherwise); grenades and smoke near the recipient are sent while there are any.
    /// </summary>
    public static class SnapshotCodec
    {
        /// <summary>Most ground items and projectiles sent to one player per snapshot (the nearest ones).</summary>
        public const int MaxGroundItems = 24;
        public const int MaxProjectiles = 12;

        [Flags]
        private enum Field : byte
        {
            PositionDelta = 1,
            PositionFull = 2,
            /// <summary>Yaw and pitch (view direction).</summary>
            Yaw = 4,
            Health = 8,
            Armor = 16,
            Ammo = 32,
            Flags = 64,
            /// <summary>Weapon in hand and shot counter (change together often enough to share a bit).</summary>
            Weapon = 128,
            All = PositionFull | Yaw | Health | Armor | Ammo | Flags | Weapon,
        }

        /// <param name="sendGround">False when the recipient's ground items are the same as in <paramref name="baseline"/>.</param>
        public static byte[] Encode(SnapshotPacket p, SnapshotPacket? baseline, bool sendGround = true)
        {
            var w = new PacketWriter(PacketType.Snapshot);
            w.U32(p.Tick);
            w.U32(baseline?.Tick ?? 0);
            w.U8((byte)p.Phase);
            w.U16(p.PhaseTicksLeft);
            w.F32(p.ZoneX);
            w.F32(p.ZoneZ);
            w.F32(p.ZoneRadius);
            w.F32(p.NextZoneX);
            w.F32(p.NextZoneZ);
            w.F32(p.NextZoneRadius);
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
                w.U8(p.SelfSlotAttach[i]);
            }
            w.U8(p.SelfVest);
            w.U8(p.SelfHelmet);
            w.U8(p.SelfHelmetPoints);
            w.U8(p.SelfBackpack);
            w.U8((byte)p.SelfHealKind);
            int items = Math.Min(p.SelfItems.Count, 255);
            w.U8((byte)items);
            for (int i = 0; i < items; i++)
            {
                w.U8((byte)p.SelfItems[i].Kind);
                w.U16(p.SelfItems[i].Count);
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

            if (baseline != null && !sendGround) w.U8(0);
            else
            {
                int n = Math.Min(p.Ground.Count, MaxGroundItems);
                w.U8((byte)(n + 1));
                for (int i = 0; i < n; i++)
                {
                    GroundItemEntry g = p.Ground[i];
                    w.U16(g.Id);
                    w.U8((byte)g.Kind);
                    w.U16(g.Count);
                    w.U8(g.Ammo);
                    w.U8((byte)g.Attach);
                    WriteDecimetres(w, g.X, g.Y, g.Z);
                }
            }
            int pn = Math.Min(p.Projectiles.Count, MaxProjectiles);
            w.U8((byte)pn);
            for (int i = 0; i < pn; i++)
            {
                ProjectileEntry e = p.Projectiles[i];
                w.U16(e.Id);
                w.U8((byte)e.Kind);
                w.U8((byte)e.State);
                WriteDecimetres(w, e.X, e.Y, e.Z);
            }
            WriteVehicles(w, p.Vehicles, baseline?.Vehicles);
            return w.ToArray();
        }

        [Flags]
        private enum CarField : byte
        {
            PositionDelta = 1,
            PositionFull = 2,
            Yaw = 4,
            Speed = 8,
            Health = 16,
            Seats = 32,
            All = PositionFull | Yaw | Speed | Health | Seats,
        }

        /// <summary>A car as the receiver will see it (the server keeps normalised entries as delta baselines).</summary>
        public static VehicleEntry Normalize(VehicleEntry v)
        {
            v.X = Quantize.Position(Quantize.Position(v.X));
            v.Y = Quantize.Position(Quantize.Position(v.Y));
            v.Z = Quantize.Position(Quantize.Position(v.Z));
            v.Yaw = Quantize.Yaw(Quantize.Yaw(v.Yaw));
            v.Speed = SpeedOf(SpeedQ(v.Speed));
            return v;
        }

        private static short SpeedQ(float mps) => (short)Math.Clamp((int)Math.Round(mps * 10f), short.MinValue, short.MaxValue);
        private static float SpeedOf(short q) => q / 10f;

        /// <summary>Only cars that changed since the baseline are written (with only the fields that changed).</summary>
        private static void WriteVehicles(PacketWriter w, List<VehicleEntry> cars, List<VehicleEntry>? baseline)
        {
            var changed = new List<(VehicleEntry Car, CarField Mask, int Dx, int Dy, int Dz)>();
            foreach (VehicleEntry v in cars)
            {
                int x = Quantize.Position(v.X), y = Quantize.Position(v.Y), z = Quantize.Position(v.Z);
                VehicleEntry? b = null;
                if (baseline != null)
                    foreach (VehicleEntry o in baseline)
                        if (o.Id == v.Id) b = o;
                if (b is not { } bb)
                {
                    changed.Add((v, CarField.All, 0, 0, 0));
                    continue;
                }
                CarField mask = 0;
                int dx = x - Quantize.Position(bb.X), dy = y - Quantize.Position(bb.Y), dz = z - Quantize.Position(bb.Z);
                if (dx != 0 || dy != 0 || dz != 0)
                    mask |= FitsShort(dx) && FitsShort(dy) && FitsShort(dz) ? CarField.PositionDelta : CarField.PositionFull;
                if (Quantize.Yaw(v.Yaw) != Quantize.Yaw(bb.Yaw)) mask |= CarField.Yaw;
                if (SpeedQ(v.Speed) != SpeedQ(bb.Speed)) mask |= CarField.Speed;
                if (v.Health != bb.Health) mask |= CarField.Health;
                if (v.Driver != bb.Driver || v.Passenger != bb.Passenger) mask |= CarField.Seats;
                if (mask != 0) changed.Add((v, mask, dx, dy, dz));
            }
            w.U8((byte)Math.Min(changed.Count, 255));
            foreach (var (v, mask, dx, dy, dz) in changed)
            {
                w.U8(v.Id);
                w.U8((byte)mask);
                if ((mask & CarField.PositionDelta) != 0)
                {
                    w.I16((short)dx);
                    w.I16((short)dy);
                    w.I16((short)dz);
                }
                if ((mask & CarField.PositionFull) != 0)
                {
                    w.I32(Quantize.Position(v.X));
                    w.I32(Quantize.Position(v.Y));
                    w.I32(Quantize.Position(v.Z));
                }
                if ((mask & CarField.Yaw) != 0) w.U16(Quantize.Yaw(v.Yaw));
                if ((mask & CarField.Speed) != 0) w.I16(SpeedQ(v.Speed));
                if ((mask & CarField.Health) != 0) w.U8(v.Health);
                if ((mask & CarField.Seats) != 0)
                {
                    w.U16(v.Driver);
                    w.U16(v.Passenger);
                }
            }
        }

        private static void ReadVehicles(PacketReader r, SnapshotPacket p, SnapshotPacket? baseline)
        {
            if (baseline != null) p.Vehicles.AddRange(baseline.Vehicles); // unchanged cars carry over
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                byte id = r.U8();
                var mask = (CarField)r.U8();
                int index = p.Vehicles.FindIndex(v => v.Id == id);
                if (index < 0 && (mask & CarField.All) != CarField.All) throw new PacketFormatException("car delta without baseline");
                VehicleEntry v = index >= 0 ? p.Vehicles[index] : new VehicleEntry { Id = id };
                int x = Quantize.Position(v.X), y = Quantize.Position(v.Y), z = Quantize.Position(v.Z);
                if ((mask & CarField.PositionDelta) != 0)
                {
                    x += r.I16();
                    y += r.I16();
                    z += r.I16();
                }
                if ((mask & CarField.PositionFull) != 0)
                {
                    x = r.I32();
                    y = r.I32();
                    z = r.I32();
                }
                v.X = Quantize.Position(x);
                v.Y = Quantize.Position(y);
                v.Z = Quantize.Position(z);
                if ((mask & CarField.Yaw) != 0) v.Yaw = Quantize.Yaw(r.U16());
                if ((mask & CarField.Speed) != 0) v.Speed = SpeedOf(r.I16());
                if ((mask & CarField.Health) != 0) v.Health = r.U8();
                if ((mask & CarField.Seats) != 0)
                {
                    v.Driver = r.U16();
                    v.Passenger = r.U16();
                }
                if (index >= 0) p.Vehicles[index] = v;
                else p.Vehicles.Add(v);
            }
        }

        private static void WriteDecimetres(PacketWriter w, float x, float y, float z)
        {
            w.I16(Decimetres(x));
            w.I16(Decimetres(y));
            w.I16(Decimetres(z));
        }

        private static short Decimetres(float metres) => (short)Math.Clamp((int)Math.Round(metres * 10f), short.MinValue, short.MaxValue);

        /// <summary>A position as the receiver sees it (decimetre precision).</summary>
        public static float RoundDecimetres(float metres) => Decimetres(metres) / 10f;

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
            p.NextZoneX = r.F32();
            p.NextZoneZ = r.F32();
            p.NextZoneRadius = r.F32();
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
                p.SelfSlotAttach[i] = r.U8();
            }
            p.SelfVest = r.U8();
            p.SelfHelmet = r.U8();
            p.SelfHelmetPoints = r.U8();
            p.SelfBackpack = r.U8();
            p.SelfHealKind = (ItemKind)r.U8();
            int items = r.U8();
            for (int i = 0; i < items; i++) p.SelfItems.Add(new ItemCount { Kind = (ItemKind)r.U8(), Count = r.U16() });

            Dictionary<ushort, SnapshotEntry>? baseById = Index(baseline);
            int n = r.U8();
            for (int i = 0; i < n; i++) p.Players.Add(ReadEntry(r, baseById));

            p.LootTaken = new byte[r.U8()];
            for (int i = 0; i < p.LootTaken.Length; i++) p.LootTaken[i] = r.U8();

            int ground = r.U8();
            if (ground == 0)
            {
                if (baseline != null) p.Ground.AddRange(baseline.Ground); // unchanged since the baseline
            }
            else
            {
                if (ground - 1 > MaxGroundItems) throw new PacketFormatException("ground items");
                for (int i = 0; i < ground - 1; i++)
                {
                    var g = new GroundItemEntry { Id = r.U16(), Kind = (ItemKind)r.U8(), Count = r.U16(), Ammo = r.U8(), Attach = (Attachments)r.U8() };
                    g.X = r.I16() / 10f;
                    g.Y = r.I16() / 10f;
                    g.Z = r.I16() / 10f;
                    p.Ground.Add(g);
                }
            }
            int pn = r.U8();
            if (pn > MaxProjectiles) throw new PacketFormatException("projectiles");
            for (int i = 0; i < pn; i++)
            {
                var e = new ProjectileEntry { Id = r.U16(), Kind = (ItemKind)r.U8(), State = (ProjectileState)r.U8() };
                e.X = r.I16() / 10f;
                e.Y = r.I16() / 10f;
                e.Z = r.I16() / 10f;
                p.Projectiles.Add(e);
            }
            ReadVehicles(r, p, baseline);
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
            byte pitch = Quantize.Pitch(e.Pitch);
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
                if (yaw != Quantize.Yaw(bb.Yaw) || pitch != Quantize.Pitch(bb.Pitch)) mask |= Field.Yaw;
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
            if ((mask & Field.Yaw) != 0)
            {
                w.U16(yaw);
                w.U8(pitch);
            }
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
            byte pitch = Quantize.Pitch(b.Pitch);
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
            if ((mask & Field.Yaw) != 0)
            {
                yaw = r.U16();
                pitch = r.U8();
            }
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
            e.Pitch = Quantize.Pitch(pitch);
            return e;
        }

        private static bool FitsShort(int v) => v >= short.MinValue && v <= short.MaxValue;
    }
}
