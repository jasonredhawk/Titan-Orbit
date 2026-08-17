using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Applies on-hit bullet-bank abilities (shock, burn, push, gravity well) to player ships.
    /// Called from <see cref="BulletSimulationSystem"/> after hull damage / heal.
    /// </summary>
    public static class BulletBankHitEffects
    {
        /// <summary>
        /// Enemy-ship on-hit: shock, burn, concussive push. Gravity wells are spawned separately
        /// for any impact kind.
        /// </summary>
        public static void ApplyShipOnHit(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity shipEntity,
            in BulletElement bullet,
            float3 hitPoint,
            float3 shipPos,
            BulletBankProfile profile,
            double serverElapsed,
            float mapW,
            float mapH)
        {
            if (profile == null || shipEntity == Entity.Null)
                return;

            int extras = bullet.FirePowerExtraLevels;
            if (profile.TryGetResolvedAbility(BulletBankAbilityType.ElectricShockDisable, extras, out BulletBankAbility shock) &&
                shock != null)
                ApplyElectricShock(em, shipEntity, shock, bullet, serverElapsed);

            if (profile.TryGetResolvedAbility(BulletBankAbilityType.BurnOverTime, extras, out BulletBankAbility burn) &&
                burn != null)
                ApplyBurn(em, ecb, shipEntity, burn, bullet, hitPoint, shipPos, serverElapsed, mapW, mapH);

            if (profile.TryGetResolvedAbility(BulletBankAbilityType.ConcussivePush, extras, out BulletBankAbility push) &&
                push != null)
                ApplyConcussivePush(em, shipEntity, hitPoint, shipPos, push, mapW, mapH, bullet.StrengthScale);
        }

        /// <summary>Spawns a gravity well at <paramref name="hitPoint"/> when the profile has GravityPull.</summary>
        public static void TrySpawnGravityWell(
            DynamicBuffer<GravityWellElement> wells,
            float3 hitPoint,
            BulletBankProfile profile,
            double serverElapsed,
            int ownerNetworkId,
            byte ownerTeam,
            int firePowerExtraLevels = 0,
            float strengthScale = 1f)
        {
            if (profile == null ||
                !profile.TryGetResolvedAbility(
                    BulletBankAbilityType.GravityPull, firePowerExtraLevels, out BulletBankAbility gravity) ||
                gravity == null)
                return;

            float strength = ResolveStrengthScale(strengthScale);
            float radius = (gravity.radius > 0.1f ? gravity.radius : 8f) * strength;
            float pull = (gravity.magnitude > 0.01f ? gravity.magnitude : 12f) * strength;
            float duration = gravity.duration > 0.05f ? gravity.duration : 1.5f;
            wells.Add(new GravityWellElement
            {
                Center = hitPoint,
                Radius = radius,
                PullAccel = pull,
                ExpiresAt = serverElapsed + duration,
                OwnerNetworkId = ownerNetworkId,
                OwnerTeam = ownerTeam,
            });
        }

        public static void ApplyElectricShock(
            EntityManager em,
            Entity shipEntity,
            BulletBankAbility shock,
            in BulletElement bullet,
            double serverElapsed)
        {
            if (!em.HasComponent<ShipElectricShockState>(shipEntity))
                return;

            float duration = shock.duration > 0.05f ? shock.duration : 1f;
            float expires = (float)(serverElapsed + duration);
            var state = em.GetComponentData<ShipElectricShockState>(shipEntity);
            if (expires > state.ExpiresAt)
                state.ExpiresAt = expires;
            state.VfxBankIndex = math.max(0, bullet.BankIndex);
            state.VfxTeam = bullet.OwnerTeam;
            em.SetComponentData(shipEntity, state);
        }

        public static void ApplyBurn(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity shipEntity,
            BulletBankAbility burn,
            in BulletElement bullet,
            float3 hitPoint,
            float3 shipPos,
            double serverElapsed,
            float mapW,
            float mapH)
        {
            if (!em.HasComponent<ShipBurnOverTimeState>(shipEntity))
                return;

            EnqueueOrAddBurn(em, ecb, shipEntity, burn, in bullet, hitPoint, shipPos,
                serverElapsed, mapW, mapH, syncShipSummary: true);
        }

        /// <summary>
        /// Starts a new burn instance on a seed-hydrated asteroid at this bullet's hit point.
        /// </summary>
        public static void ApplyAsteroidBurn(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity asteroidEntity,
            BulletBankAbility burn,
            in BulletElement bullet,
            float3 hitPoint,
            float3 asteroidPos,
            double serverElapsed,
            float mapW,
            float mapH)
        {
            if (asteroidEntity == Entity.Null || burn == null)
                return;

            EnqueueOrAddBurn(em, ecb, asteroidEntity, burn, in bullet, hitPoint, asteroidPos,
                serverElapsed, mapW, mapH, syncShipSummary: false);
        }

        /// <summary>
        /// First-hit <c>AddBuffer</c> is deferred to <paramref name="ecb"/> so
        /// <see cref="BulletSimulationSystem"/> can keep mutating <c>BulletElement</c> this tick.
        /// </summary>
        static void EnqueueOrAddBurn(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity entity,
            BulletBankAbility burn,
            in BulletElement bullet,
            float3 hitPoint,
            float3 bodyPos,
            double serverElapsed,
            float mapW,
            float mapH,
            bool syncShipSummary)
        {
            var instance = CreateBurnInstance(hitPoint, bodyPos, burn, in bullet, serverElapsed, mapW, mapH);
            if (!em.HasBuffer<BurnOverTimeElement>(entity))
            {
                ecb.AddBuffer<BurnOverTimeElement>(entity);
                s_PendingBurns.Add(new PendingBurn
                {
                    Entity = entity,
                    Instance = instance,
                    SyncShipSummary = syncShipSummary,
                });
                return;
            }

            var instances = em.GetBuffer<BurnOverTimeElement>(entity);
            AddBurnInstance(instances, instance);
            if (syncShipSummary)
                SyncShipBurnSummary(em, entity, instances);
        }

        /// <summary>Drops queued first-hit burns (call at tick start so a thrown update cannot leak).</summary>
        public static void ClearPendingBurns() => s_PendingBurns.Clear();

        /// <summary>Applies burns queued because the target had no buffer until ECB playback.</summary>
        public static void FlushPendingBurns(EntityManager em)
        {
            for (int i = 0; i < s_PendingBurns.Count; i++)
            {
                PendingBurn pending = s_PendingBurns[i];
                if (pending.Entity == Entity.Null || !em.Exists(pending.Entity))
                    continue;
                if (!em.HasBuffer<BurnOverTimeElement>(pending.Entity))
                    em.AddBuffer<BurnOverTimeElement>(pending.Entity);

                var instances = em.GetBuffer<BurnOverTimeElement>(pending.Entity);
                AddBurnInstance(instances, pending.Instance);
                if (pending.SyncShipSummary)
                    SyncShipBurnSummary(em, pending.Entity, instances);
            }

            s_PendingBurns.Clear();
        }

        struct PendingBurn
        {
            public Entity Entity;
            public BurnOverTimeElement Instance;
            public bool SyncShipSummary;
        }

        static readonly List<PendingBurn> s_PendingBurns = new List<PendingBurn>(8);

        public static void EnsureBurnBuffer(EntityManager em, Entity entity)
        {
            if (!em.HasBuffer<BurnOverTimeElement>(entity))
                em.AddBuffer<BurnOverTimeElement>(entity);
        }

        public static BurnOverTimeElement CreateBurnInstance(
            float3 hitPoint,
            float3 bodyPos,
            BulletBankAbility burn,
            in BulletElement bullet,
            double serverElapsed,
            float mapW,
            float mapH)
        {
            float duration = math.max(0.05f, burn.duration > 0f ? burn.duration : 2f);
            float tick = math.max(0.05f, burn.tickInterval > 0f ? burn.tickInterval : 0.25f);
            float dps = (burn.magnitude > 0f ? burn.magnitude : 1f) * ResolveStrengthScale(bullet.StrengthScale);
            hitPoint.y = 0f;
            bodyPos.y = 0f;
            float3 offset = ToroidalMapEcs.IsValidMapSize(mapW, mapH)
                ? ToroidalMapEcs.ShortestOffsetXZ(bodyPos, hitPoint, mapW, mapH)
                : hitPoint - bodyPos;
            offset.y = 0f;

            return new BurnOverTimeElement
            {
                HitOffset = offset,
                ExpiresAt = (float)(serverElapsed + duration),
                NextTickAt = serverElapsed + tick,
                Dps = dps,
                TickInterval = tick,
                VfxBankIndex = math.max(0, bullet.BankIndex),
                VfxTeam = bullet.OwnerTeam,
                SourceNetworkId = bullet.OwnerNetworkId,
                SourceTeam = bullet.OwnerTeam,
            };
        }

        public static void AddBurnInstance(
            DynamicBuffer<BurnOverTimeElement> instances,
            float3 hitPoint,
            float3 bodyPos,
            BulletBankAbility burn,
            in BulletElement bullet,
            double serverElapsed,
            float mapW,
            float mapH)
        {
            AddBurnInstance(instances, CreateBurnInstance(
                hitPoint, bodyPos, burn, in bullet, serverElapsed, mapW, mapH));
        }

        public static void AddBurnInstance(
            DynamicBuffer<BurnOverTimeElement> instances,
            in BurnOverTimeElement instance)
        {
            if (instances.Length >= BurnOverTimeElement.MaxInstances)
                instances.RemoveAt(0);

            instances.Add(instance);
        }

        public static void SyncShipBurnSummary(
            EntityManager em,
            Entity shipEntity,
            DynamicBuffer<BurnOverTimeElement> instances)
        {
            if (!em.HasComponent<ShipBurnOverTimeState>(shipEntity))
                return;

            var state = em.GetComponentData<ShipBurnOverTimeState>(shipEntity);
            uint seq = state.TickSequence;
            float lastDmg = state.LastTickDamage;
            state = default;
            state.TickSequence = seq;
            state.LastTickDamage = lastDmg;
            for (int i = 0; i < instances.Length; i++)
            {
                var inst = instances[i];
                if (inst.ExpiresAt > state.ExpiresAt)
                    state.ExpiresAt = inst.ExpiresAt;
                state.VfxBankIndex = inst.VfxBankIndex;
                state.VfxTeam = inst.VfxTeam;
                state.Dps = inst.Dps;
                state.TickInterval = inst.TickInterval;
                state.SourceNetworkId = inst.SourceNetworkId;
                state.SourceTeam = inst.SourceTeam;
                state.NextTickAt = inst.NextTickAt;
            }

            em.SetComponentData(shipEntity, state);
        }

        public static void ApplyConcussivePush(
            EntityManager em,
            Entity shipEntity,
            float3 hitPoint,
            float3 shipPos,
            BulletBankAbility push,
            float mapW,
            float mapH,
            float strengthScale = 1f)
        {
            float force = (push.magnitude > 0f ? push.magnitude : 12f) * ResolveStrengthScale(strengthScale);
            ApplyConcussivePushForce(em, shipEntity, hitPoint, shipPos, force, mapW, mapH);
        }

        /// <summary>
        /// Knockback from a world point using a raw force (mines — no bullet-bank ability row).
        /// Same mass-aware impulse as <see cref="ApplyConcussivePush"/>.
        /// </summary>
        public static void ApplyConcussivePushForce(
            EntityManager em,
            Entity shipEntity,
            float3 hitPoint,
            float3 shipPos,
            float force,
            float mapW,
            float mapH)
        {
            if (!em.HasComponent<PhysicsVelocity>(shipEntity))
                return;
            if (force <= 0.01f)
                return;

            float3 dir = ToroidalMapEcs.ShortestOffsetXZ(hitPoint, shipPos, mapW, mapH);
            dir.y = 0f;
            if (math.lengthsq(dir) < 0.0001f)
                dir = new float3(0f, 0f, 1f);
            else
                dir = math.normalize(dir);

            float mass = 1f;
            if (em.HasComponent<PhysicsMass>(shipEntity))
            {
                float inv = em.GetComponentData<PhysicsMass>(shipEntity).InverseMass;
                if (inv > 1e-5f)
                    mass = 1f / inv;
            }

            var vel = em.GetComponentData<PhysicsVelocity>(shipEntity);
            vel.Linear += dir * (force / math.max(ShipCollisionImpulseLogic.MinCollisionMass, mass));
            em.SetComponentData(shipEntity, vel);
        }

        /// <summary>
        /// [TITAN-ORBIT] Mine explode splash: falloff hull damage + knockback on enemy ships.
        /// Does not hit moons (call <see cref="PlanetGemMoonCombatLogic.ApplyBulletDamage"/> for contact).
        /// Does not hit asteroids (mines are anti-ship / anti-moon).
        /// </summary>
        public static void TryApplyMineBlast(
            EntityManager em,
            float3 hitPoint,
            float blastRadius,
            float blastForce,
            float centerDamage,
            Entity skipEntity,
            byte ownerTeam,
            int ownerNetworkId,
            double serverElapsed,
            float mapW,
            float mapH,
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            bool allowOwnerHits = false)
        {
            if (centerDamage <= 0.01f && blastForce <= 0.01f)
                return;
            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            float radius = math.max(0.05f, blastRadius);
            hitPoint.y = 0f;

            if (centerDamage > 0.01f)
            {
                ApplyBlastToShips(
                    em, hitPoint, radius, centerDamage, skipEntity,
                    ownerTeam, ownerNetworkId, serverElapsed, mapW, mapH,
                    ecb, gemPrefab, ShipDamageLogic.ExcessDamageGemExpulsionPerHullDamage,
                    allowOwnerHits);
            }

            if (blastForce <= 0.01f)
                return;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (states[i].IsDead)
                    continue;
                if (!allowOwnerHits)
                {
                    if (ownerNetworkId > 0 && owners[i].NetworkId == ownerNetworkId)
                        continue;
                    if (ownerTeam != 0 && (byte)states[i].Team == ownerTeam)
                        continue;
                }

                float3 pos = transforms[i].Position;
                pos.y = 0f;
                float dist = ToroidalMapEcs.ToroidalDistance(hitPoint, pos, mapW, mapH);
                if (dist >= radius)
                    continue;

                float falloff = 1f - (dist / radius);
                ApplyConcussivePushForce(
                    em, entities[i], hitPoint, pos, blastForce * math.max(0f, falloff), mapW, mapH);
            }
        }

        /// <summary>
        /// [TITAN-ORBIT] AoE damage for Concussive Push. Center takes
        /// <paramref name="centerDamage"/>; linear falloff to 0 at blast radius.
        /// Skips <paramref name="skipEntity"/> (the primary hit already took full damage).
        /// Hits enemy ships, asteroids, enemy turrets, and enemy drones.
        /// </summary>
        public static void TryApplyConcussiveBlastDamage(
            EntityManager em,
            float3 hitPoint,
            BulletBankProfile profile,
            float mapW,
            float mapH,
            in BulletElement bullet,
            float centerDamage,
            Entity skipEntity,
            double serverElapsed,
            List<PlanetaryDefenseHitTarget> defenseTargets,
            List<DroneHitTarget> droneTargets,
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float gemExpulsionPerHullDamage)
        {
            if (profile == null || centerDamage <= 0.01f)
                return;
            if (!profile.TryGetResolvedAbility(
                    BulletBankAbilityType.ConcussivePush, bullet.FirePowerExtraLevels,
                    out BulletBankAbility push) ||
                push == null)
                return;
            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return;

            // --- Join Team Crash!!! ---
            // [TITAN-ORBIT] Splash is server-only today; still honor the helpers so a
            // future client call cannot gather ships/asteroids during Instantiates.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            float strength = ResolveStrengthScale(bullet.StrengthScale);
            float radius = (push.radius > 0.1f ? push.radius : 6f) * strength;
            if (radius < 0.05f)
                return;

            hitPoint.y = 0f;
            byte ownerTeam = bullet.OwnerTeam;
            int ownerNet = bullet.OwnerNetworkId;

            ApplyBlastToShips(
                em, hitPoint, radius, centerDamage, skipEntity, ownerTeam, ownerNet,
                serverElapsed, mapW, mapH, ecb, gemPrefab, gemExpulsionPerHullDamage);
            ApplyBlastToAsteroids(em, hitPoint, radius, centerDamage, skipEntity, mapW, mapH);
            ApplyBlastToTurrets(em, hitPoint, radius, centerDamage, skipEntity, ownerTeam, serverElapsed, defenseTargets, mapW, mapH);
            ApplyBlastToDrones(em, hitPoint, radius, centerDamage, ownerTeam, ownerNet, droneTargets, mapW, mapH);
        }

        /// <summary>
        /// Hull + cargo damage on enemy ships in the blast. Leftover damage spills gems 1:1;
        /// death still needs hull and cargo both empty.
        /// </summary>
        static void ApplyBlastToShips(
            EntityManager em,
            float3 center,
            float radius,
            float centerDamage,
            Entity skipEntity,
            byte ownerTeam,
            int ownerNet,
            double serverElapsed,
            float mapW,
            float mapH,
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float gemExpulsionPerHullDamage,
            bool allowOwnerHits = false)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadWrite<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                Entity shipEntity = entities[i];
                if (shipEntity == skipEntity)
                    continue;
                if (states[i].IsDead)
                    continue;
                if (!allowOwnerHits)
                {
                    if (ownerNet > 0 && owners[i].NetworkId == ownerNet)
                        continue;
                    if (ownerTeam != 0 && (byte)states[i].Team == ownerTeam)
                        continue;
                }

                float3 pos = transforms[i].Position;
                pos.y = 0f;
                float dist = ToroidalMapEcs.ToroidalDistance(center, pos, mapW, mapH);
                float splash = BlastFalloffDamage(centerDamage, dist, radius);
                if (splash <= 0.01f)
                    continue;

                bool moonImmune = ShipMoonDockState.IsFullyLandedOnMoon(em, shipEntity);

                var ship = states[i];
                float health = ship.Health;
                float gems = ship.CurrentGems;
                bool isDead = ship.IsDead;
                var damageTeam = allowOwnerHits && ship.Team == (TeamId)ownerTeam
                    ? TeamId.None
                    : (TeamId)ownerTeam;
                var result = ShipDamageLogic.ApplyHullAndGemDamage(
                    ref health, ref gems, ref isDead, splash,
                    ship.Team, damageTeam, gemExpulsionPerHullDamage, isImmune: moonImmune);
                ship.Health = health;
                ship.CurrentGems = gems;
                ship.IsDead = isDead;
                em.SetComponentData(shipEntity, ship);

                if (result.AppliedHullDamage && em.HasComponent<ShipVitalsState>(shipEntity))
                {
                    var vitals = em.GetComponentData<ShipVitalsState>(shipEntity);
                    vitals.LastHullDamageTime = serverElapsed;
                    em.SetComponentData(shipEntity, vitals);
                }

                if ((result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead) &&
                    ownerNet > 0)
                    ShipMatchStatsLogic.SetLastDamager(em, shipEntity, ownerNet, (float)serverElapsed);

                if (result.GemsToExpel > 0.0001f && gemPrefab != Entity.Null)
                {
                    int sourceNetworkId = owners[i].NetworkId;
                    ShipGemExpulsion.SpawnFromDamage(
                        ecb,
                        gemPrefab,
                        pos,
                        result.GemsToExpel,
                        intensity: 0.5f,
                        salt: (uint)(shipEntity.Index * 19349663) ^ (uint)(serverElapsed * 1000.0),
                        (float)serverElapsed,
                        sourceNetworkId);
                }
            }
        }

        /// <summary>Subtracts falloff damage from asteroids in the blast (can finish a rock).</summary>
        static void ApplyBlastToAsteroids(
            EntityManager em,
            float3 center,
            float radius,
            float centerDamage,
            Entity skipEntity,
            float mapW,
            float mapH)
        {
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadWrite<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i] == skipEntity)
                    continue;
                if (states[i].IsDestroyed || states[i].Health <= 0f)
                    continue;

                float3 pos = transforms[i].Position;
                pos.y = 0f;
                float dist = ToroidalMapEcs.ToroidalDistance(center, pos, mapW, mapH);
                float splash = BlastFalloffDamage(centerDamage, dist, radius);
                if (splash <= 0.01f)
                    continue;

                var asteroid = states[i];
                asteroid.Health -= splash;
                if (asteroid.Health <= 0f)
                {
                    asteroid.Health = 0f;
                    asteroid.IsDestroyed = true;
                }

                em.SetComponentData(entities[i], asteroid);
            }
        }

        /// <summary>
        /// Damages enemy turrets in the blast. Does not skip a whole planet when the primary
        /// hit was one pad — other pads still take falloff. The struck pad is usually at
        /// dist≈0 and would double-dip, so we skip pads whose planet is <paramref name="skipEntity"/>
        /// and whose position is inside 0.35 of center (the primary pad).
        /// </summary>
        static void ApplyBlastToTurrets(
            EntityManager em,
            float3 center,
            float radius,
            float centerDamage,
            Entity skipEntity,
            byte ownerTeam,
            double serverElapsed,
            List<PlanetaryDefenseHitTarget> defenseTargets,
            float mapW,
            float mapH)
        {
            if (defenseTargets == null)
                return;

            for (int i = 0; i < defenseTargets.Count; i++)
            {
                var pad = defenseTargets[i];
                if (ownerTeam != 0 && pad.Team == ownerTeam)
                    continue;

                float3 pos = pad.Position;
                pos.y = 0f;
                float dist = ToroidalMapEcs.ToroidalDistance(center, pos, mapW, mapH);
                // Primary turret sits on the impact point — skip so it is not double-dipped.
                if (pad.PlanetEntity == skipEntity && dist < 0.35f)
                    continue;
                float splash = BlastFalloffDamage(centerDamage, dist, radius);
                if (splash <= 0.01f)
                    continue;

                PlanetaryDefenseHitScan.ApplyDamage(em, pad.PlanetEntity, pad.SlotIndex, splash, serverElapsed);
            }
        }

        /// <summary>Damages enemy drones in the blast using this tick's derived hit spheres.</summary>
        static void ApplyBlastToDrones(
            EntityManager em,
            float3 center,
            float radius,
            float centerDamage,
            byte ownerTeam,
            int ownerNet,
            List<DroneHitTarget> droneTargets,
            float mapW,
            float mapH)
        {
            if (droneTargets == null)
                return;

            for (int i = 0; i < droneTargets.Count; i++)
            {
                var drone = droneTargets[i];
                if (ownerNet > 0 && drone.OwnerNetworkId == ownerNet)
                    continue;
                if (ownerTeam != 0 && drone.Team == ownerTeam)
                    continue;

                float3 pos = drone.Position;
                pos.y = 0f;
                float dist = ToroidalMapEcs.ToroidalDistance(center, pos, mapW, mapH);
                if (dist < 0.35f)
                    continue;
                float splash = BlastFalloffDamage(centerDamage, dist, radius);
                if (splash <= 0.01f)
                    continue;

                DroneSwarmHitScan.ApplyDamageToDroneSlot(em, drone.ShipEntity, drone.SlotIndex, splash);
            }
        }

        /// <summary>Linear falloff: 1 at center, 0 at radius. Dist ≥ radius → 0.</summary>
        static float BlastFalloffDamage(float centerDamage, float dist, float radius)
        {
            if (radius < 0.05f || dist >= radius)
                return 0f;
            float falloff = 1f - (dist / radius);
            return centerDamage * math.max(0f, falloff);
        }

        /// <summary>
        /// Instant outward blast on loose gems around <paramref name="hitPoint"/>.
        /// Skips consumed gems and live tractor locks. Does not wrap gem transforms.
        /// </summary>
        public static void TryApplyConcussiveGemBlast(
            EntityManager em,
            float3 hitPoint,
            BulletBankProfile profile,
            float mapW,
            float mapH,
            int firePowerExtraLevels = 0,
            float strengthScale = 1f)
        {
            if (profile == null ||
                !profile.TryGetResolvedAbility(
                    BulletBankAbilityType.ConcussivePush, firePowerExtraLevels, out BulletBankAbility push) ||
                push == null)
                return;

            float strength = ResolveStrengthScale(strengthScale);
            float radius = (push.radius > 0.1f ? push.radius : 6f) * strength;
            float force = (push.magnitude > 0f ? push.magnitude : 12f) * strength;
            // Gems are scripted pickups (explosion speeds ~2); keep the blast readable, not a teleport.
            float impulse = math.min(force * 0.4f, 8f);
            AddGemVelocityInRadius(em, hitPoint, radius, impulse, towardCenter: false, mapW, mapH);
        }

        /// <summary>
        /// Adds XZ velocity to gems inside <paramref name="radius"/>. Positive impulse is
        /// outward (concussion) or toward the center (gravity, pass <paramref name="towardCenter"/>).
        /// Map size from the caller — toroidal shortest path only. Unbounded gem flight.
        /// </summary>
        public static void AddGemVelocityInRadius(
            EntityManager em,
            float3 center,
            float radius,
            float impulse,
            bool towardCenter,
            float mapW,
            float mapH)
        {
            if (impulse <= 0.0001f || radius < 0.05f)
                return;
            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return;
            // Server-only callers; keep the map-body gather gated if this helper is ever hit on a client.
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadWrite<GemKinematics>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var states = query.ToComponentDataArray<GemState>(Unity.Collections.Allocator.Temp);
            using var kinematics = query.ToComponentDataArray<GemKinematics>(Unity.Collections.Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            center.y = 0f;
            for (int i = 0; i < entities.Length; i++)
            {
                if (states[i].IsConsumed)
                    continue;

                Entity gem = entities[i];
                if (IsGemUnderTractor(em, gem))
                    continue;

                float3 pos = transforms[i].Position;
                pos.y = 0f;
                float dist = ToroidalMapEcs.ToroidalDistance(center, pos, mapW, mapH);
                if (dist > radius)
                    continue;

                float3 dir = towardCenter
                    ? ToroidalMapEcs.ToroidalDirection(pos, center, mapW, mapH)
                    : ToroidalMapEcs.ToroidalDirection(center, pos, mapW, mapH);
                dir.y = 0f;
                if (math.lengthsq(dir) < 0.0001f)
                    dir = towardCenter ? new float3(0f, 0f, 0f) : new float3(0f, 0f, 1f);

                float falloff = 1f - (dist / radius);
                var kin = kinematics[i];
                kin.Velocity += dir * (impulse * math.max(0.15f, falloff));
                kin.Velocity.y = 0f;
                em.SetComponentData(gem, kin);

                if (em.HasComponent<GemMotionState>(gem))
                {
                    var motion = em.GetComponentData<GemMotionState>(gem);
                    if (motion.Phase == GemMotionState.PhaseIdle)
                    {
                        motion.Phase = GemMotionState.PhaseCoast;
                        em.SetComponentData(gem, motion);
                    }
                }
            }
        }

        /// <summary>
        /// Gameplay strength vs the authored bullet type. Ship / PD leave this unset (0 → 1).
        /// Drones stamp <c>DroneSwarmLogic.DroneFirePowerScale</c> (1/6) for force, radius,
        /// burn DPS, and heal. Durations, tick intervals, and bank multipliers are not scaled here.
        /// </summary>
        public static float ResolveStrengthScale(float strengthScale)
        {
            if (strengthScale <= 0.01f)
                return 1f;
            return strengthScale;
        }

        /// <summary>Legacy name — same as <see cref="ResolveStrengthScale"/>.</summary>
        public static float EffectScaleFromBullet(float scaleMultiplier) =>
            ResolveStrengthScale(scaleMultiplier);

        static bool IsGemUnderTractor(EntityManager em, Entity gem)
        {
            if (!em.HasComponent<GemMotionState>(gem))
                return false;
            var motion = em.GetComponentData<GemMotionState>(gem);
            return motion.Phase == GemMotionState.PhaseTractor && motion.TractorShipId != 0;
        }
    }
}
