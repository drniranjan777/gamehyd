using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RWBR.Bots;
using RWBR.Shared;
using RWBR.Shared.Server;
using RWBR.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace RWBR.MatchServer.Tests;

public class SnapshotCodecTests
{
    private static SnapshotPacket Snap(uint tick, params SnapshotEntry[] players)
    {
        var p = new SnapshotPacket { Tick = tick, Phase = MatchPhase.Active, ZoneRadius = 50, AckSequence = 9, LootTaken = new byte[] { 5 } };
        foreach (SnapshotEntry e in players) p.Players.Add(Quantize.Normalize(e));
        return p;
    }

    private static SnapshotEntry Player(ushort id, float x, float z, byte health = 100) =>
        new() { PlayerId = id, X = x, Y = 0, Z = z, Yaw = 0.5f, Health = health, Ammo = 30, Flags = PlayerFlags.Alive };

    private static SnapshotPacket RoundTrip(SnapshotPacket p, SnapshotPacket? baseline, SnapshotHistory history)
    {
        byte[] bytes = SnapshotCodec.Encode(p, baseline);
        return SnapshotCodec.Decode(new PacketReader(bytes, bytes.Length), history);
    }

    [Fact]
    public void Delta_decodes_to_exactly_the_same_state_as_full()
    {
        var history = new SnapshotHistory();
        SnapshotPacket t1 = Snap(1, Player(1, 10.123f, -4.5f), Player(2, 0, 0), Player(3, 50, 50));
        SnapshotPacket got1 = RoundTrip(t1, null, history);
        history.Add(got1);

        SnapshotPacket t2 = Snap(2, Player(1, 10.333f, -4.4f), Player(2, 0, 0, health: 80), Player(4, 7, 7));
        SnapshotPacket delta = RoundTrip(t2, t1, history);
        SnapshotPacket full = RoundTrip(t2, null, new SnapshotHistory());

        Assert.Equal(1u, delta.BaselineTick);
        Assert.Equal(full.Players.Select(e => (e.PlayerId, e.X, e.Y, e.Z, e.Yaw, e.Health, e.Ammo, e.Flags)),
                     delta.Players.Select(e => (e.PlayerId, e.X, e.Y, e.Z, e.Yaw, e.Health, e.Ammo, e.Flags)));
        Assert.Equal(80, delta.Players.Single(e => e.PlayerId == 2).Health);
        Assert.DoesNotContain(delta.Players, e => e.PlayerId == 3); // left the snapshot
    }

    [Fact]
    public void Unchanged_players_cost_three_bytes_moving_nine_full_twentyfour()
    {
        SnapshotPacket idle1 = Snap(1, Enumerable.Range(1, 30).Select(i => Player((ushort)i, i, i)).ToArray());
        SnapshotPacket idle2 = Snap(2, Enumerable.Range(1, 30).Select(i => Player((ushort)i, i, i)).ToArray());
        SnapshotPacket moved = Snap(2, Enumerable.Range(1, 30).Select(i => Player((ushort)i, i + 0.1f, i)).ToArray());

        int full = SnapshotCodec.Encode(idle2, null).Length;
        int idle = SnapshotCodec.Encode(idle2, idle1).Length;
        int moving = SnapshotCodec.Encode(moved, idle1).Length;
        int header = SnapshotCodec.Encode(Snap(2), null).Length;

        Assert.Equal(30 * 3, idle - header);
        Assert.Equal(30 * 9, moving - header); // id + mask + 3 × int16 (yaw unchanged)
        Assert.Equal(30 * 24, full - header); // id + mask + 3 × int32 + yaw/pitch + health/armor/ammo/flags + weapon/shots
    }

