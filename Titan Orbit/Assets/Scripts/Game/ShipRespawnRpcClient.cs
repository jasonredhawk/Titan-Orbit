using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client glue that asks the server to respawn the local ship at a friendly planet.
    /// Called from the expanded minimap after the 10s death beat and the keep/forfeit
    /// loadout choice when the player clicks a world their team still owns.
    /// <para>
    /// [NETCODE] Dedicated / Relay clients send <see cref="RequestRespawnPlanetCommand"/> from
    /// ClientWorld. Local Host injects the same command onto ServerWorld with
    /// <see cref="ReceiveRpcCommandRequest"/> already set — SendRpc on ServerWorld never
    /// becomes a receive entity (same Instantiates-safe pattern as
    /// <see cref="ShipCommsRpcClient"/>).
    /// </para>
    /// The server still validates timer + ownership. A rejected click leaves the hull dead
    /// so the player can pick another planet.
    /// </summary>
    public static class ShipRespawnRpcClient
    {
        /// <summary>
        /// Enqueues the respawn request. Returns true when an RPC entity was created
        /// (not when the server has accepted it).
        /// </summary>
        /// <param name="planetId">Stable <see cref="PlanetState.PlanetId"/> from the clicked blip.</param>
        /// <param name="keepLoadout">
        /// True after a completed keep-loadout ad (or remove-ads). False strips cards + gear.
        /// </param>
        public static bool TryRequestRespawnAtPlanet(int planetId, bool keepLoadout = false)
        {
            if (planetId <= 0)
                return false;

            byte keep = keepLoadout ? (byte)1 : (byte)0;
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (TryEnqueueLocalHost(planetId, keep, localId))
                return true;

            return TrySendDedicatedRpc(planetId, keep);
        }

        /// <summary>
        /// [TITAN-ORBIT] Local Host: create the RPC entity on ServerWorld so
        /// <see cref="ShipRespawnSystem"/> sees it next tick without IPC.
        /// </summary>
        static bool TryEnqueueLocalHost(int planetId, byte keepLoadout, int networkId)
        {
            if (!EcsGameBridge.IsLocalHost())
                return false;
            if (networkId <= 0)
                return false;

            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            Entity connection = FindServerConnection(em, networkId);
            if (connection == Entity.Null)
                return false;

            var rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, new RequestRespawnPlanetCommand
            {
                PlanetId = planetId,
                KeepLoadout = keepLoadout,
            });
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        /// <summary>
        /// [NETCODE] Dedicated / Relay: SendRpc from ClientWorld. TargetConnection Null = "the
        /// server that owns this client connection."
        /// </summary>
        static bool TrySendDedicatedRpc(int planetId, byte keepLoadout)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RequestRespawnPlanetCommand
            {
                PlanetId = planetId,
                KeepLoadout = keepLoadout,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        /// <summary>
        /// Finds the server connection entity whose <see cref="NetworkId"/> matches the local player.
        /// Local Host is one in-game connection, so this uses GetSingletonEntity (no ship gather).
        /// </summary>
        static Entity FindServerConnection(EntityManager em, int networkId)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            if (query.IsEmptyIgnoreFilter)
                return Entity.Null;

            // [TITAN-ORBIT] Local Host has a single in-game connection — GetSingletonEntity
            // avoids a client-hot entity gather (join-crash verifier).
            if (query.CalculateEntityCount() != 1)
                return Entity.Null;

            Entity connection = query.GetSingletonEntity();
            if (em.GetComponentData<NetworkId>(connection).Value != networkId)
                return Entity.Null;
            return connection;
        }
    }
}
