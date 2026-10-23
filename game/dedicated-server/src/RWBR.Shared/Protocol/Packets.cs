using System;
using System.Collections.Generic;

namespace RWBR.Shared.Protocol
{
    public enum PacketType : byte
    {
        // Client -> server
        Hello = 1,
        Input = 2,
        Leave = 3,

        // Server -> client
        Welcome = 10,
        Reject = 11,
        Snapshot = 12,
        Eliminated = 13,
        MatchCompleted = 14,
    }

    public enum RejectReason : byte
    {
        ProtocolMismatch = 1,
        MatchFull = 2,
        MatchInProgress = 3,
        InvalidSessionToken = 4,
        InvalidTicket = 5,
        TicketExpired = 6,
        WrongMatch = 7,
        TicketReused = 8,
    }

    public enum MatchPhase : byte
    {
        WaitingForPlayers = 0,
        Countdown = 1,
        Active = 2,
        Completed = 3,
    }

    public enum CompletionReason : byte
    {
        Winner = 0,
        Timeout = 1,
        Cancelled = 2,
    }

    [Flags]
    public enum PlayerFlags : byte
    {
        None = 0,
        Alive = 1,
        Connected = 2,
        Reloading = 4,
        Crouched = 8,
        Healing = 16,
        HasWeapon = 32,
        Grounded = 64,
        /// <summary>Aiming down sights (others see the raised weapon and the tighter aim).</summary>
        Aiming = 128,
    }

    public static class ProtocolInfo
    {
        public const ushort Version = 7;
        /// <summary>Ground items (dropped or left by eliminated players) start at this id; lower ids are map loot spots.</summary>
        public const ushort FirstGroundItemId = 0x8000;
        public const ushort NoPlayer = ushort.MaxValue;
        public const int MaxInputsPerPacket = 3;
        public const int MaxNameBytes = 24;
    }

    public sealed class HelloPacket
    {
        public ushort ProtocolVersion = ProtocolInfo.Version;
        /// <summary>Empty for a new join; the token from the last Welcome to reconnect.</summary>
        public Guid SessionToken;
        public string Name = "";
        /// <summary>Signed join ticket from the platform API (empty on servers running without tickets).</summary>
        public byte[] Ticket = Array.Empty<byte>();
    }

    public sealed class WelcomePacket
    {
        public ushort PlayerId;
        public Guid SessionToken;
        public ushort TickRate;
        public byte MaxPlayers;
        public string MapVersion = "";
        /// <summary>WeaponCatalog.BalanceVersion on the server; the client's catalog must match.</summary>
        public ushort WeaponBalanceVersion;
    }

    public struct SnapshotEntry
    {
        public ushort PlayerId;
        public float X, Y, Z, Yaw;
        /// <summary>View pitch in radians, positive up (1 degree on the wire), for other players' aim and tracers.</summary>
        public float Pitch;
        public byte Health;
        public byte Armor;
        public byte Ammo;
        public PlayerFlags Flags;
        /// <summary>Catalog index of the weapon in hand (WeaponCatalog.None when unarmed).</summary>
        public byte Weapon;
        /// <summary>Increments per trigger pull; a change means this player fired since the last snapshot.</summary>
        public byte ShotCounter;
    }

