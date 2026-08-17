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
    /// Server: ticks burn DoT on ships and asteroids, and applies gravity-well pull
    /// to ships and loose gems.
    /// Map size from <see cref="MapStateSingleton"/>; pull uses toroidal shortest path.
    /// Does not wrap ship transforms.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletBankStatusTickSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            double elapsed = SystemAPI.Time.ElapsedTime;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            Entity gemPrefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<GamePrefabs>(out var gamePrefabs))
                gemPrefab = gamePrefabs.Gem;

            TickBurns(ref state, ref ecb, elapsed, gemPrefab);
            TickAsteroidBurns(ref state, ref ecb, elapsed);
            TickGravityWells(ref state, dt, elapsed);

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        void TickBurns(ref SystemState state, ref EntityCommandBuffer ecb, double elapsed, Entity gemPrefab)
        {
            foreach (var (burnRw, shipRw, vitalsRw, transform, entity) in SystemAPI
                         .Query<RefRW<ShipBurnOverTimeState>, RefRW<ShipState>, RefRW<ShipVitalsState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                ref var ship = ref shipRw.ValueRW;
                if (ship.IsDead || !state.EntityManager.HasBuffer<BurnOverTimeElement>(entity))
                {
                    if (burnRw.ValueRO.ExpiresAt > 0.0)
                        burnRw.ValueRW = default;
                    if (state.EntityManager.HasBuffer<BurnOverTimeElement>(entity))
                        state.EntityManager.GetBuffer<BurnOverTimeElement>(entity).Clear();
                    continue;
                }

                var instances = state.EntityManager.GetBuffer<BurnOverTimeElement>(entity);
                float3 bodyPos = transform.ValueRO.Position;
                bodyPos.y = 0f;

                for (int i = instances.Length - 1; i >= 0; i--)
                {
                    var inst = instances[i];
                    if (!inst.IsActive(elapsed))
                    {
                        instances.RemoveAt(i);
                        continue;
                    }

                    if (elapsed < inst.NextTickAt)
                        continue;

                    float tick = math.max(0.05f, inst.TickInterval);
                    float damage = math.max(0f, inst.Dps) * tick;
                    inst.NextTickAt = elapsed + tick;
                    instances[i] = inst;
                    if (damage <= 0.0001f)
                        continue;

                    float health = ship.Health;
                    float gems = ship.CurrentGems;
                    bool isDead = ship.IsDead;
                    var result = ShipDamageLogic.ApplyHullAndGemDamage(
                        ref health,
                        ref gems,
                        ref isDead,
                        CardEffectQuery.ScaleIncomingDamage(state.EntityManager, entity, damage),
                        ship.Team,
                        (TeamId)inst.SourceTeam,
                        gemExpulsionPerHullDamage: ShipDamageLogic.ExcessDamageGemExpulsionPerHullDamage,
                        isImmune: false);
                    ship.Health = health;
                    ship.CurrentGems = gems;
                    ship.IsDead = isDead;

                    if (result.AppliedHullDamage)
                        vitalsRw.ValueRW.LastHullDamageTime = elapsed;

                    if (result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead)
                    {
                        float tickDamage = math.abs(result.HealthDelta);
                        if (tickDamage < 0.0001f)
                            tickDamage = damage;
                        ref var burn = ref burnRw.ValueRW;
                        burn.TickSequence += 1;
                        burn.LastTickDamage = tickDamage;
                        float3 tickPos = bodyPos + inst.HitOffset;
                        tickPos.y = 0f;
                        SendBurnTickHit(
                            ref ecb,
                            tickPos,
                            tickDamage,
                            inst.VfxTeam,
                            inst.VfxBankIndex,
                            asteroidHealthAfter: -1f);
                    }

                    if ((result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead) &&
                        inst.SourceNetworkId > 0)
                    {
                        ShipMatchStatsLogic.SetLastDamager(
                            state.EntityManager,
                            entity,
                            inst.SourceNetworkId,
                            (float)elapsed);
                    }

                    if (result.GemsToExpel > 0.0001f && gemPrefab != Entity.Null)
                    {
                        int sourceNetworkId = 0;
                        if (state.EntityManager.HasComponent<GhostOwner>(entity))
                            sourceNetworkId = state.EntityManager.GetComponentData<GhostOwner>(entity).NetworkId;
                        ShipGemExpulsion.SpawnFromDamage(
                            ecb,
                            gemPrefab,
                            bodyPos,
                            result.GemsToExpel,
                            intensity: 0.5f,
                            salt: (uint)(entity.Index * 19349663) ^ (uint)(elapsed * 1000.0),
                            (float)elapsed,
                            sourceNetworkId);
                    }

                    if (ship.IsDead)
                        break;
                }

                BulletBankHitEffects.SyncShipBurnSummary(state.EntityManager, entity, instances);
            }
        }

        void TickAsteroidBurns(ref SystemState state, ref EntityCommandBuffer ecb, double elapsed)
        {
            foreach (var (asteroidRw, transform, entity) in SystemAPI
                         .Query<RefRW<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag, BurnOverTimeElement>()
                         .WithEntityAccess())
            {
                var instances = state.EntityManager.GetBuffer<BurnOverTimeElement>(entity);
                ref var asteroid = ref asteroidRw.ValueRW;
                if (asteroid.IsDestroyed || asteroid.Health <= 0.01f)
                {
                    instances.Clear();
                    continue;
                }

                float3 bodyPos = transform.ValueRO.Position;
                bodyPos.y = 0f;

                for (int i = instances.Length - 1; i >= 0; i--)
                {
                    var inst = instances[i];
                    if (!inst.IsActive(elapsed))
                    {
                        instances.RemoveAt(i);
                        continue;
                    }

                    if (elapsed < inst.NextTickAt)
                        continue;

                    float tick = math.max(0.05f, inst.TickInterval);
                    float damage = math.max(0f, inst.Dps) * tick;
                    inst.NextTickAt = elapsed + tick;
                    instances[i] = inst;
                    if (damage <= 0.0001f)
                        continue;

                    asteroid.Health -= damage;
                    asteroid.LastInteractTeam = (TeamId)inst.SourceTeam;
                    if (asteroid.Health <= 0f)
                    {
                        asteroid.Health = 0f;
                        asteroid.IsDestroyed = true;
                        AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, entity);
                    }

                    float3 tickPos = bodyPos + inst.HitOffset;
                    tickPos.y = 0f;
                    SendBurnTickHit(
                        ref ecb,
                        tickPos,
                        damage,
                        inst.VfxTeam,
                        inst.VfxBankIndex,
                        asteroid.Health);

                    if (asteroid.IsDestroyed)
                        break;
                }
            }
        }

        static bool IsGravityWellFriendlyOrSelf(TeamId shipTeam, int shipNetworkId, in GravityWellElement well)
        {
            if (well.OwnerNetworkId > 0 && shipNetworkId == well.OwnerNetworkId)
                return true;
            var wellTeam = (TeamId)well.OwnerTeam;
            return wellTeam != TeamId.None && wellTeam == shipTeam;
        }

        /// <summary>
        /// Sequence-0 HitRpc: replay the bank impact + asteroid float without adopting a tracer.
        /// </summary>
        static void SendBurnTickHit(
            ref EntityCommandBuffer ecb,
            float3 hitPosition,
            float damage,
            byte ownerTeam,
            int bankIndex,
            float asteroidHealthAfter)
        {
            BulletNetNotify.SendRamAsteroidHit(
                ref ecb,
                hitPosition,
                damage,
                ownerTeam,
                bankIndex,
                scaleMultiplier: 1f,
                asteroidHealthAfter);
        }

        void TickGravityWells(ref SystemState state, float dt, double elapsed)
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity) ||
                !state.EntityManager.HasBuffer<GravityWellElement>(bulletEntity))
                return;

            var wells = state.EntityManager.GetBuffer<GravityWellElement>(bulletEntity);
            for (int i = wells.Length - 1; i >= 0; i--)
            {
                if (elapsed >= wells[i].ExpiresAt)
                    wells.RemoveAtSwapBack(i);
            }

            if (wells.Length == 0)
                return;

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) ||
                !ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
                return;

            float mapW = mapState.MapWidth;
            float mapH = mapState.MapHeight;

            foreach (var (transform, velRw, ship, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                int shipNetworkId = 0;
                if (state.EntityManager.HasComponent<GhostOwner>(entity))
                    shipNetworkId = state.EntityManager.GetComponentData<GhostOwner>(entity).NetworkId;

                float3 pos = transform.ValueRO.Position;
                // Skip own ship and same-team allies — wells only pull enemies (and gems).
                float3 add = float3.zero;
                for (int i = 0; i < wells.Length; i++)
                {
                    var well = wells[i];
                    if (IsGravityWellFriendlyOrSelf(ship.ValueRO.Team, shipNetworkId, well))
                        continue;
                    float dist = ToroidalMapEcs.ToroidalDistance(well.Center, pos, mapW, mapH);
                    if (dist > well.Radius || well.Radius < 0.05f)
                        continue;

                    // Toward the well along the shortest torus path.
                    float3 dir = ToroidalMapEcs.ToroidalDirection(pos, well.Center, mapW, mapH);
                    add += dir * well.PullAccel * dt;
                }

                if (math.lengthsq(add) > 0.000001f)
                    velRw.ValueRW.Linear += add;
            }

            // Gems are scripted movers (GemKinematics), not PhysicsVelocity.
            foreach (var (kinRw, transform, gemState, entity) in SystemAPI
                         .Query<RefRW<GemKinematics>, RefRO<LocalTransform>, RefRO<GemState>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                if (gemState.ValueRO.IsConsumed)
                    continue;
                if (SystemAPI.HasComponent<GemMotionState>(entity))
                {
                    var motion = SystemAPI.GetComponent<GemMotionState>(entity);
                    if (motion.Phase == GemMotionState.PhaseTractor && motion.TractorShipId != 0)
                        continue;
                }

                float3 pos = transform.ValueRO.Position;
                float3 add = float3.zero;
                for (int i = 0; i < wells.Length; i++)
                {
                    var well = wells[i];
                    float dist = ToroidalMapEcs.ToroidalDistance(well.Center, pos, mapW, mapH);
                    if (dist > well.Radius || well.Radius < 0.05f)
                        continue;

                    float3 dir = ToroidalMapEcs.ToroidalDirection(pos, well.Center, mapW, mapH);
                    float falloff = 1f - (dist / well.Radius);
                    add += dir * (well.PullAccel * dt * math.max(0.15f, falloff));
                }

                if (math.lengthsq(add) <= 0.000001f)
                    continue;

                add.y = 0f;
                kinRw.ValueRW.Velocity += add;
                kinRw.ValueRW.Velocity.y = 0f;
                if (SystemAPI.HasComponent<GemMotionState>(entity))
                {
                    var motion = SystemAPI.GetComponent<GemMotionState>(entity);
                    if (motion.Phase == GemMotionState.PhaseIdle)
                    {
                        motion.Phase = GemMotionState.PhaseCoast;
                        SystemAPI.SetComponent(entity, motion);
                    }
                }
            }
        }
    }
}
