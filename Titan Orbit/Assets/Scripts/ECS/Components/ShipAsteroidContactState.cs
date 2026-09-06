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
    /// before events run; stays zero when the hull is free of asteroid contacts. Ship↔ship
    /// contacts set <see cref="InContactHull"/> for presentation coast only.
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
        /// 1 when a ship↔ship collision event fired this physics step. Presentation only —
        /// drive still treats hull contacts as Unity Physics.
        /// </summary>
        public byte InContactHull;

        /// <summary>
        /// Unit XZ normal pointing from the asteroid toward the ship (separation direction).
        /// Valid only when <see cref="InContact"/> is 1.
        /// </summary>
        public float3 OutwardNormal;
    }
}
