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
    /// which showed up as cmdAge≈24 and forward-then-snap-back.
    /// <para>
    /// [TITAN-ORBIT] While <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> is true
    /// (GhostSpawn Instantiates / post–TeamChoice hold), we bind from
    /// <see cref="LocalShipEntitySeed"/> only — never ship <c>ToEntityArray</c> (Crash!!!).
    /// </para>
    /// Runs first in GhostInputSystemGroup, before ShipInputApplySystem. World: ClientSimulation.
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

            Entity shipEntity = Entity.Null;

            // --- Instantiates / TeamChoice gap: bind from Instantiates-hook seed only ---
            // [TITAN-ORBIT] Player.log 2026-07-18 / 2026-07-23: TeamChoiceResult → Crash!!! on
            // ship ToEntityArray. Settling stays OFF after JoinSettleCompleted. Do NOT reimplement
            // backlog as buffer/placeholder-only — that misses ArmPostShipInstantiateHold and the
            // TeamChoiceConfirmed → local-ship Instantiates gap. Use ShouldSkipShipEntityQueries.
            // One known entity from LocalShipEntitySeed is safe (no ship archetype gather) and lets
            // input bind as soon as the hull Instantiates — even while asteroid/gem Instantiates
            // keep GhostSpawnBacklog true.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (!LocalShipEntitySeed.TryGetSeededShip(state.EntityManager, out shipEntity) ||
                    shipEntity == Entity.Null)
                    return;
                BindCommandTarget(ref state, shipEntity);
                return;
            }

            int localNetworkId = GetLocalNetworkId(ref state);
            if (localNetworkId <= 0)
                return;

            // --- Find the ship ghost owned by this client ---
            // [TITAN-ORBIT] Prefer ToEntityArray on the tiny ship set — WithEntityAccess NRE'd during
            // post-team Instantiates (stale chunk entity ptr). Ships ≠ map bodies; count is small.
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

            BindCommandTarget(ref state, shipEntity);
        }

        /// <summary>
        /// Writes <see cref="CommandTarget.targetEntity"/> on the in-game client connection.
        /// Without this, <see cref="ShipInput"/> is never sent and the server ship coasts.
        /// </summary>
        /// <param name="state">System state for the connection query.</param>
        /// <param name="shipEntity">Local owned ship ghost.</param>
        // [ECS/DOTS] Must be instance (not static) — SystemAPI.Query needs the generated type handle.
        void BindCommandTarget(ref SystemState state, Entity shipEntity)
        {
            // [NETCODE] CommandTarget on the client connection — packages IInputComponentData each tick.
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
