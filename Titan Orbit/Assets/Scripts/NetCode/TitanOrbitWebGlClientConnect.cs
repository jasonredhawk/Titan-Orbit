using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] WebGL connection entity recipe. Stock
    /// <see cref="NetworkStreamDriver.Connect"/> returns an entity index on WASM but leaves
    /// 0 <see cref="NetworkStreamConnection"/> in the world (CONNECT_TELEMETRY afterConnect=0).
    /// One <see cref="ComponentTypeSet"/> including the cleanup component, then
    /// <see cref="EntityManager.SetComponentData{T}"/>. Do not call <c>AddBuffer</c> here —
    /// that path WASM-OOBs on WebGL.
    /// </summary>
    public static class TitanOrbitWebGlClientConnect
    {
        /// <summary>
        /// Builds the NetCode client connection entity the WebGL player must use.
        /// Editor menu <c>TitanOrbit → WebGL → Validate Connect Entity Recipe</c> calls this
        /// with a dummy <see cref="NetworkConnection"/> so the recipe is checked without a
        /// 45-minute WebGL rebuild.
        /// </summary>
        public static Entity CreateConnectionEntity(
            EntityManager em,
            NetworkConnection connection,
            ConnectionState.State state)
        {
            var ent = em.CreateEntity();
            // ComponentTypeSet params ctor maxes out at 5.
            em.AddComponent(ent, new ComponentTypeSet(new ComponentType[]
            {
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadWrite<NetworkSnapshotAck>(),
                ComponentType.ReadWrite<CommandTarget>(),
                ComponentType.ReadWrite<IncomingRpcDataStreamBuffer>(),
                ComponentType.ReadWrite<OutgoingRpcDataStreamBuffer>(),
                ComponentType.ReadWrite<OutgoingCommandDataStreamBuffer>(),
                ComponentType.ReadWrite<IncomingSnapshotDataStreamBuffer>(),
                ComponentType.ReadWrite<LinkedEntityGroup>()
            }));
            em.SetComponentData(ent, new NetworkStreamConnection
            {
                Value = connection,
                DriverId = NetworkDriverStore.FirstDriverId,
                CurrentState = state
            });
            em.SetComponentData(ent, new NetworkSnapshotAck());
            em.GetBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup { Value = ent });
            // Touch stream buffers so BufferLookup in Receive sees initialized chunks.
            em.GetBuffer<IncomingRpcDataStreamBuffer>(ent);
            em.GetBuffer<OutgoingRpcDataStreamBuffer>(ent);
            em.GetBuffer<OutgoingCommandDataStreamBuffer>(ent);
            em.GetBuffer<IncomingSnapshotDataStreamBuffer>(ent);
            return ent;
        }

        /// <summary>
        /// True when the entity has every component Receive / Rpc / GhostReceive expect.
        /// </summary>
        public static bool TryDescribeMissing(EntityManager em, Entity ent, out string missing)
        {
            missing = null;
            if (!em.Exists(ent))
            {
                missing = "entity-missing";
                return false;
            }

            if (!em.HasComponent<NetworkStreamConnection>(ent)) missing = "NetworkStreamConnection";
            else if (!em.HasComponent<NetworkSnapshotAck>(ent)) missing = "NetworkSnapshotAck";
            else if (!em.HasComponent<CommandTarget>(ent)) missing = "CommandTarget";
            else if (!em.HasBuffer<IncomingRpcDataStreamBuffer>(ent)) missing = "IncomingRpcDataStreamBuffer";
            else if (!em.HasBuffer<OutgoingRpcDataStreamBuffer>(ent)) missing = "OutgoingRpcDataStreamBuffer";
            else if (!em.HasBuffer<OutgoingCommandDataStreamBuffer>(ent)) missing = "OutgoingCommandDataStreamBuffer";
            else if (!em.HasBuffer<IncomingSnapshotDataStreamBuffer>(ent)) missing = "IncomingSnapshotDataStreamBuffer";
            else if (!em.HasBuffer<LinkedEntityGroup>(ent)) missing = "LinkedEntityGroup";
            else
            {
                using var q = em.CreateEntityQuery(typeof(NetworkStreamConnection));
                if (q.CalculateEntityCount() < 1)
                    missing = "query-NetworkStreamConnection-empty";
            }

            return missing == null;
        }
    }
}
