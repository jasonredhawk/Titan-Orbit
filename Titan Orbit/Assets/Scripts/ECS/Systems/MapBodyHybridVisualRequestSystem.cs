using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client: marks Instantiated planet/asteroid/gem ghosts that still need a GameObject proxy.
    /// Runs after ghost Instantiates so <see cref="EcsWorldVisualizer"/> can create visuals in the
    /// same loading phase (one progress bar), without querying every map body each frame.
    /// World: ClientSimulation. Group: SimulationSystemGroup (late).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    public partial struct MapBodyHybridVisualRequestSystem : ISystem
    {
        /// <summary>
        /// Max new Pending tags per frame. GhostSpawn Instantiates at most 1/frame; keep headroom
        /// for planets/gems Instantiated in the same window without flooding structural changes.
        /// </summary>
        public const int MaxMarksPerFrame = 4;

        /// <summary>Requires in-game so we do not mark during menu.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// Adds <see cref="MapBodyHybridVisualPending"/> to Instantiated map bodies that are not
        /// already pending or linked.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            int marked = 0;

            // --- Asteroids ---
            // [NETCODE] PendingSpawnPlaceholder = not Instantiated yet (no AsteroidState usually).
            // Instantiated asteroids have AsteroidState + no placeholder.
            foreach (var (_, entity) in SystemAPI
                         .Query<RefRO<AsteroidState>>()
                         .WithNone<PendingSpawnPlaceholder, MapBodyHybridVisualPending, MapBodyHybridVisualLinked>()
                         .WithEntityAccess())
            {
                if (marked >= MaxMarksPerFrame)
                    break;
                ecb.AddComponent<MapBodyHybridVisualPending>(entity);
                marked++;
            }

            // --- Planets ---
            if (marked < MaxMarksPerFrame)
            {
                foreach (var (_, entity) in SystemAPI
                             .Query<RefRO<PlanetState>>()
                             .WithAll<PlanetTag>()
                             .WithNone<PendingSpawnPlaceholder, MapBodyHybridVisualPending, MapBodyHybridVisualLinked>()
                             .WithEntityAccess())
                {
                    if (marked >= MaxMarksPerFrame)
                        break;
                    ecb.AddComponent<MapBodyHybridVisualPending>(entity);
                    marked++;
                }
            }

            // --- Gems ---
            if (marked < MaxMarksPerFrame)
            {
                foreach (var (_, entity) in SystemAPI
                             .Query<RefRO<GemState>>()
                             .WithAll<GemTag>()
                             .WithNone<PendingSpawnPlaceholder, MapBodyHybridVisualPending, MapBodyHybridVisualLinked>()
                             .WithEntityAccess())
                {
                    if (marked >= MaxMarksPerFrame)
                        break;
                    ecb.AddComponent<MapBodyHybridVisualPending>(entity);
                    marked++;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
