using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using RWBR.Shared.Server;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase C part 2: items, inventory rules and their server side (ground items, drops, grenades).</summary>
public class InventoryRulesTests
{
    private static Combatant Armed(int weapon = WeaponCatalog.Rifle)
    {
        var c = new Combatant();
        c.Reset(WeaponCatalog.Get(weapon), withWeapon: true, reserveAmmo: 0, weaponIndex: weapon);
        return c;
    }

    [Fact]
    public void Backpack_weight_limits_how_much_ammo_fits_and_a_bigger_backpack_holds_more()
    {
        Combatant c = Armed();
        Assert.Equal(Items.Capacity[0], c.Capacity);
        int max = (int)(Items.Capacity[0] / Items.Weight(ItemKind.AmmoMedium));
        Assert.Equal(max, c.Take(new ItemStack(ItemKind.AmmoMedium, 1000)));
        Assert.Equal(0, c.CanTake(ItemKind.AmmoMedium, 10));
        Assert.Equal(0, c.CanTake(ItemKind.Bandage, 1));
        Assert.Equal(max, c.ReserveAmmo); // the rifle fires medium rounds

        Assert.Equal(1, c.Take(new ItemStack(ItemKind.Backpack2, 1)));
        Assert.Equal(Items.Capacity[2], c.Capacity);
        Assert.True(c.CanTake(ItemKind.Bandage, 5) > 0);
        Assert.True(c.Take(new ItemStack(ItemKind.Bandage, 5)) == 5);
        Assert.False(c.Drop(ItemKind.Backpack2, 0), "can't drop the backpack while the rest wouldn't fit");
        c.Drop(ItemKind.Bandage, 0);
        Assert.True(c.Drop(ItemKind.Backpack2, 0), "exactly full without it is fine");
    }

    [Fact]
    public void Each_weapon_reloads_from_its_own_calibre()
    {
        Combatant c = Armed(WeaponCatalog.Rifle);
        c.Take(new ItemStack(ItemKind.AmmoLight, 30));
        c.Ammo = 0;
        Assert.Equal(0, c.ReserveAmmo);
        Assert.False(c.TryStartReload(0, c.ActiveWeapon!, 30), "light rounds don't fit a rifle");
        c.Take(new ItemStack(ItemKind.Weapon, WeaponCatalog.Smg, -1));
        Assert.Equal(WeaponCatalog.Smg, c.ActiveWeaponIndex);
        Assert.Equal(30 + WeaponCatalog.Get(WeaponCatalog.Smg).MagazineSize, c.ReserveAmmo); // plus the new weapon.s spare magazine
    }

    [Fact]
    public void Attachments_fit_compatible_weapons_and_change_their_balance()
    {
        Combatant c = Armed(WeaponCatalog.Rifle);
        WeaponDefinition plain = c.ActiveWeapon!;
        c.Take(new ItemStack(ItemKind.ExtendedMag, 1));
        c.Take(new ItemStack(ItemKind.Scope4x, 1));
        c.Take(new ItemStack(ItemKind.Grip, 1));
        WeaponDefinition fitted = c.ActiveWeapon!;
        Assert.Equal(Attachments.ExtendedMag | Attachments.Scope4x | Attachments.Grip, c.SlotAttachments(0));
        Assert.Equal(45, fitted.MagazineSize);
        Assert.Equal(4f, fitted.AimZoom);
        Assert.True(fitted.MovingSpreadDegrees < plain.MovingSpreadDegrees);
        Assert.Equal(plain.Damage, fitted.Damage);
        Assert.Equal(0f, c.Weight);

        // A second scope has nowhere to go and is stored; a shotgun takes no scope, so it stays stored.
        c.Take(new ItemStack(ItemKind.Scope4x, 1));
        Assert.Equal(1, c.Count(ItemKind.Scope4x));
        c.Take(new ItemStack(ItemKind.Weapon, WeaponCatalog.Shotgun, -1));
        Assert.Equal(Attachments.None, c.SlotAttachments(1));
        Assert.Equal(1, c.Count(ItemKind.Scope4x));

        // Taking the magazine off returns the rounds that no longer fit.
        c.TrySwitch(0, c.WeaponIn(0)!, 30);
        c.Ammo = 45;
        Assert.True(c.Drop(ItemKind.ExtendedMag, 1));
        Assert.Equal(30, c.Ammo);
        Assert.Equal(15, c.ReserveAmmo);
        Assert.Contains(c.Dropped, d => d.Kind == ItemKind.ExtendedMag);
    }

