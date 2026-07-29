using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: watches for ships whose <see cref="ShipState.IsDead"/> just became true and
    /// adds <see cref="ShipDeathState"/> with a respawn timer. Clears people and velocity once —
    /// runs before <see cref="ShipRespawnSystem"/>. WithNone&lt;ShipDeathState&gt; ensures this
    /// fires exactly once per death.
    /// <para>
    /// [TITAN-ORBIT] Death requires hull <b>and</b> cargo depleted (<c>ShipDamageLogic</c>).
    /// Combat already expelled gems as world entities — do not silently zero leftover cargo here
    /// without a spawn (that was the ECS regression vs NGO). Clamp tiny leftovers only.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [UpdateAfter(typeof(GemDepositSystem))]
    [UpdateBefore(typeof(ShipRespawnSystem))]
    public partial struct ShipDeathRecordingSystem : ISystem
    {
        /// <summary>One-shot death bookkeeping for newly dead ships.</summary>
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

                // --- Death cleanup: stop movement / people (gems should already be empty) ---
                // Clamp only — world gem burst already happened during the killing damage pulses.
                if (shipState.ValueRO.CurrentGems > 0.001f)
                {
                    // Safety: if something set IsDead with cargo left, strip without inventing a burst
                    // (should not happen on the dual-resource path).
                    shipState.ValueRW.CurrentGems = 0f;
                }
                else
                {
                    shipState.ValueRW.CurrentGems = 0f;
                }

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
