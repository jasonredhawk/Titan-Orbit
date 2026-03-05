using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Definition for a single reusable ship part (typically corresponds to a USC module).
    /// Cards reference these by componentKey to know what prefab and stats to apply.
    /// </summary>
    [Serializable]
    public class ShipPartDefinition
    {
        [Tooltip("Normalized component key, e.g. \"AstroEagle_Engine_2\".")]
        public string componentKey;

        [Tooltip("Ship family this part belongs to (e.g. \"AstroEagle\").")]
        public string shipFamily;

        public SlotType slotType;
        public string displayName;

        [Tooltip("USC module prefab used for visuals (instantiated under the chassis).")]
        public GameObject modulePrefab;

        [Tooltip("Default grid width/height for cards that use this part when no explicit override is set.")]
        public int defaultGridWidth = 1;
        public int defaultGridHeight = 1;

        [Tooltip("Default bitmask for the footprint of this part in the grid.")]
        public ulong defaultShapeMask = 1;

        [Header("Default Stat Contribution")]
        public float movementSpeedAdd;
        public float rotationSpeedAdd;
        public float maxHealthAdd;
        public float healthRegenAdd;
        public float energyCapacityAdd;
        public float energyRegenAdd;
        public float gemCapacityAdd;
        public float peopleCapacityAdd;
        public float miningRateAdd;

        public float damageMultiplier = 1f;
        public float fireRateMultiplier = 1f;
        public float bulletSpeedMultiplier = 1f;

        public float massContribution;
    }

    /// <summary>
    /// Catalog of all known USC-derived ship parts. Generated/maintained via editor tooling.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipPartCatalog", menuName = "Titan Orbit/Ship Part Catalog")]
    public class ShipPartCatalog : ScriptableObject
    {
        public List<ShipPartDefinition> parts = new List<ShipPartDefinition>();

        private Dictionary<string, ShipPartDefinition> _lookup;

        public ShipPartDefinition GetPart(string componentKey)
        {
            if (string.IsNullOrEmpty(componentKey)) return null;
            EnsureLookup();
            return _lookup.TryGetValue(componentKey, out var part) ? part : null;
        }

        private void EnsureLookup()
        {
            if (_lookup != null) return;
            _lookup = new Dictionary<string, ShipPartDefinition>(StringComparer.OrdinalIgnoreCase);
            if (parts == null) return;
            foreach (var part in parts)
            {
                if (part == null || string.IsNullOrEmpty(part.componentKey)) continue;
                _lookup[part.componentKey] = part;
            }
        }
    }
}

