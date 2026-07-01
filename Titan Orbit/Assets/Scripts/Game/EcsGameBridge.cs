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

        public static bool TryGetLocalShipPosition(out Vector3 position)
        {
            position = default;

            var client = ClientWorld;
            if (client != null && client.IsCreated)
            {
                var em = client.EntityManager;
                if (TryGetShipTransform(em, ComponentType.ReadOnly<LocalPlayerShipTag>(), out var lt))
                {
                    position = lt.Position;
                    return true;
                }

                if (TryGetShipTransform(em, ComponentType.ReadOnly<GhostOwnerIsLocal>(), out lt))
                {
                    position = lt.Position;
                    return true;
                }

                if (TryGetShipFromCommandTarget(em, out lt))
                {
                    position = lt.Position;
                    return true;
                }

                int localId = GetLocalNetworkId(client);
                if (localId > 0 && TryGetShipTransformByNetworkId(em, localId, out lt))
                {
                    position = lt.Position;
                    return true;
                }
            }

            // Local Client+Server: the owned ship exists on the server immediately after team pick.
            if (IsLocalHost() && ServerWorld != null && ServerWorld.IsCreated)
            {
                int localId = GetLocalNetworkId(client);
                if (localId > 0 &&
                    TryGetShipTransformByNetworkId(ServerWorld.EntityManager, localId, out var serverLt))
                {
                    position = serverLt.Position;
                    return true;
                }
            }

            return false;
        }

        public static bool HasLocalPlayerShip() => TryGetLocalShipPosition(out _);

        public static bool TryGetLocalShipState(out ShipState state)
        {
            state = default;
            var world = ClientWorld;
            if (world == null || !world.IsCreated) return false;

            var em = world.EntityManager;
            using var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipState));
            if (tagged.CalculateEntityCount() > 0)
            {
                state = tagged.GetSingleton<ShipState>();
                return true;
            }

            using var owned = em.CreateEntityQuery(typeof(GhostOwnerIsLocal), typeof(ShipState), typeof(ShipTag));
            if (owned.CalculateEntityCount() > 0)
            {
                state = owned.GetSingleton<ShipState>();
                return true;
            }

            if (TryGetShipStateFromCommandTarget(em, out state))
                return true;

            int localId = GetLocalNetworkId(world);
            if (localId > 0 && TryGetShipStateByNetworkId(em, localId, out state))
                return true;

            if (IsLocalHost() && ServerWorld != null && ServerWorld.IsCreated &&
                localId > 0 && TryGetShipStateByNetworkId(ServerWorld.EntityManager, localId, out state))
                return true;

            return false;
        }

        public static bool TryGetLocalShipOrbitState(out ShipOrbitState orbitState)
        {
            orbitState = default;
            var world = ClientWorld;
            if (world != null && world.IsCreated)
            {
                var em = world.EntityManager;
                using var tagged = em.CreateEntityQuery(typeof(LocalPlayerShipTag), typeof(ShipOrbitState));
                if (tagged.CalculateEntityCount() > 0)
                {
                    orbitState = tagged.GetSingleton<ShipOrbitState>();
                    return true;
                }

                using var owned = em.CreateEntityQuery(typeof(GhostOwnerIsLocal), typeof(ShipOrbitState), typeof(ShipTag));
                if (owned.CalculateEntityCount() > 0)
                {
                    orbitState = owned.GetSingleton<ShipOrbitState>();
                    return true;
                }
            }

            if (IsLocalHost() && ServerWorld != null && ServerWorld.IsCreated)
            {
                int localId = GetLocalNetworkId(world);
                if (localId > 0 && TryGetShipOrbitStateByNetworkId(ServerWorld.EntityManager, localId, out orbitState))
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
            if (query.CalculateEntityCount() == 0)
                return false;
            transform = query.GetSingleton<LocalTransform>();
            return true;
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
            var world = ClientWorld ?? ServerWorld;
            if (world == null || !world.IsCreated) return default;
            var query = world.EntityManager.CreateEntityQuery(typeof(TeamStateSingleton));
            if (query.TryGetSingleton<TeamStateSingleton>(out var team))
                return team;
            return default;
        }

        public static bool TryGetPlanetStateByPlanetId(int planetId, out PlanetState state)
        {
            state = default;
            if (planetId == 0)
                return false;

            if (TryFindPlanetState(ClientWorld, planetId, out state))
                return true;

            if (IsLocalHost() && TryFindPlanetState(ServerWorld, planetId, out state))
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
    }
}
