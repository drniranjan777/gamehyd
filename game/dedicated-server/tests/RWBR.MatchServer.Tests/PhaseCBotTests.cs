using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.MatchServer.Platform;
using RWBR.Shared;
using RWBR.Shared.Bots;
using RWBR.Shared.Protocol;
using RWBR.Shared.Server;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase C part 5: bot skill, weapon choice and grenades, from hand-made snapshots.</summary>
public class BotBehaviourTests
{
    private const ushort Me = 1, Enemy = 2;
    private static readonly MapLayout Open = new("open", 200, Array.Empty<Aabb>());

    private static SnapshotPacket Snap(float enemyDistance, byte active = WeaponCatalog.Rifle, byte other = WeaponCatalog.None, bool enemyVisible = true)
    {
        var s = new SnapshotPacket { ZoneRadius = 500, NextZoneRadius = 500, SelfActiveSlot = 0, SelfReserveAmmo = 60 };
        s.SelfSlotWeapon[0] = active;
        s.SelfSlotAmmo[0] = 20;
        s.SelfSlotWeapon[1] = other;
        s.SelfSlotAmmo[1] = 6;
        s.Players.Add(new SnapshotEntry
        {
            PlayerId = Me, Health = 100, Ammo = 20, Weapon = active,
            Flags = PlayerFlags.Alive | PlayerFlags.HasWeapon | PlayerFlags.Grounded,
        });
        if (enemyVisible)
            s.Players.Add(new SnapshotEntry { PlayerId = Enemy, Z = enemyDistance, Health = 100, Flags = PlayerFlags.Alive | PlayerFlags.Grounded });
        return s;
    }

    private static bool Fires(BotBrain b, SnapshotPacket s, double t) => b.Decide(s, Me, t).Has(InputButtons.Fire);

    [Fact]
    public void Harder_bots_react_sooner_and_reach_further()
    {
        var easy = new BotBrain(1, Open, BotSkill.For(BotDifficulty.Easy));
        var hard = new BotBrain(1, Open, BotSkill.For(BotDifficulty.Hard));
        SnapshotPacket s = Snap(20);
        Assert.False(Fires(easy, s, 0));
        Assert.False(Fires(hard, s, 0));
        Assert.True(Fires(hard, s, 0.25));
        Assert.False(Fires(easy, s, 0.5), "easy bots are still reacting");
        Assert.True(Fires(easy, s, 1.0));

        SnapshotPacket far = Snap(55);
        var easyFar = new BotBrain(2, Open, BotSkill.For(BotDifficulty.Easy));
        var hardFar = new BotBrain(2, Open, BotSkill.For(BotDifficulty.Hard));
        easyFar.Decide(far, Me, 0);
        hardFar.Decide(far, Me, 0);
        Assert.False(Fires(easyFar, far, 2), "55 m is beyond an easy bot's range");
        Assert.True(Fires(hardFar, far, 2));
    }

    [Fact]
    public void Bots_switch_to_the_weapon_that_suits_the_range()
    {
        var bot = new BotBrain(3, Open, BotSkill.For(BotDifficulty.Normal));
        Assert.True(bot.Decide(Snap(5, active: WeaponCatalog.Sniper, other: WeaponCatalog.Shotgun), Me, 0).Has(InputButtons.SwitchWeapon), "shotgun up close");
        var bot2 = new BotBrain(4, Open, BotSkill.For(BotDifficulty.Normal));
        Assert.True(bot2.Decide(Snap(90, active: WeaponCatalog.Shotgun, other: WeaponCatalog.Sniper), Me, 0).Has(InputButtons.SwitchWeapon), "sniper far away");
        var bot3 = new BotBrain(5, Open, BotSkill.For(BotDifficulty.Normal));
        Assert.False(bot3.Decide(Snap(30, active: WeaponCatalog.Rifle, other: WeaponCatalog.Smg), Me, 0).Has(InputButtons.SwitchWeapon), "rifle is fine at 30 m");
        var easy = new BotBrain(6, Open, BotSkill.For(BotDifficulty.Easy));
        Assert.False(easy.Decide(Snap(5, active: WeaponCatalog.Sniper, other: WeaponCatalog.Shotgun), Me, 0).Has(InputButtons.SwitchWeapon), "easy bots don't");
    }

    [Fact]
    public void A_bot_lobs_a_frag_at_an_enemy_who_just_ducked_out_of_sight()
    {
        var bot = new BotBrain(7, Open, BotSkill.For(BotDifficulty.Normal));
        bot.Decide(Snap(12), Me, 0); // seen at 12 m
        SnapshotPacket hidden = Snap(12, enemyVisible: false);
        hidden.SelfItems.Add(new ItemCount { Kind = ItemKind.Frag, Count = 1 });
        InputCommand c = bot.Decide(hidden, Me, 1);
        Assert.True(c.Has(InputButtons.Throw));
        Assert.Equal(ItemKind.Frag, c.Item);
        Assert.Equal(BotSkill.ThrowPitch(12), c.Pitch, 3);
        Assert.False(bot.Decide(hidden, Me, 2).Has(InputButtons.Throw), "not again straight away");
        var easy = new BotBrain(8, Open, BotSkill.For(BotDifficulty.Easy));
        easy.Decide(Snap(12), Me, 0);
        Assert.False(easy.Decide(hidden, Me, 1).Has(InputButtons.Throw), "easy bots don't use grenades");
    }

