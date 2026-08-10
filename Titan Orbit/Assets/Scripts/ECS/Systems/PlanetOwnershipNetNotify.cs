using System.Collections.Generic;
using TitanOrbit.Core;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify for planet ownership flips (capture / starting neutrals).
    /// <para>
    /// [TITAN-ORBIT] Planet ghosts are low-Importance / rate-limited under MaxSendChunks, so
    /// connection lines and minimap triangles would lag on ghost snapshots alone. This RPC
    /// (plus an in-process host mirror) applies Ownership immediately and forces a client
    /// graph rebuild — same pattern as <see cref="PeopleTransportNetNotify"/> / bullet VFX RPCs.
    /// </para>
    /// </summary>
    public static class PlanetOwnershipNetNotify
    {
        /// <summary>Scratch list for quarantine-safe planet registry walks (managed, reused).</summary>
        static readonly List<Entity> s_RegistryScratch = new List<Entity>(64);

        /// <summary>
        /// Broadcasts ownership to all clients and mirrors into the host ClientWorld when present.
        /// </summary>
        /// <param name="ecb">Server command buffer (Playback later this system update).</param>
        /// <param name="planetId">Stable planet id.</param>
        /// <param name="team">New owner.</param>
        /// <param name="population">Population after the flip.</param>
        /// <param name="planetLevel">Level at flip time.</param>
        public static void Send(
            ref EntityCommandBuffer ecb,
            int planetId,
            TeamId team,
            int population,
            int planetLevel)
        {
            if (planetId == 0 || team == TeamId.None)
                return;

            byte teamByte = (byte)team;
            int level = planetLevel < 1 ? 1 : planetLevel;
            int pop = population < 0 ? 0 : population;

            // --- Host in-process (Editor / listen-server) ---
            // Apply before the remote RPC round-trip so local lines/minimap update this frame.
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
            {
                ApplyToClientWorld(
                    ClientServerBootstrap.ClientWorld.EntityManager,
                    planetId,
                    team,
                    pop,
                    level);
            }

            // --- All remote clients (+ host client connection) ---
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new PlanetOwnershipChangedRpc
            {
                PlanetId = planetId,
                Team = teamByte,
                Population = pop,
                PlanetLevel = level,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Optimistic client apply: override graph fingerprint + write PlanetState when the
        /// Instantiated ghost is already in the registry.
        /// </summary>
        public static void ApplyToClientWorld(
            EntityManager em,
            int planetId,
            TeamId team,
            int population,
            int planetLevel)
        {
            // --- Graph override (works even if the entity is not Instantiated yet) ---
            PlanetConnectionGraphCache.SetClientOwnershipOverride(planetId, team, population, planetLevel);

            // --- Per-entity write via Instantiates registry (no planet archetype gather) ---
            PlanetClientEntityRegistry.CopyLive(s_RegistryScratch);
            for (int i = 0; i < s_RegistryScratch.Count; i++)
            {
                Entity entity = s_RegistryScratch[i];
                if (entity == Entity.Null ||
                    !em.Exists(entity) ||
                    !em.HasComponent<PlanetState>(entity))
                    continue;

                var state = em.GetComponentData<PlanetState>(entity);
                if (state.PlanetId != planetId)
                    continue;

                state.Ownership = team;
                state.Population = population < 0 ? 0 : population;
                if (planetLevel > 0)
                    state.PlanetLevel = planetLevel;
                em.SetComponentData(entity, state);
                break;
            }
        }
    }

    /// <summary>
    /// Client: consumes <see cref="PlanetOwnershipChangedRpc"/> and applies optimistic Ownership
    /// so connection topology / minimap refresh without waiting for rate-limited planet ghosts.
    /// World: ClientSimulation. Group: SimulationSystemGroup (before graph rebuild).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PlanetConnectionGraphClientSystem))]
    public partial struct PlanetOwnershipChangedRpcClientSystem : ISystem
    {
        /// <summary>Requires receive-RPC queue.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ReceiveRpcCommandRequest>();
        }

        /// <summary>
        /// Applies each ownership RPC then destroys the request entity.
        /// Host may already have applied via <see cref="PlanetOwnershipNetNotify"/> — idempotent.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
            var em = state.EntityManager;

            foreach (var (rpc, rpcEntity) in SystemAPI
                         .Query<RefRO<PlanetOwnershipChangedRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var cmd = rpc.ValueRO;
                if (cmd.PlanetId != 0)
                {
                    PlanetOwnershipNetNotify.ApplyToClientWorld(
                        em,
                        cmd.PlanetId,
                        (TeamId)cmd.Team,
                        cmd.Population,
                        cmd.PlanetLevel);
                }

                ecb.DestroyEntity(rpcEntity);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }
    }
}
