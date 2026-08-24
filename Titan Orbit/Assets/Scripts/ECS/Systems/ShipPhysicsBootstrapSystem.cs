using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One-shot Unity Physics setup for NetCode predicted simulation: zero gravity (top-down space)
    /// and <see cref="LagCompensationConfig"/> so client physics can rewind during prediction.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ShipPhysicsBootstrapSystem : ISystem
    {
        bool _applied;

        public void OnUpdate(ref SystemState state)
        {
            if (_applied)
                return;

            if (SystemAPI.HasSingleton<PhysicsStep>())
            {
                var step = SystemAPI.GetSingleton<PhysicsStep>();
                ApplyShipCollisionStepTuning(ref step, state.World.IsClient());
                SystemAPI.SetSingleton(step);
            }
            else
            {
                var singleton = PhysicsStep.Default;
                ApplyShipCollisionStepTuning(ref singleton, state.World.IsClient());
                var stepEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(stepEntity, singleton);
            }

            if (!SystemAPI.HasSingleton<LagCompensationConfig>())
            {
                var lagEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(lagEntity, new LagCompensationConfig
                {
                    ServerHistorySize = state.World.IsServer() ? 16 : 0,
                    ClientHistorySize = state.World.IsClient() ? 16 : 0,
                    DeepCopyDynamicColliders = false,
                    DeepCopyStaticColliders = false,
                });
            }

            _applied = true;
        }

        /// <summary>
        /// Zero gravity, package-default solver cost on the client, a small extra
        /// iteration on the server, and incremental statics so interpolated map hulls
        /// (planets / seed-hydrated asteroids) stay in the collision world.
        /// </summary>
        static void ApplyShipCollisionStepTuning(ref PhysicsStep step, bool client)
        {
            step.Gravity = float3.zero;
            // Unity Physics default is 4. Forcing 12 on both worlds doubled Local Host cost.
            step.SolverIterationCount = client ? 4 : 6;
            step.CollisionTolerance = math.max(step.CollisionTolerance, 0.15f);
            step.MaxDynamicDepenetrationVelocity = math.max(step.MaxDynamicDepenetrationVelocity, 25f);
            step.SynchronizeCollisionWorld = 1;
            // Interpolated map bodies (planets, seed-hydrated asteroids) are static.
            // Incremental static BVH keeps GhostUpdate / hydrate poses in the collision
            // world. Predicted ships are dynamic — they do not need this.
            step.IncrementalStaticBroadphase = true;
            var stabilization = step.SolverStabilizationHeuristicSettings;
            stabilization.EnableSolverStabilization = true;
            step.SolverStabilizationHeuristicSettings = stabilization;
        }
    }
}
