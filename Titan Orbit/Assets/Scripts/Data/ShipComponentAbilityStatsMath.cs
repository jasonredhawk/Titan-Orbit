using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Which Base / PerExtra pair <see cref="ShipComponentAbilityStatsMath.GetScaleMultiplier"/> answers for.
    /// Mirrors the branches inside <see cref="ShipComponentAbilityStatsMath.ScaleStatsByTransform"/> so the
    /// ability details card can show <c>catalog × starting scale</c> with the same factor the motor applied.
    /// </summary>
    public enum ShipComponentScaleChannel
    {
        /// <summary>Weapon: average |X|+|Y|; non-weapon: average localScale.</summary>
        FirePower = 0,
        /// <summary>Weapon: 1/|Z| (2 is half rate); non-weapon: average localScale.</summary>
        FireRate = 1,
        /// <summary>Catalog speed on a weapon (prefab scale does not change it). Non-weapons still use average localScale.</summary>
        BulletSpeed = 2,
        /// <summary>Catalog range on a weapon (prefab scale does not change travel distance). Non-weapons still use average localScale.</summary>
        BulletRange = 3,
        /// <summary>Average localScale (cockpit / hull health fields).</summary>
        Health = 4,
        /// <summary>Average localScale (engine energy fields).</summary>
        Energy = 5,
        /// <summary>Average localScale, including engine and thruster move / accel.</summary>
        MoveOrAccel = 6,
        /// <summary>Average localScale. A larger fin or thruster turns faster.</summary>
        Turn = 7,
        /// <summary>Average localScale. A larger cockpit rams harder.</summary>
        Ramming = 8,
        /// <summary>Average localScale (gems / people / tractor).</summary>
        Capacity = 9,
        /// <summary>Unscaled on propulsion (designer OVERDRIVE fraction). Average localScale otherwise.</summary>
        Overdrive = 10
    }

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
                firePowerPerExtraLevel = a.firePowerPerExtraLevel + b.firePowerPerExtraLevel,
                bulletSpeed = a.bulletSpeed + b.bulletSpeed,
                bulletSpeedPerExtraLevel = a.bulletSpeedPerExtraLevel + b.bulletSpeedPerExtraLevel,
                bulletRange = a.bulletRange + b.bulletRange,
                bulletRangePerExtraLevel = a.bulletRangePerExtraLevel + b.bulletRangePerExtraLevel,
                fireRate = a.fireRate + b.fireRate,
                fireRatePerExtraLevel = a.fireRatePerExtraLevel + b.fireRatePerExtraLevel,
                rammingPower = a.rammingPower + b.rammingPower,
                rammingPowerPerExtraLevel = a.rammingPowerPerExtraLevel + b.rammingPowerPerExtraLevel,
                healthCap = a.healthCap + b.healthCap,
                healthCapPerExtraLevel = a.healthCapPerExtraLevel + b.healthCapPerExtraLevel,
                healthRegen = a.healthRegen + b.healthRegen,
                healthRegenPerExtraLevel = a.healthRegenPerExtraLevel + b.healthRegenPerExtraLevel,
                energyCap = a.energyCap + b.energyCap,
                energyCapPerExtraLevel = a.energyCapPerExtraLevel + b.energyCapPerExtraLevel,
                energyRegen = a.energyRegen + b.energyRegen,
                energyRegenPerExtraLevel = a.energyRegenPerExtraLevel + b.energyRegenPerExtraLevel,
                moveSpeed = a.moveSpeed + b.moveSpeed,
                moveSpeedPerExtraLevel = a.moveSpeedPerExtraLevel + b.moveSpeedPerExtraLevel,
                accelerationCap = a.accelerationCap + b.accelerationCap,
                accelerationCapPerExtraLevel = a.accelerationCapPerExtraLevel + b.accelerationCapPerExtraLevel,
                // [TITAN-ORBIT] OVERDRIVE speed fraction — max across parts (do not sum 0.75+0.75).
                extraSpeedPercent = Mathf.Max(a.extraSpeedPercent, b.extraSpeedPercent),
                extraSpeedPercentPerExtraLevel = Mathf.Max(a.extraSpeedPercentPerExtraLevel, b.extraSpeedPercentPerExtraLevel),
                // [TITAN-ORBIT] Absolute OD drain — sum engines (matches ResolveOverdriveFromEngines).
                extraSpeedEnergyDrain = a.extraSpeedEnergyDrain + b.extraSpeedEnergyDrain,
                extraSpeedEnergyDrainPerExtraLevel =
                    a.extraSpeedEnergyDrainPerExtraLevel + b.extraSpeedEnergyDrainPerExtraLevel,
                turnSpeed = a.turnSpeed + b.turnSpeed,
                turnSpeedPerExtraLevel = a.turnSpeedPerExtraLevel + b.turnSpeedPerExtraLevel,
                maxGems = a.maxGems + b.maxGems,
                maxGemsPerExtraLevel = a.maxGemsPerExtraLevel + b.maxGemsPerExtraLevel,
                tractorBeamDistance = a.tractorBeamDistance + b.tractorBeamDistance,
                tractorBeamDistancePerExtraLevel = a.tractorBeamDistancePerExtraLevel + b.tractorBeamDistancePerExtraLevel,
                tractorBeamPower = a.tractorBeamPower + b.tractorBeamPower,
                tractorBeamPowerPerExtraLevel = a.tractorBeamPowerPerExtraLevel + b.tractorBeamPowerPerExtraLevel,
                maxPeople = a.maxPeople + b.maxPeople,
                maxPeoplePerExtraLevel = a.maxPeoplePerExtraLevel + b.maxPeoplePerExtraLevel,
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
        /// also skips <c>bulletSpeedPerExtraLevel</c> for chassis leveling).
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
                total.bulletSpeedPerExtraLevel -= s.bulletSpeedPerExtraLevel;
                maxSpeed = Mathf.Max(maxSpeed, s.bulletSpeed);
                maxSpeedPerLevel = Mathf.Max(maxSpeedPerLevel, s.bulletSpeedPerExtraLevel);
                anyWeapon = true;
            }

            if (!anyWeapon)
                return total;

            // --- One projectile speed for the hull (fastest barrel), not N× sum ---
            total.bulletSpeed = Mathf.Max(0f, total.bulletSpeed) + maxSpeed;
            total.bulletSpeedPerExtraLevel = Mathf.Max(0f, total.bulletSpeedPerExtraLevel) + maxSpeedPerLevel;
            return total;
        }

        /// <summary>
        /// Replaces naively summed weapon <c>bulletRange</c> with the <b>max</b> across weapon parts.
        /// <para>
        /// [TITAN-ORBIT] Bullet range is a per-projectile property (same as speed — one travel
        /// distance for the hull, not N× guns). Unlike fire power, range is <b>not</b> a bottom-bar
        /// attribute; it grows with ship level via <c>bulletRangePerExtraLevel</c> and family
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
                total.bulletRangePerExtraLevel -= s.bulletRangePerExtraLevel;
                maxRange = Mathf.Max(maxRange, s.bulletRange);
                maxRangePerLevel = Mathf.Max(maxRangePerLevel, s.bulletRangePerExtraLevel);
                anyWeapon = true;
            }

            if (!anyWeapon)
                return total;

            // --- One projectile range for the hull (longest barrel), not N× sum ---
            total.bulletRange = Mathf.Max(0f, total.bulletRange) + maxRange;
            total.bulletRangePerExtraLevel = Mathf.Max(0f, total.bulletRangePerExtraLevel) + maxRangePerLevel;
            return total;
        }

        /// <summary>
        /// Intentionally keeps naively summed weapon <c>firePower</c> (total across barrels).
        /// <para>
        /// [TITAN-ORBIT] Live bullets use <b>per-mount</b> firePower from
        /// <c>ShipWeaponMountCombatLogic</c> — not this hull total. The summed value is for power
        /// score / UI “how much gun does this hull carry.” Older code averaged here so a single
        /// shared <c>ShipWeaponConfig.BulletDamage</c> would not N×; that is obsolete.
        /// </para>
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
        /// Intentionally keeps naively summed weapon <c>fireRate</c> (total across barrels).
        /// <para>
        /// [TITAN-ORBIT] Live fire uses <b>per-mount</b> fireRate / FireCooldown so a fat cannon
        /// can shoot slowly while side guns shoot fast. Hull sum is for power-score capacity only.
        /// </para>
        /// </summary>
        public static ShipComponentAbilityStats ApplyWeaponFireRateToSummedStats(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> componentIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats)
        {
            // --- Keep sum (per-barrel cadence lives on each ShipWeaponMountElement) ---
            _ = componentIds;
            _ = perComponentStats;
            return total;
        }

        /// <summary>True when every base and per-level field is exactly zero.</summary>
        public static bool IsAllZero(in ShipComponentAbilityStats s)
        {
            // --- IsAllZero ---
            return s.firePower == 0f && s.firePowerPerExtraLevel == 0f &&
                   s.bulletSpeed == 0f && s.bulletSpeedPerExtraLevel == 0f &&
                   s.bulletRange == 0f && s.bulletRangePerExtraLevel == 0f &&
                   s.fireRate == 0f && s.fireRatePerExtraLevel == 0f &&
                   s.rammingPower == 0f && s.rammingPowerPerExtraLevel == 0f &&
                   s.healthCap == 0f && s.healthCapPerExtraLevel == 0f &&
                   s.healthRegen == 0f && s.healthRegenPerExtraLevel == 0f &&
                   s.energyCap == 0f && s.energyCapPerExtraLevel == 0f &&
                   s.energyRegen == 0f && s.energyRegenPerExtraLevel == 0f &&
                   s.moveSpeed == 0f && s.moveSpeedPerExtraLevel == 0f &&
                   s.accelerationCap == 0f && s.accelerationCapPerExtraLevel == 0f &&
                   s.extraSpeedPercent == 0f && s.extraSpeedPercentPerExtraLevel == 0f &&
                   s.extraSpeedEnergyDrain == 0f && s.extraSpeedEnergyDrainPerExtraLevel == 0f &&
                   s.turnSpeed == 0f && s.turnSpeedPerExtraLevel == 0f &&
                   s.maxGems == 0f && s.maxGemsPerExtraLevel == 0f &&
                   s.tractorBeamDistance == 0f && s.tractorBeamDistancePerExtraLevel == 0f &&
                   s.tractorBeamPower == 0f && s.tractorBeamPowerPerExtraLevel == 0f &&
                   s.maxPeople == 0f && s.maxPeoplePerExtraLevel == 0f;
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
            if (result.firePowerPerExtraLevel == 0f) result.firePowerPerExtraLevel = defaults.firePowerPerExtraLevel;
            if (result.bulletSpeed == 0f) result.bulletSpeed = defaults.bulletSpeed;
            if (result.bulletSpeedPerExtraLevel == 0f) result.bulletSpeedPerExtraLevel = defaults.bulletSpeedPerExtraLevel;
            if (result.bulletRange == 0f) result.bulletRange = defaults.bulletRange;
            if (result.bulletRangePerExtraLevel == 0f) result.bulletRangePerExtraLevel = defaults.bulletRangePerExtraLevel;
            if (result.fireRate == 0f) result.fireRate = defaults.fireRate;
            if (result.fireRatePerExtraLevel == 0f) result.fireRatePerExtraLevel = defaults.fireRatePerExtraLevel;
            if (result.rammingPower == 0f) result.rammingPower = defaults.rammingPower;
            if (result.rammingPowerPerExtraLevel == 0f) result.rammingPowerPerExtraLevel = defaults.rammingPowerPerExtraLevel;
            if (result.healthCap == 0f) result.healthCap = defaults.healthCap;
            if (result.healthCapPerExtraLevel == 0f) result.healthCapPerExtraLevel = defaults.healthCapPerExtraLevel;
            if (result.healthRegen == 0f) result.healthRegen = defaults.healthRegen;
            if (result.healthRegenPerExtraLevel == 0f) result.healthRegenPerExtraLevel = defaults.healthRegenPerExtraLevel;
            if (result.energyCap == 0f) result.energyCap = defaults.energyCap;
            if (result.energyCapPerExtraLevel == 0f) result.energyCapPerExtraLevel = defaults.energyCapPerExtraLevel;
            if (result.energyRegen == 0f) result.energyRegen = defaults.energyRegen;
            if (result.energyRegenPerExtraLevel == 0f) result.energyRegenPerExtraLevel = defaults.energyRegenPerExtraLevel;
            if (result.moveSpeed == 0f) result.moveSpeed = defaults.moveSpeed;
            if (result.moveSpeedPerExtraLevel == 0f) result.moveSpeedPerExtraLevel = defaults.moveSpeedPerExtraLevel;
            if (result.accelerationCap == 0f) result.accelerationCap = defaults.accelerationCap;
            if (result.accelerationCapPerExtraLevel == 0f) result.accelerationCapPerExtraLevel = defaults.accelerationCapPerExtraLevel;
            if (result.extraSpeedPercent == 0f) result.extraSpeedPercent = defaults.extraSpeedPercent;
            if (result.extraSpeedPercentPerExtraLevel == 0f)
                result.extraSpeedPercentPerExtraLevel = defaults.extraSpeedPercentPerExtraLevel;
            if (result.extraSpeedEnergyDrain == 0f)
                result.extraSpeedEnergyDrain = defaults.extraSpeedEnergyDrain;
            if (result.extraSpeedEnergyDrainPerExtraLevel == 0f)
                result.extraSpeedEnergyDrainPerExtraLevel = defaults.extraSpeedEnergyDrainPerExtraLevel;
            if (result.turnSpeed == 0f) result.turnSpeed = defaults.turnSpeed;
            if (result.turnSpeedPerExtraLevel == 0f) result.turnSpeedPerExtraLevel = defaults.turnSpeedPerExtraLevel;
            if (result.maxGems == 0f) result.maxGems = defaults.maxGems;
            if (result.maxGemsPerExtraLevel == 0f) result.maxGemsPerExtraLevel = defaults.maxGemsPerExtraLevel;
            if (result.tractorBeamDistance == 0f) result.tractorBeamDistance = defaults.tractorBeamDistance;
            if (result.tractorBeamDistancePerExtraLevel == 0f) result.tractorBeamDistancePerExtraLevel = defaults.tractorBeamDistancePerExtraLevel;
            if (result.tractorBeamPower == 0f) result.tractorBeamPower = defaults.tractorBeamPower;
            if (result.tractorBeamPowerPerExtraLevel == 0f) result.tractorBeamPowerPerExtraLevel = defaults.tractorBeamPowerPerExtraLevel;
            if (result.maxPeople == 0f) result.maxPeople = defaults.maxPeople;
            if (result.maxPeoplePerExtraLevel == 0f) result.maxPeoplePerExtraLevel = defaults.maxPeoplePerExtraLevel;
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

        /// <summary>
        /// Size factor shared by health, gems, move, acceleration, turn, and ramming:
        /// <c>(x + y + z) / 3</c>. Weapon bullet speed and bullet range do not use it.
        /// </summary>
        /// <param name="localScale">Authored chassis-prefab child scale (not live mesh grow).</param>
        /// <returns>1 at unit scale. Z is floored at 0.01 so a flat part cannot zero the average.</returns>
        public static float GetAveragePrefabScale(Vector3 localScale)
        {
            // Signed axes, same as the historical health / gem multiply. A mirrored
            // wing with negative X keeps that sign; we do not take Abs here.
            float x = localScale.x;
            float y = localScale.y;
            float z = Mathf.Max(localScale.z, 0.01f);
            return (x + y + z) / 3f;
        }

        /// <summary>
        /// Fire-power multiplier from a weapon’s authored prefab scale: average of |X| and |Y|.
        /// <para>
        /// [TITAN-ORBIT] A barrel at <c>(2, 2, z)</c> hits twice as hard. Negative axes
        /// (mirrored left/right guns) still count as size. Used for Base and PerExtra.
        /// </para>
        /// </summary>
        /// <param name="localScale">Chassis-prefab child <c>localScale</c> (not live mesh grow).</param>
        /// <returns>Factor to multiply catalog fire power by (1 at unit scale).</returns>
        public static float GetWeaponFirePowerScale(Vector3 localScale)
        {
            float x = Mathf.Abs(localScale.x);
            float y = Mathf.Abs(localScale.y);
            return (x + y) * 0.5f;
        }

        /// <summary>
        /// Fire-rate multiplier from a weapon’s authored prefab Z. Z divides the catalog
        /// rate: a larger barrel on Z fires slower.
        /// <para>
        /// [TITAN-ORBIT] A barrel at <c>(x, y, 2)</c> fires at half the catalog rate.
        /// A barrel at <c>(x, y, 0.9)</c> fires faster than unit Z (about 1.11×).
        /// Negative axes (mirrored left/right guns) still count as size. The 0.01 floor
        /// keeps a zero Z from sending the rate to infinity (cap is 100×).
        /// </para>
        /// </summary>
        /// <param name="localScale">Chassis-prefab child <c>localScale</c> (not live mesh grow).</param>
        /// <returns>Factor to multiply catalog fire rate by (1 at unit Z, 0.5 at Z=2).</returns>
        public static float GetWeaponFireRateScale(Vector3 localScale)
        {
            float z = Mathf.Abs(localScale.z);
            return 1f / Mathf.Max(z, 0.01f);
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
                || ContainsIsolatedKeyword(id, "missile")
                || ContainsIsolatedKeyword(id, "turret")
                || ContainsIsolatedKeyword(id, "launcher"))
                return true;
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            return ShipFamilyPartTypes.IsWeapon(partType);
        }

        /// <summary>
        /// [TITAN-ORBIT] Maneuver jets: name contains Thruster or Exhaust.
        /// Author turn + Acceleration; set thrust energy drain. Do not own Energy Cap/Regen
        /// or Move Speed (engines own cruise).
        /// </summary>
        public static bool IsThrusterComponent(string componentId)
        {
            // --- IsThrusterComponent ---
            return ShipFamilyPartTypes.IsThrusterLikeName(componentId);
        }

        /// <summary>
        /// [TITAN-ORBIT] Power plants: propulsion mounts that are not thruster-like.
        /// Author Energy Cap/Regen (cumulative) + Move Speed; no turn.
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
        /// Multiplier <see cref="ScaleStatsByTransform"/> applies to one Base / PerExtra pair.
        /// <para>
        /// [TITAN-ORBIT] Starting prefab <c>localScale</c> is an art lever.
        /// A Cockpit at scale 3 contributes <c>3 ×</c> catalog Health, Gems, Troops, and Ramming.
        /// An engine or thruster at scale 2 contributes <c>2 ×</c> Move and Acceleration.
        /// Weapons still split axes for the shot itself — fire power uses average |X|+|Y|,
        /// fire rate uses 1/|Z| (2 is half rate, 0.9 is faster than 1). Bullet speed and
        /// bullet range stay the catalog values on every barrel. Turn, ramming, move, and
        /// accel still use the <c>(x+y+z)/3</c> average.
        /// OVERDRIVE on an engine or thruster stays ×1 (a designer fraction, not mesh size).
        /// </para>
        /// Safe with a default <c>(1,1,1)</c> scale (moon-store extras have no prefab child).
        /// </summary>
        /// <param name="localScale">Authored chassis-prefab child scale (not live mesh grow).</param>
        /// <param name="componentId">Part id used to classify weapon vs propulsion vs cockpit.</param>
        /// <param name="channel">Which ability field we are scaling.</param>
        /// <returns>Factor to multiply catalog Base and PerExtra by.</returns>
        public static float GetScaleMultiplier(
            Vector3 localScale,
            string componentId,
            ShipComponentScaleChannel channel)
        {
            // --- Same branches as ScaleStatsByTransform (keep these twins in lockstep) ---
            // Weapon damage and cadence keep their own axes. Bullet speed and range stay
            // catalog (×1) so a bigger barrel does not fly faster or farther. Move, turn,
            // and ramming on a barrel still use the part's average size.
            if (IsWeaponComponent(componentId))
            {
                if (channel == ShipComponentScaleChannel.FirePower)
                    return GetWeaponFirePowerScale(localScale);
                if (channel == ShipComponentScaleChannel.FireRate)
                    return GetWeaponFireRateScale(localScale);
                if (channel == ShipComponentScaleChannel.BulletSpeed
                    || channel == ShipComponentScaleChannel.BulletRange)
                    return 1f;
                if (UsesAveragePrefabScale(channel))
                    return GetAveragePrefabScale(localScale);
                return 1f;
            }

            // OVERDRIVE percent and drain are authored knobs. Move and accel are not.
            if (IsPropulsionComponent(componentId)
                && channel == ShipComponentScaleChannel.Overdrive)
                return 1f;

            return GetAveragePrefabScale(localScale);
        }

        /// <summary>
        /// True when this field grows with <see cref="GetAveragePrefabScale"/> even on a weapon.
        /// Fire power and fire rate use XY and 1/Z. Bullet speed and bullet range stay ×1 on a weapon.
        /// </summary>
        static bool UsesAveragePrefabScale(ShipComponentScaleChannel channel) =>
            channel == ShipComponentScaleChannel.MoveOrAccel
            || channel == ShipComponentScaleChannel.Turn
            || channel == ShipComponentScaleChannel.Ramming;

        /// <summary>
        /// Short HUD reason for the scale factor (empty when the multiplier is ~1).
        /// </summary>
        public static string DescribeScaleReason(
            string componentId,
            ShipComponentScaleChannel channel,
            float multiplier)
        {
            if (Mathf.Abs(multiplier - 1f) <= 0.01f)
                return string.Empty;

            if (IsWeaponComponent(componentId))
            {
                if (channel == ShipComponentScaleChannel.FirePower)
                    return "prefab XY";
                if (channel == ShipComponentScaleChannel.FireRate)
                    return "prefab Z";
            }

            _ = componentId;
            _ = channel;
            return "prefab start";
        }

        /// <summary>
        /// Scales authored stats by prefab child transform size. Weapons apply XY to
        /// fire power and 1/|Z| to fire rate (2 is half rate). Weapon bullet speed and
        /// bullet range stay the catalog values at every barrel size. Move, acceleration,
        /// turn, and ramming use average <c>(x+y+z)/3</c> on every part, including engines,
        /// thrusters, cockpits, and barrels.
        /// <para>
        /// [TITAN-ORBIT] Call only with <b>chassis prefab</b> authored localScale (art lever
        /// for part size). Do not pass live hybrid proxies after attribute mesh grow —
        /// combat already applies Fire Power attributes as numeric multipliers (mesh/collider
        /// grow is separate from this starting-scale multiply). Ability-chip math uses
        /// <see cref="GetScaleMultiplier"/> so the details card can show the same factor.
        /// OVERDRIVE on an engine or thruster is copied back unscaled.
        /// </para>
        /// </summary>
        public static ShipComponentAbilityStats ScaleStatsByTransform(
            ShipComponentAbilityStats stats,
            Transform t,
            string componentId)
        {
            if (t == null) return stats;

            Vector3 local = t.localScale;
            float averageScale = GetAveragePrefabScale(local);

            // [TITAN-ORBIT] Weapon prefab scale is the barrel’s combat lever.
            // Average |X|+|Y| fattens the hit; 1/|Z| scales shots per second
            // (2 is half the catalog rate, 0.9 is faster than unit scale).
            // Bullet speed and bullet range stay the catalog numbers on every barrel
            // size so a larger gun does not throw a faster or longer round. Energy on a
            // gun stays catalog. Extra Level still evaluates on the scaled
            // Base / PerExtra. Bullet speed and range Base and PerExtra stay catalog.
            if (IsWeaponComponent(componentId))
            {
                float firePowerScale = GetWeaponFirePowerScale(local);
                float fireRateScale = GetWeaponFireRateScale(local);
                var weaponScaled = stats;
                weaponScaled.firePower = stats.firePower * firePowerScale;
                weaponScaled.firePowerPerExtraLevel = stats.firePowerPerExtraLevel * firePowerScale;
                weaponScaled.fireRate = stats.fireRate * fireRateScale;
                weaponScaled.fireRatePerExtraLevel = stats.fireRatePerExtraLevel * fireRateScale;
                ApplyAverageScale(ref weaponScaled, stats, averageScale);
                return weaponScaled;
            }

            var scaled = Multiply(stats, averageScale);
            if (IsPropulsionComponent(componentId))
            {
                // OVERDRIVE fractions are designer knobs — not mesh-scale dependent.
                // Move and acceleration stay on averageScale (a bigger engine is faster).
                scaled.extraSpeedPercent = stats.extraSpeedPercent;
                scaled.extraSpeedPercentPerExtraLevel = stats.extraSpeedPercentPerExtraLevel;
                scaled.extraSpeedEnergyDrain = stats.extraSpeedEnergyDrain;
                scaled.extraSpeedEnergyDrainPerExtraLevel = stats.extraSpeedEnergyDrainPerExtraLevel;
            }
            return scaled;
        }

        /// <summary>
        /// Writes average-scale onto turn, ramming, move, and acceleration.
        /// Bullet speed, bullet range, and their per-level steps stay the catalog values:
        /// weapon scale must not change how fast or how far the round travels.
        /// Used for weapon barrels, which do not run the full <see cref="Multiply"/> path
        /// (that path would also resize fire power and fire rate, which have their own axes).
        /// </summary>
        static void ApplyAverageScale(
            ref ShipComponentAbilityStats target,
            ShipComponentAbilityStats catalog,
            float averageScale)
        {
            target.bulletSpeed = catalog.bulletSpeed;
            target.bulletSpeedPerExtraLevel = catalog.bulletSpeedPerExtraLevel;
            target.bulletRange = catalog.bulletRange;
            target.bulletRangePerExtraLevel = catalog.bulletRangePerExtraLevel;
            target.moveSpeed = catalog.moveSpeed * averageScale;
            target.moveSpeedPerExtraLevel = catalog.moveSpeedPerExtraLevel * averageScale;
            target.accelerationCap = catalog.accelerationCap * averageScale;
            target.accelerationCapPerExtraLevel = catalog.accelerationCapPerExtraLevel * averageScale;
            target.turnSpeed = catalog.turnSpeed * averageScale;
            target.turnSpeedPerExtraLevel = catalog.turnSpeedPerExtraLevel * averageScale;
            target.rammingPower = catalog.rammingPower * averageScale;
            target.rammingPowerPerExtraLevel = catalog.rammingPowerPerExtraLevel * averageScale;
        }

        /// <summary>Multiplies every numeric stat field by <paramref name="factor"/>.</summary>
        public static ShipComponentAbilityStats Multiply(ShipComponentAbilityStats s, float factor)
        {
            // --- Multiply ---
            return new ShipComponentAbilityStats
            {
                firePower = s.firePower * factor,
                firePowerPerExtraLevel = s.firePowerPerExtraLevel * factor,
                bulletSpeed = s.bulletSpeed * factor,
                bulletSpeedPerExtraLevel = s.bulletSpeedPerExtraLevel * factor,
                bulletRange = s.bulletRange * factor,
                bulletRangePerExtraLevel = s.bulletRangePerExtraLevel * factor,
                fireRate = s.fireRate * factor,
                fireRatePerExtraLevel = s.fireRatePerExtraLevel * factor,
                rammingPower = s.rammingPower * factor,
                rammingPowerPerExtraLevel = s.rammingPowerPerExtraLevel * factor,
                healthCap = s.healthCap * factor,
                healthCapPerExtraLevel = s.healthCapPerExtraLevel * factor,
                healthRegen = s.healthRegen * factor,
                healthRegenPerExtraLevel = s.healthRegenPerExtraLevel * factor,
                energyCap = s.energyCap * factor,
                energyCapPerExtraLevel = s.energyCapPerExtraLevel * factor,
                energyRegen = s.energyRegen * factor,
                energyRegenPerExtraLevel = s.energyRegenPerExtraLevel * factor,
                moveSpeed = s.moveSpeed * factor,
                moveSpeedPerExtraLevel = s.moveSpeedPerExtraLevel * factor,
                accelerationCap = s.accelerationCap * factor,
                accelerationCapPerExtraLevel = s.accelerationCapPerExtraLevel * factor,
                extraSpeedPercent = s.extraSpeedPercent * factor,
                extraSpeedPercentPerExtraLevel = s.extraSpeedPercentPerExtraLevel * factor,
                extraSpeedEnergyDrain = s.extraSpeedEnergyDrain * factor,
                extraSpeedEnergyDrainPerExtraLevel = s.extraSpeedEnergyDrainPerExtraLevel * factor,
                turnSpeed = s.turnSpeed * factor,
                turnSpeedPerExtraLevel = s.turnSpeedPerExtraLevel * factor,
                maxGems = s.maxGems * factor,
                maxGemsPerExtraLevel = s.maxGemsPerExtraLevel * factor,
                tractorBeamDistance = s.tractorBeamDistance * factor,
                tractorBeamDistancePerExtraLevel = s.tractorBeamDistancePerExtraLevel * factor,
                tractorBeamPower = s.tractorBeamPower * factor,
                tractorBeamPowerPerExtraLevel = s.tractorBeamPowerPerExtraLevel * factor,
                maxPeople = s.maxPeople * factor,
                maxPeoplePerExtraLevel = s.maxPeoplePerExtraLevel * factor,
            };
        }
    }
}
