using TitanOrbit.Core;
using TitanOrbit.ECS;
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
            if (IsLocalHost() &&
                ServerWorld != null &&
                ServerWorld.IsCreated)
                return ServerWorld;

            return ClientWorld ?? ServerWorld;
        }

        public static bool TryGetLocalShipPosition(out Vector3 position)
        {
            position = default;

            if (!TryGetLocalShipTransform(out var lt))
                return false;

            position = lt.Position;
            return true;
        }

        static bool TryGetLocalShipTransform(out LocalTransform transform)
        {
            transform = default;

            var world = GetVisualizationWorld();
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

        public static bool HasLocalPlayerShip() => TryGetLocalShipPosition(out _);

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
            if (ClientWorld != null && ClientWorld.IsCreated && HasNetworkStreamInGame(ClientWorld))
                return true;

#if UNITY_SERVER
            if ((ClientWorld == null || !ClientWorld.IsCreated) && HasNetworkStreamInGame(ServerWorld))
                return true;
#endif
            return false;
        }

        public static bool IsLocalHost()
        {
            return ClientWorld != null && ClientWorld.IsCreated && ServerWorld != null && ServerWorld.IsCreated &&
                   HasNetworkStreamInGame(ClientWorld) && HasNetworkStreamInGame(ServerWorld);
        }

        public static int GetLocalNetworkId()
        {
            return GetLocalNetworkId(ClientWorld);
        }

        public static bool IsMapLoadingComplete()
        {
            if (!IsNetworkInGame())
                return false;

            if (ServerWorld != null && ServerWorld.IsCreated &&
                TryGetMapLoadingComplete(ServerWorld, out var serverComplete))
                return serverComplete;

            if (ClientWorld != null && ClientWorld.IsCreated &&
                TryGetMapLoadingComplete(ClientWorld, out var clientComplete))
                return clientComplete;

            // MPPM / remote clients: MapStateSingleton is server-only and not ghosted.
            if (ClientWorld != null && ClientWorld.IsCreated &&
                HasReplicatedMapWorldContent(ClientWorld))
                return true;

            return false;
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

            if (TryGetLocalOwnedShipEntity(em, out shipEntity))
                return true;

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
