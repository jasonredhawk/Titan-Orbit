using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;

namespace TitanOrbit.Systems
{
    /// <summary>Resolves bullet bank profiles and applies stat modifiers / on-hit abilities.</summary>
    public static class BulletBankProfileUtility
    {
        public static bool TryGetProfile(int bankIndex, out BulletBankProfile profile)
        {
            profile = null;
            CombatSystem combat = CombatSystem.Instance;
            if (combat == null) return false;
            return combat.TryGetBulletBankProfile(bankIndex, out profile);
        }

        public static BulletBankStatModifiers GetStatModifiers(int bankIndex)
        {
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return BulletBankStatModifiers.Identity;
            return profile.statModifiers;
        }

        public static float ScaleFirePower(float damage, int bankIndex)
        {
            BulletBankStatModifiers mods = GetStatModifiers(bankIndex);
            float m = mods.firePowerMultiplier > 0f ? mods.firePowerMultiplier : 1f;
            return damage * m;
        }

        public static float ScaleBulletSpeed(float speed, int bankIndex)
        {
            BulletBankStatModifiers mods = GetStatModifiers(bankIndex);
            float m = mods.bulletSpeedMultiplier > 0f ? mods.bulletSpeedMultiplier : 1f;
            return speed * m;
        }

        public static float ScaleFireRate(float fireRate, int bankIndex)
        {
            BulletBankStatModifiers mods = GetStatModifiers(bankIndex);
            float m = mods.fireRateMultiplier > 0f ? mods.fireRateMultiplier : 1f;
            return fireRate * m;
        }

        public static float ScaleRammingRating(float rating, int bankIndex)
        {
            BulletBankStatModifiers mods = GetStatModifiers(bankIndex);
            float m = mods.rammingPowerMultiplier > 0f ? mods.rammingPowerMultiplier : 1f;
            return rating * m;
        }

        /// <summary>Maps component/family bullet index into CombatSystem's bank (category index when categories are used).</summary>
        public static int ResolveBankIndex(int componentOrPreferredIndex, int familyDefaultIndex = 0)
        {
            CombatSystem combat = CombatSystem.Instance;
            if (combat == null || combat.BulletPrefabBankCount <= 0)
                return -1;
            int count = combat.BulletPrefabBankCount;
            int idx = componentOrPreferredIndex >= 0 ? componentOrPreferredIndex : familyDefaultIndex;
            if (idx < 0) idx = 0;
            return idx % count;
        }

        public static int ResolveBankIndexForComponent(ShipFamilyComponentEntry entry, ShipFamilyDefinition family)
        {
            int familyDefault = family != null ? family.bulletPrefabIndex : 0;
            int componentIndex = entry != null ? entry.bulletPrefabIndex : -1;
            return ResolveBankIndex(componentIndex, familyDefault);
        }

        public static int ResolveBankIndexForFamily(ShipFamilyDefinition family) =>
            ResolveBankIndex(-1, family != null ? family.bulletPrefabIndex : 0);

        /// <summary>Applies bullet-bank stat modifiers and offense abilities for UI/store display.</summary>
        public static ShipComponentAbilityStats ApplyProfileToComponentStats(
            ShipComponentAbilityStats stats,
            ShipFamilyComponentEntry entry,
            ShipFamilyDefinition family)
        {
            int bankIndex = ResolveBankIndexForComponent(entry, family);
            if (bankIndex < 0)
                return stats;

            string partType = entry != null
                ? ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId)
                : string.Empty;
            bool isWeapon = partType == "Weapon";
            bool hasRamming = Mathf.Abs(stats.rammingPower) > 0.05f;

            ApplyStatModifiersToComponentStats(ref stats, bankIndex, applyFire: isWeapon, applyRamming: isWeapon || hasRamming);
            if (isWeapon && TryGetProfile(bankIndex, out BulletBankProfile profile) && profile != null)
                ApplyAbilityAdjustmentsToOffenseStats(ref stats, profile);
            return stats;
        }

