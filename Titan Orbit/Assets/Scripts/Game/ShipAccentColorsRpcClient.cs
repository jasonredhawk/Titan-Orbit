using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client glue that publishes <see cref="LocalPlayerShipAccents"/> after GoInGame
    /// and whenever the player paints a well. Same Local Host inject pattern as
    /// <see cref="PlayerNameRpcClient"/> — SendRpc can drop under Join Team Instantiates.
    /// <para>
    /// Local Host also writes the owned server ship directly when it exists, so paint
    /// does not depend on an RPC that arrived before the hull spawned.
    /// </para>
    /// </summary>
    public static class ShipAccentColorsRpcClient
    {
        const float ResendIntervalSeconds = 4f;
        const int MaxSendsPerSession = 8;

        static int s_SendCount;
        static float s_LastSendRealtime;
        static bool s_ForceSend;

        /// <summary>[UNITY] Domain Reload off: counters survive Play Mode.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => ResetSession();

        /// <summary>Clears send counters (session leave / Play Mode).</summary>
        public static void ResetSession()
        {
            s_SendCount = 0;
            s_LastSendRealtime = 0f;
            s_ForceSend = false;
        }

        /// <summary>
        /// Call after a well write so the next tick sends immediately even if
        /// the join burst already used <see cref="MaxSendsPerSession"/>.
        /// </summary>
        public static void NotifyChanged()
        {
            s_ForceSend = true;
            s_SendCount = 0;
            s_LastSendRealtime = 0f;
        }

        /// <summary>
        /// Publishes the local palette when the client is in-game. Safe every frame —
        /// rate-limited unless <see cref="NotifyChanged"/> was called.
        /// Waits until the local hull exists so we do not burn the send budget
        /// on team-select frames that have no ship yet.
        /// </summary>
        public static void TrySendLocalAccents()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                ResetSession();
                return;
            }

            if (!EcsGameBridge.HasLocalPlayerShip())
                return;

            if (!s_ForceSend)
            {
                if (s_SendCount >= MaxSendsPerSession)
                    return;
                if (s_SendCount > 0 &&
                    Time.realtimeSinceStartup - s_LastSendRealtime < ResendIntervalSeconds)
                    return;
            }

            ShipAccentColors accents = LocalPlayerShipAccents.Get();
            LocalPlayerThrusterStyle.CopyTo(ref accents, LocalPlayerThrusterStyle.Get());
            bool sent = TryWriteLocalHostShip(accents)
                        || TryEnqueueLocalHost(accents)
                        || TrySendDedicatedRpc(accents);
            if (!sent)
                return;

            s_ForceSend = false;
            s_SendCount++;
            s_LastSendRealtime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Local Host: stamp the server ship now. The owner's visual already uses
        /// <see cref="LocalPlayerShipAccents"/>; this is so remotes (and a later
        /// reconnect) see the same palette through GhostFields.
        /// </summary>
        static bool TryWriteLocalHostShip(in ShipAccentColors accents)
        {
            if (!EcsGameBridge.IsLocalHost())
                return false;

            int networkId = EcsGameBridge.GetLocalNetworkId();
            if (networkId <= 0)
                return false;

            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            if (!TryGetOwnedShip(em, networkId, out Entity ship))
                return false;

            if (em.HasComponent<ShipAccentColors>(ship))
                em.SetComponentData(ship, accents);
            else
                em.AddComponentData(ship, accents);
            return true;
        }

        static bool TryEnqueueLocalHost(in ShipAccentColors accents)
        {
            if (!EcsGameBridge.IsLocalHost())
                return false;

            int networkId = EcsGameBridge.GetLocalNetworkId();
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
            em.AddComponentData(rpcEntity, ToCommand(accents));
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static bool TrySendDedicatedRpc(in ShipAccentColors accents)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, ToCommand(accents));
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        static SetShipAccentColorsCommand ToCommand(in ShipAccentColors accents)
        {
            return new SetShipAccentColorsCommand
            {
                HasCustom = accents.HasCustom,
                Color2Packed = accents.Color2Packed,
                Color3Packed = accents.Color3Packed,
                EmissionPacked = accents.EmissionPacked,
                Emission2Packed = accents.Emission2Packed,
                Emission3Packed = accents.Emission3Packed,
                ThrusterCustom = accents.ThrusterCustom,
                ThrusterStyle = accents.ThrusterStyle,
                ThrusterColorPacked = accents.ThrusterColorPacked,
                ThrusterFollowTeam = accents.ThrusterFollowTeam,
            };
        }

        static bool TryGetOwnedShip(EntityManager em, int networkId, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId <= 0)
                return false;

            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var owners = query.ToComponentDataArray<GhostOwner>(Unity.Collections.Allocator.Temp);
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = entities[i];
                return true;
            }

            return false;
        }

        static Entity FindServerConnection(EntityManager em, int networkId)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            if (query.IsEmptyIgnoreFilter)
                return Entity.Null;
            if (query.CalculateEntityCount() != 1)
                return Entity.Null;

            Entity connection = query.GetSingletonEntity();
            if (em.GetComponentData<NetworkId>(connection).Value != networkId)
                return Entity.Null;
            return connection;
        }
    }
}
