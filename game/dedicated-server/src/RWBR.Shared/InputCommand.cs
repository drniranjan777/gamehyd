using System;

namespace RWBR.Shared
{
    [Flags]
    public enum InputButtons : ushort
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
        /// <summary>Held state: aiming down sights. Tighter spread, slower movement, no sprint.</summary>
        Aim = 256,
        /// <summary>Throw a grenade of kind <see cref="InputCommand.Item"/>.</summary>
        Throw = 512,
        /// <summary>Use the heal <see cref="InputCommand.Item"/> (Heal picks the best one instead).</summary>
        UseItem = 1024,
        /// <summary>Drop <see cref="InputCommand.Item"/>: <see cref="InputCommand.Target"/> units (0 = all), or the weapon in
        /// slot Target.</summary>
        DropItem = 2048,
        /// <summary>Get into the nearest car (driver's seat first) or out of the one you're in.</summary>
        Vehicle = 4096,
    }

    /// <summary>One client input sample. Clients send inputs, never positions, damage or results (BRD 7.2).</summary>
    public struct InputCommand
    {
        public uint Sequence;
        /// <summary>Latest server tick the client had rendered when sampling; bounds lag compensation.</summary>
        public uint AckTick;
        /// <summary>Local strafe (-1..1); steering in a car's driver's seat. Magnitude of (MoveX, MoveZ) is clamped to 1 on the server.</summary>
        public float MoveX;
        /// <summary>Local forward (-1..1); throttle (forward) and brake/reverse (back) when driving. Jump is the handbrake.</summary>
        public float MoveZ;
        public float Yaw;
        public float Pitch;
        public InputButtons Buttons;
        /// <summary>The item for Throw, UseItem and DropItem.</summary>
        public ItemKind Item;
        /// <summary>DropItem: how many (0 = all) or which weapon slot. Interact: 0 picks up the nearest useful item, any
        /// other value picks up the loot spot or ground item with id Target - 1 (see <see cref="PickUpTarget"/>).</summary>
        public ushort Target;

        /// <summary>The Target value that picks up loot spot or ground item <paramref name="id"/>.</summary>
        public static ushort PickUpTarget(ushort id) => (ushort)(id + 1);

        public bool Has(InputButtons b) => (Buttons & b) != 0;
    }
}
