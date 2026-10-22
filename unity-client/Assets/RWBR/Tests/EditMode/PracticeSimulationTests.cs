using NUnit.Framework;
using RWBR.Client.Practice;
using RWBR.Shared;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    public class PracticeSimulationTests
    {
        private static PracticeSimulation NewSim() => new PracticeSimulation(MapLayout.GreyboxBlock(100f));

        private static InputCommand Cmd(InputButtons buttons = InputButtons.None, float yaw = 0, float pitch = 0) =>
            new InputCommand { Buttons = buttons, Yaw = yaw, Pitch = pitch };

        private static int LootIndex(PracticeSimulation sim, LootType type)
        {
            for (int i = 0; i < sim.Map.Loot.Count; i++)
                if (sim.Map.Loot[i].Type == type) return i;
            return -1;
        }

        /// <summary>Park enemies far away and out of sight so a test controls the fight.</summary>
        private static void SidelineEnemies(PracticeSimulation sim, int except = -1)
        {
            for (int i = 0; i < sim.Enemies.Count; i++)
                if (i != except) sim.Enemies[i].Combat.Health = 0;
        }

        [Test]
        public void Starts_unarmed_with_five_enemies()
        {
            PracticeSimulation sim = NewSim();
            Assert.IsFalse(sim.Player.Combat.HasWeapon);
            Assert.AreEqual(5, sim.EnemiesAlive);
            Assert.AreEqual(PracticeOutcome.InProgress, sim.Outcome);
        }

        [Test]
        public void Rifle_is_picked_up_only_within_reach()
        {
            PracticeSimulation sim = NewSim();
            SidelineEnemies(sim);
            sim.Enemies[0].Combat.Health = 100; // keep the run in progress
            sim.Enemies[0].Motor = MotorState.StandingAt(new SVector3(-90, 0, -90));
            int rifle = LootIndex(sim, LootType.Rifle);
            SVector3 at = sim.Map.Loot[rifle].Position;

            sim.Player.Motor = MotorState.StandingAt(at + new SVector3(4, 0, 0));
            sim.Step(Cmd(InputButtons.Interact));
            Assert.IsFalse(sim.Player.Combat.HasWeapon);

            sim.Player.Motor = MotorState.StandingAt(at + new SVector3(1, 0, 0));
            sim.Step(Cmd(InputButtons.Interact));
            Assert.IsTrue(sim.Player.Combat.HasWeapon);
            Assert.IsTrue(sim.LootTaken[rifle]);
        }

        [Test]
        public void Killing_the_last_enemy_wins_the_run()
        {
            PracticeSimulation sim = NewSim();
            SidelineEnemies(sim, except: 0);
            Fighter enemy = sim.Enemies[0];
            enemy.Motor = MotorState.StandingAt(new SVector3(-60, 0, -60));
            sim.Player.Motor = MotorState.StandingAt(new SVector3(-60, 0, -70));
            sim.Player.Combat.Reset(sim.PlayerWeapon, withWeapon: true, reserveAmmo: 90);

            bool sawKill = false;
            for (int i = 0; i < 300 && sim.Outcome == PracticeOutcome.InProgress; i++)
            {
                sim.Step(Cmd(InputButtons.Fire, yaw: 0, pitch: -0.03f));
                foreach (SimEvent e in sim.Events) sawKill |= e.Type == SimEventType.Kill && e.Target == enemy.Id;
            }
            Assert.IsTrue(sawKill);
            Assert.AreEqual(PracticeOutcome.Victory, sim.Outcome);
        }

        [Test]
        public void Enemies_shoot_back_and_can_defeat_the_player()
        {
            PracticeSimulation sim = NewSim();
            SidelineEnemies(sim, except: 0);
            sim.Enemies[0].Motor = MotorState.StandingAt(new SVector3(-60, 0, -60));
            sim.Player.Motor = MotorState.StandingAt(new SVector3(-60, 0, -72));

            for (int i = 0; i < 3000 && sim.Outcome == PracticeOutcome.InProgress; i++) sim.Step(Cmd());
            Assert.AreEqual(PracticeOutcome.Defeat, sim.Outcome);
        }

        [Test]
        public void Enemies_hold_fire_without_line_of_sight()
        {
            PracticeSimulation sim = NewSim();
            SidelineEnemies(sim, except: 1);                           // enemy inside the building
            sim.Player.Motor = MotorState.StandingAt(new SVector3(0, 0, 10)); // north of the building, wall in between
            for (int i = 0; i < 150; i++) sim.Step(Cmd());
            Assert.AreEqual(GameRules.MaxHealth, sim.Player.Combat.Health);
        }

        [Test]
        public void Medkit_restores_health_after_three_seconds()
        {
            PracticeSimulation sim = NewSim();
            SidelineEnemies(sim, except: 0);
            sim.Enemies[0].Motor = MotorState.StandingAt(new SVector3(-90, 0, 90));
            sim.Player.Combat.Health = 40;
            sim.Player.Combat.Medkits = 1;

            sim.Step(Cmd(InputButtons.Heal));
            Assert.IsTrue(sim.Player.Combat.Healing);
            for (int i = 0; i < PracticeSimulation.TickRate * 3 + 1; i++) sim.Step(Cmd());
            Assert.AreEqual(90f, sim.Player.Combat.Health, 0.01f);
            Assert.AreEqual(0, sim.Player.Combat.Medkits);
        }
    }
}
