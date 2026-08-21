using Unity.Mathematics;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-ship cache of the latest ship↔asteroid contact normal from the torus resolve.
    /// Written by <see cref="ShipToroidalWorldCollisionSystem"/>; read on the <b>next</b>
    /// fixed step by <see cref="ShipPhysicsDriveLogic"/> so the motor cannot re-accelerate
    /// into the rock (progressive grind penetration).
    /// <para>
    /// [TITAN-ORBIT] Not ghosted — local predicted / server sim only. Cleared every physics tick
    /// before resolve; stays zero when the hull is free of asteroid contacts.
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