    [Fact]
    public void Quantisation_error_is_under_half_a_centimetre_and_a_tenth_of_a_degree()
    {
        var rng = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            float x = (float)(rng.NextDouble() * 200 - 100), yaw = (float)(rng.NextDouble() * 6.2 - 3.1);
            SnapshotEntry n = Quantize.Normalize(new SnapshotEntry { X = x, Yaw = yaw });
            Assert.True(Math.Abs(n.X - x) <= 0.005f + 1e-5f);
            Assert.True(Math.Abs(n.Yaw - yaw) <= 0.0001f + 1e-5f || Math.Abs(Math.Abs(n.Yaw - yaw) - 2 * Math.PI) < 1e-3);
            Assert.Equal(n.X, Quantize.Normalize(n).X); // stable: baselines re-quantise exactly
        }
    }

    [Fact]
    public void Delta_against_a_missing_baseline_is_rejected_not_misread()
    {
        SnapshotPacket t1 = Snap(1, Player(1, 1, 1)), t2 = Snap(2, Player(1, 2, 2));
        byte[] bytes = SnapshotCodec.Encode(t2, t1);
        Assert.Throws<MissingBaselineException>(() => SnapshotCodec.Decode(new PacketReader(bytes, bytes.Length), new SnapshotHistory()));
    }
}

