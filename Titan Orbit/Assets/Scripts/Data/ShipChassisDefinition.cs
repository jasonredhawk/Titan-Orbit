using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Describes a gameplay chassis: which family it belongs to, which base prefab to use,
    /// and how big the Ship/Weapon/Cargo grids are.
    /// Visual variants (USC modular prefabs) are handled as presets on top of this chassis.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipChassis", menuName = "Titan Orbit/Ship Chassis")]
    public class ShipChassisDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string chassisId;           // e.g. "Starter", "AstroEagle_Family"
        public string shipFamily;         // e.g. "AstroEagle", "VoidWhale"
        public string displayName;

        [Header("Base Prefab & Data")]
        [Tooltip("Base prefab to instantiate under Starship -> BankPivot -> Prefab. USC family root prefab or a simplified hull.")]
        public GameObject basePrefab;

        [Tooltip("Base ShipData used for this chassis (defines base stats).")]
        public ShipData baseShipData;

        [Header("Grid Layout")]
        public int shipGridWidth = 3;
        public int shipGridHeight = 7;

        public int weaponGridWidth = 2;
        public int weaponGridHeight = 3;

        public int cargoGridWidth = 3;
        public int cargoGridHeight = 4;

        [Header("Origin & Unlocking")]
        [Tooltip("Planet id this chassis is associated with. 0 or negative can mean home/starter.")]
        public int originPlanetId = 0;

        [Tooltip("Minimum home planet level required before this chassis becomes available anywhere.")]
        public int minHomePlanetLevel = 1;
    }
}

