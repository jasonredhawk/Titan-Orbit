using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-side handler for reconnect / rejoin ship RPCs. When a player reconnects mid-match,
    /// they can resume their existing ship (<see cref="ResumeExistingShipCommand"/>) or abandon
    /// it and pick a fresh team (<see cref="AbandonShipForRejoinCommand"/>). Updates CommandTarget
    /// so NetCode routes input to the correct ghost. On resume, the ship is teleported to a
    /// random point on the team's home orbit ring (same helper as new spawn / death respawn,
    /// outside the moon dock zone) with cleared velocity so reconnect never continues from the
    /// disconnect location. Runs after TeamManagementSystem.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamManagementSystem))]
    public partial struct RejoinShipManagementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // [ECS/DOTS] Team counts must exist before abandon can decrement roster slots.
            state.RequireForUpdate<TeamStateSingleton>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            // [ECS/DOTS] ECB — RPC entities are destroyed; CommandTarget updates are deferred safely.
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Resume existing ship RPC ---
            // [NETCODE] ReceiveRpcCommandRequest — incoming RPC entity; destroy after handling.
            foreach (var (req, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<ResumeExistingShipCommand>().WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                HandleResume(ref state, em, ecb, req.ValueRO.SourceConnection);
            }

            // --- Abandon ship and re-pick team RPC ---
            foreach (var (req, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<AbandonShipForRejoinCommand>().WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                HandleAbandon(ref state, em, ecb, req.ValueRO.SourceConnection);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// Re-links the connection's CommandTarget to their saved ship, teleports that ship to the
        /// team home planet, clears motion, and confirms team assignment.
        /// </summary>
        void HandleResume(ref SystemState state, EntityManager em, EntityCommandBuffer ecb, Entity connection)
        {
            // --- Resolve sender and saved ship ---
            if (!TryGetNetworkId(em, connection, out int networkId))
            {
                SendResult(ecb, connection, success: false, choice: 1, team: TeamId.None, "Missing network id.");
                return;
            }

            if (!TryFindShipForNetworkId(ref state, networkId, out Entity ship, out ShipState shipState))
            {
                SendResult(ecb, connection, success: false, choice: 1, team: TeamId.None, "No saved ship found.");
                return;
            }

            if (shipState.Team == TeamId.None || shipState.AwaitingTeamSelection)
            {
                SendResult(ecb, connection, success: false, choice: 1, team: TeamId.None, "Saved ship is not active.");
                return;
            }

            // --- Teleport to home orbit ring (never resume at last disconnect position) ---
            // [TITAN-ORBIT] Same random ring spawn as TeamManagementSystem / ShipRespawnSystem,
            // excluding the gem-moon dock zone so reconnect does not open the Orbit Menu.
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;
            float3 homePos = ShipHomeSpawnLogic.FindHomeSpawnPosition(em, shipState.Team, orbitElapsed);
            if (em.HasComponent<LocalTransform>(ship))
            {
                var transform = em.GetComponentData<LocalTransform>(ship);
                transform.Position = homePos;
                transform.Rotation = quaternion.identity;
                ecb.SetComponent(ship, transform);
            }

            // [PHYSICS] Zero hull velocity so prediction does not coast from stale motion.
            if (em.HasComponent<PhysicsVelocity>(ship))
                ecb.SetComponent(ship, PhysicsVelocity.Zero);

            // [TITAN-ORBIT] Clear gameplay kinematics + leave any orbit ring from before disconnect.
            if (em.HasComponent<ShipKinematics>(ship))
            {
                var kinematics = em.GetComponentData<ShipKinematics>(ship);
                kinematics.Velocity = float3.zero;
                ecb.SetComponent(ship, kinematics);
            }

            if (em.HasComponent<ShipOrbitState>(ship))
            {
                var orbit = em.GetComponentData<ShipOrbitState>(ship);
                orbit.OrbitPlanetId = 0;
                orbit.InOrbitRing = false;
                orbit.UsingOrbitMotor = false;
                ecb.SetComponent(ship, orbit);
            }

            // [NETCODE] CommandTarget tells NetCode which ghost receives this connection's input.
            var commandTarget = new CommandTarget { targetEntity = ship };
            if (em.HasComponent<CommandTarget>(connection))
                ecb.SetComponent(connection, commandTarget);
            else
                ecb.AddComponent(connection, commandTarget);

            SendResult(ecb, connection, success: true, choice: 1, team: shipState.Team, default);
            LogResume(networkId, shipState.Team, homePos);
        }

        /// <summary>
        /// Destroys the saved ship, decrements team count, and clears CommandTarget for fresh team pick.
        /// </summary>
        void HandleAbandon(ref SystemState state, EntityManager em, EntityCommandBuffer ecb, Entity connection)
        {
            // --- Resolve sender; no ship is still a successful abandon (fresh team pick) ---
            if (!TryGetNetworkId(em, connection, out int networkId))
            {
                SendResult(ecb, connection, success: false, choice: 2, team: TeamId.None, "Missing network id.");
                return;
            }

            if (!TryFindShipForNetworkId(ref state, networkId, out Entity ship, out ShipState shipState))
            {
                ClearCommandTarget(em, ecb, connection);
                SendResult(ecb, connection, success: true, choice: 2, team: TeamId.None, default);
                return;
            }

            if (shipState.Team != TeamId.None)
            {
                var teamState = SystemAPI.GetSingletonRW<TeamStateSingleton>();
                DecrementTeamCount(ref teamState.ValueRW, shipState.Team);
            }

            // [TITAN-ORBIT] Abandoned MEGA returns to the planet store immediately.
            MegaShipPlanetLogic.FreeSlotsOccupiedBy(em, networkId);

            ecb.DestroyEntity(ship);
            ClearCommandTarget(em, ecb, connection);
            SendResult(ecb, connection, success: true, choice: 2, team: TeamId.None, default);
            LogAbandon(networkId, shipState.Team);
        }

        /// <summary>Reads <see cref="NetworkId"/> from the NetCode connection entity that sent the RPC.</summary>
        static bool TryGetNetworkId(EntityManager em, Entity connection, out int networkId)
        {
            networkId = 0;
            if (connection == Entity.Null || !em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                return false;
            networkId = em.GetComponentData<NetworkId>(connection).Value;
            return networkId > 0;
        }

        /// <summary>Finds the ship ghost owned by this network id (GhostOwner.NetworkId).</summary>
        bool TryFindShipForNetworkId(ref SystemState state, int networkId, out Entity ship, out ShipState shipState)
        {
            ship = Entity.Null;
            shipState = default;
            foreach (var (owner, stateComp, entity) in SystemAPI.Query<RefRO<GhostOwner>, RefRO<ShipState>>()
                         .WithAll<ShipTag>().WithEntityAccess())
            {
                if (owner.ValueRO.NetworkId != networkId)
                    continue;
                ship = entity;
                shipState = stateComp.ValueRO;
                return true;
            }

            return false;
        }

        /// <summary>Clears <see cref="CommandTarget"/> so team-pick UI can assign a new ship ghost later.</summary>
        static void ClearCommandTarget(EntityManager em, EntityCommandBuffer ecb, Entity connection)
        {
            if (connection == Entity.Null || !em.Exists(connection) || !em.HasComponent<CommandTarget>(connection))
                return;
            ecb.SetComponent(connection, new CommandTarget { targetEntity = Entity.Null });
        }

        /// <summary>Decrements one roster slot when a player abandons their ship mid-match.</summary>
        static void DecrementTeamCount(ref TeamStateSingleton team, TeamId teamId)
        {
            // [TITAN-ORBIT] Clamp at zero — avoids negative counts if state was already adjusted.
            switch (teamId)
            {
                case TeamId.TeamA: team.TeamACount = Unity.Mathematics.math.max(0, team.TeamACount - 1); break;
                case TeamId.TeamB: team.TeamBCount = Unity.Mathematics.math.max(0, team.TeamBCount - 1); break;
                case TeamId.TeamC: team.TeamCCount = Unity.Mathematics.math.max(0, team.TeamCCount - 1); break;
                case TeamId.TeamD: team.TeamDCount = Unity.Mathematics.math.max(0, team.TeamDCount - 1); break;
                case TeamId.TeamE: team.TeamECount = Unity.Mathematics.math.max(0, team.TeamECount - 1); break;
            }
        }

        /// <summary>Sends RejoinShipResultRpc back to the requesting client connection.</summary>
        static void SendResult(
            EntityCommandBuffer ecb,
            Entity connection,
            bool success,
            byte choice,
            TeamId team,
            FixedString128Bytes message)
        {
            var resultEntity = ecb.CreateEntity();
            ecb.AddComponent(resultEntity, new RejoinShipResultRpc
            {
                Success = (byte)(success ? 1 : 0),
                Choice = choice,
                AssignedTeam = (byte)team,
                Message = message,
            });
            ecb.AddComponent(resultEntity, new SendRpcCommandRequest { TargetConnection = connection });
        }

        [Unity.Burst.BurstDiscard]
        static void LogResume(int networkId, TeamId team, float3 homePos)
        {
            UnityEngine.Debug.Log(
                $"[RejoinShipManagement] Resumed ship for networkId={networkId} team={team} at home {homePos}.");
        }

        [Unity.Burst.BurstDiscard]
        static void LogAbandon(int networkId, TeamId team)
        {
            UnityEngine.Debug.Log($"[RejoinShipManagement] Abandoned ship for networkId={networkId} team={team}.");
        }
    }
}
