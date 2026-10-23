using System.Collections.Generic;
using System.Linq;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using RWBR.Shared.Server;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Phase C part 1: the authoritative match hosted in-process with bots, as the game's offline bot match uses it.</summary>
public class PhaseCBotMatchTests
{
    /// <summary>A local player that only sends empty inputs (it never moves) and records what it receives.</summary>
    private sealed class IdlePlayer
    {
        private readonly SnapshotHistory _history = new();
        private uint _seq;

        public ushort Id = ProtocolInfo.NoPlayer;
        public SnapshotPacket? Latest;
        public MatchCompletedPacket? Result;
        public int Snapshots;

        public void Join(LocalMatch local) => local.SendFromPlayer(PacketCodec.Encode(new HelloPacket { Name = "Me" }));

        public void Pump(LocalMatch local)
        {
            while (local.TryReceiveForPlayer(out byte[] data))
            {
                var r = new PacketReader(data, data.Length);
                if (r.Type == PacketType.Welcome) Id = PacketCodec.DecodeWelcome(r).PlayerId;
                else if (r.Type == PacketType.Snapshot)
                {
                    SnapshotPacket s = SnapshotCodec.Decode(r, _history);
                    _history.Add(s);
                    Latest = s;
                    Snapshots++;
                }
                else if (r.Type == PacketType.MatchCompleted) Result ??= PacketCodec.DecodeMatchCompleted(r);
            }
            if (Id == ProtocolInfo.NoPlayer) return;
            var cmd = new InputCommand { Sequence = ++_seq, AckTick = Latest?.Tick ?? 0 };
            local.SendFromPlayer(PacketCodec.Encode(new List<InputCommand> { cmd }));
        }
    }

    [Fact]
    public void A_bot_match_on_the_city_runs_to_a_result_for_the_local_player()
    {
        MapLayout city = CityMap.Build();
        const int bots = 11;
        MatchConfig cfg = LocalMatch.BotMatchConfig(city, bots);
        Assert.False(cfg.StartWithWeapon, "bot matches start unarmed: loot first");
        var local = new LocalMatch(cfg, bots, seed: 3);
        var me = new IdlePlayer();
        me.Join(local);

        long maxTicks = cfg.Ticks(cfg.MaxMatchSeconds + cfg.CountdownSeconds + 30);
        while (!local.Match.IsFinished && local.Match.Tick < maxTicks)
        {
            me.Pump(local);
            local.Tick();
        }
        me.Pump(local);

        Assert.NotEqual(ProtocolInfo.NoPlayer, me.Id);
        Assert.Equal(bots + 1, local.Match.Players.Count);
        Assert.True(me.Snapshots > 100);
        Assert.NotNull(me.Result);
        Assert.Contains(me.Result!.Placements, p => p.PlayerId == me.Id);
        Assert.Equal(bots + 1, me.Result.Placements.Count);
        int kills = local.Match.Players.Sum(p => p.Kills);
        Assert.True(kills > 0, "bots loot weapons and fight");
        Assert.True(local.Match.Stats.Pickups > 0, "bots pick up loot");
    }

    [Fact]
    public void A_paused_local_match_does_not_tick_and_slow_frames_do_not_jump()
    {
        MapLayout map = MapLayout.GreyboxBlock(40f);
        var local = new LocalMatch(LocalMatch.BotMatchConfig(map, 3), 3, seed: 1);
        Assert.Equal(1, local.Advance(1.0 / 30 + 1e-6));
        local.Paused = true;
        Assert.Equal(0, local.Advance(1.0));
        local.Paused = false;
        Assert.Equal(LocalMatch.MaxTicksPerAdvance, local.Advance(5.0));
        Assert.Equal(1 + LocalMatch.MaxTicksPerAdvance, local.Match.Tick);
    }
}
