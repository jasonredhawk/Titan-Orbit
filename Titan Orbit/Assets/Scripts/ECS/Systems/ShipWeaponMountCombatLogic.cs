using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS.Authoring;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Fills each <see cref="ShipWeaponMountElement"/> with its own fire power and fire rate from
    /// the chassis <b>prefab</b> weapon child (authored localScale × family stats + ship level +
    /// Fire Power attributes).
    /// <para>
    /// [TITAN-ORBIT] Bullets use <b>per-mount</b> firePower — not a hull-wide average. A fat main
    /// cannon can hit hard and cool slowly while small side guns hit lighter and shoot faster,
    /// driven by each Weapon child’s <b>authored prefab</b> XY / Z scale.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Intentional: combat stats are read from a fresh Instantiates of the chassis
    /// prefab — <b>never</b> from the live hybrid proxy. Bottom-bar attribute upgrades grow weapon
    /// meshes via <c>ShipComponentAttributeScaleApplier</c> for looks only; that runtime scale must
    /// not multiply firePower / fireRate again (attributes already apply via
    /// <see cref="ShipAttributeUpgradeLogic.MultiplierPerLevel"/> on the numeric stats).
    /// </para>
    /// Paired with <see cref="ShipWeaponFireLogic"/> (independent per-barrel cooldowns) and
    /// <see cref="ShipStatApplyLogic"/> / <see cref="ShipChassisCatalogApplySystem"/>.
    /// </summary>
    public static class ShipWeaponMountCombatLogic
    {
        /// <summary>
        /// Scratch list for aligning bake order with combat bases (avoids per-call alloc in hot apply).
        /// </summary>
        static readonly List<WeaponCombatBase> CombatScratch = new List<WeaponCombatBase>(8);

        /// <summary>
        /// Scale-adjusted level-1 weapon stats for one prefab barrel (before ship-level / attributes).
        /// </summary>
        public struct WeaponCombatBase
        {
            /// <summary>Matches <see cref="ShipWeaponMountElement.CannonIndex"/> / bake order.</summary>
            public int CannonIndex;

            /// <summary>Authored firePower × XY transform scale.</summary>
            public float FirePower;

            /// <summary>Authored firePowerPerLevel × XY transform scale.</summary>
            public float FirePowerPerLevel;

            /// <summary>Authored fireRate × (1/Z) transform scale.</summary>
            public float FireRate;

            /// <summary>Authored fireRatePerLevel × (1/Z) transform scale.</summary>
            public float FireRatePerLevel;
        }

        /// <summary>
        /// Writes combat fields onto an existing mount buffer from the chassis prefab + family.
        /// Preserves pose and <see cref="ShipWeaponMountElement.FireCooldown"/>. Call after mounts
        /// are created or whenever ship level / Fire Power attributes change.
        /// </summary>
        /// <param name="em">Entity manager for the ship world (server or client).</param>
        /// <param name="shipEntity">Ship ghost with a <see cref="ShipWeaponMountElement"/> buffer.</param>
        /// <param name="chassisPrefab">Upgrade-tree hull prefab (Weapon children).</param>
        /// <param name="family">Family definition with Weapon component stats.</param>
        /// <param name="shipLevel">Current ship level (1 = base; 6 adds five × per-level steps).</param>
        /// <param name="attrs">Bottom-bar attribute upgrades (Fire Power multiplies mount damage).</param>
        /// <param name="fallbackDamage">Used when a mount cannot resolve prefab stats.</param>
        /// <param name="fallbackFireRate">Used when a mount cannot resolve prefab stats.</param>
        /// <returns>True when at least one mount received combat stats.</returns>
        public static bool TryApplyCombatStatsToMounts(
            EntityManager em,
            Entity shipEntity,
            GameObject chassisPrefab,
            ShipFamilyDefinition family,
            int shipLevel,
            in ShipAttributeUpgradeState attrs,
            float fallbackDamage,
            float fallbackFireRate)
        {
            // --- Guards ---
            if (!em.HasBuffer<ShipWeaponMountElement>(shipEntity))
                return false;

            var mounts = em.GetBuffer<ShipWeaponMountElement>(shipEntity);
            if (mounts.Length == 0)
                return false;

            // --- Collect per-barrel bases from prefab (same order rules as mount bake) ---
            CombatScratch.Clear();
            if (chassisPrefab != null && family != null)
                CollectWeaponCombatBases(chassisPrefab, family, CombatScratch);

            int perLvl = Mathf.Max(0, shipLevel - 1);
            // [TITAN-ORBIT] Attribute Fire Power: +10% per purchased level on every barrel.
            float firePowerAttrMul = 1f + attrs.FirePower * ShipAttributeUpgradeLogic.MultiplierPerLevel;

            float damageSum = 0f;
            float rateSum = 0f;
            int armed = 0;

            // --- Write each mount: level curve, then attribute mul; keep cooldown ---
            for (int i = 0; i < mounts.Length; i++)
            {
                ShipWeaponMountElement mount = mounts[i];
                float basePower = fallbackDamage;
                float powerPerLevel = 0f;
                float baseRate = fallbackFireRate;
                float ratePerLevel = 0f;

                if (TryFindCombatBase(CombatScratch, mount.CannonIndex, i, out WeaponCombatBase combat))
                {
                    basePower = combat.FirePower;
                    powerPerLevel = combat.FirePowerPerLevel;
                    baseRate = combat.FireRate;
                    ratePerLevel = combat.FireRatePerLevel;
                }

                // Level-1 reference for bullet VFX growth (before attribute mul).
                float referencePower = Mathf.Max(0.1f, basePower);
                float leveledPower = basePower + powerPerLevel * perLvl;
                float leveledRate = baseRate + ratePerLevel * perLvl;

                mount.FirePower = Mathf.Max(0.1f, leveledPower * firePowerAttrMul);
                mount.FireRate = Mathf.Max(0.1f, leveledRate);
                mount.ReferenceFirePower = referencePower;
                // FireCooldown preserved across stat refresh so mid-fight re-apply does not reset.
                mounts[i] = mount;

                damageSum += mount.FirePower;
                rateSum += mount.FireRate;
                armed++;
            }

            // --- Hull ShipWeaponConfig summary (HUD / fallback when a mount has no stats) ---
            // Average hit strength + average cadence — actual shots still use per-mount values.
            if (armed > 0 && em.HasComponent<ShipWeaponConfig>(shipEntity))
            {
                var weapon = em.GetComponentData<ShipWeaponConfig>(shipEntity);
                weapon.BulletDamage = damageSum / armed;
                weapon.EnergyCostPerShot = weapon.BulletDamage;
                weapon.FireRate = rateSum / armed;
                // Level-1 average (pre-attribute) so HUD/VFX baseline matches a typical barrel.
                float refSum = 0f;
                for (int i = 0; i < mounts.Length; i++)
                    refSum += Mathf.Max(0.1f, mounts[i].ReferenceFirePower);
                weapon.ReferenceBulletDamage = refSum / armed;
                em.SetComponentData(shipEntity, weapon);
            }

            return armed > 0;
        }

        /// <summary>
        /// Instantiates the chassis prefab briefly and reads each Weapon child’s scaled stats.
        /// Order / CannonIndex match <see cref="ShipChassisPrefabBakeUtility"/> mount bake.
        /// <para>
        /// [TITAN-ORBIT] Always uses a temporary Instantiates when the asset is not already a scene
        /// object, so authored prefab localScale is the combat lever — not the live ship’s
        /// attribute-grown meshes.
        /// </para>
        /// </summary>
        public static void CollectWeaponCombatBases(
            GameObject chassisPrefab,
            ShipFamilyDefinition family,
            List<WeaponCombatBase> dst)
        {
            if (dst == null || chassisPrefab == null || family == null)
                return;

            string familyId = !string.IsNullOrWhiteSpace(family.familyId)
                ? family.familyId.Trim()
                : string.Empty;

            GameObject instance = null;
            bool destroyInstance = false;
            try
            {
                // [UNITY] Prefab assets are not in a scene — instantiate so children are walkable.
                // [TITAN-ORBIT] Never walk a live hybrid hull here: attribute scale has already
                // grown those meshes for cosmetics and would double-count into firePower/fireRate.
                if (!chassisPrefab.scene.IsValid())
                {
                    instance = UnityEngine.Object.Instantiate(chassisPrefab);
                    destroyInstance = true;
                }
                else
                {
                    // Scene object (rare) — still clone so we do not read mutated live scales.
                    instance = UnityEngine.Object.Instantiate(chassisPrefab);
                    destroyInstance = true;
                }

                Transform root = instance.transform;
                var mountAuthorings = root.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
                if (mountAuthorings != null && mountAuthorings.Length > 0)
                {
                    for (int i = 0; i < mountAuthorings.Length; i++)
                    {
                        var auth = mountAuthorings[i];
                        if (auth == null || auth.transform == root)
                            continue;
                        if (!TryBuildCombatBase(family, familyId, auth.transform, auth.CannonIndex, out WeaponCombatBase b))
                            continue;
                        dst.Add(b);
                    }

                    if (dst.Count > 0)
                        return;
                }

                // --- Name / family weapon id scan (same fallback as mount bake) ---
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == root || !ShipChassisPrefabBakeUtility.LooksLikeWeaponChildForBake(t))
                        continue;
                    if (!TryBuildCombatBase(family, familyId, t, dst.Count, out WeaponCombatBase b))
                        continue;
                    dst.Add(b);
                }
            }
            finally
            {
                if (destroyInstance && instance != null)
                    UnityEngine.Object.Destroy(instance);
            }
        }

        /// <summary>
        /// Resolves family component stats for a weapon transform and applies XY / Z scale rules.
        /// </summary>
        static bool TryBuildCombatBase(
            ShipFamilyDefinition family,
            string familyId,
            Transform weaponTransform,
            int cannonIndex,
            out WeaponCombatBase result)
        {
            result = default;
            if (family == null || weaponTransform == null)
                return false;

            string componentId = ResolveComponentId(weaponTransform.name, familyId);
            if (!family.TryGetStatsForComponent(componentId, out ShipComponentAbilityStats stats))
            {
                // [TITAN-ORBIT] Prefab child may be named "Weapon" while the family entry is "Weapon".
                if (!family.TryGetStatsForComponent("Weapon", out stats))
                    return false;
            }

            ShipComponentAbilityStats scaled =
                ShipComponentAbilityStatsMath.ScaleStatsByTransform(stats, weaponTransform, componentId);

            result = new WeaponCombatBase
            {
                CannonIndex = cannonIndex,
                FirePower = scaled.firePower,
                FirePowerPerLevel = scaled.firePowerPerLevel,
                FireRate = scaled.fireRate,
                FireRatePerLevel = scaled.fireRatePerLevel,
            };
            return result.FirePower > 0.01f || result.FireRate > 0.01f;
        }

        /// <summary>
        /// Strips <c>FamilyId_</c> prefix when present so family lookup matches catalog component ids.
        /// </summary>
        static string ResolveComponentId(string transformName, string familyId)
        {
            if (string.IsNullOrEmpty(transformName))
                return "Weapon";

            if (!string.IsNullOrEmpty(familyId) &&
                transformName.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                return transformName.Substring(familyId.Length + 1);

            int underscore = transformName.IndexOf('_');
            if (underscore > 0 && underscore < transformName.Length - 1)
                return transformName.Substring(underscore + 1);

            return transformName;
        }

        /// <summary>
        /// Finds combat base by CannonIndex; falls back to list index when ids are sparse/defaulted.
        /// </summary>
        static bool TryFindCombatBase(
            List<WeaponCombatBase> bases,
            int cannonIndex,
            int mountListIndex,
            out WeaponCombatBase result)
        {
            result = default;
            if (bases == null || bases.Count == 0)
                return false;

            for (int i = 0; i < bases.Count; i++)
            {
                if (bases[i].CannonIndex != cannonIndex)
                    continue;
                result = bases[i];
                return true;
            }

            if (mountListIndex >= 0 && mountListIndex < bases.Count)
            {
                result = bases[mountListIndex];
                return true;
            }

            return false;
        }
    }
}
