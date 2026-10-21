using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using RWBR.Shared;

namespace RWBR.MatchServer;

/// <summary>Authoritative per-player state. Lives only in server memory (never in PostgreSQL).</summary>
public sealed class PlayerState
{
    public PlayerState(ushort id, string name, IPEndPoint endPoint, int historyLength)
    {
        Id = id;
        Name = name;
        EndPoint = endPoint;
        History = new Vector3[historyLength];
    }

    public ushort Id { get; }
    public string Name { get; }
    public IPEndPoint EndPoint { get; set; }
    public Guid SessionToken { get; set; } = Guid.NewGuid();

    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Health { get; set; } = GameRules.MaxHealth;
    public bool Alive { get; set; } = true;
    public bool Connected { get; set; } = true;
    public long LastHeardTick { get; set; }

    public uint LastProcessedSequence { get; set; }
    public List<InputCommand> PendingInputs { get; } = new();

    public int Ammo { get; set; }
    public long NextFireTick { get; set; }
    public long ReloadDoneTick { get; set; } = -1;
    public bool Reloading => ReloadDoneTick >= 0;

    public int Kills { get; set; }
    public int Placement { get; set; }

    /// <summary>Ring buffer of positions by tick, used to rewind targets for lag compensation.</summary>
    public Vector3[] History { get; }
}
