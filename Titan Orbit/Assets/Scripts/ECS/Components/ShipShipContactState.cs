using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-ship cache of the latest ship↔ship contact normal. Cleared every physics tick.
    /// Unity Physics owns ship↔ship bounce; this stays zero so drive does not glue hulls.
    /// <para>
    /// [TITAN-ORBIT] Not ghosted — local predicted / server sim only. Cleared every physics
    /// tick before ship↔ship resolve; stays zero when the hull is free of ship contacts.
    /// </para>
    /// </summary>
    public struct ShipShipContactState : IComponentData
    {
        /// <summary>
        /// 1 when a ship↔ship overlap was resolved this physics step; 0 otherwise.
        /// Drive reads the previous step's value (resolve runs after drive).
        /// </summary>
        public byte InContact;

        /// <summary>
        /// Unit XZ normal pointing from the other ship toward this hull (separation direction).
        /// Valid only when <see cref="InContact"/> is 1.
        /// </summary>
        public float3 OutwardNormal;
    }
}
