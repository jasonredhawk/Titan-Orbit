using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// One wing-mounted gem tractor beam. Reach and pull strength come from Capacity stats on the wing in ShipFamilyDefinition.
    /// </summary>
    public struct WingTractorBeamSlot
    {
        public Transform wingTransform;
        public float tractorBeamDistance;
        public float tractorBeamDistancePerLevel;
        public float tractorBeamPower;
        public float tractorBeamPowerPerLevel;
        public float maxGems;
        public float maxGemsPerLevel;

        public WingTractorBeamSlot(
            Transform wingTransform,
            float tractorBeamDistance,
            float tractorBeamDistancePerLevel,
            float tractorBeamPower,
            float tractorBeamPowerPerLevel,
            float maxGems,
            float maxGemsPerLevel)
        {
            this.wingTransform = wingTransform;
            this.tractorBeamDistance = tractorBeamDistance;
            this.tractorBeamDistancePerLevel = tractorBeamDistancePerLevel;
            this.tractorBeamPower = tractorBeamPower;
            this.tractorBeamPowerPerLevel = tractorBeamPowerPerLevel;
            this.maxGems = maxGems;
            this.maxGemsPerLevel = maxGemsPerLevel;
        }

        public void GetTractorParams(int shipLevel, bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            GemTractorBeamSettings.GetTractorBeamFromStats(
                tractorBeamDistance,
                tractorBeamDistancePerLevel,
                tractorBeamPower,
                tractorBeamPowerPerLevel,
                maxGems,
                maxGemsPerLevel,
                shipLevel,
                inOrbitZone,
                out searchRadius,
                out attractionSpeed);
        }

        public Vector3 GetWorldPosition()
        {
            if (wingTransform == null)
                return Vector3.zero;
            Vector3 pos = wingTransform.position;
            pos.y = 0f;
            return pos;
        }
    }
}
