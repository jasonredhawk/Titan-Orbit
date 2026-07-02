using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Marks lethal ships for delayed respawn (added once when IsDead becomes true).</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [UpdateBefore(typeof(ShipRespawnSystem))]
    public partial struct ShipDeathRecordingSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipState, kinematics, orbitState, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<ShipOrbitState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipDeathState>()
                         .WithEntityAccess())
            {
                if (!shipState.ValueRO.IsDead)
                    continue;

                shipState.ValueRW.CurrentGems = 0f;
                shipState.ValueRW.CurrentPeople = 0;
                kinematics.ValueRW.Velocity = Unity.Mathematics.float3.zero;
                orbitState.ValueRW.OrbitPlanetId = 0;
                orbitState.ValueRW.InOrbitRing = false;
                orbitState.ValueRW.UsingOrbitMotor = false;

                ecb.AddComponent(entity, new ShipDeathState
                {
                    RespawnAtTime = now + ShipRespawnSystem.RespawnDelaySeconds,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
