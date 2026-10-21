using UnityEngine;
using UnityEngine.InputSystem;

namespace RWBR.Client.Player
{
    public struct PlayerInputFrame
    {
        /// <summary>x = strafe, y = forward, each -1..1.</summary>
        public Vector2 Move;
        /// <summary>Look delta in degrees this frame.</summary>
        public Vector2 Look;
        public bool Sprint;
        public bool Fire;
    }

    public interface IPlayerInputSource
    {
        PlayerInputFrame Read();
    }

    /// <summary>Keyboard/mouse and gamepad. Touch controls (virtual stick + look pad) are a Phase 1 item.</summary>
    public sealed class DeviceInputSource : IPlayerInputSource
    {
        private const float MouseDegreesPerPixel = 0.12f;
        private const float StickDegreesPerSecond = 160f;

        public PlayerInputFrame Read()
        {
            var frame = new PlayerInputFrame();
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                frame.Move.x = (kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0);
                frame.Move.y = (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0);
                frame.Sprint = kb.leftShiftKey.isPressed;
            }
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                frame.Look += mouse.delta.ReadValue() * MouseDegreesPerPixel;
                frame.Fire |= mouse.leftButton.isPressed;
            }
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                frame.Move += pad.leftStick.ReadValue();
                frame.Look += pad.rightStick.ReadValue() * StickDegreesPerSecond * Time.deltaTime;
                frame.Sprint |= pad.leftStickButton.isPressed;
                frame.Fire |= pad.rightTrigger.isPressed;
            }
            frame.Move = Vector2.ClampMagnitude(frame.Move, 1f);
            return frame;
        }
    }
}
