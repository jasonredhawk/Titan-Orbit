using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] Marks a weapon barrel transform on the ship visual prefab hierarchy. At bake time,
    /// <see cref="StarshipGhostAuthoring"/> collects these into a <see cref="ShipWeaponMountElement"/>
    /// DynamicBuffer on the ship ghost. <see cref="ShipWeaponPose"/> and
    /// <see cref="BulletSimulationSystem"/> use buffer entries for muzzle origin and fire direction.
    /// Place on child transforms under the hull — not on the root.
    /// </summary>
    public class ShipWeaponMountAuthoring : MonoBehaviour
    {
        // --- Type members ---
        /// <summary>[TITAN-ORBIT] Index into the ship weapon config cannon list for this barrel.</summary>
        [Tooltip("Index into the ship weapon config cannon list.")]
        public int CannonIndex;

        /// <summary>[TITAN-ORBIT] Extra yaw offset in degrees from the weapon transform forward.</summary>
        [Tooltip("Extra yaw offset in degrees from the weapon transform forward.")]
        public float DirectionAngleDeg;
    }
}
