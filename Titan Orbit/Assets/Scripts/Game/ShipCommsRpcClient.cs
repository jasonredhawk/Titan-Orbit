using TitanOrbit.Core;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client glue that sends a 1–5 keyword comms sentence to the server.
    /// <para>
    /// [NETCODE] Dedicated / Relay clients send <see cref="ShipCommsCommand"/> from ClientWorld.
    /// Local Host injects the same command onto ServerWorld with
    /// <see cref="ReceiveRpcCommandRequest"/> already set — the same Instantiates-safe pattern
    /// as <see cref="PlayerNameRpcClient"/>. Under join load, SendRpc can vanish and the
    /// callout would never leave this machine.
    /// </para>
    /// <c>TeamOnly</c> is a channel request (All / Team / Commander). The server looks up
    /// the speaker's <c>ShipState.Team</c> and commander rank and targets those
    /// connections — the client cannot pick another team's inbox or spoof command rank.
    /// The panel also paints an optimistic local bubble so the speaker does not wait on RTT.
    /// Server validation / rate-limit still decide whether everyone else sees it.
    /// </summary>
    public static class ShipCommsRpcClient
    {
        /// <summary>
        /// Enqueues the command on Local Host ServerWorld or ClientWorld. Returns true when
        /// an RPC entity was created (not when the server has accepted it).
        /// </summary>
        public static bool TrySend(in ShipCommsInbox.Callout payload)
        {
            if (payload.Count < 1 || payload.Count > 5)
                return false;

            // Preserve Commander (2). Collapsing to 0/1 would drop command chrome on the echo.
            byte channel = (byte)TeamCommanderRules.Sanitize(payload.TeamOnly);
            var command = new ShipCommsCommand
            {
                Count = payload.Count,
                K0 = payload.K0,
                K1 = payload.K1,
                K2 = payload.K2,
                K3 = payload.K3,
                K4 = payload.K4,
                TeamOnly = channel,
                HasWaypoint = payload.HasWaypoint != 0 ? (byte)1 : (byte)0,
                WaypointX = payload.WaypointX,
                WaypointZ = payload.WaypointZ,
                FocusKind = payload.FocusKind,
                YouNetworkId = payload.YouNetworkId,
                PlanetId = payload.PlanetId,
                Everyone = payload.Everyone,
                Us0 = payload.Us0,
                Us1 = payload.Us1,
                Us2 = payload.Us2,
                Us3 = payload.Us3,
                MeX = payload.MeX,
                MeZ = payload.MeZ,
                YouX = payload.YouX,
                YouZ = payload.YouZ,
                GroupCount = payload.GroupCount,
                G0X = payload.G0X, G0Z = payload.G0Z,
                G1X = payload.G1X, G1Z = payload.G1Z,
                G2X = payload.G2X, G2Z = payload.G2Z,
                G3X = payload.G3X, G3Z = payload.G3Z,
                G4X = payload.G4X, G4Z = payload.G4Z,
                G5X = payload.G5X, G5Z = payload.G5Z,
                G6X = payload.G6X, G6Z = payload.G6Z,
                G7X = payload.G7X, G7Z = payload.G7Z,
            };

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (TryEnqueueLocalHost(command, localId))
                return true;

            return TrySendDedicatedRpc(command);
        }

        /// <summary>
        /// [TITAN-ORBIT] Local Host: create the RPC entity on ServerWorld so
        /// <see cref="ShipCommsServerSystem"/> sees it next tick without IPC.
        /// </summary>
        static bool TryEnqueueLocalHost(in ShipCommsCommand command, int networkId)
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
            em.AddComponentData(rpcEntity, command);
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        /// <summary>
        /// [NETCODE] Dedicated / Relay: SendRpc from ClientWorld. TargetConnection Null = "the
        /// server that owns this client connection."
        /// </summary>
        static bool TrySendDedicatedRpc(in ShipCommsCommand command)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, command);
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
