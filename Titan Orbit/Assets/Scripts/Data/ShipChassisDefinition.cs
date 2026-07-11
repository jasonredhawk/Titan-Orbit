using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Runtime chassis metadata for one upgrade-tree tier inside a <see cref="ShipFamilyDefinition"/>.
    /// Often created on the fly (not saved as an asset) when orbit UI or CardShop needs id, prefab,
    /// grid dimensions, and unlock level. Bridges designer families to per-slot purchase rules.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipChassis", menuName = "Titan Orbit/Ship Chassis")]
    public class ShipChassisDefinition : ScriptableObject
    {
        [Header("Identity")]
        /// <summary>Stable string id for save data and RPC payloads (e.g. AstroEagle_L3_b1).</summary>
        public string chassisId;

        /// <summary>Parent family id matching <see cref="ShipFamilyDefinition.familyId"/>.</summary>
        public string shipFamily;

        /// <summary>Player-facing hull name in moon dock and upgrade tree.</summary>
        public string displayName;

        [Header("Base Prefab & Data")]
        /// <summary>[UNITY] Root prefab for this tier — USC chassis with component children.</summary>
        public GameObject basePrefab;

        /// <summary>Legacy <see cref="ShipData"/> row for banking, mass, and tree branching metadata.</summary>
        public ShipData baseShipData;

        [Header("Grid Layout")]
        /// <summary>Ship-slot grid width (Tetris-style equipment on the hull).</summary>
        public int shipGridWidth = 3;

        /// <summary>Ship-slot grid height.</summary>
        public int shipGridHeight = 7;

        /// <summary>Weapon-slot grid width.</summary>
        public int weaponGridWidth = 2;

        /// <summary>Weapon-slot grid height.</summary>
        public int weaponGridHeight = 3;

        /// <summary>Cargo-slot grid width.</summary>
        public int cargoGridWidth = 3;

        /// <summary>Cargo-slot grid height.</summary>
        public int cargoGridHeight = 4;

        [Header("Origin & Unlocking")]
        /// <summary>Planet that originally sells this chassis; 0 may mean global / homeworld family.</summary>
        public int originPlanetId;

        /// <summary>Minimum captured homeworld level before this chassis appears in stores.</summary>
        public int minHomePlanetLevel = 1;
    }
}
