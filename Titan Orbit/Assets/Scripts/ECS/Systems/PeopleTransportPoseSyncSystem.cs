using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: broadcasts authoritative people-transport poses each tick so client VFX matches
    /// the same <see cref="LocalTransform"/> bullets and delivery use.
    /// <para>
    /// Not a ghost stream — PeopleTransportGhost Instantiates flooded Windows GhostSpawn.
    /// Pose RPCs are tiny and only exist while a few capsules are in flight.
    /// </para>
    /// World: ServerSimulation. Runs after <see cref="PeopleTransportSimulationSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct PeopleTransportPoseSyncSystem : ISystem
    {
        /// <summary>Emits one Active <see cref="PeopleTransportPoseRpc"/> per living transport.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (transport, transform) in SystemAPI
                         .Query<RefRO<PeopleTransportState>, RefRO<LocalTransform>>()
                         .WithAll<PeopleTransportTag>())
            {
                var t = transport.ValueRO;
                if (t.Sequence == 0)
                    continue;

                float3 pos = transform.ValueRO.Position;
                pos.y = 0f;
                PeopleTransportNetNotify.SendPose(
                    ref ecb, t.Sequence, pos, t.Velocity, PeopleTransportPoseStatus.Active);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
