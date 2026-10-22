using System;

namespace RWBR.Shared
{
    [Flags]
    public enum InputButtons : byte
    {
        None = 0,
        Fire = 1,
        Sprint = 2,
        Reload = 4,
        Jump = 8,
        /// <summary>Held state: crouched while set (clients implement toggle-to-crouch on top of this).</summary>
        Crouch = 16,
        /// <summary>Pick up the nearest loot in range.</summary>
        Interact = 32,
        /// <summary>Start using a medkit.</summary>
        Heal = 64,
        /// <summary>Swap to the weapon in the other slot.</summary>
        SwitchWeapon = 128,
    }

    /// <summary>One client input sample. Clients send inputs, never positions, damage or results (BRD 7.2).</summary>
    public struct InputCommand
    {
        public uint Sequence;
        /// <summary>Latest server tick the client had rendered when sampling; bounds lag compensation.</summary>
        public uint AckTick;
        /// <summary>Local strafe (-1..1). Magnitude of (MoveX, MoveZ) is clamped to 1 on the server.</summary>
        public float MoveX;
        /// <summary>Local forward (-1..1).</summary>
        public float MoveZ;
        public float Yaw;
        public float Pitch;
        public InputButtons Buttons;

        public bool Has(InputButtons b) => (Buttons & b) != 0;
    }
}