    [Theory]
    [InlineData(8f)]
    [InlineData(14f)]
    [InlineData(20f)]
    public void Throw_pitch_lands_a_server_grenade_near_the_distance(float distance)
    {
        var cfg = new MatchConfig { Map = Open, MinPlayers = 2, MaxPlayers = 2, CountdownSeconds = 0.1f, StartWithWeapon = true, Vehicles = false };
        var m = new Match(cfg);
        var a = new IPEndPoint(IPAddress.Loopback, 9700);
        var b = new IPEndPoint(IPAddress.Loopback, 9701);
        foreach (var ep in new[] { a, b })
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "t" });
            m.Receive(ep, hello, hello.Length);
        }
        uint seq = 0;
        void Step(InputCommand cmd)
        {
            seq++;
            cmd.Sequence = seq;
            byte[] da = PacketCodec.Encode(new List<InputCommand> { cmd });
            m.Receive(a, da, da.Length);
            byte[] db = PacketCodec.Encode(new List<InputCommand> { new() { Sequence = seq } });
            m.Receive(b, db, db.Length);
            m.Advance();
            m.Outbox.Clear();
        }
        while (m.Phase != MatchPhase.Active) Step(new InputCommand());
        m.Players[0].Position = Vector3.Zero;
        m.Players[1].Position = new Vector3(100, 0, 100);
        m.Players[0].Combat.SetCount(ItemKind.Smoke, 1); // smoke: no blast to end the test early
        Step(new InputCommand { Buttons = InputButtons.Throw, Item = ItemKind.Smoke, Yaw = 0, Pitch = BotSkill.ThrowPitch(distance) });
        for (int i = 0; i < 40; i++) Step(new InputCommand());
        var (_, _, landed) = m.Projectiles().Single();
        float z = landed.Z;
        Assert.InRange(z, distance - 2.5f, distance + 6f); // it rolls a little after landing
    }
}

public class OfflineDifficultyTests
{
    [Fact]
    public void Offline_bot_matches_use_the_chosen_difficulty()
    {
        var local = new LocalMatch(LocalMatch.BotMatchConfig(MapLayout.GreyboxBlock(40f), 3), 3, seed: 1, BotDifficulty.Hard);
        Assert.All(local.Bots, b => Assert.Equal(BotSkill.For(BotDifficulty.Hard).ReactionSeconds, b.Skill.ReactionSeconds));
    }
}

/// <summary>Phase C part 5: the match fills empty seats with bots, even on a ticket-only server.</summary>
public class FillBotTests
{
    [Fact]
    public void Bots_fill_a_ticketed_match_and_the_result_keeps_the_players_real_place()
    {
        byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 5)).ToArray();
        var cfg = new MatchConfig
        {
            Map = MapLayout.GreyboxBlock(40f), MinPlayers = 1, MaxPlayers = 6, FillBotsTo = 6, BotDifficulty = BotDifficulty.Hard,
            CountdownSeconds = 0.5f, TicketSecret = secret, MatchId = Guid.NewGuid(), StartWithWeapon = true, ZoneDamagePerSecond = 25,
            ZoneStages = new List<ZoneStage> { new(3, 6, 10), new(1, 4, 0) }, MaxMatchSeconds = 120,
        };
        var m = new Match(cfg);
        var human = new IPEndPoint(IPAddress.Loopback, 9800);
        Guid account = Guid.NewGuid();
        byte[] ticket = JoinTicket.Issue(secret, cfg.MatchId, account, "Asha", DateTimeOffset.UtcNow.AddMinutes(2), out byte[] key);
        byte[] hello = PacketCodec.Encode(new HelloPacket { Name = "Asha", Ticket = ticket });
        m.Receive(human, hello, hello.Length);
        Assert.Single(m.Players);

        var auth = new PacketAuth(key);
        uint seq = 0;
        while (!m.IsFinished && m.Tick < 30 * 200)
        {
            seq++;
            byte[] input = auth.Sign(PacketCodec.Encode(new List<InputCommand> { new() { Sequence = seq } })); // stands still
            m.Receive(human, input, input.Length);
            m.Advance();
            Assert.DoesNotContain(m.Outbox, o => o.To.Address.Equals(IPAddress.Parse("127.0.0.2"))); // bot traffic never reaches the network
            m.Outbox.Clear();
        }
        Assert.Equal(5, m.FillBotCount);
        Assert.Equal(6, m.Players.Count);
        Assert.All(m.Players.Where(p => p.IsBot), p => Assert.Equal(Guid.Empty, p.AccountId));
        Assert.True(m.Stats.ShotsFired > 0, "hard bots fight");

        ResultSubmission? r = ManagedServer.BuildResult(m, cfg.Map.Version);
        Assert.NotNull(r);
        Assert.Equal(5, r!.Bots);
        ResultParticipant me = Assert.Single(r.Participants);
        Assert.Equal(account, me.AccountId);
        Assert.Equal(m.Players.Single(p => !p.IsBot).Placement, me.Placement); // not renumbered to 1 among players
        if (r.Reason == "WINNER" && me.Placement != 1) Assert.Null(r.WinnerAccountId); // a bot won
    }
}
