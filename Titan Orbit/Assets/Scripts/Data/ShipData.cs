using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Legacy ScriptableObject holding per-ship base stats, weapon config, and visual tuning for one
    /// upgrade-tree hull slot. Newer families derive combat numbers from <see cref="ShipFamilyDefinition"/>
    /// component sums; this asset still backs upgrade-tree nodes, mass/visual scale, and banking feel
    /// on individual prefabs. Referenced by <see cref="ShipUpgradeNode"/> inside <see cref="UpgradeTree"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "New Ship Data", menuName = "Titan Orbit/Ship Data")]
    public class ShipData : ScriptableObject
    {
        [Header("Ship Identity")]
        /// <summary>Upgrade-tree tier 1–7. Determines which card levels the hull may equip.</summary>
        public int shipLevel = 1;

        /// <summary>
        /// 0-based index of this ship within its level (e.g. level 2 has two ships: 0 and 1).
        /// Used with <see cref="UpgradeTree.IsValidUpgradeStep"/> for branching edges.
        /// </summary>
        [Tooltip("0-based index of this ship within its level (e.g. level 2 has 2 ships: 0 and 1). Used for upgrade tree branching.")]
        public int branchIndex = 0;

        /// <summary>High-level role tag (fighter, miner, transport) for UI icons and AI weighting.</summary>
        public ShipFocusType focusType = ShipFocusType.Fighter;

        /// <summary>Display name shown in upgrade tree and rejoin-ship UI.</summary>
        public string shipName = "Basic Ship";

        [Header("Base Stats")]
        /// <summary>[TITAN-ORBIT] Rigidbody-style mass when empty. Fighters lighter; transport/mining heavier.</summary>
        [Tooltip("Rigidbody mass when empty. Fighters lighter, transport/mining heavier; scales with level.")]
        public float baseMass = 1f;

        /// <summary>Uniform visual scale applied to the ship mesh proxy (not collision radius directly).</summary>
        [Tooltip("Visual scale of ship model. Fighters smaller, transport/mining larger.")]
        public float visualScale = 1f;

        /// <summary>Baseline top speed before cards, components, and motor multipliers.</summary>
        public float baseMovementSpeed = 8f;

        /// <summary>Cannon layout, fire rate, energy cost, and spread — shared bullet skin across ships.</summary>
        [Tooltip("Weapon config: cannons, rate, energy, damage, spread. Same bullet skin for all ships.")]
        public WeaponConfig weaponConfig;

        /// <summary>Maximum hull hit points at spawn before card and component adds.</summary>
        public float baseMaxHealth = 100f;

        /// <summary>Passive hull regeneration per second.</summary>
        public float baseHealthRegenRate = 6f;

        /// <summary>Yaw turn rate in degrees per second.</summary>
        public float baseRotationSpeed = 180f;

        /// <summary>Maximum gems the hull can carry in cargo.</summary>
        public float baseGemCapacity = 100f;

        /// <summary>Maximum people / colonists the hull can transport.</summary>
        public float basePeopleCapacity = 10f;

        [Header("Energy (weapon system)")]
        /// <summary>Maximum stored energy for energy weapons and ability fire costs.</summary>
        public float baseEnergyCapacity = 50f;

        /// <summary>Energy recovered per second when not over capacity.</summary>
        public float baseEnergyRegenRate = 5f;

        [Header("Mining Stats")]
        /// <summary>Base asteroid mining throughput before multipliers.</summary>
        public float baseMiningRate = 10f;

        /// <summary>Designer tuning multiplier on <see cref="baseMiningRate"/>.</summary>
        public float miningMultiplier = 1f;

        [Header("Visual")]
        /// <summary>2D icon for minimap and upgrade-tree nodes.</summary>
        public Sprite shipSprite;

        /// <summary>[UNITY] Chassis prefab spawned when this hull is selected (may be superseded by family prefab).</summary>
        public GameObject shipPrefab;

        /// <summary>Team-neutral tint multiplier on the visual proxy material.</summary>
        public Color shipColor = Color.white;

        [Header("Banking (per-ship overrides — optional)")]
        /// <summary>
        /// [TITAN-ORBIT] Optional peak roll (°). Prefer the global knobs on scene
        /// <c>EcsWorldVisualizer</c> → Ship Banking unless this hull needs a unique lean.
        /// </summary>
        [Tooltip(
            "Optional peak roll (°). Leave at 111 and tune EcsWorldVisualizer → Ship Banking " +
            "for the whole session instead.")]
        public float maxBankAngle = 111f;

        /// <summary>
        /// Optional smoothing override. Prefer <c>EcsWorldVisualizer</c> → Ship Banking → Smoothing.
        /// </summary>
        [Tooltip("Optional roll catch-up rate. Prefer EcsWorldVisualizer → Ship Banking for global feel.")]
        public float bankSmoothing = 8f;
    }
}
