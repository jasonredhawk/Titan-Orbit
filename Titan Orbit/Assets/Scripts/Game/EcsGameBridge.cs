using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// MonoBehaviour-safe read API for UI, camera, and bridges. Resolves the correct
    /// NetCode world (client vs host server) and exposes match/map/planet state.
    /// </summary>
    public static class EcsGameBridge
    {
        /// <summary>NetCode client simulation world — prediction and local input run here.</summary>
        public static World ClientWorld => ClientServerBootstrap.ClientWorld;

        /// <summary>NetCode server simulation world — authoritative on dedicated server and local host.</summary>
        public static World ServerWorld => ClientServerBootstrap.ServerWorld;

        // --- World selection ---

        /// <summary>
        /// ECS world used for rendering, camera follow, and GameObject proxy sync.
        /// Host and dedicated clients both read ClientWorld so proxies use NetCode presentation
        /// (owner prediction + remote interpolation), not raw ServerWorld simulation ticks.
        /// </summary>
        public static World GetVisualizationWorld()
        {
            // --- Preferred: client presentation world once NetCode is in-game ---
            // [NETCODE] ClientWorld owns ghost presentation — interpolation for remotes, prediction for local owner.
            if (ClientWorld != null && ClientWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld))
                return ClientWorld;

            // --- Headless dedicated: no GameObject presentation ---
            // [TITAN-ORBIT] Headless dedicated — never drive GameObject proxies from ServerWorld.
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return null;

            // --- Dual-world Local Host before client is in-game ---
            // [TITAN-ORBIT] basics15 (H27/H28): while ClientWorld existed but was not gameplay-ready,
            // the old ServerWorld fallback spawned ~454 asteroid proxies, then ClientWorld rebuild
            // tore them down (102ms + 78ms DrawAsteroids spikes). Wait for ClientWorld instead.
            // IsLocalHost() cannot be used here — it itself requires gameplay-ready.
            bool dualWorldPresent =
                !TitanOrbitSessionManager.IsDedicatedOnlineClient &&
                ClientWorld != null && ClientWorld.IsCreated &&
                ServerWorld != null && ServerWorld.IsCreated;
            if (dualWorldPresent)
                return null;

            // --- Client-only join / single-world edge cases ---
            // [NETCODE] No dual-world wait: allow ServerWorld only when there is no ClientWorld
            // (legacy host tools). Prefer ClientWorld when it exists even before in-game.
            if (ClientWorld != null && ClientWorld.IsCreated)
                return ClientWorld;

            if (ServerWorld != null && ServerWorld.IsCreated)
                return ServerWorld;

            return null;
        }

        /// <summary>
        /// World that owns the local player's ship ghost tags and predicted pose.
        /// ClientWorld for host + dedicated clients; visualization world otherwise.
        /// </summary>
        public static World GetLocalPlayerShipWorld()
        {
            if (ClientWorld != null && ClientWorld.IsCreated &&
                (IsLocalHost() || TitanOrbitSessionManager.IsDedicatedOnlineClient))
                return ClientWorld;

            return GetVisualizationWorld();
        }

        // --- Local ship queries ---

        /// <summary>
        /// World position of the local ship for UI/aim — moon-dock cinematic, then client-world ECS pose.
        /// </summary>
        public static bool TryGetLocalShipPosition(out Vector3 position)
        {
            if (ShipMoonDockVisualApplier.TryGetLocalFollowPosition(out position))
                return true;

            if (TryGetLocalShipTransform(out var lt))
            {
                position = lt.Position;
                return true;
            }

            position = default;
            return false;
        }

        /// <summary>
        /// Visual proxy pose when available (onBeforeRender sync); otherwise same ECS pose as <see cref="TryGetLocalShipPosition"/>.
        /// </summary>
        public static bool TryGetLocalShipPresentationPosition(out Vector3 position)
        {
            if (ShipDisplayPose.HasLocalPose)
            {
                position = ShipDisplayPose.LocalPosition;
                return true;
            }

            return TryGetLocalShipPosition(out position);
        }

        /// <summary>Local ship <see cref="LocalTransform"/> from <see cref="GetLocalPlayerShipWorld"/>.</summary>
        public static bool TryGetLocalShipTransform(out LocalTransform transform) =>
            TryGetLocalShipTransformFromWorld(GetLocalPlayerShipWorld(), out transform);

        /// <summary>
        /// Resolves local ship pose from a specific ECS world using tag, ownership, CommandTarget, and NetworkId fallbacks.
        /// </summary>
        public static bool TryGetLocalShipTransformFromWorld(World world, out LocalTransform transform)
        {
            transform = default;

            // [TITAN-ORBIT] Team picker / rejoin screens hide ship control until player commits to a team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // [TITAN-ORBIT] No local ship camera/control until the galaxy build finishes.
            if (IsNetworkInGame() && !IsMapLoadingComplete())
                return false;

            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;

            // --- Fallback chain: explicit tag → NetCode local owner → command target → network id ---
            if (TryGetShipTransform(em, ComponentType.ReadOnly<LocalPlayerShipTag>(), out transform))
                return true;

            if (TryGetShipTransform(em, ComponentType.ReadOnly<GhostOwnerIsLocal>(), out transform))
                return true;

            if (TryGetShipFromCommandTarget(em, out transform))
                return true;

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0 && TryGetShipTransformByNetworkId(em, localId, out transform))
                return true;

            return false;
        }

        /// <summary>Gameplay velocity mirror from <see cref="ShipKinematics"/> on the local ship entity.</summary>
        public static bool TryGetLocalShipVelocity(out Vector3 velocity)
        {
            velocity = default;

            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryGetLocalShipEntity(em, out var shipEntity) ||
                !em.HasComponent<ShipKinematics>(shipEntity))
                return false;

            velocity = em.GetComponentData<ShipKinematics>(shipEntity).Velocity;
            return true;
        }

        /// <summary>True when map is loaded, team flow allows control, and a local ship position resolves.</summary>
        public static bool HasLocalPlayerShip() =>
            IsMapLoadingComplete() &&
            !ClientTeamFlowState.ShouldSuppressLocalPlayerControl() &&
            TryGetLocalShipPosition(out _);

        /// <summary>
        /// True when the server still has this player's ship from a prior session on the same match.
        /// </summary>
        public static bool TryGetRejoinableShipForLocalPlayer(out ShipState shipState)
        {
            shipState = default;
            var world = ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            int localId = GetLocalNetworkId(world);
            if (localId <= 0)
                return false;

            if (!TryGetShipStateByNetworkId(world.EntityManager, localId, out shipState))
                return false;

            return shipState.Team != TeamId.None && !shipState.AwaitingTeamSelection;
        }

        /// <summary>
        /// Full <see cref="ShipState"/> for HUD and UI — tries LocalPlayerShipTag, ownership, CommandTarget, NetworkId.
        /// </summary>
        public static bool TryGetLocalShipState(out ShipState state)
        {
            state = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipState));
            if (tagged.CalculateEntityCount() > 0)
            {
                state = tagged.GetSingleton<ShipState>();
                return true;
            }

            if (TryGetLocalOwnedShipEntity(em, out var ownedShip) &&
                em.HasComponent<ShipState>(ownedShip))
            {
                state = em.GetComponentData<ShipState>(ownedShip);
                return true;
            }

            if (TryGetShipStateFromCommandTarget(em, out state))
                return true;

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0 && TryGetShipStateByNetworkId(em, localId, out state))
                return true;

            return false;
        }

        /// <summary>Bottom-bar attribute upgrade levels for the local ship (zeros when component missing).</summary>
        public static bool TryGetLocalShipAttributeUpgrades(out ShipAttributeUpgradeState attributes)
        {
            attributes = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryGetLocalShipEntity(em, out var shipEntity))
                return false;

            if (!em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                return true;

            attributes = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
            return true;
        }

        /// <summary>Match timer and started flag from <see cref="MatchStateSingleton"/>.</summary>
        public static bool TryGetMatchState(out MatchStateSingleton match)
        {
            match = default;
            var world = ClientWorld ?? ServerWorld;
            if (world == null || !world.IsCreated)
                return false;

            using var query = world.EntityManager.CreateEntityQuery(typeof(MatchStateSingleton));
            return query.TryGetSingleton(out match);
        }

        /// <summary>Death / respawn timer state for the local ship — drives death screen UI.</summary>
        public static bool TryGetLocalShipDeathState(out ShipDeathState death)
        {
            death = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out var shipEntity) &&
                em.HasComponent<ShipDeathState>(shipEntity))
            {
                death = em.GetComponentData<ShipDeathState>(shipEntity);
                return true;
            }

            return false;
        }

        /// <summary>Planet orbit slot state for moon-orbit station UI and camera.</summary>
        public static bool TryGetLocalShipOrbitState(out ShipOrbitState orbitState)
        {
            orbitState = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipOrbitState));
            if (tagged.CalculateEntityCount() > 0)
            {
                orbitState = tagged.GetSingleton<ShipOrbitState>();
                return true;
            }

            if (TryGetLocalOwnedShipEntity(em, out var ownedShip) &&
                em.HasComponent<ShipOrbitState>(ownedShip))
            {
                orbitState = em.GetComponentData<ShipOrbitState>(ownedShip);
                return true;
            }

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0 && TryGetShipOrbitStateByNetworkId(em, localId, out orbitState))
                return true;

            return false;
        }

        /// <summary>Moon landing cinematic progress — used by dock visual applier and camera follow.</summary>
        public static bool TryGetLocalShipMoonDockState(out ShipMoonDockState moonDock)
        {
            moonDock = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out var shipEntity) &&
                em.HasComponent<ShipMoonDockState>(shipEntity))
            {
                moonDock = em.GetComponentData<ShipMoonDockState>(shipEntity);
                return true;
            }

            return false;
        }

        /// <summary>Resolves local ship <see cref="Entity"/> in an arbitrary world (host diagnostics).</summary>
        public static bool TryGetLocalShipEntityOnWorld(World world, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (world == null || !world.IsCreated)
                return false;

            return TryGetLocalShipEntity(world.EntityManager, out shipEntity);
        }

        /// <summary>Last applied <see cref="ShipInput"/> on the local ship ghost (client prediction world).</summary>
        public static bool TryGetLocalShipInput(out ShipInput input)
        {
            input = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out var shipEntity) &&
                em.HasComponent<ShipInput>(shipEntity))
            {
                input = em.GetComponentData<ShipInput>(shipEntity);
                return true;
            }

            return false;
        }

        /// <summary>Whether the player is holding deposit — gem economy HUD indicator.</summary>
        public static bool TryGetLocalShipDepositIntent(out bool wantDepositGems)
        {
            wantDepositGems = false;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out var shipEntity) &&
                em.HasComponent<ShipDepositIntent>(shipEntity))
            {
                wantDepositGems = em.GetComponentData<ShipDepositIntent>(shipEntity).WantDepositGems;
                return true;
            }

            return false;
        }

        /// <summary>Equipped component slots for orbit-station and upgrade UI.</summary>
        public static bool TryGetLocalShipLoadout(out ShipLoadoutState loadout)
        {
            loadout = default;
            var world = GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (TryGetLocalShipEntity(em, out var shipEntity) &&
                em.HasComponent<ShipLoadoutState>(shipEntity))
            {
                loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                return true;
            }

            return false;
        }

        // --- Session / network readiness ---

        /// <summary>
        /// True when NetCode reports gameplay-ready connection (in-game, not just connected to lobby).
        /// </summary>
        public static bool IsNetworkInGame()
        {
            if (ClientWorld != null && ClientWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld))
                return true;