    public sealed class SnapshotPacket
    {
        public uint Tick;
        /// <summary>Tick of the snapshot this one was delta-encoded against (0 = full snapshot).</summary>
        public uint BaselineTick;
        public MatchPhase Phase;
        public ushort PhaseTicksLeft;
        public float ZoneX, ZoneZ, ZoneRadius;
        /// <summary>Last input sequence the server processed for the recipient (for client reconciliation).</summary>
        public uint AckSequence;
        public byte AliveCount;
        /// <summary>Recipient-only inventory (other players' reserves are not broadcast).</summary>
        public ushort SelfReserveAmmo;
        public byte SelfMedkits;
        /// <summary>Recipient's vertical speed, so client prediction can replay jumps and falls exactly.</summary>
        public float SelfVerticalVelocity;
        /// <summary>Recipient's exact position (snapshot entries are quantised to 1 cm): exact reconciliation.</summary>
        public float SelfX, SelfY, SelfZ;
        /// <summary>Recipient's weapon slots: catalog index (None = empty) and magazine rounds.</summary>
        public byte SelfActiveSlot;
        public byte[] SelfSlotWeapon = { 255, 255 };
        public byte[] SelfSlotAmmo = { 0, 0 };
        public List<SnapshotEntry> Players = new List<SnapshotEntry>();
        /// <summary>Bit i set = loot spawn i has been taken.</summary>
        public byte[] LootTaken = Array.Empty<byte>();
        /// <summary>Dropped items near the recipient (nearest first, at most <see cref="SnapshotCodec.MaxGroundItems"/>).</summary>
        public List<GroundItemEntry> Ground = new List<GroundItemEntry>();
        /// <summary>Grenades and smoke clouds near the recipient.</summary>
        public List<ProjectileEntry> Projectiles = new List<ProjectileEntry>();

        // Recipient-only equipment and backpack.
        public byte SelfVest, SelfHelmet, SelfBackpack;
        public byte SelfHelmetPoints;
        public ItemKind SelfHealKind;
        public byte[] SelfSlotAttach = { 0, 0 };
        /// <summary>Backpack contents: units per item kind (zero counts are left out).</summary>
        public List<ItemCount> SelfItems = new List<ItemCount>();

        public bool IsLootTaken(int id) => id / 8 < LootTaken.Length && (LootTaken[id / 8] & (1 << (id % 8))) != 0;

        public int SelfCount(ItemKind kind)
        {
            foreach (ItemCount c in SelfItems)
                if (c.Kind == kind) return c.Count;
            return 0;
        }
    }

    public struct ItemCount
    {
        public ItemKind Kind;
        public ushort Count;
    }

    /// <summary>An item lying on the ground. Positions are sent in decimetres.</summary>
    public struct GroundItemEntry
    {
        public ushort Id;
        public ItemKind Kind;
        public ushort Count;
        public byte Ammo;
        public Attachments Attach;
        public float X, Y, Z;
    }

    public enum ProjectileState : byte
    {
        Flying = 0,
        /// <summary>A frag that just went off (shown for a few ticks for the blast effect).</summary>
        Exploded = 1,
        /// <summary>A smoke grenade's cloud.</summary>
        Cloud = 2,
    }

    public struct ProjectileEntry
    {
        public ushort Id;
        public ItemKind Kind;
        public ProjectileState State;
        public float X, Y, Z;
    }

    public sealed class EliminatedPacket
    {
        public ushort VictimId;
        public ushort KillerId = ProtocolInfo.NoPlayer;
        public byte Placement;
    }

    public struct PlacementEntry
    {
        public ushort PlayerId;
        public byte Placement;
        public byte Kills;
    }

    public sealed class MatchCompletedPacket
    {
        public CompletionReason Reason;
        public ushort WinnerId = ProtocolInfo.NoPlayer;
        public List<PlacementEntry> Placements = new List<PlacementEntry>();
    }

    /// <summary>Binary encode/decode. Decoders throw <see cref="PacketFormatException"/> on malformed input.</summary>
    public static class PacketCodec
    {
        public static byte[] Encode(HelloPacket p)
        {
            var w = new PacketWriter(PacketType.Hello);
            w.U16(p.ProtocolVersion);
            w.Guid(p.SessionToken);
            w.Str(p.Name, ProtocolInfo.MaxNameBytes);
            w.U8((byte)p.Ticket.Length);
            w.Bytes(p.Ticket);
            return w.ToArray();
        }

        public static HelloPacket DecodeHello(PacketReader r)
        {
            var p = new HelloPacket
            {
                ProtocolVersion = r.U16(),
                SessionToken = r.Guid(),
                Name = r.Str(ProtocolInfo.MaxNameBytes),
            };
            p.Ticket = r.Bytes(r.U8());
            return p;
        }

