#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using RWBR.Shared.Protocol;

namespace RWBR.Shared.Server
{
    /// <summary>
    /// Items in the match: ground items (dropped, replaced or left by eliminated players), pickups from map loot spots
    /// and the ground, drops, and grenades. All server-authoritative; clients only see the results in snapshots.
    /// </summary>
    public sealed partial class Match
    {
        public const float GrenadeSpeed = 16f;
        public const float GrenadeLift = 3f;
        public const float FragFuseSeconds = 3.5f;
        public const float FragRadius = 6f;
        public const float FragDamage = 110f;
        public const float SmokeDelaySeconds = 1.5f;
        public const float SmokeSeconds = 14f;
        public const float SmokeRadius = 5.5f;
        /// <summary>Ground items within this distance are sent to a player.</summary>
        public const float GroundItemRange = 40f;
        public const float ProjectileRange = 150f;
        /// <summary>Oldest ground items disappear beyond this many.</summary>
        public const int MaxGroundItems = 256;
        private const float ExplosionShowSeconds = 0.5f;

        private sealed class GroundItem
        {
            public ushort Id;
            public ItemStack Stack;
            public Vector3 Position;
        }

        private sealed class Projectile
        {
            public ushort Id;
            public ItemKind Kind;
            public PlayerState? Owner;
            public Vector3 Position;
            public Vector3 Velocity;
            public long TriggerTick;
            public long EndTick;
            public ProjectileState State;
        }

        private readonly List<GroundItem> _ground = new();
        private readonly List<Projectile> _projectiles = new();
        private ushort _nextGroundId = ProtocolInfo.FirstGroundItemId;
        private ushort _nextProjectileId = 1;
        private readonly List<GroundItemEntry> _groundScratch = new();
        private readonly List<ProjectileEntry> _projectileScratch = new();
        private readonly List<ItemCount> _itemsScratch = new();

        public int GroundItemCount => _ground.Count;

        /// <summary>Ground items as (id, item, position) for tests and diagnostics.</summary>
        public IEnumerable<(ushort Id, ItemStack Stack, Vector3 Position)> GroundItems()
        {
            foreach (GroundItem g in _ground) yield return (g.Id, g.Stack, g.Position);
        }

        /// <summary>Grenades and clouds as (kind, state, position) for tests and diagnostics.</summary>
        public IEnumerable<(ItemKind Kind, ProjectileState State, Vector3 Position)> Projectiles()
        {
            foreach (Projectile p in _projectiles) yield return (p.Kind, p.State, p.Position);
        }

        private void ClearItems()
        {
            _ground.Clear();
            _projectiles.Clear();
        }

        /// <summary>Puts whatever the player's inventory had to let go of on the ground around them.</summary>
        private void PlaceDrops(PlayerState p)
        {
            List<ItemStack> dropped = p.Combat.Dropped;
            for (int i = 0; i < dropped.Count; i++)
            {
                // Spread piles on a small ring so they can be told apart and picked one by one.
                double a = (i * 2.399963 + p.Id) % (Math.PI * 2);
                float r = dropped.Count == 1 ? 0.5f : 0.6f + 0.25f * (i / 6);
                var at = new Vector3(p.Position.X + (float)Math.Sin(a) * r, p.Position.Y, p.Position.Z + (float)Math.Cos(a) * r);
                SpawnGround(dropped[i], Simulation.ResolveHorizontal(at, at.Y, 0.3f, _cfg.Map));
            }
            dropped.Clear();
        }

        private void SpawnGround(ItemStack stack, Vector3 at)
        {
            if (stack.Kind == ItemKind.None || stack.Count <= 0 && stack.Kind != ItemKind.Weapon) return;
            if (_ground.Count >= MaxGroundItems) _ground.RemoveAt(0);
            float y = Simulation.GroundHeight(at, at.Y + 0.5f, _cfg.Map);
            _ground.Add(new GroundItem { Id = _nextGroundId, Stack = stack, Position = new Vector3(at.X, y, at.Z) });
            _nextGroundId = _nextGroundId >= ushort.MaxValue - 2 ? ProtocolInfo.FirstGroundItemId : (ushort)(_nextGroundId + 1);
        }

