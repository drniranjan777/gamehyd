using RWBR.Client.Diagnostics;
using RWBR.Client.Player;
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
        private const float VirtualHeight = 720f;

        private OnlineGame _game;
        private PerformanceMonitor _perf;
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
                if (_game.SelfAlive && !_game.Paused) DrawCrosshair();
                DrawVitals(s, self.Value);
                DrawPrompt();
            }
            DrawFeed();
            DrawNet(net);
            if (_game.Touch.Visible && _game.InputOverride == null) DrawTouch();
            else if (!_game.MenusOpen && Cursor.lockState != CursorLockMode.Locked)
                GUI.Label(new Rect(0, VirtualHeight * 0.62f, _w, 30), "Click to play · Esc menu", _center);
            if (_game.Paused) DrawPause();
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
            if (GUI.Button(new Rect(r.xMax - 280, r.yMax - 70, 250, 44), "Back to practice", _button)) _game.LeaveToPractice();
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
            Fill(new Rect(_w / 2 - 150, 16, 300, 34), new Color(0, 0, 0, 0.45f));
            GUI.Label(new Rect(_w / 2 - 150, 18, 300, 30), text, _center);
        }

        private void DrawZone(SnapshotPacket s, SnapshotEntry self)
        {
            float dist = new Vector2(self.X - s.ZoneX, self.Z - s.ZoneZ).magnitude;
            if (s.Phase != MatchPhase.Active || dist <= s.ZoneRadius || (self.Flags & PlayerFlags.Alive) == 0) return;
            Fill(new Rect(0, 0, _w, VirtualHeight), new Color(0.2f, 0.3f, 1f, 0.12f));
            GUI.Label(new Rect(0, 60, _w, 30), $"Outside the safe zone — move {dist - s.ZoneRadius:0} m towards the centre", _center);
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
            string state = (self.Flags & PlayerFlags.Healing) != 0 ? "  · healing…" : "";
            GUI.Label(new Rect(x, y + 42, 420, 24), $"Armor {self.Armor}   Medkits {s.SelfMedkits}{state}", _small);

            bool touch = _game.Touch.Visible && _game.InputOverride == null;
            float wx = touch ? _w - 330 : _w - 260, wy = touch ? 60 : VirtualHeight - 92;
            Fill(new Rect(wx, wy, 236, 64), new Color(0, 0, 0, 0.45f));
            if ((self.Flags & PlayerFlags.HasWeapon) == 0)
            {
                GUI.Label(new Rect(wx + 12, wy + 18, 220, 24), "UNARMED", _label);
                return;
            }
            GUI.Label(new Rect(wx + 12, wy + 6, 220, 24), (self.Flags & PlayerFlags.Reloading) != 0 ? "RIFLE · reloading" : "RIFLE", _small);
            GUI.Label(new Rect(wx + 12, wy + 24, 220, 34), $"<size=30>{self.Ammo}</size>  / {s.SelfReserveAmmo}", new GUIStyle(_label) { richText = true });
        }

        private void DrawPrompt()
        {
            int i = _game.NearestLoot();
            if (i < 0 || _game.MenusOpen) return;
            string key = _game.Touch.Visible ? "PICK UP" : "[E]";
            var r = new Rect(_w / 2 - 160, VirtualHeight * 0.64f, 320, 34);
            Fill(r, new Color(0, 0, 0, 0.5f));
            GUI.Label(r, $"{key}  {_game.Map.Loot[i].DisplayName}", _center);
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
                if (b.Button == TouchButton.Interact && !t.InteractAvailable) continue;
                if (b.Button == TouchButton.Inventory) continue; // no inventory screen online yet
                var c = new Vector2(b.Center.x * _w, (1 - b.Center.y) * VirtualHeight);
                float r = b.Radius * VirtualHeight;
                Fill(new Rect(c.x - r, c.y - r, r * 2, r * 2), new Color(1, 1, 1, t.IsHeld(b.Button) ? 0.45f : 0.22f));
                GUI.Label(new Rect(c.x - 50, c.y - 12, 100, 24), b.Label, new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter });
            }
        }

        private Vector2 ToVirtual(Vector2 screenBottomLeft) =>
            new Vector2(screenBottomLeft.x / _scale, (Screen.height - screenBottomLeft.y) / _scale);

        private void DrawPause()
        {
            Rect r = Panel(520, 360, "Paused");
            float x = r.x + 30, y = r.y + 70;
            GUI.Label(new Rect(x, y, 300, 26), $"Look sensitivity  {GameSettings.Sensitivity:0.00}", _label);
            float sens = GUI.HorizontalSlider(new Rect(x, y + 32, 460, 24), GameSettings.Sensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity);
            if (!Mathf.Approximately(sens, GameSettings.Sensitivity)) GameSettings.SetSensitivity(sens);
            GUI.Label(new Rect(x, y + 70, 300, 26), "Graphics quality", _label);
            int q = GUI.Toolbar(new Rect(x, y + 100, 460, 36), (int)GameSettings.Quality, new[] { "Low", "Medium", "High" }, _button);
            if (q != (int)GameSettings.Quality) GameSettings.SetQuality((QualityPreset)q);
            bool perf = GUI.Toggle(new Rect(x, y + 150, 460, 26), GameSettings.ShowPerformance, "  Show performance and network overlay");
            if (perf != GameSettings.ShowPerformance) GameSettings.SetShowPerformance(perf);
            if (GUI.Button(new Rect(x, r.yMax - 70, 210, 44), "Resume", _button)) _game.Paused = false;
            if (GUI.Button(new Rect(x + 250, r.yMax - 70, 210, 44), "Leave match", _button)) _game.LeaveToPractice();
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
            if (GUI.Button(new Rect(r.x + 30, r.yMax - 70, 210, 44), "Play again", _button))
            {
                if (_game.Platform != null) _game.StartQuickPlay();
                else _game.Connect(_game.ServerAddress);
            }
            if (GUI.Button(new Rect(r.x + 280, r.yMax - 70, 210, 44), "Practice", _button)) _game.LeaveToPractice();
        }
    }
}
