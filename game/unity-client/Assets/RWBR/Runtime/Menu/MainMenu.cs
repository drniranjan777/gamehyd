using System;
using System.Linq;
using RWBR.Client.Online;
using RWBR.Client.Practice;
using RWBR.Client.Settings;
using RWBR.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RWBR.Client.Menu
{
    /// <summary>
    /// Front end (greybox IMGUI, ADR 0006): play modes, profile and match history, loadout (cosmetics), store,
    /// account (deletion) and settings.
    /// The city orbits slowly behind it. Platform screens sign in with the stored guest account.
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        public const string PracticeScene = "Greybox";
        public const string OnlineScene = "Online";
        private const float BaseHeight = 720f;
        private static float VirtualHeight => BaseHeight / GameSettings.UiScale;
        private static readonly string[] Slots = { "WEAPON_SKIN", "OUTFIT", "EMOTE", "PARACHUTE", "BANNER" };

        private enum Screen
        {
            Home,
            Profile,
            Loadout,
            Store,
            Season,
            Account,
            Settings,
            Accessibility,
        }

        private Screen _screen;
        private GUIStyle _title, _label, _small, _button, _field;
        private float _scale, _w;
        private PlatformApi _api;
        private string _status;
        private ProfileDto _profile;
        private StatsDto _stats;
        private MatchHistoryPageDto _history;
        private InventoryDto _inventory;
        private CatalogDto _catalog;
        private SeasonPassDto _season;
        private DeletionStatusDto _deletion;
        private EventListDto _news;
        private bool _confirmDelete;
        private string _platformUrl;
        private string _name;
        private Vector2 _scroll;

        public string Status => _status;
        public bool Busy { get; private set; }

        private void Awake()
        {
            GameSettings.Load();
            _platformUrl = Load("rwbr.platform", OnlineGame.DefaultPlatform);
            _name = Load("rwbr.name", "Player");
            PracticeGame.RequestedMap = null;
            PracticeGame.TutorialRequested = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            LoadNews();
        }

        /// <summary>News and events for the home screen; quietly absent when the platform is unreachable.</summary>
        private async void LoadNews()
        {
            try { _news = await new PlatformApi(_platformUrl).EventsAsync(); }
            catch (ApiException) { _news = null; }
        }

        private void Update()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            float a = Time.time * 0.05f;
            cam.transform.position = new Vector3(Mathf.Sin(a) * 70f, 38f, Mathf.Cos(a) * 70f);
            cam.transform.LookAt(new Vector3(0, 10, 0));
        }

        // ---- Navigation ----

        public static void PlayTrainingGround()
        {
            PracticeGame.RequestedMap = "greybox-block-100-v2";
            PracticeGame.TutorialRequested = !TutorialDirector.Done;
            SceneManager.LoadScene(PracticeScene);
        }

        /// <summary>Firing range: every weapon, targets from 10 to 200 m, accuracy and damage readouts.</summary>
        public static void PlayTrainingRange()
        {
            PracticeGame.RequestedMap = MapLayout.TrainingRangeVersion;
            PracticeGame.TutorialRequested = false;
            SceneManager.LoadScene(PracticeScene);
        }

        public static void PlayCityPractice()
        {
            PracticeGame.RequestedMap = CityMap.Version;
            PracticeGame.TutorialRequested = false;
            SceneManager.LoadScene(PracticeScene);
        }

        public static void PlayOnline() => SceneManager.LoadScene(OnlineScene);

        private async void OpenPlatformScreen(Screen screen)
        {
            _screen = screen;
            _status = "Signing in…";
            Busy = true;
            try
            {
                Save("rwbr.platform", _platformUrl);
                if (_api == null || !_api.SignedIn)
                {
                    _api = new PlatformApi(_platformUrl);
                    await _api.SignInAsync(_name);
                }
                _profile = await _api.ProfileAsync();
                switch (screen)
                {
                    case Screen.Profile:
                        _stats = await _api.StatsAsync();
                        _history = await _api.MatchesAsync(limit: 10);
                        break;
                    case Screen.Store:
                        _catalog = await _api.CatalogAsync();
                        _inventory = await _api.InventoryAsync();
                        break;
                    case Screen.Season:
                        _season = await _api.SeasonAsync();
                        _catalog = await _api.CatalogAsync();
                        break;
                    case Screen.Account:
                        _deletion = await _api.DeletionStatusAsync();
                        _confirmDelete = false;
                        break;
                    default:
                        _inventory = await _api.InventoryAsync();
                        break;
                }
                _status = null;
            }
            catch (ApiException e)
            {
                _status = e.Code == "NETWORK" ? $"Platform not reachable ({_platformUrl})" : $"{e.Code}: {e.Message}";
            }
            finally
            {
                Busy = false;
            }
        }

        private async void Equip(string slot, string key)
        {
            Busy = true;
            try
            {
                _inventory = await _api.EquipAsync(slot, key);
                _status = null;
            }
            catch (ApiException e)
            {
                _status = $"{e.Code}: {e.Message}";
            }
            finally
            {
                Busy = false;
            }
        }

        private async void Buy(CatalogItemDto item)
        {
            Busy = true;
            _status = $"Buying {item.displayName}…";
            try
            {
                OrderDto order = await new StoreFlow(_api, DevStorePurchaser.FromEnvironment()).BuyAsync(item);
                _status = order.status == "FULFILLED" ? $"{item.displayName} is yours." : $"Order {order.status.ToLowerInvariant()}.";
                _catalog = await _api.CatalogAsync();
                _inventory = await _api.InventoryAsync();
            }
            catch (ApiException e)
            {
                _status = e.Code switch
                {
                    "INSUFFICIENT_FUNDS" => "Not enough coins yet: play matches to earn more.",
                    "ALREADY_OWNED" => "You already own this.",
                    "STORE_DISABLED" => "The store is closed for a moment. Try again soon.",
                    _ => $"{e.Code}: {e.Message}",
                };
            }
            finally
            {
                Busy = false;
            }
        }

        private async void Claim(SeasonRewardStateDto r)
        {
            Busy = true;
            try
            {
                _season = await _api.ClaimSeasonRewardAsync(r.tier, r.track);
                _status = $"Claimed {RewardText(r.reward)}.";
            }
            catch (ApiException e)
            {
                _status = $"{e.Code}: {e.Message}";
            }
            finally
            {
                Busy = false;
            }
        }

        private static string RewardText(SeasonRewardDto r) =>
            r.type == "COSMETIC" ? r.cosmeticKey.Replace('_', ' ') : $"{r.amount} {(r.currency == "COIN" ? "coins" : "gems")}";

        private async void ChangeDeletion(bool delete)
        {
            Busy = true;
            try
            {
                _deletion = delete ? await _api.RequestDeletionAsync() : await _api.CancelDeletionAsync();
                _confirmDelete = false;
                _status = null;
            }
            catch (ApiException e)
            {
                _status = $"{e.Code}: {e.Message}";
            }
            finally
            {
                Busy = false;
            }
        }

        // ---- Drawing ----

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 20, normal = { textColor = Color.white }, wordWrap = true };
            _small = new GUIStyle(_label) { fontSize = 15 };
            _title = new GUIStyle(_label) { fontSize = 46, fontStyle = FontStyle.Bold };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 22 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 20 };
        }

        private void OnGUI()
        {
            EnsureStyles();
            _scale = UnityEngine.Screen.height / VirtualHeight;
            _w = UnityEngine.Screen.width / _scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1));

            Fill(new Rect(0, 0, 520, VirtualHeight), new Color(0.05f, 0.06f, 0.08f, 0.85f));
            GUI.Label(new Rect(40, 36, 460, 60), "REAL-WORLD", _title);
            GUI.Label(new Rect(40, 86, 460, 60), "BATTLE ROYALE", _title);

            switch (_screen)
            {
                case Screen.Home: DrawHome(); break;
                case Screen.Profile: DrawProfile(); break;
                case Screen.Loadout: DrawLoadout(); break;
                case Screen.Store: DrawStore(); break;
                case Screen.Season: DrawSeason(); break;
                case Screen.Account: DrawAccount(); break;
                case Screen.Settings: DrawSettings(); break;
                case Screen.Accessibility: DrawAccessibility(); break;
            }
            if (_status != null) GUI.Label(new Rect(40, VirtualHeight - 60, 460, 50), _status, _small);
        }

        private bool Button(float y, string text) => GUI.Button(new Rect(40, y, 440, 52), text, _button);

        private void DrawHome()
        {
            float y = 170;
            if (GUI.Button(new Rect(40, y, 250, 52), TutorialDirector.Done ? "Training ground" : "Training ground (tutorial)", _button)) PlayTrainingGround();
            if (GUI.Button(new Rect(296, y, 184, 52), "Firing range", _button)) PlayTrainingRange();
            if (Button(y += 56, "City practice: Hyderabad-inspired map")) PlayCityPractice();
            if (Button(y += 56, "Play online")) PlayOnline();
            if (Button(y += 66, "Profile & match history")) OpenPlatformScreen(Screen.Profile);
            if (Button(y += 56, "Loadout")) OpenPlatformScreen(Screen.Loadout);
            if (Button(y += 56, "Store")) OpenPlatformScreen(Screen.Store);
            if (Button(y += 56, "Season pass")) OpenPlatformScreen(Screen.Season);
            if (Button(y += 56, "Settings")) _screen = Screen.Settings;
            DrawNews();
        }

        private void DrawNews()
        {
            EventDto[] items = _news?.items;
            if (items == null || items.Length == 0) return;
            Rect r = new Rect(Mathf.Max(560, _w - 460), 40, 420, 60 + Mathf.Min(items.Length, 3) * 92);
            Fill(r, new Color(0.05f, 0.06f, 0.08f, 0.85f));
            GUI.Label(new Rect(r.x + 18, r.y + 12, r.width - 36, 30), "News & events", _label);
            float y = r.y + 50;
            for (int i = 0; i < items.Length && i < 3; i++)
            {
                EventDto e = items[i];
                GUI.Label(new Rect(r.x + 18, y, r.width - 36, 26), (e.live ? "● LIVE  " : "") + e.title, _label);
                string body = string.IsNullOrEmpty(e.body) ? "" : e.body.Length > 110 ? e.body.Substring(0, 107) + "…" : e.body;
                GUI.Label(new Rect(r.x + 18, y + 28, r.width - 36, 56), body, _small);
                y += 92;
            }
        }

        private void Back()
        {
            if (GUI.Button(new Rect(40, VirtualHeight - 120, 200, 46), "Back", _button)) _screen = Screen.Home;
        }

        private void DrawProfile()
        {
            Panel(out Rect r);
            if (_profile != null)
            {
                GUI.Label(new Rect(r.x + 24, r.y + 20, r.width - 48, 34), $"{_profile.displayName}   ·   Level {_profile.level}", _label);
                Bar(new Rect(r.x + 24, r.y + 60, 360, 14), 1f - _profile.xpForNextLevel / 1000f, new Color(0.4f, 0.8f, 1f));
                GUI.Label(new Rect(r.x + 396, r.y + 54, 300, 26), $"{_profile.xp} XP ({_profile.xpForNextLevel} to next level)", _small);
            }
            if (_stats != null)
                GUI.Label(new Rect(r.x + 24, r.y + 90, r.width - 48, 26),
                    $"Matches {_stats.matchesPlayed}   Wins {_stats.wins}   Eliminations {_stats.kills}   Win rate {_stats.winRate:P0}", _small);
            if (_history?.items != null)
            {
                float y = r.y + 130;
                GUI.Label(new Rect(r.x + 24, y, 400, 26), "Recent matches", _label);
                y += 34;
                if (_history.items.Length == 0) GUI.Label(new Rect(r.x + 24, y, 500, 26), "No matches yet: play online!", _small);
                foreach (MatchHistoryItemDto m in _history.items)
                {
                    DateTime when = DateTime.TryParse(m.completedAt, out DateTime d) ? d.ToLocalTime() : DateTime.MinValue;
                    GUI.Label(new Rect(r.x + 24, y, r.width - 48, 24),
                        $"#{m.placement}/{m.players}   {m.kills} elims   {m.survivalSeconds / 60}:{m.survivalSeconds % 60:00} survived   +{m.xpAwarded} XP  +{m.coinsAwarded} coins   {when:dd MMM HH:mm}", _small);
                    y += 26;
                }
            }
            Back();
        }

        private void DrawLoadout()
        {
            Panel(out Rect r);
            if (_inventory != null)
            {
                GUI.Label(new Rect(r.x + 24, r.y + 20, r.width - 48, 30), $"Coins {_inventory.wallets.COIN}    Gems {_inventory.wallets.GEM}", _label);
                float y = r.y + 64;
                foreach (string slot in Slots)
                {
                    string equipped = slot switch
                    {
                        "WEAPON_SKIN" => _inventory.loadout.WEAPON_SKIN,
                        "OUTFIT" => _inventory.loadout.OUTFIT,
                        "EMOTE" => _inventory.loadout.EMOTE,
                        "PARACHUTE" => _inventory.loadout.PARACHUTE,
                        _ => _inventory.loadout.BANNER,
                    };
                    GUI.Label(new Rect(r.x + 24, y, 200, 26), slot.Replace('_', ' '), _small);
                    float x = r.x + 200;
                    InventoryItemDto[] owned = _inventory.items.Where(i => i.kind == slot).ToArray();
                    if (owned.Length == 0) GUI.Label(new Rect(x, y, 300, 26), "none owned yet", _small);
                    foreach (InventoryItemDto item in owned)
                    {
                        bool on = item.cosmeticKey == equipped;
                        GUI.enabled = !on && !Busy;
                        if (GUI.Button(new Rect(x, y - 2, 220, 30), on ? $"✓ {item.displayName}" : item.displayName)) Equip(slot, item.cosmeticKey);
                        GUI.enabled = true;
                        x += 230;
                    }
                    y += 40;
                }
                GUI.Label(new Rect(r.x + 24, y + 10, r.width - 48, 50), "Cosmetics never change weapon stats. Get more in the Store.", _small);
            }
            Back();
        }

        private void DrawSeason()
        {
            Panel(out Rect r);
            if (_season != null)
            {
                if (!_season.Active)
                {
                    GUI.Label(new Rect(r.x + 24, r.y + 20, r.width - 48, 60), "No season is running right now. The next one starts soon.", _label);
                }
                else
                {
                    SeasonInfoDto s = _season.season;
                    SeasonProgressDto p = _season.progress;
                    DateTime ends = DateTime.TryParse(s.endsAt, out DateTime d) ? d.ToLocalTime() : DateTime.MinValue;
                    GUI.Label(new Rect(r.x + 24, r.y + 16, r.width - 48, 30), $"{s.title}   ·   tier {p.tier} of {s.maxTier}   ·   ends {ends:dd MMM}", _label);
                    Bar(new Rect(r.x + 24, r.y + 54, 360, 14), 1f - p.xpToNextTier / (float)s.xpPerTier, new Color(1f, 0.75f, 0.2f));
                    GUI.Label(new Rect(r.x + 396, r.y + 48, 360, 26), $"{p.xpToNextTier} XP to the next tier (earn XP in matches)", _small);

                    CatalogItemDto passItem = Array.Find(_catalog?.items ?? Array.Empty<CatalogItemDto>(), i => i.productKey == s.premiumProductKey);
                    if (!p.premium && passItem != null)
                    {
                        bool canPay = StoreFlow.IsInGameCurrency(passItem) || DevStorePurchaser.FromEnvironment() != null;
                        GUI.enabled = !Busy && canPay;
                        if (GUI.Button(new Rect(r.xMax - 300, r.y + 12, 276, 40), canPay ? $"Season pass {StoreFlow.Price(passItem.price)}" : "Season pass (soon)", _button))
                            BuySeasonPass(passItem);
                        GUI.enabled = true;
                    }
                    else if (p.premium)
                    {
                        GUI.Label(new Rect(r.xMax - 300, r.y + 18, 276, 30), "★ Season pass active", _label);
                    }

                    SeasonRewardStateDto[] rewards = _season.rewards ?? Array.Empty<SeasonRewardStateDto>();
                    _scroll = GUI.BeginScrollView(new Rect(r.x + 16, r.y + 84, r.width - 32, r.height - 170), _scroll, new Rect(0, 0, r.width - 56, rewards.Length * 40));
                    float y = 0;
                    foreach (SeasonRewardStateDto reward in rewards)
                    {
                        string track = reward.track == "PREMIUM" ? "★ " : "";
                        GUI.Label(new Rect(8, y + 6, r.width - 320, 28), $"Tier {reward.tier}   {track}{RewardText(reward.reward)}", _small);
                        string label = reward.claimed ? "Claimed" : reward.unlocked ? "Claim" : reward.track == "PREMIUM" && !p.premium ? "Pass needed" : "Locked";
                        GUI.enabled = !Busy && reward.unlocked && !reward.claimed;
                        if (GUI.Button(new Rect(r.width - 260, y + 2, 200, 32), label)) Claim(reward);
                        GUI.enabled = true;
                        y += 40;
                    }
                    GUI.EndScrollView();
                }
            }
            Back();
        }

        private async void BuySeasonPass(CatalogItemDto item)
        {
            Busy = true;
            try
            {
                await new StoreFlow(_api, DevStorePurchaser.FromEnvironment()).BuyAsync(item);
                _season = await _api.SeasonAsync();
                _status = "Season pass unlocked: premium rewards are ready to claim.";
            }
            catch (ApiException e)
            {
                _status = $"{e.Code}: {e.Message}";
            }
            finally
            {
                Busy = false;
            }
        }

        private void DrawAccessibility()
        {
            float y = 170;
            GUI.Label(new Rect(40, y, 440, 30), $"Interface size {GameSettings.UiScale:0.0}×", _label);
            float scale = GUI.HorizontalSlider(new Rect(40, y += 36, 440, 20), GameSettings.UiScale, GameSettings.MinUiScale, GameSettings.MaxUiScale);
            scale = Mathf.Round(scale * 10f) / 10f;
            bool cues = GUI.Toggle(new Rect(40, y += 40, 440, 30), GameSettings.VisualSoundCues, " Show gunshot directions on screen", _label);
            GUI.Label(new Rect(64, y += 30, 420, 50), "Arrows around the crosshair point to shots you would hear.", _small);
            bool calm = GUI.Toggle(new Rect(40, y += 56, 440, 30), GameSettings.ReduceFlashing, " Reduce flashing", _label);
            GUI.Label(new Rect(64, y += 30, 420, 50), "Turns off the red screen flash when you take damage.", _small);
            if (!Mathf.Approximately(scale, GameSettings.UiScale) || cues != GameSettings.VisualSoundCues || calm != GameSettings.ReduceFlashing)
                GameSettings.SetAccessibility(scale, cues, calm);
            if (GUI.Button(new Rect(40, VirtualHeight - 120, 200, 46), "Back", _button)) _screen = Screen.Settings;
        }

        private void DrawStore()
        {
            Panel(out Rect r);
            if (_inventory != null)
                GUI.Label(new Rect(r.x + 24, r.y + 20, r.width - 48, 30), $"Coins {_inventory.wallets.COIN}    Gems {_inventory.wallets.GEM}", _label);
            if (_catalog != null)
            {
                if (!_catalog.storeEnabled) GUI.Label(new Rect(r.x + 24, r.y + 64, r.width - 48, 30), "The store is closed for a moment.", _label);
                bool money = DevStorePurchaser.FromEnvironment() != null;
                CatalogItemDto[] items = _catalog.items ?? Array.Empty<CatalogItemDto>();
                _scroll = GUI.BeginScrollView(new Rect(r.x + 16, r.y + 64, r.width - 32, r.height - 150), _scroll,
                    new Rect(0, 0, r.width - 56, items.Length * 64));
                float y = 0;
                foreach (CatalogItemDto item in items)
                {
                    GUI.Label(new Rect(8, y, r.width - 300, 28), item.displayName, _label);
                    GUI.Label(new Rect(8, y + 28, r.width - 300, 24), item.description, _small);
                    bool canPay = StoreFlow.IsInGameCurrency(item) || money;
                    GUI.enabled = !Busy && !item.owned && canPay;
                    string label = item.owned ? "Owned" : canPay ? StoreFlow.Price(item.price) : StoreFlow.Price(item.price) + " (soon)";
                    if (GUI.Button(new Rect(r.width - 280, y + 6, 220, 40), label, _button)) Buy(item);
                    GUI.enabled = true;
                    y += 64;
                }
                GUI.EndScrollView();
                GUI.Label(new Rect(r.x + 24, r.yMax - 76, r.width - 48, 36),
                    "Cosmetic only: nothing here changes how a weapon performs. Coins are earned by playing.", _small);
            }
            Back();
        }

        private void DrawAccount()
        {
            Panel(out Rect r);
            float y = r.y + 20;
            if (_profile != null)
            {
                GUI.Label(new Rect(r.x + 24, y, r.width - 48, 30), _profile.displayName, _label);
                GUI.Label(new Rect(r.x + 24, y += 34, r.width - 48, 26), "Player id (for support and your web profile):", _small);
                GUI.TextField(new Rect(r.x + 24, y += 26, 460, 32), _profile.playerId, _field);
            }
            if (_deletion != null)
            {
                y += 60;
                GUI.enabled = !Busy;
                if (_deletion.status == "PENDING_DELETION")
                {
                    DateTime when = DateTime.TryParse(_deletion.scheduledFor, out DateTime d) ? d.ToLocalTime() : DateTime.MinValue;
                    GUI.Label(new Rect(r.x + 24, y, r.width - 48, 50), $"Your account will be deleted on {when:dd MMM yyyy}. Until then you can change your mind.", _label);
                    if (GUI.Button(new Rect(r.x + 24, y + 60, 300, 46), "Keep my account", _button)) ChangeDeletion(false);
                }
                else
                {
                    GUI.Label(new Rect(r.x + 24, y, r.width - 48, 76),
                        "Deleting your account erases your name, progress and items after 30 days. Purchase records are kept as the law requires. Purchases are not refunded.", _small);
                    if (!_confirmDelete)
                    {
                        if (GUI.Button(new Rect(r.x + 24, y + 84, 300, 46), "Delete account", _button)) _confirmDelete = true;
                    }
                    else if (GUI.Button(new Rect(r.x + 24, y + 84, 420, 46), "Yes, delete my account in 30 days", _button))
                    {
                        ChangeDeletion(true);
                    }
                }
                GUI.enabled = true;
            }
            if (GUI.Button(new Rect(40, VirtualHeight - 120, 200, 46), "Back", _button)) _screen = Screen.Settings;
        }

        private void DrawSettings()
        {
            float y = 170;
            GUI.Label(new Rect(40, y, 200, 30), "Name", _label);
            _name = GUI.TextField(new Rect(160, y, 320, 34), _name, 24, _field);
            GUI.Label(new Rect(40, y += 50, 200, 30), "Platform", _label);
            _platformUrl = GUI.TextField(new Rect(160, y, 320, 34), _platformUrl, 120, _field);
            GUI.Label(new Rect(40, y += 56, 440, 30), $"Look sensitivity {GameSettings.Sensitivity:0.00}", _small);
            float s = GUI.HorizontalSlider(new Rect(40, y += 28, 440, 20), GameSettings.Sensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity);
            if (!Mathf.Approximately(s, GameSettings.Sensitivity)) GameSettings.SetSensitivity(s);
            GUI.Label(new Rect(40, y += 26, 440, 30), $"Aim sensitivity {GameSettings.AimSensitivity:0.00}", _small);
            float a = GUI.HorizontalSlider(new Rect(40, y += 28, 440, 20), GameSettings.AimSensitivity, GameSettings.MinAimSensitivity, GameSettings.MaxAimSensitivity);
            if (!Mathf.Approximately(a, GameSettings.AimSensitivity)) GameSettings.SetAimSensitivity(a);
            int q = GUI.Toolbar(new Rect(40, y += 34, 440, 36), (int)GameSettings.Quality, new[] { "Low", "Medium", "High" }, _button);
            if (q != (int)GameSettings.Quality) GameSettings.SetQuality((QualityPreset)q);
            GUI.Label(new Rect(40, y += 50, 440, 26), "Volume: master / effects / interface", _small);
            float m = GUI.HorizontalSlider(new Rect(40, y += 28, 140, 20), GameSettings.MasterVolume, 0, 1);
            float fx = GUI.HorizontalSlider(new Rect(190, y, 140, 20), GameSettings.SfxVolume, 0, 1);
            float ui = GUI.HorizontalSlider(new Rect(340, y, 140, 20), GameSettings.UiVolume, 0, 1);
            if (!Mathf.Approximately(m, GameSettings.MasterVolume) || !Mathf.Approximately(fx, GameSettings.SfxVolume) || !Mathf.Approximately(ui, GameSettings.UiVolume))
                GameSettings.SetVolumes(m, fx, ui);
            if (GUI.Button(new Rect(40, y + 40, 440, 46), "Accessibility", _button)) _screen = Screen.Accessibility;
            if (GUI.Button(new Rect(260, VirtualHeight - 120, 220, 46), "Account & data", _button))
            {
                Save("rwbr.name", _name);
                Save("rwbr.platform", _platformUrl);
                _api = null; // platform or name may have changed
                OpenPlatformScreen(Screen.Account);
            }
            if (GUI.Button(new Rect(40, VirtualHeight - 120, 200, 46), "Back", _button))
            {
                Save("rwbr.name", _name);
                Save("rwbr.platform", _platformUrl);
                _api = null; // platform or name may have changed
                _screen = Screen.Home;
            }
        }

        private void Panel(out Rect r)
        {
            r = new Rect(540, 40, Mathf.Max(520, _w - 580), VirtualHeight - 80);
            Fill(r, new Color(0.05f, 0.06f, 0.08f, 0.85f));
            if (Busy) GUI.Label(new Rect(r.x + 24, r.yMax - 40, 300, 26), "Loading…", _small);
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void Bar(Rect r, float fraction, Color fg)
        {
            Fill(r, new Color(0, 0, 0, 0.6f));
            Fill(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(fraction), r.height - 4), fg);
        }

        private static string Load(string key, string fallback)
        {
            try { return PlayerPrefs.GetString(key, fallback); }
            catch (Exception) { return fallback; }
        }

        private static void Save(string key, string value)
        {
            try
            {
                PlayerPrefs.SetString(key, value);
                PlayerPrefs.Save();
            }
            catch (Exception)
            {
                // Storage unavailable: the setting lasts for this session only.
            }
        }
    }
}
