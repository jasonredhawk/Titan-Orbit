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
    /// <para>
    /// [TITAN-ORBIT] During <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> we only
    /// tag the Instantiates-hook <see cref="LocalShipEntitySeed"/> entity — no ship gathers
    /// (Player.log 2026-07-23 TeamChoice Crash!!!).
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LocalPlayerTagSystem : ISystem
    {
        EntityQuery _taggedShipQuery;
        EntityQuery _shipOwnerQuery;
        EntityQuery _shipLocalOwnerQuery;

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
                // [TITAN-ORBIT] Tiny tagged-ship query only (not all ShipTag). Safe enough while
                // suppress is on; we must not leave orphan LocalPlayerShipTag through map load.
                if (!ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                {
                    var tagged = _taggedShipQuery.ToEntityArray(Allocator.Temp);
                    for (int i = 0; i < tagged.Length; i++)
                        ecb.RemoveComponent<LocalPlayerShipTag>(tagged[i]);
                    tagged.Dispose();
                    ecb.Playback(state.EntityManager);
                }

                ecb.Dispose();
                return;
            }

            // --- Ship Instantiates / TeamChoice gap: tag from Instantiates-hook seed only ---
            // [TITAN-ORBIT] Player.log 2026-07-23: TeamChoiceResult lifts suppress then ship
            // gathers Crash!!!. ShouldSkipShipEntityQueries covers Settling, GhostSpawnBacklog
            // (incl. post-ship hold), and the short ArmPostTeamChoiceHold Instantiates gap.
            // One-entity seed path keeps HUD / HasLocalPlayerShip caches warm without ToEntityArray.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (LocalShipEntitySeed.TryGetSeededShip(state.EntityManager, out var seeded) &&
                    seeded != Entity.Null &&
                    state.EntityManager.Exists(seeded) &&
                    state.EntityManager.HasComponent<ShipTag>(seeded) &&
                    LocalShipEntitySeed.EntityMatchesLocalOwner(state.EntityManager, seeded) &&
                    !state.EntityManager.HasComponent<LocalPlayerShipTag>(seeded))
                {
                    ecb.AddComponent<LocalPlayerShipTag>(seeded);
                    ecb.Playback(state.EntityManager);
                }

                ecb.Dispose();
                return;
            }

            int localNetworkId = GetLocalNetworkId(ref state);

            // --- Path 1: CommandTarget on the in-game connection ---
            // After settle, require GhostOwner.NetworkId so a stale CommandTarget cannot
            // tag Player 2. Path 2 strips extras when owner does not match.
            foreach (var cmd in SystemAPI.Query<RefRO<CommandTarget>>().WithAll<NetworkStreamInGame>())
            {
                var target = cmd.ValueRO.targetEntity;
                if (target == Entity.Null || !state.EntityManager.Exists(target))
                    continue;
                if (!state.EntityManager.HasComponent<ShipTag>(target))
                    continue;
                // After settle: only the owned hull. Owner 0 / missing GhostOwner is P2 arriving late.
                if (localNetworkId <= 0 ||
                    !state.EntityManager.HasComponent<GhostOwner>(target) ||
                    state.EntityManager.GetComponentData<GhostOwner>(target).NetworkId != localNetworkId)
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

        /// <summary>Reads NetworkId from the in-game client connection entity.</summary>
        int GetLocalNetworkId(ref SystemState state)
        {
            foreach (var netId in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                return netId.ValueRO.Value;
            return -1;
        }
    }
}
