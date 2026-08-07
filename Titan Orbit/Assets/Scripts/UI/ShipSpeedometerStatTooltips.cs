using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Which speedometer band the player is hovering. Each maps to a rollover that lists the
    /// chassis / store parts and live motor factors behind that HUD number.
    /// </summary>
    public enum SpeedometerStatSection
    {
        /// <summary>Speed bar, SPD line, territory / load / OVERDRIVE factors.</summary>
        Speed = 0,
        /// <summary>Accel bar and ACC / brake numbers (chassis Accel − mass tax).</summary>
        Accel = 1,
        /// <summary>MASS on the ACC line — mobility totalMass (gems + people + ComponentSize).</summary>
        Mass = 2,
        /// <summary>RAM line — rating × totalMass × speed (impact); grind uses taxed Accel.</summary>
        Ram = 3,
        /// <summary>BUL line — hull-average firepower / rate from weapon parts.</summary>
        Bullets = 4
    }

    /// <summary>
    /// Builds rich-text rollover copy for <see cref="ShipSpeedometerHUD"/>.
    /// Resolves the same prefab + moon-store part list the motor uses
    /// (<see cref="ShipFamilyStatsCalculator"/>), then filters lines per section so players can see
    /// which components feed SPD / ACC / MASS / RAM / BUL.
    /// Presentation-only — never writes ECS. Caches the expensive prefab Instantiates across frames.
    /// </summary>
    public static class ShipSpeedometerStatTooltips
    {
        /// <summary>
        /// Cached chassis part list for tooltip rebuilds. Invalidated when chassis id, ship level,
        /// or equipped store-component hash changes.
        /// </summary>
        public struct PartCache
        {
            /// <summary>True after a successful <see cref="TryRefreshPartCache"/>.</summary>
            public bool Valid;

            /// <summary>Chassis id used for the last successful refresh.</summary>
            public string ChassisId;

            /// <summary>Ship level used when aggregating propulsion at level 1 then scaled in copy.</summary>
            public int ShipLevel;

            /// <summary>Hash of moon-store ShipComponent rows (0 when none).</summary>
            public int EquipmentHash;

            /// <summary>Family asset for display names and growth fraction.</summary>
            public ShipFamilyDefinition Family;

            /// <summary>Matched prefab + store component ids (parallel to <see cref="Stats"/>).</summary>
            public List<string> Ids;

            /// <summary>Level-1 scaled per-part stats (pre ship-tier growth / attributes).</summary>
            public List<ShipComponentAbilityStats> Stats;

            /// <summary>Propulsion pool at ship level (primary + half Speed/Lvl extras + summed accel).</summary>
            public ShipPropulsionAggregation.Result Propulsion;
        }

        /// <summary>
        /// Live numbers already computed by the speedometer for this frame. Passed into Build so the
        /// rollover can show the same totals the bars display (territory, load, OD, mass, ram).
        /// </summary>
        public struct LiveContext
        {
            public ShipState Ship;
            public ShipMotorConfig Motor;
            public ShipComponentAbilityStats EffectiveStats;
            public ShipWeaponConfig Weapon;
            public float CurrentSpeed;
            public float LiveMaxSpeed;
            public float CruiseMaxSpeed;
            public float BarMaxSpeed;
            public float TerritoryMult;
            /// <summary>totalMass = gems×mG + people×mP + componentSize×mCS (mobility tax).</summary>
            public float TotalMass;
            /// <summary>Chassis MaxSpeed before mass tax (leveled + attrs).</summary>
            public float ChassisMaxSpeed;
            /// <summary>Chassis Accel before mass tax (leveled + attrs).</summary>
            public float ChassisAccel;
            /// <summary>Chassis turn °/s before mass tax.</summary>
            public float ChassisTurnDeg;
            /// <summary>
            /// After-tax Accel (mobility tax only — no territory / OVERDRIVE).
            /// Grind damage uses this lever.
            /// </summary>
            public float TaxedAccel;
            /// <summary>ComponentSize fed into totalMass (HullMassReference).</summary>
            public float ComponentSize;
            public float OverdriveCapacityMult;
            public float OverdriveActiveMult;
            public float MovementMass;
            public float MaxForwardAccel;
            public float MaxBrake;
            public float RamAsteroidDamage;
            public float RamSelfDamage;
            public float RamRating;
        }

        /// <summary>
        /// Rebuilds the part list when chassis / equipment / level changed. Instantiates the chassis
        /// prefab once per change (same path as <see cref="ShipStatApplyLogic"/> store merge).
        /// </summary>
        /// <param name="em">Client visualization world EntityManager.</param>
        /// <param name="shipEntity">Local ship ghost.</param>
        /// <param name="chassisId">Resolved chassis id (e.g. AstroEagle_T3).</param>
        /// <param name="shipLevel">Current ship level for propulsion aggregation.</param>
        /// <param name="cache">In/out cache — left unchanged when key matches.</param>
        /// <returns>True when the cache is valid for tooltip builds.</returns>
        public static bool TryRefreshPartCache(
            EntityManager em,
            Entity shipEntity,
            string chassisId,
            int shipLevel,
            ref PartCache cache)
        {
            // --- Guards ---
            if (string.IsNullOrEmpty(chassisId) || shipEntity == Entity.Null || !em.Exists(shipEntity))
            {
                cache.Valid = false;
                return false;
            }

            int equipmentHash = ComputeStoreComponentHash(em, shipEntity);
            if (cache.Valid
                && cache.ChassisId == chassisId
                && cache.ShipLevel == shipLevel
                && cache.EquipmentHash == equipmentHash
                && cache.Ids != null
                && cache.Stats != null)
            {
                return true;
            }

            // --- Resolve family + tier prefab ---
            if (!ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family)
                || family == null)
            {
                cache.Valid = false;
                return false;
            }

            var config = ShipStatApplyLogic.Config;
            var tier = config != null ? config.GetTierEntryForChassisId(chassisId) : null;
            if (tier?.prefab == null)
            {
                cache.Valid = false;
                return false;
            }

            // --- Prefab sum (raw) + optional moon-store ShipComponent rows ---
            // [TITAN-ORBIT] Same merge as ShipStatApplyLogic.TryGetBaseStatsWithStoreComponents so
            // tooltip part lists match what the motor actually flies with.
            var extraIds = CollectStoreComponentIds(em, shipEntity);
            ShipFamilyStatsCalculator.SumResult sum =
                ShipFamilyStatsCalculator.SumFromPrefabHierarchy(
                    tier.prefab, family, shipLevel: 1, applyPropulsionAndWeaponRules: false);
            if (extraIds.Count > 0)
                sum = ShipFamilyStatsCalculator.AppendExtraComponentsAndAggregate(
                    sum, family, extraIds, shipLevel: 1);
            else
                ShipFamilyStatsCalculator.ApplySharedAggregationRules(ref sum, family, shipLevel: 1);

            if (cache.Ids == null)
                cache.Ids = new List<string>(8);
            else
                cache.Ids.Clear();
            if (cache.Stats == null)
                cache.Stats = new List<ShipComponentAbilityStats>(8);
            else
                cache.Stats.Clear();

            if (sum.MatchedComponentIds != null && sum.PerComponentStats != null)
            {
                int n = Mathf.Min(sum.MatchedComponentIds.Count, sum.PerComponentStats.Count);
                for (int i = 0; i < n; i++)
                {
                    cache.Ids.Add(sum.MatchedComponentIds[i]);
                    cache.Stats.Add(sum.PerComponentStats[i]);
                }
            }

            cache.Propulsion = ShipPropulsionAggregation.ComputeThrusterPropulsion(
                cache.Ids, cache.Stats, shipLevel);
            cache.Family = family;
            cache.ChassisId = chassisId;
            cache.ShipLevel = shipLevel;
            cache.EquipmentHash = equipmentHash;
            cache.Valid = cache.Ids.Count > 0;
            return cache.Valid;
        }

        /// <summary>
        /// Builds TMP rich text for one section. Safe to call every hover enter; returns a short
        /// fallback when the part cache is empty.
        /// </summary>
        public static string Build(SpeedometerStatSection section, in PartCache parts, in LiveContext live)
        {
            var sb = new StringBuilder(512);
            switch (section)
            {
                case SpeedometerStatSection.Speed:
                    AppendSpeedTooltip(sb, parts, live);
                    break;
                case SpeedometerStatSection.Accel:
                    AppendAccelTooltip(sb, parts, live);
                    break;
                case SpeedometerStatSection.Mass:
                    AppendMassTooltip(sb, parts, live);
                    break;
                case SpeedometerStatSection.Ram:
                    AppendRamTooltip(sb, parts, live);
                    break;
                case SpeedometerStatSection.Bullets:
                    AppendBulletsTooltip(sb, parts, live);
                    break;
            }

            return sb.Length > 0 ? sb.ToString() : "<color=#888888>No breakdown available</color>";
        }

        // --------------------------------------------------------------------------
        // Section builders
        // -------------------------------------------------------------------------

        /// <summary>SPD: propulsion parts + empty-hold / territory / load / OVERDRIVE stack.</summary>
        static void AppendSpeedTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "SPD — top speed");
            sb.AppendLine("<color=#AAAAAA>Primary thruster Move once; extras add ½ Speed/Lvl.</color>");

            // --- Per propulsion part ---
            int written = 0;
            if (parts.Valid && parts.Ids != null)
            {
                for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
                {
                    if (!ShipComponentAbilityStats.IsPropulsionComponent(parts.Ids[i]))
                        continue;

                    string name = ResolvePartName(parts.Family, parts.Ids[i]);
                    float baseMove = parts.Stats[i].moveSpeed;
                    float perLvl = parts.Stats[i].moveSpeedPerAbilityLevel;
                    bool primary = i == parts.Propulsion.primaryIndex;

                    if (primary)
                    {
                        sb.Append("• <color=#AAEEDD>").Append(name).Append("</color>  ");
                        sb.Append("+").Append(F1(baseMove)).Append(" Move <color=#888888>(primary)</color>");
                    }
                    else
                    {
                        float cruiseGain = Mathf.Max(0f, perLvl)
                            * ShipPropulsionAggregation.AdditionalPropulsionMoveSpeedPerLevelFactor;
                        sb.Append("• ").Append(name).Append("  ");
                        sb.Append("+").Append(F1(cruiseGain)).Append(" cruise");
                        if (baseMove > 0.05f)
                            sb.Append(" <color=#888888>(base ").Append(F1(baseMove)).Append(")</color>");
                    }

                    sb.AppendLine();
                    written++;
                }
            }

            if (written == 0)
                sb.AppendLine("<color=#888888>No propulsion parts matched.</color>");

            // --- Aggregation + chassis → mass tax → live ---
            sb.AppendLine();
            sb.Append("Pool top Move  <color=#AAEEDD>").Append(F1(parts.Propulsion.topMoveSpeed)).Append("</color>");
            sb.AppendLine();
            sb.Append("Chassis Move  ").Append(F1(live.ChassisMaxSpeed));
            sb.Append(" <color=#888888>(level + attrs)</color>").AppendLine();
            sb.Append("− totalMass ").Append(F1(live.TotalMass)).Append(" × SpeedWeight");
            sb.Append(" → ").Append(F1(live.CruiseMaxSpeed / Mathf.Max(0.001f, live.TerritoryMult))).AppendLine();

            if (live.TerritoryMult > 1.001f)
            {
                sb.Append("Territory  ×").Append(F1(live.TerritoryMult));
                sb.Append(" → cruise ").Append(F1(live.CruiseMaxSpeed)).AppendLine();
            }
            else
            {
                sb.Append("Cruise max  ").Append(F1(live.CruiseMaxSpeed)).AppendLine();
            }

            if (live.OverdriveCapacityMult > 1.001f)
            {
                sb.Append("<color=#FFCC66>OVERDRIVE cap ×")
                    .Append(F1(live.OverdriveCapacityMult))
                    .Append(" → bar ")
                    .Append(F1(live.BarMaxSpeed))
                    .Append("</color>")
                    .AppendLine();
            }

            if (live.OverdriveActiveMult > 1.001f)
            {
                sb.Append("<color=#FFCC66>Burst ON ×")
                    .Append(F1(live.OverdriveActiveMult))
                    .Append(" → live ")
                    .Append(F1(live.LiveMaxSpeed))
                    .Append("</color>")
                    .AppendLine();
            }

            sb.Append("Now  ").Append(F1(live.CurrentSpeed))
                .Append(" / ").Append(F1(live.LiveMaxSpeed));
        }

        /// <summary>ACC: summed propulsion accel → chassis → − totalMass × AccelWeightPerMass.</summary>
        static void AppendAccelTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "ACC — acceleration");
            sb.AppendLine("<color=#AAAAAA>Part Accel sums → chassis (level + attrs) → minus totalMass × AccelWeight.</color>");

            int written = 0;
            if (parts.Valid && parts.Ids != null)
            {
                for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
                {
                    if (!ShipComponentAbilityStats.IsPropulsionComponent(parts.Ids[i]))
                        continue;

                    float contrib = ShipPropulsionAggregation.GetPropulsionAccelerationContribution(
                        parts.Stats[i], Mathf.Max(0, parts.ShipLevel - 1));
                    if (contrib < 0.05f)
                        continue;

                    string name = ResolvePartName(parts.Family, parts.Ids[i]);
                    sb.Append("• ").Append(name).Append("  +")
                        .Append(F1(contrib)).Append(" Accel").AppendLine();
                    written++;
                }
            }

            if (written == 0)
                sb.AppendLine("<color=#888888>No propulsion accel parts matched.</color>");

            sb.AppendLine();
            sb.Append("Sum Accel  ").Append(F1(parts.Propulsion.sumAcceleration)).AppendLine();
            sb.Append("Chassis Accel  ").Append(F1(live.ChassisAccel));
            sb.Append(" <color=#888888>(level + attrs)</color>").AppendLine();
            sb.Append("− totalMass ").Append(F1(live.TotalMass)).Append(" × AccelWeight");
            sb.Append(" → <color=#40EB73>").Append(F1(live.MaxForwardAccel / Mathf.Max(0.001f, live.TerritoryMult * Mathf.Max(1f, live.OverdriveActiveMult)))).Append("</color>").AppendLine();
            sb.Append("Live max a  <color=#40EB73>").Append(F1(live.MaxForwardAccel)).Append("</color>");
            if (live.TerritoryMult > 1.001f || live.OverdriveActiveMult > 1.001f)
                sb.Append(" <color=#888888>(× territory / OD)</color>");
            sb.AppendLine();
            sb.Append("Brake  ").Append(F1(live.MaxBrake)).Append("/s");
        }

        /// <summary>MASS: totalMass for mobility tax (gems + people + ComponentSize).</summary>
        static void AppendMassTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "MASS — totalMass (mobility tax)");
            sb.AppendLine("<color=#AAAAAA>totalMass = gems×MassPerGem + people×MassPerPerson + size×MassPerComponentSize</color>");

            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float mGem = settings != null ? settings.massPerGem : 0.01f;
            float mPerson = settings != null ? settings.massPerPerson : 0.15f;
            float mSize = settings != null ? settings.massPerComponentSize : 1f;

            float gemMass = live.Ship.CurrentGems * mGem;
            float peopleMass = live.Ship.CurrentPeople * mPerson;
            float sizeMass = live.ComponentSize * mSize;

            sb.Append("Gems  ").Append(F0(live.Ship.CurrentGems))
                .Append(" × ").Append(F2(mGem))
                .Append(" = ").Append(F2(gemMass)).AppendLine();
            sb.Append("People  ").Append(F0(live.Ship.CurrentPeople))
                .Append(" × ").Append(F2(mPerson))
                .Append(" = ").Append(F2(peopleMass)).AppendLine();
            sb.Append("ComponentSize  ").Append(F1(live.ComponentSize))
                .Append(" × ").Append(F2(mSize))
                .Append(" = ").Append(F2(sizeMass)).AppendLine();
            // Same totalMass the MASS line prints (and that Speed/Accel/Turn tax uses).
            float sumParts = gemMass + peopleMass + sizeMass;
            sb.Append("totalMass  <color=#AAEEDD>").Append(F1(live.TotalMass)).Append("</color>");
            if (Mathf.Abs(sumParts - live.TotalMass) > 0.05f)
                sb.Append(" <color=#888888>(parts ").Append(F1(sumParts)).Append(")</color>");
            sb.AppendLine();

            if (settings != null)
            {
                sb.AppendLine();
                sb.Append("Speed drag  −").Append(F1(live.TotalMass * settings.speedWeightPerMass)).AppendLine();
                sb.Append("Accel drag  −").Append(F1(live.TotalMass * settings.accelWeightPerMass)).AppendLine();
                sb.Append("Turn drag  −").Append(F1(live.TotalMass * settings.turnWeightPerMass)).Append("/s");
            }

            // --- Optional: list non-cosmetic hullish parts as structure contributors ---
            if (parts.Valid && parts.Ids != null && parts.Ids.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("<color=#888888>Chassis parts (ComponentSize from prefab scales):</color>");
                int shown = 0;
                for (int i = 0; i < parts.Ids.Count && shown < 8; i++)
                {
                    string id = parts.Ids[i];
                    if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                        continue;
                    sb.Append("• ").Append(ResolvePartName(parts.Family, id)).AppendLine();
                    shown++;
                }

                if (parts.Ids.Count > shown)
                    sb.Append("<color=#888888>… +")
                        .Append(parts.Ids.Count - shown)
                        .Append(" more</color>");
            }
        }

        /// <summary>RAM: parts with rammingPower + live impact estimate (rating × totalMass × speed).</summary>
        static void AppendRamTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "RAM — impact damage");
            sb.AppendLine("<color=#AAAAAA>Impact = rating × totalMass × closing speed (after-tax flight).</color>");
            sb.AppendLine("<color=#AAAAAA>Grind = rating × totalMass × taxed Accel × pulse (while thrusting into rock).</color>");

            int written = 0;
            float sumRam = 0f;
            if (parts.Valid && parts.Ids != null)
            {
                for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
                {
                    float ram = parts.Stats[i].rammingPower;
                    if (ram < 0.05f)
                        continue;

                    string name = ResolvePartName(parts.Family, parts.Ids[i]);
                    sb.Append("• ").Append(name).Append("  +")
                        .Append(F1(ram)).Append(" Ramming").AppendLine();
                    sumRam += ram;
                    written++;
                }
            }

            if (written == 0)
                sb.AppendLine("<color=#888888>No parts author Ramming (family fallback may apply).</color>");
            else
                sb.Append("Sum (level-1)  ").Append(F1(sumRam)).AppendLine();

            float familyRam = live.Motor.RammingPower > 0f
                ? live.Motor.RammingPower
                : live.EffectiveStats.rammingPower;
            sb.AppendLine();
            sb.Append("Motor Ramming  ").Append(F1(familyRam)).AppendLine();
            sb.Append("Rating  ").Append(F1(live.RamRating)).AppendLine();
            sb.Append("totalMass  ").Append(F1(live.TotalMass)).AppendLine();
            sb.Append("Taxed Accel  ").Append(F1(live.TaxedAccel));
            sb.Append(" <color=#888888>(grind lever)</color>").AppendLine();
            sb.Append("At ").Append(F1(live.CurrentSpeed)).Append("/s → ");
            sb.Append("ast <color=#FFAA66>").Append(F1(live.RamAsteroidDamage)).Append("</color>  ");
            sb.Append("hull <color=#FF6666>").Append(F1(live.RamSelfDamage)).Append("</color>");
        }

        /// <summary>BUL: weapon parts + hull-average config the HUD shows.</summary>
        static void AppendBulletsTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "BUL — weapons");
            sb.AppendLine("<color=#AAAAAA>HUD shows hull averages. Each mount still fires its own Fire Power.</color>");

            int written = 0;
            if (parts.Valid && parts.Ids != null)
            {
                for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
                {
                    if (!ShipComponentAbilityStats.IsWeaponComponent(parts.Ids[i]))
                        continue;

                    var s = parts.Stats[i];
                    if (s.firePower < 0.05f && s.fireRate < 0.01f)
                        continue;

                    string name = ResolvePartName(parts.Family, parts.Ids[i]);
                    sb.Append("• ").Append(name).Append("  ");
                    if (s.firePower >= 0.05f)
                        sb.Append("+").Append(F1(s.firePower)).Append(" Fire  ");
                    if (s.fireRate >= 0.01f)
                        sb.Append(F1(s.fireRate)).Append("/s  ");
                    if (s.bulletSpeed >= 0.05f)
                        sb.Append("spd ").Append(F1(s.bulletSpeed));
                    sb.AppendLine();
                    written++;
                }
            }

            if (written == 0)
                sb.AppendLine("<color=#888888>No weapon parts matched.</color>");

            sb.AppendLine();
            float dps = live.Weapon.BulletDamage * live.Weapon.FireRate;
            sb.Append("Hull avg  ").Append(F1(live.Weapon.BulletDamage)).Append("/hit  ");
            sb.Append(F1(dps)).Append("/s  ");
            sb.Append("<color=#888888>").Append(F1(live.Weapon.FireRate)).Append("/s</color>");
        }

        // --------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        static void AppendHeader(StringBuilder sb, string title)
        {
            sb.Append("<b>").Append(title).Append("</b>").AppendLine();
            sb.AppendLine();
        }

        /// <summary>Moon-dock display name, or formatted component id.</summary>
        static string ResolvePartName(ShipFamilyDefinition family, string componentId)
        {
            if (family != null
                && family.TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry)
                && entry != null)
            {
                return ShipComponentStoreData.GetDisplayName(entry);
            }

            return ShipComponentStoreData.FormatComponentId(componentId);
        }

        /// <summary>Collects equipped moon-store ShipComponent ids (not cards / consumables).</summary>
        static List<string> CollectStoreComponentIds(EntityManager em, Entity shipEntity)
        {
            var ids = new List<string>(4);
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                return ids;

            var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            for (int i = 0; i < buf.Length; i++)
            {
                var e = buf[i];
                if ((StoreItemType)e.ItemType != StoreItemType.ShipComponent)
                    continue;
                string id = e.ComponentId.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }

            return ids;
        }

        /// <summary>Stable hash of store ShipComponent rows for cache invalidation.</summary>
        static int ComputeStoreComponentHash(EntityManager em, Entity shipEntity)
        {
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                return 0;

            var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            int hash = 17;
            for (int i = 0; i < buf.Length; i++)
            {
                var e = buf[i];
                if ((StoreItemType)e.ItemType != StoreItemType.ShipComponent)
                    continue;
                hash = hash * 31 + e.ComponentId.GetHashCode();
                hash = hash * 31 + e.ItemLevel;
            }

            return hash;
        }

        static string F0(float v) =>
            v.ToString("0", CultureInfo.InvariantCulture);

        static string F1(float v) =>
            v.ToString("0.0", CultureInfo.InvariantCulture);

        static string F2(float v) =>
            v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
