using RWBR.Client.Diagnostics;
using RWBR.Client.Combat;
using RWBR.Client.Player;
using RWBR.Client.Accessibility;
using RWBR.Client.Settings;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using UnityEngine;

namespace RWBR.Client.Online
{
    /// <summary>
    /// Greybox online HUD (IMGUI, ADR 0006): connection and match phase, zone warning, vitals from the server,
    /// crosshair and hit markers, feed, results, network diagnostics, pause menu and touch controls.
    /// </summary>
    [RequireComponent(typeof(OnlineGame))]
    public sealed class OnlineHud : MonoBehaviour
    {
        private const float BaseHeight = 720f;
        /// <summary>Layout height in virtual units; smaller when the player enlarges the UI.</summary>
        private static float VirtualHeight => BaseHeight / GameSettings.UiScale;

        private OnlineGame _game;
        private PerformanceMonitor _perf;
        private readonly RWBR.Client.Player.TouchLayoutEditor _touchEditor = new RWBR.Client.Player.TouchLayoutEditor();
        private bool _editingTouch;
        private GUIStyle _label, _small, _big, _center, _button, _field;
        private float _scale, _w;
        private string _addressDraft;
        private string _platformDraft;

        private void Awake()
        {
            _game = GetComponent<OnlineGame>();
            _perf = FindAnyObjectByType<PerformanceMonitor>();
        }

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 18, normal = { textColor = Color.white } };
            _small = new GUIStyle(_label) { fontSize = 14 };
            _big = new GUIStyle(_label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _center = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 20 };
        }

        private void OnGUI()
        {
            if (_game == null) return;
            EnsureStyles();
            _scale = Screen.height / VirtualHeight;
            _w = Screen.width / _scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1));

            NetClient net = _game.Net;
            if (_game.Error != null || net.State != ConnectionState.Connected)
            {
                DrawConnect(net);
                return;
            }

            SnapshotPacket s = net.Latest;
            SnapshotEntry? self = _game.Self;
            DrawDamageFlash();
            if (s != null && self.HasValue)
            {
                DrawZone(s, self.Value);
                DrawPhase(s);
                if (_game.Aim.Scoped) ScopeOverlay.Draw(_w, VirtualHeight, Time.time - _game.LastHitTime, _game.LastHitWasKill);
                else if (_game.SelfAlive && !_game.Paused) DrawCrosshair();
            SoundCues.Draw(_w, VirtualHeight, Camera.main);
                DrawVitals(s, self.Value);
                DrawNavigation(s, self.Value);
                DrawQuickBar();
                DrawPrompt();
            }
            DrawFeed();
            DrawNet(net);
            if (_game.Touch.Visible && _game.InputOverride == null) DrawTouch();
            else if (!_game.MenusOpen && Cursor.lockState != CursorLockMode.Locked)
                GUI.Label(new Rect(0, VirtualHeight * 0.62f, _w, 30), "Click to play · Right mouse aim · Esc menu", _center);
            if (_game.InventoryOpen && !_game.Paused) DrawInventory();
            if (_game.MapOpen && !_game.Paused && s != null && self.HasValue) DrawFullMap(s, self.Value);
            if (_game.Paused) DrawPause();
            else _editingTouch = false;
            if (net.Result != null) DrawResult(net);
        }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private void Bar(Rect r, float fraction, Color fg)
        {
            Fill(r, new Color(0, 0, 0, 0.55f));
            Fill(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(fraction), r.height - 4), fg);
        }

        private Rect Panel(float width, float height, string title)
        {
            Fill(new Rect(0, 0, _w, VirtualHeight), new Color(0, 0, 0, 0.45f));
            Rect r = new Rect(_w / 2 - width / 2, VirtualHeight / 2 - height / 2, width, height);
            Fill(r, new Color(0.08f, 0.09f, 0.11f, 0.95f));
            GUI.Label(new Rect(r.x, r.y + 12, r.width, 40), title, new GUIStyle(_big) { fontSize = 28 });
            return r;
        }

        private void DrawConnect(NetClient net)
        {
            Rect r = Panel(600, 440, "Online match");
            string status = _game.Error
                ?? (_game.QuickPlayActive && net.State != ConnectionState.Connecting ? _game.QuickPlayStatus : null)
                ?? (net.State == ConnectionState.Rejected
                    ? $"Server rejected the join: {net.RejectedBecause}"
                    : net.State == ConnectionState.Connecting ? $"Connecting to {_game.ServerAddress}…" : "Not connected");
            GUI.Label(new Rect(r.x + 20, r.y + 64, r.width - 40, 44), status, new GUIStyle(_center) { wordWrap = true });

            if (_platformDraft == null) _platformDraft = _game.PlatformUrl;
            if (_addressDraft == null) _addressDraft = _game.ServerAddress;
            float x = r.x + 30, w = r.width - 60;
            GUI.Label(new Rect(x, r.y + 116, 140, 30), "Name", _label);
            _game.PlayerName = GUI.TextField(new Rect(x + 140, r.y + 114, w - 140, 34), _game.PlayerName, ProtocolInfo.MaxNameBytes, _field);

            GUI.Label(new Rect(x, r.y + 162, 140, 30), "Platform", _label);
            _platformDraft = GUI.TextField(new Rect(x + 140, r.y + 160, w - 140, 34), _platformDraft, 120, _field);
            if (_game.QuickPlayActive && net.State != ConnectionState.Connecting)
            {
                if (GUI.Button(new Rect(x, r.y + 204, w, 44), "Cancel matchmaking", _button)) _game.CancelQuickPlay();
            }
            else if (GUI.Button(new Rect(x, r.y + 204, w, 44), "Quick play (sign in + matchmaking)", _button))
            {
                _game.PlatformUrl = _platformDraft.Trim();
                _game.StartQuickPlay();
            }

            GUI.Label(new Rect(x, r.y + 270, 140, 30), "Dev server", _label);
            _addressDraft = GUI.TextField(new Rect(x + 140, r.y + 268, w - 140, 34), _addressDraft, 64, _field);
            if (GUI.Button(new Rect(x, r.yMax - 70, 250, 44), "Connect directly", _button)) _game.Connect(_addressDraft);
            if (GUI.Button(new Rect(r.xMax - 280, r.yMax - 70, 250, 44), "Main menu", _button)) _game.LeaveToMenu();
        }

        private void DrawPhase(SnapshotPacket s)
        {
            string text;
            switch (s.Phase)
            {
                case MatchPhase.WaitingForPlayers:
                    text = $"Waiting for players  {s.Players.Count} / {_game.Net.Welcome?.MaxPlayers}";
                    break;
                case MatchPhase.Countdown:
                    GUI.Label(new Rect(0, VirtualHeight * 0.3f, _w, 60), $"{Mathf.CeilToInt(s.PhaseTicksLeft / (float)_game.Net.Welcome.TickRate)}", _big);
                    text = "Match starting";
                    break;
                case MatchPhase.Active:
                    text = $"Alive  {s.AliveCount}";
                    break;
                default:
                    text = "Match over";
                    break;
            }
            Fill(new Rect(_w / 2 - 150, 72, 300, 34), new Color(0, 0, 0, 0.45f));
            GUI.Label(new Rect(_w / 2 - 150, 74, 300, 30), text, _center);
        }

        private void DrawZone(SnapshotPacket s, SnapshotEntry self)
        {
            float dist = new Vector2(self.X - s.ZoneX, self.Z - s.ZoneZ).magnitude;
            if (s.Phase != MatchPhase.Active || dist <= s.ZoneRadius || (self.Flags & PlayerFlags.Alive) == 0) return;
            Fill(new Rect(0, 0, _w, VirtualHeight), new Color(0.2f, 0.3f, 1f, 0.12f));
            GUI.Label(new Rect(0, 112, _w, 30), $"Outside the safe zone — move {dist - s.ZoneRadius:0} m towards the centre", _center);
        }

        private void DrawCrosshair()
        {
            float cx = _w / 2, cy = VirtualHeight / 2;
            Camera cam = Camera.main;
            float gap = 5f + _game.CurrentSpreadDegrees * VirtualHeight / (cam != null ? cam.fieldOfView : 60f);
            var c = new Color(1, 1, 1, 0.9f);
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

        private void DrawDamageFlash()
        {
            if (GameSettings.ReduceFlashing) return;
            float t = Time.time - _game.LastDamagedTime;
            if (t < 0.3f) Fill(new Rect(0, 0, _w, VirtualHeight), new Color(0.8f, 0f, 0f, 0.28f * (1 - t / 0.3f)));
        }

        private void DrawVitals(SnapshotPacket s, SnapshotEntry self)
        {
            float x = 24, y = VirtualHeight - 92;
            Bar(new Rect(x, y, 260, 12), self.Armor / GameRules.MaxArmor, new Color(0.3f, 0.6f, 1f));
            float hp = self.Health / GameRules.MaxHealth;
            Bar(new Rect(x, y + 16, 260, 22), hp, Color.Lerp(new Color(0.9f, 0.2f, 0.2f), new Color(0.3f, 0.85f, 0.4f), hp));
            GUI.Label(new Rect(x + 6, y + 15, 260, 24), $"{self.Health}", _label);
            string state = (self.Flags & PlayerFlags.Healing) != 0 ? $"  · using {Items.Name(s.SelfHealKind).ToLowerInvariant()}…" : "";
            Combatant inv = _game.Inventory;
            string gear = inv == null ? "" :
                $"Vest {Level(inv.VestLevel)}  Helmet {Level(inv.HelmetLevel)}  Bag {Level(inv.BackpackLevel)} {inv.Weight:0}/{inv.Capacity}";
            GUI.Label(new Rect(x, y + 42, 520, 24), gear + state, _small);

            bool touch = _game.Touch.Visible && _game.InputOverride == null;
            float wx = touch ? _w - 330 : _w - 260, wy = touch ? 60 : VirtualHeight - 92;
            Fill(new Rect(wx, wy, 236, 64), new Color(0, 0, 0, 0.45f));
            if ((self.Flags & PlayerFlags.HasWeapon) == 0)
            {
                GUI.Label(new Rect(wx + 12, wy + 18, 220, 24), "UNARMED", _label);
                return;
            }
            string name = _game.SelfWeapon.DisplayName.ToUpperInvariant();
            GUI.Label(new Rect(wx + 12, wy + 6, 220, 24), (self.Flags & PlayerFlags.Reloading) != 0 ? name + " · reloading" : name, _small);
            for (int slot = 0; slot < 2; slot++)
            {
                byte w = s.SelfSlotWeapon[slot];
                bool active = slot == s.SelfActiveSlot;
                var r = new Rect(wx + slot * 120, wy - 26, 116, 22);
                Fill(r, new Color(0, 0, 0, active ? 0.6f : 0.3f));
                GUI.Label(r, $" {slot + 1} {(WeaponCatalog.IsValid(w) ? WeaponCatalog.Get(w).DisplayName : "—")}", _small);
            }
            GUI.Label(new Rect(wx + 12, wy + 24, 220, 34), $"<size=30>{self.Ammo}</size>  / {s.SelfReserveAmmo}", new GUIStyle(_label) { richText = true });
            string fitted = AttachmentNames((Attachments)s.SelfSlotAttach[s.SelfActiveSlot]);
            if (fitted.Length > 0) GUI.Label(new Rect(wx + 120, wy + 34, 116, 24), fitted, _small);
        }

        private static string Level(int level) => level > 0 ? $"L{level}" : "—";

        private readonly MapHud _mapHud = new MapHud();

        private MapHud.ZoneView ZoneOf(SnapshotPacket s) => new MapHud.ZoneView
        {
            Center = new Vector2(s.ZoneX, s.ZoneZ),
            Radius = s.ZoneRadius,
            NextCenter = new Vector2(s.NextZoneX, s.NextZoneZ),
            NextRadius = s.NextZoneRadius,
        };

        /// <summary>The compass along the top and the minimap in the top-right corner (left of the pause button on touch).</summary>
        private void DrawNavigation(SnapshotPacket s, SnapshotEntry self)
        {
            if (_game.Map == null || _game.MapOpen) return;
            var me = new Vector2(self.X, self.Z);
            MapHud.ZoneView zone = ZoneOf(s);
            _mapHud.DrawCompass(new Rect(_w / 2 - 200, 6, 400, 22), _game.ViewYaw, me, _game.Marker, zone, _small);
            bool touch = _game.Touch.Visible && _game.InputOverride == null;
            const float size = 200;
            float right = touch ? _w - 0.09f * _w : _w - 16;
            _mapHud.DrawMinimap(new Rect(right - size, 16, size, size), _game.Map, me, _game.ViewYaw, zone, _game.Marker);
            GUI.Label(new Rect(right - size, 16 + size + 2, size, 20), _game.Map.DistrictAt(self.X, self.Z), new GUIStyle(_small) { alignment = TextAnchor.UpperCenter });
        }

        /// <summary>The full map: tap or click to place a marker (again on it to clear it).</summary>
        private void DrawFullMap(SnapshotPacket s, SnapshotEntry self)
        {
            Fill(new Rect(0, 0, _w, VirtualHeight), new Color(0, 0, 0, 0.55f));
            float side = Mathf.Min(VirtualHeight - 90, _w - 40);
            var r = new Rect(_w / 2 - side / 2, 46, side, side);
            GUI.Label(new Rect(0, 10, _w, 30), "Map — tap to place a marker, tap it again to clear", _center);
            Vector2? clicked = _mapHud.DrawFullMap(r, _game.Map, new Vector2(self.X, self.Z), _game.ViewYaw, ZoneOf(s), _game.Marker, _small);
            if (clicked.HasValue) _game.SetMarker(clicked);
            if (GUI.Button(new Rect(_w / 2 - 80, r.yMax + 8, 160, 34), "Close", _button)) _game.MapOpen = false;
        }

        private static string AttachmentNames(Attachments a)
        {
            string s = "";
            if ((a & Attachments.Scope4x) != 0) s += "4x ";
            if ((a & Attachments.ExtendedMag) != 0) s += "Mag ";
            if ((a & Attachments.Grip) != 0) s += "Grip";
            return s.Trim();
        }

        /// <summary>Quick use: tap a heal to use it, tap a grenade to select it (G or THROW throws it).</summary>
        private void DrawQuickBar()
        {
            Combatant inv = _game.Inventory;
            if (inv == null || _game.MenusOpen || !_game.SelfAlive) return;
            // Along the bottom with a keyboard; on touch screens a column at the right edge, clear of the buttons.
            bool touch = _game.Touch.Visible && _game.InputOverride == null;
            float x = touch ? _w - 108 : _w / 2 - 250, y = touch ? 250 : VirtualHeight - 58;
            int i = 0;
            foreach (ItemKind k in new[] { ItemKind.Bandage, ItemKind.Medkit, ItemKind.EnergyDrink, ItemKind.Frag, ItemKind.Smoke })
            {
                int n = inv.Count(k);
                bool throwable = Items.Category(k) == ItemCategory.Throwable;
                bool selected = throwable && _game.SelectedThrowable == k;
                var r = touch ? new Rect(x, y + i * 44, 100, 40) : new Rect(x + i * 100, y, 96, 40);
                GUI.enabled = n > 0;
                if (GUI.Button(r, $"{(selected ? "▶" : "")}{ShortName(k)} {n}", _button))
                {
                    if (throwable) _game.SelectThrowable(k);
                    else _game.UseItem(k);
                }
                i++;
            }
            GUI.enabled = true;
        }

        private static string ShortName(ItemKind k)
        {
            switch (k)
            {
                case ItemKind.Bandage: return "Band.";
                case ItemKind.Medkit: return "Med";
                case ItemKind.EnergyDrink: return "Drink";
                case ItemKind.Frag: return "Frag";
                case ItemKind.Smoke: return "Smoke";
                default: return Items.Name(k);
            }
        }

        private Vector2 _bagScroll, _nearScroll;

        /// <summary>The bag screen: equipment, weapons and attachments, backpack contents, and items within reach.</summary>
        private void DrawInventory()
        {
            Combatant inv = _game.Inventory;
            if (inv == null) return;
            Rect r = Panel(Mathf.Min(_w - 40, 980), Mathf.Min(VirtualHeight - 40, 620), "Bag");
            float x = r.x + 24, y = r.y + 64, col = (r.width - 72) / 2;

            // Left: what you carry.
            GUI.Label(new Rect(x, y, col, 26), $"Backpack {Level(inv.BackpackLevel)}   weight {inv.Weight:0} / {inv.Capacity}", _label);
            y += 30;
            EquipmentRow(ref y, x, col, $"Helmet {Level(inv.HelmetLevel)}  ({inv.HelmetPoints:0} points)", inv.HelmetLevel > 0, ItemKind.Helmet1);
            EquipmentRow(ref y, x, col, $"Vest {Level(inv.VestLevel)}  ({inv.Armor:0} points)", inv.VestLevel > 0, ItemKind.Vest1);
            EquipmentRow(ref y, x, col, $"Backpack {Level(inv.BackpackLevel)}", inv.BackpackLevel > 0, ItemKind.Backpack1);
            for (int slot = 0; slot < Combatant.SlotCount; slot++)
            {
                int w = inv.SlotWeapon(slot);
                string text = w < 0 ? $"Slot {slot + 1}: empty" : $"Slot {slot + 1}: {WeaponCatalog.Get(w).DisplayName}  {inv.SlotAmmo(slot)} rds  {AttachmentNames(inv.SlotAttachments(slot))}";
                GUI.Label(new Rect(x, y, col - 90, 26), text, _small);
                GUI.enabled = w >= 0;
                if (GUI.Button(new Rect(x + col - 84, y, 84, 26), "Drop", _button)) _game.DropWeapon(slot);
                GUI.enabled = true;
                y += 30;
            }
            float listTop = y + 6, listHeight = r.yMax - 70 - listTop;
            var stored = new System.Collections.Generic.List<ItemKind>();
            for (int k = 0; k < Items.KindCount; k++)
                if (Items.Stored((ItemKind)k) && inv.Count((ItemKind)k) > 0) stored.Add((ItemKind)k);
            _bagScroll = GUI.BeginScrollView(new Rect(x, listTop, col, listHeight), _bagScroll, new Rect(0, 0, col - 20, Mathf.Max(listHeight, stored.Count * 32)));
            float iy = 0;
            if (stored.Count == 0) GUI.Label(new Rect(0, 0, col, 26), "Nothing in the backpack", _small);
            foreach (ItemKind k in stored)
            {
                int n = inv.Count(k);
                GUI.Label(new Rect(0, iy, col - 230, 26), $"{Items.Name(k)} ×{n}", _small);
                if (Items.Category(k) == ItemCategory.Heal && GUI.Button(new Rect(col - 226, iy, 66, 26), "Use", _button)) _game.UseItem(k);
                if (n > 1 && GUI.Button(new Rect(col - 156, iy, 66, 26), "Half", _button)) _game.DropItem(k, n / 2);
                if (GUI.Button(new Rect(col - 86, iy, 66, 26), "Drop", _button)) _game.DropItem(k, 0);
                iy += 32;
            }
            GUI.EndScrollView();

            // Right: what's on the ground within reach.
            float rx = x + col + 24, ry = r.y + 64;
            GUI.Label(new Rect(rx, ry, col, 26), "Nearby", _label);
            var near = _game.NearbyItems();
            float nearHeight = r.yMax - 70 - (ry + 30);
            _nearScroll = GUI.BeginScrollView(new Rect(rx, ry + 30, col, nearHeight), _nearScroll, new Rect(0, 0, col - 20, Mathf.Max(nearHeight, near.Count * 32)));
            float ny = 0;
            if (near.Count == 0) GUI.Label(new Rect(0, 0, col, 26), "Nothing within reach", _small);
            foreach (OnlineGame.NearbyItem item in near)
            {
                GUI.Label(new Rect(0, ny, col - 110, 26), item.Useful ? item.Label : $"{item.Label} (no use or no room)", _small);
                GUI.enabled = item.Useful;
                if (GUI.Button(new Rect(col - 106, ny, 86, 26), "Take", _button)) _game.PickUp(item.Id);
                GUI.enabled = true;
                ny += 32;
            }
            GUI.EndScrollView();

            string keys = _game.Touch.Visible ? "" : "  ·  Tab closes";
            GUI.Label(new Rect(r.x, r.yMax - 64, r.width, 22), $"Drop puts items on the ground next to you{keys}", _center);
            if (GUI.Button(new Rect(r.x + r.width / 2 - 80, r.yMax - 40, 160, 34), "Close", _button)) _game.InventoryOpen = false;
        }

        private void EquipmentRow(ref float y, float x, float col, string text, bool worn, ItemKind kind)
        {
            GUI.Label(new Rect(x, y, col - 90, 26), text, _small);
            GUI.enabled = worn;
            if (GUI.Button(new Rect(x + col - 84, y, 84, 26), "Drop", _button)) _game.DropItem(kind);
            GUI.enabled = true;
            y += 30;
        }

        private void DrawPrompt()
        {
            if (!_game.NearestPickup(out OnlineGame.NearbyItem item) || _game.MenusOpen) return;
            string key = _game.Touch.Visible ? "PICK UP" : "[E]";
            int more = _game.NearbyItems().Count - 1;
            string extra = more > 0 ? (_game.Touch.Visible ? $"  (+{more}: BAG)" : $"  (+{more}: Tab)") : "";
            var r = new Rect(_w / 2 - 200, VirtualHeight * 0.64f, 400, 34);
            Fill(r, new Color(0, 0, 0, 0.5f));
            GUI.Label(r, $"{key}  {item.Label}{extra}", _center);
        }

        private void DrawFeed()
        {
            float y = 70;
            foreach (OnlineGame.FeedEntry e in _game.Feed)
            {
                GUI.Label(new Rect(24, y, 600, 22), e.Text, _small);
                y += 20;
            }
        }

        private void DrawNet(NetClient net)
        {
            if (!GameSettings.ShowPerformance) return;
            ClientPrediction p = _game.Prediction;
            int deltaPct = net.SnapshotsDecoded == 0 ? 0 : 100 * net.DeltaSnapshots / net.SnapshotsDecoded;
            string fps = _perf != null ? $"{1000f / Mathf.Max(_perf.SmoothedFrameMs, 0.01f):0} fps · " : "";
            string text = $"{fps}RTT {net.RoundTripMs:0} ms · in {net.KbpsIn:0} kbps · {deltaPct}% delta · " +
                          $"corrections {p?.Corrections ?? 0} (max {p?.MaxError ?? 0:0.00} m)" + (_game.Reconnects > 0 ? $" · reconnects {_game.Reconnects}" : "");
            GUI.Label(new Rect(_w - 720, VirtualHeight - 26, 710, 22), text, new GUIStyle(_small) { alignment = TextAnchor.MiddleRight });
        }

        private void DrawTouch()
        {
            TouchControls t = _game.Touch;
            if (t.StickOrigin.HasValue)
            {
                Vector2 o = ToVirtual(t.StickOrigin.Value), k = ToVirtual(t.StickKnob);
                float r = TouchControls.StickRadiusFraction * VirtualHeight;
                Fill(new Rect(o.x - r, o.y - r, r * 2, r * 2), new Color(1, 1, 1, 0.12f));
                Fill(new Rect(k.x - r * 0.45f, k.y - r * 0.45f, r * 0.9f, r * 0.9f), new Color(1, 1, 1, 0.35f));
            }
            foreach (TouchControls.ButtonLayout b in TouchControls.Layout)
            {
                if (!t.Shows(b.Button)) continue;
                var c = new Vector2(b.Center.x * _w, (1 - b.Center.y) * VirtualHeight);
                float r = b.Radius * VirtualHeight;
                Fill(new Rect(c.x - r, c.y - r, r * 2, r * 2), new Color(1, 1, 1, t.IsHeld(b.Button) || (b.Button == TouchButton.Aim && _game.Aim.Aiming) ? 0.45f : 0.22f));
                string label = b.Button == TouchButton.Throw ? (_game.SelectedThrowable == ItemKind.Smoke ? "SMOKE" : "FRAG") : b.Label;
                GUI.Label(new Rect(c.x - 50, c.y - 12, 100, 24), label, new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter });
            }
        }

        private Vector2 ToVirtual(Vector2 screenBottomLeft) =>
            new Vector2(screenBottomLeft.x / _scale, (Screen.height - screenBottomLeft.y) / _scale);

        private void DrawPause()
        {
            if (_editingTouch)
            {
                if (_touchEditor.Draw(_w, VirtualHeight, _label, _small, _button)) _editingTouch = false;
                return;
            }
            Rect r = Panel(520, 530, "Paused");
            float x = r.x + 30, y = r.y + 70;
            GUI.Label(new Rect(x, y, 300, 26), $"Look sensitivity  {GameSettings.Sensitivity:0.00}", _label);
            float sens = GUI.HorizontalSlider(new Rect(x, y + 32, 460, 24), GameSettings.Sensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity);
            if (!Mathf.Approximately(sens, GameSettings.Sensitivity)) GameSettings.SetSensitivity(sens);
            y += 60;
            GUI.Label(new Rect(x, y, 300, 26), $"Aim sensitivity  {GameSettings.AimSensitivity:0.00}", _label);
            float aim = GUI.HorizontalSlider(new Rect(x, y + 32, 460, 24), GameSettings.AimSensitivity, GameSettings.MinAimSensitivity, GameSettings.MaxAimSensitivity);
            if (!Mathf.Approximately(aim, GameSettings.AimSensitivity)) GameSettings.SetAimSensitivity(aim);
            GUI.Label(new Rect(x, y + 70, 300, 26), "Graphics quality", _label);
            int q = GUI.Toolbar(new Rect(x, y + 100, 460, 36), (int)GameSettings.Quality, new[] { "Low", "Medium", "High" }, _button);
            if (q != (int)GameSettings.Quality) GameSettings.SetQuality((QualityPreset)q);
            bool perf = GUI.Toggle(new Rect(x, y + 150, 460, 26), GameSettings.ShowPerformance, "  Show performance and network overlay");
            if (perf != GameSettings.ShowPerformance) GameSettings.SetShowPerformance(perf);
            GUI.Label(new Rect(x, y + 186, 140, 24), "Volume", _small);
            float m = GUI.HorizontalSlider(new Rect(x + 130, y + 192, 120, 20), GameSettings.MasterVolume, 0, 1);
            float sfx = GUI.HorizontalSlider(new Rect(x + 320, y + 192, 120, 20), GameSettings.SfxVolume, 0, 1);
            if (!Mathf.Approximately(m, GameSettings.MasterVolume) || !Mathf.Approximately(sfx, GameSettings.SfxVolume))
                GameSettings.SetVolumes(m, sfx, GameSettings.UiVolume);
            if (GUI.Button(new Rect(x, y + 226, 220, 44), "Touch layout", _button)) _editingTouch = true;
            if (GUI.Button(new Rect(x, r.yMax - 70, 210, 44), "Resume", _button)) _game.Paused = false;
            if (GUI.Button(new Rect(x + 250, r.yMax - 70, 210, 44), "Leave match", _button)) _game.LeaveToMenu();
        }

        private void DrawResult(NetClient net)
        {
            MatchCompletedPacket res = net.Result;
            int place = 0;
            foreach (PlacementEntry p in res.Placements)
                if (p.PlayerId == net.PlayerId) place = p.Placement;
            bool won = res.WinnerId == net.PlayerId;
            Rect r = Panel(520, 280, won ? "WINNER" : $"#{place}");
            string winner = res.WinnerId == ProtocolInfo.NoPlayer ? "No winner" : $"Winner: Player {res.WinnerId}";
            GUI.Label(new Rect(r.x, r.y + 80, r.width, 30), $"{winner}   ·   {res.Reason}", _center);
            if (_game.Platform != null)
            {
                ProfileDto before = _game.ProfileBefore, after = _game.ProfileAfter;
                string progress = after == null
                    ? "Saving your result…"
                    : $"Level {after.level}  ·  {after.xp} XP  (+{after.xp - (before?.xp ?? 0)})";
                GUI.Label(new Rect(r.x, r.y + 118, r.width, 30), progress, _center);
            }
            if (_game.Local != null)
                GUI.Label(new Rect(r.x, r.y + 118, r.width, 30), "Offline bot match: not counted on your profile", _center);
            if (GUI.Button(new Rect(r.x + 30, r.yMax - 70, 210, 44), "Play again", _button))
            {
                if (_game.Platform != null) _game.StartQuickPlay();
                else if (_game.Local != null) _game.RestartBotMatch();
                else _game.Connect(_game.ServerAddress);
            }
            if (GUI.Button(new Rect(r.x + 280, r.yMax - 70, 210, 44), "Main menu", _button)) _game.LeaveToMenu();
            DrawReportPanel(new Rect(r.x, r.yMax + 12, r.width, 0));
        }

        private static readonly string[] ReportReasons = { "CHEATING", "HARASSMENT", "INAPPROPRIATE_NAME", "GRIEFING" };
        private int _reportReason;
        private bool _reportOpen;
        private Vector2 _reportScroll;

        /// <summary>Below the result: report another player from this match (server-confirmed roster only).</summary>
        private void DrawReportPanel(Rect at)
        {
            MatchResultDto result = _game.PlatformResult;
            if (result?.participants == null || _game.Platform == null) return;
            string self = _game.Platform.UserId;
            if (!_reportOpen)
            {
                if (GUI.Button(new Rect(at.x + 155, at.y, 210, 36), "Report a player", _button)) _reportOpen = true;
                return;
            }
            int others = 0;
            foreach (ResultParticipantDto p in result.participants) if (p.playerId != self) others++;
            Rect r = new Rect(at.x, at.y, at.width, 92 + Mathf.Min(others, 3) * 34);
            Fill(r, new Color(0.08f, 0.09f, 0.11f, 0.95f));
            _reportReason = GUI.Toolbar(new Rect(r.x + 12, r.y + 10, r.width - 24, 30), _reportReason,
                new[] { "Cheating", "Harassment", "Name", "Griefing" });
            _reportScroll = GUI.BeginScrollView(new Rect(r.x, r.y + 50, r.width, Mathf.Min(others, 3) * 34), _reportScroll,
                new Rect(0, 0, r.width - 20, others * 34));
            float y = 0;
            foreach (ResultParticipantDto p in result.participants)
            {
                if (p.playerId == self) continue;
                GUI.Label(new Rect(16, y + 4, r.width - 200, 26), $"#{p.placement}  {p.displayName}  ·  {p.kills} elims", _label);
                bool done = _game.HasReported(p.playerId);
                GUI.enabled = !done;
                if (GUI.Button(new Rect(r.width - 170, y, 134, 30), done ? "Reported" : "Report")) _game.ReportPlayer(p.playerId, ReportReasons[_reportReason]);
                GUI.enabled = true;
                y += 34;
            }
            GUI.EndScrollView();
            if (_game.ReportStatus != null) GUI.Label(new Rect(r.x + 16, r.yMax - 32, r.width - 32, 26), _game.ReportStatus, _label);
        }
    }
}
