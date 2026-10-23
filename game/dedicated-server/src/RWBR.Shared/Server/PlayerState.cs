#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Server
{
    /// <summary>Authoritative per-player state. Lives only in server memory (never in PostgreSQL).</summary>
    public sealed class PlayerState
    {
        public PlayerState(ushort id, string name, IPEndPoint endPoint, int historyLength)
        {
            Id = id;
            Name = name;
            EndPoint = endPoint;
            History = new MotorState[historyLength];
        }

        public ushort Id { get; }
        public string Name { get; }
        public IPEndPoint EndPoint { get; set; }
        public Guid SessionToken { get; set; } = Guid.NewGuid();

        /// <summary>Movement state; a field so it can be advanced by reference.</summary>
        public MotorState Motor;
        public Combatant Combat { get; } = new();

        public Vector3 Position
        {
            get => Motor.Position;
            set => Motor.Position = value;
        }

        public float Health
        {
            get => Combat.Health;
            set => Combat.Health = value;
        }

        public int Ammo => Combat.Ammo;
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        /// <summary>Aiming down sights on the last processed input (armed players only).</summary>
        public bool Aiming { get; set; }
        /// <summary>In the match (not eliminated). Separate from Combat.Alive so disconnect forfeits are explicit.</summary>
        public bool Alive { get; set; } = true;
        public bool Connected { get; set; } = true;
        public long LastHeardTick { get; set; }

        public uint LastProcessedSequence { get; set; }
        /// <summary>Latest snapshot tick the client reported decoding; the delta baseline for its next snapshot.</summary>
        public uint LastAckedSnapshot { get; set; }
        /// <summary>Platform account (ticket mode only).</summary>
        public Guid AccountId { get; set; }
        /// <summary>Verifies the tag on this player's packets (ticket mode only).</summary>
        public PacketAuth? Auth { get; set; }
        public List<InputCommand> PendingInputs { get; } = new();

        public int Kills { get; set; }
        /// <summary>Increments on every trigger pull (wraps); clients turn changes into remote shot effects.</summary>
        public byte ShotCounter { get; set; }
        /// <summary>Tick of elimination (0 while alive), for survival time in the result.</summary>
        public long EliminatedAtTick { get; set; }
        public int Placement { get; set; }

        /// <summary>Ring buffer of motor states by tick, used to rewind targets for lag compensation.</summary>
        public MotorState[] History { get; }

        /// <summary>Hash of the ground items sent in each recent snapshot (by tick), to resend them only on change.</summary>
        public ulong[] GroundHash { get; } = new ulong[SnapshotHistory.Size];
        public long[] GroundHashTick { get; } = new long[SnapshotHistory.Size];
    }
}
