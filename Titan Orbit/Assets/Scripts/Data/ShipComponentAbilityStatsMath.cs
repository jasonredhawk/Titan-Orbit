using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Pure math helpers for <see cref="ShipComponentAbilityStats"/> — addition, zero checks, fallback fill,
    /// transform-based scaling, weapon projectile-speed aggregation (max, not sum), and component-id
    /// classification (weapon vs engine vs thruster). No Unity scene access; safe to call from editor
    /// tooling and runtime stat pipelines.
    /// </summary>
    public static class ShipComponentAbilityStatsMath
    {
        /// <summary>Field-wise sum of two stat blocks.</summary>
        public static ShipComponentAbilityStats Add(ShipComponentAbilityStats a, ShipComponentAbilityStats b)
        {
            // --- Add ---
            return new ShipComponentAbilityStats
            {
                firePower = a.firePower + b.firePower,
                firePowerPerLevel = a.firePowerPerLevel + b.firePowerPerLevel,
                bulletSpeed = a.bulletSpeed + b.bulletSpeed,
                bulletSpeedPerLevel = a.bulletSpeedPerLevel + b.bulletSpeedPerLevel,
                fireRate = a.fireRate + b.fireRate,
                fireRatePerLevel = a.fireRatePerLevel + b.fireRatePerLevel,
                rammingPower = a.rammingPower + b.rammingPower,
                rammingPowerPerLevel = a.rammingPowerPerLevel + b.rammingPowerPerLevel,
                healthCap = a.healthCap + b.healthCap,
                healthCapPerLevel = a.healthCapPerLevel + b.healthCapPerLevel,
                healthRegen = a.healthRegen + b.healthRegen,
                healthRegenPerLevel = a.healthRegenPerLevel + b.healthRegenPerLevel,
                energyCap = a.energyCap + b.energyCap,
                energyCapPerLevel = a.energyCapPerLevel + b.energyCapPerLevel,
                energyRegen = a.energyRegen + b.energyRegen,
                energyRegenPerLevel = a.energyRegenPerLevel + b.energyRegenPerLevel,
                moveSpeed = a.moveSpeed + b.moveSpeed,
                moveSpeedPerLevel = a.moveSpeedPerLevel + b.moveSpeedPerLevel,
                accelerationCap = a.accelerationCap + b.accelerationCap,
                accelerationCapPerLevel = a.accelerationCapPerLevel + b.accelerationCapPerLevel,
                turnSpeed = a.turnSpeed + b.turnSpeed,
                turnSpeedPerLevel = a.turnSpeedPerLevel + b.turnSpeedPerLevel,
                maxGems = a.maxGems + b.maxGems,
                maxGemsPerLevel = a.maxGemsPerLevel + b.maxGemsPerLevel,
                tractorBeamDistance = a.tractorBeamDistance + b.tractorBeamDistance,
                tractorBeamDistancePerLevel = a.tractorBeamDistancePerLevel + b.tractorBeamDistancePerLevel,
                tractorBeamPower = a.tractorBeamPower + b.tractorBeamPower,
                tractorBeamPowerPerLevel = a.tractorBeamPowerPerLevel + b.tractorBeamPowerPerLevel,
                maxPeople = a.maxPeople + b.maxPeople,
                maxPeoplePerLevel = a.maxPeoplePerLevel + b.maxPeoplePerLevel,
            };
        }

        public static void AddInPlace(ref ShipComponentAbilityStats target, ShipComponentAbilityStats other)
        {
            target = Add(target, other);
        }

        /// <summary>
        /// Replaces naively summed weapon <c>bulletSpeed</c> with the <b>max</b> across weapon parts.
        /// <para>
        /// [TITAN-ORBIT] Bullet speed is a per-projectile property (legacy <c>WeaponConfig</c> ~12–20),
        /// not a pool like firePower. Field-wise <see cref="Add"/> turns a 6-gun hull into 6× speed
        /// (e.g. 72) — top-tier free ships then shoot lasers. Fire power still sums; projectile
        /// speed does not. Player speed growth is attribute upgrades / Shard cards only
        /// (<see cref="ShipComponentStoreData.GetEffectiveStatsAtShipLevel"/> also skips
        /// <c>bulletSpeedPerLevel</c> for chassis leveling).
        /// </para>
        /// Call after summing scaled component stats (same slot as propulsion aggregation).
        /// </summary>
        public static ShipComponentAbilityStats ApplyWeaponProjectileSpeedToSummedStats(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            if (componentIds == null || perComponentStats == null)
                return total;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count == 0)
                return total;

            float maxSpeed = 0f;
            float maxSpeedPerLevel = 0f;
            bool anyWeapon = false;

            // --- Peel weapon contributions out of the naive sum ---
            for (int i = 0; i < count; i++)
            {
                if (!IsWeaponComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats s = perComponentStats[i];
                total.bulletSpeed -= s.bulletSpeed;
                total.bulletSpeedPerLevel -= s.bulletSpeedPerLevel;
                maxSpeed = Mathf.Max(maxSpeed, s.bulletSpeed);
                maxSpeedPerLevel = Mathf.Max(maxSpeedPerLevel, s.bulletSpeedPerLevel);
                anyWeapon = true;
            }

            if (!anyWeapon)
                return total;

            // --- One projectile speed for the hull (fastest barrel), not N× sum ---
            total.bulletSpeed = Mathf.Max(0f, total.bulletSpeed) + maxSpeed;
            total.bulletSpeedPerLevel = Mathf.Max(0f, total.bulletSpeedPerLevel) + maxSpeedPerLevel;
            return total;
        }

        /// <summary>True when every base and per-level field is exactly zero.</summary>
        public static bool IsAllZero(in ShipComponentAbilityStats s)
        {
            // --- IsAllZero ---
            return s.firePower == 0f && s.firePowerPerLevel == 0f &&
                   s.bulletSpeed == 0f && s.bulletSpeedPerLevel == 0f &&
                   s.fireRate == 0f && s.fireRatePerLevel == 0f &&
                   s.rammingPower == 0f && s.rammingPowerPerLevel == 0f &&
                   s.healthCap == 0f && s.healthCapPerLevel == 0f &&
                   s.healthRegen == 0f && s.healthRegenPerLevel == 0f &&
                   s.energyCap == 0f && s.energyCapPerLevel == 0f &&
                   s.energyRegen == 0f && s.energyRegenPerLevel == 0f &&
                   s.moveSpeed == 0f && s.moveSpeedPerLevel == 0f &&
                   s.accelerationCap == 0f && s.accelerationCapPerLevel == 0f &&
                   s.turnSpeed == 0f && s.turnSpeedPerLevel == 0f &&
                   s.maxGems == 0f && s.maxGemsPerLevel == 0f &&
                   s.tractorBeamDistance == 0f && s.tractorBeamDistancePerLevel == 0f &&
                   s.tractorBeamPower == 0f && s.tractorBeamPowerPerLevel == 0f &&
                   s.maxPeople == 0f && s.maxPeoplePerLevel == 0f;
        }

        /// <summary>
        /// Copies <paramref name="defaults"/> into any zero field of <paramref name="stats"/>.
        /// [TITAN-ORBIT] Prevents a missing component entry from zeroing an entire hull stat.
        /// </summary>
        public static ShipComponentAbilityStats WithZeroStatFallbacks(
            in ShipComponentAbilityStats stats,
            in ShipComponentAbilityStats defaults)
        {
            var result = stats;
            if (result.firePower == 0f) result.firePower = defaults.firePower;
            if (result.firePowerPerLevel == 0f) result.firePowerPerLevel = defaults.firePowerPerLevel;
            if (result.bulletSpeed == 0f) result.bulletSpeed = defaults.bulletSpeed;
            if (result.bulletSpeedPerLevel == 0f) result.bulletSpeedPerLevel = defaults.bulletSpeedPerLevel;
            if (result.fireRate == 0f) result.fireRate = defaults.fireRate;
            if (result.fireRatePerLevel == 0f) result.fireRatePerLevel = defaults.fireRatePerLevel;
            if (result.rammingPower == 0f) result.rammingPower = defaults.rammingPower;
            if (result.rammingPowerPerLevel == 0f) result.rammingPowerPerLevel = defaults.rammingPowerPerLevel;
            if (result.healthCap == 0f) result.healthCap = defaults.healthCap;
            if (result.healthCapPerLevel == 0f) result.healthCapPerLevel = defaults.healthCapPerLevel;
            if (result.healthRegen == 0f) result.healthRegen = defaults.healthRegen;
            if (result.healthRegenPerLevel == 0f) result.healthRegenPerLevel = defaults.healthRegenPerLevel;
            if (result.energyCap == 0f) result.energyCap = defaults.energyCap;
            if (result.energyCapPerLevel == 0f) result.energyCapPerLevel = defaults.energyCapPerLevel;
            if (result.energyRegen == 0f) result.energyRegen = defaults.energyRegen;
            if (result.energyRegenPerLevel == 0f) result.energyRegenPerLevel = defaults.energyRegenPerLevel;
            if (result.moveSpeed == 0f) result.moveSpeed = defaults.moveSpeed;
            if (result.moveSpeedPerLevel == 0f) result.moveSpeedPerLevel = defaults.moveSpeedPerLevel;
            if (result.accelerationCap == 0f) result.accelerationCap = defaults.accelerationCap;
            if (result.accelerationCapPerLevel == 0f) result.accelerationCapPerLevel = defaults.accelerationCapPerLevel;
            if (result.turnSpeed == 0f) result.turnSpeed = defaults.turnSpeed;
            if (result.turnSpeedPerLevel == 0f) result.turnSpeedPerLevel = defaults.turnSpeedPerLevel;
            if (result.maxGems == 0f) result.maxGems = defaults.maxGems;
            if (result.maxGemsPerLevel == 0f) result.maxGemsPerLevel = defaults.maxGemsPerLevel;
            if (result.tractorBeamDistance == 0f) result.tractorBeamDistance = defaults.tractorBeamDistance;
            if (result.tractorBeamDistancePerLevel == 0f) result.tractorBeamDistancePerLevel = defaults.tractorBeamDistancePerLevel;
            if (result.tractorBeamPower == 0f) result.tractorBeamPower = defaults.tractorBeamPower;
            if (result.tractorBeamPowerPerLevel == 0f) result.tractorBeamPowerPerLevel = defaults.tractorBeamPowerPerLevel;
            if (result.maxPeople == 0f) result.maxPeople = defaults.maxPeople;
            if (result.maxPeoplePerLevel == 0f) result.maxPeoplePerLevel = defaults.maxPeoplePerLevel;
            return result;
        }

        /// <summary>Average of localScale axes — used as generic size multiplier for non-weapon parts.</summary>
        public static float GetNormalizedScaleFromTransform(Transform t)
        {
            // --- Compute value ---
            if (t == null) return 1f;
            Vector3 s = t.localScale;
            return (s.x + s.y + s.z) / 3f;
        }

        public static bool IsWeaponComponent(string componentId)
        {
            // --- IsWeaponComponent ---
            if (string.IsNullOrEmpty(componentId)) return false;
            string id = componentId.TrimStart();
            if (id.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase)) return true;
            return ContainsIsolatedKeyword(id, "weapon");
        }

        public static bool IsThrusterComponent(string componentId)
        {
            // --- IsThrusterComponent ---
            if (string.IsNullOrEmpty(componentId)) return false;
            string id = componentId.TrimStart();
            if (id.StartsWith("Thruster", StringComparison.OrdinalIgnoreCase)) return true;
            return ContainsIsolatedKeyword(id, "thruster");
        }

        public static bool IsEngineComponent(string componentId)
        {
            // --- IsEngineComponent ---
            if (string.IsNullOrEmpty(componentId)) return false;
            string id = componentId.TrimStart();
            if (IsThrusterComponent(id)) return false;
            if (id.StartsWith("Engine", StringComparison.OrdinalIgnoreCase)) return true;
            return ContainsIsolatedKeyword(id, "engine") || ContainsIsolatedKeyword(id, "thrust");
        }

        public static bool IsPropulsionComponent(string componentId)
        {
            // --- IsPropulsionComponent ---
            if (IsThrusterComponent(componentId) || IsEngineComponent(componentId))
                return true;
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            return string.Equals(partType, "Engine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Thruster", StringComparison.OrdinalIgnoreCase);
        }

        static bool ContainsIsolatedKeyword(string s, string keyword)
        {
            // --- ContainsIsolatedKeyword ---
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(keyword)) return false;
            int idx = 0;
            while ((idx = s.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = idx + keyword.Length;
                bool okBefore = idx == 0 || !char.IsLetter(s[idx - 1]);
                bool okAfter = end >= s.Length || !char.IsLetter(s[end]);
                if (okBefore && okAfter)
                    return true;
                idx++;
            }
            return false;
        }

        /// <summary>
        /// Scales authored stats by prefab child transform size. Weapons: XY → fire power, Z → fire rate.
        /// Propulsion move/accel ignore scale; turn and ramming are never scaled. [TITAN-ORBIT] Art size affects combat stats.
        /// </summary>
        public static ShipComponentAbilityStats ScaleStatsByTransform(
            ShipComponentAbilityStats stats,
            Transform t,
            string componentId)
        {
            if (t == null) return stats;
            float x = t.localScale.x;
            float y = t.localScale.y;
            float z = Mathf.Max(t.localScale.z, 0.01f);

            if (IsWeaponComponent(componentId))
            {
                // [TITAN-ORBIT] Wider weapon mesh → more fire power; deeper (Z) mesh → slower fire rate.
                float firePowerScale = (x + y) * 0.5f;
                float fireRateScale = 1f / z;
                return new ShipComponentAbilityStats
                {
                    firePower = stats.firePower * firePowerScale,
                    firePowerPerLevel = stats.firePowerPerLevel * firePowerScale,
                    bulletSpeed = stats.bulletSpeed,
                    bulletSpeedPerLevel = stats.bulletSpeedPerLevel,
                    fireRate = stats.fireRate * fireRateScale,
                    fireRatePerLevel = stats.fireRatePerLevel * fireRateScale,
                    rammingPower = stats.rammingPower,
                    rammingPowerPerLevel = stats.rammingPowerPerLevel,
                    healthCap = stats.healthCap,
                    healthCapPerLevel = stats.healthCapPerLevel,
                    healthRegen = stats.healthRegen,
                    healthRegenPerLevel = stats.healthRegenPerLevel,
                    energyCap = stats.energyCap,
                    energyCapPerLevel = stats.energyCapPerLevel,
                    energyRegen = stats.energyRegen,
                    energyRegenPerLevel = stats.energyRegenPerLevel,
                    moveSpeed = stats.moveSpeed,
                    moveSpeedPerLevel = stats.moveSpeedPerLevel,
                    accelerationCap = stats.accelerationCap,
                    accelerationCapPerLevel = stats.accelerationCapPerLevel,
                    turnSpeed = stats.turnSpeed,
                    turnSpeedPerLevel = stats.turnSpeedPerLevel,
                    maxGems = stats.maxGems,
                    maxGemsPerLevel = stats.maxGemsPerLevel,
                    tractorBeamDistance = stats.tractorBeamDistance,
                    tractorBeamDistancePerLevel = stats.tractorBeamDistancePerLevel,
                    tractorBeamPower = stats.tractorBeamPower,
                    tractorBeamPowerPerLevel = stats.tractorBeamPowerPerLevel,
                    maxPeople = stats.maxPeople,
                    maxPeoplePerLevel = stats.maxPeoplePerLevel,
                };
            }

            float scale = (x + y + z) / 3f;
            var scaled = Multiply(stats, scale);
            scaled.turnSpeed = stats.turnSpeed;
            scaled.turnSpeedPerLevel = stats.turnSpeedPerLevel;
            scaled.rammingPower = stats.rammingPower;
            scaled.rammingPowerPerLevel = stats.rammingPowerPerLevel;
            if (IsPropulsionComponent(componentId))
            {
                scaled.moveSpeed = stats.moveSpeed;
                scaled.moveSpeedPerLevel = stats.moveSpeedPerLevel;
                scaled.accelerationCap = stats.accelerationCap;
                scaled.accelerationCapPerLevel = stats.accelerationCapPerLevel;
            }
            return scaled;
        }

        /// <summary>Multiplies every stat field by <paramref name="factor"/>.</summary>
        public static ShipComponentAbilityStats Multiply(ShipComponentAbilityStats s, float factor)
        {
            // --- Multiply ---
            return new ShipComponentAbilityStats
            {
                firePower = s.firePower * factor,
                firePowerPerLevel = s.firePowerPerLevel * factor,
                bulletSpeed = s.bulletSpeed * factor,
                bulletSpeedPerLevel = s.bulletSpeedPerLevel * factor,
                fireRate = s.fireRate * factor,
                fireRatePerLevel = s.fireRatePerLevel * factor,
                rammingPower = s.rammingPower * factor,
                rammingPowerPerLevel = s.rammingPowerPerLevel * factor,
                healthCap = s.healthCap * factor,
                healthCapPerLevel = s.healthCapPerLevel * factor,
                healthRegen = s.healthRegen * factor,
                healthRegenPerLevel = s.healthRegenPerLevel * factor,
                energyCap = s.energyCap * factor,
                energyCapPerLevel = s.energyCapPerLevel * factor,
                energyRegen = s.energyRegen * factor,
                energyRegenPerLevel = s.energyRegenPerLevel * factor,
                moveSpeed = s.moveSpeed * factor,
                moveSpeedPerLevel = s.moveSpeedPerLevel * factor,
                accelerationCap = s.accelerationCap * factor,
                accelerationCapPerLevel = s.accelerationCapPerLevel * factor,
                turnSpeed = s.turnSpeed * factor,
                turnSpeedPerLevel = s.turnSpeedPerLevel * factor,
                maxGems = s.maxGems * factor,
                maxGemsPerLevel = s.maxGemsPerLevel * factor,
                tractorBeamDistance = s.tractorBeamDistance * factor,
                tractorBeamDistancePerLevel = s.tractorBeamDistancePerLevel * factor,
                tractorBeamPower = s.tractorBeamPower * factor,
                tractorBeamPowerPerLevel = s.tractorBeamPowerPerLevel * factor,
                maxPeople = s.maxPeople * factor,
                maxPeoplePerLevel = s.maxPeoplePerLevel * factor,
            };
        }
    }
}