        /// <summary>Inputs newest first; the redundant older ones cover packet loss.</summary>
        public static byte[] Encode(IReadOnlyList<InputCommand> inputs)
        {
            int n = Math.Min(inputs.Count, ProtocolInfo.MaxInputsPerPacket);
            var w = new PacketWriter(PacketType.Input);
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                InputCommand c = inputs[i];
                w.U32(c.Sequence);
                w.U32(c.AckTick);
                w.F32(c.MoveX);
                w.F32(c.MoveZ);
                w.F32(c.Yaw);
                w.F32(c.Pitch);
                w.U16((ushort)c.Buttons);
                w.U8((byte)c.Item);
                w.U16(c.Target);
            }
            return w.ToArray();
        }

        public static List<InputCommand> DecodeInput(PacketReader r)
        {
            int n = r.U8();
            if (n < 1 || n > ProtocolInfo.MaxInputsPerPacket) throw new PacketFormatException("input count");
            var list = new List<InputCommand>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new InputCommand
                {
                    Sequence = r.U32(),
                    AckTick = r.U32(),
                    MoveX = r.F32(),
                    MoveZ = r.F32(),
                    Yaw = r.F32(),
                    Pitch = r.F32(),
                    Buttons = (InputButtons)r.U16(),
                    Item = (ItemKind)r.U8(),
                    Target = r.U16(),
                });
            }
            return list;
        }

        public static byte[] EncodeLeave() => new PacketWriter(PacketType.Leave).ToArray();

        public static byte[] Encode(WelcomePacket p)
        {
            var w = new PacketWriter(PacketType.Welcome);
            w.U16(p.PlayerId);
            w.Guid(p.SessionToken);
            w.U16(p.TickRate);
            w.U8(p.MaxPlayers);
            w.Str(p.MapVersion, 64);
            w.U16(p.WeaponBalanceVersion);
            return w.ToArray();
        }

        public static WelcomePacket DecodeWelcome(PacketReader r) => new WelcomePacket
        {
            PlayerId = r.U16(),
            SessionToken = r.Guid(),
            TickRate = r.U16(),
            MaxPlayers = r.U8(),
            MapVersion = r.Str(64),
            WeaponBalanceVersion = r.U16(),
        };

        public static byte[] EncodeReject(RejectReason reason)
        {
            var w = new PacketWriter(PacketType.Reject);
            w.U8((byte)reason);
            return w.ToArray();
        }

        public static RejectReason DecodeReject(PacketReader r) => (RejectReason)r.U8();

        /// <summary>Full snapshot (no baseline). See <see cref="SnapshotCodec"/> for delta encoding.</summary>
        public static byte[] Encode(SnapshotPacket p) => SnapshotCodec.Encode(p, null);

        /// <summary>Decodes a full snapshot; delta snapshots need <see cref="SnapshotCodec.Decode"/> with a history.</summary>
        public static SnapshotPacket DecodeSnapshot(PacketReader r) => SnapshotCodec.Decode(r, null);

        public static byte[] Encode(EliminatedPacket p)
        {
            var w = new PacketWriter(PacketType.Eliminated);
            w.U16(p.VictimId);
            w.U16(p.KillerId);
            w.U8(p.Placement);
            return w.ToArray();
        }

        public static EliminatedPacket DecodeEliminated(PacketReader r) => new EliminatedPacket
        {
            VictimId = r.U16(),
            KillerId = r.U16(),
            Placement = r.U8(),
        };

        public static byte[] Encode(MatchCompletedPacket p)
        {
            var w = new PacketWriter(PacketType.MatchCompleted);
            w.U8((byte)p.Reason);
            w.U16(p.WinnerId);
            w.U8((byte)p.Placements.Count);
            foreach (PlacementEntry e in p.Placements)
            {
                w.U16(e.PlayerId);
                w.U8(e.Placement);
                w.U8(e.Kills);
            }
            return w.ToArray();
        }

        public static MatchCompletedPacket DecodeMatchCompleted(PacketReader r)
        {
            var p = new MatchCompletedPacket { Reason = (CompletionReason)r.U8(), WinnerId = r.U16() };
            int n = r.U8();
            for (int i = 0; i < n; i++)
                p.Placements.Add(new PlacementEntry { PlayerId = r.U16(), Placement = r.U8(), Kills = r.U8() });
            return p;
        }
    }
}
