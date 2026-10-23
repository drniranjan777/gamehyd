using System.Linq;
using RWBR.Client.Diagnostics;
using RWBR.Client.Player;
using RWBR.Client.Accessibility;
using RWBR.Client.Combat;
using RWBR.Client.Settings;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Practice
{
    /// <summary>
    /// Greybox HUD drawn with IMGUI (no assets needed). Production UI is a Phase 4 item; this exists to make the
    /// vertical slice playable and measurable on devices. Layout is in a 1280x720-high virtual space.
    /// </summary>
    [RequireComponent(typeof(PracticeGame))]
    public sealed class PracticeHud : MonoBehaviour
    {
        private const float BaseHeight = 720f;
        /// <summary>Layout height in virtual units; smaller when the player enlarges the UI.</summary>
        private static float VirtualHeight => BaseHeight / GameSettings.UiScale;

        private PracticeGame _game;
        private PerformanceMonitor _perf;
        private readonly RWBR.Client.Player.TouchLayoutEditor _touchEditor = new RWBR.Client.Player.TouchLayoutEditor();
        private bool _editingTouch;
        private Texture2D _white;
        private GUIStyle _label, _big, _small, _center, _button;
        private float _scale;
        private float _w;

        private void Awake()
        {
            _game = GetComponent<PracticeGame>();
            _perf = FindAnyObjectByType<PerformanceMonitor>();
            _white = Texture2D.whiteTexture;
        }

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 18, normal = { textColor = Color.white } };
            _small = new GUIStyle(_label) { fontSize = 14 };
            _big = new GUIStyle(_label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _center = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
        }

        private void OnGUI()
        {
            if (_game == null || _game.Sim == null) return;
            EnsureStyles();
            _scale = Screen.height / VirtualHeight;
            _w = Screen.width / _scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1));

            PracticeSimulation sim = _game.Sim;
            Fighter p = sim.Player;

            DrawEnemyBars(sim);
            if (sim.IsRange) DrawRangeMarkers(sim);
            DrawDamageNumbers();
            DrawDamageFlash();
            if (_game.Aim.Scoped) ScopeOverlay.Draw(_w, VirtualHeight, Time.time - _game.LastHitTime, _game.LastHitWasKill);
            if (sim.Outcome == PracticeOutcome.InProgress && !_game.Paused && !_game.Aim.Scoped) DrawCrosshair(p);
            SoundCues.Draw(_w, VirtualHeight, Camera.main);
            DrawVitals(p);
            DrawWeapon(sim, p);
            DrawObjective(sim);
            DrawTutorial();
            DrawPrompt(sim, p);
            DrawPerf();
            if (_game.Touch.Visible && _game.InputOverride == null) DrawTouch();
            else if (!_game.MenusOpen && Cursor.lockState != CursorLockMode.Locked)
                GUI.Label(new Rect(0, VirtualHeight * 0.62f, _w, 30), "Click to play · WASD move · Shift sprint · Right mouse aim · Space jump · C crouch · E pick up · R reload · Q swap · H heal · Esc menu", _center);

            if (_game.InventoryOpen) DrawInventory(sim, p);
            if (_game.Paused) DrawPause();
            else _editingTouch = false;
            if (sim.Outcome != PracticeOutcome.InProgress) DrawOutcome(sim);
        }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private void Bar(Rect r, float fraction, Color fg)
        {
            Fill(r, new Color(0, 0, 0, 0.55f));
            Fill(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(fraction), r.height - 4), fg);
        }

        private void DrawCrosshair(Fighter p)
        {
            float cx = _w / 2, cy = VirtualHeight / 2;
            Camera cam = Camera.main;
            float pxPerDegree = VirtualHeight / (cam != null ? cam.fieldOfView : 60f);
            float gap = 5f + _game.CurrentSpreadDegrees * pxPerDegree;
            Color c = p.Combat.HasWeapon ? new Color(1, 1, 1, 0.9f) : new Color(1, 1, 1, 0.35f);
            Fill(new Rect(cx - gap - 10, cy - 1, 10, 2), c);
            Fill(new Rect(cx + gap, cy - 1, 10, 2), c);
            Fill(new Rect(cx - 1, cy - gap - 10, 2, 10), c);
            Fill(new Rect(cx - 1, cy + gap, 2, 10), c);

            if (Time.time - _game.LastHitTime < 0.15f)
            {
                Color hit = _game.LastHitWasKill ? new Color(1f, 0.25f, 0.2f) : Color.white;
                Matrix4x4 m = GUI.matrix;
                GUIUtility.RotateAroundPivot(45, new Vector2(cx * _scale, cy * _scale));
                Fill(new Rect(cx - 12, cy - 1.5f, 24, 3), hit);
                Fill(new Rect(cx - 1.5f, cy - 12, 3, 24), hit);
                GUI.matrix = m;
            }
        }

        private void DrawDamageNumbers()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            foreach (PracticeGame.DamageNumber n in _game.DamageNumbers)
            {
                float age = Time.time - n.Time;
                Vector3 sp = cam.WorldToScreenPoint(n.Position + Vector3.up * (0.3f + age * 0.6f));
                if (sp.z <= 0) continue;
                float x = sp.x / _scale, y = (Screen.height - sp.y) / _scale;
                var style = new GUIStyle(_label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                style.normal.textColor = new Color(n.Headshot ? 1f : 1f, n.Headshot ? 0.8f : 1f, n.Headshot ? 0.2f : 1f, 1f - age / 1.2f);
                GUI.Label(new Rect(x - 40, y - 12, 80, 24), Mathf.RoundToInt(n.Amount).ToString(), style);
            }
        }

        /// <summary>Distance labels over each lane's post.</summary>
        private void DrawRangeMarkers(PracticeSimulation sim)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            for (int lane = 0; lane < MapLayout.RangeDistances.Length; lane++)
            {
                float d = MapLayout.RangeDistances[lane];
                var world = new Vector3(MapLayout.RangeLaneX(lane) - 2.5f, 3f, MapLayout.RangeFiringLine + d);
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0) continue;
                float x = sp.x / _scale, y = (Screen.height - sp.y) / _scale;
                Fill(new Rect(x - 30, y - 11, 60, 22), new Color(0, 0, 0, 0.5f));
                GUI.Label(new Rect(x - 30, y - 11, 60, 22), $"{d:0} m", new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter });
            }
        }

        private void DrawDamageFlash()
        {
            if (GameSettings.ReduceFlashing) return;
            float t = Time.time - _game.LastDamagedTime;
            if (t < 0.3f) Fill(new Rect(0, 0, _w, VirtualHeight), new Color(0.8f, 0f, 0f, 0.28f * (1 - t / 0.3f)));
        }

        private void DrawVitals(Fighter p)
        {
            float x = 24, y = VirtualHeight - 92;
            float hp = p.Combat.Health / GameRules.MaxHealth;
            Bar(new Rect(x, y, 260, 12), p.Combat.Armor / GameRules.MaxArmor, new Color(0.3f, 0.6f, 1f));
            Bar(new Rect(x, y + 16, 260, 22), hp, Color.Lerp(new Color(0.9f, 0.2f, 0.2f), new Color(0.3f, 0.85f, 0.4f), hp));
            GUI.Label(new Rect(x + 6, y + 15, 260, 24), $"{Mathf.CeilToInt(p.Combat.Health)}", _label);
            Combatant c = p.Combat;
            GUI.Label(new Rect(x, y + 42, 560, 24),
                $"Vest {Lvl(c.VestLevel)}  Helmet {Lvl(c.HelmetLevel)}  Bag {Lvl(c.BackpackLevel)}   Heals {c.Count(ItemKind.Bandage)}/{c.Medkits}/{c.Count(ItemKind.EnergyDrink)}", _small);

            if (c.Healing)
            {
                long total = Combatant.Ticks(Combatant.HealSeconds(c.HealKind), PracticeSimulation.TickRate);
                float frac = 1f - (c.HealDoneTick - _game.Sim.Tick) / (float)total;
                Bar(new Rect(_w / 2 - 110, VirtualHeight / 2 + 60, 220, 14), frac, new Color(0.4f, 1f, 0.5f));
                GUI.Label(new Rect(_w / 2 - 110, VirtualHeight / 2 + 76, 220, 24), $"Using {Items.Name(c.HealKind).ToLowerInvariant()}…", _center);
            }
        }

        private static string Lvl(int level) => level > 0 ? $"L{level}" : "—";

        private void DrawWeapon(PracticeSimulation sim, Fighter p)
        {
            bool touch = _game.Touch.Visible && _game.InputOverride == null;
            float x = touch ? _w - 330 : _w - 260, y = touch ? 20 : VirtualHeight - 92;
            Fill(new Rect(x, y, 236, 64), new Color(0, 0, 0, 0.45f));
            if (!p.Combat.HasWeapon)
            {
                GUI.Label(new Rect(x + 12, y + 8, 220, 24), "UNARMED", _label);
                GUI.Label(new Rect(x + 12, y + 34, 220, 24), "Find a rifle", _small);
                return;
            }
            GUI.Label(new Rect(x + 12, y + 6, 230, 24), sim.PlayerWeapon.DisplayName.ToUpperInvariant(), _small);
            DrawSlots(p, x, y - 26);
            GUI.Label(new Rect(x + 12, y + 24, 220, 34), $"<size=30>{p.Combat.Ammo}</size>  / {p.Combat.ReserveAmmo}", new GUIStyle(_label) { richText = true });
            if (p.Combat.Reloading)
            {
                long total = Combatant.Ticks(sim.PlayerWeapon.ReloadSeconds, PracticeSimulation.TickRate);
                float frac = 1f - (p.Combat.ReloadDoneTick - sim.Tick) / (float)total;
                Bar(new Rect(x, y + 66, 236, 8), frac, new Color(1f, 0.85f, 0.3f));
            }
            else if (p.Combat.Ammo == 0 && p.Combat.ReserveAmmo == 0)
            {
                GUI.Label(new Rect(x, y + 66, 236, 24), "Out of ammo", _small);
            }
        }

        private void DrawTutorial()
        {
            TutorialDirector t = _game.Tutorial;
            if (t == null || _game.MenusOpen) return;
            var r = new Rect(_w / 2 - 300, 70, 600, 58);
            Fill(r, new Color(0.1f, 0.25f, 0.45f, 0.8f));
            GUI.Label(new Rect(r.x + 14, r.y + 4, 120, 22), $"TRAINING {Mathf.Min(t.Index + 1, t.Count)}/{t.Count}", _small);
            GUI.Label(new Rect(r.x + 14, r.y + 24, r.width - 28, 30), t.Text(_game.Touch.Visible), _label);
        }

        private void DrawSlots(Fighter p, float x, float y)
        {
            for (int slot = 0; slot < Combatant.SlotCount; slot++)
            {
                int w = p.Combat.SlotWeapon(slot);
                string name = w >= 0 ? WeaponCatalog.Get(w).DisplayName : "—";
                bool active = slot == p.Combat.ActiveSlot;
                var r = new Rect(x + slot * 120, y, 116, 22);
                Fill(r, new Color(0, 0, 0, active ? 0.6f : 0.3f));
                GUI.Label(r, $" {slot + 1} {name}", new GUIStyle(_small) { normal = { textColor = active ? Color.white : new Color(1, 1, 1, 0.6f) } });
            }
        }

        private void DrawObjective(PracticeSimulation sim)
        {
            float x = 24, y = 20;
            if (sim.IsRange)
            {
                Fill(new Rect(x, y, 420, 34), new Color(0, 0, 0, 0.45f));
                int pct = _game.ShotsFired > 0 ? Mathf.RoundToInt(100f * _game.ShotsHit / _game.ShotsFired) : 0;
                string last = _game.LastHitDistance > 0 ? $"   Last hit {_game.LastHitDistance:0} m" : "";
                GUI.Label(new Rect(x + 10, y + 5, 410, 26), $"Accuracy {pct}% ({_game.ShotsHit}/{_game.ShotsFired}){last}", _label);
            }
            else
            {
                Fill(new Rect(x, y, 250, 34), new Color(0, 0, 0, 0.45f));
                GUI.Label(new Rect(x + 10, y + 5, 240, 26), $"Enemies left: {sim.EnemiesAlive} / {sim.Enemies.Count}", _label);
            }
            string area = sim.Map.DistrictAt(sim.Player.Motor.Position.X, sim.Player.Motor.Position.Z);
            if (area.Length > 0) GUI.Label(new Rect(x + 260, y + 5, 300, 26), area, _label);
            float fy = y + 44;
            foreach (PracticeGame.FeedEntry e in _game.Feed)
            {
                GUI.Label(new Rect(x, fy, 520, 22), e.Text, _small);
                fy += 20;
            }
        }

        private void DrawPrompt(PracticeSimulation sim, Fighter p)
        {
            int i = sim.NearestPickup(p);
            if (i < 0 || _game.MenusOpen) return;
            string key = _game.Touch.Visible ? "PICK UP" : "[E]";
            Rect r = new Rect(_w / 2 - 160, VirtualHeight * 0.64f, 320, 34);
            Fill(r, new Color(0, 0, 0, 0.5f));
            GUI.Label(r, $"{key}  {sim.Map.Loot[i].DisplayName}", _center);
        }

        private void DrawEnemyBars(PracticeSimulation sim)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            foreach (Fighter e in sim.Enemies)
            {
                if (!e.Combat.Alive || !_game.Views.TryGetBody(e.Id, out Transform body)) continue;
                Vector3 world = body.position + Vector3.up * (e.Motor.Height + 0.35f);
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0 || sp.z > 45f) continue;
                if (!sim.HasLineOfSight(cam.transform.position.ToShared(), e.Chest)) continue;
                float x = sp.x / _scale, y = (Screen.height - sp.y) / _scale;
                Bar(new Rect(x - 30, y, 60, 7), e.Combat.Health / GameRules.MaxHealth, new Color(0.95f, 0.3f, 0.25f));
            }
        }

        private void DrawPerf()
        {
            if (!GameSettings.ShowPerformance || _perf == null) return;
            float ms = _perf.SmoothedFrameMs;
            string batches = _perf.LastAvgBatches >= 0 ? $"  {_perf.LastAvgBatches} batches" : "";
            string text = $"{1000f / Mathf.Max(ms, 0.01f):0} fps  {ms:0.0} ms  p95 {_perf.LastP95Ms:0.0}{batches}  {GameSettings.Quality}" +
                          (_perf.LastWithinBudget ? "" : "  over budget");
            GUI.Label(new Rect(_w - 530, VirtualHeight - 26, 520, 22), text, new GUIStyle(_small) { alignment = TextAnchor.MiddleRight });
        }

        private void DrawTouch()
        {
            TouchControls t = _game.Touch;
            float h = VirtualHeight;
            if (t.StickOrigin.HasValue)
            {
                Vector2 o = ToVirtual(t.StickOrigin.Value), k = ToVirtual(t.StickKnob);
                float r = TouchControls.StickRadiusFraction * h;
                Circle(o, r, new Color(1, 1, 1, 0.12f));
                Circle(k, r * 0.45f, new Color(1, 1, 1, 0.35f));
            }
            foreach (TouchControls.ButtonLayout b in TouchControls.Layout)
            {
                if (b.Button == TouchButton.Interact && !t.InteractAvailable) continue;
                Vector2 c = new Vector2(b.Center.x * _w, (1 - b.Center.y) * h);
                bool on = t.IsHeld(b.Button) || (b.Button == TouchButton.Aim && _game.Aim.Aiming);
                float alpha = on ? 0.45f : 0.22f;
                Circle(c, b.Radius * h, new Color(1, 1, 1, alpha));
                GUI.Label(new Rect(c.x - 50, c.y - 12, 100, 24), b.Label, new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter });
            }
        }

        private Vector2 ToVirtual(Vector2 screenBottomLeft) =>
            new Vector2(screenBottomLeft.x / _scale, (Screen.height - screenBottomLeft.y) / _scale);

        private void Circle(Vector2 c, float r, Color color)
        {
            // Greybox: a square with the circle's footprint is enough to show touch targets.
            Fill(new Rect(c.x - r, c.y - r, r * 2, r * 2), color);
        }

        private Rect Panel(float width, float height, string title)
        {
            Fill(new Rect(0, 0, _w, VirtualHeight), new Color(0, 0, 0, 0.45f));
            Rect r = new Rect(_w / 2 - width / 2, VirtualHeight / 2 - height / 2, width, height);
            Fill(r, new Color(0.08f, 0.09f, 0.11f, 0.95f));
            GUI.Label(new Rect(r.x, r.y + 12, r.width, 40), title, new GUIStyle(_big) { fontSize = 28 });
            return r;
        }

        private void DrawInventory(PracticeSimulation sim, Fighter p)
        {
            Rect r = Panel(520, 360, "Inventory");
            float x = r.x + 30, y = r.y + 70;
            Combatant c = p.Combat;
            string slots = string.Join("  ·  ", new[] { 0, 1 }.Select(i => c.SlotWeapon(i) >= 0 ? WeaponCatalog.Get(c.SlotWeapon(i)).DisplayName : "—"));
            GUI.Label(new Rect(x, y, 460, 26), $"Weapons:  {slots}", _label);
            GUI.Label(new Rect(x, y + 32, 460, 26), $"Magazine:  {c.Ammo} / {sim.PlayerWeapon.MagazineSize}", _label);
            string ammo = c.HasWeapon ? Items.Name(c.ActiveAmmoKind) : "Ammo";
            GUI.Label(new Rect(x, y + 64, 460, 26), $"{ammo}:  {c.ReserveAmmo}     Backpack {Lvl(c.BackpackLevel)}: {c.Weight:0} / {c.Capacity}", _label);
            GUI.Label(new Rect(x, y + 96, 460, 26), $"Vest {Lvl(c.VestLevel)} ({c.Armor:0})   Helmet {Lvl(c.HelmetLevel)} ({c.HelmetPoints:0})", _label);
            GUI.Label(new Rect(x, y + 128, 300, 26), $"Bandages {c.Count(ItemKind.Bandage)}  Medkits {c.Medkits}  Drinks {c.Count(ItemKind.EnergyDrink)}", _label);
            GUI.enabled = c.BestHeal() != ItemKind.None && !c.Healing;
            if (GUI.Button(new Rect(x + 340, y + 124, 120, 34), "Heal", _button))
            {
                _game.RequestHeal();
                _game.InventoryOpen = false;
            }
            GUI.enabled = true;
            if (GUI.Button(new Rect(r.x + r.width / 2 - 80, r.yMax - 56, 160, 40), "Close", _button)) _game.InventoryOpen = false;
        }

        private void DrawPause()
        {
            if (_editingTouch)
            {
                if (_touchEditor.Draw(_w, VirtualHeight, _label, _small, _button)) _editingTouch = false;
                return;
            }
            Rect r = Panel(520, 590, "Paused");
            float x = r.x + 30, y = r.y + 70;

            GUI.Label(new Rect(x, y, 200, 26), $"Look sensitivity  {GameSettings.Sensitivity:0.00}", _label);
            float s = GUI.HorizontalSlider(new Rect(x, y + 32, 460, 24), GameSettings.Sensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity);
            if (!Mathf.Approximately(s, GameSettings.Sensitivity)) GameSettings.SetSensitivity(s);
            y += 60;
            GUI.Label(new Rect(x, y, 300, 26), $"Aim sensitivity  {GameSettings.AimSensitivity:0.00}", _label);
            float a = GUI.HorizontalSlider(new Rect(x, y + 32, 460, 24), GameSettings.AimSensitivity, GameSettings.MinAimSensitivity, GameSettings.MaxAimSensitivity);
            if (!Mathf.Approximately(a, GameSettings.AimSensitivity)) GameSettings.SetAimSensitivity(a);

            GUI.Label(new Rect(x, y + 70, 200, 26), "Graphics quality", _label);
            string[] names = { "Low", "Medium", "High" };
            int q = GUI.Toolbar(new Rect(x, y + 100, 460, 36), (int)GameSettings.Quality, names, _button);
            if (q != (int)GameSettings.Quality) GameSettings.SetQuality((QualityPreset)q);

            bool perf = GUI.Toggle(new Rect(x, y + 150, 460, 26), GameSettings.ShowPerformance, "  Show performance overlay");
            if (perf != GameSettings.ShowPerformance) GameSettings.SetShowPerformance(perf);
            VolumeSliders(x, y + 186);
            if (GUI.Button(new Rect(x, y + 226, 220, 44), "Touch layout", _button)) _editingTouch = true;

            if (GUI.Button(new Rect(x, r.yMax - 70, 210, 44), "Resume", _button)) _game.Paused = false;
            if (GUI.Button(new Rect(x + 250, r.yMax - 70, 210, 44), "Restart", _button)) _game.Restart();
            if (GUI.Button(new Rect(x, r.yMax - 124, 220, 44), "Play online", _button))
                UnityEngine.SceneManagement.SceneManager.LoadScene("Online");
            if (GUI.Button(new Rect(x + 240, r.yMax - 124, 220, 44), "Main menu", _button))
                UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        }

        private void VolumeSliders(float x, float y)
        {
            GUI.Label(new Rect(x, y, 140, 24), "Volume", _small);
            GUI.Label(new Rect(x + 70, y, 60, 24), "master", _small);
            float m = GUI.HorizontalSlider(new Rect(x + 130, y + 6, 120, 20), GameSettings.MasterVolume, 0, 1);
            GUI.Label(new Rect(x + 260, y, 60, 24), "effects", _small);
            float s = GUI.HorizontalSlider(new Rect(x + 320, y + 6, 120, 20), GameSettings.SfxVolume, 0, 1);
            if (!Mathf.Approximately(m, GameSettings.MasterVolume) || !Mathf.Approximately(s, GameSettings.SfxVolume))
                GameSettings.SetVolumes(m, s, GameSettings.UiVolume);
        }

        private void DrawOutcome(PracticeSimulation sim)
        {
            bool won = sim.Outcome == PracticeOutcome.Victory;
            Rect r = Panel(520, 280, won ? "VICTORY" : "ELIMINATED");
            float secs = _game.RunEndTime - _game.RunStartTime;
            GUI.Label(new Rect(r.x, r.y + 80, r.width, 30), won ? "All enemies cleared" : "Try a different route or use cover", _center);
            GUI.Label(new Rect(r.x, r.y + 120, r.width, 30), $"Eliminations {_game.Kills} / {sim.Enemies.Count}   Time {secs:0}s", _center);
            if (GUI.Button(new Rect(r.x + r.width / 2 - 100, r.yMax - 70, 200, 46), "Play again", _button)) _game.Restart();
        }
    }
}
