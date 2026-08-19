using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: applies <see cref="PlayerNameAnnounceRpc"/> into <see cref="PlayerNameRosterCache"/>.
    /// Nameplates and <c>TeamLeaderboardHUD</c> read that cache — they never walk ship entities
    /// for names (join-crash safe: singleton RPC drain only).
    /// World: ClientSimulation. Paired with <see cref="PlayerNameServerSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PlayerNameAnnounceClientSystem : ISystem
    {
        /// <summary>
        /// Copies each inbound announce into the process-wide roster, then destroys the RPC entity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Drain announce RPCs ---
            // [NETCODE] ReceiveRpcCommandRequest marks inbound RPC entities from the network.
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<PlayerNameAnnounceRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                PlayerNameAnnounceRpc row = rpc.ValueRO;
                PlayerNameRosterCache.Upsert(row.NetworkId, row.DisplayName, row.BadgeId);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
