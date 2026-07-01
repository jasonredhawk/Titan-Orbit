using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Marks a weapon barrel transform on the ship prefab. Baked into the parent ship's mount buffer.
    /// Bullet direction uses this transform's local forward (same as legacy Weapon components).
    /// </summary>
    public class ShipWeaponMountAuthoring : MonoBehaviour
    {
        [Tooltip("Index into the ship weapon config cannon list.")]
        public int CannonIndex;
        [Tooltip("Extra yaw offset in degrees from the weapon transform forward.")]
        public float DirectionAngleDeg;
    }
}