        /// <summary>
        /// Server-validated pickup: the loot spot or ground item named by <paramref name="target"/> (Target - 1), or the
        /// nearest one in reach that would be useful. Map spots hand over a fresh weapon with a full magazine; anything
        /// that doesn't fit in the backpack stays on the ground.
        /// </summary>
        private void TryPickUp(PlayerState p, ushort target)
        {
            int wantId = target == 0 ? -1 : target - 1;
            IReadOnlyList<LootSpawn> loot = Loot;
            int bestSpot = -1;
            GroundItem? bestGround = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < loot.Count; i++)
            {
                if (wantId >= 0 && i != wantId) continue;
                if (_lootTaken[i] || !Simulation.CanReach(p.Motor, loot[i].Position) || p.Combat.CanTake(loot[i].Kind, loot[i].Count) <= 0) continue;
                float d = Vector3.DistanceSquared(p.Position, loot[i].Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestSpot = i;
                }
            }
            foreach (GroundItem g in _ground)
            {
                if (wantId >= 0 && g.Id != wantId) continue;
                if (!Simulation.CanReach(p.Motor, g.Position) || p.Combat.CanTake(g.Stack.Kind, g.Stack.Count) <= 0) continue;
                float d = Vector3.DistanceSquared(p.Position, g.Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestGround = g;
                    bestSpot = -1;
                }
            }

            if (bestGround != null)
            {
                int taken = p.Combat.Take(bestGround.Stack);
                if (Items.Stored(bestGround.Stack.Kind) && taken < bestGround.Stack.Count) bestGround.Stack.Count -= taken;
                else _ground.Remove(bestGround);
            }
            else if (bestSpot >= 0)
            {
                LootSpawn spot = loot[bestSpot];
                int taken = p.Combat.Take(new ItemStack(spot.Kind, spot.Count, -1));
                _lootTaken[bestSpot] = true;
                if (Items.Stored(spot.Kind) && taken < spot.Count) SpawnGround(new ItemStack(spot.Kind, spot.Count - taken), spot.Position);
            }
            else
            {
                Stats.PickupsRejected++;
                return;
            }
            Stats.Pickups++;
            PlaceDrops(p); // replaced weapon or armour
        }

        private void Throw(PlayerState p, ItemKind kind)
        {
            if (!p.Combat.TryThrow(Tick, kind, _cfg.TickRate)) return;
            Vector3 dir = Simulation.AimDirection(p.Yaw, p.Pitch);
            Vector3 start = p.Motor.Eye + new Vector3(dir.X, 0, dir.Z) * 0.4f;
            _projectiles.Add(new Projectile
            {
                Id = _nextProjectileId++,
                Kind = kind,
                Owner = p,
                Position = start,
                Velocity = dir * GrenadeSpeed + new Vector3(0, GrenadeLift, 0),
                TriggerTick = Tick + _cfg.Ticks(kind == ItemKind.Frag ? FragFuseSeconds : SmokeDelaySeconds),
                State = ProjectileState.Flying,
            });
            if (_nextProjectileId == 0) _nextProjectileId = 1;
        }