public class TicketTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly Guid MatchId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Ticket_verifies_and_both_sides_derive_the_same_session_key()
    {
        byte[] t = JoinTicket.Issue(Secret, MatchId, Guid.NewGuid(), "Asha", Now.AddMinutes(2), out byte[] key);
        Assert.True(JoinTicket.TryVerify(t, Secret, out JoinTicket? parsed));
        Assert.Equal(MatchId, parsed!.MatchId);
        Assert.Equal("Asha", parsed.DisplayName);
        Assert.Equal(key, JoinTicket.DeriveSessionKey(Secret, parsed.TicketId));
        Assert.DoesNotContain(Convert.ToHexString(key), Convert.ToHexString(t)); // key never travels in the ticket
    }

    [Fact]
    public void Tickets_issued_by_the_node_api_verify_here_with_the_same_session_key()
    {
        // Produced by apps/api/src/tickets/join-ticket.ts (issueTicket) for these exact inputs.
        byte[] ticket = Convert.FromHexString(
            "010c0d0e0f0a0b080907060504030201001111111122223333444455555555555566666666777788889999aaaaaaaaaaaa00d2496b08" +
            "4173686120e29c938e8c25c95c78129b23b555e8ee5b5ec4a90c89d463a6e63ca50558aa08d3d753");
        byte[] secret = Convert.FromHexString(string.Concat(Enumerable.Repeat("a1", 32)));
        Assert.True(JoinTicket.TryVerify(ticket, secret, out JoinTicket? t));
        Assert.Equal(Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"), t!.TicketId);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), t.MatchId);
        Assert.Equal(Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), t.AccountId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), t.ExpiresAt);
        Assert.Equal("Asha ✓", t.DisplayName);
        Assert.Equal("9d77267fd1ee9bf1a3cbe5b86081f899", Convert.ToHexString(JoinTicket.DeriveSessionKey(secret, t.TicketId)).ToLowerInvariant());
    }

    [Fact]
    public void Tampered_tickets_and_wrong_secrets_fail()
    {
        byte[] t = JoinTicket.Issue(Secret, MatchId, Guid.NewGuid(), "Asha", Now, out _);
        byte[] tampered = (byte[])t.Clone();
        tampered[20] ^= 1;
        Assert.False(JoinTicket.TryVerify(tampered, Secret, out _));
        Assert.False(JoinTicket.TryVerify(t, new byte[32], out _));
        Assert.False(JoinTicket.TryVerify(t.Take(10).ToArray(), Secret, out _));
    }

    [Fact]
    public void Packet_tags_detect_any_change()
    {
        using var auth = new PacketAuth(new byte[16]);
        byte[] signed = auth.Sign(new byte[] { 2, 1, 2, 3 });
        Assert.True(auth.Verify(signed, signed.Length));
        signed[2] ^= 0x10;
        Assert.False(auth.Verify(signed, signed.Length));
        using var other = new PacketAuth(Enumerable.Repeat((byte)7, 16).ToArray());
        Assert.False(other.Verify(auth.Sign(new byte[] { 2, 9 }), 10));
    }

    private sealed class Client
    {
        public Client(int port) => EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        public IPEndPoint EndPoint;
        public byte[] Key = Array.Empty<byte>();
        public byte[] Ticket = Array.Empty<byte>();
        public Guid Token;
        public RejectReason? Rejected;
        public bool Welcomed;
    }

    private static Match TicketMatch(DateTimeOffset now) =>
        new(new MatchConfig { MinPlayers = 4, MaxPlayers = 4, TicketSecret = Secret, MatchId = MatchId }, () => now);

    private static void Hello(Match m, Client c, bool signWithKey = false)
    {
        byte[] hello = PacketCodec.Encode(new HelloPacket { SessionToken = c.Token, Name = "ignored", Ticket = c.Token == Guid.Empty ? c.Ticket : Array.Empty<byte>() });
        if (signWithKey)
        {
            using var a = new PacketAuth(c.Key);
            hello = a.Sign(hello);
        }
        m.Receive(c.EndPoint, hello, hello.Length);
        Read(m, c);
    }

    private static void Read(Match m, Client c)
    {
        foreach (var (to, data) in m.Outbox.Where(o => o.To.Equals(c.EndPoint)))
        {
            var r = new PacketReader(data, data.Length);
            if (r.Type == PacketType.Reject) c.Rejected = PacketCodec.DecodeReject(r);
            if (r.Type == PacketType.Welcome)
            {
                c.Welcomed = true;
                c.Token = PacketCodec.DecodeWelcome(r).SessionToken;
            }
        }
        m.Outbox.Clear();
    }

    private static Client WithTicket(int port, DateTimeOffset expires, Guid? match = null, Guid? account = null)
    {
        var c = new Client(port);
        c.Ticket = JoinTicket.Issue(Secret, match ?? MatchId, account ?? Guid.NewGuid(), $"p{port}", expires, out c.Key);
        return c;
    }

    [Fact]
    public void Server_in_ticket_mode_rejects_missing_expired_foreign_and_reused_tickets()
    {
        Match m = TicketMatch(Now);
        var none = new Client(1);
        Hello(m, none);
        Assert.Equal(RejectReason.InvalidTicket, none.Rejected);

        Client expired = WithTicket(2, Now.AddSeconds(-1));
        Hello(m, expired);
        Assert.Equal(RejectReason.TicketExpired, expired.Rejected);

        Client foreign = WithTicket(3, Now.AddMinutes(1), match: Guid.NewGuid());
        Hello(m, foreign);
        Assert.Equal(RejectReason.WrongMatch, foreign.Rejected);

        Client ok = WithTicket(4, Now.AddMinutes(1));
        Hello(m, ok);
        Assert.True(ok.Welcomed);
        Assert.Equal("p4", m.Players.Single().Name); // display name comes from the signed ticket

        var replay = new Client(5) { Ticket = ok.Ticket };
        Hello(m, replay);
        Assert.Equal(RejectReason.TicketReused, replay.Rejected);
        Assert.Equal(4, m.Stats.TicketsRejected);
    }

    [Fact]
    public void Unsigned_or_forged_inputs_are_dropped_and_signed_ones_accepted()
    {
        Match m = TicketMatch(Now);
        Client c = WithTicket(10, Now.AddMinutes(1));
        Hello(m, c);
        byte[] input = PacketCodec.Encode(new List<InputCommand> { new() { Sequence = 1, MoveZ = 1 } });

        m.Receive(c.EndPoint, input, input.Length);
        Assert.Equal(1, m.Stats.BadTags);

        using var wrong = new PacketAuth(new byte[16]);
        byte[] forged = wrong.Sign(input);
        m.Receive(c.EndPoint, forged, forged.Length);
        Assert.Equal(2, m.Stats.BadTags);
        Assert.Empty(m.Players[0].PendingInputs);

        using var right = new PacketAuth(c.Key);
        byte[] signed = right.Sign(input);
        m.Receive(c.EndPoint, signed, signed.Length);
        Assert.Single(m.Players[0].PendingInputs);
    }

    [Fact]
    public void Reconnect_needs_the_session_key_not_just_the_token()
    {
        Match m = TicketMatch(Now);
        Client c = WithTicket(20, Now.AddMinutes(1));
        Hello(m, c);
        Guid token = c.Token;

        var thief = new Client(21) { Token = token };
        Hello(m, thief);
        Assert.Equal(RejectReason.InvalidSessionToken, thief.Rejected);

        c.EndPoint = new IPEndPoint(IPAddress.Loopback, 22);
        c.Welcomed = false;
        Hello(m, c, signWithKey: true);
        Assert.True(c.Welcomed);
        Assert.NotEqual(token, c.Token);
    }
}

