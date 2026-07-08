using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Configures Unity Physics for a top-down space game:
    ///  - zero gravity (server + client)
    ///  - on the server, forces the NetCode-gated PhysicsSystemGroup to tick every full tick by
    ///    enabling lag compensation. Without this, interpolated-only ghosts would leave the physics
    ///    group idle and ships (now physics-driven) would never move.
    /// The client intentionally has no lag-compensation config so its physics loop stays idle and
    /// ships remain purely snapshot-interpolated.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TitanOrbitPhysicsBootstrapSystem : ISystem
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
                SystemAPI.SetSingleton(step);
            }
            else
            {
                var singleton = PhysicsStep.Default;
                singleton.Gravity = float3.zero;
                var stepEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(stepEntity, singleton);
            }

            if (state.World.IsServer() && !SystemAPI.HasSingleton<LagCompensationConfig>())
            {
                var lagEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(lagEntity, new LagCompensationConfig
                {
                    ServerHistorySize = 16,
                    ClientHistorySize = 0,
                    DeepCopyDynamicColliders = false,
                    DeepCopyStaticColliders = false,
                });
            }

            _applied = true;
        }
    }
}
