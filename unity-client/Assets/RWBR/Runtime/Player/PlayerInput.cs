using UnityEngine;
using UnityEngine.InputSystem;

namespace RWBR.Client.Player
{
    public struct PlayerInputFrame
    {
        /// <summary>x = strafe, y = forward, each -1..1.</summary>
        public Vector2 Move;
        /// <summary>Look delta in degrees this frame (before the sensitivity setting).</summary>
        public Vector2 Look;
        public bool Sprint;
        public bool Fire;

        // One-frame presses; the game latches them until the next simulation tick consumes them.
        public bool JumpPressed;
        public bool CrouchToggled;
        public bool ReloadPressed;
        public bool InteractPressed;
        public bool HealPressed;
        public bool InventoryToggled;
        public bool PausePressed;

        public void Merge(PlayerInputFrame other)
        {
            Move = Vector2.ClampMagnitude(Move + other.Move, 1f);
            Look += other.Look;
            Sprint |= other.Sprint;
            Fire |= other.Fire;
            JumpPressed |= other.JumpPressed;
            CrouchToggled |= other.CrouchToggled;
            ReloadPressed |= other.ReloadPressed;
            InteractPressed |= other.InteractPressed;
            HealPressed |= other.HealPressed;
            InventoryToggled |= other.InventoryToggled;
            PausePressed |= other.PausePressed;
        }
    }

    public interface IPlayerInputSource
    {
        PlayerInputFrame Read();
    }

    /// <summary>Keyboard/mouse and gamepad. Touch is handled by <see cref="TouchControls"/>.</summary>
    public sealed class DeviceInputSource : IPlayerInputSource
    {
        private const float MouseDegreesPerPixel = 0.12f;
        private const float StickDegreesPerSecond = 160f;

        /// <summary>Mouse look only while the cursor is locked (desktop), so menus stay usable.</summary>
        public bool MouseLookEnabled { get; set; } = true;

        public PlayerInputFrame Read()
        {
            var f = new PlayerInputFrame();
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                f.Move.x = (kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0);
                f.Move.y = (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0);
                f.Sprint = kb.leftShiftKey.isPressed;
                f.JumpPressed = kb.spaceKey.wasPressedThisFrame;
                f.CrouchToggled = kb.cKey.wasPressedThisFrame || kb.leftCtrlKey.wasPressedThisFrame;
                f.ReloadPressed = kb.rKey.wasPressedThisFrame;
                f.InteractPressed = kb.eKey.wasPressedThisFrame || kb.fKey.wasPressedThisFrame;
                f.HealPressed = kb.hKey.wasPressedThisFrame || kb.digit4Key.wasPressedThisFrame;
                f.InventoryToggled = kb.tabKey.wasPressedThisFrame || kb.iKey.wasPressedThisFrame;
                f.PausePressed = kb.escapeKey.wasPressedThisFrame;
            }
            Mouse mouse = Mouse.current;
            if (mouse != null && MouseLookEnabled)
            {
                f.Look += mouse.delta.ReadValue() * MouseDegreesPerPixel;
                f.Fire |= mouse.leftButton.isPressed;
            }
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                f.Move += pad.leftStick.ReadValue();
                f.Look += pad.rightStick.ReadValue() * StickDegreesPerSecond * Time.unscaledDeltaTime;
                f.Sprint |= pad.leftStickButton.isPressed;
                f.Fire |= pad.rightTrigger.isPressed;
                f.JumpPressed |= pad.buttonSouth.wasPressedThisFrame;
                f.CrouchToggled |= pad.buttonEast.wasPressedThisFrame;
                f.ReloadPressed |= pad.buttonWest.wasPressedThisFrame;
                f.InteractPressed |= pad.buttonNorth.wasPressedThisFrame;
                f.HealPressed |= pad.dpad.up.wasPressedThisFrame;
                f.InventoryToggled |= pad.selectButton.wasPressedThisFrame;
                f.PausePressed |= pad.startButton.wasPressedThisFrame;
            }
            f.Move = Vector2.ClampMagnitude(f.Move, 1f);
            return f;
        }
    }
}