    [Fact]
    public void Helmets_absorb_head_damage_and_vests_body_damage()
    {
        var c = new Combatant();
        c.Take(new ItemStack(ItemKind.Helmet2, Items.ArmorPoints[2]));
        Assert.Equal(25f, c.ApplyDamage(50, headshot: true));
        Assert.Equal(Items.ArmorPoints[2] - 25f, c.HelmetPoints);
        Assert.Equal(40f, c.ApplyDamage(40), 3); // no vest: body damage goes straight through
        c.ApplyDamage(200, headshot: true);
        Assert.Equal(0, c.HelmetLevel);

        var d = new Combatant();
        d.Take(new ItemStack(ItemKind.Vest1, Items.ArmorPoints[1]));
        Assert.Equal(1, d.CanTake(ItemKind.Vest2, Items.ArmorPoints[2]));
        d.Take(new ItemStack(ItemKind.Vest2, Items.ArmorPoints[2]));
        Assert.Equal(2, d.VestLevel);
        Assert.Contains(d.Dropped, s => s.Kind == ItemKind.Vest1); // the old vest goes on the ground
        Assert.Equal(0, d.CanTake(ItemKind.Vest1, Items.ArmorPoints[1]));
    }

    [Fact]
    public void Heals_have_their_own_rules()
    {
        var c = new Combatant { Health = 60 };
        c.SetCount(ItemKind.Bandage, 5);
        c.SetCount(ItemKind.Medkit, 1);
        c.SetCount(ItemKind.EnergyDrink, 1);
        Assert.Equal(ItemKind.Bandage, c.BestHeal());
        Assert.True(c.TryStartHeal(0, 30, ItemKind.Bandage));
        c.Update(Combatant.Ticks(Items.BandageSeconds, 30), Armed().ActiveWeapon!);
        Assert.Equal(75f, c.Health); // bandages stop at 75
        Assert.False(c.TryStartHeal(200, 30, ItemKind.Bandage));
        Assert.Equal(ItemKind.EnergyDrink, c.BestHeal());

        Assert.True(c.TryStartHeal(300, 30, ItemKind.EnergyDrink));
        long done = 300 + Combatant.Ticks(Items.DrinkSeconds, 30);
        c.Update(done, Armed().ActiveWeapon!);
        Assert.Equal(75f, c.Health, 2);
        for (long t = done + 1; t <= done + Combatant.Ticks(Items.BoostSeconds, 30) + 5; t++) c.Update(t, Armed().ActiveWeapon!);
        Assert.Equal(75f + Items.DrinkHeal, c.Health, 1); // healed a little at a time
    }

    [Fact]
    public void Dropping_and_being_eliminated_put_items_on_the_ground_as_piles()
    {
        Combatant c = Armed(WeaponCatalog.Rifle);
        c.Take(new ItemStack(ItemKind.AmmoMedium, 40));
        c.Take(new ItemStack(ItemKind.Bandage, 3));
        Assert.True(c.Drop(ItemKind.AmmoMedium, 10));
        Assert.Equal(30, c.Count(ItemKind.AmmoMedium));
        c.Dropped.Clear();
        c.Take(new ItemStack(ItemKind.Helmet1, Items.ArmorPoints[1]));
        c.DropAll();
        Assert.Equal(0, c.Count(ItemKind.AmmoMedium));
        Assert.False(c.HasWeapon);
        Assert.Contains(c.Dropped, s => s.Kind == ItemKind.Weapon && s.Count == WeaponCatalog.Rifle && s.Ammo == 30);
        Assert.Contains(c.Dropped, s => s.Kind == ItemKind.AmmoMedium && s.Count == 30);
        Assert.Contains(c.Dropped, s => s.Kind == ItemKind.Bandage && s.Count == 3);
        Assert.Contains(c.Dropped, s => s.Kind == ItemKind.Helmet1);
    }

