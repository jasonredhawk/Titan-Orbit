using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Copies the finished server pose and the flight inputs that produced it into
    /// <see cref="ShipRemoteFlightSample"/>. Runs after
    /// <see cref="ShipKinematicsSyncSystem"/> so position (deterministic tick) and
    /// velocity (kinematics) are the tick the ghost will send. Players and bots both
    /// already have <see cref="ShipInput"/> on the hull.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(ShipKinematicsSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipRemoteFlightSampleSystem : ISystem
    {
        /// <summary>Require a baked sample so a missing ghost field fails quietly.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipRemoteFlightSample>();
        }

        /// <summary>One sample per simulated hull, stamped with the server tick.</summary>
        public void OnUpdate(ref SystemState state)
        {
            uint tick = 0;
            if (SystemAPI.TryGetSingleton(out NetworkTime networkTime) && networkTime.ServerTick.IsValid)
                tick = networkTime.ServerTick.TickIndexForValidTick;

            foreach (var (sample, transform, kinematics, input) in SystemAPI
                         .Query<RefRW<ShipRemoteFlightSample>, RefRO<LocalTransform>, RefRO<ShipKinematics>, RefRO<ShipInput>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithNone<Prefab>())
            {
                var captured = sample.ValueRO;
                captured.Capture(tick, transform.ValueRO, kinematics.ValueRO, input.ValueRO);
                sample.ValueRW = captured;
            }
        }
    }
}