public class DeltaBaselineTests
{
    [Fact]
    public void Server_deltas_against_the_tick_each_client_acknowledged()
    {
        MatchConfig cfg = MatchConfig.Fast(2);
        var m = new Match(cfg);
        var a = new IPEndPoint(IPAddress.Loopback, 30);
        var b = new IPEndPoint(IPAddress.Loopback, 31);
        foreach (IPEndPoint ep in new[] { a, b })
        {
            byte[] hello = PacketCodec.Encode(new HelloPacket());
            m.Receive(ep, hello, hello.Length);
        }

        var history = new SnapshotHistory();
        SnapshotPacket? last = null;
        uint seq = 0;
        for (int i = 0; i < 20; i++)
        {
            byte[] input = PacketCodec.Encode(new List<InputCommand> { new() { Sequence = ++seq, MoveZ = 1, AckTick = last?.Tick ?? 0 } });
            m.Receive(a, input, input.Length);
            m.Advance();
            byte[] data = m.Outbox.Last(o => o.To.Equals(a) && o.Data[0] == (byte)PacketType.Snapshot).Data;
            m.Outbox.Clear();
            last = SnapshotCodec.Decode(new PacketReader(data, data.Length), history);
            history.Add(last);
        }

        Assert.NotEqual(0u, last!.BaselineTick);
        Assert.True(m.Stats.DeltaSnapshots > 10);
        // b never acknowledged anything: it only ever gets full snapshots.
        Assert.True(m.Stats.FullSnapshots >= 20);
        PlayerState pa = m.Players.Single(p => p.EndPoint.Equals(a));
        SnapshotEntry seen = last.Players.Single(e => e.PlayerId == pa.Id);
        Assert.True(Math.Abs(seen.Z - pa.Position.Z) < 0.01f);
    }
}

public class ImpairedNetworkTests
{
    private readonly ITestOutputHelper _out;

    public ImpairedNetworkTests(ITestOutputHelper output) => _out = output;

    private async Task<(HostReport, List<BotClient>)> Play(MatchConfig cfg, Func<int, BotOptions> options)
    {
        var match = new Match(cfg);
        using var host = new UdpMatchHost(match, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Task<HostReport> server = host.RunAsync(cts.Token);
        var bots = await Task.Run(() => BotSwarm.Run(new IPEndPoint(IPAddress.Loopback, host.Port), cfg.MaxPlayers,
            TimeSpan.FromSeconds(110), ct: cts.Token, options: options));
        HostReport report = await server;
        _out.WriteLine($"active={report.ActiveSeconds}s out={report.KbpsOutPerPlayer}kbps/player delta={report.Stats.DeltaSnapshots} " +
                       $"full={report.Stats.FullSnapshots} dupes={report.Stats.DuplicateInputs} missingBaselines={bots.Sum(b => b.MissingBaselines)}");
        return (report, bots);
    }

    [Fact]
    public async Task Match_completes_under_poor_4g_loss_latency_and_jitter()
    {
        var (report, bots) = await Play(MatchConfig.Fast(8), _ => new BotOptions { LossRate = 0.05, LatencyMs = 75, JitterMs = 40 });
        Assert.Equal(CompletionReason.Winner, report.Reason);
        Assert.All(bots, b => Assert.NotNull(b.Result));
        Assert.True(report.Stats.DeltaSnapshots > report.Stats.FullSnapshots, "deltas should dominate even with loss");
        foreach (BotClient b in bots) b.Dispose();
    }

    [Fact]
    public async Task Match_completes_with_signed_tickets_and_packet_tags()
    {
        byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        MatchConfig cfg = MatchConfig.Fast(8);
        cfg.TicketSecret = secret;
        cfg.MatchId = Guid.NewGuid();
        var (report, bots) = await Play(cfg, i =>
        {
            byte[] t = JoinTicket.Issue(secret, cfg.MatchId, Guid.NewGuid(), $"bot{i}", DateTimeOffset.UtcNow.AddMinutes(2), out byte[] key);
            return new BotOptions { Ticket = t, SessionKey = key };
        });
        Assert.Equal(CompletionReason.Winner, report.Reason);
        Assert.Equal(0, report.Stats.BadTags);
        Assert.Equal(0, report.Stats.TicketsRejected);
        foreach (BotClient b in bots) b.Dispose();
    }
}
