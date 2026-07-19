using TitanOrbit.Core;
using TitanOrbit.Diagnostics;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Tunable timing for people transport orbit dwell and hull interaction radius.
    /// Used by <see cref="PeopleTransportDispatchSystem"/> and simulation systems.
    /// </summary>
    public static class PeopleTransportConstants
    {
        /// <summary>Seconds a ship must dwell in orbit ring before people load/unload begins.</summary>
        public const float OrbitDwellBeforeTransferSeconds = 2f;

        /// <summary>Multiplier on base transfer rate (1 = designer default).</summary>
        public const float TransferSpeedMultiplier = 1f;

        /// <summary>Fallback hull radius when ship collider scale is unavailable.</summary>
        public const float DefaultShipHullRadius = 1f;

        /// <summary>
        /// Locks Y to the flat map plane. Positions stay unbounded (ships do not wrap);
        /// delivery/range still use toroidal distance helpers.
        /// </summary>
        public static void WriteTransform(ref LocalTransform transform, float3 position, float mapW, float mapH)
        {
            _ = mapW;
            _ = mapH;
            position.y = 0f;
            transform.Position = position;
        }

        /// <summary>Planar write using current position (map size unused — signature kept for call sites).</summary>
        public static void WriteTransform(ref LocalTransform transform, float3 position)
        {
            WriteTransform(ref transform, position, ToroidalMapEcs.MapWidth, ToroidalMapEcs.MapHeight);
        }
    }

    /// <summary>Outcome when a transport unloads population at a planet (friendly, drain, or capture).</summary>
    public enum PeopleUnloadOutcome : byte
    {
        Friendly = 0,
        HostileDrain = 1,
        Captured = 2,
    }

    /// <summary>
    /// Server: after a ship dwells in an orbit ring, dispatches incremental people load/unload.
    /// Spawns server-only transport entities (<see cref="PeopleTransportTag"/>) plus a cosmetic
    /// <see cref="PeopleTransportSpawnRpc"/> for client VFX. Does not Instantiate the old
    /// PeopleTransportGhost — that flooded Windows GhostSpawn and kicked clients to the main menu.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipPhysicsDriveSystem))]
    public partial struct PeopleTransportDispatchSystem : ISystem
    {
        /// <summary>
        /// Each frame: dwell timers → load/unload chunks → CreateEntity transports + VFX RPC.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Fixed-step dispatch ---
            // [TITAN-ORBIT] Server-only CreateEntity (no ghost Instantiates). Windows clients
            // dropped to the main menu when orbit spawned PeopleTransportGhost floods.
            float dt = SystemAPI.Time.DeltaTime;
            float now = (float)SystemAPI.Time.ElapsedTime;
            float mapW = 1000f;
            float mapH = 1000f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }

            var planetById = new NativeHashMap<int, Entity>(32, Allocator.Temp);
            var planetStateById = new NativeHashMap<int, PlanetState>(32, Allocator.Temp);
            var planetTransformById = new NativeHashMap<int, LocalTransform>(32, Allocator.Temp);
            foreach (var (planet, transform, entity) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>()
                         .WithEntityAccess())
            {
                int id = planet.ValueRO.PlanetId;
                planetById[id] = entity;
                planetStateById[id] = planet.ValueRO;
                planetTransformById[id] = transform.ValueRO;
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipState, shipInput, orbit, moonDock, transferState, shipTransform, shipEntity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipInput>, RefRO<ShipOrbitState>, RefRO<ShipMoonDockState>,
                             RefRW<ShipPeopleTransferState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                ref var transfer = ref transferState.ValueRW;
                float3 shipPos = shipTransform.ValueRO.Position;
                if (!orbit.ValueRO.InOrbitRing || orbit.ValueRO.OrbitPlanetId == 0)
                {
                    transfer.OrbitDwellSeconds = 0f;
                    transfer.LoadAccumulator = 0f;
                    transfer.UnloadAccumulator = 0f;
                    continue;
                }

                int orbitPlanetId = orbit.ValueRO.OrbitPlanetId;
                if (!CanTransferPeople(
                        in orbit.ValueRO, in shipInput.ValueRO, in moonDock.ValueRO, shipPos,
                        orbitPlanetId, planetTransformById, planetStateById, mapW, mapH))
                {
                    transfer.OrbitDwellSeconds = 0f;
                    transfer.LoadAccumulator = 0f;
                    transfer.UnloadAccumulator = 0f;
                    continue;
                }

                if (orbitPlanetId != transfer.LastOrbitPlanetId)
                {
                    transfer.LastOrbitPlanetId = orbitPlanetId;
                    transfer.OrbitDwellSeconds = 0f;
                    transfer.LoadAccumulator = 0f;
                    transfer.UnloadAccumulator = 0f;
                }

                transfer.OrbitDwellSeconds += dt;
                if (transfer.OrbitDwellSeconds < PeopleTransportConstants.OrbitDwellBeforeTransferSeconds)
                    continue;

                if (!planetById.TryGetValue(orbitPlanetId, out var planetEntity))
                    continue;

                var planetState = planetStateById[orbitPlanetId];
                var planetTransform = planetTransformById[orbitPlanetId];
                float planetSize = math.max(0.5f, planetTransform.Scale);
                int maxPop = PlanetPopulationMath.GetMaxPopulation(planetSize, planetState.PlanetLevel);
                int halfCap = math.max(1, maxPop / 2);

                int shipLevel = math.max(1, shipState.ValueRO.ShipLevel);
                int planetLevel = math.max(1, planetState.PlanetLevel);
                float loadChunk = math.max(1f, math.min(shipLevel, planetLevel));
                float unloadChunk = math.max(1f, shipLevel);
                float loadStep = loadChunk * dt * PeopleTransportConstants.TransferSpeedMultiplier;
                float unloadStep = unloadChunk * dt * PeopleTransportConstants.TransferSpeedMultiplier;
                int shipNetworkId = GetShipNetworkId(ref state, shipEntity);
                if (shipNetworkId == 0)
                    continue;
                float3 planetPos = planetTransform.Position;
                float shipHullRadius = PeopleTransportMath.GetShipHullRadius(shipTransform.ValueRO.Scale);
                bool friendly = shipState.ValueRO.Team != TeamId.None && planetState.Ownership == shipState.ValueRO.Team;

                if (friendly)
                {
                    if (planetState.Population < halfCap)
                    {
                        transfer.UnloadAccumulator += unloadStep;
                        int availablePeople = shipState.ValueRO.CurrentPeople - (int)transfer.PeopleInTransit;
                        if (transfer.UnloadAccumulator >= unloadChunk && availablePeople > 0)
                        {
                            int room = halfCap - planetState.Population;
                            int send = (int)math.min(unloadChunk, math.min(availablePeople, room));
                            if (send > 0 && TryDispatchUnload(
                                    ref ecb, ref shipState.ValueRW, ref planetState, ref transfer,
                                    send, shipNetworkId, planetState.PlanetId, shipState.ValueRO.Team,
                                    shipPos, planetPos, planetSize, shipHullRadius, mapW, mapH, now))
                            {
                                transfer.UnloadAccumulator = 0f;
                                planetStateById[planetState.PlanetId] = planetState;
                                ecb.SetComponent(planetEntity, planetState);
                            }
                        }
                    }
                    else
                    {
                        transfer.LoadAccumulator += loadStep;
                        if (transfer.LoadAccumulator >= loadChunk)
                        {
                            int space = shipState.ValueRO.PeopleCapacity - shipState.ValueRO.CurrentPeople -
                                        (int)transfer.PeopleInTransit;
                            int surplus = planetState.Population - halfCap;
                            int send = (int)math.min(loadChunk, math.min(space, surplus));
                            if (send > 0 && TryDispatchLoad(
                                    ref ecb, ref shipState.ValueRW, ref planetState,
                                    ref transfer, send, shipNetworkId, planetState.PlanetId, shipState.ValueRO.Team,
                                    shipPos, planetPos, planetSize, mapW, mapH, now))
                            {
                                transfer.LoadAccumulator = 0f;
                                planetStateById[planetState.PlanetId] = planetState;
                                ecb.SetComponent(planetEntity, planetState);
                            }
                        }
                    }
                }
                else
                {
                    transfer.UnloadAccumulator += unloadStep;
                    int availablePeople = shipState.ValueRO.CurrentPeople - (int)transfer.PeopleInTransit;
                    if (transfer.UnloadAccumulator >= unloadChunk && availablePeople > 0)
                    {
                        int send = (int)math.min(unloadChunk, availablePeople);
                        if (TryDispatchUnload(
                                ref ecb, ref shipState.ValueRW, ref planetState, ref transfer,
                                send, shipNetworkId, planetState.PlanetId, shipState.ValueRO.Team,
                                shipPos, planetPos, planetSize, shipHullRadius, mapW, mapH, now))
                        {
                            transfer.UnloadAccumulator = 0f;
                        }
                    }
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            planetById.Dispose();
            planetStateById.Dispose();
            planetTransformById.Dispose();
        }

        static bool CanTransferPeople(
            in ShipOrbitState orbit,
            in ShipInput input,
            in ShipMoonDockState moonDock,
            float3 shipPos,
            int orbitPlanetId,
            NativeHashMap<int, LocalTransform> planetTransformById,
            NativeHashMap<int, PlanetState> planetStateById,
            float mapW,
            float mapH)
        {
            if (!orbit.InOrbitRing || orbitPlanetId == 0 || orbit.OrbitPlanetId != orbitPlanetId)
                return false;
            if (input.Thrust || input.Fire.IsSet)
                return false;
            if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.01f)
                return false;

            if (!planetTransformById.TryGetValue(orbitPlanetId, out var planetTransform) ||
                !planetStateById.TryGetValue(orbitPlanetId, out var planetState))
                return false;

            float planetSize = math.max(0.5f, planetTransform.Scale);
            PlanetOrbitMath.GetRingRadiiWorld(planetSize, planetState.PlanetLevel, out float inner, out float outer, out _);
            float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetTransform.Position, mapW, mapH);
            return PlanetOrbitMath.IsInOrbitRing(dist, inner, outer);
        }

        static int GetShipNetworkId(ref SystemState state, Entity shipEntity)
        {
            // --- Compute value ---
            if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                return state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            return 0;
        }

        static bool TryDispatchLoad(
            ref EntityCommandBuffer ecb,
            ref ShipState ship,
            ref PlanetState planet,
            ref ShipPeopleTransferState transfer,
            int amount,
            int shipNetworkId,
            int planetId,
            TeamId team,
            float3 shipPos,
            float3 planetPos,
            float planetSize,
            float mapW,
            float mapH,
            float now)
        {
            float3 spawnPos = PeopleTransportMath.GetPlanetSurfaceSpawnToward(planetPos, planetSize, shipPos, mapW, mapH);
            float3 targetPos = shipPos;
            float3 loadDir = ToroidalMapEcs.ToroidalDirection(spawnPos, targetPos, mapW, mapH);
            spawnPos += loadDir * 0.2f;
            SpawnTransport(ref ecb, spawnPos, targetPos, amount, shipNetworkId, planetId, 0,
                shipNetworkId, true, team, now, mapW, mapH);
            planet.Population -= amount;
            transfer.PeopleInTransit += amount;
            return true;
        }

        /// <summary>
        /// Spawns an unload transport. Does NOT change <see cref="ShipState.CurrentPeople"/> yet —
        /// people stay reserved in <see cref="ShipPeopleTransferState.PeopleInTransit"/> until the
        /// sphere reaches the planet (or is shot down). That keeps floating −1 in sync with arrival.
        /// </summary>
        static bool TryDispatchUnload(
            ref EntityCommandBuffer ecb,
            ref ShipState ship,
            ref PlanetState planet,
            ref ShipPeopleTransferState transfer,
            int amount,
            int shipNetworkId,
            int planetId,
            TeamId team,
            float3 shipPos,
            float3 planetPos,
            float planetSize,
            float shipHullRadius,
            float mapW,
            float mapH,
            float now)
        {
            _ = ship;
            _ = planet;
            // Target = planet surface; spawn = planet-facing ship flank (not ship forward / nose).
            float3 targetPos = PeopleTransportMath.GetPlanetSurfaceToward(planetPos, planetSize, shipPos, mapW, mapH);
            float3 spawnPos = PeopleTransportMath.GetShipUnloadSpawnToward(
                shipPos, shipHullRadius, planetPos, mapW, mapH);
            SpawnTransport(ref ecb, spawnPos, targetPos, amount, 0, planetId,
                planetId, shipNetworkId, false, team, now, mapW, mapH);
            // Reserve only — CurrentPeople drops on delivery / destruction (floating text timing).
            transfer.PeopleInTransit += amount;
            return true;
        }

        /// <summary>
        /// Spawns a server-only transport entity (delivery + combat) and notifies clients for VFX.
        /// [TITAN-ORBIT] Not a ghost — Instantiating PeopleTransportGhost on orbit flooded Windows
        /// GhostSpawn (Instantiates=1/frame) and kicked clients back to the main menu.
        /// </summary>
        static void SpawnTransport(
            ref EntityCommandBuffer ecb,
            float3 spawnPos,
            float3 targetPos,
            int amount,
            int targetShipNetworkId,
            int sourcePlanetId,
            int targetPlanetId,
            int sourceShipNetworkId,
            bool isLoad,
            TeamId team,
            float now,
            float mapW,
            float mapH)
        {
            float3 dir = ToroidalMapEcs.ToroidalDirection(spawnPos, targetPos, mapW, mapH);
            float cruise = PeopleTransportMath.ComputeCruiseSpeed(spawnPos, targetPos, isLoad, mapW, mapH);
            // Start below cruise so the hop eases in (legacy magnet ramped with MoveTowards).
            float initialMul = isLoad ? 0.55f : 0.45f;
            float3 velocity = dir * cruise * initialMul;
            float scale = PeopleTransportMath.GetVisualScaleMultiplier(amount) * 0.25f;
            byte isLoadByte = (byte)(isLoad ? 1 : 0);
            byte teamByte = (byte)team;

            // --- Server-only sim entity (no GhostAuthoring / no client Instantiates) ---
            Entity transport = ecb.CreateEntity();
            ecb.AddComponent<PeopleTransportTag>(transport);
            ecb.AddComponent(transport, LocalTransform.FromPositionRotationScale(spawnPos, quaternion.identity, scale));
            ecb.AddComponent(transport, new PeopleTransportState
            {
                Amount = amount,
                Health = PeopleTransportMath.ComputeMaxHealth(amount),
                Velocity = velocity,
                SpawnPosition = spawnPos,
                SpawnTime = now,
                CruiseSpeed = cruise,
                TargetShipNetworkId = targetShipNetworkId,
                SourcePlanetId = sourcePlanetId,
                TargetPlanetId = targetPlanetId,
                SourceShipNetworkId = sourceShipNetworkId,
                IsLoad = isLoadByte,
                Team = teamByte,
            });

            // --- Client VFX (PeopleTransportVfxDriver) ---
            uint sequence = PeopleTransportVfxBridge.NextSequence();
            float3 bakedTarget = targetPos;
            bakedTarget.y = 0f;
            var vfxReq = new PeopleTransportVfxBridge.SpawnRequest
            {
                Sequence = sequence,
                SpawnPosition = spawnPos,
                TargetPosition = bakedTarget,
                Velocity = velocity,
                CruiseSpeed = cruise,
                Amount = amount,
                TargetShipNetworkId = targetShipNetworkId,
                IsLoad = isLoadByte,
                Team = teamByte,
            };
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                PeopleTransportVfxBridge.TryEnqueue(vfxReq);

            // --- Broadcast spawn RPC (62B layout with TargetPosition — must match headless) ---
            // #region agent log
            AgentDebugSessionLog.Write("pre-fix", "A", "PeopleTransportDispatchSystem.SpawnTransport",
                "server_sending_people_transport_rpc",
                "{\"seq\":" + sequence + ",\"isLoad\":" + isLoadByte +
                ",\"shipNetId\":" + targetShipNetworkId + ",\"amount\":" + amount + "}");
            // #endregion
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new PeopleTransportSpawnRpc
            {
                Sequence = sequence,
                SpawnPosition = spawnPos,
                TargetPosition = bakedTarget,
                Velocity = velocity,
                CruiseSpeed = cruise,
                Amount = amount,
                TargetShipNetworkId = targetShipNetworkId,
                SourcePlanetId = sourcePlanetId,
                TargetPlanetId = targetPlanetId,
                IsLoad = isLoadByte,
                Team = teamByte,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }

    /// <summary>Magnet-steers in-flight people transports and applies load/unload on arrival.</summary>
    /// <summary>
    /// Server authoritative simulation of people-transport projectiles: movement, homing,
    /// unload at planets/ships, capture/drain outcomes. Runs after dispatch and bullets.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportDispatchSystem))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    public partial struct PeopleTransportSimulationSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            float dt = SystemAPI.Time.DeltaTime;
            float now = (float)SystemAPI.Time.ElapsedTime;
            float mapW = 1000f;
            float mapH = 1000f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }

            var planetById = new NativeHashMap<int, Entity>(32, Allocator.Temp);
            var planetStateById = new NativeHashMap<int, PlanetState>(32, Allocator.Temp);
            var planetTransformById = new NativeHashMap<int, LocalTransform>(32, Allocator.Temp);
            foreach (var (planet, transform, entity) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>()
                         .WithEntityAccess())
            {
                int id = planet.ValueRO.PlanetId;
                planetById[id] = entity;
                planetStateById[id] = planet.ValueRO;
                planetTransformById[id] = transform.ValueRO;
            }

            var shipByNetworkId = new NativeHashMap<int, Entity>(32, Allocator.Temp);
            var shipStateByNetworkId = new NativeHashMap<int, ShipState>(32, Allocator.Temp);
            var shipTransformByNetworkId = new NativeHashMap<int, LocalTransform>(32, Allocator.Temp);
            var shipMoonDockByNetworkId = new NativeHashMap<int, ShipMoonDockState>(32, Allocator.Temp);
            var shipInputByNetworkId = new NativeHashMap<int, ShipInput>(32, Allocator.Temp);
            var shipOrbitByNetworkId = new NativeHashMap<int, ShipOrbitState>(32, Allocator.Temp);
            foreach (var (owner, shipState, shipInput, shipOrbit, moonDock, transform, entity) in SystemAPI
                         .Query<RefRO<GhostOwner>, RefRO<ShipState>, RefRO<ShipInput>, RefRO<ShipOrbitState>,
                             RefRO<ShipMoonDockState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                int id = owner.ValueRO.NetworkId;
                if (id == 0) continue;
                shipByNetworkId[id] = entity;
                shipStateByNetworkId[id] = shipState.ValueRO;
                shipTransformByNetworkId[id] = transform.ValueRO;
                shipMoonDockByNetworkId[id] = moonDock.ValueRO;
                shipInputByNetworkId[id] = shipInput.ValueRO;
                shipOrbitByNetworkId[id] = shipOrbit.ValueRO;
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (transport, transform, entity) in SystemAPI
                         .Query<RefRW<PeopleTransportState>, RefRW<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                ref var t = ref transport.ValueRW;
                if (t.Health <= 0f && t.Amount > 0f)
                    t.Health = PeopleTransportMath.ComputeMaxHealth(t.Amount);
                float elapsed = now - t.SpawnTime;
                float3 myPos = transform.ValueRO.Position;
                myPos.y = 0f;
                bool isLoad = t.IsLoad != 0;
                var team = (TeamId)t.Team;

                StepTransportMotion(
                    ref t, ref transform.ValueRW, isLoad, myPos, dt, mapW, mapH,
                    shipStateByNetworkId, shipTransformByNetworkId, shipMoonDockByNetworkId,
                    shipInputByNetworkId, shipOrbitByNetworkId,
                    planetTransformById, planetStateById);
                myPos = transform.ValueRO.Position;
                myPos.y = 0f;

                if (isLoad)
                {
                    if (!planetStateById.ContainsKey(t.SourcePlanetId) ||
                        !planetTransformById.ContainsKey(t.SourcePlanetId))
                    {
                        ecb.DestroyEntity(entity);
                        continue;
                    }

                    if (!shipByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipEntity) ||
                        !shipStateByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipState) ||
                        !shipTransformByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipTransform))
                    {
                        var sourcePlanetOnly = planetStateById[t.SourcePlanetId];
                        var sourceTransformOnly = planetTransformById[t.SourcePlanetId];
                        float sourceSizeOnly = math.max(0.5f, sourceTransformOnly.Scale);
                        sourcePlanetOnly.Population = math.min(
                            sourcePlanetOnly.Population + (int)t.Amount,
                            PlanetPopulationMath.GetMaxPopulation(sourceSizeOnly, sourcePlanetOnly.PlanetLevel));
                        planetStateById[t.SourcePlanetId] = sourcePlanetOnly;
                        ecb.SetComponent(planetById[t.SourcePlanetId], sourcePlanetOnly);
                        ecb.DestroyEntity(entity);
                        continue;
                    }

                    shipMoonDockByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipMoonDock);
                    planetTransformById.TryGetValue(t.SourcePlanetId, out var sourceTransform);
                    planetStateById.TryGetValue(t.SourcePlanetId, out var sourcePlanetState);
                    float sourcePlanetSize = math.max(0.5f, sourceTransform.Scale);
                    bool eligible = IsShipEligibleForLoad(
                        shipState, shipInputByNetworkId[t.TargetShipNetworkId], shipOrbitByNetworkId[t.TargetShipNetworkId],
                        shipMoonDock, shipTransform.Position, sourceTransform.Position, sourcePlanetSize,
                        sourcePlanetState.PlanetLevel, t.SourcePlanetId, mapW, mapH);

                    if (!eligible)
                    {
                        if (PeopleTransportMath.CanCompleteReturnToSourcePlanet(
                                myPos, t.SpawnPosition, sourceTransform.Position, sourcePlanetSize, elapsed, mapW, mapH))
                        {
                            var sourcePlanet = planetStateById[t.SourcePlanetId];
                            ReturnLoadToPlanet(ref state, ref sourcePlanet, shipEntity, t.Amount, sourcePlanetSize);
                            planetStateById[t.SourcePlanetId] = sourcePlanet;
                            ecb.SetComponent(planetById[t.SourcePlanetId], sourcePlanet);
                            ecb.DestroyEntity(entity);
                        }

                        continue;
                    }

                    float shipRadius = PeopleTransportMath.GetShipHullRadius(shipTransform.Scale);
                    float3 shipCenter = shipTransform.Position;
                    if (PeopleTransportMath.CanDeliverLoadToShip(myPos, shipCenter, shipRadius, mapW, mapH) &&
                        PeopleTransportMath.HasBriefTravelBeforeLoad(myPos, t.SpawnPosition, elapsed, mapW, mapH))
                    {
                        DeliverLoad(ref state, shipEntity, ref shipState, t.Amount, team);
                        shipStateByNetworkId[t.TargetShipNetworkId] = shipState;
                        ecb.SetComponent(shipEntity, shipState);
                        ecb.DestroyEntity(entity);
                        continue;
                    }
                }
                else
                {
                    if (!planetStateById.TryGetValue(t.TargetPlanetId, out var planetState) ||
                        !planetTransformById.TryGetValue(t.TargetPlanetId, out var planetTransform))
                    {
                        // Planet gone — release reserved outbound crew back to the ship.
                        if (shipByNetworkId.TryGetValue(t.SourceShipNetworkId, out var orphanShipEntity) &&
                            shipStateByNetworkId.TryGetValue(t.SourceShipNetworkId, out var orphanShipState) &&
                            state.EntityManager.HasComponent<ShipPeopleTransferState>(orphanShipEntity))
                        {
                            var transfer = state.EntityManager.GetComponentData<ShipPeopleTransferState>(orphanShipEntity);
                            transfer.PeopleInTransit = math.max(0f, transfer.PeopleInTransit - t.Amount);
                            state.EntityManager.SetComponentData(orphanShipEntity, transfer);
                            _ = orphanShipState;
                        }

                        ecb.DestroyEntity(entity);
                        continue;
                    }

                    float planetSize = math.max(0.5f, planetTransform.Scale);

                    if (PeopleTransportMath.CanCompleteUnloadDelivery(
                            myPos, t.SpawnPosition, planetTransform.Position, planetSize, elapsed, mapW, mapH))
                    {
                        // Ship inventory drops here (not at dispatch) so floating −1 matches surface arrival.
                        if (shipByNetworkId.TryGetValue(t.SourceShipNetworkId, out var unloadShipEntity) &&
                            shipStateByNetworkId.TryGetValue(t.SourceShipNetworkId, out var unloadShipState))
                        {
                            CommitOutboundPeople(ref state, unloadShipEntity, ref unloadShipState, t.Amount);
                            shipStateByNetworkId[t.SourceShipNetworkId] = unloadShipState;
                            ecb.SetComponent(unloadShipEntity, unloadShipState);
                        }

                        var planetEntity = planetById[t.TargetPlanetId];
                        var unloadOutcome = DeliverUnload(ref planetState, t.Amount, team, planetTransform, planetSize);
                        planetStateById[t.TargetPlanetId] = planetState;
                        ecb.SetComponent(planetEntity, planetState);
                        if (state.EntityManager.HasComponent<PlanetGrowthState>(planetEntity))
                        {
                            var growth = state.EntityManager.GetComponentData<PlanetGrowthState>(planetEntity);
                            growth.FractionalPopulation = math.max(0f, planetState.Population);
                            if (unloadOutcome == PeopleUnloadOutcome.Captured)
                                growth.LastHostilePopulationImpactServerTime = 0f;
                            else if (unloadOutcome == PeopleUnloadOutcome.HostileDrain)
                                growth.LastHostilePopulationImpactServerTime = now;
                            ecb.SetComponent(planetEntity, growth);
                        }

                        ecb.DestroyEntity(entity);
                        continue;
                    }
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            planetById.Dispose();
            planetStateById.Dispose();
            planetTransformById.Dispose();
            shipByNetworkId.Dispose();
            shipStateByNetworkId.Dispose();
            shipTransformByNetworkId.Dispose();
            shipMoonDockByNetworkId.Dispose();
            shipInputByNetworkId.Dispose();
            shipOrbitByNetworkId.Dispose();
        }

        internal static void StepTransportMotion(
            ref PeopleTransportState transport,
            ref LocalTransform transform,
            bool isLoad,
            float3 myPos,
            float dt,
            float mapW,
            float mapH,
            NativeHashMap<int, ShipState> shipStateByNetworkId,
            NativeHashMap<int, LocalTransform> shipTransformByNetworkId,
            NativeHashMap<int, ShipMoonDockState> shipMoonDockByNetworkId,
            NativeHashMap<int, ShipInput> shipInputByNetworkId,
            NativeHashMap<int, ShipOrbitState> shipOrbitByNetworkId,
            NativeHashMap<int, LocalTransform> planetTransformById,
            NativeHashMap<int, PlanetState> planetStateById)
        {
            float3 target = float3.zero;
            bool hasTarget = false;

            if (isLoad &&
                shipTransformByNetworkId.TryGetValue(transport.TargetShipNetworkId, out var shipTransform) &&
                shipStateByNetworkId.TryGetValue(transport.TargetShipNetworkId, out var shipState) &&
                shipInputByNetworkId.TryGetValue(transport.TargetShipNetworkId, out var shipInput) &&
                shipOrbitByNetworkId.TryGetValue(transport.TargetShipNetworkId, out var shipOrbit) &&
                planetTransformById.TryGetValue(transport.SourcePlanetId, out var sourceTransform) &&
                planetStateById.TryGetValue(transport.SourcePlanetId, out var sourcePlanetState))
            {
                float sourcePlanetSize = math.max(0.5f, sourceTransform.Scale);
                shipMoonDockByNetworkId.TryGetValue(transport.TargetShipNetworkId, out var shipMoonDock);
                bool eligible = IsShipEligibleForLoad(
                    shipState, shipInput, shipOrbit, shipMoonDock, shipTransform.Position, sourceTransform.Position,
                    sourcePlanetSize, sourcePlanetState.PlanetLevel, transport.SourcePlanetId, mapW, mapH);

                target = eligible
                    ? PeopleTransportMath.GetShipMagnetTarget(
                        shipTransform.Position,
                        PeopleTransportMath.GetShipHullRadius(shipTransform.Scale),
                        myPos, mapW, mapH)
                    : PeopleTransportMath.GetPlanetSurfaceToward(
                        sourceTransform.Position, sourcePlanetSize, myPos, mapW, mapH);
                hasTarget = true;
            }
            else if (!isLoad &&
                     TryResolvePlanetTransform(transport.TargetPlanetId, transport.SourcePlanetId, planetTransformById,
                         out var unloadPlanetTransform))
            {
                float planetSize = math.max(0.5f, unloadPlanetTransform.Scale);
                target = PeopleTransportMath.GetPlanetSurfaceToward(
                    unloadPlanetTransform.Position, planetSize, myPos, mapW, mapH);
                hasTarget = true;
            }
            else if (isLoad &&
                     planetTransformById.TryGetValue(transport.SourcePlanetId, out var fallbackSourceTransform))
            {
                float planetSize = math.max(0.5f, fallbackSourceTransform.Scale);
                target = PeopleTransportMath.GetPlanetSurfaceToward(
                    fallbackSourceTransform.Position, planetSize, myPos, mapW, mapH);
                hasTarget = true;
            }

            if (!hasTarget)
                return;

            transport.Velocity = PeopleTransportMath.SteerMagnetVelocity(
                myPos, target, transport.Velocity, dt, transport.CruiseSpeed, mapW, mapH);
            myPos += transport.Velocity * dt;
            // [TITAN-ORBIT] Wrap after integrate so transports crossing a seam stay canonical.
            PeopleTransportConstants.WriteTransform(ref transform, myPos, mapW, mapH);
        }

        static bool TryResolvePlanetTransform(
            int targetPlanetId,
            int sourcePlanetId,
            NativeHashMap<int, LocalTransform> planetTransformById,
            out LocalTransform planetTransform)
        {
            if (targetPlanetId != 0 &&
                planetTransformById.TryGetValue(targetPlanetId, out planetTransform))
                return true;

            if (sourcePlanetId != 0 &&
                planetTransformById.TryGetValue(sourcePlanetId, out planetTransform))
                return true;

            planetTransform = default;
            return false;
        }

        internal static bool IsShipEligibleForLoad(
            in ShipState ship,
            in ShipInput input,
            in ShipOrbitState orbit,
            in ShipMoonDockState moonDock,
            float3 shipPos,
            float3 planetPos,
            float planetSize,
            int planetLevel,
            int sourcePlanetId,
            float mapW,
            float mapH)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (input.Thrust || input.Fire.IsSet)
                return false;
            if (!orbit.InOrbitRing || orbit.OrbitPlanetId != sourcePlanetId)
                return false;
            if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.01f)
                return false;

            PlanetOrbitMath.GetRingRadiiWorld(planetSize, planetLevel, out float inner, out float outer, out _);
            float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetPos, mapW, mapH);
            return PlanetOrbitMath.IsInOrbitRing(dist, inner, outer);
        }

        static void DeliverLoad(ref SystemState state, Entity shipEntity, ref ShipState ship, float amount, TeamId team)
        {
            // --- DeliverLoad (arrival) ---
            // CurrentPeople rises here → EcsFloatingCountPresenter shows +N when the sphere is consumed.
            int space = ship.PeopleCapacity - ship.CurrentPeople;
            int toAdd = (int)math.min(amount, space);
            if (toAdd > 0)
                ship.CurrentPeople += toAdd;

            if (state.EntityManager.HasComponent<ShipPeopleTransferState>(shipEntity))
            {
                var transfer = state.EntityManager.GetComponentData<ShipPeopleTransferState>(shipEntity);
                transfer.PeopleInTransit = math.max(0f, transfer.PeopleInTransit - amount);
                state.EntityManager.SetComponentData(shipEntity, transfer);
            }

            LogPeopleEvent("Load", toAdd, team);
        }

        /// <summary>
        /// Removes reserved outbound crew from the ship when an unload arrives or is shot down.
        /// Triggers floating −N via <see cref="ShipState.CurrentPeople"/> delta.
        /// </summary>
        static void CommitOutboundPeople(ref SystemState state, Entity shipEntity, ref ShipState ship, float amount)
        {
            int remove = (int)math.min(amount, ship.CurrentPeople);
            if (remove > 0)
                ship.CurrentPeople -= remove;

            if (state.EntityManager.HasComponent<ShipPeopleTransferState>(shipEntity))
            {
                var transfer = state.EntityManager.GetComponentData<ShipPeopleTransferState>(shipEntity);
                transfer.PeopleInTransit = math.max(0f, transfer.PeopleInTransit - amount);
                state.EntityManager.SetComponentData(shipEntity, transfer);
            }
        }

        static void ReturnLoadToPlanet(
            ref SystemState state,
            ref PlanetState planet,
            Entity shipEntity,
            float amount,
            float planetSize)
        {
            int maxPop = PlanetPopulationMath.GetMaxPopulation(planetSize, planet.PlanetLevel);
            planet.Population = math.min(planet.Population + (int)amount, maxPop);

            if (state.EntityManager.HasComponent<ShipPeopleTransferState>(shipEntity))
            {
                var transfer = state.EntityManager.GetComponentData<ShipPeopleTransferState>(shipEntity);
                transfer.PeopleInTransit = math.max(0f, transfer.PeopleInTransit - amount);
                state.EntityManager.SetComponentData(shipEntity, transfer);
            }
        }

        static PeopleUnloadOutcome DeliverUnload(ref PlanetState planet, float amount, TeamId team, LocalTransform planetTransform, float planetSize)
        {
            // --- DeliverUnload ---
            int maxPop = PlanetPopulationMath.GetMaxPopulation(planetSize, planet.PlanetLevel);
            int moved = (int)amount;

            if (planet.Ownership != TeamId.None && planet.Ownership == team)
            {
                planet.Population = math.min(planet.Population + moved, maxPop);
                LogPeopleEvent("Friendly unload", moved, team);
                return PeopleUnloadOutcome.Friendly;
            }

            planet.Population -= moved;
            if (planet.Population <= 0)
            {
                planet.Ownership = team;
                planet.Population = 0;
                LogPlanetCaptured(planet.PlanetId, team);
                return PeopleUnloadOutcome.Captured;
            }

            LogPeopleEvent("Hostile unload", moved, team);
            return PeopleUnloadOutcome.HostileDrain;
        }

        /// <summary>
        /// Shot down in flight. Load: release inbound reservation only (planet already paid).
        /// Unload: crew are casualties — drop CurrentPeople now (never reach the planet).
        /// </summary>
        [BurstDiscard]
        public static void DestroyFromBulletDamage(ref SystemState state, Entity transportEntity, in PeopleTransportState transport)
        {
            // --- DestroyFromBulletDamage ---
            var em = state.EntityManager;
            if (transport.Amount <= 0f)
            {
                em.DestroyEntity(transportEntity);
                return;
            }

            int shipNetworkId = transport.IsLoad != 0
                ? transport.TargetShipNetworkId
                : transport.SourceShipNetworkId;

            if (shipNetworkId != 0)
            {
                using var shipQuery = em.CreateEntityQuery(
                    ComponentType.ReadOnly<ShipTag>(),
                    ComponentType.ReadOnly<GhostOwner>(),
                    ComponentType.ReadWrite<ShipState>());
                using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                using var ships = shipQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < ships.Length; i++)
                {
                    if (owners[i].NetworkId != shipNetworkId)
                        continue;

                    var shipEntity = ships[i];
                    if (transport.IsLoad != 0)
                    {
                        // Load destroyed: free inbound reservation (never boarded).
                        if (em.HasComponent<ShipPeopleTransferState>(shipEntity))
                        {
                            var transfer = em.GetComponentData<ShipPeopleTransferState>(shipEntity);
                            transfer.PeopleInTransit = math.max(0f, transfer.PeopleInTransit - transport.Amount);
                            em.SetComponentData(shipEntity, transfer);
                        }
                    }
                    else
                    {
                        // Unload destroyed: people die — commit the reserved drop from the ship.
                        var ship = em.GetComponentData<ShipState>(shipEntity);
                        CommitOutboundPeople(ref state, shipEntity, ref ship, transport.Amount);
                        em.SetComponentData(shipEntity, ship);
                    }

                    break;
                }
            }

            em.DestroyEntity(transportEntity);
            LogDestroyedByBullet((int)transport.Amount, (TeamId)transport.Team);
        }

        [BurstDiscard]
        static void LogDestroyedByBullet(int amount, TeamId team)
        {
            UnityEngine.Debug.Log($"[PeopleTransport] Destroyed by hostile fire ({amount} casualties, team={team}).");
        }

        [BurstDiscard]
        static void LogPeopleEvent(string kind, int amount, TeamId team)
        {
            UnityEngine.Debug.Log($"[PeopleTransport] {kind} {amount} (team={team}).");
        }

        [BurstDiscard]
        static void LogPlanetCaptured(int planetId, TeamId team)
        {
            UnityEngine.Debug.Log($"[PeopleTransport] Planet {planetId} captured by {team}.");
        }
    }

    /// <summary>
    /// Legacy placeholder — do not re-enable.
    /// Client float VFX is owned by <c>TitanOrbit.Game.PeopleTransportVisualSyncSystem</c>, which
    /// magnet-steers into <c>GhostPresentationTransformCache</c> only and never writes ghost
    /// <see cref="LocalTransform"/> (writing LT caused stepped/fighting visuals).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct PeopleTransportPresentationMotionSystem : ISystem
    {
        /// <summary>Keeps this legacy system permanently off — see class summary.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.Enabled = false;
        }

        /// <summary>No-op — presentation lives in PeopleTransportVisualSyncSystem.</summary>
        public void OnUpdate(ref SystemState state) { }
    }
}
