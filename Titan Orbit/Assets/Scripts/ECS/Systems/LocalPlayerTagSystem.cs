using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only: tags the locally owned ship entity with <see cref="LocalPlayerShipTag"/> so
    /// input and presentation code can find "my ship" quickly. Matches ships via CommandTarget,
    /// GhostOwner network id, or GhostOwnerIsLocal. While team/rejoin flow suppresses control,
    /// actively strips any existing local tags so map-load orphans cannot keep the tag.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LocalPlayerTagSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Team / rejoin gate ---
            // [TITAN-ORBIT] Until TeamChoiceConfirmed, strip tags instead of early-return — a prior
            // frame may have tagged a GhostOwner orphan during join before Pending latched.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                foreach (var (_, entity) in SystemAPI
                             .Query<RefRO<LocalPlayerShipTag>>()
                             .WithAll<ShipTag>()
                             .WithEntityAccess())
                {
                    ecb.RemoveComponent<LocalPlayerShipTag>(entity);
                }

                ecb.Playback(state.EntityManager);
                ecb.Dispose();
                return;
            }

            int localNetworkId = GetLocalNetworkId(ref state);

            // --- Path 1: CommandTarget on the in-game connection ---
            // [NETCODE] CommandTarget on the connection points at the controlled ship ghost.
            foreach (var (cmd, entity) in SystemAPI.Query<RefRO<CommandTarget>>().WithAll<NetworkStreamInGame>().WithEntityAccess())
            {
                var target = cmd.ValueRO.targetEntity;
                if (target == Entity.Null || !state.EntityManager.Exists(target))
                    continue;
                if (!state.EntityManager.HasComponent<ShipTag>(target))
                    continue;
                if (!state.EntityManager.HasComponent<LocalPlayerShipTag>(target))
                    ecb.AddComponent<LocalPlayerShipTag>(target);
            }

            // --- Path 2: GhostOwner.NetworkId matches local client ---
            // [NETCODE] GhostOwner.NetworkId matches the local client's network id.
            foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>().WithAll<ShipTag>().WithEntityAccess())
            {
                bool isLocal = localNetworkId > 0 && owner.ValueRO.NetworkId == localNetworkId;
                if (isLocal && !state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.AddComponent<LocalPlayerShipTag>(entity);
                else if (!isLocal && state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.RemoveComponent<LocalPlayerShipTag>(entity);
            }

            // --- Path 3: NetCode GhostOwnerIsLocal tag (fallback) ---
            foreach (var (_, entity) in SystemAPI.Query<RefRO<GhostOwnerIsLocal>>().WithAll<ShipTag>().WithEntityAccess())
            {
                if (!state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.AddComponent<LocalPlayerShipTag>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>Reads NetworkId from the in-game client connection entity.</summary>
        int GetLocalNetworkId(ref SystemState state)
        {
            foreach (var netId in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                return netId.ValueRO.Value;
            return -1;
        }
    }
}
