using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS.Authoring;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Fills each <see cref="ShipWeaponMountElement"/> with Extra Level fire power / fire rate.
    /// <para>
    /// [TITAN-ORBIT] Each barrel keeps its own catalog Base / PerExtra (not prefab-scale
    /// multiplied) and evaluates independently — weapons do <b>not</b> use the non-weapon
    /// <c>(N−1)</c> stack term: <c>Base + PerExtra × ((shipLevel−1) + abilityLevel)</c>.
    /// </para>
    /// <para>
    /// Combat stats are read from a fresh Instantiates of the chassis prefab — never from the
    /// live hybrid proxy. Paired with <see cref="ShipWeaponFireLogic"/> and
    /// <see cref="ShipStatApplyLogic"/>.
    /// </para>
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

            /// <summary>Authored catalog firePower (not multiplied by transform scale).</summary>
            public float FirePower;

            /// <summary>Authored catalog firePowerPerExtraLevel (not multiplied by transform scale).</summary>
            public float FirePowerPerLevel;

            /// <summary>Authored catalog fireRate (not multiplied by transform scale).</summary>
            public float FireRate;

            /// <summary>Authored catalog fireRatePerExtraLevel (not multiplied by transform scale).</summary>
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

            // --- Each mount: own Base + PerExtra × ((shipLevel−1) + ability) — no (N−1) ---
            float damageSum = 0f;
            float rateSum = 0f;
            int armed = 0;

            for (int i = 0; i < mounts.Length; i++)
            {
                ShipWeaponMountElement mount = mounts[i];
                float basePower = fallbackDamage;
                float powerPer = 0f;
                float baseRate = fallbackFireRate;
                float ratePer = 0f;

                if (TryFindCombatBase(CombatScratch, mount.CannonIndex, i, out WeaponCombatBase combat))
                {
                    basePower = combat.FirePower;
                    powerPer = combat.FirePowerPerLevel;
                    baseRate = combat.FireRate;
                    ratePer = combat.FireRatePerLevel;
                }

                // [TITAN-ORBIT] Weapons fire individually — componentCount ignored (false stack flag).
                float leveledPower = ShipComponentExtraLevelMath.Evaluate(
                    basePower,
                    powerPer,
                    shipLevel,
                    attrs.FirePower,
                    componentCount: 1,
                    includeExtraComponentLevels: false);
                float leveledRate = ShipComponentExtraLevelMath.Evaluate(
                    baseRate,
                    ratePer,
                    shipLevel,
                    abilityLevel: 0,
                    componentCount: 1,
                    includeExtraComponentLevels: false);

                mount.FirePower = Mathf.Max(0.1f, leveledPower);
                mount.FireRate = Mathf.Max(0.1f, leveledRate);
                mount.ReferenceFirePower = Mathf.Max(0.1f, basePower);
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
        /// Instantiates the chassis prefab briefly and reads each Weapon child’s catalog stats.
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

            // Walk the prefab asset. Do not clone — catalog apply used to Instantiates this
            // hull every tick when a MEGA chassis id fought the Hawk fallback dirty flag.
            Transform root = chassisPrefab.transform;
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
            var assemblies = new System.Collections.Generic.List<UnityEngine.Transform>(16);
            MegaShipPartClassifier.CollectWeaponAssemblies(root, assemblies);
            for (int i = 0; i < assemblies.Count; i++)
            {
                var t = assemblies[i];
                if (t == root)
                    continue;
                if (!TryBuildCombatBase(family, familyId, t, dst.Count, out WeaponCombatBase b))
                    continue;
                dst.Add(b);
            }
        }

        /// <summary>
        /// Resolves family component stats for a weapon transform (catalog values, no scale multiply).
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
                FirePowerPerLevel = scaled.firePowerPerExtraLevel,
                FireRate = scaled.fireRate,
                FireRatePerLevel = scaled.fireRatePerExtraLevel,
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
