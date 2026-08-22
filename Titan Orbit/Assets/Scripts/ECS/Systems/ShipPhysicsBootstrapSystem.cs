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
        /// Zero gravity, more solver iterations, and contact stabilization so compound
        /// ship hulls depenetrate instead of merging. Restitution stays 0 — bounce is
        /// <see cref="TitanOrbit.Simulation.ShipCollisionImpulseLogic"/>.
        /// </summary>
        static void ApplyShipCollisionStepTuning(ref PhysicsStep step)
        {
            step.Gravity = float3.zero;
            step.SolverIterationCount = math.max(step.SolverIterationCount, 12);
            var stabilization = step.SolverStabilizationHeuristicSettings;
            stabilization.EnableSolverStabilization = true;
            step.SolverStabilizationHeuristicSettings = stabilization;
        }
    }
}
