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
        EntityQuery _taggedShipQuery;
        EntityQuery _shipOwnerQuery;
        EntityQuery _shipLocalOwnerQuery;
        EntityQuery _placeholderQuery;

        /// <summary>Caches small ship queries (never map-body full scans).</summary>
        public void OnCreate(ref SystemState state)
        {
            _taggedShipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<LocalPlayerShipTag>(),
                ComponentType.ReadOnly<ShipTag>());
            _shipOwnerQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
            _shipLocalOwnerQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwnerIsLocal>());
            _placeholderQuery = state.GetEntityQuery(ComponentType.ReadOnly<PendingSpawnPlaceholder>());
        }

        /// <summary>Each sim tick: strip or apply LocalPlayerShipTag for the owned ship.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Team / rejoin gate ---
            // [TITAN-ORBIT] Until TeamChoiceConfirmed, strip tags instead of early-return — a prior
            // frame may have tagged a GhostOwner orphan during join before Pending latched.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                var tagged = _taggedShipQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < tagged.Length; i++)
                    ecb.RemoveComponent<LocalPlayerShipTag>(tagged[i]);
                tagged.Dispose();

                ecb.Playback(state.EntityManager);
                ecb.Dispose();
                return;
            }

            // --- Ship Instantiates settle (post Join Team) ---
            // [TITAN-ORBIT] Player.log 2026-07-18 20:51: TeamChoiceResult → Crash!!! in ship
            // WithEntityAccess while Settling stays OFF (JoinSettleCompleted). Skip while GhostSpawn
            // still has Instantiates backlog; use ToEntityArray on the tiny ship set afterward.
            if (ClientJoinSettleCache.Settling || HasGhostSpawnBacklog(ref state))
            {
                ecb.Dispose();
                return;
            }

            int localNetworkId = GetLocalNetworkId(ref state);

            // --- Path 1: CommandTarget on the in-game connection ---
            // [NETCODE] CommandTarget on the connection points at the controlled ship ghost.
            foreach (var cmd in SystemAPI.Query<RefRO<CommandTarget>>().WithAll<NetworkStreamInGame>())
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
            var ships = _shipOwnerQuery.ToEntityArray(Allocator.Temp);
            var owners = _shipOwnerQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < ships.Length; i++)
            {
                Entity entity = ships[i];
                bool isLocal = localNetworkId > 0 && owners[i].NetworkId == localNetworkId;
                if (isLocal && !state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.AddComponent<LocalPlayerShipTag>(entity);
                else if (!isLocal && state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.RemoveComponent<LocalPlayerShipTag>(entity);
            }

            ships.Dispose();
            owners.Dispose();

            // --- Path 3: NetCode GhostOwnerIsLocal tag (fallback) ---
            var localOwned = _shipLocalOwnerQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < localOwned.Length; i++)
            {
                Entity entity = localOwned[i];
                if (!state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.AddComponent<LocalPlayerShipTag>(entity);
            }

            localOwned.Dispose();

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>True while GhostSpawn Instantiates / placeholders are still draining.</summary>
        bool HasGhostSpawnBacklog(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonEntity<GhostSpawnQueue>(out Entity spawnQueue) &&
                state.EntityManager.HasBuffer<GhostSpawnBuffer>(spawnQueue) &&
                state.EntityManager.GetBuffer<GhostSpawnBuffer>(spawnQueue).Length > 0)
                return true;

            return !_placeholderQuery.IsEmptyIgnoreFilter;
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
