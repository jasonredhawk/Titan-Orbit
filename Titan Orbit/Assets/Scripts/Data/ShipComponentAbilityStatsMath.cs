using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Pure math helpers for <see cref="ShipComponentAbilityStats"/> — addition, zero checks, fallback fill,
    /// transform-based scaling, weapon projectile-speed / range aggregation (max, not sum), and component-id
    /// classification (weapon vs engine vs thruster). Fire power / fire rate stay as field-wise sums
    /// for power-score totals; live bullets use per-mount stats instead. No Unity scene access; safe
    /// to call from editor tooling and runtime stat pipelines.
    /// </summary>
    public static class ShipComponentAbilityStatsMath
    {
        /// <summary>Field-wise sum of two stat blocks.</summary>
        public static ShipComponentAbilityStats Add(ShipComponentAbilityStats a, ShipComponentAbilityStats b)
        {
            // --- Add ---
            // [TITAN-ORBIT] firePower / fireRate are summed for hull power-score capacity.
            // Per-bullet damage and per-barrel cadence come from ShipWeaponMountElement, not this total.
            // bulletSpeed / bulletRange are corrected by ApplyWeaponProjectile* (max, not sum).
            return new ShipComponentAbilityStats
            {
                firePower = a.firePower + b.firePower,
                firePowerPerAbilityLevel = a.firePowerPerAbilityLevel + b.firePowerPerAbilityLevel,
                bulletSpeed = a.bulletSpeed + b.bulletSpeed,
                bulletSpeedPerAbilityLevel = a.bulletSpeedPerAbilityLevel + b.bulletSpeedPerAbilityLevel,
                bulletRange = a.bulletRange + b.bulletRange,
                bulletRangePerAbilityLevel = a.bulletRangePerAbilityLevel + b.bulletRangePerAbilityLevel,
                fireRate = a.fireRate + b.fireRate,
                fireRatePerAbilityLevel = a.fireRatePerAbilityLevel + b.fireRatePerAbilityLevel,
                rammingPower = a.rammingPower + b.rammingPower,
                rammingPowerPerAbilityLevel = a.rammingPowerPerAbilityLevel + b.rammingPowerPerAbilityLevel,
                healthCap = a.healthCap + b.healthCap,
                healthCapPerAbilityLevel = a.healthCapPerAbilityLevel + b.healthCapPerAbilityLevel,
                healthRegen = a.healthRegen + b.healthRegen,
                healthRegenPerAbilityLevel = a.healthRegenPerAbilityLevel + b.healthRegenPerAbilityLevel,
                energyCap = a.energyCap + b.energyCap,
                energyCapPerAbilityLevel = a.energyCapPerAbilityLevel + b.energyCapPerAbilityLevel,
                energyRegen = a.energyRegen + b.energyRegen,
                energyRegenPerAbilityLevel = a.energyRegenPerAbilityLevel + b.energyRegenPerAbilityLevel,
                moveSpeed = a.moveSpeed + b.moveSpeed,
                moveSpeedPerAbilityLevel = a.moveSpeedPerAbilityLevel + b.moveSpeedPerAbilityLevel,
                accelerationCap = a.accelerationCap + b.accelerationCap,
                accelerationCapPerAbilityLevel = a.accelerationCapPerAbilityLevel + b.accelerationCapPerAbilityLevel,
                // [TITAN-ORBIT] OVERDRIVE speed fraction — max across parts (do not sum 0.75+0.75).
                extraSpeedPercent = Mathf.Max(a.extraSpeedPercent, b.extraSpeedPercent),
                extraSpeedPercentPerAbilityLevel = Mathf.Max(a.extraSpeedPercentPerAbilityLevel, b.extraSpeedPercentPerAbilityLevel),
                // [TITAN-ORBIT] Absolute OD drain — sum engines (matches ResolveOverdriveFromEngines).
                extraSpeedEnergyDrain = a.extraSpeedEnergyDrain + b.extraSpeedEnergyDrain,
                extraSpeedEnergyDrainPerAbilityLevel =
                    a.extraSpeedEnergyDrainPerAbilityLevel + b.extraSpeedEnergyDrainPerAbilityLevel,
                turnSpeed = a.turnSpeed + b.turnSpeed,
                turnSpeedPerAbilityLevel = a.turnSpeedPerAbilityLevel + b.turnSpeedPerAbilityLevel,
                maxGems = a.maxGems + b.maxGems,
                maxGemsPerAbilityLevel = a.maxGemsPerAbilityLevel + b.maxGemsPerAbilityLevel,
                tractorBeamDistance = a.tractorBeamDistance + b.tractorBeamDistance,
                tractorBeamDistancePerAbilityLevel = a.tractorBeamDistancePerAbilityLevel + b.tractorBeamDistancePerAbilityLevel,
                tractorBeamPower = a.tractorBeamPower + b.tractorBeamPower,
                tractorBeamPowerPerAbilityLevel = a.tractorBeamPowerPerAbilityLevel + b.tractorBeamPowerPerAbilityLevel,
                maxPeople = a.maxPeople + b.maxPeople,
                maxPeoplePerAbilityLevel = a.maxPeoplePerAbilityLevel + b.maxPeoplePerAbilityLevel,
                // [TITAN-ORBIT] Weight is per-part authoring — do not sum into hull totals.
                extraStackWeight = 0f,
            };
        }

        public static void AddInPlace(ref ShipComponentAbilityStats target, ShipComponentAbilityStats other)
        {
            target = Add(target, other);
        }

        /// <summary>
        /// Replaces naively summed weapon <c>bulletSpeed</c> with the <b>max</b> across weapon parts.
        /// <para>
        /// [TITAN-ORBIT] Bullet speed is a per-projectile property (legacy <c>WeaponConfig</c> ~12–20).
        /// Field-wise <see cref="Add"/> turns a 6-gun hull into 6× speed (e.g. 72) — top-tier free
        /// ships then shoot lasers. Projectile speed uses max (fastest barrel). Player speed growth
        /// is attribute upgrades / Shard cards only (<see cref="ShipComponentStoreData.GetEffectiveStatsAtShipLevel"/>
        /// also skips <c>bulletSpeedPerAbilityLevel</c> for chassis leveling).
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
                total.bulletSpeedPerAbilityLevel -= s.bulletSpeedPerAbilityLevel;
                maxSpeed = Mathf.Max(maxSpeed, s.bulletSpeed);
                maxSpeedPerLevel = Mathf.Max(maxSpeedPerLevel, s.bulletSpeedPerAbilityLevel);
                anyWeapon = true;
            }

            if (!anyWeapon)
                return total;

            // --- One projectile speed for the hull (fastest barrel), not N× sum ---
            total.bulletSpeed = Mathf.Max(0f, total.bulletSpeed) + maxSpeed;
            total.bulletSpeedPerAbilityLevel = Mathf.Max(0f, total.bulletSpeedPerAbilityLevel) + maxSpeedPerLevel;
            return total;
        }

        /// <summary>
        /// Replaces naively summed weapon <c>bulletRange</c> with the <b>max</b> across weapon parts.
        /// <para>
        /// [TITAN-ORBIT] Bullet range is a per-projectile property (same as speed — one travel
        /// distance for the hull, not N× guns). Unlike fire power, range is <b>not</b> a bottom-bar
        /// attribute; it grows with ship level via <c>bulletRangePerAbilityLevel</c> and family
        /// <c>bulletRangeMul</c>. Writes into <c>ShipWeaponConfig.BulletMaxDistance</c> at apply time.
        /// </para>
        /// Call after summing scaled component stats (same slot as projectile-speed aggregation).
        /// </summary>
        public static ShipComponentAbilityStats ApplyWeaponBulletRangeToSummedStats(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            if (componentIds == null || perComponentStats == null)
                return total;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count == 0)
                return total;

            float maxRange = 0f;
            float maxRangePerLevel = 0f;
            bool anyWeapon = false;

            // --- Peel weapon contributions out of the naive sum ---
            for (int i = 0; i < count; i++)
            {
                if (!IsWeaponComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats s = perComponentStats[i];
                total.bulletRange -= s.bulletRange;
                total.bulletRangePerAbilityLevel -= s.bulletRangePerAbilityLevel;
                maxRange = Mathf.Max(maxRange, s.bulletRange);
                maxRangePerLevel = Mathf.Max(maxRangePerLevel, s.bulletRangePerAbilityLevel);
                anyWeapon = true;
            }

            if (!anyWeapon)
                return total;

            // --- One projectile range for the hull (longest barrel), not N× sum ---
            total.bulletRange = Mathf.Max(0f, total.bulletRange) + maxRange;
            total.bulletRangePerAbilityLevel = Mathf.Max(0f, total.bulletRangePerAbilityLevel) + maxRangePerLevel;
            return total;
        }

        /// <summary>
        /// Keeps naively summed weapon <c>firePower</c> (total across barrels).
        /// <para>
        /// [TITAN-ORBIT] Live bullets use <b>per-mount</b> firePower from
        /// <c>ShipWeaponMountCombatLogic</c> — not this hull total. The summed value is for power
        /// score / UI “how much gun does this hull carry.” Older code averaged here so a single
        /// shared <c>ShipWeaponConfig.BulletDamage</c> would not N×; that is obsolete.
        /// </para>
        /// Pair with <see cref="ApplyWeaponFireRateToSummedStats"/> so hull
        /// <c>firePower × fireRate</c> equals true sustained DPS (sum of per-barrel products).
        /// </summary>
        public static ShipComponentAbilityStats ApplyWeaponFirePowerToSummedStats(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            // --- Keep sum (per-bullet damage lives on each ShipWeaponMountElement) ---
            _ = componentIds;
            _ = perComponentStats;
            return total;
        }

        /// <summary>
        /// Rewrites hull <c>fireRate</c> so <c>firePower × fireRate</c> equals the sum of
        /// per-weapon <c>firePower × fireRate</c> (true sustained DPS / energy drain).
        /// <para>
        /// [TITAN-ORBIT] Live fire uses <b>per-mount</b> cadence from
        /// <c>ShipWeaponMountCombatLogic</c>. Naively summing fireRate across a Machinegun +
        /// Missile made <c>(Σ FP) × (Σ FR)</c> explode (e.g. 13 × 7.8 ≈ 100 DPS when real
        /// combined fire is ≈ 21). Energy complementarity, mid-rock TTK, and overgunned
        /// outliers all read the hull product — so the product must match Σ(FPᵢ×FRᵢ).
        /// </para>
        /// Hull <c>firePower</c> stays the barrel sum (gun “size” for power score); effective
        /// fireRate becomes <c>trueDps / firePower</c>.
        /// </summary>
        /// <param name="total">Aggregated hull stats after stack pools (weapon FP already summed).</param>
        /// <param name="componentIds">Parallel matched part ids (post weapon-id collapse).</param>
        /// <param name="perComponentStats">Parallel scaled per-part stats.</param>
        /// <returns>Hull stats with fireRate fitted to true sustained DPS.</returns>
        public static ShipComponentAbilityStats ApplyWeaponFireRateToSummedStats(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            // --- Guard ---
            // [STANDARD] Missing lists → leave total unchanged (caller may be mid-refactor).
            if (componentIds == null || perComponentStats == null)
                return total;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            if (count == 0)
                return total;

            // --- True sustained DPS = sum of per-barrel products ---
            // [TITAN-ORBIT] Each mount fires independently; drain/DPS add, they do not cross-multiply.
            float trueDps = 0f;
            bool anyWeapon = false;
            for (int i = 0; i < count; i++)
            {
                if (!IsWeaponComponent(componentIds[i]))
                    continue;

                ShipComponentAbilityStats s = perComponentStats[i];
                float fp = Mathf.Max(0f, s.firePower);
                float fr = Mathf.Max(0f, s.fireRate);
                trueDps += fp * fr;
                anyWeapon = true;
            }

            if (!anyWeapon || trueDps <= 0.0001f)
                return total;

            // --- Fit effective fireRate so hull FP × FR == trueDps ---
            // [TITAN-ORBIT] firePower already equals Σ barrel FP from stack aggregation.
            float hullFirePower = Mathf.Max(0f, total.firePower);
            if (hullFirePower <= 0.0001f)
            {
                // No summed FP (odd catalog) — store DPS on firePower with FR=1.
                total.firePower = trueDps;
                total.fireRate = 1f;
                total.fireRatePerAbilityLevel = 0f;
                return total;
            }

            total.fireRate = trueDps / hullFirePower;
            // Per-level fireRate stays 0 for weapons by design; clear any leaked sum.
            total.fireRatePerAbilityLevel = 0f;
            return total;
        }

        /// <summary>
        /// Sustained damage-per-second across weapon parts: Σ(<c>firePower × fireRate</c>).
        /// Use this instead of multiplying hull sums when you only have the per-part lists.
        /// </summary>
        /// <param name="componentIds">Matched part ids.</param>
        /// <param name="perComponentStats">Scaled per-part stats (same order).</param>
        /// <returns>Non-negative sustained DPS.</returns>
        public static float ComputeSustainedWeaponDps(
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            if (componentIds == null || perComponentStats == null)
                return 0f;

            int count = Mathf.Min(componentIds.Count, perComponentStats.Count);
            float dps = 0f;
            for (int i = 0; i < count; i++)
            {
                if (!IsWeaponComponent(componentIds[i]))
                    continue;
                ShipComponentAbilityStats s = perComponentStats[i];
                dps += Mathf.Max(0f, s.firePower) * Mathf.Max(0f, s.fireRate);
            }

            return dps;
        }

        /// <summary>True when every base and per-level field is exactly zero.</summary>
        public static bool IsAllZero(in ShipComponentAbilityStats s)
        {
            // --- IsAllZero ---
            return s.firePower == 0f && s.firePowerPerAbilityLevel == 0f &&
                   s.bulletSpeed == 0f && s.bulletSpeedPerAbilityLevel == 0f &&
                   s.bulletRange == 0f && s.bulletRangePerAbilityLevel == 0f &&
                   s.fireRate == 0f && s.fireRatePerAbilityLevel == 0f &&
                   s.rammingPower == 0f && s.rammingPowerPerAbilityLevel == 0f &&
                   s.healthCap == 0f && s.healthCapPerAbilityLevel == 0f &&
                   s.healthRegen == 0f && s.healthRegenPerAbilityLevel == 0f &&
                   s.energyCap == 0f && s.energyCapPerAbilityLevel == 0f &&
                   s.energyRegen == 0f && s.energyRegenPerAbilityLevel == 0f &&
                   s.moveSpeed == 0f && s.moveSpeedPerAbilityLevel == 0f &&
                   s.accelerationCap == 0f && s.accelerationCapPerAbilityLevel == 0f &&
                   s.extraSpeedPercent == 0f && s.extraSpeedPercentPerAbilityLevel == 0f &&
                   s.extraSpeedEnergyDrain == 0f && s.extraSpeedEnergyDrainPerAbilityLevel == 0f &&
                   s.turnSpeed == 0f && s.turnSpeedPerAbilityLevel == 0f &&
                   s.maxGems == 0f && s.maxGemsPerAbilityLevel == 0f &&
                   s.tractorBeamDistance == 0f && s.tractorBeamDistancePerAbilityLevel == 0f &&
                   s.tractorBeamPower == 0f && s.tractorBeamPowerPerAbilityLevel == 0f &&
                   s.maxPeople == 0f && s.maxPeoplePerAbilityLevel == 0f;
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
            if (result.firePowerPerAbilityLevel == 0f) result.firePowerPerAbilityLevel = defaults.firePowerPerAbilityLevel;
            if (result.bulletSpeed == 0f) result.bulletSpeed = defaults.bulletSpeed;
            if (result.bulletSpeedPerAbilityLevel == 0f) result.bulletSpeedPerAbilityLevel = defaults.bulletSpeedPerAbilityLevel;
            if (result.bulletRange == 0f) result.bulletRange = defaults.bulletRange;
            if (result.bulletRangePerAbilityLevel == 0f) result.bulletRangePerAbilityLevel = defaults.bulletRangePerAbilityLevel;
            if (result.fireRate == 0f) result.fireRate = defaults.fireRate;
            if (result.fireRatePerAbilityLevel == 0f) result.fireRatePerAbilityLevel = defaults.fireRatePerAbilityLevel;
            if (result.rammingPower == 0f) result.rammingPower = defaults.rammingPower;
            if (result.rammingPowerPerAbilityLevel == 0f) result.rammingPowerPerAbilityLevel = defaults.rammingPowerPerAbilityLevel;
            if (result.healthCap == 0f) result.healthCap = defaults.healthCap;
            if (result.healthCapPerAbilityLevel == 0f) result.healthCapPerAbilityLevel = defaults.healthCapPerAbilityLevel;
            if (result.healthRegen == 0f) result.healthRegen = defaults.healthRegen;
            if (result.healthRegenPerAbilityLevel == 0f) result.healthRegenPerAbilityLevel = defaults.healthRegenPerAbilityLevel;
            if (result.energyCap == 0f) result.energyCap = defaults.energyCap;
            if (result.energyCapPerAbilityLevel == 0f) result.energyCapPerAbilityLevel = defaults.energyCapPerAbilityLevel;
            if (result.energyRegen == 0f) result.energyRegen = defaults.energyRegen;
            if (result.energyRegenPerAbilityLevel == 0f) result.energyRegenPerAbilityLevel = defaults.energyRegenPerAbilityLevel;
            if (result.moveSpeed == 0f) result.moveSpeed = defaults.moveSpeed;
            if (result.moveSpeedPerAbilityLevel == 0f) result.moveSpeedPerAbilityLevel = defaults.moveSpeedPerAbilityLevel;
            if (result.accelerationCap == 0f) result.accelerationCap = defaults.accelerationCap;
            if (result.accelerationCapPerAbilityLevel == 0f) result.accelerationCapPerAbilityLevel = defaults.accelerationCapPerAbilityLevel;
            if (result.extraSpeedPercent == 0f) result.extraSpeedPercent = defaults.extraSpeedPercent;
            if (result.extraSpeedPercentPerAbilityLevel == 0f)
                result.extraSpeedPercentPerAbilityLevel = defaults.extraSpeedPercentPerAbilityLevel;
            if (result.extraSpeedEnergyDrain == 0f)
                result.extraSpeedEnergyDrain = defaults.extraSpeedEnergyDrain;
            if (result.extraSpeedEnergyDrainPerAbilityLevel == 0f)
                result.extraSpeedEnergyDrainPerAbilityLevel = defaults.extraSpeedEnergyDrainPerAbilityLevel;
            if (result.turnSpeed == 0f) result.turnSpeed = defaults.turnSpeed;
            if (result.turnSpeedPerAbilityLevel == 0f) result.turnSpeedPerAbilityLevel = defaults.turnSpeedPerAbilityLevel;
            if (result.maxGems == 0f) result.maxGems = defaults.maxGems;
            if (result.maxGemsPerAbilityLevel == 0f) result.maxGemsPerAbilityLevel = defaults.maxGemsPerAbilityLevel;
            if (result.tractorBeamDistance == 0f) result.tractorBeamDistance = defaults.tractorBeamDistance;
            if (result.tractorBeamDistancePerAbilityLevel == 0f) result.tractorBeamDistancePerAbilityLevel = defaults.tractorBeamDistancePerAbilityLevel;
            if (result.tractorBeamPower == 0f) result.tractorBeamPower = defaults.tractorBeamPower;
            if (result.tractorBeamPowerPerAbilityLevel == 0f) result.tractorBeamPowerPerAbilityLevel = defaults.tractorBeamPowerPerAbilityLevel;
            if (result.maxPeople == 0f) result.maxPeople = defaults.maxPeople;
            if (result.maxPeoplePerAbilityLevel == 0f) result.maxPeoplePerAbilityLevel = defaults.maxPeoplePerAbilityLevel;
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
            if (ContainsIsolatedKeyword(id, "weapon")
                || ContainsIsolatedKeyword(id, "gun")
                || ContainsIsolatedKeyword(id, "cannon")
                || ContainsIsolatedKeyword(id, "missile"))
                return true;
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            return ShipFamilyPartTypes.IsWeapon(partType);
        }

        /// <summary>
        /// [TITAN-ORBIT] Maneuver jets: name contains Thruster or Exhaust.
        /// Author turn + move/accel; set thrust energy drain. Do not own Energy Cap/Regen.
        /// </summary>
        public static bool IsThrusterComponent(string componentId)
        {
            // --- IsThrusterComponent ---
            return ShipFamilyPartTypes.IsThrusterLikeName(componentId);
        }

        /// <summary>
        /// [TITAN-ORBIT] Power plants: propulsion mounts that are not thruster-like.
        /// Author Energy Cap/Regen (cumulative) + move/accel; no turn.
        /// </summary>
        public static bool IsEngineComponent(string componentId)
        {
            // --- IsEngineComponent ---
            return ShipFamilyPartTypes.IsEngineLikeName(componentId);
        }

        public static bool IsPropulsionComponent(string componentId)
        {
            // --- IsPropulsionComponent ---
            if (IsThrusterComponent(componentId) || IsEngineComponent(componentId))
                return true;
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            return ShipFamilyPartTypes.IsPropulsion(partType);
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
        /// Soft floor / ceiling for weapon mesh → fire-power scale.
        /// Stops mirrored negative axes and tiny art scales from inventing god-tier DPS.
        /// </summary>
        public const float WeaponFirePowerScaleMin = 0.35f;

        /// <summary>Soft ceiling for weapon mesh → fire-power scale (huge art meshes stay readable).</summary>
        public const float WeaponFirePowerScaleMax = 2.5f;

        /// <summary>
        /// Soft floor for weapon mesh → fire-rate scale (<c>1 / |z|</c>).
        /// Tiny Z used to explode cadence (e.g. z=0.01 → 100× fireRate).
        /// </summary>
        public const float WeaponFireRateScaleMin = 0.4f;

        /// <summary>Soft ceiling for weapon mesh → fire-rate scale.</summary>
        public const float WeaponFireRateScaleMax = 2.5f;

        /// <summary>
        /// Scales authored stats by prefab child transform size. Weapons: XY → fire power, Z → fire rate.
        /// Propulsion move/accel ignore scale; turn and ramming are never scaled.
        /// <para>
        /// [TITAN-ORBIT] Call only with <b>chassis prefab</b> authored localScale (art lever for
        /// mixed calibers on one hull). Do not pass live hybrid proxies after attribute mesh grow —
        /// combat already applies Fire Power attributes as numeric multipliers (mesh/collider grow
        /// is separate from firePower / fireRate).
        /// </para>
        /// <para>
        /// [TITAN-ORBIT] Scales use absolute local axes and are clamped — USC mirrors (negative X)
        /// and needle-thin Z must not invert damage or create machinegun outliers.
        /// </para>
        /// </summary>
        public static ShipComponentAbilityStats ScaleStatsByTransform(
            ShipComponentAbilityStats stats,
            Transform t,
            string componentId)
        {
            if (t == null) return stats;
            // [TITAN-ORBIT] Abs — mirrored weapon/engine children keep positive contribution.
            float x = Mathf.Abs(t.localScale.x);
            float y = Mathf.Abs(t.localScale.y);
            float z = Mathf.Max(Mathf.Abs(t.localScale.z), 0.01f);

            if (IsWeaponComponent(componentId))
            {
                // [TITAN-ORBIT] Wider weapon mesh → more fire power; deeper (Z) mesh → slower fire rate.
                // Written onto each ShipWeaponMountElement for independent barrels.
                float firePowerScale = Mathf.Clamp((x + y) * 0.5f, WeaponFirePowerScaleMin, WeaponFirePowerScaleMax);
                float fireRateScale = Mathf.Clamp(1f / z, WeaponFireRateScaleMin, WeaponFireRateScaleMax);
                return new ShipComponentAbilityStats
                {
                    firePower = stats.firePower * firePowerScale,
                    firePowerPerAbilityLevel = stats.firePowerPerAbilityLevel * firePowerScale,
                    bulletSpeed = stats.bulletSpeed,
                    bulletSpeedPerAbilityLevel = stats.bulletSpeedPerAbilityLevel,
                    // [TITAN-ORBIT] Range is hull-level (like speed) — not scaled by mesh size.
                    bulletRange = stats.bulletRange,
                    bulletRangePerAbilityLevel = stats.bulletRangePerAbilityLevel,
                    fireRate = stats.fireRate * fireRateScale,
                    fireRatePerAbilityLevel = stats.fireRatePerAbilityLevel * fireRateScale,
                    rammingPower = stats.rammingPower,
                    rammingPowerPerAbilityLevel = stats.rammingPowerPerAbilityLevel,
                    healthCap = stats.healthCap,
                    healthCapPerAbilityLevel = stats.healthCapPerAbilityLevel,
                    healthRegen = stats.healthRegen,
                    healthRegenPerAbilityLevel = stats.healthRegenPerAbilityLevel,
                    energyCap = stats.energyCap,
                    energyCapPerAbilityLevel = stats.energyCapPerAbilityLevel,
                    energyRegen = stats.energyRegen,
                    energyRegenPerAbilityLevel = stats.energyRegenPerAbilityLevel,
                    moveSpeed = stats.moveSpeed,
                    moveSpeedPerAbilityLevel = stats.moveSpeedPerAbilityLevel,
                    accelerationCap = stats.accelerationCap,
                    accelerationCapPerAbilityLevel = stats.accelerationCapPerAbilityLevel,
                    extraSpeedPercent = stats.extraSpeedPercent,
                    extraSpeedPercentPerAbilityLevel = stats.extraSpeedPercentPerAbilityLevel,
                    extraSpeedEnergyDrain = stats.extraSpeedEnergyDrain,
                    extraSpeedEnergyDrainPerAbilityLevel = stats.extraSpeedEnergyDrainPerAbilityLevel,
                    turnSpeed = stats.turnSpeed,
                    turnSpeedPerAbilityLevel = stats.turnSpeedPerAbilityLevel,
                    maxGems = stats.maxGems,
                    maxGemsPerAbilityLevel = stats.maxGemsPerAbilityLevel,
                    tractorBeamDistance = stats.tractorBeamDistance,
                    tractorBeamDistancePerAbilityLevel = stats.tractorBeamDistancePerAbilityLevel,
                    tractorBeamPower = stats.tractorBeamPower,
                    tractorBeamPowerPerAbilityLevel = stats.tractorBeamPowerPerAbilityLevel,
                    maxPeople = stats.maxPeople,
                    maxPeoplePerAbilityLevel = stats.maxPeoplePerAbilityLevel,
                    extraStackWeight = stats.extraStackWeight,
                };
            }

            float scale = (x + y + z) / 3f;
            var scaled = Multiply(stats, scale);
            scaled.turnSpeed = stats.turnSpeed;
            scaled.turnSpeedPerAbilityLevel = stats.turnSpeedPerAbilityLevel;
            scaled.rammingPower = stats.rammingPower;
            scaled.rammingPowerPerAbilityLevel = stats.rammingPowerPerAbilityLevel;
            scaled.extraStackWeight = stats.extraStackWeight;
            if (IsPropulsionComponent(componentId))
            {
                scaled.moveSpeed = stats.moveSpeed;
                scaled.moveSpeedPerAbilityLevel = stats.moveSpeedPerAbilityLevel;
                scaled.accelerationCap = stats.accelerationCap;
                scaled.accelerationCapPerAbilityLevel = stats.accelerationCapPerAbilityLevel;
                // OVERDRIVE fractions are designer knobs — not mesh-scale dependent.
                scaled.extraSpeedPercent = stats.extraSpeedPercent;
                scaled.extraSpeedPercentPerAbilityLevel = stats.extraSpeedPercentPerAbilityLevel;
                scaled.extraSpeedEnergyDrain = stats.extraSpeedEnergyDrain;
                scaled.extraSpeedEnergyDrainPerAbilityLevel = stats.extraSpeedEnergyDrainPerAbilityLevel;
            }
            return scaled;
        }

        /// <summary>Multiplies every <b>numeric</b> stat field by <paramref name="factor"/> (not extraStackWeight).</summary>
        public static ShipComponentAbilityStats Multiply(ShipComponentAbilityStats s, float factor)
        {
            // --- Multiply ---
            return new ShipComponentAbilityStats
            {
                firePower = s.firePower * factor,
                firePowerPerAbilityLevel = s.firePowerPerAbilityLevel * factor,
                bulletSpeed = s.bulletSpeed * factor,
                bulletSpeedPerAbilityLevel = s.bulletSpeedPerAbilityLevel * factor,
                bulletRange = s.bulletRange * factor,
                bulletRangePerAbilityLevel = s.bulletRangePerAbilityLevel * factor,
                fireRate = s.fireRate * factor,
                fireRatePerAbilityLevel = s.fireRatePerAbilityLevel * factor,
                rammingPower = s.rammingPower * factor,
                rammingPowerPerAbilityLevel = s.rammingPowerPerAbilityLevel * factor,
                healthCap = s.healthCap * factor,
                healthCapPerAbilityLevel = s.healthCapPerAbilityLevel * factor,
                healthRegen = s.healthRegen * factor,
                healthRegenPerAbilityLevel = s.healthRegenPerAbilityLevel * factor,
                energyCap = s.energyCap * factor,
                energyCapPerAbilityLevel = s.energyCapPerAbilityLevel * factor,
                energyRegen = s.energyRegen * factor,
                energyRegenPerAbilityLevel = s.energyRegenPerAbilityLevel * factor,
                moveSpeed = s.moveSpeed * factor,
                moveSpeedPerAbilityLevel = s.moveSpeedPerAbilityLevel * factor,
                accelerationCap = s.accelerationCap * factor,
                accelerationCapPerAbilityLevel = s.accelerationCapPerAbilityLevel * factor,
                extraSpeedPercent = s.extraSpeedPercent * factor,
                extraSpeedPercentPerAbilityLevel = s.extraSpeedPercentPerAbilityLevel * factor,
                extraSpeedEnergyDrain = s.extraSpeedEnergyDrain * factor,
                extraSpeedEnergyDrainPerAbilityLevel = s.extraSpeedEnergyDrainPerAbilityLevel * factor,
                turnSpeed = s.turnSpeed * factor,
                turnSpeedPerAbilityLevel = s.turnSpeedPerAbilityLevel * factor,
                maxGems = s.maxGems * factor,
                maxGemsPerAbilityLevel = s.maxGemsPerAbilityLevel * factor,
                tractorBeamDistance = s.tractorBeamDistance * factor,
                tractorBeamDistancePerAbilityLevel = s.tractorBeamDistancePerAbilityLevel * factor,
                tractorBeamPower = s.tractorBeamPower * factor,
                tractorBeamPowerPerAbilityLevel = s.tractorBeamPowerPerAbilityLevel * factor,
                maxPeople = s.maxPeople * factor,
                maxPeoplePerAbilityLevel = s.maxPeoplePerAbilityLevel * factor,
                // [TITAN-ORBIT] Stack weight is a fraction, not a scaled ability number.
                extraStackWeight = s.extraStackWeight,
            };
        }
    }
}
