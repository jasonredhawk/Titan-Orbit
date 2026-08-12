using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] Marks a wing transform on the ship visual prefab and stores authored tractor-beam
    /// stats (search radius, pull power, max gems). At bake time, <see cref="StarshipGhostAuthoring"/>
    /// copies these into <see cref="ShipWingTractorBeamElement"/> on the ship ghost;
    /// <see cref="GemTractorBeamSystem"/> reads the buffer server-side to assign and pull gems.
    /// Stats scale with ship level and widen in orbit rings via GemTractorBeamMath.
    /// </summary>
    public class ShipWingTractorBeamAuthoring : MonoBehaviour
    {
        // --- Type members ---
        [Header("Tractor Beam (normal space)")]

        /// <summary>[TITAN-ORBIT] Base gem search radius at ship level 1 (world units).</summary>
        public float tractorBeamDistance = 3f;

        /// <summary>[TITAN-ORBIT] Additional search radius per ship level above 1.</summary>
        public float tractorBeamDistancePerExtraLevel = 0.75f;

        /// <summary>[TITAN-ORBIT] Base gem pull speed at ship level 1.</summary>
        public float tractorBeamPower = 4f;

        /// <summary>[TITAN-ORBIT] Additional pull speed per ship level above 1.</summary>
        public float tractorBeamPowerPerExtraLevel = 1f;

        /// <summary>[TITAN-ORBIT] Base max gems this wing holds at ship level 1.</summary>
        public float maxGems = 8f;

        /// <summary>[TITAN-ORBIT] Additional gem capacity per ship level above 1.</summary>
        public float maxGemsPerExtraLevel = 2f;
    }
}
