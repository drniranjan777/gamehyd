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
    }

    public static class ProtocolInfo
    {
        public const ushort Version = 1;
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
    }

    public sealed class WelcomePacket
    {
        public ushort PlayerId;
        public Guid SessionToken;
        public ushort TickRate;
        public byte MaxPlayers;
        public string MapVersion = "";
    }

    public struct SnapshotEntry
    {
        public ushort PlayerId;
        public float X, Y, Z, Yaw;
        public byte Health;
        public byte Ammo;
        public PlayerFlags Flags;
    }

    public sealed class SnapshotPacket
    {
        public uint Tick;
        public MatchPhase Phase;
        public ushort PhaseTicksLeft;
        public float ZoneX, ZoneZ, ZoneRadius;
        /// <summary>Last input sequence the server processed for the recipient (for client reconciliation).</summary>
        public uint AckSequence;
        public byte AliveCount;
        public List<SnapshotEntry> Players = new List<SnapshotEntry>();
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
            return w.ToArray();
        }

        public static HelloPacket DecodeHello(PacketReader r) => new HelloPacket
        {
            ProtocolVersion = r.U16(),
            SessionToken = r.Guid(),
            Name = r.Str(ProtocolInfo.MaxNameBytes),
        };

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
                w.U8((byte)c.Buttons);
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
                    Buttons = (InputButtons)r.U8(),
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
            return w.ToArray();
        }

        public static WelcomePacket DecodeWelcome(PacketReader r) => new WelcomePacket
        {
            PlayerId = r.U16(),
            SessionToken = r.Guid(),
            TickRate = r.U16(),
            MaxPlayers = r.U8(),
            MapVersion = r.Str(64),
        };

        public static byte[] EncodeReject(RejectReason reason)
        {
            var w = new PacketWriter(PacketType.Reject);
            w.U8((byte)reason);
            return w.ToArray();
        }

        public static RejectReason DecodeReject(PacketReader r) => (RejectReason)r.U8();

        public static byte[] Encode(SnapshotPacket p)
        {
            var w = new PacketWriter(PacketType.Snapshot);
            w.U32(p.Tick);
            w.U8((byte)p.Phase);
            w.U16(p.PhaseTicksLeft);
            w.F32(p.ZoneX);
            w.F32(p.ZoneZ);
            w.F32(p.ZoneRadius);
            w.U32(p.AckSequence);
            w.U8(p.AliveCount);
            w.U8((byte)p.Players.Count);
            foreach (SnapshotEntry e in p.Players)
            {
                w.U16(e.PlayerId);
                w.F32(e.X);
                w.F32(e.Y);
                w.F32(e.Z);
                w.F32(e.Yaw);
                w.U8(e.Health);
                w.U8(e.Ammo);
                w.U8((byte)e.Flags);
            }
            return w.ToArray();
        }

        public static SnapshotPacket DecodeSnapshot(PacketReader r)
        {
            var p = new SnapshotPacket
            {
                Tick = r.U32(),
                Phase = (MatchPhase)r.U8(),
                PhaseTicksLeft = r.U16(),
                ZoneX = r.F32(),
                ZoneZ = r.F32(),
                ZoneRadius = r.F32(),
                AckSequence = r.U32(),
                AliveCount = r.U8(),
            };
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                p.Players.Add(new SnapshotEntry
                {
                    PlayerId = r.U16(),
                    X = r.F32(),
                    Y = r.F32(),
                    Z = r.F32(),
                    Yaw = r.F32(),
                    Health = r.U8(),
                    Ammo = r.U8(),
                    Flags = (PlayerFlags)r.U8(),
                });
            }
            return p;
        }

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
