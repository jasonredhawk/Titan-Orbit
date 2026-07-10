using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>UI and MonoBehaviour access point for ECS game state.</summary>
    public static class EcsGameBridge
    {
        public static World ClientWorld => ClientServerBootstrap.ClientWorld;
        public static World ServerWorld => ClientServerBootstrap.ServerWorld;

        /// <summary>ECS world used for rendering and local-player camera follow.</summary>
        public static World GetVisualizationWorld()
        {
            if (TitanOrbitSessionManager.IsDedicatedOnlineClient &&
                ClientWorld != null &&
                ClientWorld.IsCreated)
                return ClientWorld;

            if (IsLocalHost() &&
                ServerWorld != null &&
                ServerWorld.IsCreated)
                return ServerWorld;

            return ClientWorld ?? ServerWorld;
        }

        public static bool TryGetLocalShipPosition(out Vector3 position)
        {
            if (ShipMoonDockVisualApplier.TryGetLocalFollowPosition(out position))
                return true;

            position = default;

            if (!TryGetLocalShipTransform(out var lt))
                return false;

            position = lt.Position;
            return true;
        }

        public static bool TryGetLocalShipTransform(out LocalTransform transform) =>
            TryGetLocalShipTransformFromWorld(GetVisualizationWorld(), out transform);

        public static bool TryGetLocalShipTransformFromWorld(World world, out LocalTransform transform)
        {
            transform = default;

            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // No local ship camera/control until the galaxy build finishes.
            if (IsNetworkInGame() && !IsMapLoadingComplete())
                return false;

            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
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

        public static bool TryGetLocalShipVelocity(out Vector3 velocity)
        {
            velocity = default;

            var world = TitanOrbitSessionManager.IsDedicatedOnlineClient &&
                        ClientWorld != null &&
                        ClientWorld.IsCreated
                ? ClientWorld
                : GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryGetLocalShipEntity(em, out var shipEntity) ||
                !em.HasComponent<ShipKinematics>(shipEntity))
                return false;

            velocity = em.GetComponentData<ShipKinematics>(shipEntity).Velocity;
            return true;
        }

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

        public static bool TryGetLocalShipState(out ShipState state)
        {
            state = default;
            var world = GetVisualizationWorld();
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

        public static bool TryGetLocalShipAttributeUpgrades(out ShipAttributeUpgradeState attributes)
        {
            attributes = default;
            var world = GetVisualizationWorld();
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

        public static bool TryGetMatchState(out MatchStateSingleton match)
        {
            match = default;
            var world = ClientWorld ?? ServerWorld;
            if (world == null || !world.IsCreated)
                return false;

            using var query = world.EntityManager.CreateEntityQuery(typeof(MatchStateSingleton));
            return query.TryGetSingleton(out match);
        }

        public static bool TryGetLocalShipDeathState(out ShipDeathState death)
        {
            death = default;
            var world = GetVisualizationWorld();
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

        public static bool TryGetLocalShipOrbitState(out ShipOrbitState orbitState)
        {
            orbitState = default;
            var world = GetVisualizationWorld();
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

        public static bool TryGetLocalShipMoonDockState(out ShipMoonDockState moonDock)
        {
            moonDock = default;
            var world = GetVisualizationWorld();
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

        public static bool TryGetLocalShipEntityOnWorld(World world, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (world == null || !world.IsCreated)
                return false;

            return TryGetLocalShipEntity(world.EntityManager, out shipEntity);
        }

        public static bool TryGetLocalShipInput(out ShipInput input)
        {
            input = default;
            var world = GetVisualizationWorld();
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

        public static bool TryGetLocalShipDepositIntent(out bool wantDepositGems)
        {
            wantDepositGems = false;
            var world = GetVisualizationWorld();
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

        public static bool TryGetLocalShipLoadout(out ShipLoadoutState loadout)
        {
            loadout = default;
            var world = GetVisualizationWorld();
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

        public static bool IsNetworkInGame()
        {
            if (ClientWorld != null && ClientWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld))
                return true;

#if UNITY_SERVER
            if ((ClientWorld == null || !ClientWorld.IsCreated) &&
                ServerWorld != null && ServerWorld.IsCreated &&
                TitanOrbitSessionManager.IsClientConnectionReady(ServerWorld))
                return true;
#endif
            return false;
        }

        public static bool IsLocalHost()
        {
            if (TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return false;

            return ClientWorld != null && ClientWorld.IsCreated && ServerWorld != null && ServerWorld.IsCreated &&
                   TitanOrbitSessionManager.IsClientGameplayReady(ClientWorld) &&
                   TitanOrbitSessionManager.IsClientConnectionReady(ServerWorld);
        }

        public static int GetLocalNetworkId()
        {
            return GetLocalNetworkId(ClientWorld);
        }

        public static bool IsMapLoadingComplete()
        {
            if (!IsNetworkInGame())
            {
                ResetRemoteMapLoadTracking();
                return false;
            }

            // Local host: ServerWorld owns map generation and MapStateSingleton.
            if (IsLocalHost() &&
                ServerWorld != null && ServerWorld.IsCreated &&
                TryGetMapLoadingComplete(ServerWorld, out var serverComplete))
                return serverComplete;

            if (ClientWorld != null && ClientWorld.IsCreated &&
                TryGetMapLoadingComplete(ClientWorld, out var clientComplete))
                return clientComplete;

            // MPPM / dedicated clients: MapStateSingleton is not ghosted — infer from replicated bodies.
            if (IsRemoteMapObserverClient() && ClientWorld != null && ClientWorld.IsCreated)
                return TryGetReplicatedMapLoadComplete(ClientWorld);

            return false;
        }

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

        public static bool TryGetMapLoadingStepCounts(out int completedSteps, out int totalSteps)
        {
            completedSteps = 0;
            totalSteps = 0;
            if (!IsNetworkInGame())
                return false;

            // Local host: only trust authoritative server totals — never infer from client ghost counts.
            if (IsLocalHost() && ServerWorld != null && ServerWorld.IsCreated)
            {
                if (TryReadMapLoadingState(ServerWorld, out completedSteps, out totalSteps, out _, out _))
                    return totalSteps > 0;
                return false;
            }

            if (TryGetMapLoadingState(out completedSteps, out totalSteps, out _, out _))
                return totalSteps > 0;

            return false;
        }

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
                ServerWorld != null && ServerWorld.IsCreated &&
                TryReadMapLoadingState(ServerWorld, out completedSteps, out totalSteps, out loadingComplete, out progress))
                return true;

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

        static bool TryGetLocalShipEntity(EntityManager em, out Entity shipEntity)
        {
            shipEntity = Entity.Null;

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

        static bool HasNetworkStreamInGame(World world)
        {
            if (world == null || !world.IsCreated) return false;
            return world.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame)).CalculateEntityCount() > 0;
        }

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
        // Conservative spawn-step estimate until home planets reveal team count (MapGenerationSettings min asteroids).
        const int DefaultRemoteMapSpawnSteps = 460;
        static int s_RemoteMapExpectedTotal = -1;
        static int s_RemoteMapPlanetCount = -1;
        static int s_RemoteMapAsteroidCount = -1;
        static float s_RemoteMapStableSince = -1f;

        static void ResetRemoteMapLoadTracking()
        {
            s_RemoteMapExpectedTotal = -1;
            s_RemoteMapPlanetCount = -1;
            s_RemoteMapAsteroidCount = -1;
            s_RemoteMapStableSince = -1f;
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

            return s_RemoteMapExpectedTotal > 0 ? s_RemoteMapExpectedTotal : DefaultRemoteMapSpawnSteps;
        }

        static bool TryGetReplicatedMapBodyCounts(World client, out int homeCount, out int planetCount, out int asteroidCount)
        {
            homeCount = 0;
            planetCount = 0;
            asteroidCount = 0;
            if (client == null || !client.IsCreated)
                return false;

            var em = client.EntityManager;
            homeCount = CountReplicatedHomePlanets(em);
            using var planets = em.CreateEntityQuery(typeof(PlanetState), typeof(PlanetTag));
            using var asteroids = em.CreateEntityQuery(typeof(AsteroidState));
            planetCount = planets.CalculateEntityCount();
            asteroidCount = asteroids.CalculateEntityCount();
            return homeCount > 0 || planetCount > 0 || asteroidCount > 0;
        }

        static int EstimateExpectedRemoteMapBodies(int homeCount)
        {
            if (homeCount <= 0)
                return 0;

            if (ClientWorld != null && ClientWorld.IsCreated &&
                ClientWorld.EntityManager.CreateEntityQuery(typeof(MapLayoutEntryElement))
                    .TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout) &&
                layout.Length > 0)
                return layout.Length;

            // Typical roll: homes + neutrals + hundreds of asteroids (see MapGenerationSettings).
            return homeCount + 12 + 444;
        }

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
        /// Remote clients wait for home planets + asteroid field ghosts to finish streaming, then a short settle window.
        /// </summary>
        static bool TryGetReplicatedMapLoadComplete(World client)
        {
            if (!TryGetReplicatedMapBodyCounts(client, out int homes, out int planets, out int asteroids))
                return false;

            if (homes < 1 || planets < homes)
                return false;

            if (asteroids < RemoteMapMinAsteroids)
                return false;

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

        /// <summary>True when replicated planet/ship ghosts indicate the client has enough world state for lobby UI.</summary>
        public static bool HasClientReplicatedMapContent()
        {
            return ClientWorld != null && ClientWorld.IsCreated && HasReplicatedMapWorldContent(ClientWorld);
        }

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

            if (ServerWorld != null && ServerWorld.IsCreated)
            {
                using var homes = ServerWorld.EntityManager.CreateEntityQuery(typeof(HomePlanetTag));
                int homeCount = homes.CalculateEntityCount();
                if (homeCount > 0)
                {
                    activeTeamCount = homeCount;
                    return true;
                }

                if (ServerWorld.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton))
                        .TryGetSingleton<TeamStateSingleton>(out var serverTeam) &&
                    serverTeam.ActiveTeamCount > 0)
                {
                    activeTeamCount = serverTeam.ActiveTeamCount;
                    return true;
                }
            }

            var world = GetVisualizationWorld();
            if (world != null && world.IsCreated)
            {
                int replicatedHomeCount = CountReplicatedHomePlanets(world.EntityManager);
                if (replicatedHomeCount > 0)
                {
                    activeTeamCount = replicatedHomeCount;
                    return true;
                }
            }

            if (!IsMapLoadingComplete())
                return false;

            var teamState = GetTeamState();
            if (teamState.ActiveTeamCount > 0)
            {
                activeTeamCount = teamState.ActiveTeamCount;
                return true;
            }

            return false;
        }

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

        public static bool TryGetPlanetRotationByPlanetId(int planetId, out quaternion rotation)
        {
            rotation = quaternion.identity;
            if (planetId == 0)
                return false;

            if (IsLocalHost() && TryFindPlanetRotation(ServerWorld, planetId, out rotation))
                return true;

            return TryFindPlanetRotation(ClientWorld, planetId, out rotation);
        }

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
