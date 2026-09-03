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
                ApplyShipCollisionStepTuning(ref step);
                SystemAPI.SetSingleton(step);
            }
            else
            {
                var singleton = PhysicsStep.Default;
                ApplyShipCollisionStepTuning(ref singleton);
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
        /// Unity Physics owns ship↔ship and ship↔world contacts.
        /// Speculative CCD is <see cref="PhysicsStep.CollisionTolerance"/> (not per-ship
        /// compound <c>CastCollider</c>). Incremental broadphase keeps 333 asteroids +
        /// 60–100 ships from full-rebuilding the BVH when a rock is culled or a hull moves.
        /// <see cref="PhysicsStep.SynchronizeCollisionWorld"/> stays off — AfterPhysics
        /// gameplay reads the collision-event stream, not a resynced query world.
        /// </summary>
        static void ApplyShipCollisionStepTuning(ref PhysicsStep step)
        {
            step.Gravity = float3.zero;
            step.SimulationType = SimulationType.UnityPhysics;
            step.MultiThreaded = 1;
            step.SubstepCount = 1;
            // Default is 4. 8 covers compound hull stacking without the 12-iteration cost
            // that scales with every dynamic pair at 60–100 ships.
            step.SolverIterationCount = 8;
            // Speculative contacts: hulls that would tunnel at cruise speed still generate events.
            step.CollisionTolerance = 0.15f;
            step.MaxDynamicDepenetrationVelocity = 25f;
            step.SynchronizeCollisionWorld = 0;
            step.IncrementalDynamicBroadphase = true;
            step.IncrementalStaticBroadphase = true;
            var stabilization = step.SolverStabilizationHeuristicSettings;
            stabilization.EnableSolverStabilization = true;
            step.SolverStabilizationHeuristicSettings = stabilization;
        }
    }
}
