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
                step.Gravity = float3.zero;
                // [PHYSICS] Extra solver iterations help fast ships hit static asteroid colliders.
                step.SolverIterationCount = math.max(step.SolverIterationCount, 8);
                SystemAPI.SetSingleton(step);
            }
            else
            {
                var singleton = PhysicsStep.Default;
                singleton.Gravity = float3.zero;
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
    }
}
