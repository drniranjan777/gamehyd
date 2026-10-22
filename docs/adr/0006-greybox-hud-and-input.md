# ADR 0006 — Greybox HUD and input for the vertical slice

- **Status:** Accepted for Phase 1 (22 Oct 2026); revisit in Phase 4 with the art direction
- **Context:** Phase 1 needs a playable, measurable slice on phones (HUD, inventory, settings, touch controls) before UI art exists.

## Decision

- **HUD:** Unity IMGUI (`PracticeHud`), drawn in a 720-px-high virtual space. No assets, no fonts to import, works in batch tests. It is throwaway: production UI moves to UI Toolkit or uGUI in Phase 4, together with the UI art.
- **Input:** Unity Input System 1.20 (`activeInputHandler: 2` = both, so IMGUI menus keep receiving touch/mouse events on every platform).
  - Desktop/gamepad: `DeviceInputSource`. Mouse look only while the cursor is locked; the first click locks it.
  - Touch: `TouchControls` (EnhancedTouch). Floating stick on the left 45% (full forward push = sprint); drag-to-look on the right; buttons for fire (drag to aim while firing), jump, crouch toggle, reload, heal, contextual pick-up, bag and pause.
- **Aim model (third person):** the crosshair is the camera centre. The game finds what the centre ray hits (walls, enemies, ground) and sends the yaw/pitch from the character's eye to that point, because the shared rules shoot from the eye. The move stick is rotated by the camera-vs-aim yaw difference, so movement follows the camera without drift. The server receives an ordinary `InputCommand`.

## Consequences

- The touch layout is fixed fractions of the screen; a layout editor (BRD §6 "control-layout customization") is a Phase 4 item.
- Recoil is applied to the camera only, so it shows up in the next aim the server receives. Online, it needs no server support.