#if UNITY_SERVER
            // [NETCODE] Headless dedicated server may have ServerWorld only — treat in-game when connection ready.
            if ((ClientWorld == null || !ClientWorld.IsCreated) &&
                ServerWorld != null && ServerWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientConnectionReady(ServerWorld))
                return true;
#endif
            return false;
        }

        /// <summary>
        /// True when this machine runs both client and server worlds in one process (editor host / MPPM host).
        /// </summary>
        public static bool IsLocalHost()
        {
            if (TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return false;

            return ClientWorld != null && ClientWorld.IsCreated && ServerWorld != null && ServerWorld.IsCreated &&
                   TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld) &&
                   TitanOrbitSessionManager.IsClientConnectionReady(ServerWorld);
        }

        /// <summary>NetCode <see cref="NetworkId"/> for this client's connection entity.</summary>
        public static int GetLocalNetworkId()
        {
            return GetLocalNetworkId(ClientWorld);
        }

        /// <summary>
        /// Map generation finished — host reads <see cref="MapStateSingleton"/>; remote clients infer from ghost stream.
        /// Once true for a session, stays true until disconnect so late ghost arrivals do not flash loading UI.
        /// </summary>
        public static bool IsMapLoadingComplete()
        {
            if (!IsNetworkInGame())
            {
                ResetRemoteMapLoadTracking();
                return false;
            }

            // [TITAN-ORBIT] Latch — replicated asteroid/planet counts can tick upward after the first "complete".
            if (s_MapLoadingLatchedComplete)
                return true;

            bool complete = EvaluateMapLoadingComplete();
            if (complete)
                s_MapLoadingLatchedComplete = true;
            return complete;
        }

        /// <summary>Computes map-ready from server singleton or remote ghost heuristics (no latch).</summary>
        static bool EvaluateMapLoadingComplete()
        {
            // --- Local host: ServerWorld owns map generation and MapStateSingleton ---
            if (IsLocalHost() &&
                ServerWorld != null && ServerWorld.IsCreated &&
                TryGetMapLoadingComplete(ServerWorld, out var serverComplete))
                return serverComplete;

            // --- Remote / dedicated Windows client ---
            // [TITAN-ORBIT] Do NOT return ClientWorld MapStateSingleton.LoadingComplete==false here.
            // That short-circuited before proxy heuristics and left the loading bar capped at 99% forever
            // when the singleton ghost was missing, stale, or never flipped true (common on Relay).
            if (IsRemoteMapObserverClient() && ClientWorld != null && ClientWorld.IsCreated)
            {
                // Trust a positive ghost flag if it arrives; otherwise use proxy / settle heuristics.
                if (TryGetMapLoadingComplete(ClientWorld, out var ghostComplete) && ghostComplete)
                    return true;
                return TryGetReplicatedMapLoadComplete(ClientWorld);
            }

            if (ClientWorld != null && ClientWorld.IsCreated &&
                TryGetMapLoadingComplete(ClientWorld, out var clientComplete))
                return clientComplete;

            return false;
        }

        /// <summary>0–1 loading bar progress for loading screen UI.</summary>
        public static bool TryGetMapLoadingProgress(out float progress)
        {
            progress = 0f;
            if (!IsNetworkInGame())
                return false;

            if (TryGetMapLoadingState(out var completedSteps, out var totalSteps, out var loadingComplete, out progress))
                return true;

            if (ClientWorld != null && ClientWorld.IsCreated)
            {
                progress = EstimateClientMapLoadProgress(ClientWorld, out completedSteps, out totalSteps);
                return totalSteps > 0 || progress > 0f;
            }

            return false;
        }

        /// <summary>Completed vs total spawn steps for loading screen step counter.</summary>
        public static bool TryGetMapLoadingStepCounts(out int completedSteps, out int totalSteps)
        {
            completedSteps = 0;
            totalSteps = 0;
            if (!IsNetworkInGame())
                return false;

            return TryGetVisibleMapLoadingStepCounts(out completedSteps, out totalSteps);
        }

        /// <summary>
        /// Loading UI contract (one bar, two phases — not two bars):
        /// <list type="number">
        /// <item><see cref="MapSessionMetaRpc"/> — server sends map totals once (how many to build).</item>
        /// <item>Phase A while Settling: show GhostSpawn Instantiates so the bar is not frozen at 0.</item>
        /// <item>Phase B: planet/asteroid GameObject proxies (<see cref="EcsWorldVisualizer.MapLoadingProxyCount"/>).</item>
        /// </list>
        /// Two bars would not stop Crash!!! — only safer gates do. This just makes progress honest.
        /// </summary>
        static bool TryGetVisibleMapLoadingStepCounts(out int completedSteps, out int totalSteps)
        {
            completedSteps = 0;
            totalSteps = 0;

            // --- Denominator: server meta only (latched) ---
            // [TITAN-ORBIT] Do not use layout-buffer length or settings midpoint as "/ N" —
            // that looked like “server data arriving” and jumped mid-load. Wait for meta.
            if (MapSessionMetaCache.HasMeta && MapSessionMetaCache.LoadingTotalSteps > 0)
                s_LatchedLoadingTotalSteps = MapSessionMetaCache.LoadingTotalSteps;

            totalSteps = s_LatchedLoadingTotalSteps;
            if (totalSteps <= 0)
                return false;

            // --- Numerator (never regress to 0 when Settling ends) ---
            // [TITAN-ORBIT] Phase A used InstantiatesSession only while Settling. When Settling OFF
            // and proxies were still 0 (player bake missing Pending), the bar snapped to 0/N and
            // stalled on "Building map visuals". Use Max(proxies, Instantiates) so the UI holds
            // receive progress until GO proxies catch up. MapBodyHybridVisualRequestSystem backfills
            // Pending when bake omitted it.
            int proxies = EcsWorldVisualizer.MapLoadingProxyCount;
            int receive = TitanOrbitJoinLoadCounters.InstantiatesSession;
            completedSteps = proxies > receive ? proxies : receive;

            if (totalSteps > 0 && completedSteps > totalSteps)
                completedSteps = totalSteps;
            return true;
        }

        // --- Map loading helpers (private) ---

        /// <summary>Counts server-side home planets for remote loading denominator refinement.</summary>
        static int CountServerHomePlanets(World server)
        {
            if (server == null || !server.IsCreated)
                return 0;

            using var homes = server.EntityManager.CreateEntityQuery(typeof(HomePlanetTag));
            return homes.CalculateEntityCount();
        }

        /// <summary>Aggregates map loading from host server, client singleton, or replicated body heuristics.</summary>
        static bool TryGetMapLoadingState(
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            if (IsLocalHost() &&
                ServerWorld != null && ServerWorld.IsCreated)
            {
                if (TryGetVisibleMapLoadingStepCounts(out completedSteps, out totalSteps))
                {
                    TryGetMapLoadingComplete(ServerWorld, out loadingComplete);
                    progress = totalSteps > 0
                        ? Mathf.Clamp01((float)completedSteps / totalSteps)
                        : 0f;
                    return true;
                }

                if (TryReadMapLoadingState(ServerWorld, out completedSteps, out totalSteps, out loadingComplete, out progress))
                    return true;
            }

            if (ClientWorld != null && ClientWorld.IsCreated &&
                TryReadMapLoadingState(ClientWorld, out completedSteps, out totalSteps, out loadingComplete, out progress))
                return true;

            if (IsRemoteMapObserverClient() && ClientWorld != null && ClientWorld.IsCreated &&
                TryReadReplicatedMapLoadProgress(ClientWorld, out completedSteps, out totalSteps, out loadingComplete, out progress))
                return true;

            if (ServerWorld != null && ServerWorld.IsCreated)
                return TryReadSpawnedBodyProgress(ServerWorld, out completedSteps, out totalSteps, out loadingComplete, out progress);

            return false;
        }

        /// <summary>Reads authoritative <see cref="MapStateSingleton"/> progress fields from a world.</summary>
        static bool TryReadMapLoadingState(
            World world,
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            if (world == null || !world.IsCreated)
                return false;

            if (!world.EntityManager.CreateEntityQuery(typeof(MapStateSingleton))
                    .TryGetSingleton<MapStateSingleton>(out var map))
                return false;

            completedSteps = map.LoadingCompletedSteps;
            totalSteps = map.LoadingTotalSteps;
            loadingComplete = map.LoadingComplete;
            progress = loadingComplete
                ? 1f
                : totalSteps > 0
                    ? Mathf.Clamp01((float)completedSteps / totalSteps)
                    : Mathf.Clamp01(map.LoadingProgress);
            return totalSteps > 0 || map.LoadingProgress > 0f || loadingComplete;
        }

        /// <summary>Fallback progress when singleton exists but ghosts are the visible numerator.</summary>
        static bool TryReadSpawnedBodyProgress(
            World world,
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            var em = world.EntityManager;
            using var planets = em.CreateEntityQuery(typeof(PlanetState));
            using var asteroids = em.CreateEntityQuery(typeof(AsteroidState));
            completedSteps = planets.CalculateEntityCount() + asteroids.CalculateEntityCount();
            if (completedSteps <= 0)
                return false;

            if (em.CreateEntityQuery(typeof(MapStateSingleton)).TryGetSingleton<MapStateSingleton>(out var map) &&
                map.LoadingTotalSteps > 0)
            {
                totalSteps = map.LoadingTotalSteps;
                loadingComplete = map.LoadingComplete;
            }
            else
            {
                return false;
            }

            progress = loadingComplete ? 1f : Mathf.Clamp01((float)completedSteps / totalSteps);
            return true;
        }

        /// <summary>Client-side map progress estimate when singleton is missing or incomplete.</summary>
        static float EstimateClientMapLoadProgress(World client, out int completedSteps, out int totalSteps)
        {
            completedSteps = 0;
            totalSteps = 0;

            var em = client.EntityManager;
            bool hasMapState = em.CreateEntityQuery(typeof(MapStateSingleton))
                .TryGetSingleton<MapStateSingleton>(out var map);

            if (hasMapState)
            {
                completedSteps = map.LoadingCompletedSteps;
                totalSteps = map.LoadingTotalSteps;
                if (map.LoadingComplete)
                    return 1f;
                if (totalSteps > 0)
                    return Mathf.Clamp01((float)completedSteps / totalSteps);
                if (map.LoadingProgress > 0f)
                    return Mathf.Clamp01(map.LoadingProgress);
            }

            using var planets = em.CreateEntityQuery(typeof(PlanetState));
            using var asteroids = em.CreateEntityQuery(typeof(AsteroidState));
            completedSteps = planets.CalculateEntityCount() + asteroids.CalculateEntityCount();

            if (hasMapState && map.LoadingTotalSteps > 0)
            {
                totalSteps = map.LoadingTotalSteps;
                return Mathf.Clamp01((float)math.min(completedSteps, totalSteps) / totalSteps);
            }

            if (em.CreateEntityQuery(typeof(MapLayoutEntryElement)).TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout) &&
                layout.Length > 0)
            {
                totalSteps = layout.Length;
                return Mathf.Clamp01((float)completedSteps / totalSteps);
            }

            int homeCount = CountReplicatedHomePlanets(em);
            totalSteps = ResolveRemoteMapExpectedTotal(homeCount);
            if (completedSteps <= 0)
                return 0f;

            return Mathf.Clamp01((float)completedSteps / totalSteps);
        }

        // --- Local ship entity resolution (private) ---

        /// <summary>Finds ship transform via NetCode <see cref="CommandTarget"/> on in-game connections.</summary>
        static bool TryGetShipFromCommandTarget(EntityManager em, out LocalTransform transform)
        {
            transform = default;
            using var connections = em.CreateEntityQuery(
                typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(CommandTarget));
            using var targets = connections.ToComponentDataArray<CommandTarget>(Allocator.Temp);
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i].targetEntity;
                if (target == Entity.Null || !em.Exists(target))
                    continue;
                if (!em.HasComponent<ShipTag>(target) || !em.HasComponent<LocalTransform>(target))
                    continue;
                transform = em.GetComponentData<LocalTransform>(target);
                return true;
            }

            return false;
        }

        /// <summary>First ship with <see cref="GhostOwnerIsLocal"/> enableable flag set.</summary>
        static bool TryGetLocalOwnedShipEntity(EntityManager em, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            using var query = em.CreateEntityQuery(typeof(GhostOwnerIsLocal), typeof(ShipTag));
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!em.IsComponentEnabled<GhostOwnerIsLocal>(entities[i]))
                    continue;
                shipEntity = entities[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Central local-ship lookup: NetworkId match, GhostOwnerIsLocal, LocalPlayerShipTag, then CommandTarget.
        /// Returns false while team/rejoin flow suppresses control so presentation cannot latch onto an
        /// orphan GhostOwner ship during map load.
        /// </summary>
        static bool TryGetLocalShipEntity(EntityManager em, out Entity shipEntity)
        {
            shipEntity = Entity.Null;

            // [TITAN-ORBIT] Same gate as TryGetLocalShipTransformFromWorld — ShipVisualSyncSystem and
            // camera follow this path; without it, a rejoin orphan drove presentation before Join Team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            int localId = GetLocalNetworkId(ClientWorld);
            if (localId > 0)
            {
                using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
                using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                using var entities = query.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < owners.Length; i++)
                {
                    if (owners[i].NetworkId != localId)
                        continue;
                    shipEntity = entities[i];
                    return true;
                }
            }

            if (TryGetLocalOwnedShipEntity(em, out shipEntity))
                return true;

            using var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipTag));
            if (tagged.CalculateEntityCount() > 0)
            {
                using var entities = tagged.ToEntityArray(Allocator.Temp);
                if (entities.Length > 0)
                {
                    shipEntity = entities[0];
                    return true;
                }
            }

            using var connections = em.CreateEntityQuery(
                typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(CommandTarget));
            using var targets = connections.ToComponentDataArray<CommandTarget>(Allocator.Temp);
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i].targetEntity;
                if (target == Entity.Null || !em.Exists(target) || !em.HasComponent<ShipTag>(target))
                    continue;
                shipEntity = target;
                return true;
            }

            return false;
        }

        /// <summary>Reads <see cref="ShipState"/> from the ship pointed at by <see cref="CommandTarget"/>.</summary>
        static bool TryGetShipStateFromCommandTarget(EntityManager em, out ShipState state)
        {
            state = default;
            using var connections = em.CreateEntityQuery(
                typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(CommandTarget));
            using var targets = connections.ToComponentDataArray<CommandTarget>(Allocator.Temp);
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i].targetEntity;
                if (target == Entity.Null || !em.Exists(target))
                    continue;
                if (!em.HasComponent<ShipTag>(target) || !em.HasComponent<ShipState>(target))
                    continue;
                state = em.GetComponentData<ShipState>(target);
                return true;
            }

            return false;
        }

        /// <summary>First ship matching marker component (tag or GhostOwnerIsLocal).</summary>
        static bool TryGetShipTransform(EntityManager em, ComponentType marker, out LocalTransform transform)
        {
            transform = default;
            using var query = em.CreateEntityQuery(marker, typeof(ShipTag), typeof(LocalTransform));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (marker == ComponentType.ReadOnly<GhostOwnerIsLocal>() &&
                    !em.IsComponentEnabled<GhostOwnerIsLocal>(entities[i]))
                    continue;

                transform = transforms[i];
                return true;
            }

            return false;
        }

        /// <summary>Ship pose lookup by replicated <see cref="GhostOwner.NetworkId"/>.</summary>
        static bool TryGetShipTransformByNetworkId(EntityManager em, int networkId, out LocalTransform transform)
        {
            transform = default;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(LocalTransform));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                transform = transforms[i];
                return true;
            }

            return false;
        }

        /// <summary><see cref="ShipState"/> lookup by <see cref="GhostOwner.NetworkId"/>.</summary>
        static bool TryGetShipStateByNetworkId(EntityManager em, int networkId, out ShipState state)
        {
            state = default;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(ShipState));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                state = states[i];
                return true;
            }

            return false;
        }

        /// <summary><see cref="ShipOrbitState"/> lookup by <see cref="GhostOwner.NetworkId"/>.</summary>
        static bool TryGetShipOrbitStateByNetworkId(EntityManager em, int networkId, out ShipOrbitState orbitState)
        {
            orbitState = default;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(ShipOrbitState));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipOrbitState>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                orbitState = states[i];
                return true;
            }

            return false;
        }

        /// <summary>First in-game connection's <see cref="NetworkId"/> on the client world.</summary>
        static int GetLocalNetworkId(World clientWorld)
        {
            if (clientWorld == null || !clientWorld.IsCreated)
                return -1;

            var em = clientWorld.EntityManager;
            using var ids = em.CreateEntityQuery(
                    typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : -1;
        }

        /// <summary>Whether any connection entity has <see cref="NetworkStreamInGame"/>.</summary>
        static bool HasNetworkStreamInGame(World world)
        {
            if (world == null || !world.IsCreated) return false;
            return world.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame)).CalculateEntityCount() > 0;
        }

        /// <summary>Reads <see cref="MapStateSingleton.LoadingComplete"/> from a world.</summary>
        static bool TryGetMapLoadingComplete(World world, out bool loadingComplete)
        {
            loadingComplete = false;
            if (world == null || !world.IsCreated) return false;
            if (!world.EntityManager.CreateEntityQuery(typeof(MapStateSingleton)).TryGetSingleton<MapStateSingleton>(out var map))
                return false;
            loadingComplete = map.LoadingComplete;
            return true;
        }

        /// <summary>Remote LAN/MPPM/dedicated clients have no ServerWorld map singleton.</summary>
        static bool IsRemoteMapObserverClient()
        {
            if (IsLocalHost())
                return false;

            if (TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return true;

            if (TitanOrbit.NetCode.TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                return true;

            // LAN client in main editor may still have a suspended ServerWorld from menu bootstrap.
            return ClientWorld != null && ClientWorld.IsCreated && IsNetworkInGame();
        }

        const float RemoteMapStableSeconds = 0.5f;
        const int RemoteMapMinAsteroids = 32;

        /// <summary>Cached expected spawn total for remote loading bar denominator.</summary>
        static int s_RemoteMapExpectedTotal = -1;
        /// <summary>Last observed replicated planet count — stability detection.</summary>
        static int s_RemoteMapPlanetCount = -1;
        /// <summary>Last observed replicated asteroid count — stability detection.</summary>
        static int s_RemoteMapAsteroidCount = -1;
        /// <summary>realtimeSinceStartup when body counts last changed — settle window before "complete".</summary>
        static float s_RemoteMapStableSince = -1f;
        /// <summary>Stays true after first successful <see cref="IsMapLoadingComplete"/> until session reset.</summary>
        static bool s_MapLoadingLatchedComplete;
        /// <summary>Last known team count for lobby UI — avoids flicker when home ghosts briefly desync.</summary>
        static int s_LatchedActiveTeamCount;
        /// <summary>
        /// Loading-screen "/ N" denominator for the current join. Latched so the UI does not jump
        /// from settings midpoint to layout length mid-stream.
        /// </summary>
        static int s_LatchedLoadingTotalSteps;

        /// <summary>
        /// realtimeSinceStartup when proxy count last changed after Settling OFF — plateau timeout.
        /// </summary>
        static float s_ProxyCatchupWaitSince = -1f;

        /// <summary>Last MapLoadingProxyCount while waiting for load complete (detects stalls).</summary>
        static int s_LastProxyCountForCatchup = -1;

        /// <summary>Clears remote map heuristics when disconnecting or leaving in-game state.</summary>
        static void ResetRemoteMapLoadTracking()
        {
            s_RemoteMapExpectedTotal = -1;
            s_RemoteMapPlanetCount = -1;
            s_RemoteMapAsteroidCount = -1;
            s_RemoteMapStableSince = -1f;
            s_MapLoadingLatchedComplete = false;
            s_LatchedActiveTeamCount = 0;
            s_LatchedLoadingTotalSteps = 0;
            s_ProxyCatchupWaitSince = -1f;
            s_LastProxyCountForCatchup = -1;
            // [TITAN-ORBIT] Drop latched MapSessionMetaRpc so the next join does not reuse old totals.
            MapSessionMetaCache.Clear();
        }

        /// <summary>
        /// Remote clients never learn the true spawn queue length; keep a fixed denominator for the loading bar.
        /// Refines once replicated home planets reveal team count.
        /// </summary>
        static int ResolveRemoteMapExpectedTotal(int homeCount)
        {
            if (homeCount > 0)
            {
                s_RemoteMapExpectedTotal = EstimateExpectedRemoteMapBodies(homeCount);
                return s_RemoteMapExpectedTotal;
            }

            return s_RemoteMapExpectedTotal > 0 ? s_RemoteMapExpectedTotal : EstimateMapSpawnStepsFromSettings(0);
        }

        /// <summary>Reads neutral + asteroid midpoint from <see cref="MapGenerationSettingsCache"/>.</summary>
        static int EstimateMapSpawnStepsFromSettings(int homeCount)
        {
            if (MapGenerationSettingsCache.Settings != null)
            {
                var s = MapGenerationSettingsCache.Settings;
                int neutrals = (s.minNeutralPlanets + s.maxNeutralPlanets + 1) / 2;
                int asteroids = (s.asteroidsAtMinMapSize + s.asteroidsAtMaxMapSize + 1) / 2;
                if (homeCount > 0)
                    return homeCount + neutrals + asteroids;

                int teams = (s.minTeamsPerMatch + s.maxTeamsPerMatch + 1) / 2;
                return teams + neutrals + asteroids;
            }

            return homeCount > 0 ? homeCount + 12 + 666 : 678;
        }

        /// <summary>Counts replicated home/planet/asteroid ghosts on the client world.</summary>
        static bool TryGetReplicatedMapBodyCounts(World client, out int homeCount, out int planetCount, out int asteroidCount)
        {
            homeCount = 0;
            planetCount = 0;
            asteroidCount = 0;
            if (client == null || !client.IsCreated)
                return false;

            var em = client.EntityManager;
            homeCount = CountReplicatedHomePlanets(em);
            // --- Instantiated ghosts only ---
            // [NETCODE] Exclude PendingSpawnPlaceholder so the loading bar tracks real Instantiates
            // (1/frame), not CreateEntity placeholders that have no hull / visuals yet.
            using var planets = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.Exclude<PendingSpawnPlaceholder>());
            using var asteroids = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.Exclude<PendingSpawnPlaceholder>());
            planetCount = planets.CalculateEntityCount();
            asteroidCount = asteroids.CalculateEntityCount();
            return homeCount > 0 || planetCount > 0 || asteroidCount > 0;
        }

        /// <summary>Expected total bodies from layout buffer or map settings given home planet count.</summary>
        static int EstimateExpectedRemoteMapBodies(int homeCount)
        {
            if (homeCount <= 0)
                return 0;

            if (ClientWorld != null && ClientWorld.IsCreated &&
                ClientWorld.EntityManager.CreateEntityQuery(typeof(MapLayoutEntryElement))
                    .TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout) &&
                layout.Length > 0)
                return layout.Length;

            return EstimateMapSpawnStepsFromSettings(homeCount);
        }

        /// <summary>Remote client loading progress from replicated planet + asteroid ghost counts.</summary>
        static bool TryReadReplicatedMapLoadProgress(
            World client,
            out int completedSteps,
            out int totalSteps,
            out bool loadingComplete,
            out float progress)
        {
            completedSteps = 0;
            totalSteps = 0;
            loadingComplete = false;
            progress = 0f;

            if (!TryGetReplicatedMapBodyCounts(client, out int homes, out int planets, out int asteroids))
                return false;

            completedSteps = planets + asteroids;
            totalSteps = ResolveRemoteMapExpectedTotal(homes);

            progress = Mathf.Clamp01((float)completedSteps / totalSteps);
            loadingComplete = TryGetReplicatedMapLoadComplete(client);
            return true;
        }

        /// <summary>
        /// Remote clients: dismiss loading only when real Instantiated map ghosts are mostly present
        /// and join settle has finished (Instantiates backlog drained + hybrid proxies allowed).
        /// <para>
        /// Previously we returned true as soon as the layout buffer arrived (+ 32 asteroids). That
        /// dismissed the loading screen while GhostSpawn was still Instantiating at 1/frame and
        /// <see cref="ClientJoinSettleCache"/> was skipping GO proxies — join-team showed an empty map.
        /// </para>
        /// </summary>
        static bool TryGetReplicatedMapLoadComplete(World client)
        {
            // --- Still Instantiating / transform-gated ---
            // [TITAN-ORBIT] Loading UI must cover ClientJoinSettle, not only "first ghosts arrived".
            if (ClientJoinSettleCache.Settling)
            {
                s_ProxyCatchupWaitSince = -1f;
                s_LastProxyCountForCatchup = -1;
                return false;
            }

            int expectedTotal = 0;
            if (MapSessionMetaCache.HasMeta && MapSessionMetaCache.LoadingTotalSteps > 0)
                expectedTotal = MapSessionMetaCache.LoadingTotalSteps;
            else if (s_LatchedLoadingTotalSteps > 0)
                expectedTotal = s_LatchedLoadingTotalSteps;

            // --- Authoritative meta: receive done OR proxies caught up ---
            // [TITAN-ORBIT] Playability first: once GhostSpawn Instantiates have covered meta N and
            // Settling is OFF, dismiss the loading screen. GO proxies can finish in the background
            // (SpawnRequest drain). Waiting on proxies alone pinned Windows clients at N/N forever
            // when GhostComponent Pending could not be AddComponent'd at runtime.
            if (expectedTotal > 0)
            {
                int proxies = EcsWorldVisualizer.MapLoadingProxyCount;
                int receive = TitanOrbitJoinLoadCounters.InstantiatesSession;
                float proxyRatio = (float)proxies / expectedTotal;

                if (proxyRatio >= 0.92f)
                    return true;

                // Instantiates covered the map total — allow a short grace, then play.
                if (receive >= expectedTotal)
                {
                    if (s_ProxyCatchupWaitSince < 0f)
                        s_ProxyCatchupWaitSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - s_ProxyCatchupWaitSince >= 1.5f)
                        return true;
                }

                // --- Plateau clock while proxies still climbing ---
                if (proxies != s_LastProxyCountForCatchup)
                {
                    s_LastProxyCountForCatchup = proxies;
                    if (receive < expectedTotal)
                        s_ProxyCatchupWaitSince = Time.realtimeSinceStartup;
                }
                else if (s_ProxyCatchupWaitSince < 0f)
                {
                    s_ProxyCatchupWaitSince = Time.realtimeSinceStartup;
                }

                float stalledSeconds = Time.realtimeSinceStartup - s_ProxyCatchupWaitSince;

                if (proxies >= RemoteMapMinAsteroids && stalledSeconds >= 4f)
                    return true;

                if (stalledSeconds >= 12f)
                    return true;

                return false;
            }

            // No meta under quarantine: refuse gather-based completion (avoids Crash!!!).
            if (ClientJoinSettleCache.TransformQuarantine)
                return false;

            if (!TryGetReplicatedMapBodyCounts(client, out int homes, out int planets, out int asteroids))
                return false;

            if (homes < 1 || planets < homes)
                return false;

            if (asteroids < RemoteMapMinAsteroids)
                return false;

            // Fallback without meta: ghost counts stable for a short window (no layout early-out).
            if (planets != s_RemoteMapPlanetCount || asteroids != s_RemoteMapAsteroidCount)
            {
                s_RemoteMapPlanetCount = planets;
                s_RemoteMapAsteroidCount = asteroids;
                s_RemoteMapStableSince = Time.realtimeSinceStartup;
                return false;
            }

            if (s_RemoteMapStableSince < 0f ||
                Time.realtimeSinceStartup - s_RemoteMapStableSince < RemoteMapStableSeconds)
                return false;

            return true;
        }

        /// <summary>Length of ghost-replicated map layout buffer on the client (0 until finalize).</summary>
        static bool TryGetReplicatedLayoutEntryCount(World client, out int count)
        {
            count = 0;
            if (client == null || !client.IsCreated)
                return false;

            if (!client.EntityManager.CreateEntityQuery(typeof(MapLayoutEntryElement))
                    .TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout))
                return false;

            count = layout.Length;
            return count > 0;
        }

        /// <summary>True when replicated planet/ship ghosts indicate the client has enough world state for lobby UI.</summary>
        public static bool HasClientReplicatedMapContent()
        {
            return ClientWorld != null && ClientWorld.IsCreated && HasReplicatedMapWorldContent(ClientWorld);
        }

        /// <summary>True when enough planet/asteroid ghosts have streamed and counts stabilized.</summary>
        static bool HasReplicatedMapWorldContent(World client)
        {
            var em = client.EntityManager;
            using var planets = em.CreateEntityQuery(typeof(PlanetState));
            if (planets.CalculateEntityCount() >= 3)
                return true;

            // Host picked a team — at least one ship ghost means the match is live.
            using var ships = em.CreateEntityQuery(typeof(ShipTag));
            return ships.CalculateEntityCount() > 0;
        }

        // --- Team / match queries ---

        /// <summary>Team roster singleton — prefers ServerWorld on host, else ClientWorld.</summary>
        public static TeamStateSingleton GetTeamState()
        {
            if (ServerWorld != null && ServerWorld.IsCreated)
            {
                var serverQuery = ServerWorld.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton));
                if (serverQuery.TryGetSingleton<TeamStateSingleton>(out var serverTeam))
                    return serverTeam;
            }

            if (ClientWorld != null && ClientWorld.IsCreated)
            {
                var clientQuery = ClientWorld.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton));
                if (clientQuery.TryGetSingleton<TeamStateSingleton>(out var clientTeam))
                    return clientTeam;
            }

            return default;
        }

        /// <summary>Number of teams in this match (from home planets, then server team state).</summary>
        public static bool TryGetActiveTeamCount(out int activeTeamCount)
        {
            activeTeamCount = 0;

            // [TITAN-ORBIT] Latch team count once discovered so Join Team UI does not bounce to "Preparing teams...".
            if (s_LatchedActiveTeamCount > 0)
            {
                activeTeamCount = s_LatchedActiveTeamCount;
                return true;
            }

            // --- MapSessionMetaRpc (dedicated clients — no ServerWorld, no gather) ---
            // [TITAN-ORBIT] Prefer this before home-planet queries (those ToComponentDataArray paths
            // are unsafe under TransformQuarantine and often return 0 while meta already has TeamCount).
            if (MapSessionMetaCache.HasMeta && MapSessionMetaCache.TeamCount > 0)
            {
                activeTeamCount = MapSessionMetaCache.TeamCount;
                return LatchActiveTeamCount(activeTeamCount);
            }

            if (ServerWorld != null && ServerWorld.IsCreated)
            {
                using var homes = ServerWorld.EntityManager.CreateEntityQuery(typeof(HomePlanetTag));
                int homeCount = homes.CalculateEntityCount();
                if (homeCount > 0)
                {
                    activeTeamCount = homeCount;
                    return LatchActiveTeamCount(activeTeamCount);
                }

                if (ServerWorld.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton))
                        .TryGetSingleton<TeamStateSingleton>(out var serverTeam) &&
                    serverTeam.ActiveTeamCount > 0)
                {
                    activeTeamCount = serverTeam.ActiveTeamCount;
                    return LatchActiveTeamCount(activeTeamCount);
                }
            }

            var world = GetLocalPlayerShipWorld();
            if (world != null && world.IsCreated)
            {
                int replicatedHomeCount = CountReplicatedHomePlanets(world.EntityManager);
                if (replicatedHomeCount > 0)
                {
                    activeTeamCount = replicatedHomeCount;
                    return LatchActiveTeamCount(activeTeamCount);
                }
            }

            if (!IsMapLoadingComplete())
                return false;

            var teamState = GetTeamState();
            if (teamState.ActiveTeamCount > 0)
            {
                activeTeamCount = teamState.ActiveTeamCount;
                return LatchActiveTeamCount(activeTeamCount);
            }

            return false;
        }

        /// <summary>Stores the first non-zero team count for the current in-game session.</summary>
        static bool LatchActiveTeamCount(int count)
        {
            if (count > 0)
                s_LatchedActiveTeamCount = count;
            return count > 0;
        }

        /// <summary>Counts home planets with <see cref="PlanetState.IsHomePlanet"/> in replicated state.</summary>
        static int CountReplicatedHomePlanets(EntityManager em)
        {
            using var query = em.CreateEntityQuery(typeof(PlanetState), typeof(PlanetTag));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            int count = 0;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].IsHomePlanet)
                    count++;
            }

            return count;
        }

        // --- Planet queries ---

        /// <summary><see cref="PlanetState"/> by stable <see cref="PlanetState.PlanetId"/> across host/client worlds.</summary>
        public static bool TryGetPlanetStateByPlanetId(int planetId, out PlanetState state)
        {
            state = default;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetState(ServerWorld, planetId, out state))
                return true;

            if (TryFindPlanetState(ClientWorld, planetId, out state))
                return true;

            return false;
        }

        /// <summary>Gem-moon combat state for a planet — shield, orbit zone, contributed gems UI.</summary>
        public static bool TryGetPlanetGemMoonStateByPlanetId(int planetId, out PlanetGemMoonState moonState)
        {
            moonState = default;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetGemMoonState(ServerWorld, planetId, out moonState))
                return true;

            if (TryFindPlanetGemMoonState(ClientWorld, planetId, out moonState))
                return true;

            return false;
        }

        /// <summary>Reads contributed gem bank balance from the server ledger (local host only).</summary>
        public static bool TryGetContributedGems(int homePlanetId, out float amount)
        {
            amount = 0f;
            if (homePlanetId <= 0 || !IsLocalHost())
                return false;

            var server = ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            int networkId = GetLocalNetworkId();
            if (networkId <= 0)
                return false;

            var em = server.EntityManager;
            using var query = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != homePlanetId)
                    continue;

                amount = ContributedGemsLogic.Get(em, entities[i], networkId);
                return true;
            }

            return false;
        }

        /// <summary>Linear search for <see cref="PlanetState"/> by planet id in a world.</summary>
        static bool TryFindPlanetState(World world, int planetId, out PlanetState state)
        {
            state = default;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                state = states[i];
                return true;
            }

            return false;
        }

        /// <summary>Linear search for <see cref="PlanetGemMoonState"/> by parent planet id.</summary>
        static bool TryFindPlanetGemMoonState(World world, int planetId, out PlanetGemMoonState moonState)
        {
            moonState = default;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(PlanetGemMoonState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var moonStates = query.ToComponentDataArray<PlanetGemMoonState>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                moonState = moonStates[i];
                return true;
            }

            return false;
        }

        /// <summary>Planet world position, scale, and state by <see cref="PlanetState.PlanetId"/>.</summary>
        public static bool TryGetPlanetPoseByPlanetId(int planetId, out float3 position, out float scale, out PlanetState state)
        {
            position = default;
            scale = 1f;
            state = default;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetPose(ServerWorld, planetId, out position, out scale, out state))
                return true;

            if (TryFindPlanetPose(ClientWorld, planetId, out position, out scale, out state))
                return true;

            return false;
        }

        /// <summary>Planet visual spin rotation for minimap and world labels.</summary>
        public static bool TryGetPlanetRotationByPlanetId(int planetId, out quaternion rotation)
        {
            rotation = quaternion.identity;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetRotation(ServerWorld, planetId, out rotation))
                return true;

            return TryFindPlanetRotation(ClientWorld, planetId, out rotation);
        }

        /// <summary>Planet pose (position, scale, state) linear search by planet id.</summary>
        static bool TryFindPlanetPose(World world, int planetId, out float3 position, out float scale, out PlanetState state)
        {
            position = default;
            scale = 1f;
            state = default;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(LocalTransform));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                state = states[i];
                position = transforms[i].Position;
                scale = math.max(0.25f, transforms[i].Scale);
                return true;
            }

            return false;
        }

        /// <summary>Planet <see cref="LocalTransform.Rotation"/> linear search by planet id.</summary>
        static bool TryFindPlanetRotation(World world, int planetId, out quaternion rotation)
        {
            rotation = quaternion.identity;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState), typeof(LocalTransform));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                rotation = transforms[i].Rotation;
                return true;
            }

            return false;
        }
    }
}
