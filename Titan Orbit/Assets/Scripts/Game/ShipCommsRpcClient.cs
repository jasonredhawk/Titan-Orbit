using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client glue that sends a 1–3 keyword comms sentence to the server.
    /// <para>
    /// [NETCODE] Dedicated / Relay clients send <see cref="ShipCommsCommand"/> from ClientWorld.
    /// Local Host injects the same command onto ServerWorld with
    /// <see cref="ReceiveRpcCommandRequest"/> already set — the same Instantiates-safe pattern
    /// as <see cref="PlayerNameRpcClient"/>. Under join load, SendRpc can vanish and the
    /// callout would never leave this machine.
    /// </para>
    /// <c>TeamOnly</c> is a channel request (All vs teammates). The server looks up the
    /// speaker's <c>ShipState.Team</c> and targets those connections — the client cannot
    /// pick another team's inbox.
    /// The panel also paints an optimistic local bubble so the speaker does not wait on RTT.
    /// Server validation / rate-limit still decide whether everyone else sees it.
    /// </summary>
    public static class ShipCommsRpcClient
    {
        /// <summary>
        /// Enqueues the command on Local Host ServerWorld or ClientWorld. Returns true when
        /// an RPC entity was created (not when the server has accepted it).
        /// </summary>
        /// <param name="count">Live keyword count (1–3).</param>
        /// <param name="k0">First catalog index.</param>
        /// <param name="k1">Second catalog index (ignored when count is 1).</param>
        /// <param name="k2">Third catalog index (ignored when count is under 3).</param>
        /// <param name="teamOnly">1 = teammates only; 0 = every client.</param>
        public static bool TrySend(byte count, byte k0, byte k1, byte k2, byte teamOnly)
        {
            if (count < 1 || count > 3)
                return false;

            // [TITAN-ORBIT] Clamp to 0/1 so a stale caller cannot put junk on the wire.
            byte channel = teamOnly != 0 ? (byte)1 : (byte)0;

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (TryEnqueueLocalHost(count, k0, k1, k2, channel, localId))
                return true;

            return TrySendDedicatedRpc(count, k0, k1, k2, channel);
        }

        /// <summary>
        /// [TITAN-ORBIT] Local Host: create the RPC entity on ServerWorld so
        /// <see cref="ShipCommsServerSystem"/> sees it next tick without IPC.
        /// </summary>
        static bool TryEnqueueLocalHost(byte count, byte k0, byte k1, byte k2, byte teamOnly, int networkId)
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
            em.AddComponentData(rpcEntity, new ShipCommsCommand
            {
                Count = count,
                K0 = k0,
                K1 = k1,
                K2 = k2,
                TeamOnly = teamOnly,
            });
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        /// <summary>
        /// [NETCODE] Dedicated / Relay: SendRpc from ClientWorld. TargetConnection Null = "the
        /// server that owns this client connection."
        /// </summary>
        static bool TrySendDedicatedRpc(byte count, byte k0, byte k1, byte k2, byte teamOnly)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new ShipCommsCommand
            {
                Count = count,
                K0 = k0,
                K1 = k1,
                K2 = k2,
                TeamOnly = teamOnly,
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
