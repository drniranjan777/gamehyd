using System.Collections.Generic;
using NUnit.Framework;
using RWBR.Client.Online;
using RWBR.Shared;
using RWBR.Shared.Protocol;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    public class OnlineClientTests
    {
        private const int TickRate = 30;
        private const ushort SelfId = 7;
        private static readonly MapLayout Map = MapLayout.GreyboxBlock(100f);

        /// <summary>What the server would send after processing inputs up to <paramref name="ack"/>.</summary>
        private static SnapshotPacket ServerSnapshot(uint tick, uint ack, MotorState server)
        {
            var s = new SnapshotPacket
            {
                Tick = tick,
                AckSequence = ack,
                SelfVerticalVelocity = server.VerticalVelocity,
                SelfX = server.Position.X,
                SelfY = server.Position.Y,
                SelfZ = server.Position.Z,
            };
            var flags = PlayerFlags.Alive;
            if (server.Grounded) flags |= PlayerFlags.Grounded;
            if (server.Crouched) flags |= PlayerFlags.Crouched;
            s.Players.Add(Quantize.Normalize(new SnapshotEntry
            {
                PlayerId = SelfId,
                X = server.Position.X,
                Y = server.Position.Y,
                Z = server.Position.Z,
                Flags = flags,
            }));
            return s;
        }

        [Test]
        public void Prediction_matches_the_server_through_latency_jumps_and_stairs()
        {
            var prediction = new ClientPrediction(Map, TickRate);
            MotorState server = MotorState.StandingAt(new SVector3(8.75f, 0, -9)); // foot of the stairs
            prediction.Reconcile(ServerSnapshot(1, 0, server), SelfId);
            Assert.IsTrue(prediction.Initialized);

            const int latencyTicks = 4; // inputs reach the server 4 ticks after the client applied them
            var inFlight = new Queue<InputCommand>();
            float peakY = 0;
            for (uint i = 1; i <= 120; i++)
            {
                var cmd = new InputCommand { Sequence = i, MoveZ = 1, Buttons = i % 40 == 0 ? InputButtons.Jump : InputButtons.None };
                prediction.Apply(cmd);
                peakY = System.Math.Max(peakY, prediction.Predicted.Position.Y);
                inFlight.Enqueue(cmd);
                if (inFlight.Count > latencyTicks)
                {
                    InputCommand arrived = inFlight.Dequeue();
                    Simulation.Step(ref server, arrived, 1f / TickRate, Map);
                    prediction.Reconcile(ServerSnapshot(i + 1, arrived.Sequence, server), SelfId);
                }
            }

            Assert.Less(prediction.MaxError, 0.001f, "prediction drifted from the server");
            Assert.AreEqual(0, prediction.Corrections);
            Assert.Greater(peakY, 2f, "did not climb the stairs");
        }

        [Test]
        public void Server_correction_is_applied_but_blended_visually()
        {
            var prediction = new ClientPrediction(Map, TickRate);
            MotorState server = MotorState.StandingAt(new SVector3(-60, 0, -60));
            prediction.Reconcile(ServerSnapshot(1, 0, server), SelfId);
            for (uint i = 1; i <= 10; i++) prediction.Apply(new InputCommand { Sequence = i, MoveZ = 1 });
            SVector3 renderedBefore = prediction.RenderPosition;

            // The server says we did not move at all (e.g. inputs were rejected).
            prediction.Reconcile(ServerSnapshot(2, 10, server), SelfId);
            Assert.AreEqual(server.Position.Z, prediction.Predicted.Position.Z, 0.01f);
            Assert.AreEqual(renderedBefore.Z, prediction.RenderPosition.Z, 0.01f, "camera snapped instead of blending");
            Assert.AreEqual(1, prediction.Corrections);

            for (int f = 0; f < 60; f++) prediction.DecayOffset(1f / 60);
            Assert.AreEqual(server.Position.Z, prediction.RenderPosition.Z, 0.02f, "offset did not decay");
        }

        [Test]
        public void Interpolator_renders_behind_newest_and_blends_between_snapshots()
        {
            var interp = new RemoteInterpolator(delayTicks: 2);
            // One snapshot per tick of real time, as from a healthy connection.
            for (uint t = 10; t <= 40; t++)
            {
                var s = new SnapshotPacket { Tick = t };
                s.Players.Add(new SnapshotEntry { PlayerId = 3, X = t - 10, Flags = PlayerFlags.Alive });
                interp.Add(s);
                interp.Advance(1f / TickRate, TickRate);
            }
            Assert.IsTrue(interp.TrySample(3, out SVector3 p, out _, out _));
            Assert.That(p.X, Is.InRange(27.8f, 28.4f), "should render ~2 ticks behind the newest (tick 40 -> x 28)");

            interp.Advance(0.5f / TickRate, TickRate); // half a tick later: between snapshots 38 and 39
            interp.TrySample(3, out p, out _, out _);
            Assert.That(p.X, Is.InRange(28.3f, 28.9f));
            Assert.AreEqual(38u, interp.ViewedTick);
        }

        [Test]
        public void Server_addresses_are_validated()
        {
            Assert.IsTrue(OnlineGame.TryParseAddress("10.0.0.5:7777", out string host, out int port));
            Assert.AreEqual("10.0.0.5", host);
            Assert.AreEqual(7777, port);
            Assert.IsFalse(OnlineGame.TryParseAddress("localhost", out _, out _));
            Assert.IsFalse(OnlineGame.TryParseAddress("host:99999", out _, out _));
        }

        [Test]
        public void Other_players_reload_progress_is_timed_from_when_it_starts()
        {
            var t = new ReloadTracker();
            Assert.AreEqual(-1f, t.Progress(3, false, 2f, 10f));
            Assert.AreEqual(0f, t.Progress(3, true, 2f, 10f), 1e-5f, "starts when the flag is first seen");
            Assert.AreEqual(0.5f, t.Progress(3, true, 2f, 11f), 1e-5f);
            Assert.AreEqual(1f, t.Progress(3, true, 2f, 13f), 1e-5f, "held at the end, never loops");
            Assert.AreEqual(0.25f, t.Progress(4, true, 4f, 12f) + t.Progress(4, true, 4f, 13f), 1e-5f, "players are timed separately");
            Assert.AreEqual(-1f, t.Progress(3, false, 2f, 14f));
            Assert.AreEqual(0f, t.Progress(3, true, 2f, 20f), 1e-5f, "the next reload starts from zero");
        }
    }
}
