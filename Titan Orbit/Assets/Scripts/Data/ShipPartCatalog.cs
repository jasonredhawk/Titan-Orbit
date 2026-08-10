using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One reusable ship part row — typically a USC (Ultimate Spaceships Creator) module prefab
    /// plus default grid footprint and stat deltas. Cards reference parts by
    /// <see cref="componentKey"/> so loadout UI and visual appliers know which prefab to spawn.
    /// Client/editor only — not serialized over NetCode; cards carry the resolved stats at bake time.
    /// </summary>
    [Serializable]
    public class ShipPartDefinition
    {
        /// <summary>Normalized lookup id, e.g. <c>AstroEagle_Engine_2</c>. Case-insensitive in <see cref="ShipPartCatalog"/>.</summary>
        [Tooltip("Normalized component key, e.g. \"AstroEagle_Engine_2\".")]
        public string componentKey;

        /// <summary>Parent ship family name used for grouping in editor tooling and moon-dock filters.</summary>
        [Tooltip("Ship family this part belongs to (e.g. \"AstroEagle\").")]
        public string shipFamily;

        /// <summary>Which Tetris-style grid this part occupies when equipped (<see cref="SlotType"/>).</summary>
        public SlotType slotType;

        /// <summary>Player-facing label in card shop and loadout tooltips.</summary>
        public string displayName;

        /// <summary>[UNITY] USC module prefab parented under the chassis visual proxy at runtime.</summary>
        [Tooltip("USC module prefab used for visuals (instantiated under the chassis).")]
        public GameObject modulePrefab;

        /// <summary>Default footprint width when a <see cref="CardData"/> does not override grid size.</summary>
        [Tooltip("Default grid width/height for cards that use this part when no explicit override is set.")]
        public int defaultGridWidth = 1;

        /// <summary>Default footprint height (rows) for the part's bounding box.</summary>
        public int defaultGridHeight = 1;

        /// <summary>Row-major bitmask of filled cells inside the default grid (see <see cref="CardData.shapeMask"/>).</summary>
        [Tooltip("Default bitmask for the footprint of this part in the grid.")]
        public ulong defaultShapeMask = 1;

        [Header("Default Stat Contribution")]
        /// <summary>Flat additive movement speed (world units/s) when this part is equipped.</summary>
        public float movementSpeedAdd;
        /// <summary>Flat additive turn rate (deg/s).</summary>
        public float rotationSpeedAdd;
        /// <summary>Flat additive max hull hit points.</summary>
        public float maxHealthAdd;
        /// <summary>Flat additive hull regeneration per second.</summary>
        public float healthRegenAdd;
        /// <summary>Flat additive weapon energy pool size.</summary>
        public float energyCapacityAdd;
        /// <summary>Flat additive energy regeneration per second.</summary>
        public float energyRegenAdd;
        /// <summary>Flat additive carried-gem capacity.</summary>
        public float gemCapacityAdd;
        /// <summary>Flat additive colonist / people capacity.</summary>
        public float peopleCapacityAdd;
        /// <summary>Flat additive asteroid mining rate.</summary>
        public float miningRateAdd;

        /// <summary>Multiplicative weapon damage (1 = unchanged). Stacks with card and family stats.</summary>
        public float damageMultiplier = 1f;
        /// <summary>Multiplicative shots-per-second factor.</summary>
        public float fireRateMultiplier = 1f;
        /// <summary>Multiplicative projectile speed factor.</summary>
        public float bulletSpeedMultiplier = 1f;

        /// <summary>[TITAN-ORBIT] Extra rigidbody-style mass contribution for motor feel.</summary>
        public float massContribution;
    }

    /// <summary>
    /// ScriptableObject catalog of all USC-derived ship parts. Editor tooling generates and maintains
    /// the list; <see cref="CardData"/> and loadout systems look up prefabs and default grid footprints
    /// by <see cref="ShipPartDefinition.componentKey"/>. Loaded as a project asset — not per-scene.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipPartCatalog", menuName = "Titan Orbit/Ship Part Catalog")]
    public class ShipPartCatalog : ScriptableObject
    {
        /// <summary>Authoritative list of part rows maintained by editor import pipelines.</summary>
        public List<ShipPartDefinition> parts = new List<ShipPartDefinition>();

        /// <summary>[STANDARD] Lazy dictionary for O(1) key lookup; rebuilt once per asset load.</summary>
        private Dictionary<string, ShipPartDefinition> _lookup;

        /// <summary>
        /// Case-insensitive lookup by normalized component key (e.g. AstroEagle_Engine_2).
        /// Returns null when the key is missing or empty.
        /// </summary>
        public ShipPartDefinition GetPart(string componentKey)
        {
            if (string.IsNullOrEmpty(componentKey)) return null;

            // --- Build cache on first access ---
            EnsureLookup();
            return _lookup.TryGetValue(componentKey, out var part) ? part : null;
        }

        /// <summary>Populates <see cref="_lookup"/> from <see cref="parts"/>; skips null rows and blank keys.</summary>
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
