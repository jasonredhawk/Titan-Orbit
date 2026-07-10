using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Marks a weapon barrel transform on the ship visual prefab. At runtime,
    /// ShipWeaponMountSyncSystem copies local pose + CannonIndex into the ship ghost's
    /// ShipWeaponMountElement buffer. BulletSimulationSystem and ShipWeaponPose use
    /// these entries for muzzle origin and fire direction. Place on child transforms
    /// under the hull — not on the root.
    /// </summary>
    public class ShipWeaponMountAuthoring : MonoBehaviour
    {
        [Tooltip("Index into the ship weapon config cannon list.")]
        public int CannonIndex;
        [Tooltip("Extra yaw offset in degrees from the weapon transform forward.")]
        public float DirectionAngleDeg;
    }
}
