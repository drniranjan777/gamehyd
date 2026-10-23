using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using Match = RWBR.Shared.Server.Match;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Server;
using RWBR.Shared.Protocol;
using Xunit;

namespace RWBR.MatchServer.Tests;

public class WeaponCatalogTests
{
    [Fact]
    public void Roster_has_six_original_weapons_with_sane_balance()
    {
        Assert.Equal(6, WeaponCatalog.Count);
        Assert.Equal(WeaponCatalog.Count, WeaponCatalog.All.Select(w => w.Id).Distinct().Count());
        Assert.Equal(Enum.GetValues<WeaponClass>().Length, WeaponCatalog.All.Select(w => w.Class).Distinct().Count());
        foreach (WeaponDefinition w in WeaponCatalog.All)
        {
            Assert.Equal(WeaponCatalog.BalanceVersion, w.BalanceVersion);
            Assert.True(w.Damage > 0 && w.Range > 0 && w.MagazineSize > 0 && w.FireIntervalSeconds > 0, w.Id);
            Assert.True(w.DamageAt(0) >= w.DamageAt(w.FalloffStart + 1) && w.DamageAt(w.Range) >= w.Damage * w.MinDamageFactor - 1e-3f, w.Id);
            Assert.True(w.HeadshotMultiplier >= 1, w.Id);
        }
        Assert.Equal(8, WeaponCatalog.Get(WeaponCatalog.Shotgun).Pellets);
        Assert.Equal(WeaponCatalog.Sniper, WeaponCatalog.IndexOf("longshot_sr"));
    }

    /// <summary>The database's latest balance rows (migration 0010, v4) must equal the runtime catalog, value by value.</summary>
    [Fact]
    public void Database_balance_rows_match_the_catalog()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "database", "migrations"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        string sql = File.ReadAllText(Path.Combine(dir!, "database", "migrations", "0010_weapon_balance_v4.sql"));
        var rows = Regex.Matches(sql, @"\((\d+), '([a-z0-9_]+)', '(\{[^']*\})'\)");
        Assert.Equal(WeaponCatalog.Count, rows.Count);
        foreach (System.Text.RegularExpressions.Match row in rows)
        {
            Assert.Equal(WeaponCatalog.BalanceVersion, int.Parse(row.Groups[1].Value));
            WeaponDefinition w = WeaponCatalog.Get(WeaponCatalog.IndexOf(row.Groups[2].Value));
            JsonElement b = JsonDocument.Parse(row.Groups[3].Value).RootElement;
            void Same(string key, double expected) => Assert.True(Math.Abs(b.GetProperty(key).GetDouble() - expected) < 1e-4, $"{w.Id}.{key}");
            Same("damage", w.Damage);
            Same("pellets", w.Pellets);
            Same("headshotMultiplier", w.HeadshotMultiplier);
            Same("range", w.Range);
            Same("falloffStart", w.FalloffStart);
            Same("minDamageFactor", w.MinDamageFactor);
            Same("fireIntervalSeconds", w.FireIntervalSeconds);
            Same("magazineSize", w.MagazineSize);
            Same("reloadSeconds", w.ReloadSeconds);
            Same("switchSeconds", w.SwitchSeconds);
            Same("spreadDegrees", w.SpreadDegrees);
            Same("movingSpreadDegrees", w.MovingSpreadDegrees);
            Same("airborneSpreadDegrees", w.AirborneSpreadDegrees);
            Same("aimSpreadMultiplier", w.AimSpreadMultiplier);
            Same("aimZoom", w.AimZoom);
        }
        string v3 = File.ReadAllText(Path.Combine(dir!, "database", "migrations", "0006_weapons_and_city_map.sql"));
        Assert.Contains("767DC0BCF7944070946E4FDC2A6D7C7CFBABCB7A5CC818EC32295409EF7089D3", v3); // city geometry pin
    }

    [Fact]
    public void Damage_falls_off_linearly_between_falloff_start_and_range()
    {
        WeaponDefinition r = WeaponCatalog.Get(WeaponCatalog.Rifle); // 20 dmg, full to 40 m, 70 % at 120 m
        Assert.Equal(20f, r.DamageAt(40), 3);
        Assert.Equal(17f, r.DamageAt(80), 3);
        Assert.Equal(14f, r.DamageAt(120), 3);
    }
}

public class WeaponSlotTests
{
    private static LootSpawn Weapon(int index) => new(0, LootType.Weapon, Vector3.Zero, index);

