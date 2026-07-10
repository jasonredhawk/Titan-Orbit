using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Legacy ScriptableObject holding per-ship base stats, weapon config, and visual tuning.
    /// Newer hulls derive combat numbers from <see cref="ShipFamilyDefinition"/> component sums; this asset
    /// still backs upgrade-tree nodes, mass/visual scale, and banking feel on individual prefabs.
    /// </summary>
    [CreateAssetMenu(fileName = "New Ship Data", menuName = "Titan Orbit/Ship Data")]
    public class ShipData : ScriptableObject
    {
        [Header("Ship Identity")]
        public int shipLevel = 1;
        [Tooltip("0-based index of this ship within its level (e.g. level 2 has 2 ships: 0 and 1). Used for upgrade tree branching.")]
        public int branchIndex = 0;
        public ShipFocusType focusType = ShipFocusType.Fighter;
        public string shipName = "Basic Ship";

        [Header("Base Stats")]
        [Tooltip("Rigidbody mass when empty. Fighters lighter, transport/mining heavier; scales with level.")]
        public float baseMass = 1f;
        [Tooltip("Visual scale of ship model. Fighters smaller, transport/mining larger.")]
        public float visualScale = 1f;
        public float baseMovementSpeed = 8f;
        [Tooltip("Weapon config: cannons, rate, energy, damage, spread. Same bullet skin for all ships.")]
        public WeaponConfig weaponConfig;
        public float baseMaxHealth = 100f;
        public float baseHealthRegenRate = 6f;
        public float baseRotationSpeed = 180f;
        public float baseGemCapacity = 100f;
        public float basePeopleCapacity = 10f;
        [Header("Energy (weapon system)")]
        public float baseEnergyCapacity = 50f;  // Max stored energy for energy weapons
        public float baseEnergyRegenRate = 5f;  // Energy per second

        [Header("Mining Stats")]
        public float baseMiningRate = 10f;
        public float miningMultiplier = 1f;

        [Header("Visual")]
        public Sprite shipSprite;
        public GameObject shipPrefab;
        public Color shipColor = Color.white;

        [Header("Banking (per-ship)")]
        [Tooltip("Maximum roll angle (degrees) for the fastest-turning ship at full turn. Others scale down toward 35°.")]
        public float maxBankAngle = 111f;
        [Tooltip("How quickly roll catches up to the target.")]
        public float bankSmoothing = 8f;
    }
}
