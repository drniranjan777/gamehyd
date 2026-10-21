using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Deterministic tests that drive <see cref="Match"/> tick by tick without sockets.</summary>
public class MatchAuthorityTests
{
    private sealed class FakeClient
    {
        private readonly Match _match;
        private uint _seq;

        public FakeClient(Match match, int port)
        {
            _match = match;
            EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        }

        public IPEndPoint EndPoint { get; set; }
        public ushort Id { get; private set; }
        public Guid Token { get; private set; }
        public RejectReason? Rejected { get; private set; }
        public PlayerState State => _match.Players.Single(p => p.Id == Id);

        public void Hello(Guid token = default) => Deliver(PacketCodec.Encode(new HelloPacket { SessionToken = token, Name = "t" }));

        public uint Input(InputCommand cmd)
        {
            cmd.Sequence = cmd.Sequence == 0 ? ++_seq : cmd.Sequence;
            Deliver(PacketCodec.Encode(new List<InputCommand> { cmd }));
            return cmd.Sequence;
        }

        public void Deliver(byte[] data) => _match.Receive(EndPoint, data, data.Length);

        public void Read()
        {
            foreach (var (to, data) in _match.Outbox.Where(o => o.To.Equals(EndPoint)))
            {
                var r = new PacketReader(data, data.Length);
                if (r.Type == PacketType.Welcome)
                {
                    WelcomePacket w = PacketCodec.DecodeWelcome(r);
                    Id = w.PlayerId;
                    Token = w.SessionToken;
                }
                else if (r.Type == PacketType.Reject)
                {
                    Rejected = PacketCodec.DecodeReject(r);
                }
            }
        }
    }

    private static (Match, List<FakeClient>) StartedMatch(int players, Action<MatchConfig>? tweak = null)
    {
        MatchConfig cfg = MatchConfig.Fast(players);
        cfg.Map = new MapLayout("open-40", 40, new[] { Aabb.FromCenter(0, 0, 4, 4, 5) });
        tweak?.Invoke(cfg);
        var match = new Match(cfg);
        var clients = Enumerable.Range(0, players).Select(i => new FakeClient(match, 5000 + i)).ToList();
        foreach (FakeClient c in clients)
        {
            c.Hello();
            c.Read();
        }
        match.Outbox.Clear();
        while (match.Phase != MatchPhase.Active)
        {
            foreach (FakeClient c in clients) c.Input(new InputCommand());
            Step(match);
        }
        return (match, clients);
    }