    [Fact]
    public void City_loot_table_holds_every_item_and_is_the_same_every_time()
    {
        MapLayout city = CityMap.Build();
        var kinds = city.Loot.Select(l => l.Kind).ToHashSet();
        foreach (ItemKind k in Enum.GetValues<ItemKind>().Where(k => k != ItemKind.None))
            Assert.Contains(k, kinds);
        Assert.Equal(city.Loot.Select(l => (l.Kind, l.Count)), city.Loot.Select(l => LootTable.Resolve(city.Version, l)).Select(l => (l.Kind, l.Count)));
        Assert.All(MapLayout.GreyboxBlock(100f).Loot, l => Assert.Contains(l.Kind, new[] { ItemKind.Weapon, ItemKind.AmmoMedium, ItemKind.Medkit, ItemKind.Vest1, ItemKind.Vest3 }));
    }
}

public class MatchItemTests
{
    private static readonly WeaponDefinition Rifle = WeaponCatalog.Get(WeaponCatalog.Rifle);

    private static Match Started(out List<IPEndPoint> clients, MapLayout map, int players = 2, bool armed = true)
    {
        MatchConfig cfg = MatchConfig.Fast(players);
        cfg.Map = map;
        cfg.Weapon = Rifle;
        cfg.StartWithWeapon = armed;
        cfg.ZoneStages = new List<ZoneStage> { new ZoneStage(600, 1, 10) };
        cfg.MaxMatchSeconds = 900;
        var match = new Match(cfg);
        clients = Enumerable.Range(0, players).Select(i => new IPEndPoint(IPAddress.Loopback, 9100 + i)).ToList();
        foreach (IPEndPoint c in clients)
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "t" });
            match.Receive(c, hello, hello.Length);
        }
        uint seq = 0;
        while (match.Phase != MatchPhase.Active)
        {
            seq++;
            foreach (IPEndPoint c in clients) Send(match, c, new InputCommand { Sequence = seq });
            match.Advance();
            match.Outbox.Clear();
        }
        return match;
    }

    private static uint _seq = 5000;

    private static void Send(Match m, IPEndPoint from, InputCommand cmd)
    {
        byte[] data = PacketCodec.Encode(new List<InputCommand> { cmd });
        m.Receive(from, data, data.Length);
    }

    private static void Act(Match m, IPEndPoint from, InputCommand cmd)
    {
        cmd.Sequence = ++_seq;
        Send(m, from, cmd);
        m.Advance();
    }

    private static void Idle(Match m, List<IPEndPoint> clients, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            ++_seq;
            foreach (IPEndPoint c in clients) Send(m, c, new InputCommand { Sequence = _seq });
            m.Advance();
            m.Outbox.Clear();
        }
    }

    private static MapLayout Open(params LootSpawn[] loot) => new("open", 60, Array.Empty<Aabb>(), loot);

    [Fact]
    public void Dropped_items_can_be_picked_up_by_id_and_partial_pickups_leave_the_rest()
    {
        Match m = Started(out var clients, Open());
        PlayerState a = m.Players[0], b = m.Players[1];
        a.Position = new Vector3(0, 0, 0);
        b.Position = new Vector3(20, 0, 0);
        a.Combat.SetCount(ItemKind.AmmoMedium, 60);
        a.Combat.SetCount(ItemKind.Bandage, 2);

        Act(m, clients[0], new InputCommand { Buttons = InputButtons.DropItem, Item = ItemKind.AmmoMedium, Target = 0 });
        Act(m, clients[0], new InputCommand { Buttons = InputButtons.DropItem, Item = ItemKind.Bandage, Target = 1 });
        Assert.Equal(2, m.GroundItemCount);
        Assert.Equal(1, a.Combat.Count(ItemKind.Bandage));
        var bandage = m.GroundItems().Single(g => g.Stack.Kind == ItemKind.Bandage);

        // B walks over, fills up with other ammo first, then picks the bandage by id.
        b.Position = a.Position + new Vector3(0.3f, 0, 0.3f);
        b.Combat.SetCount(ItemKind.AmmoMedium, 0); // drop the starting rounds' weight
        b.Combat.SetCount(ItemKind.AmmoLight, 200); // 80 of 100
        Act(m, clients[1], new InputCommand { Buttons = InputButtons.Interact, Target = InputCommand.PickUpTarget(bandage.Id) });
        Assert.Equal(1, b.Combat.Count(ItemKind.Bandage));
        Act(m, clients[1], new InputCommand { Buttons = InputButtons.Interact });
        Assert.Equal(36, b.Combat.Count(ItemKind.AmmoMedium)); // 18 weight left: 36 rounds
        Assert.Equal(60 - 36, m.GroundItems().Single().Stack.Count);
    }

    [Fact]
    public void Eliminated_players_leave_their_items_and_others_see_them_in_snapshots()
    {
        Match m = Started(out var clients, Open(), players: 3);
        PlayerState a = m.Players[0], victim = m.Players[1];
        a.Position = new Vector3(0, 0, 0);
        victim.Position = new Vector3(0, 0, 8);
        m.Players[2].Position = new Vector3(50, 0, 50);
        victim.Combat.SetCount(ItemKind.Frag, 2);
        victim.Combat.Take(new ItemStack(ItemKind.Helmet3, Items.ArmorPoints[3]));
        victim.Health = 1;

        for (int i = 0; i < 30 && victim.Alive; i++) Act(m, clients[0], new InputCommand { Buttons = InputButtons.Fire, Yaw = 0, Pitch = 0 });
        Assert.False(victim.Alive);
        var kinds = m.GroundItems().Select(g => g.Stack.Kind).ToList();
        Assert.Contains(ItemKind.Weapon, kinds);
        Assert.Contains(ItemKind.Frag, kinds);
        Assert.Contains(ItemKind.Helmet3, kinds);

        m.Outbox.Clear();
        Act(m, clients[0], new InputCommand());
        byte[] data = m.Outbox.Last(o => o.To.Equals(clients[0])).Data;
        var history = new SnapshotHistory();
        SnapshotPacket s = SnapshotCodec.Decode(new PacketReader(data, data.Length), history);
        Assert.Equal(m.GroundItemCount, s.Ground.Count);
        Assert.Contains(s.Ground, g => g.Kind == ItemKind.Helmet3 && g.Count > 0); // with whatever points it had left
        Assert.Equal(WeaponCatalog.Rifle, s.SelfSlotWeapon[0]);
    }

    [Fact]
    public void Ground_items_are_resent_only_when_they_change()
    {
        var full = new SnapshotPacket { Tick = 10 };
        full.Ground.Add(new GroundItemEntry { Id = ProtocolInfo.FirstGroundItemId, Kind = ItemKind.Medkit, Count = 1, X = 1, Y = 0, Z = 2 });
        byte[] a = SnapshotCodec.Encode(full, null);
        var history = new SnapshotHistory();
        SnapshotPacket got = SnapshotCodec.Decode(new PacketReader(a, a.Length), history);
        history.Add(got);
        Assert.Single(got.Ground);

        var next = new SnapshotPacket { Tick = 11 };
        byte[] same = SnapshotCodec.Encode(next, got, sendGround: false);
        SnapshotPacket kept = SnapshotCodec.Decode(new PacketReader(same, same.Length), history);
        Assert.Single(kept.Ground); // carried over from the baseline
        Assert.Equal(2 + 1 + 2 + 1 + 1 + 6, a.Length - same.Length); // the one entry is not resent
    }

    [Fact]
    public void A_frag_bounces_lands_and_hurts_players_in_the_open_but_not_behind_a_wall()
    {
        var wall = new[] { new Aabb(new Vector3(-3, 0, 14), new Vector3(3, 3, 14.5f)) };
        Match m = Started(out var clients, new MapLayout("walled", 60, wall), players: 3);
        PlayerState thrower = m.Players[0], open = m.Players[1], hidden = m.Players[2];
        thrower.Position = new Vector3(0, 0, 0);
        open.Position = new Vector3(4, 0, 10);
        hidden.Position = new Vector3(0, 0, 16);
        thrower.Combat.SetCount(ItemKind.Frag, 1);

        // A low throw: lands near z = 10 and rolls to a stop.
        Act(m, clients[0], new InputCommand { Buttons = InputButtons.Throw, Item = ItemKind.Frag, Yaw = 0, Pitch = -0.1f });
        Assert.Equal(0, thrower.Combat.Count(ItemKind.Frag));
        Assert.Single(m.Projectiles());
        Idle(m, clients, (int)(Match.FragFuseSeconds * 30) + 2);
        var blast = m.Projectiles().Single();
        Assert.Equal(ProjectileState.Exploded, blast.State);
        Assert.True(blast.Position.Z < 14, $"the wall stopped the grenade at {blast.Position}");
        Assert.True(open.Health < GameRules.MaxHealth || !open.Alive, "in the open: hurt");
        Assert.Equal(GameRules.MaxHealth, hidden.Health);
        Idle(m, clients, 30);
        Assert.Empty(m.Projectiles());
    }

    [Fact]
    public void Smoke_makes_a_cloud_that_lines_of_sight_pass_through_then_clears()
    {
        Match m = Started(out var clients, Open());
        PlayerState a = m.Players[0];
        a.Position = Vector3.Zero;
        a.Combat.SetCount(ItemKind.Smoke, 1);
        Act(m, clients[0], new InputCommand { Buttons = InputButtons.Throw, Item = ItemKind.Smoke, Yaw = 0, Pitch = -0.2f });
        Idle(m, clients, (int)(Match.SmokeDelaySeconds * 30) + 30);
        var cloud = m.Projectiles().Single();
        Assert.Equal(ProjectileState.Cloud, cloud.State);
        Assert.True(m.ThroughSmoke(cloud.Position + new Vector3(-20, 1.5f, 0), cloud.Position + new Vector3(20, 1.5f, 0)));
        Assert.False(m.ThroughSmoke(new Vector3(-20, 1.5f, -30), new Vector3(20, 1.5f, -30)));
        Idle(m, clients, (int)(Match.SmokeSeconds * 30) + 2);
        Assert.Empty(m.Projectiles());
    }

    [Fact]
    public void Heal_and_use_item_buttons_pick_the_heal()
    {
        Match m = Started(out var clients, Open());
        PlayerState a = m.Players[0];
        a.Health = 30;
        a.Combat.SetCount(ItemKind.Bandage, 1);
        a.Combat.SetCount(ItemKind.Medkit, 1);
        Act(m, clients[0], new InputCommand { Buttons = InputButtons.UseItem, Item = ItemKind.Bandage });
        Assert.Equal(ItemKind.Bandage, a.Combat.HealKind);
        Idle(m, clients, (int)(Items.BandageSeconds * 30) + 1);
        Assert.Equal(45f, a.Health);
        Act(m, clients[0], new InputCommand { Buttons = InputButtons.Heal });
        Assert.Equal(ItemKind.Medkit, a.Combat.HealKind); // bandages are gone; the medkit is the best left
    }
}
