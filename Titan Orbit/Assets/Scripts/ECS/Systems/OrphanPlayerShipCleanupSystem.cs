using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: destroy a hull whose owner is gone so a recycled <see cref="NetworkId"/>
    /// cannot fly it, after copying that ship into <see cref="MatchPlayerShipStore"/>.
    /// Coming back to this same match offers the snapshot. A second live hull for one id
    /// is deleted; the newest stays and <see cref="CommandTarget"/> points at it.
    /// World: ServerSimulation. Group: SimulationSystemGroup, after team spawn.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamManagementSystem))]
    public partial struct OrphanPlayerShipCleanupSystem : ISystem
    {
        /// <summary>Ship hulls. Cached so an empty match does not build a query every tick.</summary>
        EntityQuery _ships;

        /// <summary>Main-menu leave RPCs. Must run even when no ship is left, or a late RPC can hit a recycled NetworkId.</summary>
        EntityQuery _leaveRpcs;

        /// <summary>Caches the small ship and connection queries (player count, not map bodies).</summary>
        public void OnCreate(ref SystemState state)
        {
            _ships = state.GetEntityQuery(ComponentType.ReadOnly<ShipTag>());
            _leaveRpcs = state.GetEntityQuery(
                ComponentType.ReadOnly<LeaveMatchDespawnShipCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
        }

        /// <summary>
        /// Drops disconnected owners and extra hulls that share one live NetworkId.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            bool hasLeave = !_leaveRpcs.IsEmpty;
            bool hasShips = !_ships.IsEmpty;
            if (!hasLeave && !hasShips)
                return;

            var em = state.EntityManager;
            if (hasLeave)
                ConsumeLeaveRpcs(ref state, em);
            if (_ships.IsEmpty)
                return;

            var liveIds = new NativeHashSet<int>(16, Allocator.Temp);
            var connectionById = new NativeHashMap<int, Entity>(16, Allocator.Temp);

            // --- Who is still in the match ---
            // [NETCODE] RequestDisconnect means this client already hit Leave. Do not let that
            // id protect the old hull for another tick.
            foreach (var (netId, entity) in SystemAPI.Query<RefRO<NetworkId>>()
                         .WithAll<NetworkStreamConnection>()
                         .WithNone<NetworkStreamRequestDisconnect>()
                         .WithEntityAccess())
            {
                int id = netId.ValueRO.Value;
                if (id <= 0)
                    continue;
                liveIds.Add(id);
                connectionById[id] = entity;
            }

            var bestById = new NativeHashMap<int, Entity>(16, Allocator.Temp);
            var destroy = new NativeList<Entity>(16, Allocator.Temp);

            foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                int id = owner.ValueRO.NetworkId;
                // Owner 0 is a hull mid-spawn. Leave it for TeamManagementSystem.
                if (id <= 0)
                    continue;

                if (!liveIds.Contains(id))
                {
                    destroy.Add(entity);
                    continue;
                }

                if (!bestById.TryGetValue(id, out Entity best))
                {
                    bestById[id] = entity;
                    continue;
                }

                if (ShipGhostAge.IsNewerThan(em, entity, best))
                {
                    destroy.Add(best);
                    bestById[id] = entity;
                }
                else
                    destroy.Add(entity);
            }

            // Snapshot disconnected hulls before roster release frees a titan bay.
            // A duplicate of someone still connected is not a leave — do not overwrite their save.
            var captureBest = new NativeHashMap<int, Entity>(16, Allocator.Temp);
            for (int i = 0; i < destroy.Length; i++)
            {
                Entity candidate = destroy[i];
                if (!em.Exists(candidate) || !em.HasComponent<GhostOwner>(candidate))
                    continue;
                int ownerId = em.GetComponentData<GhostOwner>(candidate).NetworkId;
                if (ownerId <= 0 || liveIds.Contains(ownerId))
                    continue;
                if (!captureBest.TryGetValue(ownerId, out Entity incumbent) ||
                    ShipGhostAge.IsNewerThan(em, candidate, incumbent))
                    captureBest[ownerId] = candidate;
            }

            foreach (var pair in captureBest)
            {
                MatchPlayerShipStore.TryCapture(em, pair.Value);
                MatchPlayerShipStore.Unbind(pair.Key);
            }

            captureBest.Dispose();

            // Roster math first. AddComponent / DestroyEntity are structural and would
            // invalidate a singleton ref taken earlier in the tick.
            var releasedIds = new NativeHashSet<int>(16, Allocator.Temp);
            if (destroy.Length > 0 && SystemAPI.TryGetSingletonRW<TeamStateSingleton>(out var team))
            {
                for (int i = 0; i < destroy.Length; i++)
                {
                    Entity ship = destroy[i];
                    if (!em.Exists(ship) || !em.HasComponent<GhostOwner>(ship))
                        continue;
                    int id = em.GetComponentData<GhostOwner>(ship).NetworkId;
                    if (id > 0 && liveIds.Contains(id))
                        continue;
                    if (!releasedIds.Contains(id))
                    {
                        releasedIds.Add(id);
                        ReleaseRosterSlot(em, ref team.ValueRW, ship);
                    }
                }
            }

            // --- Drive input at the hull we kept ---
            foreach (var pair in bestById)
            {
                if (!connectionById.TryGetValue(pair.Key, out Entity connection) || !em.Exists(connection))
                    continue;
                var target = new CommandTarget { targetEntity = pair.Value };
                if (em.HasComponent<CommandTarget>(connection))
                {
                    if (em.GetComponentData<CommandTarget>(connection).targetEntity != pair.Value)
                        em.SetComponentData(connection, target);
                }
                else
                    em.AddComponentData(connection, target);
            }

            for (int i = 0; i < destroy.Length; i++)
            {
                Entity ship = destroy[i];
                if (!em.Exists(ship))
                    continue;
                int id = 0;
                bool disconnected = true;
                if (em.HasComponent<GhostOwner>(ship))
                {
                    id = em.GetComponentData<GhostOwner>(ship).NetworkId;
                    disconnected = id <= 0 || !liveIds.Contains(id);
                }
                LogRemoved(id, !disconnected);
                em.DestroyEntity(ship);
            }

            releasedIds.Dispose();

            destroy.Dispose();
            bestById.Dispose();
            connectionById.Dispose();
            liveIds.Dispose();
        }

        /// <summary>
        /// Main-menu leave RPC: destroy that connection's hull before the transport times out.
        /// </summary>
        void ConsumeLeaveRpcs(ref SystemState state, EntityManager em)
        {
            var ids = new NativeList<int>(4, Allocator.Temp);
            var rpcEntities = new NativeList<Entity>(4, Allocator.Temp);
            foreach (var (req, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<LeaveMatchDespawnShipCommand>()
                         .WithEntityAccess())
            {
                rpcEntities.Add(entity);
                Entity connection = req.ValueRO.SourceConnection;
                if (connection != Entity.Null && em.Exists(connection) && em.HasComponent<NetworkId>(connection))
                    ids.Add(em.GetComponentData<NetworkId>(connection).Value);
            }

            for (int i = 0; i < rpcEntities.Length; i++)
            {
                if (em.Exists(rpcEntities[i]))
                    em.DestroyEntity(rpcEntities[i]);
            }

            for (int i = 0; i < ids.Length; i++)
                PlayerShipExit.DespawnOwner(em, ids[i], releaseRoster: true);

            ids.Dispose();
            rpcEntities.Dispose();
        }

        /// <summary>Frees the team slot and any titan bay this hull was holding.</summary>
        static void ReleaseRosterSlot(EntityManager em, ref TeamStateSingleton team, Entity ship)
        {
            if (!em.Exists(ship))
                return;

            int networkId = 0;
            if (em.HasComponent<GhostOwner>(ship))
                networkId = em.GetComponentData<GhostOwner>(ship).NetworkId;
            if (em.HasComponent<MegaShipState>(ship))
                MegaShipStatApplyLogic.ReleaseMegaOccupancy(em, ship);
            if (networkId > 0)
                MegaShipPlanetLogic.FreeSlotsOccupiedBy(em, networkId);

            if (!em.HasComponent<ShipState>(ship))
                return;
            TeamId teamId = em.GetComponentData<ShipState>(ship).Team;
            switch (teamId)
            {
                case TeamId.TeamA: team.TeamACount = Unity.Mathematics.math.max(0, team.TeamACount - 1); break;
                case TeamId.TeamB: team.TeamBCount = Unity.Mathematics.math.max(0, team.TeamBCount - 1); break;
                case TeamId.TeamC: team.TeamCCount = Unity.Mathematics.math.max(0, team.TeamCCount - 1); break;
                case TeamId.TeamD: team.TeamDCount = Unity.Mathematics.math.max(0, team.TeamDCount - 1); break;
                case TeamId.TeamE: team.TeamECount = Unity.Mathematics.math.max(0, team.TeamECount - 1); break;
            }
        }

        [Unity.Burst.BurstDiscard]
        static void LogRemoved(int networkId, bool duplicateOfLiveOwner)
        {
            UnityEngine.Debug.Log(
                duplicateOfLiveOwner
                    ? $"[OrphanPlayerShipCleanup] Removed older duplicate ship networkId={networkId}."
                    : $"[OrphanPlayerShipCleanup] Removed disconnected ship networkId={networkId}.");
        }
    }
}
