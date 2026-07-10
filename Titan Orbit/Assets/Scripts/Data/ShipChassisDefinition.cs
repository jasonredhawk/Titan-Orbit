using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime chassis metadata resolved from a <see cref="ShipFamilyDefinition"/> upgrade-tree tier.
    /// Often created on the fly (not saved as an asset) when orbit UI or CardShop needs id, prefab, and unlock level.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipChassis", menuName = "Titan Orbit/Ship Chassis")]
    public class ShipChassisDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string chassisId;
        public string shipFamily;
        public string displayName;

        [Header("Base Prefab & Data")]
        public GameObject basePrefab;
        public ShipData baseShipData;

        [Header("Grid Layout")]
        public int shipGridWidth = 3;
        public int shipGridHeight = 7;
        public int weaponGridWidth = 2;
        public int weaponGridHeight = 3;
        public int cargoGridWidth = 3;
        public int cargoGridHeight = 4;

        [Header("Origin & Unlocking")]
        public int originPlanetId;
        public int minHomePlanetLevel = 1;
    }
}
