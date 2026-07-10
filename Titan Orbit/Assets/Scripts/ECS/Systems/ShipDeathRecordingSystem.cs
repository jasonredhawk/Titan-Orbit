using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: watches for ships whose <see cref="ShipState.IsDead"/> just became true and
    /// adds <see cref="ShipDeathState"/> with a respawn timer. Clears cargo and velocity once —
    /// runs before <see cref="ShipRespawnSystem"/>. WithNone&lt;ShipDeathState&gt; ensures this
    /// fires exactly once per death.
    /// </summary>
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

                // --- Death cleanup: drop cargo, stop movement ---
                shipState.ValueRW.CurrentGems = 0f;
                shipState.ValueRW.CurrentPeople = 0;
                kinematics.ValueRW.Velocity = Unity.Mathematics.float3.zero;
                orbitState.ValueRW.OrbitPlanetId = 0;
                orbitState.ValueRW.InOrbitRing = false;
                orbitState.ValueRW.UsingOrbitMotor = false;

                // [TITAN-ORBIT] Schedule respawn — ShipRespawnSystem removes this component later.
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
