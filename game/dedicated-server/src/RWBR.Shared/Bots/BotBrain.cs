#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Bots
{
    /// <summary>
    /// Bot behaviour from server snapshots, used by offline bot matches, server fill bots, the UDP bot client and the
    /// match simulator. With a map (<see cref="NavGrid"/>) bots navigate around buildings on the ground, go for weapons
    /// and ammo when they need them, fight enemies they can actually see after a reaction time, pick the better of their
    /// weapons for the range, throw frags at enemies who just ducked behind cover and smoke when hurt under fire, and
    /// roam inside the safe zone. How well they do it comes from <see cref="BotSkill"/>. Without a map they fall back to
    /// the original straight-line behaviour.
    /// </summary>
    public sealed class BotBrain
    {
        private const float RepathSeconds = 2f;
        private const float WaypointReached = 1.2f;
        private const float LootDetour = 60f;
        private static readonly float[] PartWay = { 0.6f, 0.35f, 0.15f };

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
        private readonly BotSkill _skill;
        // Reaction: whom we are looking at and since when.
        private ushort _seenId = ProtocolInfo.NoPlayer;
        private double _seenSince;
        // The last enemy we saw (for grenades when they duck out of sight).
        private Vector3 _lastSeenAt;
        private double _lastSeenTime = -100;
        private double _nextThrow, _nextSwitch;

        public BotBrain(int seed, MapLayout? map = null, BotSkill? skill = null)
        {
            _rng = new Random(seed);
            _map = map;
            _nav = map != null ? NavGrid.For(map) : null;
            _skill = skill ?? BotSkill.For(BotDifficulty.Normal);
        }

        public BotSkill Skill => _skill;

        /// <summary>The match's loot spots (its loot table); the map's default items when not set.</summary>
        public IReadOnlyList<LootSpawn>? Loot { get; set; }

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

            // Reaction time: a newly seen enemy is only shot at after a moment.
            if (!hasTarget) _seenId = ProtocolInfo.NoPlayer;
            else
            {
                if (target.PlayerId != _seenId)
                {
                    _seenId = target.PlayerId;
                    _seenSince = time;
                }
                _lastSeenAt = new Vector3(target.X, target.Y, target.Z);
                _lastSeenTime = time;
            }
            bool reacted = hasTarget && time - _seenSince >= _skill.ReactionSeconds;
            // Never further than the weapon in hand carries usefully.
            float engage = WeaponCatalog.IsValid(self.Weapon) ? Math.Min(_skill.EngageRange, WeaponCatalog.Get(self.Weapon).Range * 0.8f) : _skill.EngageRange;

            float yaw = self.Yaw, pitch = 0;
            if (hasTarget && canFight)
            {
                float dx = target.X - me.X, dz = target.Z - me.Z;
                float dy = (target.Y + 1.2f) - (me.Y + GameRules.EyeHeight);
                yaw = (float)Math.Atan2(dx, dz) + (float)(_rng.NextDouble() * 2 - 1) * _skill.AimError;
                pitch = (float)Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) + (float)(_rng.NextDouble() * 2 - 1) * _skill.AimError * 0.5f;
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
            else if (hasTarget && canFight && best < engage && !OutsideZone(snap, me, 0.85f))
            {
                // Fight. Up close: strafe, and close in a little. At range: aim down sights; skilled bots also stop
                // and crouch for the tighter spread, as players do.
                bool steady = _skill.ChoosesWeapons && best > 35;
                worldMove = steady ? Vector3.Zero
                    : Simulation.Right(yaw) * (float)Math.Sin(time * 2 + selfId) + (best > 15 ? Simulation.Forward(yaw) : Vector3.Zero);
                if (best > 20) buttons |= InputButtons.Aim;
                if (steady) buttons |= InputButtons.Crouch;
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
                if (!hasTarget && self.Health < _skill.HealBelow && (self.Flags & PlayerFlags.Healing) == 0 && HasHeal(snap)) buttons |= InputButtons.Heal;
                if (!hasTarget || !canFight) yaw = worldMove.LengthSquared() > 0.01f ? (float)Math.Atan2(worldMove.X, worldMove.Z) : yaw;
                Unstick(me, time, ref buttons);
            }

            if (reacted && canFight && best < engage) buttons |= InputButtons.Fire;
            if (armed && self.Ammo == 0 && snap.SelfReserveAmmo > 0) buttons |= InputButtons.Reload;
            if (_skill.ChoosesWeapons && hasTarget && ShouldSwitch(snap, self, best, time)) buttons |= InputButtons.SwitchWeapon;

            ItemKind item = ItemKind.None;
            if (_skill.UsesGrenades && time >= _nextThrow && (self.Flags & (PlayerFlags.Reloading | PlayerFlags.Healing)) == 0)
            {
                float hidden = Vector3.Distance(me, _lastSeenAt);
                if (!hasTarget && time - _lastSeenTime < 3 && hidden >= 7 && hidden <= 22 && snap.SelfCount(ItemKind.Frag) > 0)
                {
                    // They ducked behind cover: lob a frag where they were.
                    yaw = (float)Math.Atan2(_lastSeenAt.X - me.X, _lastSeenAt.Z - me.Z);
                    pitch = BotSkill.ThrowPitch(hidden);
                    item = ItemKind.Frag;
                }
                else if (hasTarget && self.Health < 40 && best < 40 && snap.SelfCount(ItemKind.Smoke) > 0)
                {
                    // Hurt and under fire: smoke between us, then the usual retreat to heal.
                    yaw = (float)Math.Atan2(target.X - me.X, target.Z - me.Z);
                    pitch = BotSkill.ThrowPitch(6);
                    item = ItemKind.Smoke;
                }
                if (item != ItemKind.None)
                {
                    buttons = (buttons & ~(InputButtons.Fire | InputButtons.SwitchWeapon)) | InputButtons.Throw;
                    _nextThrow = time + 6;
                }
            }
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
                Item = item,
            };
        }

        /// <summary>Whether the other slot's weapon suits this range clearly better (and has rounds), at most once a second.</summary>
        private bool ShouldSwitch(SnapshotPacket snap, SnapshotEntry self, float distance, double time)
        {
            if (time < _nextSwitch || (self.Flags & PlayerFlags.Reloading) != 0) return false;
            int active = snap.SelfActiveSlot, other = 1 - active;
            byte a = snap.SelfSlotWeapon[active], o = snap.SelfSlotWeapon[other];
            if (!WeaponCatalog.IsValid(o)) return false;
            bool otherLoaded = snap.SelfSlotAmmo[other] > 0;
            if (!WeaponCatalog.IsValid(a)) return otherLoaded;
            if (self.Ammo == 0 && snap.SelfReserveAmmo == 0) return otherLoaded; // the one in hand is dry
            float now = BotSkill.Suitability(WeaponCatalog.Get(a).Class, distance);
            float alt = BotSkill.Suitability(WeaponCatalog.Get(o).Class, distance);
            if (!otherLoaded || alt < now + 2) return false;
            _nextSwitch = time + 1;
            return true;
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

        /// <summary>
        /// The circle bots aim for: the next one once it is announced (it lies inside the current one, so heading
        /// there early is always safe), else the current one.
        /// </summary>
        private static (float X, float Z, float R) Target(SnapshotPacket snap) =>
            snap.NextZoneRadius > 0 && snap.NextZoneRadius < snap.ZoneRadius
                ? (snap.NextZoneX, snap.NextZoneZ, snap.NextZoneRadius)
                : (snap.ZoneX, snap.ZoneZ, snap.ZoneRadius);

        private static bool OutsideZone(SnapshotPacket snap, Vector3 p, float fraction)
        {
            var (x, z, r) = Target(snap);
            return new Vector2(p.X - x, p.Z - z).Length() > r * fraction;
        }

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
            var (zx, zz, zr) = Target(snap);
            var zone = new Vector3(zx, 0, zz);
            if (OutsideZone(snap, me, 0.8f))
            {
                // Run toward the zone centre (aim at a point well inside the ring).
                Vector3 dir = Vector3.Normalize(zone - new Vector3(me.X, 0, me.Z));
                goal = zone - dir * zr * 0.3f;
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
                    double angle = _rng.NextDouble() * Math.PI * 2, r = Math.Sqrt(_rng.NextDouble()) * zr * 0.6;
                    _roamTarget = new Vector3(zx + (float)(Math.Sin(angle) * r), 0, zz + (float)(Math.Cos(angle) * r));
                    _roamUntil = time + 20 + _rng.NextDouble() * 20;
                }
                goal = _roamTarget;
            }
            _goal = goal;
            _path = _nav!.FindPath(me, goal);
            // The goal is in water or a building, or too far for one search: head part of the way there instead.
            for (int i = 0; _path == null && _goalLoot < 0 && _goalGround < 0 && i < PartWay.Length; i++)
            {
                Vector3 mid = Vector3.Lerp(me, goal, PartWay[i]);
                mid.Y = 0;
                _path = _nav.FindPath(me, mid);
                if (_path != null) _goal = mid;
            }
            _pathIndex = 0;
            if (_path == null) _roamUntil = 0; // unreachable: pick another target next time
        }

        private bool OutsideZoneTarget(SnapshotPacket snap) => OutsideZone(snap, _roamTarget, 0.8f);

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
            IReadOnlyList<LootSpawn> loot = Loot ?? _map!.Loot;
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
