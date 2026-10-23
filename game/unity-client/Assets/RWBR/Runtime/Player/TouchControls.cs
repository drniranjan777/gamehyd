using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace RWBR.Client.Player
{
    public enum TouchButton
    {
        Fire,
        Jump,
        Crouch,
        Reload,
        Heal,
        Interact,
        Inventory,
        Pause,
        Switch,
        Aim,
    }

    /// <summary>
    /// Mobile controls: floating move stick on the left, drag-to-look on the right (also while holding Fire),
    /// AIM toggles aiming down sights, and on-screen buttons. Layout is in screen fractions so it scales with resolution. Drawn by the HUD.
    /// </summary>
    public sealed class TouchControls : IPlayerInputSource
    {
        public struct ButtonLayout
        {
            public TouchButton Button;
            public string Label;
            /// <summary>Centre in screen fractions, origin bottom-left (Input System convention).</summary>
            public Vector2 Center;
            /// <summary>Radius as a fraction of screen height.</summary>
            public float Radius;
        }

        private enum Role
        {
            Stick,
            Look,
            Button,
        }

        private struct Tracked
        {
            public Role Role;
            public TouchButton Button;
            public Vector2 Origin;
            public Vector2 Last;
        }

        public const float StickRadiusFraction = 0.11f;
        private const float LookDegreesPerPixel = 0.12f;

        public static readonly ButtonLayout[] Layout =
        {
            new ButtonLayout { Button = TouchButton.Fire, Label = "FIRE", Center = new Vector2(0.86f, 0.32f), Radius = 0.10f },
            new ButtonLayout { Button = TouchButton.Jump, Label = "JUMP", Center = new Vector2(0.94f, 0.13f), Radius = 0.065f },
            new ButtonLayout { Button = TouchButton.Crouch, Label = "CROUCH", Center = new Vector2(0.81f, 0.10f), Radius = 0.065f },
            new ButtonLayout { Button = TouchButton.Reload, Label = "RELOAD", Center = new Vector2(0.95f, 0.47f), Radius = 0.055f },
            new ButtonLayout { Button = TouchButton.Heal, Label = "HEAL", Center = new Vector2(0.70f, 0.10f), Radius = 0.055f },
            new ButtonLayout { Button = TouchButton.Interact, Label = "PICK UP", Center = new Vector2(0.64f, 0.42f), Radius = 0.065f },
            new ButtonLayout { Button = TouchButton.Inventory, Label = "BAG", Center = new Vector2(0.05f, 0.90f), Radius = 0.05f },
            new ButtonLayout { Button = TouchButton.Pause, Label = "| |", Center = new Vector2(0.96f, 0.90f), Radius = 0.045f },
            new ButtonLayout { Button = TouchButton.Switch, Label = "SWAP", Center = new Vector2(0.80f, 0.55f), Radius = 0.05f },
            new ButtonLayout { Button = TouchButton.Aim, Label = "AIM", Center = new Vector2(0.73f, 0.27f), Radius = 0.06f },
        };

        private readonly Dictionary<int, Tracked> _touches = new Dictionary<int, Tracked>();
        private readonly HashSet<TouchButton> _held = new HashSet<TouchButton>();

        public TouchControls()
        {
            if (!EnhancedTouchSupport.enabled) EnhancedTouchSupport.Enable();
        }

        /// <summary>Show touch UI: a touchscreen is present, or forced for editor testing.</summary>
        public bool Visible => ForceVisible || Touchscreen.current != null;
        public bool ForceVisible { get; set; }
        /// <summary>Set by the game each frame: the pick-up button only responds when something is in reach.</summary>
        public bool InteractAvailable { get; set; }

        /// <summary>Current stick origin and knob in screen pixels (bottom-left origin), for drawing.</summary>
        public Vector2? StickOrigin { get; private set; }
        public Vector2 StickKnob { get; private set; }

        public bool IsHeld(TouchButton b) => _held.Contains(b);

        public PlayerInputFrame Read()
        {
            var f = new PlayerInputFrame { SelectSlot = -1 };
            StickOrigin = null;
            _held.Clear();
            float h = Screen.height;

            var seen = new HashSet<int>();
            foreach (ETouch t in ETouch.activeTouches)
            {
                int id = t.touchId;
                seen.Add(id);
                Vector2 pos = t.screenPosition;

                if (!_touches.TryGetValue(id, out Tracked tr))
                {
                    tr = Classify(pos, ref f);
                    _touches[id] = tr;
                }

                switch (tr.Role)
                {
                    case Role.Stick:
                        Vector2 offset = Vector2.ClampMagnitude(pos - tr.Origin, StickRadiusFraction * h);
                        f.Move += offset / (StickRadiusFraction * h);
                        f.Sprint |= offset.y > 0.95f * StickRadiusFraction * h;
                        StickOrigin = tr.Origin;
                        StickKnob = tr.Origin + offset;
                        break;
                    case Role.Look:
                        f.Look += (pos - tr.Last) * LookDegreesPerPixel;
                        break;
                    case Role.Button:
                        _held.Add(tr.Button);
                        if (tr.Button == TouchButton.Fire)
                        {
                            f.Fire = true;
                            f.Look += (pos - tr.Last) * LookDegreesPerPixel; // drag the fire button to aim
                        }
                        break;
                }
                tr.Last = pos;
                _touches[id] = tr;
            }

            var ended = new List<int>();
            foreach (int id in _touches.Keys)
                if (!seen.Contains(id)) ended.Add(id);
            foreach (int id in ended) _touches.Remove(id);

            f.Move = Vector2.ClampMagnitude(f.Move, 1f);
            return f;
        }

        private Tracked Classify(Vector2 pos, ref PlayerInputFrame f)
        {
            foreach (ButtonLayout b in Layout)
            {
                if (b.Button == TouchButton.Interact && !InteractAvailable) continue;
                Vector2 c = new Vector2(b.Center.x * Screen.width, b.Center.y * Screen.height);
                if (Vector2.Distance(pos, c) > b.Radius * Screen.height) continue;

                switch (b.Button)
                {
                    case TouchButton.Jump: f.JumpPressed = true; break;
                    case TouchButton.Crouch: f.CrouchToggled = true; break;
                    case TouchButton.Reload: f.ReloadPressed = true; break;
                    case TouchButton.Heal: f.HealPressed = true; break;
                    case TouchButton.Interact: f.InteractPressed = true; break;
                    case TouchButton.Inventory: f.InventoryToggled = true; break;
                    case TouchButton.Pause: f.PausePressed = true; break;
                    case TouchButton.Switch: f.SwitchPressed = true; break;
                    case TouchButton.Aim: f.AimToggled = true; break;
                }
                return new Tracked { Role = Role.Button, Button = b.Button, Origin = pos, Last = pos };
            }
            bool left = pos.x < Screen.width * 0.45f;
            return new Tracked { Role = left ? Role.Stick : Role.Look, Origin = pos, Last = pos };
        }
    }
}
