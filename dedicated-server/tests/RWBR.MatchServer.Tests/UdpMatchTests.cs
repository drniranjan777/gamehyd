using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RWBR.Bots;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace RWBR.MatchServer.Tests;

/// <summary>End-to-end over real UDP sockets on loopback: server + headless bots.</summary>
public class UdpMatchTests
{
    private readonly ITestOutputHelper _out;

    public UdpMatchTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(8)]
    public async Task Bots_play_a_full_match_to_one_server_decided_winner(int players)
    {
        var match = new Match(MatchConfig.Fast(players));
        using var host = new UdpMatchHost(match, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task<HostReport> server = host.RunAsync(cts.Token);

        var endpoint = new IPEndPoint(IPAddress.Loopback, host.Port);
        var bots = await Task.Run(() => BotSwarm.Run(endpoint, players, TimeSpan.FromSeconds(80), ct: cts.Token));
        HostReport report = await server;

        _out.WriteLine($"ticks={report.Ticks} active={report.ActiveSeconds}s tickAvg={report.TickMsAvg}ms p99={report.TickMsP99}ms " +
                       $"rate={report.TickRateAchieved}Hz out={report.KbpsOutPerPlayer}kbps/player shots={report.Stats.ShotsFired} hits={report.Stats.Hits}");

        Assert.Equal(CompletionReason.Winner, report.Reason);
        Assert.All(bots, b => Assert.NotNull(b.Result));
        MatchCompletedPacket result = bots[0].Result!;
        Assert.All(bots, b => Assert.Equal(result.WinnerId, b.Result!.WinnerId));
        Assert.Equal(Enumerable.Range(1, players), result.Placements.Select(p => (int)p.Placement).OrderBy(p => p));
        Assert.Equal(result.WinnerId, result.Placements.Single(p => p.Placement == 1).PlayerId);
        foreach (BotClient b in bots) b.Dispose();
    }
}
