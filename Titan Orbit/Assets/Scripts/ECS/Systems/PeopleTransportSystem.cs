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
    /// <see cref="PeopleTransportSpawnRpc"/>. Delivered crew parks as derived escort
    /// drones around the hull. Unload peels one parked orb at a time.
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
            int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = math.max(1, tickRate.SimulationTickRate);
            NetworkTime networkTime = default;
            bool hasNetworkTime = SystemAPI.TryGetSingleton<NetworkTime>(out networkTime);
            float hopClock = PeopleTransportEscortLogic.ReadHopClockSeconds(
                now, hasNetworkTime ? networkTime : default, hz);
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
            // Friendly unloads already in the air. Hostile drains are not garrison.
            var garrisonInbound = new NativeHashMap<int, int>(16, Allocator.Temp);
            foreach (var hopRef in SystemAPI.Query<RefRO<PeopleTransportState>>().WithAll<PeopleTransportTag>())
            {
                PeopleTransportState hop = hopRef.ValueRO;
                if (hop.IsLoad != 0 || hop.Returning != 0 || hop.TargetPlanetId == 0)
                    continue;
                if (!planetStateById.TryGetValue(hop.TargetPlanetId, out var dest))
                    continue;
                if (dest.Ownership != (TeamId)hop.Team)
                    continue;
                int people = (int)math.round(math.max(0f, hop.Amount));
                if (people <= 0)
                    continue;
                garrisonInbound.TryGetValue(hop.TargetPlanetId, out int sum);
                garrisonInbound[hop.TargetPlanetId] = sum + people;
            }

            foreach (var (shipState, shipInput, orbit, moonDock, transferState, shipTransform, shipEntity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipInput>, RefRW<ShipOrbitState>, RefRO<ShipMoonDockState>,
                             RefRW<ShipPeopleTransferState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    // Seats are gone. Death presentation scatters the parked meshes on the client.
                    orbit.ValueRW.IsTransferringPeople = false;
                    PeopleTransportEscortLogic.ClearAll(state.EntityManager, shipEntity);
                    continue;
                }

                ref var transfer = ref transferState.ValueRW;
                int shipLevelEarly = math.max(1, shipState.ValueRO.ShipLevel);
                PeopleTransportEscortLogic.SyncParkedEscorts(
                    state.EntityManager, shipEntity, shipState.ValueRO.CurrentPeople,
                    PeopleTransportEscortLogic.ResolveChunk(shipLevelEarly, transfer.LastLoadCombineMax),
                    GetShipNetworkId(ref state, shipEntity));
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
                // Troops already flying home onto this friendly planet. They count toward
                // the 49% line so another sphere is not launched on top of them.
                garrisonInbound.TryGetValue(orbitPlanetId, out int inboundGarrison);

                // --- Load vs unload batch sizes (people/sec + people per packed sphere) ---
                // [TITAN-ORBIT] Load and unload = ship × planet (L6 at L3 → +18).
                // Live PlanetLevel / ShipLevel every tick — leveling mid-orbit must change chunk size.
                int shipLevel = math.max(1, shipState.ValueRO.ShipLevel);
                int planetLevel = math.max(1, planetState.PlanetLevel);
                int shipNetworkId = GetShipNetworkId(ref state, shipEntity);
                if (shipNetworkId == 0)
                {
                    orbit.ValueRW.IsTransferringPeople = false;
                    continue;
                }

                // [TITAN-ORBIT] Top troop commander packs 5% more people per sphere
                // (round-to-nearest). Load step uses the same chunk so throughput rises too.
                bool topTroop = SystemAPI.TryGetSingleton<ShipCommandRoleSnapshot>(out var troopRoles)
                                && troopRoles.IsTransporter(shipState.ValueRO.Team, shipNetworkId);
                float loadChunk = TeamCommandRoleRules.ScaleTransferChunk(
                    PeopleTransportMath.GetTransferChunk(shipLevel, planetLevel), topTroop);
                float transferMul = CardEffectQuery.GetMul(state.EntityManager, shipEntity, CardEffectKind.PeopleTransferSpeedMul);
                float loadStep = loadChunk * dt * PeopleTransportConstants.TransferSpeedMultiplier * transferMul;
                float3 planetPos = planetTransform.Position;
                bool friendly = shipState.ValueRO.Team != TeamId.None && planetState.Ownership == shipState.ValueRO.Team;
                orbit.ValueRW.IsTransferringPeople = ComputeIsTransferringPeople(
                    dwellReady, friendly, planetState.Population, halfCap, maxPop,
                    shipState.ValueRO.CurrentPeople, shipState.ValueRO.PeopleCapacity,
                    transfer.PeopleInTransit);

                // Latch load for this dwell so a friendly pickup cannot flip to unload
                // mid-orbit. A latched unload stays an unload: friendly worlds under 49%
                // keep receiving troops, and crossing that line does not start a load
                // until the ship leaves the ring.
                bool lockedLoad = transfer.TransferDirection == PeopleTransferDirection.Load;
                bool lockedUnload = transfer.TransferDirection == PeopleTransferDirection.Unload;
                bool wantUnload = !lockedLoad && ShouldUnloadPeople(
                    in shipState.ValueRO, in planetState, maxPop, inboundGarrison);
                if (wantUnload)
                {
                    // Each parked escort flies down intact — same people, same size, same HP.
                    // The old unload chunk carved a big seat into a smaller sphere and the
                    // replacement spawned at full health.
                    transfer.TransferDirection = PeopleTransferDirection.Unload;
                    bool intervalReady = transfer.UnloadAccumulator <= 0.01f ||
                        (now - transfer.UnloadAccumulator) >=
                        PeopleTransportConstants.UnloadDispatchIntervalSeconds;
                    if (intervalReady &&
                        shipState.ValueRO.CurrentPeople > 0 &&
                        TryDispatchUnload(
                            ref ecb, ref state, shipEntity, ref shipState.ValueRW, shipNetworkId,
                            planetState.PlanetId, shipState.ValueRO.Team, planetState.Ownership,
                            shipTransform.ValueRO, planetPos, planetSize,
                            mapW, mapH, now, hopClock))
                    {
                        transfer.UnloadAccumulator = now;
                    }
                }
                else if (!lockedUnload && friendly)
                {
                    // --- Packed load, capped by cargo space and deployable surplus ---
                    // [TITAN-ORBIT] Ideal size is loadChunk, but surplus or space may be
                    // smaller — then we still send a partial sphere (often +1). That keeps
                    // multi-ship orbits sharing trickle people near the 50% reserve instead
                    // of one high-level ship blocking until a full +2…+6 surplus exists.
                    // Direction stays unload when this dwell was already dropping troops,
                    // including a friendly planet that just crossed 49%.
                    int space = shipState.ValueRO.PeopleCapacity - shipState.ValueRO.CurrentPeople -
                                (int)transfer.PeopleInTransit;
                    int surplus = planetState.Population - halfCap;
                    if (space > 0 && surplus > 0)
                    {
                        transfer.TransferDirection = PeopleTransferDirection.Load;
                        transfer.LoadAccumulator += loadStep;
                        if (transfer.LoadAccumulator >= loadChunk)
                        {
                            int send = (int)math.min(loadChunk, math.min(space, surplus));
                            if (send > 0 && TryDispatchLoad(
                                    ref ecb, ref state, shipEntity, ref shipState.ValueRW, ref planetState,
                                    ref transfer, send, (int)loadChunk, shipNetworkId, planetState.PlanetId,
                                    shipState.ValueRO.Team, shipPos, planetPos, planetSize, mapW, mapH, now, hopClock))
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
            garrisonInbound.Dispose();
            planetById.Dispose();
            planetStateById.Dispose();
            planetTransformById.Dispose();
        }

        internal static void AddGarrisonReserved(ref NativeHashMap<int, int> reserved, int planetId, int people)
        {
            if (people <= 0 || planetId == 0)
                return;
            reserved.TryGetValue(planetId, out int sum);
            reserved[planetId] = sum + people;
        }

        static void ResetTransferDwell(ref ShipPeopleTransferState transfer)
        {
            transfer.OrbitDwellSeconds = 0f;
            transfer.LoadAccumulator = 0f;
            transfer.UnloadAccumulator = 0f;
            transfer.TransferDirection = PeopleTransferDirection.None;
        }

        /// <summary>
        /// Hostile or unowned worlds always take a drop. A friendly world keeps
        /// taking troops while it is under 49% of its cap, counting people already
        /// in flight, so the drop stops short of the 50% load reserve.
        /// </summary>
        static bool ShouldUnloadPeople(in ShipState ship, in PlanetState planet, int maxPop, int inboundGarrison)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.CurrentPeople <= 0)
                return false;
            bool friendly = ship.Team != TeamId.None && planet.Ownership == ship.Team;
            if (!friendly)
                return true;
            int projected = planet.Population + math.max(0, inboundGarrison);
            return PlanetPopulationMath.NeedsFriendlyGarrison(projected, maxPop);
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
            int maxPop,
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
                bool loading = space > 0 && surplus > 0;
                bool garrison = currentPeople > 0 &&
                                PlanetPopulationMath.NeedsFriendlyGarrison(planetPopulation, maxPop);
                return loading || garrison;
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

            // [TITAN-ORBIT] Live re-check: ship pivot must sit in the annulus this tick.
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

        /// <summary>
        /// Spawns one packed load transport carrying <paramref name="amount"/> people.
        /// Planet pays immediately; the ship gains crew only when the sphere delivers.
        /// </summary>
        /// <param name="amount">People packed into this single sphere (ideal size
        /// <c>shipLevel × planetLevel</c>; caller may pass a smaller partial when surplus
        /// or cargo space is tight).</param>
        /// <param name="combineChunk">Pack size latched for escort slots on delivery.
        /// Same value stored in <see cref="ShipPeopleTransferState.LastLoadCombineMax"/>.</param>
        static bool TryDispatchLoad(
            ref EntityCommandBuffer ecb,
            ref SystemState state,
            Entity shipEntity,
            ref ShipState ship,
            ref PlanetState planet,
            ref ShipPeopleTransferState transfer,
            int amount,
            int combineChunk,
            int shipNetworkId,
            int planetId,
            TeamId team,
            float3 shipPos,
            float3 planetPos,
            float planetSize,
            float mapW,
            float mapH,
            float now,
            float hopClock)
        {
            _ = now;
            if (amount <= 0)
                return false;

            // --- Spawn on planet surface toward the ship ---
            float3 spawnPos = PeopleTransportMath.GetPlanetSurfaceSpawnToward(planetPos, planetSize, shipPos, mapW, mapH);
            float3 targetPos = shipPos;
            float3 loadDir = ToroidalMapEcs.ToroidalDirection(spawnPos, targetPos, mapW, mapH);
            spawnPos += loadDir * 0.2f;
            // Seat this sphere as if everyone already inbound has landed. The one
            // that fills a capsule homes there; the next reserves the following
            // seat instead of chasing the same capsule.
            int inbound = (int)math.round(math.max(0f, transfer.PeopleInTransit));
            int peopleBefore = math.max(0, ship.CurrentPeople) + inbound;
            PeopleTransportEscortLogic.ResolveLoadHomeSeat(
                state.EntityManager, shipEntity, peopleBefore,
                amount, combineChunk, out byte seatId, out bool ownsSeat);

            // --- One packed sphere for the whole batch ---
            // [TITAN-ORBIT] Amount drives visual scale, HP, and ±N floating text. One spawn/pose
            // RPC stream instead of N × +1 flights (less bandwidth + fewer client Instantiates).
            // Target baked here is only a fallback — the live flight homes on the seat above.
            SpawnTransport(ref ecb, spawnPos, targetPos, amount, shipNetworkId, planetId, 0,
                shipNetworkId, true, team, hopClock, mapW, mapH, seatId, ownsSeat, peopleBefore, -1f, 0);

            // [TITAN-ORBIT] Planet pays immediately; ship gains only on DeliverLoad (transitory vessel).
            planet.Population -= amount;
            transfer.PeopleInTransit += amount;
            return true;
        }

        /// <summary>
        /// Launches one parked escort toward the planet without resizing it.
        /// The sphere carries that seat's people and its current health. Ship pays
        /// immediately; the planet gains (or is drained) when the sphere delivers.
        /// Up to <see cref="PeopleTransportMath.MaxEscortVisualSlots"/> escorts exist,
        /// and each one is a single descent — they are not split into smaller pieces.
        /// </summary>
        static bool TryDispatchUnload(
            ref EntityCommandBuffer ecb,
            ref SystemState state,
            Entity shipEntity,
            ref ShipState ship,
            int shipNetworkId,
            int planetId,
            TeamId team,
            TeamId planetOwnership,
            LocalTransform shipTransform,
            float3 planetPos,
            float planetSize,
            float mapW,
            float mapH,
            float now,
            float hopClock)
        {
            _ = now;
            if (ship.CurrentPeople <= 0)
                return false;

            if (!PeopleTransportEscortLogic.TryPeelParkedSeat(
                    state.EntityManager, shipEntity, out byte seatId, out int peeled, out float peeledHealth))
            {
                return false;
            }

            if (peeled <= 0)
                return false;

            // Keep the seat's size. Clamping to the unload chunk replaced a damaged
            // escort with a smaller sphere whose max HP made the old damage look like full health.
            int amount = peeled;
            float3 shipPos = shipTransform.Position;
            PeopleTransportEscortLogic.GetEscortHullExtents(
                state.EntityManager, shipEntity, shipTransform.Scale, out float extX, out float extZ);
            float3 heading = float3.zero;
            if (state.EntityManager.HasComponent<ShipKinematics>(shipEntity))
                heading = state.EntityManager.GetComponentData<ShipKinematics>(shipEntity).FormationHeading;
            float3 spawnPos = PeopleTransportMath.EvaluateEscortSwarmPose(
                shipPos, shipTransform.Rotation, extX, extZ, seatId, amount, shipNetworkId,
                hopClock, mapW, mapH, heading);
            float3 targetPos = PeopleTransportMath.GetPlanetSurfaceToward(
                planetPos, planetSize, spawnPos, mapW, mapH);

            SpawnTransport(ref ecb, spawnPos, targetPos, amount, 0, 0, planetId,
                shipNetworkId, false, team, hopClock, mapW, mapH, seatId, false, 0, peeledHealth,
                (byte)planetOwnership);

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
            float hopClock,
            float mapW,
            float mapH,
            byte seatId,
            bool ownsSeat,
            int seatClaimPeople,
            float healthOverride = -1f,
            byte targetOwnerAtSpawn = 0)
        {
            float3 dir = ToroidalMapEcs.ToroidalDirection(spawnPos, targetPos, mapW, mapH);
            float cruise = PeopleTransportMath.ComputeCruiseSpeed(spawnPos, targetPos, isLoad, mapW, mapH);
            // Planar launch dir * cruise. Y packs hop-clock spawn seconds so clients share
            // the same closed-form t without changing the 62-byte SpawnRpc layout.
            float3 velocity = dir * cruise;
            velocity.y = hopClock;
            float scale = PeopleTransportMath.GetVisualScaleMultiplier(amount) * 0.25f;
            byte isLoadByte = (byte)(isLoad ? 1 : 0);
            byte teamByte = (byte)team;
            float health = healthOverride > 0.01f
                ? healthOverride
                : PeopleTransportMath.ComputeMaxHealth(amount);

            // Sequence ties server sim entity ↔ client VFX ↔ pose RPCs (not a ghost).
            uint sequence = PeopleTransportVfxBridge.NextSequence();

            // --- Server-only sim entity (RPC VFX on clients, not GhostSpawn) ---
            // Bullets / delivery read LocalTransform here. Clients share the closed-form hop.
            Entity transport = ecb.CreateEntity();
            ecb.AddComponent<PeopleTransportTag>(transport);
            ecb.AddComponent(transport, LocalTransform.FromPositionRotationScale(spawnPos, quaternion.identity, scale));
            ecb.AddComponent(transport, new PeopleTransportState
            {
                Sequence = sequence,
                Amount = amount,
                Health = health,
                Velocity = velocity,
                SpawnPosition = spawnPos,
                SpawnTime = hopClock,
                CruiseSpeed = cruise,
                TargetShipNetworkId = targetShipNetworkId,
                SourcePlanetId = sourcePlanetId,
                TargetPlanetId = targetPlanetId,
                SourceShipNetworkId = sourceShipNetworkId,
                IsLoad = isLoadByte,
                Team = teamByte,
                SeatId = seatId,
                OwnsSeat = (byte)(ownsSeat ? 1 : 0),
                SeatClaimPeople = math.max(0, seatClaimPeople),
                TargetOwnerAtSpawn = targetOwnerAtSpawn,
                Returning = 0,
            });

            // --- Client VFX spawn (PeopleTransportVfxDriver) ---
            float3 bakedTarget = targetPos;
            // Y carries live HP (same 62-byte layout). 0 means "full" for older packets.
            // Clients read it, then flatten Y before using the point as a destination.
            bakedTarget.y = health;
            // Seat rides in spawn Y (seat+1). XZ stays the launch point. Layout stays 62 bytes.
            float3 rpcSpawn = spawnPos;
            rpcSpawn.y = seatId + 1f;
            // VFX/RPC carry the involved ship for hull floats (load dest, or unload source).
            int vfxShipId = targetShipNetworkId != 0 ? targetShipNetworkId : sourceShipNetworkId;
            var vfxReq = new PeopleTransportVfxBridge.SpawnRequest
            {
                Sequence = sequence,
                SpawnPosition = rpcSpawn,
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

            // --- Spawn RPC only for cameras the flight crosses (seat stays in SpawnPosition.y) ---
            var spawnRpc = new PeopleTransportSpawnRpc
            {
                Sequence = sequence,
                SpawnPosition = rpcSpawn,
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
            float3 pathFrom = spawnPos;
            pathFrom.y = 0f;
            ViewInterestFanout.EmitSegment(ref ecb, spawnRpc, pathFrom, bakedTarget, 4f, 0);
        }
    }

    /// <summary>
    /// Server authoritative simulation of people-transport spheres. A load that opens
    /// a new escort slot joins that seeded seat. A load that only tops up cargo homes
    /// on the parked capsule that grows. Unload sorties leave a slot and rejoin it
    /// with the same formation step as fighter and mining drones. Runs after dispatch
    /// and bullets.
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
            int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = math.max(1, tickRate.SimulationTickRate);
            NetworkTime networkTime = default;
            bool hasNetworkTime = SystemAPI.TryGetSingleton<NetworkTime>(out networkTime);
            float hopClock = PeopleTransportEscortLogic.ReadHopClockSeconds(
                now, hasNetworkTime ? networkTime : default, hz);
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
            var claimReleases = new NativeList<PeopleInboundClaimRelease>(4, Allocator.Temp);

            // Friendly unloads already in the air count against the 49% line. Capture
            // leftovers are judged later in this tick, after a hop may have flipped ownership.
            var garrisonReserved = new NativeHashMap<int, int>(16, Allocator.Temp);
            foreach (var transport in SystemAPI.Query<RefRO<PeopleTransportState>>().WithAll<PeopleTransportTag>())
            {
                PeopleTransportState hop = transport.ValueRO;
                if (hop.IsLoad != 0 || hop.Returning != 0 || hop.TargetPlanetId == 0 || hop.TargetOwnerAtSpawn == 255)
                    continue;
                if (!planetStateById.TryGetValue(hop.TargetPlanetId, out var livePlanet))
                    continue;
                if (hop.TargetOwnerAtSpawn != (byte)livePlanet.Ownership || (TeamId)hop.Team != livePlanet.Ownership)
                    continue;
                PeopleTransportDispatchSystem.AddGarrisonReserved(
                    ref garrisonReserved, hop.TargetPlanetId, (int)math.round(math.max(0f, hop.Amount)));
            }

            foreach (var (transport, transform, entity) in SystemAPI
                         .Query<RefRW<PeopleTransportState>, RefRW<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                ref var t = ref transport.ValueRW;
                float elapsed = hopClock - t.SpawnTime;
                float3 myPos = transform.ValueRO.Position;
                myPos.y = 0f;
                bool isLoad = t.IsLoad != 0;
                var team = (TeamId)t.Team;

                if (isLoad &&
                    t.TargetShipNetworkId != 0 &&
                    shipByNetworkId.TryGetValue(t.TargetShipNetworkId, out var loadShip) &&
                    shipStateByNetworkId.TryGetValue(t.TargetShipNetworkId, out var loadState) &&
                    state.EntityManager.HasBuffer<PeopleEscortVitalElement>(loadShip))
                {
                    int combine = 0;
                    if (state.EntityManager.HasComponent<ShipPeopleTransferState>(loadShip))
                        combine = state.EntityManager.GetComponentData<ShipPeopleTransferState>(loadShip).LastLoadCombineMax;
                    int chunk = PeopleTransportEscortLogic.ResolveChunk(
                        math.max(1, loadState.ShipLevel), combine);
                    float3 approach = myPos;
                    if (planetTransformById.TryGetValue(t.SourcePlanetId, out var approachPlanet))
                        approach = approachPlanet.Position;
                    shipTransformByNetworkId.TryGetValue(t.TargetShipNetworkId, out var loadXfForSeat);
                    if (PeopleTransportEscortLogic.TryRetargetLoadSeat(
                            state.EntityManager, loadShip, t.SeatClaimPeople, (int)math.max(1f, t.Amount),
                            chunk, t.SeatId, t.OwnsSeat != 0, approach, loadXfForSeat,
                            t.TargetShipNetworkId, hopClock, mapW, mapH,
                            out byte retargeted, out bool ownsNewSeat))
                    {
                        // Drop a reservation only when this hop is leaving it for a
                        // parked capsule. A fresh reserve is the seat it now owns.
                        if (t.OwnsSeat != 0 && !ownsNewSeat && retargeted != t.SeatId)
                            PeopleTransportEscortLogic.RemoveSeat(state.EntityManager, loadShip, t.SeatId);

                        t.OwnsSeat = (byte)(ownsNewSeat ? 1 : 0);
                        if (retargeted != t.SeatId)
                        {
                            t.SeatId = retargeted;
                            // Rebase onto the new seat. StepTroopDrone keeps speed
                            // so the sphere curves instead of braking to a stop.
                            t.FormationReady = 0;
                            float3 shipPos = shipTransformByNetworkId.TryGetValue(t.TargetShipNetworkId, out var loadXf)
                                ? loadXf.Position
                                : myPos;
                            PeopleTransportNetNotify.SendSeatRetarget(
                                ref ecb, t.Sequence, retargeted, shipPos, t.TargetShipNetworkId);
                        }
                    }
                }

                // A capture this tick (earlier hop) leaves this sphere aimed at the old owner.
                // Keep it unloading while the planet is under 49%; otherwise fly it home.
                if (!isLoad && t.Returning == 0 && t.TargetPlanetId != 0 && t.TargetOwnerAtSpawn != 255 &&
                    planetStateById.TryGetValue(t.TargetPlanetId, out var recallPlanet) &&
                    planetTransformById.TryGetValue(t.TargetPlanetId, out var recallTransform) &&
                    planetById.TryGetValue(t.TargetPlanetId, out var recallEntity) &&
                    t.TargetOwnerAtSpawn != (byte)recallPlanet.Ownership)
                {
                    int people = (int)math.round(math.max(0f, t.Amount));
                    float recallSize = math.max(0.5f, recallTransform.Scale);
                    int recallMax = EffectiveMaxPopulation(
                        ref state, recallEntity, recallSize, recallPlanet.PlanetLevel);
                    garrisonReserved.TryGetValue(t.TargetPlanetId, out int already);
                    if (people > 0 && PeopleTransportMath.ShouldKeepUnloadingAfterCapture(
                            t.TargetOwnerAtSpawn, recallPlanet.Ownership, team,
                            recallPlanet.Population, recallMax, already))
                    {
                        t.TargetOwnerAtSpawn = (byte)recallPlanet.Ownership;
                        PeopleTransportDispatchSystem.AddGarrisonReserved(ref garrisonReserved, t.TargetPlanetId, people);
                    }
                    else
                    {
                        t.Returning = 1;
                        // This hop was seeded when the planet was still ours. It is going
                        // home, so it must not keep blocking the 49% line.
                        if ((TeamId)t.TargetOwnerAtSpawn == team)
                        {
                            garrisonReserved.TryGetValue(t.TargetPlanetId, out int held);
                            garrisonReserved[t.TargetPlanetId] = math.max(0, held - people);
                        }
                    }
                }

                StepTransportMotion(
                    ref state, ref t, ref transform.ValueRW, isLoad, myPos, dt, hopClock, mapW, mapH,
                    shipByNetworkId, shipStateByNetworkId, shipTransformByNetworkId,
                    shipMoonDockByNetworkId, shipInputByNetworkId, shipOrbitByNetworkId,
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
                        int sourceMax = EffectiveMaxPopulation(
                            ref state, planetById[t.SourcePlanetId], sourceSizeOnly, sourcePlanetOnly.PlanetLevel);
                        sourcePlanetOnly.Population = math.min(
                            sourcePlanetOnly.Population + (int)t.Amount,
                            sourceMax);
                        planetStateById[t.SourcePlanetId] = sourcePlanetOnly;
                        ecb.SetComponent(planetById[t.SourcePlanetId], sourcePlanetOnly);
                        ClearInboundPeopleInTransit(ref state, t.TargetShipNetworkId, t.Amount, shipByNetworkId);
                        NoteInboundClaimRelease(ref claimReleases, t.TargetShipNetworkId, t.Sequence, t.Amount);
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
                            int sourceMax = EffectiveMaxPopulation(
                                ref state, planetById[t.SourcePlanetId], sourcePlanetSize, sourcePlanet.PlanetLevel);
                            ReturnLoadToPlanet(
                                ref state, ref sourcePlanet, shipEntity, t.Amount, sourceMax, t.SeatId, t.OwnsSeat != 0);
                            NoteInboundClaimRelease(ref claimReleases, t.TargetShipNetworkId, t.Sequence, t.Amount);
                            planetStateById[t.SourcePlanetId] = sourcePlanet;
                            ecb.SetComponent(planetById[t.SourcePlanetId], sourcePlanet);
                            PeopleTransportNetNotify.EndAndDestroy(
                                ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Returned);
                        }

                        continue;
                    }

                    bool atSlot = false;
                    if (PeopleTransportEscortLogic.TryEvaluateTroopSlot(
                            state.EntityManager, shipEntity, shipTransform, t.TargetShipNetworkId,
                            t.SeatId, t.Amount, hopClock, mapW, mapH, out float3 loadSlot))
                    {
                        atSlot = ToroidalMapEcs.ToroidalDistance(myPos, loadSlot, mapW, mapH)
                                 <= PeopleTransportMath.TroopSlotArriveDistance;
                    }
                    else
                    {
                        float shipRadius = PeopleTransportMath.GetShipHullRadius(shipTransform.Scale);
                        atSlot = PeopleTransportMath.CanDeliverLoadToShip(
                            myPos, shipTransform.Position, shipRadius, mapW, mapH);
                    }

                    if (atSlot &&
                        PeopleTransportMath.HasBriefTravelBeforeLoad(myPos, t.SpawnPosition, elapsed, mapW, mapH))
                    {
                        int sourcePlanetLevel = sourcePlanetState.PlanetLevel;
                        DeliverLoad(
                            ref state, shipEntity, ref shipState, t.Amount, team, sourcePlanetLevel, t.SeatId);
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
                    bool recallUnload = t.Returning != 0 ||
                        PeopleTransportMath.ShouldRecallUnloadHop(
                            t.TargetOwnerAtSpawn, planetState.Ownership);
                    if (recallUnload)
                    {
                        if (shipByNetworkId.TryGetValue(t.SourceShipNetworkId, out var homeShip) &&
                            shipStateByNetworkId.TryGetValue(t.SourceShipNetworkId, out var homeState) &&
                            shipTransformByNetworkId.TryGetValue(t.SourceShipNetworkId, out var homeXf) &&
                            !homeState.IsDead &&
                            !homeState.AwaitingTeamSelection)
                        {
                            bool backAtSlot = false;
                            if (PeopleTransportEscortLogic.TryEvaluateTroopSlot(
                                    state.EntityManager, homeShip, homeXf, t.SourceShipNetworkId,
                                    t.SeatId, t.Amount, hopClock, mapW, mapH, out float3 homeSlot))
                            {
                                float homeDist = ToroidalMapEcs.ToroidalDistance(
                                    myPos, homeSlot, mapW, mapH);
                                if (t.ReturnArmed == 0 &&
                                    homeDist > PeopleTransportMath.TroopSlotArriveDistance)
                                    t.ReturnArmed = 1;
                                backAtSlot = homeDist <= PeopleTransportMath.TroopSlotArriveDistance;
                            }
                            else
                            {
                                float shipRadius = PeopleTransportMath.GetShipHullRadius(homeXf.Scale);
                                backAtSlot = PeopleTransportMath.CanDeliverLoadToShip(
                                    myPos, homeXf.Position, shipRadius, mapW, mapH);
                                if (backAtSlot)
                                    t.ReturnArmed = 1;
                            }

                            // Armed once the sortie has left the slot, so a capture on the
                            // launch frame does not refund before the sphere has moved.
                            bool rejoinReady = t.ReturnArmed != 0 || elapsed > 0.4f;
                            if (backAtSlot && rejoinReady)
                            {
                                CompleteUnloadReturnToShip(
                                    ref state, homeShip, ref homeState, t.Amount, t.SeatId, t.Health);
                                shipStateByNetworkId[t.SourceShipNetworkId] = homeState;
                                ecb.SetComponent(homeShip, homeState);
                                PeopleTransportNetNotify.EndAndDestroy(
                                    ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Returned);
                            }
                        }
                        else
                        {
                            PeopleTransportNetNotify.EndAndDestroy(
                                ref ecb, entity, in t, myPos, PeopleTransportPoseStatus.Destroyed);
                        }

                        continue;
                    }

                    if (PeopleTransportMath.CanCompleteUnloadDelivery(
                            myPos, t.SpawnPosition, planetTransform.Position, planetSize, elapsed, mapW, mapH))
                    {
                        // Ship already debited at dispatch — only apply planet-side outcome here.
                        var planetEntity = planetById[t.TargetPlanetId];
                        int planetMax = EffectiveMaxPopulation(
                            ref state, planetEntity, planetSize, planetState.PlanetLevel);
                        var unloadOutcome = DeliverUnload(
                            ref planetState, t.Amount, team, planetMax);

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
                        if (unloadOutcome == PeopleUnloadOutcome.Friendly)
                        {
                            // Landed crew is on the planet now. Drop it from the in-flight reserve
                            // so the next hop in this tick is not counted twice.
                            garrisonReserved.TryGetValue(t.TargetPlanetId, out int held);
                            if (held > 0)
                            {
                                garrisonReserved[t.TargetPlanetId] = math.max(
                                    0, held - (int)math.round(math.max(0f, t.Amount)));
                            }
                        }

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

            ApplyInboundClaimReleases(state.EntityManager, claimReleases);
            claimReleases.Dispose();
            garrisonReserved.Dispose();
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
            ref SystemState state,
            ref PeopleTransportState transport,
            ref LocalTransform transform,
            bool isLoad,
            float3 myPos,
            float dt,
            float timeSeconds,
            float mapW,
            float mapH,
            NativeHashMap<int, Entity> shipByNetworkId,
            NativeHashMap<int, ShipState> shipStateByNetworkId,
            NativeHashMap<int, LocalTransform> shipTransformByNetworkId,
            NativeHashMap<int, ShipMoonDockState> shipMoonDockByNetworkId,
            NativeHashMap<int, ShipInput> shipInputByNetworkId,
            NativeHashMap<int, ShipOrbitState> shipOrbitByNetworkId,
            NativeHashMap<int, LocalTransform> planetTransformById,
            NativeHashMap<int, PlanetState> planetStateById)
        {
            bool loadEligible = false;
            int slotShipId = isLoad ? transport.TargetShipNetworkId : transport.SourceShipNetworkId;
            bool hasSlot = false;
            float3 slot = float3.zero;
            if (slotShipId != 0 &&
                shipByNetworkId.TryGetValue(slotShipId, out var slotShip) &&
                shipTransformByNetworkId.TryGetValue(slotShipId, out var slotXf))
            {
                hasSlot = PeopleTransportEscortLogic.TryEvaluateTroopSlot(
                    state.EntityManager, slotShip, slotXf, slotShipId,
                    transport.SeatId, transport.Amount, timeSeconds, mapW, mapH, out slot);
            }

            bool hasPlanet = false;
            float3 planetCenter = float3.zero;
            float planetSize = 1f;
            bool recallUnload = !isLoad && transport.Returning != 0;

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
                loadEligible = IsShipEligibleForLoad(
                    shipState, shipInput, shipOrbit, shipMoonDock, shipTransform.Position, sourceTransform.Position,
                    sourcePlanetSize, sourcePlanetState.PlanetLevel, transport.SourcePlanetId, mapW, mapH);
                hasPlanet = true;
                planetCenter = sourceTransform.Position;
                planetSize = sourcePlanetSize;
            }
            else if (isLoad &&
                     planetTransformById.TryGetValue(transport.SourcePlanetId, out var fallbackSource))
            {
                hasPlanet = true;
                planetCenter = fallbackSource.Position;
                planetSize = math.max(0.5f, fallbackSource.Scale);
            }
            else if (!isLoad)
            {
                if (!recallUnload &&
                    planetStateById.TryGetValue(transport.TargetPlanetId, out var destPlanet) &&
                    PeopleTransportMath.ShouldRecallUnloadHop(
                        transport.TargetOwnerAtSpawn, destPlanet.Ownership))
                    recallUnload = true;

                if (recallUnload)
                    transport.Returning = 1;

                if (!recallUnload &&
                    TryResolvePlanetTransform(
                        transport.TargetPlanetId, transport.SourcePlanetId, planetTransformById,
                        out var unloadPlanetTransform))
                {
                    hasPlanet = true;
                    planetCenter = unloadPlanetTransform.Position;
                    planetSize = math.max(0.5f, unloadPlanetTransform.Scale);
                }
            }

            if (isLoad && !loadEligible)
                transport.Returning = 1;
            else if (isLoad)
                transport.Returning = 0;

            if (!PeopleTransportEscortLogic.TryBuildTroopMoveIntent(
                    isLoad,
                    loadEligible,
                    recallUnload,
                    hasSlot,
                    slot,
                    hasPlanet,
                    planetCenter,
                    planetSize,
                    myPos,
                    mapW,
                    mapH,
                    out var intent))
                return;

            float3 offset = transport.FormationOffset;
            float3 offsetVel = transport.FormationVelocity;
            float3 prevIdle = transport.FormationPrevIdle;
            byte ready = transport.FormationReady;
            byte anchor = transport.FormationAnchor;
            PeopleTransportEscortLogic.StepTroopDrone(
                ref offset, ref offsetVel, ref prevIdle, ref ready, ref anchor,
                myPos, in intent, dt, mapW, mapH, out float3 pos, out float3 worldVel);
            transport.FormationOffset = offset;
            transport.FormationVelocity = offsetVel;
            transport.FormationPrevIdle = prevIdle;
            transport.FormationReady = ready;
            transport.FormationAnchor = anchor;
            transport.Velocity = worldVel;
            PeopleTransportConstants.WriteTransform(ref transform, pos, mapW, mapH);
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

            // [TITAN-ORBIT] Live re-check: ship pivot must sit in the annulus this tick.
            PlanetOrbitMath.GetRingRadiiWorld(planetSize, planetLevel, out float inner, out float outer, out _);
            float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetPos, mapW, mapH);
            return PlanetOrbitMath.IsInOrbitRing(dist, inner, outer);
        }

        static void DeliverLoad(
            ref SystemState state,
            Entity shipEntity,
            ref ShipState ship,
            float amount,
            TeamId team,
            int planetLevel,
            byte seatId)
        {
            // --- DeliverLoad (arrival) ---
            // CurrentPeople rises here; the reserved seat parks as an escort drone.
            int space = ship.PeopleCapacity - ship.CurrentPeople;
            int toAdd = (int)math.min(amount, space);
            if (toAdd > 0)
            {
                ship.CurrentPeople += toAdd;
            }

            _ = planetLevel;
            PeopleTransportEscortLogic.MarkSeatParked(state.EntityManager, shipEntity, seatId);
            int combine = 0;
            if (state.EntityManager.HasComponent<ShipPeopleTransferState>(shipEntity))
                combine = state.EntityManager.GetComponentData<ShipPeopleTransferState>(shipEntity).LastLoadCombineMax;
            PeopleTransportEscortLogic.SyncParkedEscorts(
                state.EntityManager, shipEntity, ship.CurrentPeople,
                PeopleTransportEscortLogic.ResolveChunk(math.max(1, ship.ShipLevel), combine),
                0);
            ClearPeopleInTransitOnShip(ref state, shipEntity, amount);
            LogPeopleEvent("Load", toAdd, team);
        }

        /// <summary>
        /// Hold limit including triangle LINK bonus. Base-only caps were clamping a
        /// full linked world back down to CAP and throwing away the extra crew.
        /// </summary>
        static int EffectiveMaxPopulation(ref SystemState state, Entity planetEntity, float planetSize, int planetLevel)
        {
            float bonus = 0f;
            if (state.EntityManager.HasComponent<PlanetGrowthState>(planetEntity))
                bonus = state.EntityManager.GetComponentData<PlanetGrowthState>(planetEntity).ConnectionBonusFraction;
            return PlanetPopulationMath.GetEffectiveMaxPopulation(planetSize, planetLevel, bonus);
        }

        /// <summary>
        /// Returns inbound crew to the planet when the ship left the orbit ring (or became ineligible).
        /// Client VFX retargets the same sphere home and shows +N on surface consume.
        /// <paramref name="maxPop"/> is the effective cap (base CAP plus LINK).
        /// </summary>
        static void ReturnLoadToPlanet(
            ref SystemState state,
            ref PlanetState planet,
            Entity shipEntity,
            float amount,
            int maxPop,
            byte seatId,
            bool ownsSeat)
        {
            planet.Population = math.min(planet.Population + (int)amount, maxPop);
            // Merge hops home on a parked capsule. Deleting that seat would erase
            // the escort the sphere was flying into.
            if (ownsSeat)
                PeopleTransportEscortLogic.RemoveSeat(state.EntityManager, shipEntity, seatId);
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

        /// <summary>
        /// Capture (or planet-gone) recall: cargo returns to the hull and parked
        /// escort seats rebuild from the new count.
        /// </summary>
        static void CompleteUnloadReturnToShip(
            ref SystemState state,
            Entity shipEntity,
            ref ShipState ship,
            float amount,
            byte seatId,
            float health)
        {
            // Same seat id the sortie left, so the parked orb reappears where the
            // sphere just rejoined — not a freshly hashed slot.
            PeopleTransportEscortLogic.RestoreParkedSeat(
                state.EntityManager, shipEntity, seatId, (int)amount, health);
            RefundUnloadToShip(ref ship, amount);
            int combine = 0;
            if (state.EntityManager.HasComponent<ShipPeopleTransferState>(shipEntity))
                combine = state.EntityManager.GetComponentData<ShipPeopleTransferState>(shipEntity).LastLoadCombineMax;
            PeopleTransportEscortLogic.SyncParkedEscorts(
                state.EntityManager, shipEntity, ship.CurrentPeople,
                PeopleTransportEscortLogic.ResolveChunk(math.max(1, ship.ShipLevel), combine),
                0);
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

        /// <summary>Earlier load that will not land, and the people later claims should drop.</summary>
        struct PeopleInboundClaimRelease
        {
            public int ShipNetworkId;
            public uint Sequence;
            public int Amount;
        }

        static void NoteInboundClaimRelease(
            ref NativeList<PeopleInboundClaimRelease> releases,
            int shipNetworkId,
            uint sequence,
            float amount)
        {
            int people = (int)math.round(math.max(0f, amount));
            if (people <= 0 || shipNetworkId == 0 || sequence == 0)
                return;
            releases.Add(new PeopleInboundClaimRelease
            {
                ShipNetworkId = shipNetworkId,
                Sequence = sequence,
                Amount = people,
            });
        }

        static void ReleaseInboundClaim(EntityManager em, int shipNetworkId, uint sequence, float amount)
        {
            var releases = new NativeList<PeopleInboundClaimRelease>(1, Allocator.Temp);
            NoteInboundClaimRelease(ref releases, shipNetworkId, sequence, amount);
            ApplyInboundClaimReleases(em, releases);
            releases.Dispose();
        }

        /// <summary>
        /// Shrink seat claims on loads that spawned after a hop which will not land.
        /// One query for the whole batch — this runs on abort, not every tick.
        /// </summary>
        static void ApplyInboundClaimReleases(EntityManager em, NativeList<PeopleInboundClaimRelease> releases)
        {
            if (releases.Length == 0)
                return;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadWrite<PeopleTransportState>(),
                ComponentType.ReadOnly<PeopleTransportTag>());
            using var hops = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < hops.Length; i++)
            {
                var hop = em.GetComponentData<PeopleTransportState>(hops[i]);
                if (hop.IsLoad == 0 || hop.Sequence == 0)
                    continue;

                int claim = hop.SeatClaimPeople;
                bool changed = false;
                for (int r = 0; r < releases.Length; r++)
                {
                    PeopleInboundClaimRelease rel = releases[r];
                    if (rel.Amount <= 0 || hop.TargetShipNetworkId != rel.ShipNetworkId)
                        continue;
                    if (hop.Sequence <= rel.Sequence)
                        continue;
                    claim = math.max(0, claim - rel.Amount);
                    changed = true;
                }

                if (!changed || claim == hop.SeatClaimPeople)
                    continue;
                hop.SeatClaimPeople = claim;
                em.SetComponentData(hops[i], hop);
            }
        }

        static PeopleUnloadOutcome DeliverUnload(ref PlanetState planet, float amount, TeamId team, int maxPop)
        {
            // --- DeliverUnload ---
            // maxPop is the effective cap (CAP + LINK), not the base size/level cap.
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
                    if (transport.OwnsSeat != 0)
                        PeopleTransportEscortLogic.RemoveSeat(em, ships[i], transport.SeatId);
                    ReleaseInboundClaim(
                        em, transport.TargetShipNetworkId, transport.Sequence, transport.Amount);
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
