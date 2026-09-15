using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: consume gem event RPCs and hydrate local crystals (no gem ghosts).
    /// Copies payloads out of the receive query, destroys the RPC entities, then Instantiates.
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct GemEventRpcClientSystem : ISystem
    {
        struct PendingSpawn
        {
            public GemSpawnRecipe Recipe;
        }

        struct PendingBurst
        {
            public float3 Origin;
            public float Remaining;
            public uint Seed;
            public float SpawnServerTime;
            public bool IsBonus;
        }

        /// <summary>Consumes inbound gem RPCs every client tick.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var destroyEcb = new EntityCommandBuffer(Allocator.Temp);
            var spawns = new NativeList<PendingSpawn>(8, Allocator.Temp);
            var bursts = new NativeList<PendingBurst>(4, Allocator.Temp);
            var consumed = new NativeList<int>(8, Allocator.Temp);
            var values = new NativeList<int2>(4, Allocator.Temp);
            var valueAmounts = new NativeList<float>(4, Allocator.Temp);
            var locks = new NativeList<GemTractorLockRpc>(4, Allocator.Temp);
            var catchUps = new NativeList<GemCatchUpRpc>(8, Allocator.Temp);

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<GemSpawnRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                var r = rpc.ValueRO;
                r.Position.y = 0f;
                spawns.Add(new PendingSpawn { Recipe = GemNetNotify.ToRecipe(r) });
                destroyEcb.DestroyEntity(reqEntity);
            }

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<GemBurstRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                var r = rpc.ValueRO;
                r.Origin.y = 0f;
                bursts.Add(new PendingBurst
                {
                    Origin = r.Origin,
                    Remaining = r.RemainingValue,
                    Seed = r.Seed,
                    SpawnServerTime = r.SpawnServerTime,
                    IsBonus = r.IsBonus != 0,
                });
                destroyEcb.DestroyEntity(reqEntity);
            }

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<GemConsumedRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                consumed.Add(rpc.ValueRO.SpawnId);
                destroyEcb.DestroyEntity(reqEntity);
            }

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<GemValueChangedRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                values.Add(new int2(rpc.ValueRO.SpawnId, 0));
                valueAmounts.Add(rpc.ValueRO.RemainingValue);
                destroyEcb.DestroyEntity(reqEntity);
            }

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<GemTractorLockRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                locks.Add(rpc.ValueRO);
                destroyEcb.DestroyEntity(reqEntity);
            }

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<GemCatchUpRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                catchUps.Add(rpc.ValueRO);
                destroyEcb.DestroyEntity(reqEntity);
            }

            destroyEcb.Playback(em);
            destroyEcb.Dispose();

            Entity gemPrefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs))
                gemPrefab = prefabs.Gem;

            float now = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                em, SystemAPI.Time.ElapsedTime);

            if (gemPrefab != Entity.Null)
            {
                for (int i = 0; i < catchUps.Length; i++)
                    ClientLocalGemSpawn.SpawnFromCatchUp(em, gemPrefab, catchUps[i]);

                for (int i = 0; i < bursts.Length; i++)
                {
                    var b = bursts[i];
                    float elapsed = math.max(0f, now - b.SpawnServerTime);
                    ClientLocalGemSpawn.SpawnBurst(
                        em, gemPrefab, b.Origin, b.Remaining, b.Seed, b.SpawnServerTime, b.IsBonus, elapsed);
                }

                for (int i = 0; i < spawns.Length; i++)
                {
                    var recipe = spawns[i].Recipe;
                    float elapsed = math.max(0f, now - recipe.SpawnServerTime);
                    ClientLocalGemSpawn.SpawnFromRecipe(em, gemPrefab, recipe, elapsed);
                }
            }

            for (int i = 0; i < valueAmounts.Length; i++)
                ClientLocalGemSpawn.ApplyValueChanged(em, values[i].x, valueAmounts[i]);

            for (int i = 0; i < consumed.Length; i++)
                ClientLocalGemSpawn.Consume(em, consumed[i]);

            for (int i = 0; i < locks.Length; i++)
                ClientLocalGemSpawn.ApplyTractorLock(em, locks[i]);

            spawns.Dispose();
            bursts.Dispose();
            consumed.Dispose();
            values.Dispose();
            valueAmounts.Dispose();
            locks.Dispose();
            catchUps.Dispose();
        }
    }

    /// <summary>
    /// Client: primary-wing tractor pull on hydrated gems (lock comes from RPC, not assignment).
    /// Runs before client gem motion so velocity and pose stay same-tick coherent.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemEventRpcClientSystem))]
    [UpdateBefore(typeof(GemClientMotionSystem))]
    public partial struct GemClientTractorPullSystem : ISystem
    {
        /// <summary>Overwrites velocity toward the locked wing after deploy is ready.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH) &&
                SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
                ToroidalMapEcs.SetMapSize(mapW, mapH);
            }

            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return;

            float nowServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);
            int simHz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate) &&
                tickRate.SimulationTickRate > 0)
                simHz = tickRate.SimulationTickRate;

            var em = state.EntityManager;
            var shipByNetId = new NativeHashMap<int, Entity>(8, Allocator.Temp);
            foreach (var (owner, shipEntity) in SystemAPI
                         .Query<RefRO<GhostOwner>>()
                         .WithAll<ShipTag, ShipState, LocalTransform>()
                         .WithEntityAccess())
            {
                int id = owner.ValueRO.NetworkId;
                if (id > 0)
                    shipByNetId.TryAdd(id, shipEntity);
            }

            foreach (var (motion, gemState, transform, kinematics) in SystemAPI
                         .Query<RefRW<GemMotionState>, RefRO<GemState>, RefRO<LocalTransform>, RefRW<GemKinematics>>()
                         .WithAll<GemTag, ClientSeedHydratedGem>())
            {
                int shipId = motion.ValueRO.TractorShipId;
                if (shipId == 0)
                    continue;
                if (gemState.ValueRO.IsConsumed)
                    continue;
                if (!GemTractorBeamMath.IsDeployPullReady(
                        motion.ValueRO.TractorLockTick,
                        motion.ValueRO.TractorExtendDuration,
                        nowServerTime,
                        simHz))
                    continue;
                if (!shipByNetId.TryGetValue(shipId, out Entity shipEntity) || !em.Exists(shipEntity))
                    continue;

                var shipTransform = em.GetComponentData<LocalTransform>(shipEntity);
                var ship = em.GetComponentData<ShipState>(shipEntity);
                int shipLevel = math.max(1, ship.ShipLevel);
                bool inOrbit = em.HasComponent<ShipOrbitState>(shipEntity) &&
                               em.GetComponentData<ShipOrbitState>(shipEntity).InOrbitRing;

                int wingIndex = motion.ValueRO.TractorWingIndex;
                float3 pullTarget = shipTransform.Position;
                float pullSpeed = GemTractorBeamMath.MinGameplayPullSpeed;
                if (em.HasBuffer<ShipWingTractorBeamElement>(shipEntity))
                {
                    var wings = em.GetBuffer<ShipWingTractorBeamElement>(shipEntity);
                    if (wingIndex >= 0 && wingIndex < wings.Length)
                    {
                        pullTarget = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wingIndex]);
                        ShipWingTractorBeamPose.GetTractorParams(
                            wings[wingIndex], shipLevel, inOrbit, out _, out pullSpeed);
                    }
                }

                float3 toWing = GemTractorBeamMath.ToroidalDirection(
                    transform.ValueRO.Position, pullTarget, mapW, mapH);
                if (math.lengthsq(toWing) < 0.0001f)
                    continue;

                var kin = kinematics.ValueRO;
                kin.Velocity = toWing * math.max(pullSpeed, GemTractorBeamMath.MinGameplayPullSpeed);
                kinematics.ValueRW = kin;
                if (motion.ValueRO.Phase != GemMotionState.PhaseTractor)
                {
                    var m = motion.ValueRO;
                    m.Phase = GemMotionState.PhaseTractor;
                    motion.ValueRW = m;
                }
            }

            shipByNetId.Dispose();
        }
    }

    /// <summary>
    /// Client: same coast integrator as the server so hydrated gems keep moving without snapshots.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemEventRpcClientSystem))]
    public partial struct GemClientMotionSystem : ISystem
    {
        /// <summary>Integrates velocity + tumble, then wraps XZ.</summary>
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            bool haveMap = ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH);
            if (!haveMap &&
                SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
                haveMap = true;
                ToroidalMapEcs.SetMapSize(mapW, mapH);
            }

            foreach (var (kinematics, transform, entity) in SystemAPI
                         .Query<RefRW<GemKinematics>, RefRW<LocalTransform>>()
                         .WithAll<GemTag, ClientSeedHydratedGem>()
                         .WithEntityAccess())
            {
                var kin = kinematics.ValueRO;
                bool underTractor = false;
                bool hasMotion = SystemAPI.HasComponent<GemMotionState>(entity);
                GemMotionState motionRo = default;
                if (hasMotion)
                {
                    motionRo = SystemAPI.GetComponent<GemMotionState>(entity);
                    underTractor = motionRo.Phase == GemMotionState.PhaseTractor &&
                                   motionRo.TractorShipId != 0;
                    if (motionRo.Phase == GemMotionState.PhaseTractor && motionRo.TractorShipId == 0)
                    {
                        motionRo.Phase = GemMotionState.PhaseCoast;
                        SystemAPI.SetComponent(entity, motionRo);
                    }
                }

                var lt = transform.ValueRO;
                float3 pos = lt.Position;
                quaternion rot = lt.Rotation;
                float3 vel = kin.Velocity;
                float3 ang = kin.AngularVelocity;
                byte phase = hasMotion ? motionRo.Phase : GemMotionState.PhaseCoast;

                GemMotionLogic.IntegrateStep(
                    ref pos,
                    ref rot,
                    ref vel,
                    ref ang,
                    ref phase,
                    underTractor,
                    dt,
                    settings.LinearDamping,
                    settings.AngularDamping,
                    settings.StopSpeedThreshold,
                    mapW,
                    mapH,
                    haveMap);

                lt.Position = pos;
                lt.Rotation = rot;
                transform.ValueRW = lt;
                kinematics.ValueRW = new GemKinematics { Velocity = vel, AngularVelocity = ang };

                if (!hasMotion || underTractor)
                    continue;
                if (motionRo.Phase == phase)
                    continue;
                motionRo.Phase = phase;
                SystemAPI.SetComponent(entity, motionRo);
            }
        }
    }

    /// <summary>
    /// Client: expire hydrated gems after the same lifetime the server uses.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemClientMotionSystem))]
    public partial struct GemClientLifetimeSystem : ISystem
    {
        /// <summary>Destroys local gems whose elapsed life exceeds settings.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();
            float lifetime = settings.GemLifetimeSeconds;
            float now = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);
            var em = state.EntityManager;
            var expire = new NativeList<int>(8, Allocator.Temp);

            foreach (var gemState in SystemAPI
                         .Query<RefRO<GemState>>()
                         .WithAll<GemTag, ClientSeedHydratedGem>())
            {
                if (gemState.ValueRO.IsConsumed)
                    continue;
                float spawnTime = gemState.ValueRO.SpawnServerTime;
                if (spawnTime <= 0f)
                    continue;
                if (now - spawnTime < lifetime)
                    continue;
                expire.Add(gemState.ValueRO.SpawnId);
            }

            for (int i = 0; i < expire.Length; i++)
                ClientLocalGemSpawn.Consume(em, expire[i]);
            expire.Dispose();
        }
    }

    /// <summary>
    /// Server + client: strip leftover NetCode ghost identity from Instantiated gem prefabs
    /// so GhostSend / GhostUpdate never own event-hydrated crystals.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSendSystem))]
    public partial struct GemLocalGhostStripSystem : ISystem
    {
        EntityQuery _ghostedGems;

        /// <summary>Caches gems that still carry <see cref="GhostInstance"/>.</summary>
        public void OnCreate(ref SystemState state)
        {
            _ghostedGems = state.GetEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GhostInstance>());
        }

        /// <summary>Strips ghost identity from any Instantiated gem prefab leftovers.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_ghostedGems.IsEmptyIgnoreFilter)
                return;

            var em = state.EntityManager;
            var entities = _ghostedGems.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                ClientLocalMapBodySpawn.StripGhostNetworking(em, entities[i]);
            entities.Dispose();
        }
    }
}
