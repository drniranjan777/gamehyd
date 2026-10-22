using System;
using System.Collections.Generic;
using RWBR.Shared;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Client.Practice
{
    public enum PracticeOutcome
    {
        InProgress,
        Victory,
        Defeat,
    }

    public enum SimEventType
    {
        Shot,
        Hit,
        Kill,
        Pickup,
    }

    public struct SimEvent
    {
        public SimEventType Type;
        public ushort Actor;
        public ushort Target;
        public SVector3 From;
        public SVector3 To;
        public float Damage;
        public LootType Loot;
    }

    public sealed class Fighter
    {
        public Fighter(ushort id, string name, bool isPlayer)
        {
            Id = id;
            Name = name;
            IsPlayer = isPlayer;
        }

        public ushort Id { get; }
        public string Name { get; }
        public bool IsPlayer { get; }
        public MotorState Motor;
        public Combatant Combat { get; } = new Combatant();
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public SVector3 Chest => Motor.Position + new SVector3(0, Motor.Height * 0.65f, 0);
    }

    /// <summary>
    /// Offline single-player loop for the Phase 1 vertical slice: loot a rifle, clear the AI enemies.
    /// Engine-agnostic and built on the same shared rules the authoritative server runs (movement, combat,
    /// spread, pickups), so offline behaviour matches online behaviour.
    /// </summary>
    public sealed class PracticeSimulation
    {
        public const int TickRate = 30;
        public const float Dt = 1f / TickRate;
        public const ushort PlayerId = 1;

        private readonly List<EnemyBrain> _brains = new List<EnemyBrain>();

        public PracticeSimulation(MapLayout map)
        {
            Map = map;
            Player = new Fighter(PlayerId, "You", isPlayer: true);
            LootTaken = new bool[map.Loot.Count];
            Reset();
        }

        public MapLayout Map { get; }
        public WeaponDefinition PlayerWeapon { get; } = WeaponDefinition.DefaultRifle();
        public WeaponDefinition EnemyWeapon { get; } = new WeaponDefinition
        {
            Id = "rifle_enemy_practice",
            Damage = 9f,
            Range = 60f,
            FireIntervalSeconds = 0.3f,
            MagazineSize = 20,
            ReloadSeconds = 2.5f,
            SpreadDegrees = 2.5f,
        };

        public Fighter Player { get; }
        public List<Fighter> Enemies { get; } = new List<Fighter>();
        public bool[] LootTaken { get; }
        public long Tick { get; private set; }
        public PracticeOutcome Outcome { get; private set; }
        public List<SimEvent> Events { get; } = new List<SimEvent>();
        public int EnemiesAlive => Enemies.FindAll(e => e.Combat.Alive).Count;

        /// <summary>Player spawn: south of the block, facing north towards the building.</summary>
        public static SVector3 PlayerSpawn => new SVector3(0, 0, -25);

        public void Reset()
        {
            Tick = 0;
            Outcome = PracticeOutcome.InProgress;
            Events.Clear();
            Array.Clear(LootTaken, 0, LootTaken.Length);

            Player.Motor = MotorState.StandingAt(PlayerSpawn);
            Player.Yaw = 0;
            Player.Pitch = 0;
            Player.Combat.Reset(PlayerWeapon, withWeapon: false, reserveAmmo: 0);

            Enemies.Clear();
            _brains.Clear();
            SVector3[] homes =
            {
                new SVector3(0, MapLayout.RoofTop, 3),   // on the roof
                new SVector3(4, 0, -1),                  // inside the building
                new SVector3(28, 0, 30),                 // market
                new SVector3(-35, 0, 20),                // apartments
                new SVector3(15, 0, 18),                 // behind cover
            };
            for (int i = 0; i < homes.Length; i++)
            {
                var e = new Fighter((ushort)(PlayerId + 1 + i), $"Enemy {i + 1}", isPlayer: false);
                e.Motor = MotorState.StandingAt(homes[i]);
                e.Yaw = (float)Math.PI; // facing south, towards the player's approach
                e.Combat.Reset(EnemyWeapon, withWeapon: true, reserveAmmo: 9999);
                Enemies.Add(e);
                _brains.Add(new EnemyBrain(homes[i], phase: i * 1.7f));
            }
        }

        /// <summary>Advances one tick with the player's input. Events from this tick are in <see cref="Events"/>.</summary>
        public void Step(InputCommand playerInput)
        {
            Events.Clear();
            if (Outcome != PracticeOutcome.InProgress) return;
            Tick++;

            if (Player.Combat.Alive) Act(Player, playerInput, PlayerWeapon);
            for (int i = 0; i < Enemies.Count; i++)
            {
                Fighter e = Enemies[i];
                if (!e.Combat.Alive) continue;
                Act(e, _brains[i].Decide(e, Player, this), EnemyWeapon);
            }

            if (!Player.Combat.Alive) Outcome = PracticeOutcome.Defeat;
            else if (EnemiesAlive == 0) Outcome = PracticeOutcome.Victory;
        }

        private void Act(Fighter f, InputCommand cmd, WeaponDefinition weapon)
        {
            f.Yaw = Simulation.WrapYaw(cmd.Yaw);
            f.Pitch = Simulation.SanitizeAngle(cmd.Pitch, GameRules.MaxPitchRadians);
            Simulation.Step(ref f.Motor, cmd, Dt, Map);
            f.Combat.Update(Tick, weapon);

            if (cmd.Has(InputButtons.Interact)) TryPickUp(f);
            if (cmd.Has(InputButtons.Heal)) f.Combat.TryStartHeal(Tick, TickRate);
            if (cmd.Has(InputButtons.Reload)) f.Combat.TryStartReload(Tick, weapon, TickRate);
            if (cmd.Has(InputButtons.Fire) && f.Combat.TryFire(Tick, weapon, TickRate) == FireResult.Fired)
                Shoot(f, cmd, weapon);
        }

        /// <summary>The loot the fighter would pick up right now, or -1.</summary>
        public int NearestPickup(Fighter f)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Map.Loot.Count; i++)
            {
                LootSpawn item = Map.Loot[i];
                if (LootTaken[i] || !Simulation.CanReach(f.Motor, item.Position) || !f.Combat.CanPickUp(item)) continue;
                float d = SVector3.DistanceSquared(f.Motor.Position, item.Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        private void TryPickUp(Fighter f)
        {
            int i = NearestPickup(f);
            if (i < 0) return;
            LootTaken[i] = true;
            f.Combat.PickUp(Map.Loot[i], f.IsPlayer ? PlayerWeapon : EnemyWeapon);
            Events.Add(new SimEvent { Type = SimEventType.Pickup, Actor = f.Id, Loot = Map.Loot[i].Type, To = Map.Loot[i].Position });
        }

        public float CurrentSpread(Fighter f, InputCommand cmd, WeaponDefinition weapon)
        {
            bool moving = cmd.MoveX * cmd.MoveX + cmd.MoveZ * cmd.MoveZ > 0.01f;
            return weapon.SpreadFor(moving, f.Motor.Crouched, !f.Motor.Grounded);
        }

        private void Shoot(Fighter shooter, InputCommand cmd, WeaponDefinition weapon)
        {
            SVector3 origin = shooter.Motor.Eye;
            SVector3 dir = Simulation.SpreadDirection(shooter.Yaw, shooter.Pitch, CurrentSpread(shooter, cmd, weapon),
                Simulation.ShotSeed(shooter.Id, cmd.Sequence));
            float wall = Simulation.WallDistance(origin, dir, weapon.Range, Map);

            Fighter hit = null;
            float bestT = wall;
            foreach (Fighter target in AllFighters())
            {
                if (target == shooter || !target.Combat.Alive) continue;
                if (!shooter.IsPlayer && !target.IsPlayer) continue; // enemies do not shoot each other
                float? t = Simulation.RayCapsule(origin, dir, target.Motor.Position, target.Motor.Height, weapon.Range);
                if (t.HasValue && t.Value < bestT)
                {
                    bestT = t.Value;
                    hit = target;
                }
            }

            Events.Add(new SimEvent { Type = SimEventType.Shot, Actor = shooter.Id, From = origin, To = origin + dir * bestT });
            if (hit == null) return;

            float lost = hit.Combat.ApplyDamage(weapon.Damage);
            hit.Combat.CancelHeal();
            Events.Add(new SimEvent { Type = SimEventType.Hit, Actor = shooter.Id, Target = hit.Id, To = origin + dir * bestT, Damage = lost });
            if (!hit.Combat.Alive)
                Events.Add(new SimEvent { Type = SimEventType.Kill, Actor = shooter.Id, Target = hit.Id, To = hit.Motor.Position });
        }

        public IEnumerable<Fighter> AllFighters()
        {
            yield return Player;
            foreach (Fighter e in Enemies) yield return e;
        }

        public bool HasLineOfSight(SVector3 from, SVector3 to)
        {
            SVector3 delta = to - from;
            float dist = delta.Length();
            if (dist < 1e-3f) return true;
            return Simulation.WallDistance(from, delta / dist, dist, Map) >= dist - 0.01f;
        }
    }
}