        /// <summary>Applies bullet-bank stat modifiers and offense abilities to a baked chassis breakdown.</summary>
        public static ShipFamilyPowerScoreBreakdown ApplyProfileToBreakdown(
            ShipFamilyPowerScoreBreakdown breakdown,
            int bankIndex)
        {
            if (bankIndex < 0 || !breakdown.HasDisplayStats)
                return breakdown;

            var stats = new ShipComponentAbilityStats
            {
                firePower = breakdown.firePower,
                bulletSpeed = breakdown.bulletSpeed,
                fireRate = breakdown.fireRate,
                rammingPower = breakdown.rammingPower,
                healthCap = breakdown.healthCap,
                healthRegen = breakdown.healthRegen,
                energyCap = breakdown.energyCap,
                energyRegen = breakdown.energyRegen,
                moveSpeed = breakdown.moveSpeed,
                turnSpeed = breakdown.turnSpeed,
                maxGems = breakdown.gemCap,
                maxPeople = breakdown.peopleCap,
            };

            ApplyStatModifiersToComponentStats(ref stats, bankIndex, applyFire: true, applyRamming: true);
            if (TryGetProfile(bankIndex, out BulletBankProfile profile) && profile != null)
                ApplyAbilityAdjustmentsToOffenseStats(ref stats, profile);

            return ShipFamilyPowerScoreBreakdown.FromSummedShipStats(stats);
        }

        private static void ApplyStatModifiersToComponentStats(
            ref ShipComponentAbilityStats stats,
            int bankIndex,
            bool applyFire,
            bool applyRamming)
        {
            if (applyFire)
            {
                stats.firePower = ScaleFirePower(stats.firePower, bankIndex);
                stats.bulletSpeed = ScaleBulletSpeed(stats.bulletSpeed, bankIndex);
                stats.fireRate = ScaleFireRate(stats.fireRate, bankIndex);
            }

            if (applyRamming)
                stats.rammingPower = ScaleRammingRating(stats.rammingPower, bankIndex);
        }

        private static void ApplyAbilityAdjustmentsToOffenseStats(ref ShipComponentAbilityStats stats, BulletBankProfile profile)
        {
            if (profile?.abilities != null)
            {
                for (int i = 0; i < profile.abilities.Count; i++)
                {
                    BulletBankAbility ability = profile.abilities[i];
                    if (ability == null || ability.type != BulletBankAbilityType.BurnOverTime)
                        continue;

                    float dps = ability.magnitude > 0f ? ability.magnitude : stats.firePower * 0.2f;
                    float duration = ability.duration > 0f ? ability.duration : 2f;
                    stats.firePower += dps * duration;
                }
            }

            float peakDamageMultiplier = 1f;
            peakDamageMultiplier = Mathf.Max(peakDamageMultiplier, profile.GetDamageMultiplier(BulletBankDamageTarget.Everything));
            peakDamageMultiplier = Mathf.Max(peakDamageMultiplier, profile.GetDamageMultiplier(BulletBankDamageTarget.Asteroid));
            peakDamageMultiplier = Mathf.Max(peakDamageMultiplier, profile.GetDamageMultiplier(BulletBankDamageTarget.ShipOrDrone));
            peakDamageMultiplier = Mathf.Max(peakDamageMultiplier, profile.GetDamageMultiplier(BulletBankDamageTarget.GemMoon));
            peakDamageMultiplier = Mathf.Max(peakDamageMultiplier, profile.GetDamageMultiplier(BulletBankDamageTarget.Gem));
            if (peakDamageMultiplier > 1.001f)
                stats.firePower *= peakDamageMultiplier;

            if (profile.HasAbility(BulletBankAbilityType.HealFriendly)
                && profile.TryGetAbility(BulletBankAbilityType.HealFriendly, out BulletBankAbility healAbility)
                && healAbility.magnitude > 0f)
            {
                if (stats.firePower < healAbility.magnitude * 0.5f)
                    stats.firePower = healAbility.magnitude;
                else
                    stats.firePower = Mathf.Max(stats.firePower, healAbility.magnitude);
            }
        }

        public static float ResolveDamageForTarget(float baseDamage, int bankIndex, BulletBankDamageTarget target)
        {
            if (baseDamage <= 0f) return baseDamage;
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return baseDamage;
            return baseDamage * profile.GetDamageMultiplier(target);
        }

