#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Bots
{
    /// <summary>
    /// Test-player behaviour from server snapshots, used by the UDP bot client and the in-process match simulator.
    /// With a map (<see cref="NavGrid"/>) bots navigate around buildings on the ground, go for weapons and ammo when
    /// they need them, fight enemies they can actually see, and roam inside the safe zone. Without a map they fall back
    /// to the original straight-line behaviour.
    /// </summary>
    public sealed class BotBrain
    {
        private const float RepathSeconds = 2f;
        private const float WaypointReached = 1.2f;
        private const float EngageRange = 45f;
        private const float LootDetour = 60f;

        private readonly Random _rng;
        private readonly MapLayout? _map;
        private readonly NavGrid? _nav;

        private Vector3? _goal;
        private int _goalLoot = -1;
        /// <summary>Ground item (dropped or left by an eliminated player) the bot is going for, or -1.</summary>
        private int _goalGround = -1;
        private List<Vector3>? _path;
        private int _pathIndex;
        private double _lastPlan = -100, _lastProgress;
        private Vector3 _progressFrom;
        private Vector3 _roamTarget;
        private double _roamUntil;
        private double _goalSince;

        public BotBrain(int seed, MapLayout? map = null)
        {
            _rng = new Random(seed);
            _map = map;
            _nav = map != null ? NavGrid.For(map) : null;
        }

        public InputCommand Decide(SnapshotPacket? snap, ushort selfId, double time)
        {
            if (snap == null) return default;
            SnapshotEntry self = default;
            bool found = false;
            foreach (SnapshotEntry e in snap.Players)
            {
                if (e.PlayerId != selfId) continue;
                self = e;
                found = true;
            }
            if (!found || (self.Flags & PlayerFlags.Alive) == 0) return default;

            var me = new Vector3(self.X, self.Y, self.Z);
            var eye = me + new Vector3(0, GameRules.EyeHeight, 0);
            SnapshotEntry target = default;
            bool hasTarget = false;
            float best = float.MaxValue;
            foreach (SnapshotEntry p in snap.Players)
            {
                if (p.PlayerId == selfId || (p.Flags & PlayerFlags.Alive) == 0) continue;
                var pos = new Vector3(p.X, p.Y, p.Z);
                float d = Vector3.Distance(me, pos);
                if (d >= best) continue;
                if (_map != null && d > 2f)
                {
                    Vector3 to = pos + new Vector3(0, 1.2f, 0) - eye;
                    if (Simulation.WallDistance(eye, Vector3.Normalize(to), to.Length(), _map) < to.Length() - 0.3f) continue; // behind cover
                    if (ThroughSmoke(snap, eye, pos + new Vector3(0, 1.2f, 0))) continue; // hidden by smoke
                }
                best = d;
                target = p;
                hasTarget = true;
            }

            bool armed = self.Weapon != WeaponCatalog.None || snap.SelfSlotWeapon[0] != WeaponCatalog.None || snap.SelfSlotWeapon[1] != WeaponCatalog.None;
            bool outOfAmmo = armed && self.Ammo == 0 && snap.SelfReserveAmmo == 0;
            bool canFight = armed && !outOfAmmo;

            float yaw = self.Yaw, pitch = 0;
            if (hasTarget && canFight)
            {
                float dx = target.X - me.X, dz = target.Z - me.Z;
                float dy = (target.Y + 1.2f) - (me.Y + GameRules.EyeHeight);
                yaw = (float)Math.Atan2(dx, dz) + (float)(_rng.NextDouble() - 0.5) * 0.04f;
                pitch = (float)Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz));
            }

            Vector3 worldMove;
            var buttons = InputButtons.None;
            ushort pickUp = 0;
            if (_nav == null)
            {
                // Original behaviour: head for the zone centre when near the edge; otherwise close in and strafe.
                float fromCenter = new Vector2(me.X - snap.ZoneX, me.Z - snap.ZoneZ).Length();
                worldMove = fromCenter > snap.ZoneRadius * 0.7f
                    ? Vector3.Normalize(new Vector3(snap.ZoneX - me.X, 0, snap.ZoneZ - me.Z))
                    : (hasTarget && best > 12 ? Simulation.Forward(yaw) : Vector3.Zero) + Simulation.Right(yaw) * (float)Math.Sin(time * 2 + selfId);
            }
            else if (hasTarget && canFight && best < EngageRange && !OutsideZone(snap, me, 0.85f))
            {
                // Fight: strafe, and close in when far (the target is visible, so the line is mostly open).
                worldMove = Simulation.Right(yaw) * (float)Math.Sin(time * 2 + selfId) + (best > 15 ? Simulation.Forward(yaw) : Vector3.Zero);
                _path = null;
            }
            else
            {
                PlanIfNeeded(snap, self, me, time, armed, outOfAmmo);
                worldMove = FollowPath(me);
                if ((_goalLoot >= 0 || _goalGround >= 0) && Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(_goal!.Value.X, _goal.Value.Z)) < GameRules.PickupRange * 0.8f)
                {
                    buttons |= InputButtons.Interact;
                    pickUp = PickUpTarget();
                    if (_goalLoot >= 0 ? snap.IsLootTaken(_goalLoot) : !GroundHas(snap, _goalGround)) ClearGoal();
                    else if (time - _goalSince > 3) ClearGoal(); // no room for it: look for something else
                }
                // Heal when nobody is in sight.
                if (!hasTarget && self.Health < 70 && (self.Flags & PlayerFlags.Healing) == 0 && HasHeal(snap)) buttons |= InputButtons.Heal;
                if (!hasTarget || !canFight) yaw = worldMove.LengthSquared() > 0.01f ? (float)Math.Atan2(worldMove.X, worldMove.Z) : yaw;
                Unstick(me, time, ref buttons);
            }

            if (hasTarget && canFight && best < EngageRange) buttons |= InputButtons.Fire;
            if (armed && self.Ammo == 0 && snap.SelfReserveAmmo > 0) buttons |= InputButtons.Reload;
            float mz = Vector3.Dot(worldMove, Simulation.Forward(yaw));
            if (mz > 0.7f && (buttons & InputButtons.Fire) == 0) buttons |= InputButtons.Sprint;

            return new InputCommand
            {
                MoveX = Vector3.Dot(worldMove, Simulation.Right(yaw)),
                MoveZ = mz,
                Yaw = yaw,
                Pitch = pitch,
                Buttons = buttons,
                Target = pickUp,
            };
        }

        private ushort PickUpTarget() =>
            InputCommand.PickUpTarget((ushort)(_goalGround >= 0 ? _goalGround : _goalLoot));

        private static bool GroundHas(SnapshotPacket snap, int id)
        {
            foreach (GroundItemEntry g in snap.Ground)
                if (g.Id == id) return true;
            return false;
        }

        private static bool HasHeal(SnapshotPacket snap) =>
            snap.SelfCount(ItemKind.Bandage) > 0 || snap.SelfCount(ItemKind.Medkit) > 0 || snap.SelfCount(ItemKind.EnergyDrink) > 0;

        private static bool ThroughSmoke(SnapshotPacket snap, Vector3 from, Vector3 to)
        {
            foreach (ProjectileEntry p in snap.Projectiles)
                if (p.State == ProjectileState.Cloud &&
                    Server.Match.SegmentNearPoint(from, to, new Vector3(p.X, p.Y + 1.5f, p.Z), Server.Match.SmokeRadius)) return true;
            return false;
        }

        private static bool OutsideZone(SnapshotPacket snap, Vector3 p, float fraction) =>
            new Vector2(p.X - snap.ZoneX, p.Z - snap.ZoneZ).Length() > snap.ZoneRadius * fraction;

        /// <summary>Chooses a goal (zone, loot, roam) and a path to it, at most every couple of seconds.</summary>
        private void PlanIfNeeded(SnapshotPacket snap, SnapshotEntry self, Vector3 me, double time, bool armed, bool outOfAmmo)
        {
            bool goalGone = (_goalLoot >= 0 && snap.IsLootTaken(_goalLoot)) || (_goalGround >= 0 && !GroundHas(snap, _goalGround));
            if (_path != null && !goalGone && time - _lastPlan < RepathSeconds && _pathIndex < _path.Count) return;
            _lastPlan = time;

            Vector3 goal;
            _goalLoot = -1;
            _goalGround = -1;
            _goalSince = time;
            var zone = new Vector3(snap.ZoneX, 0, snap.ZoneZ);
            if (OutsideZone(snap, me, 0.8f))
            {
                // Run toward the zone centre (aim at a point well inside the ring).
                Vector3 dir = Vector3.Normalize(zone - new Vector3(me.X, 0, me.Z));
                goal = zone - dir * snap.ZoneRadius * 0.3f;
            }
            else if (FindLoot(snap, self, me, !armed ? LootNeed.Weapon : outOfAmmo ? LootNeed.Ammo : LootNeed.Any, out int loot, out int ground, out Vector3 at))
            {
                _goalLoot = loot;
                _goalGround = ground;
                goal = at;
            }
            else
            {
                if (time > _roamUntil || OutsideZoneTarget(snap))
                {
                    double angle = _rng.NextDouble() * Math.PI * 2, r = Math.Sqrt(_rng.NextDouble()) * snap.ZoneRadius * 0.6;
                    _roamTarget = new Vector3(snap.ZoneX + (float)(Math.Sin(angle) * r), 0, snap.ZoneZ + (float)(Math.Cos(angle) * r));
                    _roamUntil = time + 20 + _rng.NextDouble() * 20;
                }
                goal = _roamTarget;
            }
            _goal = goal;
            _path = _nav!.FindPath(me, goal);
            _pathIndex = 0;
            if (_path == null) _roamUntil = 0; // unreachable: pick another target next time
        }

        private bool OutsideZoneTarget(SnapshotPacket snap) =>
            new Vector2(_roamTarget.X - snap.ZoneX, _roamTarget.Z - snap.ZoneZ).Length() > snap.ZoneRadius * 0.8f;

        private enum LootNeed
        {
            Weapon,
            Ammo,
            Any,
        }

        /// <summary>
        /// Nearest untaken ground-level map loot or ground item of the needed kind, inside the zone and within a detour
        /// radius: a weapon when unarmed, ammo for the weapon in hand when out, otherwise anything useful (ammo for the
        /// weapon in hand, heals, grenades, attachments, and armour or a backpack better than the one worn).
        /// </summary>
        private bool FindLoot(SnapshotPacket snap, SnapshotEntry self, Vector3 me, LootNeed need, out int index, out int ground, out Vector3 at)
        {
            index = -1;
            ground = -1;
            at = default;
            float best = need == LootNeed.Any ? 25f : LootDetour;
            ItemKind ammo = WeaponCatalog.IsValid(self.Weapon) ? Items.AmmoFor(WeaponCatalog.Get(self.Weapon).Class) : ItemKind.None;
            IReadOnlyList<LootSpawn> loot = _map!.Loot;
            for (int i = 0; i < loot.Count; i++)
            {
                LootSpawn l = loot[i];
                if (!Wanted(snap, need, ammo, l.Kind) || l.Position.Y > 1f || snap.IsLootTaken(i) || OutsideZone(snap, l.Position, 0.9f)) continue;
                float d = Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(l.Position.X, l.Position.Z));
                if (d < best && d < Detour(need, l.Kind) && _nav!.IsWalkable(l.Position))
                {
                    best = d;
                    index = i;
                    at = l.Position;
                }
            }
            foreach (GroundItemEntry g in snap.Ground)
            {
                var p = new Vector3(g.X, g.Y, g.Z);
                if (!Wanted(snap, need, ammo, g.Kind) || p.Y > 1f || OutsideZone(snap, p, 0.9f)) continue;
                float d = Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(p.X, p.Z));
                if (d < best && d < Detour(need, g.Kind) && _nav!.IsWalkable(p))
                {
                    best = d;
                    index = -1;
                    ground = g.Id;
                    at = p;
                }
            }
            return index >= 0 || ground >= 0;
        }

        /// <summary>Grenades and attachments are only worth a short detour; what keeps a bot fighting is worth more.</summary>
        private static float Detour(LootNeed need, ItemKind kind)
        {
            ItemCategory c = Items.Category(kind);
            return need == LootNeed.Any && (c == ItemCategory.Throwable || c == ItemCategory.Attachment) ? 10f : float.MaxValue;
        }

        private static bool Wanted(SnapshotPacket snap, LootNeed need, ItemKind ammo, ItemKind kind)
        {
            if (need == LootNeed.Weapon) return kind == ItemKind.Weapon;
            if (need == LootNeed.Ammo) return kind == ammo;
            switch (Items.Category(kind))
            {
                case ItemCategory.Ammo: return kind == ammo;
                case ItemCategory.Heal:
                case ItemCategory.Throwable:
                case ItemCategory.Attachment: return true;
                case ItemCategory.Vest: return Items.Level(kind) > snap.SelfVest;
                case ItemCategory.Helmet: return Items.Level(kind) > snap.SelfHelmet;
                case ItemCategory.Backpack: return Items.Level(kind) > snap.SelfBackpack;
                default: return false;
            }
        }

        private Vector3 FollowPath(Vector3 me)
        {
            if (_path == null || _pathIndex >= _path.Count) return Vector3.Zero;
            Vector3 wp = _path[_pathIndex];
            var flat = new Vector3(wp.X - me.X, 0, wp.Z - me.Z);
            if (flat.Length() < WaypointReached && _pathIndex < _path.Count - 1)
            {
                _pathIndex++;
                wp = _path[_pathIndex];
                flat = new Vector3(wp.X - me.X, 0, wp.Z - me.Z);
            }
            return flat.LengthSquared() > 0.04f ? Vector3.Normalize(flat) : Vector3.Zero;
        }

        /// <summary>If the bot hasn't moved a metre in 2 s while trying to, jump and re-plan.</summary>
        private void Unstick(Vector3 me, double time, ref InputButtons buttons)
        {
            if (Vector3.Distance(me, _progressFrom) > 1f)
            {
                _progressFrom = me;
                _lastProgress = time;
                return;
            }
            if (time - _lastProgress > 2 && _path != null && _pathIndex < _path.Count)
            {
                buttons |= InputButtons.Jump;
                _path = null;
                _roamUntil = 0;
                _lastProgress = time;
            }
        }

        private void ClearGoal()
        {
            _goal = null;
            _goalLoot = -1;
            _goalGround = -1;
            _path = null;
        }
    }
}
