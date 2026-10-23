using System.Collections.Generic;
using UnityEngine;

namespace RWBR.Client.Player
{
    /// <summary>
    /// Settings > Touch layout: drag the on-screen buttons where your thumbs want them and change their size. Drawn with
    /// IMGUI in the menu's virtual units (width × height); on phones IMGUI receives the touch as the mouse. Saves when a
    /// drag ends, on size changes and on Done.
    /// </summary>
    public sealed class TouchLayoutEditor
    {
        private TouchButton? _dragging;
        private Vector2 _grab;

        /// <summary>Draws the editor; true when the player pressed Done.</summary>
        public bool Draw(float width, float height, GUIStyle label, GUIStyle small, GUIStyle button)
        {
            Fill(new Rect(0, 0, width, height), new Color(0.06f, 0.07f, 0.09f, 0.97f));
            HandleDrag(width, height);

            List<(TouchControls.ButtonLayout A, TouchControls.ButtonLayout B)> overlaps = TouchControls.Overlaps(width / height);
            var clashing = new HashSet<TouchButton>();
            foreach (var (a, b) in overlaps)
            {
                clashing.Add(a.Button);
                clashing.Add(b.Button);
            }

            // The stick lives anywhere on the left 45 % of the screen; show that zone.
            Fill(new Rect(0, 0, width * 0.45f, height), new Color(1, 1, 1, 0.03f));
            GUI.Label(new Rect(20, height - 40, width * 0.45f - 40, 30), "Move stick: touch anywhere here", small);

            foreach (TouchControls.ButtonLayout b in TouchControls.Layout)
            {
                Vector2 c = new Vector2(b.Center.x * width, (1 - b.Center.y) * height);
                float r = b.Radius * height;
                Color color = _dragging == b.Button ? new Color(0.4f, 0.7f, 1f, 0.55f)
                    : clashing.Contains(b.Button) ? new Color(1f, 0.35f, 0.3f, 0.45f) : new Color(1, 1, 1, 0.25f);
                Fill(new Rect(c.x - r, c.y - r, r * 2, r * 2), color);
                GUI.Label(new Rect(c.x - 60, c.y - 12, 120, 24), b.Label, new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
            }

            var panel = new Rect(width / 2 - 260, 20, 520, 150);
            Fill(panel, new Color(0, 0, 0, 0.7f));
            GUI.Label(new Rect(panel.x + 20, panel.y + 10, 480, 28), "Drag the buttons to move them", label);
            GUI.Label(new Rect(panel.x + 20, panel.y + 44, 160, 24), $"Button size {TouchControls.ButtonScale:0.00}×", small);
            float scale = GUI.HorizontalSlider(new Rect(panel.x + 180, panel.y + 50, 320, 20), TouchControls.ButtonScale,
                TouchControls.MinButtonScale, TouchControls.MaxButtonScale);
            if (!Mathf.Approximately(scale, TouchControls.ButtonScale))
            {
                TouchControls.SetButtonScale(scale);
                TouchControls.SaveLayout();
            }
            string status = overlaps.Count == 0 ? "No buttons overlap." : $"{overlaps[0].A.Label} overlaps {overlaps[0].B.Label}: move one apart.";
            GUI.Label(new Rect(panel.x + 20, panel.y + 76, 480, 24), status, small);
            if (GUI.Button(new Rect(panel.x + 20, panel.y + 104, 150, 36), "Reset", button)) TouchControls.ResetLayout();
            bool done = GUI.Button(new Rect(panel.xMax - 170, panel.y + 104, 150, 36), "Done", button);
            if (done) TouchControls.SaveLayout();
            return done;
        }

        private void HandleDrag(float width, float height)
        {
            Event e = Event.current;
            Vector2 fraction = new Vector2(e.mousePosition.x / width, 1 - e.mousePosition.y / height);
            switch (e.type)
            {
                case EventType.MouseDown:
                    foreach (TouchControls.ButtonLayout b in TouchControls.Layout)
                    {
                        Vector2 c = new Vector2(b.Center.x * width, (1 - b.Center.y) * height);
                        if (Vector2.Distance(c, e.mousePosition) > b.Radius * height) continue;
                        if (new Rect(width / 2 - 260, 20, 520, 150).Contains(e.mousePosition)) break; // the panel's controls win
                        _dragging = b.Button;
                        _grab = fraction - b.Center;
                        e.Use();
                        break;
                    }
                    break;
                case EventType.MouseDrag when _dragging.HasValue:
                    TouchControls.MoveButton(_dragging.Value, fraction - _grab);
                    e.Use();
                    break;
                case EventType.MouseUp when _dragging.HasValue:
                    _dragging = null;
                    TouchControls.SaveLayout();
                    e.Use();
                    break;
            }
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
