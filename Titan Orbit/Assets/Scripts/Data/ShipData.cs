using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject containing ship statistics and configuration
    /// </summary>
    [CreateAssetMenu(fileName = "New Ship Data", menuName = "Titan Orbit/Ship Data")]
    public class ShipData : ScriptableObject
    {
        [Header("Ship Identity")]
        public int shipLevel = 1;
        public ShipFocusType focusType = ShipFocusType.Fighter;
        public string shipName = "Basic Ship";

        [Header("Base Stats")]
        public float baseMovementSpeed = 10f;
        public float baseFireRate = 1f;
        public float baseFirePower = 10f;
        public float baseBulletSpeed = 20f;
        public float baseMaxHealth = 100f;
        public float baseHealthRegenRate = 1f;
        public float baseRotationSpeed = 180f;
        public float baseGemCapacity = 100f;
        public float basePeopleCapacity = 10f;

        [Header("Mining Stats")]
        public float baseMiningRate = 10f;
        public float miningMultiplier = 1f;

        [Header("Visual")]
        public Sprite shipSprite;
        public GameObject shipPrefab;
        public Color shipColor = Color.white;
    }
}