    private static void Step(Match m, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            m.Advance();
            m.Outbox.Clear();
        }
    }

    [Fact]
    public void Match_starts_after_countdown_once_min_players_join()
    {
        var (match, clients) = StartedMatch(4);
        Assert.Equal(MatchPhase.Active, match.Phase);
        Assert.All(clients, c => Assert.True(c.State.Alive));
        Assert.Equal(4, match.Players.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Speed_hack_and_input_bursts_are_capped_to_sprint_speed()
    {
        var (match, clients) = StartedMatch(2);
        FakeClient cheater = clients[0];
        Vector3 start = cheater.State.Position;
        float yaw = MathF.Atan2(-start.X, -start.Z); // walk toward the open side of the map

        int ticks = match.Config.TickRate; // one second
        for (int i = 0; i < ticks; i++)
        {
            // 3 inputs per tick, each with an absurd move vector.
            for (int k = 0; k < 3; k++)
                cheater.Input(new InputCommand { MoveZ = 500, MoveX = 0, Yaw = yaw + MathF.PI, Buttons = InputButtons.Sprint });
            Step(match);
        }

        float moved = Vector3.Distance(start, cheater.State.Position);
        Assert.True(moved <= GameRules.SprintSpeed * 1.0f + 0.05f, $"moved {moved} m in 1 s");
        Assert.True(match.Stats.DroppedBacklogInputs > 0);
    }

    [Fact]
    public void Replayed_and_duplicate_inputs_are_ignored()
    {
        var (match, clients) = StartedMatch(2);
        FakeClient c = clients[0];
        uint seq = c.Input(new InputCommand { MoveZ = 1 });
        Step(match);
        Assert.Equal(seq, c.State.LastProcessedSequence);

        long dupesBefore = match.Stats.DuplicateInputs;
        c.Input(new InputCommand { Sequence = seq, MoveZ = 1 });
        c.Input(new InputCommand { Sequence = seq - 1, MoveZ = 1 });
        Assert.Equal(dupesBefore + 2, match.Stats.DuplicateInputs);
        Assert.Empty(c.State.PendingInputs);
    }

    [Fact]
    public void Holding_fire_cannot_exceed_weapon_cadence()
    {
        var (match, clients) = StartedMatch(2);
        FakeClient shooter = clients[0];
        int ticks = match.Config.TickRate; // one second of "fire" on every tick
        for (int i = 0; i < ticks; i++)
        {
            shooter.Input(new InputCommand { Yaw = 1.0f, Buttons = InputButtons.Fire });
            Step(match);
        }
        int maxShots = (int)Math.Ceiling(1.0 / match.Config.Weapon.FireIntervalSeconds) + 1;
        Assert.InRange(match.Stats.ShotsFired, 1, maxShots);
        Assert.Equal(match.Config.Weapon.MagazineSize - match.Stats.ShotsFired, shooter.State.Ammo);
    }

    [Fact]
    public void Hits_require_line_of_sight()
    {
        var (match, clients) = StartedMatch(2);
        PlayerState a = clients[0].State, b = clients[1].State;
        a.Position = new Vector3(0, 0, -10);
        b.Position = new Vector3(0, 0, 10); // the 4x4x5 box at the origin is between them
        Array.Fill(b.History, b.Position);

        for (int i = 0; i < 10; i++)
        {
            clients[0].Input(new InputCommand { Yaw = 0, Buttons = InputButtons.Fire, AckTick = (uint)match.Tick });
            Step(match);
        }
        Assert.True(match.Stats.ShotsFired > 0);
        Assert.Equal(0, match.Stats.Hits);
        Assert.Equal(GameRules.MaxHealth, b.Health);
    }

    [Fact]
    public void Server_decides_elimination_and_winner()
    {
        var (match, clients) = StartedMatch(2);
        PlayerState a = clients[0].State, b = clients[1].State;
        a.Position = new Vector3(-10, 0, -10);
        b.Position = new Vector3(-10, 0, 0);
        Array.Fill(b.History, b.Position);

        for (int i = 0; i < 60 && match.Phase == MatchPhase.Active; i++)
        {
            clients[0].Input(new InputCommand { Yaw = 0, Pitch = -0.02f, Buttons = InputButtons.Fire, AckTick = (uint)match.Tick });
            clients[1].Input(new InputCommand());
            Step(match);
        }

        Assert.Equal(MatchPhase.Completed, match.Phase);
        Assert.NotNull(match.Result);
        Assert.Equal(CompletionReason.Winner, match.Result!.Reason);
        Assert.Equal(a.Id, match.Result.WinnerId);
        Assert.Equal(1, a.Placement);
        Assert.Equal(2, b.Placement);
        Assert.Equal(1, a.Kills);
    }

    [Fact]
    public void Packets_from_unknown_senders_and_unknown_types_change_nothing()
    {
        var (match, clients) = StartedMatch(2);
        var stranger = new FakeClient(match, 6000);
        stranger.Input(new InputCommand { MoveZ = 1, Buttons = InputButtons.Fire });
        stranger.Deliver(new byte[] { 99, 1, 2, 3 }); // e.g. a forged "I won" message: no such packet exists
        Step(match);
        Assert.Equal(1, match.Stats.UnknownSenderPackets);
        Assert.Equal(1, match.Stats.MalformedPackets);
        Assert.Equal(2, match.Players.Count);
        Assert.Equal(MatchPhase.Active, match.Phase);
    }

    [Fact]
    public void Reconnect_within_grace_keeps_the_player_and_rotates_the_token()
    {
        var (match, clients) = StartedMatch(2);
        FakeClient c = clients[0];
        Guid oldToken = c.Token;
        ushort id = c.Id;

        // Go silent past the disconnect timeout but inside the grace period.
        Step(match, match.Config.Ticks(match.Config.DisconnectTimeoutSeconds) + 2);
        Assert.False(c.State.Connected);
        Assert.True(c.State.Alive);

        c.EndPoint = new IPEndPoint(IPAddress.Loopback, 7001); // new network, new port
        c.Hello(oldToken);
        c.Read();
        Assert.Equal(id, c.Id);
        Assert.NotEqual(oldToken, c.Token);
        Assert.True(c.State.Connected);

        // The old token is single-use.
        var thief = new FakeClient(match, 7002);
        thief.Hello(oldToken);
        thief.Read();
        Assert.Equal(RejectReason.InvalidSessionToken, thief.Rejected);
    }

    [Fact]
    public void Disconnect_beyond_grace_eliminates_the_player()
    {
        var (match, clients) = StartedMatch(3);
        int grace = match.Config.Ticks(match.Config.ReconnectGraceSeconds);
        for (int i = 0; i < grace + 5; i++)
        {
            clients[1].Input(new InputCommand());
            clients[2].Input(new InputCommand());
            Step(match);
        }
        Assert.False(clients[0].State.Alive);
        Assert.Equal(3, clients[0].State.Placement);
    }

    [Fact]
    public void Late_joiners_are_rejected_once_the_match_is_active()
    {
        var (match, _) = StartedMatch(2);
        var late = new FakeClient(match, 8000);
        late.Hello();
        late.Read();
        Assert.Equal(RejectReason.MatchInProgress, late.Rejected);
    }
}
