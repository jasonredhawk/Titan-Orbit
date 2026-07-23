using TitanOrbit.Core;
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
        /// <summary>Ships are few — safe to ToEntityArray after Instantiates settle (unlike map bodies).</summary>
        EntityQuery _shipQuery;

        /// <summary>Requires an in-game client connection before binding the command target.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            _shipQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
        }

        /// <summary>
        /// [NETCODE] Each GhostInput tick: find local ship ghost and set CommandTarget.targetEntity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Skip during team-pick / rejoin UI ---
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return;

            // --- Skip while ship Instantiates / TeamChoice gap ---
            // [TITAN-ORBIT] Player.log 2026-07-18 / 2026-07-23: TeamChoiceResult → Crash!!! on
            // ship ToEntityArray. Settling stays OFF after JoinSettleCompleted. Do NOT reimplement
            // backlog as buffer/placeholder-only — that misses ArmPostShipInstantiateHold and the
            // TeamChoiceConfirmed → local-ship Instantiates gap. Use ShouldSkipShipEntityQueries.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
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

            // [NETCODE] CommandTarget on the client connection — without this, IInputComponentData
            // is never sent and the server ship coasts while the client predicts motion.
            foreach (var cmd in SystemAPI.Query<RefRW<CommandTarget>>().WithAll<NetworkStreamConnection, NetworkStreamInGame>())
            {
                if (cmd.ValueRO.targetEntity != shipEntity)
                    cmd.ValueRW = new CommandTarget { targetEntity = shipEntity };
            }
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
