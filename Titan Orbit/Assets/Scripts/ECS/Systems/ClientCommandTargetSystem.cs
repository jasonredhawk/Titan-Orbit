using TitanOrbit.Core;
using TitanOrbit.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client-only: points the connection's <see cref="CommandTarget"/> at the locally
    /// owned ship ghost so NetCode packages <see cref="ShipInput"/> commands every tick.
    /// Required for both dedicated clients and Local Host (Client+Server in one process) —
    /// skipping Local Host left the server ship without input while the client predicted thrust,
    /// which showed up as cmdAge≈24 and forward-then-snap-back. Runs first in
    /// GhostInputSystemGroup, before ShipInputApplySystem. World: ClientSimulation.
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ClientCommandTargetSystem : ISystem
    {
        /// <summary>Ships are few — safe to ToEntityArray (unlike map-body gathers).</summary>
        EntityQuery _shipQuery;

        /// <summary>GhostSpawn Instantiates backlog (placeholders).</summary>
        EntityQuery _placeholderQuery;

        /// <summary>Last bound ship — avoids spamming debug NDJSON every GhostInput tick.</summary>
        Entity _debugLastBoundShip;

        /// <summary>Requires an in-game client connection before binding the command target.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            _shipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
            _placeholderQuery = state.GetEntityQuery(ComponentType.ReadOnly<PendingSpawnPlaceholder>());
        }

        /// <summary>
        /// [NETCODE] Each GhostInput tick: find local ship ghost and set CommandTarget.targetEntity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Skip during team-pick / rejoin UI ---
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return;

            // --- Skip while GhostSpawn Instantiates settle ---
            // [TITAN-ORBIT] Settling alone is NOT enough after JoinSettleCompleted: TeamChoice ship
            // Instantiates no longer flip Settling ON (that path Crash!!!'d). Player.log 2026-07-18
            // 20:51: TeamChoiceResult → Crash!!! in WithEntityAccess GetEntityDataPtrRO.
            if (ClientJoinSettleCache.Settling || HasGhostSpawnBacklog(ref state))
                return;

            int localNetworkId = GetLocalNetworkId(ref state);
            if (localNetworkId <= 0)
                return;

            // --- Find the ship ghost owned by this client ---
            // [TITAN-ORBIT] Prefer ToEntityArray on the tiny ship set — WithEntityAccess NRE'd during
            // post-team Instantiates (stale chunk entity ptr). Ships ≠ map bodies; count is small.
            Entity shipEntity = Entity.Null;
            var entities = _shipQuery.ToEntityArray(Allocator.Temp);
            var owners = _shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != localNetworkId)
                    continue;
                shipEntity = entities[i];
                break;
            }

            entities.Dispose();
            owners.Dispose();

            if (shipEntity == Entity.Null)
                return;

            // #region agent log
            if (_debugLastBoundShip != shipEntity)
            {
                _debugLastBoundShip = shipEntity;
                AgentDebugSessionLog.Write("post-fix", "F", "ClientCommandTargetSystem.OnUpdate",
                    "command_target_bound",
                    "{\"shipIndex\":" + shipEntity.Index + ",\"netId\":" + localNetworkId + "}");
            }
            // #endregion

            // [NETCODE] CommandTarget on the client connection — without this, IInputComponentData
            // is never sent and the server ship coasts while the client predicts motion.
            foreach (var cmd in SystemAPI.Query<RefRW<CommandTarget>>().WithAll<NetworkStreamConnection, NetworkStreamInGame>())
            {
                if (cmd.ValueRO.targetEntity != shipEntity)
                    cmd.ValueRW = new CommandTarget { targetEntity = shipEntity };
            }
        }

        /// <summary>
        /// True while GhostSpawn still has queued Instantiates or placeholders — unsafe for ship
        /// entity iteration right after Join Team.
        /// </summary>
        bool HasGhostSpawnBacklog(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonEntity<GhostSpawnQueue>(out Entity spawnQueue) &&
                state.EntityManager.HasBuffer<GhostSpawnBuffer>(spawnQueue) &&
                state.EntityManager.GetBuffer<GhostSpawnBuffer>(spawnQueue).Length > 0)
                return true;

            return !_placeholderQuery.IsEmptyIgnoreFilter;
        }

        /// <summary>Reads this client's NetworkId from the in-game connection.</summary>
        int GetLocalNetworkId(ref SystemState state)
        {
            foreach (var netId in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                return netId.ValueRO.Value;
            return -1;
        }
    }
}
