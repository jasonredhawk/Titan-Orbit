using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>Ten-stat power breakdown for ship upgrade tree UI bars.</summary>
    [Serializable]
    public struct ShipFamilyPowerScoreBreakdown
    {
        public const int DisplayStatCount = 10;

        public float offense;
        public float defense;
        public float energy;
        public float mobility;
        public float capacity;
        public float firePower;
        public float bulletSpeed;
        public float fireRate;
        public float rammingPower;
        public float healthCap;
        public float healthRegen;
        public float energyCap;
        public float energyRegen;
        public float moveSpeed;
        public float turnSpeed;
        public float gemCap;
        public float peopleCap;

        public float Total => offense + defense + energy + mobility + capacity;

        public float DisplayTotal =>
            firePower + bulletSpeed + healthCap + healthRegen + energyCap + energyRegen +
            moveSpeed + turnSpeed + gemCap + peopleCap;

        public bool HasDisplayStats => DisplayTotal > 0.01f;

        public float GetDisplayTotalForUi() => HasDisplayStats ? DisplayTotal : Total;

        public float GetDisplayStatValue(int statIndex)
        {
            if (HasDisplayStats)
            {
                switch (statIndex)
                {
                    case 0: return firePower;
                    case 1: return bulletSpeed;
                    case 2: return healthCap;
                    case 3: return healthRegen;
                    case 4: return energyCap;
                    case 5: return energyRegen;
                    case 6: return moveSpeed;
                    case 7: return turnSpeed;
                    case 8: return gemCap;
                    case 9: return peopleCap;
                }

                return 0f;
            }

            const float halfCategory = 0.5f;
            switch (statIndex)
            {
                case 0:
                case 1: return offense * halfCategory;
                case 2:
                case 3: return defense * halfCategory;
                case 4:
                case 5: return energy * halfCategory;
                case 6:
                case 7: return mobility * halfCategory;
                case 8:
                case 9: return capacity * halfCategory;
                default: return 0f;
            }
        }

        public static int GetPurchaseGemCost(ShipFamilyChassisTierEntry tier, int shipLevel)
        {
            if (tier == null)
                return 0;
            float baseCap = tier.powerScoreBreakdown.gemCap > 0.01f
                ? tier.powerScoreBreakdown.gemCap
                : 50f + shipLevel * 25f;
            return Mathf.RoundToInt(2f * Mathf.Max(0f, baseCap));
        }

        public static ShipFamilyPowerScoreBreakdown FromSummedShipStats(ShipComponentAbilityStats s)
        {
            return new ShipFamilyPowerScoreBreakdown
            {
                firePower = s.firePower,
                bulletSpeed = s.bulletSpeed,
                fireRate = s.fireRate,
                rammingPower = s.rammingPower,
                healthCap = s.healthCap,
                healthRegen = s.healthRegen,
                energyCap = s.energyCap,
                energyRegen = s.energyRegen,
                moveSpeed = s.moveSpeed,
                turnSpeed = s.turnSpeed,
                gemCap = s.maxGems,
                peopleCap = s.maxPeople
            };
        }
    }
}
