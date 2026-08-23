using Unity.Mathematics;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-ship cache of the latest same-tile ship↔asteroid contact normal from Unity Physics
    /// collision events. Written after Export by <see cref="ShipAsteroidContactFrictionSystem"/>;
    /// read on the <b>next</b> fixed step by <see cref="ShipPhysicsDriveLogic"/> so the motor
    /// cannot re-accelerate into the rock (progressive grind penetration).
    /// <para>
    /// [TITAN-ORBIT] Not ghosted — local predicted / server sim only. Cleared every physics tick
    /// before events run. Asteroid events and <see cref="ShipShipSolidContactSystem"/> both
    /// set <see cref="InContact"/> so drive rejects inward thrust and local display raw-follows
    /// (coast vs ram snaps looks jerky).
    /// </para>
    /// </summary>
    public struct ShipAsteroidContactState : IComponentData
    {
        /// <summary>
        /// 1 when a ship↔asteroid collision event fired this physics step; 0 otherwise.
        /// Drive reads the previous step's value (events run after drive).
        /// </summary>
        public byte InContact;

        /// <summary>
        /// Unit XZ normal pointing from the asteroid toward the ship (separation direction).
        /// Valid only when <see cref="InContact"/> is 1.
        /// </summary>
        public float3 OutwardNormal;
    }
}
