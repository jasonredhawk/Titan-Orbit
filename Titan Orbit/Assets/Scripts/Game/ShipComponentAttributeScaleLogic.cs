using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Maps bottom-bar attribute upgrade levels to per-component mesh scale factors on ship proxies
    /// (ported from legacy Starship). Each chassis part group (cockpit, wing, weapon, engine, etc.)
    /// scales from relevant upgrade stats with visibility dampening (ComponentScaleVisibility).
    /// Used by ShipComponentAttributeScaleApplier — <b>presentation only</b>.
    /// <para>
    /// [TITAN-ORBIT] This must never feed combat. Fire power / fire rate come from family Weapon
    /// stats × <b>authored prefab</b> localScale (via <c>ShipWeaponMountCombatLogic</c>) plus ship
    /// level and numeric attribute multipliers — not from these grown proxy meshes.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Whole-ship tier size (+10%/level) is <c>LocalTransform.Scale</c> /
    /// <see cref="BodyCollisionMath.GetShipTierScale"/> — not this per-part grow. Attribute mesh
    /// scale is only for bottom-bar upgrade feedback on top of that uniform hull size.
    /// </para>
    /// </summary>
    public static class ShipComponentAttributeScaleLogic
    {
        /// <summary>Dampens how much upgrade ratios affect visible mesh scale (0.6 = 60% of stat delta).</summary>
        public const float ComponentScaleVisibility = 0.6f;
        public const float WingGemScaleBoost = 1.67f;

        public struct ScaleGroup
        {
            public List<Transform> Transforms;
            public List<Vector3> BaseScales;
            public List<Vector3> BasePositions;
        }

        /// <summary>True when weapon components in the family carry energy stats (affects weapon scale blend).</summary>
        public static bool FamilyHasWeaponComponentEnergy(ShipFamilyDefinition family)
        {
            if (family?.components == null)
                return false;

            for (int i = 0; i < family.components.Count; i++)
            {
                var entry = family.components[i];
                if (entry == null || string.IsNullOrEmpty(entry.componentId))
                    continue;
                if (!ShipComponentAbilityStatsMath.IsWeaponComponent(entry.componentId))
                    continue;
                if (entry.stats.energyCap > 0.01f || entry.stats.energyRegen > 0.01f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Captures current local scale/position for each transform in a component group.
        /// Nested same-group children are dropped — see <see cref="PruneNestedTransforms"/>.
        /// </summary>
        public static ScaleGroup BuildGroup(List<Transform> transforms)
        {
            var group = new ScaleGroup
            {
                Transforms = new List<Transform>(),
                BaseScales = new List<Vector3>(),
                BasePositions = new List<Vector3>(),
            };

            if (transforms == null)
                return group;

            for (int i = 0; i < transforms.Count; i++)
            {
                Transform t = transforms[i];
                if (t == null)
                    continue;
                group.Transforms.Add(t);
                group.BaseScales.Add(t.localScale);
                group.BasePositions.Add(t.localPosition);
            }

            // --- Outermost-only ---
            // ChassisComponentStats also lists nested Wing/Thruster/etc. children. Scaling both
            // parent and child multiplies in world space (scaleFactor²) — wings looked enormous
            // after gem-capacity upgrades. Keep the outer mount; children inherit its scale.
            PruneNestedTransforms(ref group);
            return group;
        }

        /// <summary>
        /// Removes any transform that is a descendant of another transform already in the group.
        /// Call again after manually appending transforms (e.g. Hull → cockpit group).
        /// </summary>
        /// <param name="group">Scale group whose parallel lists are kept in sync.</param>
        public static void PruneNestedTransforms(ref ScaleGroup group)
        {
            if (group.Transforms == null || group.Transforms.Count <= 1)
                return;

            // Walk backward so removals do not shift unvisited indices.
            for (int i = group.Transforms.Count - 1; i >= 0; i--)
            {
                Transform candidate = group.Transforms[i];
                if (candidate == null)
                {
                    RemoveAt(ref group, i);
                    continue;
                }

                // --- Is this transform under another group member? ---
                // [UNITY] IsChildOf is true for any ancestor in the hierarchy (not only direct parent).
                bool nestedUnderSibling = false;
                for (int j = 0; j < group.Transforms.Count; j++)
                {
                    if (i == j)
                        continue;
                    Transform other = group.Transforms[j];
                    if (other != null && candidate.IsChildOf(other))
                    {
                        nestedUnderSibling = true;
                        break;
                    }
                }

                if (nestedUnderSibling)
                    RemoveAt(ref group, i);
            }
        }

        /// <summary>Drops one index from all three parallel lists on a scale group.</summary>
        static void RemoveAt(ref ScaleGroup group, int index)
        {
            group.Transforms.RemoveAt(index);
            if (index < group.BaseScales.Count)
                group.BaseScales.RemoveAt(index);
            if (index < group.BasePositions.Count)
                group.BasePositions.RemoveAt(index);
        }

        /// <summary>
        /// Removes transforms that sit under a member of <b>any</b> scale group (not only the same bucket).
        /// Call once after all groups are built so a Cover under a Wing is not scaled again as Part.
        /// </summary>
        public static void PruneNestedAcrossGroups(
            ref ScaleGroup cockpit,
            ref ScaleGroup wing,
            ref ScaleGroup weapon,
            ref ScaleGroup engine,
            ref ScaleGroup thruster,
            ref ScaleGroup part)
        {
            // --- Flatten every mount we might scale ---
            var all = new List<Transform>(64);
            AppendGroupTransforms(cockpit, all);
            AppendGroupTransforms(wing, all);
            AppendGroupTransforms(weapon, all);
            AppendGroupTransforms(engine, all);
            AppendGroupTransforms(thruster, all);
            AppendGroupTransforms(part, all);

            if (all.Count <= 1)
                return;

            // --- Drop any transform that is a descendant of another scaled mount ---
            // [TITAN-ORBIT] Same-group prune is not enough: ProfileSet put Hull cosmetics in Part
            // while their Wing/Engine parents also grow → world scale multiplies across buckets.
            PruneIfNestedUnderAny(ref cockpit, all);
            PruneIfNestedUnderAny(ref wing, all);
            PruneIfNestedUnderAny(ref weapon, all);
            PruneIfNestedUnderAny(ref engine, all);
            PruneIfNestedUnderAny(ref thruster, all);
            PruneIfNestedUnderAny(ref part, all);
        }

        /// <summary>Appends non-null transforms from <paramref name="group"/> into <paramref name="dst"/>.</summary>
        static void AppendGroupTransforms(in ScaleGroup group, List<Transform> dst)
        {
            if (group.Transforms == null || dst == null)
                return;
            for (int i = 0; i < group.Transforms.Count; i++)
            {
                Transform t = group.Transforms[i];
                if (t != null)
                    dst.Add(t);
            }
        }

        /// <summary>
        /// Removes group members that are children of any transform in <paramref name="allScaled"/>
        /// (other than themselves).
        /// </summary>
        static void PruneIfNestedUnderAny(ref ScaleGroup group, List<Transform> allScaled)
        {
            if (group.Transforms == null || allScaled == null || group.Transforms.Count == 0)
                return;

            for (int i = group.Transforms.Count - 1; i >= 0; i--)
            {
                Transform candidate = group.Transforms[i];
                if (candidate == null)
                {
                    RemoveAt(ref group, i);
                    continue;
                }

                bool nested = false;
                for (int j = 0; j < allScaled.Count; j++)
                {
                    Transform other = allScaled[j];
                    if (other == null || other == candidate)
                        continue;
                    // [UNITY] IsChildOf — true when other is any ancestor.
                    if (candidate.IsChildOf(other))
                    {
                        nested = true;
                        break;
                    }
                }

                if (nested)
                    RemoveAt(ref group, i);
            }
        }

        /// <summary>Computes scale factors from upgrade state and applies to all component groups.</summary>
        /// <param name="territoryMovementMult">
        /// Friendly-triangle speed multiplier (usually 1). Scales engine/thruster meshes the same way
        /// a MovementSpeed attribute upgrade would — visual feedback that territory boost is active.
        /// </param>
        public static void Apply(
            in ShipAttributeUpgradeState attrs,
            ScaleGroup cockpit,
            ScaleGroup wing,
            ScaleGroup weapon,
            ScaleGroup engine,
            ScaleGroup thruster,
            ScaleGroup part,
            bool hasWeaponComponentEnergy,
            float territoryMovementMult = 1f)
        {
            ComputeScaleFactors(
                attrs,
                hasWeaponComponentEnergy,
                cockpit.Transforms.Count,
                out float cockpitScale,
                out float wingScale,
                out float weaponScale,
                out float engineScale,
                out float thrusterScale,
                out float partScale);

            // --- Territory speed feedback (engine + thruster only) ---
            // [TITAN-ORBIT] Match NGO feel: faster in friendly triangles → bigger propulsion meshes.
            float tMult = Mathf.Max(1f, territoryMovementMult);
            engineScale = Mathf.Min(engineScale * tMult, 2f);
            thrusterScale = Mathf.Min(thrusterScale * tMult, 2.5f);

            ApplyGroup(cockpit, cockpitScale);
            ApplyGroup(wing, wingScale);
            ApplyGroup(weapon, weaponScale);
            ApplyGroup(engine, engineScale);
            ApplyGroup(thruster, thrusterScale);
            ApplyGroup(part, partScale);
        }

        /// <summary>
        /// Derives per-group scale from attribute upgrade ratios (+10% per level from ShipAttributeUpgradeLogic).
        /// </summary>
        static void ComputeScaleFactors(
            in ShipAttributeUpgradeState attrs,
            bool hasWeaponComponentEnergy,
            int cockpitCount,
            out float cockpitScale,
            out float wingScale,
            out float weaponScale,
            out float engineScale,
            out float thrusterScale,
            out float partScale)
        {
            float vis = Mathf.Max(0.2f, ComponentScaleVisibility);
            float multiplier = ShipAttributeUpgradeLogic.MultiplierPerLevel;

            // --- Upgrade ratios (1.0 = no upgrades, 1.1 = one level, etc.) ---
            float rHealth = AttributeUpgradeRatio(attrs.MaxHealth, multiplier);
            float rHealthRegen = AttributeUpgradeRatio(attrs.HealthRegen, multiplier);
            float rEnergyCap = AttributeUpgradeRatio(attrs.EnergyCapacity, multiplier);
            float rEnergyRegen = AttributeUpgradeRatio(attrs.EnergyRegen, multiplier);
            float rPeople = AttributeUpgradeRatio(attrs.PeopleCapacity, multiplier);
            float rGem = AttributeUpgradeRatio(attrs.GemCapacity, multiplier);
            float rMove = AttributeUpgradeRatio(attrs.MovementSpeed, multiplier);
            float rTurn = AttributeUpgradeRatio(attrs.RotationSpeed, multiplier);
            float rDamage = AttributeUpgradeRatio(attrs.FirePower, multiplier);
            float rBulletSpeed = AttributeUpgradeRatio(attrs.BulletSpeed, multiplier);

            float avgBody = (rHealth + rPeople + rEnergyCap + rEnergyRegen) * 0.25f;
            float avgWeapon = (rDamage + rBulletSpeed) * 0.5f;
            float avgPart = (rHealth + rHealthRegen + rGem + rPeople) * 0.25f;

            cockpitScale = Mathf.Max(
                StatScale(avgBody, vis),
                StatScale(Mathf.Max(Mathf.Max(rHealth, rPeople), Mathf.Max(rEnergyCap, rEnergyRegen)), vis, 0.9f));

            // --- Wings (gem capacity primary, turn secondary) ---
            // [TITAN-ORBIT] Cargo upgrades grow wings so tractor pads read as "bigger hold".
            // WingGemScaleBoost (~1.67) × visibility (0.6) ≈ 1:1 with the +10%/level gem ratio —
            // same feel as before; do not raise this to "fix" size (check nested prune first).
            float wingScaleFromGem = StatScale(rGem, vis, WingGemScaleBoost);
            float wingScaleFromTurn = StatScale(rTurn, vis, 0.9f);
            wingScale = Mathf.Max(wingScaleFromGem, StatScale((rGem + rTurn) * 0.5f, vis));
            wingScale = Mathf.Max(wingScale, wingScaleFromTurn);

            weaponScale = Mathf.Max(
                StatScale(avgWeapon, vis),
                StatScale(Mathf.Max(rDamage, rBulletSpeed), vis, 0.9f));
            if (hasWeaponComponentEnergy || cockpitCount == 0)
                weaponScale = Mathf.Max(weaponScale, StatScale(avgBody, vis, 0.85f));

            engineScale = Mathf.Max(StatScale(rMove, vis), StatScale((rMove + rHealth) * 0.5f, vis, 0.85f));
            thrusterScale = Mathf.Max(StatScale(rMove, vis, 0.9f), StatScale(rTurn, vis, 0.8f));
            partScale = Mathf.Max(StatScale(avgPart, vis), StatScale(Mathf.Max(rGem, rHealth), vis, 0.85f));

            // [TITAN-ORBIT] Hard caps prevent extreme mesh distortion at max upgrades.
            wingScale = Mathf.Min(wingScale, 3.5f);
            cockpitScale = Mathf.Min(cockpitScale, 3f);
            weaponScale = Mathf.Min(weaponScale, 3f);
            engineScale = Mathf.Min(engineScale, 2f);
            thrusterScale = Mathf.Min(thrusterScale, 2.5f);
            partScale = Mathf.Min(partScale, 3f);
        }

        static float AttributeUpgradeRatio(int attributeLevel, float multiplierPerLevel) =>
            1f + attributeLevel * multiplierPerLevel;

        static float StatScale(float ratio, float visibility, float boost = 1f)
        {
            float clampedRatio = Mathf.Max(1f, ratio);
            return Mathf.Max(1f, 1f + (clampedRatio - 1f) * visibility * Mathf.Max(0.01f, boost));
        }

        static void ApplyGroup(ScaleGroup group, float scaleFactor)
        {
            for (int i = 0; i < group.Transforms.Count; i++)
            {
                Transform t = group.Transforms[i];
                if (t == null || i >= group.BaseScales.Count)
                    continue;

                t.localScale = group.BaseScales[i] * scaleFactor;
                if (i < group.BasePositions.Count)
                    t.localPosition = group.BasePositions[i] * scaleFactor;
            }
        }
    }
}
