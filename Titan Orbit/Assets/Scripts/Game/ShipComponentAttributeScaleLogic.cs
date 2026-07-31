using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Maps bottom-bar attribute upgrade levels to per-component mesh scale factors on ship proxies.
    /// Each chassis part group grows by its Part Profile <b>per-level percent of base</b>
    /// (<c>perLevel / base</c> from <c>ShipFamilyPartCalcProfileSet.asset</c>
    /// <c>EvaluateAtVersion(1)</c>). Multiple bottom-bar drivers on one part <b>share</b> growth
    /// (each contributes <c>1/N</c> of its percent) and are <b>added</b> — never multiplied —
    /// then <see cref="ShipFamilyPartCalcProfileSet.globalUpgradeScaleMultiplier"/> (default 0.25)
    /// dampens that growth on every part.
    /// Used by <see cref="ShipComponentAttributeScaleApplier"/> — <b>presentation only</b>.
    /// <para>
    /// Example: Wing has N=4 drivers, <c>healthCap=10</c>, <c>healthCapPerLevel=1</c> → one
    /// MaxHealth purchase grows the wing by +(10%/4)=+2.5%. Full single-driver feel returns when
    /// all N drivers are maxed at the same fraction (sum of shares ≈ one full percent curve).
    /// </para>
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
    /// <para>
    /// Keep driver field maps in sync with <c>Assets/Resources/ShipFamilyPartCalcProfileSet.asset</c>
    /// <c>partProfiles</c> rows. Do not use flat <c>ShipAttributeUpgradeLogic.MultiplierPerLevel</c>
    /// for mesh size — that is combat/stat math only.
    /// </para>
    /// </summary>
    public static class ShipComponentAttributeScaleLogic
    {
        /// <summary>Ignore tiny bases so we never divide by near-zero.</summary>
        const float BaseEpsilon = 0.0001f;

        /// <summary>
        /// Cached <c>perLevel / base</c> fractions per part group, built once at proxy bind from
        /// ProfileSet <c>EvaluateAtVersion(1)</c>. Zero means that driver does not grow the mesh.
        /// </summary>
        public struct ProfileScaleRates
        {
            /// <summary>
            /// From <see cref="ShipFamilyPartCalcProfileSet.globalUpgradeScaleMultiplier"/>.
            /// Multiplies growth on every part after 1/N sharing (0.25 = 25% of computed growth).
            /// </summary>
            public float GlobalUpgradeScaleMultiplier;

            // --- Cockpit: Offense (ramming stand-in) + Health + Capacity ---
            /// <summary>FirePower → rammingPower fraction (presentation stand-in; no ramming bottom-bar).</summary>
            public float CockpitOffense;
            public float CockpitHealth;
            public float CockpitHealthRegen;
            public float CockpitGems;
            public float CockpitPeople;

            // --- Wing: Health + Capacity (tractor has no bottom-bar attr) ---
            public float WingHealth;
            public float WingHealthRegen;
            public float WingGems;
            public float WingPeople;

            // --- Engine/Thrust: MovementSpeed → avg(moveSpeed, accelerationCap) fractions ---
            public float EngineMove;

            // --- Tail: RotationSpeed → turnSpeed ---
            public float TailTurn;

            // --- Weapon Bullet/Cannon averaged: Offense + Energy ---
            public float WeaponFirePower;
            public float WeaponBulletSpeed;
            public float WeaponEnergyCap;
            public float WeaponEnergyRegen;

            // --- Hull (legacy Part_*): Health ---
            public float HullHealth;
            public float HullHealthRegen;
        }

        /// <summary>
        /// One chassis part bucket: parallel lists of transforms and their authored local scale/position
        /// captured at bind time so we can re-apply a factor without compounding.
        /// </summary>
        public struct ScaleGroup
        {
            /// <summary>Mounts in this bucket (outermost only after prune).</summary>
            public List<Transform> Transforms;
            /// <summary>Authored localScale at bind — multiplied by the group factor each apply.</summary>
            public List<Vector3> BaseScales;
            /// <summary>Authored localPosition at bind — scaled outward with the same factor.</summary>
            public List<Vector3> BasePositions;
        }

        /// <summary>
        /// Builds per-group <c>perLevel/base</c> fractions from the shared ProfileSet (version 1).
        /// Returns default (all zeros → no grow) when the asset is missing.
        /// </summary>
        public static ProfileScaleRates BuildRatesFromProfileSet(ShipFamilyPartCalcProfileSet profileSet)
        {
            var rates = new ProfileScaleRates
            {
                // Default 25% when asset missing — matches ScriptableObject field default.
                GlobalUpgradeScaleMultiplier = ShipFamilyPartCalcProfileSet.DefaultGlobalUpgradeScaleMultiplier,
            };
            if (profileSet == null)
                return rates;

            // --- Global dampener (editable on the ProfileSet asset in the Inspector) ---
            rates.GlobalUpgradeScaleMultiplier = Mathf.Max(0f, profileSet.globalUpgradeScaleMultiplier);

            // --- Resolve version-1 stats (FillPerLevelIfZero runs inside EvaluateAtVersion) ---
            ShipComponentAbilityStats cockpit = EvaluateOrDefault(profileSet, ShipFamilyPartTypes.Cockpit);
            ShipComponentAbilityStats wing = EvaluateOrDefault(profileSet, ShipFamilyPartTypes.Wing);
            ShipComponentAbilityStats engine = EvaluateOrDefault(profileSet, ShipFamilyPartTypes.Engine);
            ShipComponentAbilityStats tail = EvaluateOrDefault(profileSet, ShipFamilyPartTypes.Tail);
            ShipComponentAbilityStats hull = EvaluateOrDefault(profileSet, ShipFamilyPartTypes.Hull);
            ShipComponentAbilityStats weaponBullet = EvaluateOrDefault(profileSet, ShipFamilyPartTypes.WeaponBullet);
            ShipComponentAbilityStats weaponCannon = EvaluateOrDefault(profileSet, ShipFamilyPartTypes.WeaponCannon);

            // Cockpit Offense uses rammingPower (FirePower is the bottom-bar stand-in).
            rates.CockpitOffense = PerLevelFraction(cockpit.rammingPower, cockpit.rammingPowerPerLevel);
            rates.CockpitHealth = PerLevelFraction(cockpit.healthCap, cockpit.healthCapPerLevel);
            rates.CockpitHealthRegen = PerLevelFraction(cockpit.healthRegen, cockpit.healthRegenPerLevel);
            rates.CockpitGems = PerLevelFraction(cockpit.maxGems, cockpit.maxGemsPerLevel);
            rates.CockpitPeople = PerLevelFraction(cockpit.maxPeople, cockpit.maxPeoplePerLevel);

            rates.WingHealth = PerLevelFraction(wing.healthCap, wing.healthCapPerLevel);
            rates.WingHealthRegen = PerLevelFraction(wing.healthRegen, wing.healthRegenPerLevel);
            rates.WingGems = PerLevelFraction(wing.maxGems, wing.maxGemsPerLevel);
            rates.WingPeople = PerLevelFraction(wing.maxPeople, wing.maxPeoplePerLevel);

            // Engine/Thrust: one MovementSpeed driver = average of move + accel fractions.
            rates.EngineMove = AverageFraction(
                PerLevelFraction(engine.moveSpeed, engine.moveSpeedPerLevel),
                PerLevelFraction(engine.accelerationCap, engine.accelerationCapPerLevel));

            rates.TailTurn = PerLevelFraction(tail.turnSpeed, tail.turnSpeedPerLevel);

            rates.WeaponFirePower = AverageFraction(
                PerLevelFraction(weaponBullet.firePower, weaponBullet.firePowerPerLevel),
                PerLevelFraction(weaponCannon.firePower, weaponCannon.firePowerPerLevel));
            rates.WeaponBulletSpeed = AverageFraction(
                PerLevelFraction(weaponBullet.bulletSpeed, weaponBullet.bulletSpeedPerLevel),
                PerLevelFraction(weaponCannon.bulletSpeed, weaponCannon.bulletSpeedPerLevel));
            rates.WeaponEnergyCap = AverageFraction(
                PerLevelFraction(weaponBullet.energyCap, weaponBullet.energyCapPerLevel),
                PerLevelFraction(weaponCannon.energyCap, weaponCannon.energyCapPerLevel));
            rates.WeaponEnergyRegen = AverageFraction(
                PerLevelFraction(weaponBullet.energyRegen, weaponBullet.energyRegenPerLevel),
                PerLevelFraction(weaponCannon.energyRegen, weaponCannon.energyRegenPerLevel));

            rates.HullHealth = PerLevelFraction(hull.healthCap, hull.healthCapPerLevel);
            rates.HullHealthRegen = PerLevelFraction(hull.healthRegen, hull.healthRegenPerLevel);

            return rates;
        }

        /// <summary>Evaluates a Part Profile at version 1, or default stats when the row is missing.</summary>
        static ShipComponentAbilityStats EvaluateOrDefault(ShipFamilyPartCalcProfileSet profileSet, string partType)
        {
            if (profileSet != null && profileSet.TryGetProfile(partType, out ShipFamilyPartCalcProfile profile) && profile != null)
                return profile.EvaluateAtVersion(1);
            return default;
        }

        /// <summary>
        /// True when weapon components in the family carry energy stats.
        /// [LEGACY] Kept for callers; weapon mesh grow always uses ProfileSet Energy fractions now.
        /// </summary>
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
            // parent and child multiplies in world space (scaleFactor²) — wings looked enormous.
            // Keep the outer mount; children inherit its scale.
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
            ref ScaleGroup tail,
            ref ScaleGroup part)
        {
            // --- Flatten every mount we might scale ---
            var all = new List<Transform>(64);
            AppendGroupTransforms(cockpit, all);
            AppendGroupTransforms(wing, all);
            AppendGroupTransforms(weapon, all);
            AppendGroupTransforms(engine, all);
            AppendGroupTransforms(thruster, all);
            AppendGroupTransforms(tail, all);
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
            PruneIfNestedUnderAny(ref tail, all);
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

        /// <summary>
        /// Computes scale factors from upgrade levels × cached ProfileSet fractions and applies them.
        /// </summary>
        /// <param name="rates">Per-group <c>perLevel/base</c> fractions from <see cref="BuildRatesFromProfileSet"/>.</param>
        /// <param name="territoryMovementMult">
        /// Friendly-triangle speed multiplier (usually 1). Scales Engine/Thrust meshes for territory feedback.
        /// </param>
        public static void Apply(
            in ShipAttributeUpgradeState attrs,
            in ProfileScaleRates rates,
            ScaleGroup cockpit,
            ScaleGroup wing,
            ScaleGroup weapon,
            ScaleGroup engine,
            ScaleGroup thruster,
            ScaleGroup tail,
            ScaleGroup part,
            float territoryMovementMult = 1f)
        {
            ComputeScaleFactors(
                attrs,
                rates,
                out float cockpitScale,
                out float wingScale,
                out float weaponScale,
                out float engineScale,
                out float thrusterScale,
                out float tailScale,
                out float partScale);

            // --- Territory speed feedback (Engine/Thrust mounts only) ---
            // [TITAN-ORBIT] Faster in friendly triangles → bigger propulsion meshes.
            float tMult = Mathf.Max(1f, territoryMovementMult);
            engineScale *= tMult;
            thrusterScale *= tMult;

            ApplyGroup(cockpit, cockpitScale);
            ApplyGroup(wing, wingScale);
            ApplyGroup(weapon, weaponScale);
            ApplyGroup(engine, engineScale);
            ApplyGroup(thruster, thrusterScale);
            ApplyGroup(tail, tailScale);
            ApplyGroup(part, partScale);
        }

        /// <summary>
        /// Derives per-group scale: shared <c>1/N</c> of each driver's percent-of-base growth, summed.
        /// </summary>
        static void ComputeScaleFactors(
            in ShipAttributeUpgradeState attrs,
            in ProfileScaleRates rates,
            out float cockpitScale,
            out float wingScale,
            out float weaponScale,
            out float engineScale,
            out float thrusterScale,
            out float tailScale,
            out float partScale)
        {
            // [NETCODE] Levels come from ghosted ShipAttributeUpgradeState on the ship entity.
            //
            // [TITAN-ORBIT] N = bottom-bar ability slots for that Part Profile (not tractor).
            // Each slot contributes (level × fraction) / N so product compounding cannot explode.

            float globalMul = rates.GlobalUpgradeScaleMultiplier;

            // --- Cockpit: Offense + Health + Capacity (5 drivers) ---
            cockpitScale = SharedAbilityScale(
                5,
                globalMul,
                attrs.FirePower * rates.CockpitOffense,
                attrs.MaxHealth * rates.CockpitHealth,
                attrs.HealthRegen * rates.CockpitHealthRegen,
                attrs.GemCapacity * rates.CockpitGems,
                attrs.PeopleCapacity * rates.CockpitPeople);

            // --- Wing: Health + Capacity (4 drivers; tractor omitted) ---
            wingScale = SharedAbilityScale(
                4,
                globalMul,
                attrs.MaxHealth * rates.WingHealth,
                attrs.HealthRegen * rates.WingHealthRegen,
                attrs.GemCapacity * rates.WingGems,
                attrs.PeopleCapacity * rates.WingPeople);

            // --- Weapon Bullet/Cannon: Offense + Energy (4 drivers) ---
            weaponScale = SharedAbilityScale(
                4,
                globalMul,
                attrs.FirePower * rates.WeaponFirePower,
                attrs.BulletSpeed * rates.WeaponBulletSpeed,
                attrs.EnergyCapacity * rates.WeaponEnergyCap,
                attrs.EnergyRegen * rates.WeaponEnergyRegen);

            // --- Engine/Thrust: MovementSpeed only (N=1 → full percent) ---
            engineScale = SharedAbilityScale(1, globalMul, attrs.MovementSpeed * rates.EngineMove);
            thrusterScale = engineScale;

            // --- Tail: RotationSpeed only ---
            tailScale = SharedAbilityScale(1, globalMul, attrs.RotationSpeed * rates.TailTurn);

            // --- Hull Part_*: Health (2 drivers) ---
            partScale = SharedAbilityScale(
                2,
                globalMul,
                attrs.MaxHealth * rates.HullHealth,
                attrs.HealthRegen * rates.HullHealthRegen);
        }

        /// <summary>
        /// <c>perLevel / base</c> when base is meaningful; otherwise 0 (driver disabled).
        /// </summary>
        public static float PerLevelFraction(float baseValue, float perLevel)
        {
            if (baseValue <= BaseEpsilon)
                return 0f;
            return Mathf.Max(0f, perLevel) / baseValue;
        }

        /// <summary>
        /// Combines ability drivers without multiplicative compounding.
        /// Each entry is <c>attributeLevel × (perLevel/base)</c>; the part has
        /// <paramref name="driverCount"/> slots so each contributes only <c>1/N</c> of that growth,
        /// then <paramref name="globalUpgradeScaleMultiplier"/> scales that growth globally:
        /// <c>scale = 1 + globalMul × sum(growth_i / N)</c>.
        /// </summary>
        /// <param name="driverCount">Fixed ability-slot count for the part (e.g. 4 for Wing/Weapon).</param>
        /// <param name="globalUpgradeScaleMultiplier">
        /// From ProfileSet (default 0.25). 1 = full growth; 0 = no grow.
        /// </param>
        /// <param name="levelTimesFraction">
        /// Per-driver <c>level × fraction</c> terms (same length as the part's driver list).
        /// </param>
        public static float SharedAbilityScale(
            int driverCount,
            float globalUpgradeScaleMultiplier,
            params float[] levelTimesFraction)
        {
            int n = Mathf.Max(1, driverCount);
            float growth = 0f;
            if (levelTimesFraction != null)
            {
                for (int i = 0; i < levelTimesFraction.Length; i++)
                    growth += Mathf.Max(0f, levelTimesFraction[i]) / n;
            }

            // --- Global dampener ---
            // [TITAN-ORBIT] Tunable on ShipFamilyPartCalcProfileSet.globalUpgradeScaleMultiplier.
            float globalMul = Mathf.Max(0f, globalUpgradeScaleMultiplier);
            return 1f + growth * globalMul;
        }

        /// <summary>
        /// Averages positive fractions; ignores zeros so a missing profile field does not dilute.
        /// Both zero → 0.
        /// </summary>
        static float AverageFraction(float a, float b)
        {
            bool hasA = a > BaseEpsilon;
            bool hasB = b > BaseEpsilon;
            if (hasA && hasB)
                return (a + b) * 0.5f;
            if (hasA)
                return a;
            if (hasB)
                return b;
            return 0f;
        }

        /// <summary>
        /// Writes <paramref name="scaleFactor"/> × bind-time localScale/localPosition onto each mount.
        /// </summary>
        static void ApplyGroup(ScaleGroup group, float scaleFactor)
        {
            if (group.Transforms == null)
                return;

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
