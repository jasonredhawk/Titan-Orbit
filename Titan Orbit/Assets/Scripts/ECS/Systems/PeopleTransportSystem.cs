using TitanOrbit.Core;
using TitanOrbit.Data;
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

        /// <summary>
        /// Seconds between ship→planet unload hops (wall-clock ElapsedTime).
        /// One packed sphere per interval; first hop of a dwell can fire immediately.
        /// </summary>
        public const float UnloadDispatchIntervalSeconds = 1f;

        /// <summary>Fallback hull radius when ship collider scale is unavailable.</summary>
        public const float DefaultShipHullRadius = 1f;

        /// <summary>
        /// Locks Y to the flat map plane and wraps XZ into the canonical rectangle.
        /// Delivery / magnet range still use toroidal distance helpers.
        /// </summary>
        public static void WriteTransform(ref LocalTransform transform, float3 position, float mapW, float mapH)
        {
            position.y = 0f;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                position = ToroidalMapEcs.Wrap(position, mapW, mapH);
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
    /// Server: after a ship dwells in an orbit ring, dispatches incremental people
    /// <b>load</b> (planet→ship hops) and <b>unload</b> (ship→planet hops). Both use
    /// server-only <see cref="PeopleTransportTag"/> + cosmetic
    /// <see cref="PeopleTransportSpawnRpc"/>. Crew on the ship is cargo only — no
    /// external escort spheres. Unload fires one packed sphere at a time.
    /// <para>
    /// [TITAN-ORBIT] One batch = one transport sphere carrying <c>Amount</c> people (scaled up).
    /// Load batch = <c>shipLevel × planetLevel</c> (L6 ship at L3 planet → one +18).
    /// Unload uses the same packed size, one sphere every 0.5s.
    /// Packing into a single sphere cuts spawn/pose RPC traffic and client Instantiates vs N
    /// separate +1 flights.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] When surplus above the 50% reserve is smaller than the full load chunk,
    /// the ship still gets a partial transport (often +1). That lets several orbiting ships each
    /// receive a trickle +1 instead of one high-level ship waiting until surplus covers +2…+6.
    /// </para>
    /// </summary>
    // [ECS/DOTS] Drive is in PredictedFixedStepSimulationSystemGroup — UpdateAfter that type from
    // SimulationSystemGroup is invalid. Run after the predicted fixed-step group instead.
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    public partial struct PeopleTransportDispatchSystem : ISystem
    {
        /// <summary>
        /// Each frame: dwell timers → load/unload chunks → CreateEntity transports + VFX RPC.
        /// Load and unload chunk = <c>shipLevel × planetLevel</c>. Unload fires once per
        /// <see cref="PeopleTransportConstants.UnloadDispatchIntervalSeconds"/>.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Fixed-step dispatch ---
            // [TITAN-ORBIT] Server-only CreateEntity (no ghost Instantiates). Windows clients
            // dropped to the main menu when orbit spawned PeopleTransportGhost floods.
            float dt = SystemAPI.Time.DeltaTime;
            float now = (float)SystemAPI.Time.ElapsedTime;
            // [TITAN-ORBIT] Missing map size = skip transports this tick (do not invent 1000×1000).
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;
            float mapW = map.MapWidth;
            float mapH = map.MapHeight;

            // Empty lobby: no orbiting hulls. Skip the 25-planet hashmap (main did not).
            bool anyShip = false;
            foreach (var _ in SystemAPI.Query<RefRO<ShipState>>().WithAll<ShipTag>())
            {
                anyShip = true;
                break;
            }

            if (!anyShip)
                return;

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
                         .Query<RefRW<ShipState>, RefRO<ShipInput>, RefRW<ShipOrbitState>, RefRO<ShipMoonDockState>,
                             RefRW<ShipPeopleTransferState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    orbit.ValueRW.IsTransferringPeople = false;
                    continue;
                }

                ref var transfer = ref transferState.ValueRW;
                float3 shipPos = shipTransform.ValueRO.Position;
                if (!orbit.ValueRO.InOrbitRing || orbit.ValueRO.OrbitPlanetId == 0)
                {
                    ResetTransferDwell(ref transfer);
                    orbit.ValueRW.IsTransferringPeople = false;
                    continue;
                }

                int orbitPlanetId = orbit.ValueRO.OrbitPlanetId;
                if (!CanTransferPeople(
                        in orbit.ValueRO, in shipInput.ValueRO, in moonDock.ValueRO, shipPos,
                        orbitPlanetId, planetTransformById, planetStateById, mapW, mapH))
                {
                    ResetTransferDwell(ref transfer);
                    orbit.ValueRW.IsTransferringPeople = false;
                    continue;
                }

                if (orbitPlanetId != transfer.LastOrbitPlanetId)
                {
                    transfer.LastOrbitPlanetId = orbitPlanetId;
                    ResetTransferDwell(ref transfer);
                }

                transfer.OrbitDwellSeconds += dt;
                bool dwellReady = transfer.OrbitDwellSeconds >=
                    PeopleTransportConstants.OrbitDwellBeforeTransferSeconds;
                if (!dwellReady)
                {
                    orbit.ValueRW.IsTransferringPeople = transfer.PeopleInTransit > 0.01f;
                    continue;
                }

                if (!planetById.TryGetValue(orbitPlanetId, out var planetEntity))
                {
                    orbit.ValueRW.IsTransferringPeople = false;
                    continue;
                }

                var planetState = planetStateById[orbitPlanetId];
                var planetTransform = planetTransformById[orbitPlanetId];
                float planetSize = math.max(0.5f, planetTransform.Scale);
                // [TITAN-ORBIT] Same effective cap as growth / world labels (includes triangle bonuses).
                // Base-only halfCap made territory-boosted planets look "full" on the HUD while
                // dispatch still thought they were below 50% (or the reverse).
                float connectionBonus = 0f;
                if (state.EntityManager.HasComponent<PlanetGrowthState>(planetEntity))
                    connectionBonus = state.EntityManager
                        .GetComponentData<PlanetGrowthState>(planetEntity).ConnectionBonusFraction;
                int maxPop = PlanetPopulationMath.GetEffectiveMaxPopulation(
                    planetSize, planetState.PlanetLevel, connectionBonus);
                int halfCap = math.max(1, maxPop / 2);

                // --- Load vs unload batch sizes (people/sec + people per packed sphere) ---
                // [TITAN-ORBIT] Load and unload = ship × planet (L6 at L3 → +18).
                // Live PlanetLevel / ShipLevel every tick — leveling mid-orbit must change chunk size.
                int shipLevel = math.max(1, shipState.ValueRO.ShipLevel);
                int planetLevel = math.max(1, planetState.PlanetLevel);
                float loadChunk = PeopleTransportMath.GetTransferChunk(shipLevel, planetLevel);
                float transferMul = CardEffectQuery.GetMul(state.EntityManager, shipEntity, CardEffectKind.PeopleTransferSpeedMul);
                float loadStep = loadChunk * dt * PeopleTransportConstants.TransferSpeedMultiplier * transferMul;
                int shipNetworkId = GetShipNetworkId(ref state, shipEntity);
                if (shipNetworkId == 0)
                {
                    orbit.ValueRW.IsTransferringPeople = false;
                    continue;
                }
                float3 planetPos = planetTransform.Position;
                bool friendly = shipState.ValueRO.Team != TeamId.None && planetState.Ownership == shipState.ValueRO.Team;
                orbit.ValueRW.IsTransferringPeople = ComputeIsTransferringPeople(
                    dwellReady, friendly, planetState.Population, halfCap,
                    shipState.ValueRO.CurrentPeople, shipState.ValueRO.PeopleCapacity,
                    transfer.PeopleInTransit);

                // Latch load for this dwell so a friendly pickup cannot flip to unload
                // mid-orbit. Unload is hostile/neutral only — never dump onto allies.
                bool lockedLoad = transfer.TransferDirection == PeopleTransferDirection.Load;
                bool wantUnload = !lockedLoad && ShouldUnloadPeople(
                    in shipState.ValueRO, in planetState);
                if (wantUnload)
                {
                    transfer.TransferDirection = PeopleTransferDirection.Unload;
                    int unloadChunk = PeopleTransportMath.GetTransferChunk(shipLevel, planetLevel);
                    bool intervalReady = transfer.UnloadAccumulator <= 0.01f ||
                        (now - transfer.UnloadAccumulator) >=
                        PeopleTransportConstants.UnloadDispatchIntervalSeconds;
                    if (intervalReady)
                    {
                        int send = math.min(unloadChunk, shipState.ValueRO.CurrentPeople);
                        if (send > 0 && TryDispatchUnload(
                                ref ecb, ref shipState.ValueRW, send, shipNetworkId,
                                planetState.PlanetId, shipState.ValueRO.Team,
                                shipPos, shipTransform.ValueRO.Scale, planetPos, planetSize,
                                mapW, mapH, now))
                        {
                            transfer.UnloadAccumulator = now;
                        }
                    }
                }
                else
                {
                    transfer.TransferDirection = PeopleTransferDirection.Load;

                    if (friendly)
                    {
                        transfer.LoadAccumulator += loadStep;
                        if (transfer.LoadAccumulator >= loadChunk)
                        {
                            // --- Packed load, capped by cargo space and deployable surplus ---
                            // [TITAN-ORBIT] Ideal size is loadChunk, but surplus or space may be
                            // smaller — then we still send a partial sphere (often +1). That keeps
                            // multi-ship orbits sharing trickle people near the 50% reserve instead
                            // of one high-level ship blocking until a full +2…+6 surplus exists.
                            int space = shipState.ValueRO.PeopleCapacity - shipState.ValueRO.CurrentPeople -
                                        (int)transfer.PeopleInTransit;
                            int surplus = planetState.Population - halfCap;
                            int send = (int)math.min(loadChunk, math.min(space, surplus));
                            if (send > 0 && TryDispatchLoad(
                                    ref ecb, ref shipState.ValueRW, ref planetState,
                                    ref transfer, send, shipNetworkId, planetState.PlanetId, shipState.ValueRO.Team,
                                    shipPos, planetPos, planetSize, mapW, mapH, now))
                            {
                                transfer.LastLoadCombineMax = (int)loadChunk;
                                transfer.LoadAccumulator = 0f;
                                planetStateById[planetState.PlanetId] = planetState;
                                ecb.SetComponent(planetEntity, planetState);
                            }
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

        static void ResetTransferDwell(ref ShipPeopleTransferState transfer)
        {
            transfer.OrbitDwellSeconds = 0f;
            transfer.LoadAccumulator = 0f;
            transfer.UnloadAccumulator = 0f;
            transfer.TransferDirection = PeopleTransferDirection.None;
        }

        /// <summary>
        /// Hostile or unowned worlds only. Allied planets never accept a dump.
        /// </summary>
        static bool ShouldUnloadPeople(in ShipState ship, in PlanetState planet)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.CurrentPeople <= 0)
                return false;
            bool friendly = ship.Team != TeamId.None && planet.Ownership == ship.Team;
            return !friendly;
        }

        /// <summary>
        /// True when this ship can load or unload troops (or inbound crew is still flying).
        /// Ghosted onto <see cref="ShipOrbitState.IsTransferringPeople"/> so orbit-ring tint
        /// matches the troop-transfer lock — not mere ring entry.
        /// </summary>
        static bool ComputeIsTransferringPeople(
            bool dwellReady,
            bool friendly,
            int planetPopulation,
            int halfCap,
            int currentPeople,
            int peopleCapacity,
            float peopleInTransit)
        {
            if (peopleInTransit > 0.01f)
                return true;
            if (!dwellReady)
                return false;

            if (friendly)
            {
                int space = peopleCapacity - currentPeople;
                int surplus = planetPopulation - halfCap;
                return space > 0 && surplus > 0;
            }

            return currentPeople > 0;
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
            // [TITAN-ORBIT] Thrust breaks dwell (player is flying). Fire is ignored — weapons are
            // locked in the orbit ring, so holding shoot must not block people load/unload.
            if (input.Thrust)
                return false;
            if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.01f)
                return false;

            if (!planetTransformById.TryGetValue(orbitPlanetId, out var planetTransform) ||
                !planetStateById.TryGetValue(orbitPlanetId, out var planetState))
                return false;

            // [TITAN-ORBIT] InOrbitRing is hull-overlap from the drive job. Keep dwell when
            // only a wing is in the band — do not require the pivot inside the annulus.
            float planetSize = math.max(0.5f, planetTransform.Scale);
            PlanetOrbitMath.GetRingRadiiWorld(planetSize, planetState.PlanetLevel, out float inner, out float outer, out _);
            float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetTransform.Position, mapW, mapH);
            return PlanetOrbitMath.IsInOrbitRing(dist, inner, outer) || orbit.InOrbitRing;
        }

        static int GetShipNetworkId(ref SystemState state, Entity shipEntity)
        {
            // --- Compute value ---
            if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                return state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            return 0;
        }

        /// <summary>
        /// Spawns one packed load transport carrying <paramref name="amount"/> people.
        /// Planet pays immediately; the ship gains crew only when the sphere delivers.
        /// </summary>
        /// <param name="amount">People packed into this single sphere (ideal size
        /// <c>shipLevel × planetLevel</c>; caller may pass a smaller partial when surplus
        /// or cargo space is tight).</param>
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
            _ = ship;
            if (amount <= 0)
                return false;

            // --- Spawn on planet surface toward the ship ---
            float3 spawnPos = PeopleTransportMath.GetPlanetSurfaceSpawnToward(planetPos, planetSize, shipPos, mapW, mapH);
            float3 targetPos = shipPos;
            float3 loadDir = ToroidalMapEcs.ToroidalDirection(spawnPos, targetPos, mapW, mapH);
            spawnPos += loadDir * 0.2f;

            // --- One packed sphere for the whole batch ---
            // [TITAN-ORBIT] Amount drives visual scale, HP, and ±N floating text. One spawn/pose
            // RPC stream instead of N × +1 flights (less bandwidth + fewer client Instantiates).
            SpawnTransport(ref ecb, spawnPos, targetPos, amount, shipNetworkId, planetId, 0,
                shipNetworkId, true, team, now, mapW, mapH);

            // [TITAN-ORBIT] Planet pays immediately; ship gains only on DeliverLoad (transitory vessel).
            planet.Population -= amount;
            transfer.PeopleInTransit += amount;
            return true;
        }

        /// <summary>
        /// Spawns one packed unload transport. Ship pays immediately; the planet
        /// gains (or is drained) when the sphere delivers.
        /// </summary>
        static bool TryDispatchUnload(
            ref EntityCommandBuffer ecb,
            ref ShipState ship,
            int amount,
            int shipNetworkId,
            int planetId,
            TeamId team,
            float3 shipPos,
            float shipScale,
            float3 planetPos,
            float planetSize,
            float mapW,
            float mapH,
            float now)
        {
            if (amount <= 0 || ship.CurrentPeople < amount)
                return false;

            float hullR = PeopleTransportMath.GetShipHullRadius(shipScale);
            float3 spawnPos = PeopleTransportMath.GetShipUnloadSpawnToward(
                shipPos, hullR, planetPos, mapW, mapH);
            float3 targetPos = PeopleTransportMath.GetPlanetSurfaceToward(
                planetPos, planetSize, spawnPos, mapW, mapH);

            SpawnTransport(ref ecb, spawnPos, targetPos, amount, 0, 0, planetId,
                shipNetworkId, false, team, now, mapW, mapH);

            ship.CurrentPeople = math.max(0, ship.CurrentPeople - amount);
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

            // Sequence ties server sim entity ↔ client VFX ↔ pose RPCs (not a ghost).
            uint sequence = PeopleTransportVfxBridge.NextSequence();

            // --- Server-only sim entity (RPC VFX on clients, not GhostSpawn) ---
            // Bullets / delivery read LocalTransform here. Clients magnet to the live ship.
            Entity transport = ecb.CreateEntity();
            ecb.AddComponent<PeopleTransportTag>(transport);
            ecb.AddComponent(transport, LocalTransform.FromPositionRotationScale(spawnPos, quaternion.identity, scale));
            ecb.AddComponent(transport, new PeopleTransportState
            {
                Sequence = sequence,
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

            // --- Client VFX spawn (PeopleTransportVfxDriver) ---
            float3 bakedTarget = targetPos;
            bakedTarget.y = 0f;
            // VFX/RPC carry the involved ship for hull floats (load dest, or unload source).
            int vfxShipId = targetShipNetworkId != 0 ? targetShipNetworkId : sourceShipNetworkId;
            var vfxReq = new PeopleTransportVfxBridge.SpawnRequest
            {
                Sequence = sequence,
                SpawnPosition = spawnPos,
                TargetPosition = bakedTarget,
                Velocity = velocity,
                CruiseSpeed = cruise,
                Amount = amount,
                TargetShipNetworkId = vfxShipId,
                SourcePlanetId = sourcePlanetId,
                TargetPlanetId = targetPlanetId,
                IsLoad = isLoadByte,
                Team = teamByte,
            };
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                PeopleTransportVfxBridge.TryEnqueue(vfxReq);

            // --- Broadcast spawn RPC (62B layout with TargetPosition — must match headless) ---
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new PeopleTransportSpawnRpc
            {
                Sequence = sequence,
                SpawnPosition = spawnPos,
                TargetPosition = bakedTarget,
                Velocity = velocity,
                CruiseSpeed = cruise,
                Amount = amount,
                TargetShipNetworkId = vfxShipId,
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
            // [TITAN-ORBIT] Missing map size = skip transport sim this tick (do not invent 1000×1000).
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;
            float mapW = map.MapWidth;
            float mapH = map.MapHeight;

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
                        PeopleTransportNetNotify.EndAndDestroy(
                            ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Destroyed);
                        continue;
                    }

                    if (!shipByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipEntity) ||
                        !shipStateByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipState) ||
                        !shipTransformByNetworkId.TryGetValue(t.TargetShipNetworkId, out var shipTransform))
                    {
                        // Destination ship gone — refund planet and free inbound capacity reservation.
                        var sourcePlanetOnly = planetStateById[t.SourcePlanetId];
                        var sourceTransformOnly = planetTransformById[t.SourcePlanetId];
                        float sourceSizeOnly = math.max(0.5f, sourceTransformOnly.Scale);
                        sourcePlanetOnly.Population = math.min(
                            sourcePlanetOnly.Population + (int)t.Amount,
                            PlanetPopulationMath.GetMaxPopulation(sourceSizeOnly, sourcePlanetOnly.PlanetLevel));
                        planetStateById[t.SourcePlanetId] = sourcePlanetOnly;
                        ecb.SetComponent(planetById[t.SourcePlanetId], sourcePlanetOnly);
                        ClearInboundPeopleInTransit(ref state, t.TargetShipNetworkId, t.Amount, shipByNetworkId);
                        PeopleTransportNetNotify.EndAndDestroy(
                            ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Returned);
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
                            PeopleTransportNetNotify.EndAndDestroy(
                                ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Returned);
                        }

                        continue;
                    }

                    float shipRadius = PeopleTransportMath.GetShipHullRadius(shipTransform.Scale);
                    float3 shipCenter = shipTransform.Position;
                    if (PeopleTransportMath.CanDeliverLoadToShip(myPos, shipCenter, shipRadius, mapW, mapH) &&
                        PeopleTransportMath.HasBriefTravelBeforeLoad(myPos, t.SpawnPosition, elapsed, mapW, mapH))
                    {
                        int sourcePlanetLevel = sourcePlanetState.PlanetLevel;
                        DeliverLoad(
                            ref state, shipEntity, ref shipState, t.Amount, team, sourcePlanetLevel);
                        shipStateByNetworkId[t.TargetShipNetworkId] = shipState;
                        ecb.SetComponent(shipEntity, shipState);
                            PeopleTransportNetNotify.EndAndDestroy(
                            ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Consumed);
                        continue;
                    }
                }
                else
                {
                    // Unload hops are independent of the source hull: ship death zeros cargo
                    // still aboard, but these spheres already left and keep flying to the planet.
                    if (!planetStateById.TryGetValue(t.TargetPlanetId, out var planetState) ||
                        !planetTransformById.TryGetValue(t.TargetPlanetId, out var planetTransform))
                    {
                        // Planet gone — crew already left the ship at dispatch. Refund only if the
                        // hull is still alive; a dead source ship must not swallow in-flight unloads.
                        if (shipByNetworkId.TryGetValue(t.SourceShipNetworkId, out var orphanShipEntity) &&
                            shipStateByNetworkId.TryGetValue(t.SourceShipNetworkId, out var orphanShipState) &&
                            !orphanShipState.IsDead &&
                            !orphanShipState.AwaitingTeamSelection)
                        {
                            RefundUnloadToShip(ref orphanShipState, t.Amount);
                            shipStateByNetworkId[t.SourceShipNetworkId] = orphanShipState;
                            ecb.SetComponent(orphanShipEntity, orphanShipState);
                        }

                        PeopleTransportNetNotify.EndAndDestroy(
                            ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Destroyed);
                        continue;
                    }

                    float planetSize = math.max(0.5f, planetTransform.Scale);

                    if (PeopleTransportMath.CanCompleteUnloadDelivery(
                            myPos, t.SpawnPosition, planetTransform.Position, planetSize, elapsed, mapW, mapH))
                    {
                        // Ship already debited at dispatch — only apply planet-side outcome here.
                        var planetEntity = planetById[t.TargetPlanetId];
                        var unloadOutcome = DeliverUnload(ref planetState, t.Amount, team, planetTransform, planetSize);

                        // --- Per-planet siege ledger (hostile drain + the capturing unload) ---
                        // [TITAN-ORBIT] Top contributor = most troops delivered by the capturing team.
                        int peopleScore = (int)t.Amount;
                        if (peopleScore > 0 &&
                            t.SourceShipNetworkId > 0 &&
                            (unloadOutcome == PeopleUnloadOutcome.HostileDrain ||
                             unloadOutcome == PeopleUnloadOutcome.Captured))
                        {
                            PlanetPeopleContributionLogic.Add(
                                state.EntityManager,
                                planetEntity,
                                t.SourceShipNetworkId,
                                peopleScore,
                                team);
                        }

                        if (unloadOutcome == PeopleUnloadOutcome.Captured)
                        {
                            planetState.TopContributorNetworkId =
                                PlanetPeopleContributionLogic.ResolveTopAndClear(
                                    state.EntityManager,
                                    planetEntity,
                                    team,
                                    t.SourceShipNetworkId);
                        }

                        planetStateById[t.TargetPlanetId] = planetState;
                        ecb.SetComponent(planetEntity, planetState);

                        // --- Match-long transporter score (minimap top transporter badge) ---
                        // [TITAN-ORBIT] Credit the dispatching ship for every successful unload
                        // (friendly reinforce, hostile drain, or capture) — people reached the planet.
                        if (peopleScore > 0 &&
                            t.SourceShipNetworkId > 0 &&
                            shipByNetworkId.TryGetValue(t.SourceShipNetworkId, out Entity sourceShipEntity))
                        {
                            ShipMatchStatsLogic.TryAddOnShip(
                                state.EntityManager,
                                sourceShipEntity,
                                kills: 0,
                                gemsDeposited: 0,
                                peopleDelivered: peopleScore);
                        }

                        if (unloadOutcome == PeopleUnloadOutcome.Captured)
                        {
                            // [TITAN-ORBIT] Capture destroys all planetary defense turrets — slots
                            // become empty placeholders for the new owner (plan rule).
                            PlanetaryDefenseSlotSyncSystem.WipeSlotsForOwnershipChange(
                                state.EntityManager,
                                planetEntity,
                                team,
                                planetState.PlanetLevel);

                            // [TITAN-ORBIT] Immediate client graph / minimap refresh — do not wait on
                            // rate-limited planet ghost snapshots (MaxSendChunks + low Importance).
                            PlanetOwnershipNetNotify.Send(
                                ref ecb,
                                planetState.PlanetId,
                                team,
                                planetState.Population,
                                planetState.PlanetLevel,
                                planetState.TopContributorNetworkId);
                        }
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

                        PeopleTransportNetNotify.EndAndDestroy(
                            ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Consumed);
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

        /// <summary>
        /// Whether a load transport should keep chasing this ship (orbit ring + idle + same planet).
        /// Shared by server magnet steering and client VFX retarget / return-to-planet.
        /// </summary>
        public static bool IsShipEligibleForLoad(
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
            // [TITAN-ORBIT] Same as CanTransferPeople — thrust cancels unload dwell; Fire does not
            // (orbit-ring weapons lock; holding shoot must not starve unload).
            if (input.Thrust)
                return false;
            if (!orbit.InOrbitRing || orbit.OrbitPlanetId != sourcePlanetId)
                return false;
            if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.01f)
                return false;

            // [TITAN-ORBIT] InOrbitRing is hull-overlap from the drive job. Keep the load
            // magnet on a wing-capture tick — do not require the pivot inside the annulus.
            PlanetOrbitMath.GetRingRadiiWorld(planetSize, planetLevel, out float inner, out float outer, out _);
            float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetPos, mapW, mapH);
            return PlanetOrbitMath.IsInOrbitRing(dist, inner, outer) || orbit.InOrbitRing;
        }

        static void DeliverLoad(
            ref SystemState state,
            Entity shipEntity,
            ref ShipState ship,
            float amount,
            TeamId team,
            int planetLevel)
        {
            // --- DeliverLoad (arrival) ---
            // CurrentPeople rises here; client VFX shows +N at the transport consume position.
            int space = ship.PeopleCapacity - ship.CurrentPeople;
            int toAdd = (int)math.min(amount, space);
            if (toAdd > 0)
            {
                ship.CurrentPeople += toAdd;
            }

            _ = planetLevel;

            ClearPeopleInTransitOnShip(ref state, shipEntity, amount);
            LogPeopleEvent("Load", toAdd, team);
        }

        /// <summary>
        /// Returns inbound crew to the planet when the ship left the orbit ring (or became ineligible).
        /// Client VFX retargets the same sphere home and shows +N on surface consume.
        /// </summary>
        static void ReturnLoadToPlanet(
            ref SystemState state,
            ref PlanetState planet,
            Entity shipEntity,
            float amount,
            float planetSize)
        {
            int maxPop = PlanetPopulationMath.GetMaxPopulation(planetSize, planet.PlanetLevel);
            planet.Population = math.min(planet.Population + (int)amount, maxPop);
            ClearPeopleInTransitOnShip(ref state, shipEntity, amount);
        }

        /// <summary>
        /// Puts unload crew back on the ship when the destination planet entity disappears mid-flight.
        /// </summary>
        static void RefundUnloadToShip(ref ShipState ship, float amount)
        {
            int add = (int)math.max(0f, amount);
            if (add <= 0)
                return;
            ship.CurrentPeople = math.min(ship.PeopleCapacity, ship.CurrentPeople + add);
        }

        /// <summary>Decrements inbound <see cref="ShipPeopleTransferState.PeopleInTransit"/> on a ship entity.</summary>
        static void ClearPeopleInTransitOnShip(ref SystemState state, Entity shipEntity, float amount)
        {
            if (!state.EntityManager.HasComponent<ShipPeopleTransferState>(shipEntity))
                return;

            var transfer = state.EntityManager.GetComponentData<ShipPeopleTransferState>(shipEntity);
            transfer.PeopleInTransit = math.max(0f, transfer.PeopleInTransit - amount);
            state.EntityManager.SetComponentData(shipEntity, transfer);
        }

        /// <summary>
        /// Clears inbound reservation when the destination ship entity is already missing from maps
        /// (lookup by network id if the entity still exists).
        /// </summary>
        static void ClearInboundPeopleInTransit(
            ref SystemState state,
            int shipNetworkId,
            float amount,
            NativeHashMap<int, Entity> shipByNetworkId)
        {
            if (shipNetworkId == 0 || !shipByNetworkId.TryGetValue(shipNetworkId, out var shipEntity))
                return;
            ClearPeopleInTransitOnShip(ref state, shipEntity, amount);
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
        /// Shot down in flight.
        /// Load: free inbound capacity reservation (planet already paid at spawn — people lost).
        /// Unload: ship already debited at dispatch — no further ship change (casualties in space).
        /// </summary>
        [BurstDiscard]
        public static void DestroyFromBulletDamage(ref SystemState state, Entity transportEntity, in PeopleTransportState transport)
        {
            // --- DestroyFromBulletDamage ---
            var em = state.EntityManager;
            float3 pos = PeopleTransportNetNotify.ReadPosition(em, transportEntity);

            if (transport.Amount <= 0f)
            {
                PeopleTransportNetNotify.EndAndDestroyImmediate(
                    ref state, transportEntity, in transport, pos, PeopleTransportPoseStatus.Destroyed);
                return;
            }

            // Only load transports still hold a PeopleInTransit reservation on the destination ship.
            if (transport.IsLoad != 0 && transport.TargetShipNetworkId != 0)
            {
                using var shipQuery = em.CreateEntityQuery(
                    ComponentType.ReadOnly<ShipTag>(),
                    ComponentType.ReadOnly<GhostOwner>(),
                    ComponentType.ReadWrite<ShipPeopleTransferState>());
                using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                using var ships = shipQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < ships.Length; i++)
                {
                    if (owners[i].NetworkId != transport.TargetShipNetworkId)
                        continue;

                    ClearPeopleInTransitOnShip(ref state, ships[i], transport.Amount);
                    break;
                }
            }

            PeopleTransportNetNotify.EndAndDestroyImmediate(
                ref state, transportEntity, in transport, pos, PeopleTransportPoseStatus.Destroyed);
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