    [Fact]
    public void Weapons_fill_empty_slots_then_replace_the_one_in_hand()
    {
        var c = new Combatant();
        c.Reset(WeaponCatalog.Get(0), withWeapon: false, reserveAmmo: 0);
        c.PickUp(Weapon(WeaponCatalog.Smg), WeaponCatalog.Get(WeaponCatalog.Smg));
        Assert.Equal((WeaponCatalog.Smg, -1, 0), (c.SlotWeapon(0), c.SlotWeapon(1), c.ActiveSlot));
        Assert.Equal(32, c.Ammo);

        c.PickUp(Weapon(WeaponCatalog.Sniper), WeaponCatalog.Get(WeaponCatalog.Sniper));
        Assert.Equal((WeaponCatalog.Smg, WeaponCatalog.Sniper, 1), (c.SlotWeapon(0), c.SlotWeapon(1), c.ActiveSlot));
        Assert.False(c.CanPickUp(Weapon(WeaponCatalog.Sniper)), "already carried");

        c.PickUp(Weapon(WeaponCatalog.Shotgun), WeaponCatalog.Get(WeaponCatalog.Shotgun));
        Assert.Equal((WeaponCatalog.Smg, WeaponCatalog.Shotgun), (c.SlotWeapon(0), c.SlotWeapon(1)));
    }

    [Fact]
    public void Switching_cancels_reload_and_delays_the_next_shot_by_the_draw_time()
    {
        var c = new Combatant();
        c.Reset(WeaponCatalog.Get(WeaponCatalog.Rifle), withWeapon: true, reserveAmmo: 90);
        c.PickUp(Weapon(WeaponCatalog.Pistol), WeaponCatalog.Get(WeaponCatalog.Pistol)); // into slot 2, drawn
        Assert.True(c.TrySwitch(0, WeaponCatalog.Get(WeaponCatalog.Rifle), 30));
        Assert.Equal(WeaponCatalog.Rifle, c.ActiveWeaponIndex);
        c.Ammo = 10;
        Assert.True(c.TryStartReload(1, WeaponCatalog.Get(WeaponCatalog.Rifle), 30));
        Assert.True(c.TrySwitch(2, WeaponCatalog.Get(WeaponCatalog.Pistol), 30));
        Assert.False(c.Reloading);
        int draw = Combatant.Ticks(WeaponCatalog.Get(WeaponCatalog.Pistol).SwitchSeconds, 30);
        Assert.Equal(FireResult.Cooldown, c.TryFire(2 + draw - 1, WeaponCatalog.Get(WeaponCatalog.Pistol), 30));
        Assert.Equal(FireResult.Fired, c.TryFire(2 + draw, WeaponCatalog.Get(WeaponCatalog.Pistol), 30));
    }

    [Fact]
    public void Quick_swapping_cannot_skip_a_sniper_bolt()
    {
        var c = new Combatant();
        WeaponDefinition sniper = WeaponCatalog.Get(WeaponCatalog.Sniper), pistol = WeaponCatalog.Get(WeaponCatalog.Pistol);
        c.Reset(sniper, withWeapon: true, reserveAmmo: 10, weaponIndex: WeaponCatalog.Sniper);
        c.PickUp(Weapon(WeaponCatalog.Pistol), pistol);
        c.TrySwitch(0, sniper, 30);
        Assert.Equal(FireResult.Cooldown, c.TryFire(0, sniper, 30)); // drawing
        long ready = Combatant.Ticks(sniper.SwitchSeconds, 30);
        Assert.Equal(FireResult.Fired, c.TryFire(ready, sniper, 30));
        c.TrySwitch(ready + 1, pistol, 30);
        c.TrySwitch(ready + 2, sniper, 30);
        long bolt = Combatant.Ticks(sniper.FireIntervalSeconds, 30);
        Assert.Equal(FireResult.Cooldown, c.TryFire(ready + bolt - 1, sniper, 30));
        Assert.Equal(FireResult.Fired, c.TryFire(ready + bolt, sniper, 30));
    }
}

public class BallisticsTests
{
    private static readonly MapLayout Open = new("open", 300, Array.Empty<Aabb>());
    private static readonly List<PelletResult> Results = new();

    private static List<PelletResult> Shoot(int weapon, float distance, float aimHeight, float spread = 0)
    {
        var eye = new Vector3(0, GameRules.EyeHeight, 0);
        var target = new TargetCapsule { Feet = new Vector3(0, 0, distance), Height = GameRules.PlayerHeight };
        float pitch = MathF.Atan2(aimHeight - GameRules.EyeHeight, distance);
        Ballistics.Fire(eye, 0, pitch, spread, 1234, WeaponCatalog.Get(weapon), Open, new[] { target }, Results);
        return Results;
    }

