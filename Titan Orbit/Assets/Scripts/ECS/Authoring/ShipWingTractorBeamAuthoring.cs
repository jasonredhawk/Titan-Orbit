using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>Marks a wing transform and stores authored tractor-beam stats for gem pull.</summary>
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