        /// <summary>Applies bank stat range and optional burn travel/lifetime extensions.</summary>
        public static void ApplyBulletFlightModifiers(int bankIndex, ref float lifetime, ref float maxDistance)
        {
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return;

            BulletBankStatModifiers mods = profile.statModifiers;
            float statRange = mods.bulletRangeMultiplier > 0f ? mods.bulletRangeMultiplier : 1f;
            maxDistance *= statRange;

            if (!profile.HasBurn)
                return;

            maxDistance *= profile.GetBurnBulletRangeMultiplier();
            float burnDur = profile.GetBurnDuration();
            if (burnDur > 0f)
                lifetime = Mathf.Max(lifetime, burnDur * 0.65f);
        }

        /// <summary>Server: apply non-damage abilities after damage/heal has been resolved.</summary>
        public static void ApplyOnHitEffects(
            int bankIndex,
            Collider hitCollider,
            Vector3 impactWorldPos,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId,
            float resolvedDamage,
            bool targetWasHealed)
        {
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return;
            if (profile.abilities == null || profile.abilities.Count == 0)
                return;

            Starship ship = hitCollider != null ? hitCollider.GetComponentInParent<Starship>() : null;
            bool isFriendlyShip = ship != null && !ship.IsDead && ship.ShipTeam == ownerTeam;

            for (int i = 0; i < profile.abilities.Count; i++)
            {
                BulletBankAbility a = profile.abilities[i];
                if (a == null) continue;

                switch (a.type)
                {
                    case BulletBankAbilityType.ElectricShockDisable:
                        if (ship != null && !isFriendlyShip)
                        {
                            float dur = a.duration > 0f ? a.duration : 1f;
                            ship.ApplyBulletElectricShockOnServer(dur);
                        }
                        break;

                    case BulletBankAbilityType.BurnOverTime:
                        if (ship != null && !isFriendlyShip && !targetWasHealed)
                        {
                            float dps = a.magnitude > 0f ? a.magnitude : resolvedDamage * 0.2f;
                            float dur = a.duration > 0f ? a.duration : 2f;
                            float tick = a.tickInterval > 0.05f ? a.tickInterval : 0.25f;
                            ship.ApplyBulletBurnOnServer(dps, dur, tick, ownerTeam, bankIndex, impactWorldPos);
                        }
                        break;

                    case BulletBankAbilityType.ConcussivePush:
                        {
                            float force = a.magnitude > 0f ? a.magnitude : 8f;
                            BulletImpactForceUtility.ApplyKnockbackFromImpact(
                                hitCollider, impactWorldPos, force, pull: false, ownerTeam);
                        }
                        break;

                    case BulletBankAbilityType.GravityPull:
                        {
                            float radius = a.radius > 0f ? a.radius : 6f;
                            float force = a.magnitude > 0f ? a.magnitude : 12f;
                            float dur = a.duration > 0f ? a.duration : 1.5f;
                            CombatSystem combat = CombatSystem.Instance;
                            if (combat != null)
                            {
                                combat.RegisterBulletGravityWell(
                                    impactWorldPos, radius, force, dur, ownerTeam, ownerShipNetworkId);
                            }
                        }
                        break;
                }
            }
        }

        public static bool TryHealFriendlyShip(
            Starship ship,
            int bankIndex,
            float baseDamage,
            TeamManager.Team ownerTeam,
            out float healApplied)
        {
            healApplied = 0f;
            if (ship == null || ship.IsDead) return false;
            if (ship.ShipTeam != ownerTeam) return false;
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return false;
            if (!profile.HasAbility(BulletBankAbilityType.HealFriendly))
                return false;

            float heal = baseDamage;
            if (profile.TryGetAbility(BulletBankAbilityType.HealFriendly, out BulletBankAbility healAbility) && healAbility.magnitude > 0f)
                heal = healAbility.magnitude;

            healApplied = ship.ApplyBulletHealOnServer(heal, ownerTeam);
            return healApplied > 0.0001f;
        }
    }
}
