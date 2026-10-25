using TitanOrbit;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: applies gift RPCs into <see cref="OrbitUnlockedGiftClientState"/>
    /// so the panel can show the button, the roster, and the "yours" line.
    /// World: ClientSimulation. Dedicated servers do not run this.
    /// Local Host also writes that state from the server system, because a
    /// server→client RPC can drop under join load.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct OrbitUnlockedGiftClientSystem : ISystem
    {
        /// <summary>Drains every gift RPC that arrived this tick. They are one-shot.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (_, entity) in SystemAPI.Query<RefRO<GiftAdminAccessRpc>>().WithEntityAccess())
            {
                OrbitUnlockedGiftClientState.GrantAdmin();
                ecb.DestroyEntity(entity);
            }

            foreach (var (row, entity) in SystemAPI.Query<RefRO<GiftRosterEntryRpc>>().WithEntityAccess())
            {
                OrbitUnlockedGiftClientState.ApplyEntry(row.ValueRO);
                ecb.DestroyEntity(entity);
            }

            foreach (var (result, entity) in SystemAPI.Query<RefRO<GiftOrbitUnlockedResultRpc>>().WithEntityAccess())
            {
                OrbitUnlockedGiftClientState.ApplyResult(result.ValueRO.TargetNetworkId, result.ValueRO.Status);
                ecb.DestroyEntity(entity);
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<OrbitUnlockedGiftedRpc>>().WithEntityAccess())
            {
                ApplyGifted();
                ecb.DestroyEntity(entity);
            }

            foreach (var (result, entity) in SystemAPI.Query<RefRO<GiftBetaTesterResultRpc>>().WithEntityAccess())
            {
                OrbitUnlockedGiftClientState.ApplyBetaResult(result.ValueRO.TargetNetworkId, result.ValueRO.Status);
                ecb.DestroyEntity(entity);
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<BetaTesterGiftedRpc>>().WithEntityAccess())
            {
                OrbitUnlockedGiftClientState.NoteBetaTesterGifted();
                ecb.DestroyEntity(entity);
            }

            foreach (var (session, entity) in SystemAPI.Query<RefRO<BetaTesterSessionRpc>>().WithEntityAccess())
            {
                BetaTesterClientFlags.ApplyServer(session.ValueRO.Owned != 0, session.ValueRO.Flags);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Marks the local player as gifted. The panel reads Cloud Save from
        /// there — this assembly cannot call the account service.
        /// </summary>
        [Unity.Burst.BurstDiscard]
        static void ApplyGifted()
        {
            OrbitUnlockedGiftClientState.NoteGifted();
        }
    }
}
