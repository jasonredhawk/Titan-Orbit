using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject describing a single upgrade card that can be purchased and equipped into a ship's
    /// Tetris-style grids (ship, weapon, cargo). Cards either reference a USC visual part via
    /// <see cref="componentKey"/> or apply pure stat modifiers. Serialized on disk; stable id from
    /// <see cref="GetStableCardId"/> is used for save/load. Shop draws filter by level, rarity, and
    /// <see cref="minHomePlanetLevel"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "New Card", menuName = "Titan Orbit/Card")]
    public class CardData : ScriptableObject
    {
        [Header("Identity")]
        /// <summary>Stable ID for save/load and networking (e.g. AstroEagle_Engine_2_L).</summary>
        public string cardId;
        /// <summary>Shop and loadout UI title.</summary>
        public string displayName;
        [TextArea]
        /// <summary>Tooltip body describing stat effects.</summary>
        public string description;
        /// <summary>Icon in card shop grid.</summary>
        public Sprite icon;

        [Header("Sloting")]
        /// <summary>Which equipment grid accepts this card.</summary>
        public SlotType slotType;

        [Tooltip("Grid footprint for this card in squares (Tetris-like shape). Width/height define the bounding box; shapeMask defines which cells are filled.")]
        public int gridWidth = 1;
        public int gridHeight = 1;

        /// <summary>
        /// Bitmask row-major from top-left. For example, a 2x3 L-shape could be:
        /// row0: 1 0  (bits 0,1)
        /// row1: 1 0  (bits 2,3)
        /// row2: 1 1  (bits 4,5)
        /// </summary>
        [Tooltip("Row-major bit mask of occupied cells within the gridWidth x gridHeight footprint. Bit n = 1 means the cell is filled.")]
        public ulong shapeMask;

        [Header("Availability")]
        [Tooltip("Card level (1–n). A ship can only equip cards with level <= ship level. Does not change stat numbers — tune stats on the asset.")]
        public int cardLevel = 1;

        [Tooltip("Affects how often this card is picked in shop draws. Tune combat/economy power with stat fields below.")]
        public CardRarity rarity = CardRarity.Common;

        [Tooltip("Minimum home planet level required before this card can appear in any store.")]
        public int minHomePlanetLevel = 1;

        [Tooltip("Optional origin planet id / index. 0 or negative can mean \"global\" (e.g. starter/home family).")]
        public int originPlanetId = 0;

        [Tooltip("Base gem cost for purchasing this card (before any dynamic modifiers).")]
        public float gemCost = 20f;

        [Header("Requirements")]
        [Tooltip("Minimum effective energy capacity required on the ship before this card becomes purchasable/equippable (e.g. big cannons).")]
        public float minEffectiveEnergyCapacity = 0f;

        [Tooltip("Minimum effective gem capacity required.")]
        public float minEffectiveGemCapacity = 0f;

        [Tooltip("Optional: cardId that must already be present before this card can be used (e.g. requires a specific hull).")]
        public string requiredCardId;

        [Header("Stat Effects")]
        [Tooltip("Flat additive modifiers.")]
        public float movementSpeedAdd;
        public float rotationSpeedAdd;
        public float maxHealthAdd;
        public float healthRegenAdd;
        public float energyCapacityAdd;
        public float energyRegenAdd;
        public float gemCapacityAdd;
        public float peopleCapacityAdd;
        public float miningRateAdd;

        [Tooltip("Multiplicative modifiers (1.0 = no change, 0.5 = half, 1.5 = +50%). Applied on top of base + flat modifiers.")]
        public float damageMultiplier = 1f;
        public float fireRateMultiplier = 1f;
        public float bulletSpeedMultiplier = 1f;
        [Tooltip("Multiplies gem deposit transfer speed while docked at a gem moon.")]
        public float gemDepositSpeedMultiplier = 1f;
        [Tooltip("Multiplies people load and unload speed while in orbit.")]
        public float peopleTransferSpeedMultiplier = 1f;

        [Header("Unique Effects")]
        [Tooltip("Named overlay rows (deposit speed, mining yield, drone HP, …). Applied by CardEffectQuery.")]
        public System.Collections.Generic.List<CardEffect> effects = new System.Collections.Generic.List<CardEffect>();

        [Tooltip("Optional family-style multipliers stacked after the hull's ShipFamilySpecialBonuses. Only ≠1 fields apply.")]
        public ShipFamilySpecialBonuses familyBonusOverlay = ShipFamilySpecialBonuses.Identity;

        [Header("Mass & Visual")]
        [Tooltip("Additional mass contributed by this part.")]
        public float massContribution;

        [Tooltip("Optional USC module prefab to instantiate for this card (e.g. a wing, engine, gun).")]
        public GameObject partPrefab;

        [Tooltip("Normalized component key from USC mapping (e.g. \"AstroEagle_Engine_2\").")]
        public string componentKey;

        private void OnEnable()
        {
            // [TITAN-ORBIT] Heal empty cardId/displayName from asset file name (generator convention).
            CardDataRuntimeRestore.TryRestoreFromAssetName(this);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // --- OnValidate ---
            if (string.IsNullOrEmpty(cardId) && !string.IsNullOrEmpty(name))
                cardId = name;
            CardDataRuntimeRestore.TryRestoreFromAssetName(this);
        }
#endif

        /// <summary>Stable id for networking and save/load. Falls back to the asset file name when cardId was not serialized.</summary>
        public string GetStableCardId()
        {
            if (!string.IsNullOrEmpty(cardId)) return cardId;
            return string.IsNullOrEmpty(name) ? string.Empty : name;
        }

        /// <summary>Player-facing title; uses <see cref="displayName"/> or a name derived from the asset file.</summary>
        public string GetDisplayNameOrDefault()
        {
            // --- Compute value ---
            if (!string.IsNullOrEmpty(displayName)) return displayName;
            CardDataRuntimeRestore.TryRestoreFromAssetName(this);
            return string.IsNullOrEmpty(displayName) ? name : displayName;
        }

        /// <summary>Card body text for shop UI.</summary>
        public string GetDescriptionOrDefault()
        {
            // --- Compute value ---
            if (!string.IsNullOrEmpty(description)) return description;
            CardDataRuntimeRestore.TryRestoreFromAssetName(this);
            return description ?? string.Empty;
        }
    }
}

