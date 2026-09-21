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
            public GemVisualTint Tint;
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
                    Tint = (GemVisualTint)r.IsBonus,
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

            bool haveNetworkClock = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(em, out float now);
            if (gemPrefab != Entity.Null && haveNetworkClock)
            {
                ClientLocalGemSpawn.FlushDeferred(em, gemPrefab, now);

                for (int i = 0; i < catchUps.Length; i++)
                    ClientLocalGemSpawn.SpawnFromCatchUp(em, gemPrefab, catchUps[i]);

                for (int i = 0; i < bursts.Length; i++)
                {
                    var b = bursts[i];
                    float elapsed = math.max(0f, now - b.SpawnServerTime);
                    ClientLocalGemSpawn.SpawnBurst(
                        em, gemPrefab, b.Origin, b.Remaining, b.Seed, b.SpawnServerTime, b.Tint, elapsed);
                }

                for (int i = 0; i < spawns.Length; i++)
                {
                    var recipe = spawns[i].Recipe;
                    float elapsed = math.max(0f, now - recipe.SpawnServerTime);
                    ClientLocalGemSpawn.SpawnFromRecipe(em, gemPrefab, recipe, elapsed);
                }
            }
            else if (gemPrefab != Entity.Null)
            {
                // Clock is not ready — do not IntegrateElapsed against World.Time.
                for (int i = 0; i < catchUps.Length; i++)
                    ClientLocalGemSpawn.DeferCatchUp(catchUps[i]);
                for (int i = 0; i < bursts.Length; i++)
                {
                    var b = bursts[i];
                    ClientLocalGemSpawn.DeferBurst(
                        b.Origin, b.Remaining, b.Seed, b.SpawnServerTime, b.Tint);
                }

                for (int i = 0; i < spawns.Length; i++)
                    ClientLocalGemSpawn.DeferSpawn(spawns[i].Recipe);
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
            if (!GemClientSimStepper.TryGetSteps(state.EntityManager, out _, out _))
                return;

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
            if (!GemClientSimStepper.TryGetSteps(state.EntityManager, out int steps, out float dt))
                return;

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

                for (int s = 0; s < steps; s++)
                {
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
                }

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

            GemClientSimStepper.Commit(state.EntityManager);
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
    /// Client: hide crystals the local hull has been overlapping long enough that a real
    /// server scoop would already have sent <see cref="GemConsumedRpc"/>. Leftover visuals
    /// (lost consume, burst-settings extra, clock-parked pose) stay on screen otherwise.
    /// Cargo-full leftovers are left alone — those are still live server gems.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemClientLifetimeSystem))]
    public partial struct GemClientOrphanHideSystem : ISystem
    {
        const float OrphanOverlapSeconds = 0.5f;
        const float MinGemAgeSeconds = 0.6f;

        NativeHashMap<int, float> _overlap;
        EntityQuery _localShipQuery;

        /// <summary>Allocates the overlap accumulator and caches the local-ship query.</summary>
        public void OnCreate(ref SystemState state)
        {
            _overlap = new NativeHashMap<int, float>(16, Allocator.Persistent);
            _localShipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<LocalPlayerShipTag>(),
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        /// <summary>Releases the overlap accumulator.</summary>
        public void OnDestroy(ref SystemState state)
        {
            if (_overlap.IsCreated)
                _overlap.Dispose();
        }

        /// <summary>Consumes local leftovers after a sustained absorb-zone overlap.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (_overlap.IsCreated)
                    _overlap.Clear();
                return;
            }

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

            var em = state.EntityManager;
            if (!TryGetLocalShip(
                    em, _localShipQuery, out Entity shipEntity, out var shipState, out var shipTransform))
            {
                _overlap.Clear();
                return;
            }

            if (shipState.IsDead || shipState.AwaitingTeamSelection)
            {
                _overlap.Clear();
                return;
            }

            if (shipState.GemCapacity - shipState.CurrentGems <= 0.001f)
            {
                _overlap.Clear();
                return;
            }

            int shipNetworkId = 0;
            if (em.HasComponent<GhostOwner>(shipEntity))
                shipNetworkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;

            float now = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                em, SystemAPI.Time.ElapsedTime);
            float dt = SystemAPI.Time.DeltaTime;
            var pickupSettings = TractorBeamSettingsCache.ResolveOrDefault();
            bool hasWings = em.HasBuffer<ShipWingTractorBeamElement>(shipEntity);
            DynamicBuffer<ShipWingTractorBeamElement> wings = default;
            if (hasWings)
                wings = em.GetBuffer<ShipWingTractorBeamElement>(shipEntity);

            var hide = new NativeList<int>(4, Allocator.Temp);
            var seen = new NativeHashSet<int>(16, Allocator.Temp);

            foreach (var (gemState, gemTransform) in SystemAPI
                         .Query<RefRO<GemState>, RefRO<LocalTransform>>()
                         .WithAll<GemTag, ClientSeedHydratedGem>())
            {
                int spawnId = gemState.ValueRO.SpawnId;
                if (spawnId == 0 || gemState.ValueRO.IsConsumed)
                    continue;

                seen.Add(spawnId);
                if (now - gemState.ValueRO.SpawnServerTime < MinGemAgeSeconds)
                    continue;
                if (GemSelfPickupBlock.IsPickupBlockedForShip(gemState.ValueRO, shipNetworkId, now))
                {
                    _overlap.Remove(spawnId);
                    continue;
                }

                if (!IsInsideAbsorbZone(
                        em,
                        shipEntity,
                        shipTransform,
                        gemTransform.ValueRO.Position,
                        gemState.ValueRO,
                        hasWings,
                        wings,
                        pickupSettings,
                        mapW,
                        mapH))
                {
                    _overlap.Remove(spawnId);
                    continue;
                }

                float accum = 0f;
                if (_overlap.TryGetValue(spawnId, out float prev))
                    accum = prev;
                accum += dt;
                _overlap[spawnId] = accum;
                if (accum >= OrphanOverlapSeconds)
                    hide.Add(spawnId);
            }

            if (_overlap.Count > seen.Count)
            {
                var stale = new NativeList<int>(4, Allocator.Temp);
                foreach (var kv in _overlap)
                {
                    if (!seen.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Length; i++)
                    _overlap.Remove(stale[i]);
                stale.Dispose();
            }

            for (int i = 0; i < hide.Length; i++)
            {
                _overlap.Remove(hide[i]);
                ClientLocalGemSpawn.Consume(em, hide[i]);
            }

            hide.Dispose();
            seen.Dispose();
        }

        static bool TryGetLocalShip(
            EntityManager em,
            EntityQuery localShipQuery,
            out Entity shipEntity,
            out ShipState shipState,
            out LocalTransform shipTransform)
        {
            shipEntity = Entity.Null;
            shipState = default;
            shipTransform = default;
            if (localShipQuery.IsEmptyIgnoreFilter)
                return false;

            shipEntity = localShipQuery.GetSingletonEntity();
            shipState = em.GetComponentData<ShipState>(shipEntity);
            shipTransform = em.GetComponentData<LocalTransform>(shipEntity);
            return true;
        }

        static bool IsInsideAbsorbZone(
            EntityManager em,
            Entity shipEntity,
            in LocalTransform shipTransform,
            float3 gemPos,
            in GemState gemState,
            bool hasWings,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            TractorBeamSettings pickupSettings,
            float mapW,
            float mapH)
        {
            if (hasWings)
            {
                float collectRadius = GemCollectMath.ResolveWingCollectRadius(
                    pickupSettings, gemState.Value, gemState.Size)
                    + CardEffectQuery.GetValue(em, shipEntity, CardEffectKind.GemPickupRadiusAdd);
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wi]);
                    if (GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH) <= collectRadius)
                        return true;
                }

                if (!pickupSettings.AlsoUseHullPickupWithWings)
                    return false;
            }

            float hullRange = GemCollectMath.ResolveHullCollectRadius(
                pickupSettings, gemState.Value, gemState.Size, shipTransform.Scale)
                + CardEffectQuery.GetValue(em, shipEntity, CardEffectKind.GemPickupRadiusAdd);
            return GemTractorBeamMath.ToroidalDistance(gemPos, shipTransform.Position, mapW, mapH) <=
                   hullRange;
        }
    }

    /// <summary>
    /// Client: destroy leftover ghost-prefab gems that never went through
    /// <see cref="ClientLocalGemSpawn"/>. Those crystals render but have no server pickup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemEventRpcClientSystem))]
    public partial struct GemClientGhostLeftoverPurgeSystem : ISystem
    {
        /// <summary>Destroys non-hydrated gem Instantiates (keeps the baked prefab).</summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var purge = new NativeList<int>(4, Allocator.Temp);
            var purgeEntities = new NativeList<Entity>(4, Allocator.Temp);

            foreach (var (gemState, gemEntity) in SystemAPI
                         .Query<RefRO<GemState>>()
                         .WithAll<GemTag>()
                         .WithNone<ClientSeedHydratedGem, Prefab>()
                         .WithEntityAccess())
            {
                int spawnId = gemState.ValueRO.SpawnId;
                if (spawnId != 0)
                    purge.Add(spawnId);
                else
                    purgeEntities.Add(gemEntity);
            }

            for (int i = 0; i < purge.Length; i++)
                ClientLocalGemSpawn.Consume(em, purge[i]);
            for (int i = 0; i < purgeEntities.Length; i++)
            {
                if (em.Exists(purgeEntities[i]))
                    em.DestroyEntity(purgeEntities[i]);
            }

            purge.Dispose();
            purgeEntities.Dispose();
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