        /// <summary>Moves grenades (gravity, bounces off walls and floors), sets off frags and starts and ends smoke.</summary>
        private void StepProjectiles()
        {
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                Projectile pr = _projectiles[i];
                if (pr.State != ProjectileState.Flying)
                {
                    if (Tick >= pr.EndTick) _projectiles.RemoveAt(i);
                    continue;
                }
                Move(pr);
                if (Tick < pr.TriggerTick) continue;
                if (pr.Kind == ItemKind.Frag)
                {
                    Explode(pr);
                    pr.State = ProjectileState.Exploded;
                    pr.EndTick = Tick + _cfg.Ticks(ExplosionShowSeconds);
                }
                else
                {
                    pr.State = ProjectileState.Cloud;
                    pr.EndTick = Tick + _cfg.Ticks(SmokeSeconds);
                }
            }
        }

        private void Move(Projectile pr)
        {
            pr.Velocity.Y -= GameRules.Gravity * _dt;
            Vector3 from = pr.Position;
            Vector3 next = from + pr.Velocity * _dt;
            var flat = new Vector3(next.X - from.X, 0, next.Z - from.Z);
            float len = flat.Length();
            if (len > 1e-4f)
            {
                Vector3 dir = flat / len;
                float wall = Simulation.WallDistance(from, dir, len + 0.1f, _cfg.Map);
                if (wall < len + 0.1f)
                {
                    float keep = Math.Max(0, wall - 0.1f);
                    next.X = from.X + dir.X * keep;
                    next.Z = from.Z + dir.Z * keep;
                    pr.Velocity.X *= -0.35f; // bounce back off the wall
                    pr.Velocity.Z *= -0.35f;
                }
            }
            float limit = _cfg.Map.HalfSize - 0.5f;
            next.X = Math.Clamp(next.X, -limit, limit);
            next.Z = Math.Clamp(next.Z, -limit, limit);
            float ground = Simulation.GroundHeight(new Vector3(next.X, from.Y, next.Z), from.Y + 0.05f, _cfg.Map);
            if (next.Y <= ground)
            {
                next.Y = ground;
                if (pr.Velocity.Y < 0) pr.Velocity.Y = -pr.Velocity.Y * 0.3f;
                pr.Velocity.X *= 0.6f;
                pr.Velocity.Z *= 0.6f;
                if (pr.Velocity.Length() < 0.6f) pr.Velocity = Vector3.Zero;
            }
            pr.Position = next;
        }

        /// <summary>Blast damage falls off linearly to zero at <see cref="FragRadius"/>; walls between block it.</summary>
        private void Explode(Projectile pr)
        {
            Vector3 blast = pr.Position + new Vector3(0, 0.3f, 0);
            DamageVehiclesFromBlast(blast, pr.Owner);
            foreach (PlayerState victim in _players)
            {
                if (!victim.Alive) continue;
                Vector3 chest = victim.Position + new Vector3(0, victim.Motor.Height * 0.55f, 0);
                Vector3 to = chest - blast;
                float d = to.Length();
                if (d > FragRadius) continue;
                if (d > 0.3f && Simulation.WallDistance(blast, to / d, d, _cfg.Map) < d - 0.3f) continue; // behind cover
                float damage = FragDamage * (1f - d / FragRadius);
                Stats.Hits++;
                victim.Combat.ApplyDamage(damage);
                victim.Combat.CancelHeal();
                if (!victim.Combat.Alive) Eliminate(victim, pr.Owner == victim ? null : pr.Owner);
            }
        }

        /// <summary>Whether the line from <paramref name="a"/> to <paramref name="b"/> passes through a smoke cloud.</summary>
        public bool ThroughSmoke(Vector3 a, Vector3 b)
        {
            foreach (Projectile pr in _projectiles)
                if (pr.State == ProjectileState.Cloud && SegmentNearPoint(a, b, pr.Position + new Vector3(0, 1.5f, 0), SmokeRadius)) return true;
            return false;
        }

        public static bool SegmentNearPoint(Vector3 a, Vector3 b, Vector3 c, float radius)
        {
            Vector3 ab = b - a;
            float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(c - a, ab) / ab.LengthSquared(), 0, 1);
            return Vector3.DistanceSquared(a + ab * t, c) <= radius * radius;
        }

        /// <summary>Fills the recipient-only parts of a snapshot: backpack, equipment, nearby ground items and grenades.</summary>
        private bool FillSelf(SnapshotPacket snapshot, PlayerState p, SnapshotPacket? baseline)
        {
            Combatant c = p.Combat;
            snapshot.SelfVest = (byte)c.VestLevel;
            snapshot.SelfHelmet = (byte)c.HelmetLevel;
            snapshot.SelfHelmetPoints = (byte)Math.Clamp(Math.Ceiling(c.HelmetPoints), 0, 255);
            snapshot.SelfBackpack = (byte)c.BackpackLevel;
            snapshot.SelfHealKind = c.Healing ? c.HealKind : ItemKind.None;
            for (int slot = 0; slot < Combatant.SlotCount; slot++) snapshot.SelfSlotAttach[slot] = (byte)c.SlotAttachments(slot);
            _itemsScratch.Clear();
            for (int k = 0; k < Items.KindCount; k++)
            {
                var kind = (ItemKind)k;
                int n = c.Count(kind);
                if (n > 0 && Items.Stored(kind)) _itemsScratch.Add(new ItemCount { Kind = kind, Count = (ushort)Math.Min(n, ushort.MaxValue) });
            }
            snapshot.SelfItems = _itemsScratch;

            _groundScratch.Clear();
            foreach (GroundItem g in _ground)
            {
                if (Vector3.DistanceSquared(g.Position, p.Position) > GroundItemRange * GroundItemRange) continue;
                _groundScratch.Add(new GroundItemEntry
                {
                    Id = g.Id, Kind = g.Stack.Kind, Count = (ushort)Math.Clamp(g.Stack.Count, 0, ushort.MaxValue),
                    Ammo = (byte)Math.Clamp(g.Stack.Ammo, 0, 255), Attach = g.Stack.Attach,
                    X = SnapshotCodec.RoundDecimetres(g.Position.X), Y = SnapshotCodec.RoundDecimetres(g.Position.Y), Z = SnapshotCodec.RoundDecimetres(g.Position.Z),
                });
            }
            Vector3 me = p.Position;
            _groundScratch.Sort((x, y) => DistanceSquared(x.X, x.Z, me).CompareTo(DistanceSquared(y.X, y.Z, me)));
            if (_groundScratch.Count > SnapshotCodec.MaxGroundItems) _groundScratch.RemoveRange(SnapshotCodec.MaxGroundItems, _groundScratch.Count - SnapshotCodec.MaxGroundItems);
            snapshot.Ground = _groundScratch;

            _projectileScratch.Clear();
            foreach (Projectile pr in _projectiles)
            {
                if (Vector3.DistanceSquared(pr.Position, me) > ProjectileRange * ProjectileRange) continue;
                if (_projectileScratch.Count >= SnapshotCodec.MaxProjectiles) break;
                _projectileScratch.Add(new ProjectileEntry { Id = pr.Id, Kind = pr.Kind, State = pr.State, X = pr.Position.X, Y = pr.Position.Y, Z = pr.Position.Z });
            }
            snapshot.Projectiles = _projectileScratch;

            // Ground items are resent only when this player's list differs from the one in its baseline.
            ulong hash = 1469598103934665603UL;
            foreach (GroundItemEntry g in _groundScratch)
                hash = (hash ^ ((ulong)g.Id << 32 | (ulong)g.Count << 16 | (ulong)g.Ammo << 8 | (byte)g.Attach)) * 1099511628211UL;
            int slotIndex = (int)(Tick % SnapshotHistory.Size);
            p.GroundHashTick[slotIndex] = Tick;
            p.GroundHash[slotIndex] = hash;
            if (baseline == null) return true;
            int baseIndex = (int)(baseline.Tick % SnapshotHistory.Size);
            return p.GroundHashTick[baseIndex] != baseline.Tick || p.GroundHash[baseIndex] != hash;
        }

        private static float DistanceSquared(float x, float z, Vector3 p) => (x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z);
    }
}
