using System;
using Unity.Collections;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Safe read of Unity Physics collision events. Predicted ClientWorld often has
    /// <c>CollisionEventDataStream.IsCreated == true</c> after a dispose, so Burst
    /// <c>ICollisionEventsJob</c> NREs in <c>NativeStream.ForEachCount</c>.
    /// Walk the enumerator on the main thread and swallow a dead stream.
    /// </summary>
    static class PhysicsCollisionEventStream
    {
        public static bool TryCopyEvents(in SimulationSingleton sim, NativeList<CollisionEvent> dest)
        {
            dest.Clear();
            if (sim.Type != SimulationType.UnityPhysics)
                return false;

            try
            {
                var simulation = sim.AsSimulation();
                foreach (var ev in simulation.CollisionEvents)
                    dest.Add(ev);
                return true;
            }
            catch (Exception)
            {
                dest.Clear();
                return false;
            }
        }
    }
}
