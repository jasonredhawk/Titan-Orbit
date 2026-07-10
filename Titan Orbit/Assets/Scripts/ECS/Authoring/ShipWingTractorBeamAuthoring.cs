using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Marks a wing transform on the ship visual prefab and stores authored tractor-beam
    /// stats (search radius, pull power, max gems). ShipWingTractorBeamSyncSystem copies
    /// these into ShipWingTractorBeamElement on the ship ghost; GemTractorBeamSystem
    /// reads the buffer server-side to assign and pull gems. Stats scale with ship level
    /// and widen in orbit rings via GemTractorBeamMath.
    /// </summary>
    public class ShipWingTractorBeamAuthoring : MonoBehaviour
    {
        [Header("Tractor Beam (normal space)")]
        public float tractorBeamDistance = 3f;
        public float tractorBeamDistancePerLevel = 0.75f;
        public float tractorBeamPower = 4f;
        public float tractorBeamPowerPerLevel = 1f;
        public float maxGems = 8f;
        public float maxGemsPerLevel = 2f;
    }
}
