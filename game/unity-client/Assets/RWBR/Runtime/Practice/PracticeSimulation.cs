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
        Switch,
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
        public bool Headshot;
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
        /// <summary>Training range: where each target stands, whether it strafes, and when it went down.</summary>
        private readonly List<(SVector3 Home, bool Moving)> _targetsHome = new List<(SVector3, bool)>();
        private readonly List<long> _downSince = new List<long>();

        /// <summary>Seconds before a downed range target stands up again.</summary>
        public const float TargetRespawnSeconds = 2f;

        public PracticeSimulation(MapLayout map)
        {
            Map = map;
            Player = new Fighter(PlayerId, "You", isPlayer: true);
            LootTaken = new bool[map.Loot.Count];
            Reset();
        }

        public MapLayout Map { get; }
        /// <summary>The player's weapon in hand (catalog balance), or the rifle when unarmed (for HUD defaults).</summary>
        public WeaponDefinition PlayerWeapon => WeaponOf(Player);
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

        /// <summary>Player spawn on the training ground: south of the block, facing north towards the building.</summary>
        public static SVector3 PlayerSpawn => new SVector3(0, 0, -25);

        public bool IsCity => Map.Version == CityMap.Version;
        /// <summary>Training range: passive targets that stand up again, every weapon on the bench, endless ammo.</summary>
        public bool IsRange => Map.Version == MapLayout.TrainingRangeVersion;
        /// <summary>While true (tutorial), enemies hold fire.</summary>
        public bool EnemiesPassive { get; set; }

        public WeaponDefinition WeaponOf(Fighter f)
        {
            if (!f.IsPlayer) return EnemyWeapon;
            int i = f.Combat.ActiveWeaponIndex;
            return WeaponCatalog.Get(WeaponCatalog.IsValid(i) ? i : WeaponCatalog.Rifle);
        }

        public void Reset()
        {
            Tick = 0;
            Outcome = PracticeOutcome.InProgress;
            Events.Clear();
            Array.Clear(LootTaken, 0, LootTaken.Length);

            SVector3 spawn = IsCity || IsRange ? Map.Spawns[0] : PlayerSpawn;
            Player.Motor = MotorState.StandingAt(spawn);
            Player.Yaw = IsCity ? (float)Math.Atan2(-spawn.X, -spawn.Z) : 0; // city: face the centre
            Player.Pitch = 0;
            Player.Combat.Reset(WeaponCatalog.Get(WeaponCatalog.Rifle), withWeapon: IsRange, reserveAmmo: IsRange ? GameRules.MaxReserveAmmo : 0);

            Enemies.Clear();
            _brains.Clear();
            _targetsHome.Clear();
            _downSince.Clear();
            if (IsRange)
            {
                ResetRange();
                return;
            }
            SVector3[] homes = IsCity ? CityHomes() : new[]
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

        private void ResetRange()
        {
            for (int lane = 0; lane < MapLayout.RangeDistances.Length; lane++)
                _targetsHome.Add((new SVector3(MapLayout.RangeLaneX(lane), 0, MapLayout.RangeFiringLine + MapLayout.RangeDistances[lane]), false));
            _targetsHome.Add((new SVector3(-26, 0, MapLayout.RangeFiringLine + 28), true));
            _targetsHome.Add((new SVector3(26, 0, MapLayout.RangeFiringLine + 53), true));
            for (int i = 0; i < _targetsHome.Count; i++)
            {
                (SVector3 home, bool moving) = _targetsHome[i];
                float distance = home.Z - MapLayout.RangeFiringLine;
                var t = new Fighter((ushort)(PlayerId + 1 + i), moving ? $"Moving target {distance:0} m" : $"Target {distance:0} m", isPlayer: false);
                t.Motor = MotorState.StandingAt(home);
                t.Yaw = (float)Math.PI; // facing the firing line
                t.Combat.Reset(EnemyWeapon, withWeapon: false, reserveAmmo: 0);
                Enemies.Add(t);
                _downSince.Add(-1);
            }
        }

        /// <summary>Distance from the firing line to a range target (or from the player for other modes).</summary>
        public float TargetDistance(Fighter target) =>
            IsRange ? target.Motor.Position.Z - MapLayout.RangeFiringLine : SVector3.Distance(target.Motor.Position, Player.Motor.Position);

        private void StepRange()
        {
            Player.Combat.ReserveAmmo = GameRules.MaxReserveAmmo;
            long respawnTicks = (long)(TargetRespawnSeconds * TickRate);
            float t = Tick * Dt;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Fighter target = Enemies[i];
                (SVector3 home, bool moving) = _targetsHome[i];
                if (!target.Combat.Alive)
                {
                    if (_downSince[i] < 0) _downSince[i] = Tick;
                    if (Tick - _downSince[i] < respawnTicks) continue;
                    _downSince[i] = -1;
                    target.Motor = MotorState.StandingAt(home);
                    target.Combat.Reset(EnemyWeapon, withWeapon: false, reserveAmmo: 0);
                    continue;
                }
                // Strafe across the lane: 5 m/s peak, about 6 m either side of home (the integral of the sine).
                var cmd = new InputCommand { Yaw = (float)Math.PI, MoveX = moving ? (float)Math.Sin(t * 0.8f + i) : 0 };
                Simulation.Step(ref target.Motor, cmd, Dt, Map);
            }
        }

        /// <summary>City practice: 8 enemies near loot in every district (deterministic picks).</summary>
        private SVector3[] CityHomes()
        {
            var homes = new List<SVector3>();
            string[] order = { "Char Gate Plaza", "Old City Bazaar", "Hi-Tech Towers", "Residential Colony", "Lakeside" };
            for (int round = 0; homes.Count < 8 && round < 4; round++)
            {
                foreach (string district in order)
                {
                    int seen = 0;
                    foreach (LootSpawn l in Map.Loot)
                    {
                        if (Map.DistrictAt(l.Position.X, l.Position.Z) != district || l.Position.Y > 0.1f) continue;
                        if (seen++ != 7 + round * 11) continue;
                        homes.Add(l.Position);
                        break;
                    }
                    if (homes.Count >= 8) break;
                }
            }
            return homes.ToArray();
        }

        /// <summary>Advances one tick with the player's input. Events from this tick are in <see cref="Events"/>.</summary>
        public void Step(InputCommand playerInput)
        {
            Events.Clear();
            if (Outcome != PracticeOutcome.InProgress) return;
            Tick++;

            if (Player.Combat.Alive) Act(Player, playerInput);
            if (IsRange)
            {
                StepRange();
                return; // no win or loss on the range
            }
            for (int i = 0; i < Enemies.Count; i++)
            {
                Fighter e = Enemies[i];
                if (!e.Combat.Alive) continue;
                InputCommand c = _brains[i].Decide(e, Player, this);
                if (EnemiesPassive) c.Buttons &= ~InputButtons.Fire;
                Act(e, c);
            }

            if (!Player.Combat.Alive) Outcome = PracticeOutcome.Defeat;
            else if (EnemiesAlive == 0) Outcome = PracticeOutcome.Victory;
        }

        private void Act(Fighter f, InputCommand cmd)
        {
            f.Yaw = Simulation.WrapYaw(cmd.Yaw);
            f.Pitch = Simulation.SanitizeAngle(cmd.Pitch, GameRules.MaxPitchRadians);
            Simulation.Step(ref f.Motor, cmd, Dt, Map);
            f.Combat.Update(Tick, WeaponOf(f));

            if (cmd.Has(InputButtons.Interact)) TryPickUp(f);
            if (cmd.Has(InputButtons.SwitchWeapon) && f.IsPlayer)
            {
                int other = f.Combat.SlotWeapon(1 - f.Combat.ActiveSlot);
                if (other >= 0 && f.Combat.TrySwitch(Tick, WeaponCatalog.Get(other), TickRate))
                    Events.Add(new SimEvent { Type = SimEventType.Switch, Actor = f.Id });
            }
            WeaponDefinition weapon = WeaponOf(f);
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
            if (!(IsRange && Map.Loot[i].WeaponIndex >= 0)) LootTaken[i] = true; // the range's weapon rack never empties
            int w = Map.Loot[i].WeaponIndex;
            f.Combat.PickUp(Map.Loot[i], w >= 0 ? WeaponCatalog.Get(w) : WeaponOf(f));
            Events.Add(new SimEvent { Type = SimEventType.Pickup, Actor = f.Id, Loot = Map.Loot[i].Type, To = Map.Loot[i].Position });
        }

        public float CurrentSpread(Fighter f, InputCommand cmd, WeaponDefinition weapon)
        {
            bool moving = cmd.MoveX * cmd.MoveX + cmd.MoveZ * cmd.MoveZ > 0.01f;
            return weapon.SpreadFor(moving, f.Motor.Crouched, !f.Motor.Grounded, cmd.Has(InputButtons.Aim));
        }

        private readonly List<TargetCapsule> _targets = new List<TargetCapsule>();
        private readonly List<Fighter> _targetFighters = new List<Fighter>();
        private readonly List<PelletResult> _pellets = new List<PelletResult>();

        /// <summary>The same ballistics the server runs: pellets, walls, headshots, falloff.</summary>
        private void Shoot(Fighter shooter, InputCommand cmd, WeaponDefinition weapon)
        {
            _targets.Clear();
            _targetFighters.Clear();
            foreach (Fighter target in AllFighters())
            {
                if (target == shooter || !target.Combat.Alive) continue;
                if (!shooter.IsPlayer && !target.IsPlayer) continue; // enemies do not shoot each other
                _targets.Add(new TargetCapsule { Feet = target.Motor.Position, Height = target.Motor.Height });
                _targetFighters.Add(target);
            }

            SVector3 origin = shooter.Motor.Eye;
            Ballistics.Fire(origin, shooter.Yaw, shooter.Pitch, CurrentSpread(shooter, cmd, weapon),
                Simulation.ShotSeed(shooter.Id, cmd.Sequence), weapon, Map, _targets, _pellets);
            foreach (PelletResult p in _pellets)
            {
                Events.Add(new SimEvent { Type = SimEventType.Shot, Actor = shooter.Id, From = origin, To = p.End });
                if (p.Target < 0) continue;
                Fighter hit = _targetFighters[p.Target];
                if (!hit.Combat.Alive) continue;
                float lost = hit.Combat.ApplyDamage(p.Damage);
                hit.Combat.CancelHeal();
                Events.Add(new SimEvent { Type = SimEventType.Hit, Actor = shooter.Id, Target = hit.Id, From = origin, To = p.End, Damage = lost, Headshot = p.Headshot });
                if (!hit.Combat.Alive)
                    Events.Add(new SimEvent { Type = SimEventType.Kill, Actor = shooter.Id, Target = hit.Id, To = hit.Motor.Position, Headshot = p.Headshot });
            }
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
