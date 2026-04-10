using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject describing a single upgrade card that can be purchased and equipped into a ship's grids.
    /// Cards can either represent a concrete visual part (USC module) or a pure stat modifier.
    /// </summary>
    [CreateAssetMenu(fileName = "New Card", menuName = "Titan Orbit/Card")]
    public class CardData : ScriptableObject
    {
        [Header("Identity")]
        public string cardId;              // Stable ID for save/load and networking (e.g. "AstroEagle_Engine_2_L")
        public string displayName;
        [TextArea]
        public string description;
        public Sprite icon;

        [Header("Sloting")]
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
        [Tooltip("Multiplies people load speed while in orbit (unload uses a fixed base rate on the ship; future cards may add an unload multiplier).")]
        public float peopleTransferSpeedMultiplier = 1f;

        [Header("Mass & Visual")]
        [Tooltip("Additional mass contributed by this part.")]
        public float massContribution;

        [Tooltip("Optional USC module prefab to instantiate for this card (e.g. a wing, engine, gun).")]
        public GameObject partPrefab;

        [Tooltip("Normalized component key from USC mapping (e.g. \"AstroEagle_Engine_2\").")]
        public string componentKey;
    }
}