    [Fact]
    public void Headshots_multiply_damage_and_body_shots_do_not()
    {
        WeaponDefinition dmr = WeaponCatalog.Get(WeaponCatalog.Marksman);
        PelletResult head = Shoot(WeaponCatalog.Marksman, 20, 1.65f).Single();
        Assert.True(head.Headshot);
        Assert.Equal(dmr.Damage * dmr.HeadshotMultiplier, head.Damage, 3);

        PelletResult body = Shoot(WeaponCatalog.Marksman, 20, 1.0f).Single();
        Assert.False(body.Headshot);
        Assert.Equal(dmr.Damage, body.Damage, 3);
    }

    [Fact]
    public void Shotgun_pellets_spread_deterministically_and_lose_power_with_range()
    {
        List<PelletResult> close = Shoot(WeaponCatalog.Shotgun, 4, 1.0f, WeaponCatalog.Get(WeaponCatalog.Shotgun).SpreadDegrees).ToList();
        Assert.Equal(8, close.Count);
        Assert.True(close.Count(p => p.Target == 0) >= 6, "most pellets should hit at 4 m");
        List<PelletResult> again = Shoot(WeaponCatalog.Shotgun, 4, 1.0f, WeaponCatalog.Get(WeaponCatalog.Shotgun).SpreadDegrees).ToList();
        Assert.Equal(close.Select(p => p.End), again.Select(p => p.End));

        float near = close.Where(p => p.Target == 0).Sum(p => p.Damage);
        float far = Shoot(WeaponCatalog.Shotgun, 25, 1.0f, WeaponCatalog.Get(WeaponCatalog.Shotgun).SpreadDegrees).Where(p => p.Target == 0).Sum(p => p.Damage);
        Assert.True(far < near / 3, $"near {near}, far {far}");
    }

    [Fact]
    public void Out_of_range_shots_do_nothing()
    {
        PelletResult r = Shoot(WeaponCatalog.Pistol, 80, 1.0f).Single(); // pistol range 50 m
        Assert.Equal(-1, r.Target);
        Assert.Equal(0, r.Damage);
    }
}

public class ServerWeaponTests
{
    [Fact]
    public void Server_switches_weapons_from_input_and_reports_weapon_and_shots_in_snapshots()
    {
        MatchConfig cfg = MatchConfig.Fast(2);
        cfg.Map = new MapLayout("w", 40, Array.Empty<Aabb>(), new[] { new LootSpawn(0, LootType.Weapon, Vector3.Zero, WeaponCatalog.Smg) });
        var m = new Match(cfg);
        var a = new IPEndPoint(IPAddress.Loopback, 40);
        var b = new IPEndPoint(IPAddress.Loopback, 41);
        uint seq = 0;
        void Send(IPEndPoint ep, InputCommand c)
        {
            c.Sequence = ++seq;
            byte[] d = PacketCodec.Encode(new List<InputCommand> { c });
            m.Receive(ep, d, d.Length);
        }
        foreach (IPEndPoint ep in new[] { a, b })
        {
            byte[] h = PacketCodec.Encode(new HelloPacket());
            m.Receive(ep, h, h.Length);
        }
        while (m.Phase != MatchPhase.Active)
        {
            Send(a, default);
            Send(b, default);
            m.Advance();
        }
        PlayerState pa = m.Players[0];
        pa.Position = new Vector3(0.5f, 0, 0);

        Send(a, new InputCommand { Buttons = InputButtons.Interact });
        m.Advance();
        Assert.Equal(WeaponCatalog.Smg, pa.Combat.ActiveWeaponIndex); // second slot, drawn
        Send(a, new InputCommand { Buttons = InputButtons.SwitchWeapon });
        m.Advance();
        Assert.Equal(cfg.StartingWeaponIndex, pa.Combat.ActiveWeaponIndex);

        for (int i = 0; i < 40; i++)
        {
            Send(a, new InputCommand { Buttons = InputButtons.Fire, Yaw = 2f });
            m.Advance();
        }
        m.Outbox.Clear();
        Send(a, default);
        m.Advance();
        byte[] snap = m.Outbox.Last(o => o.To.Equals(a) && o.Data[0] == (byte)PacketType.Snapshot).Data;
        SnapshotPacket s = SnapshotCodec.Decode(new PacketReader(snap, snap.Length), null);
        SnapshotEntry me = s.Players.Single(e => e.PlayerId == pa.Id);
        Assert.Equal((byte)cfg.StartingWeaponIndex, me.Weapon);
        Assert.Equal(pa.ShotCounter, me.ShotCounter);
        Assert.True(me.ShotCounter > 0);
        Assert.Equal(new byte[] { (byte)cfg.StartingWeaponIndex, WeaponCatalog.Smg }, s.SelfSlotWeapon);
        Assert.Equal(0, s.SelfActiveSlot);
    }
}
