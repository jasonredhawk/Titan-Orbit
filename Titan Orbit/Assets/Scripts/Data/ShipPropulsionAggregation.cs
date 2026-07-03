using UnityEngine;

namespace TitanOrbit.Data
{
    public static class ShipPropulsionAggregation
    {
        public static float ApplyShipLevelMobilityScale(float value, int levelsAboveOne)
        {
            int perLvl = Mathf.Max(0, levelsAboveOne);
            return value - value * 0.11f * perLvl;
        }
    }
}
