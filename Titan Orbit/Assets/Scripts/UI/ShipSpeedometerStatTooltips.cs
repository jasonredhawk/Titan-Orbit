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
    /// <para>
    /// [TITAN-ORBIT] Tip bodies are static capacity / max-impact snapshots. The HUD rebuilds them
    /// on hover-enter or when chassis / ability levels change — not every LateUpdate with live speed.
    /// </para>
    /// </summary>
    public static class ShipSpeedometerStatTooltips
    {
        /// <summary>Reused tip builder (main-thread UI only) to cut GC on hover rebuilds.</summary>
        static readonly StringBuilder s_BuildSb = new StringBuilder(512);
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

            /// <summary>Propulsion pool at ship level (primary ×1 + extras × extraStackWeight of own stats).</summary>
            public ShipPropulsionAggregation.Result Propulsion;
        }

        /// <summary>
        /// Static motor / capacity numbers for tip and ability-chip rebuilds.
        /// Filled when chassis / ability levels change (or on tip hover), not every flight frame.
        /// Ram fields are max impact at full cruise — not current closing speed.
        /// </summary>
        public struct LiveContext
        {
            public ShipState Ship;
            public ShipMotorConfig Motor;
            public ShipComponentAbilityStats EffectiveStats;
            public ShipWeaponConfig Weapon;
            /// <summary>Unused for tip copy (kept so older call sites compile); prefer cruise/bar max.</summary>
            public float CurrentSpeed;
            /// <summary>Cruise ceiling used as “full speed” for max-impact ram estimates.</summary>
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
            /// <summary>
            /// After-tax turn °/s (mobility tax only — no territory / OVERDRIVE).
            /// Quick-stat TS chip shows this; chassis turn stays in <see cref="ChassisTurnDeg"/>.
            /// </summary>
            public float TaxedTurnDeg;
            /// <summary>ComponentSize fed into totalMass (HullMassReference).</summary>
            public float ComponentSize;
            public float OverdriveCapacityMult;
            public float OverdriveActiveMult;
            public float MovementMass;
            public float MaxForwardAccel;
            public float MaxBrake;
            /// <summary>Asteroid impact at full cruise (static max).</summary>
            public float RamAsteroidDamage;
            /// <summary>Self damage at full cruise (static max).</summary>
            public float RamSelfDamage;
            public float RamRating;
            /// <summary>
            /// Bottom-HUD Move Speed ability purchases (adds move + accel PerAbilityLevel steps).
            /// </summary>
            public int MoveSpeedAbilityLevel;

            /// <summary>
            /// Preview for next Move Speed purchase (aggregated moveSpeedPerAbilityLevel step).
            /// </summary>
            public float MoveStepPreview;
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
        /// Builds TMP rich text for one section. Call on hover-enter or snapshot dirty — not every frame.
        /// Returns a short fallback when the part cache is empty.
        /// </summary>
        public static string Build(SpeedometerStatSection section, in PartCache parts, in LiveContext live)
        {
            StringBuilder sb = s_BuildSb;
            sb.Clear();
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

        /// <summary>SPD: propulsion parts + mass tax + static cruise / OD capacity ceilings.</summary>
        static void AppendSpeedTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "SPD — top speed");
            sb.AppendLine("<color=#5B7A94>See Move Speed chip for full ability pipeline. Capacity values are static ceilings.</color>");
            ShipAbilityStatBreakdown.AppendGroupedFieldGrid(
                sb, parts, ShipAbilityStatBreakdown.StatField.MoveSpeed, "Move", useStackWeight: true);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "CHASSIS", "7EC8FF");
            AppendChassisMoveBreakdown(sb, parts, live);
            AppendMassTaxEffectsBreakdown(sb, in live, includeMove: true, includeAccel: false);

            // --- Static ceilings (no current flight speed) ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "CAPACITY", "7EC8FF");
            float cruise = live.CruiseMaxSpeed > 0.01f ? live.CruiseMaxSpeed : live.ChassisMaxSpeed;
            sb.Append("Cruise max  ").Append(FResult(cruise)).AppendLine();
            if (live.OverdriveCapacityMult > 1.001f)
            {
                sb.Append("<color=#FFCC66>OVERDRIVE cap x")
                    .Append(FDetail(live.OverdriveCapacityMult))
                    .Append(" -> bar ")
                    .Append(FResult(live.BarMaxSpeed))
                    .Append("</color>");
            }
        }

        /// <summary>ACC: primary Accel ×1 + extras × weight of their Accel → chassis → mass tax.</summary>
        static void AppendAccelTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "ACC — acceleration");
            sb.AppendLine("<color=#5B7A94>See Move Speed chip for ability steps. Capacity values are static ceilings.</color>");
            ShipAbilityStatBreakdown.AppendGroupedFieldGrid(
                sb, parts, ShipAbilityStatBreakdown.StatField.AccelerationCap, "Accel", useStackWeight: true);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "CHASSIS", "7EC8FF");
            AppendChassisAccelBreakdown(sb, parts, live);
            AppendMassTaxEffectsBreakdown(sb, in live, includeMove: false, includeAccel: true);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "CAPACITY", "7EC8FF");
            sb.Append("Max thrust  <color=#40EB73>").Append(FResult(live.MaxForwardAccel)).Append("</color>");
            if (live.TerritoryMult > 1.001f)
                sb.Append(" <color=#5B7A94>(x territory)</color>");
            sb.AppendLine();
            sb.Append("Brake  ").Append(FResult(live.MaxBrake)).Append("/s");
        }

        /// <summary>MASS: totalMass for mobility tax (gems + people + ComponentSize).</summary>
        static void AppendMassTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "MASS — totalMass (mobility tax)");
            ShipStatTooltipChrome.AppendSectionBanner(sb, "BREAKDOWN", "C9A0FF");
            sb.AppendLine("<color=#5B7A94>totalMass = gems x MassPerGem + people x MassPerPerson + size x MassPerComponentSize</color>");

            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float mGem = settings != null ? settings.massPerGem : 0.01f;
            float mPerson = settings != null ? settings.massPerPerson : 0.15f;
            float mSize = settings != null ? settings.massPerComponentSize : 1f;

            float gemMass = live.Ship.CurrentGems * mGem;
            float peopleMass = live.Ship.CurrentPeople * mPerson;
            float sizeMass = live.ComponentSize * mSize;

            sb.Append("Gems  ").Append(F0(live.Ship.CurrentGems))
                .Append(" x ").Append(F2(mGem))
                .Append(" = ").Append(F2(gemMass)).AppendLine();
            sb.Append("People  ").Append(F0(live.Ship.CurrentPeople))
                .Append(" x ").Append(F2(mPerson))
                .Append(" = ").Append(F2(peopleMass)).AppendLine();
            sb.Append("ComponentSize  ").Append(F1(live.ComponentSize))
                .Append(" x ").Append(F2(mSize))
                .Append(" = ").Append(F2(sizeMass)).AppendLine();
            // Same totalMass the MASS line prints (and that Speed/Accel/Turn tax uses).
            float sumParts = gemMass + peopleMass + sizeMass;
            sb.Append("totalMass  <color=#AAEEDD>").Append(F1(live.TotalMass)).Append("</color>");
            if (Mathf.Abs(sumParts - live.TotalMass) > 0.05f)
                sb.Append(" <color=#5B7A94>(parts ").Append(F1(sumParts)).Append(")</color>");
            sb.AppendLine();

            if (settings != null)
            {
                ShipStatTooltipChrome.AppendSectionBanner(sb, "DRAG", "7EC8FF");
                sb.Append("Speed drag  -").Append(F1(live.TotalMass * settings.speedWeightPerMass)).AppendLine();
                sb.Append("Accel drag  -").Append(F1(live.TotalMass * settings.accelWeightPerMass)).AppendLine();
                sb.Append("Turn drag  -").Append(F1(live.TotalMass * settings.turnWeightPerMass)).Append("/s");
            }

            // --- Optional: list non-cosmetic hullish parts as structure contributors ---
            if (parts.Valid && parts.Ids != null && parts.Ids.Count > 0)
            {
                ShipStatTooltipChrome.AppendSectionBanner(sb, "CHASSIS", "5B7A94");
                int shown = 0;
                for (int i = 0; i < parts.Ids.Count && shown < 8; i++)
                {
                    string id = parts.Ids[i];
                    if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                        continue;
                    sb.Append("- ").Append(ResolvePartName(parts.Family, id)).AppendLine();
                    shown++;
                }

                if (parts.Ids.Count > shown)
                    sb.Append("<color=#5B7A94>+")
                        .Append(parts.Ids.Count - shown)
                        .Append(" more</color>");
            }
        }

        /// <summary>RAM: parts with rammingPower + max impact at full cruise (rating × mass × cruise).</summary>
        static void AppendRamTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "RAM — impact damage");
            ShipStatTooltipChrome.AppendSectionBanner(sb, "PARTS", "FFAA66");
            sb.AppendLine("<color=#5B7A94>Impact = rating x totalMass x closing speed (after-tax flight).</color>");
            sb.AppendLine("<color=#5B7A94>Grind = rating x totalMass x taxed Accel x pulse (while thrusting into rock).</color>");

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
                    sb.Append("- ").Append(name).Append("  +")
                        .Append(F1(ram)).Append(" Ramming").AppendLine();
                    sumRam += ram;
                    written++;
                }
            }

            if (written == 0)
                sb.AppendLine("<color=#5B7A94>No parts author Ramming (family fallback may apply).</color>");
            else
                sb.Append("Sum (level-1)  ").Append(F1(sumRam)).AppendLine();

            float familyRam = live.Motor.RammingPower > 0f
                ? live.Motor.RammingPower
                : live.EffectiveStats.rammingPower;
            float fullCruise = live.CruiseMaxSpeed > 0.01f ? live.CruiseMaxSpeed : live.ChassisMaxSpeed;
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MAX IMPACT", "FFCC66");
            sb.Append("Motor Ramming  ").Append(F1(familyRam)).AppendLine();
            sb.Append("Rating  ").Append(F1(live.RamRating)).AppendLine();
            sb.Append("totalMass  ").Append(F1(live.TotalMass)).AppendLine();
            sb.Append("Taxed Accel  ").Append(F1(live.TaxedAccel));
            sb.Append(" <color=#5B7A94>(grind lever)</color>").AppendLine();
            sb.Append("At full cruise  ").Append(F1(fullCruise)).Append("/s -> ");
            sb.Append("ast <color=#FFAA66>").Append(F1(live.RamAsteroidDamage)).Append("</color>  ");
            sb.Append("hull <color=#FF6666>").Append(F1(live.RamSelfDamage)).Append("</color>");
        }

        /// <summary>BUL: weapon parts + hull-average config the HUD shows.</summary>
        static void AppendBulletsTooltip(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            AppendHeader(sb, "BUL — weapons");
            ShipStatTooltipChrome.AppendSectionBanner(sb, "WEAPONS", "FF8A5B");
            sb.AppendLine("<color=#5B7A94>HUD shows hull averages. Each mount still fires its own Fire Power.</color>");

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
                    sb.Append("- ").Append(name).Append("  ");
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
                sb.AppendLine("<color=#5B7A94>No weapon parts matched.</color>");

            ShipStatTooltipChrome.AppendSectionBanner(sb, "HULL AVG", "7EC8FF");
            float dps = live.Weapon.BulletDamage * live.Weapon.FireRate;
            sb.Append("Hull avg  ").Append(F1(live.Weapon.BulletDamage)).Append("/hit  ");
            sb.Append(F1(dps)).Append("/s  ");
            sb.Append("<color=#5B7A94>").Append(F1(live.Weapon.FireRate)).Append("/s</color>");
        }

        // --------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Expanded totalMass composition + subtractive drag on Move / Accel.
        /// Used by Move Speed ability tips and SPD/ACC speedometer tips so the old one-liner
        /// "- totalMass X tax -> cruise Y" is replaced with a readable pipeline.
        /// </summary>
        /// <param name="sb">Tip string builder.</param>
        /// <param name="live">Live motor / cargo numbers from the HUD.</param>
        /// <param name="includeMove">When true, show chassis Move - speed drag -> cruise.</param>
        /// <param name="includeAccel">When true, show chassis Accel - accel drag -> taxed Accel.</param>
        public static void AppendMassTaxEffectsBreakdown(
            StringBuilder sb,
            in LiveContext live,
            bool includeMove,
            bool includeAccel)
        {
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", "C9A0FF");

            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float mGem = settings != null ? settings.massPerGem : 0.01f;
            float mPerson = settings != null ? settings.massPerPerson : 0.15f;
            float mSize = settings != null ? settings.massPerComponentSize : 1f;
            float speedW = settings != null ? settings.speedWeightPerMass : 0.1f;
            float accelW = settings != null ? settings.accelWeightPerMass : 0.1f;
            float minSpeed = settings != null ? settings.minSpeed : 0.1f;
            float minAccel = settings != null ? settings.minAccel : 0.1f;

            float gemMass = live.Ship.CurrentGems * mGem;
            float peopleMass = live.Ship.CurrentPeople * mPerson;
            float sizeMass = live.ComponentSize * mSize;

            sb.AppendLine("<color=#5B7A94>totalMass = cargo + hull size (subtracts from chassis Move/Accel)</color>");
            sb.Append("Gems  ").Append(F0(live.Ship.CurrentGems))
                .Append(" x ").Append(F2(mGem))
                .Append(" = ").Append(F2(gemMass)).AppendLine();
            sb.Append("People  ").Append(F0(live.Ship.CurrentPeople))
                .Append(" x ").Append(F2(mPerson))
                .Append(" = ").Append(F2(peopleMass)).AppendLine();
            sb.Append("Hull size  ").Append(F1(live.ComponentSize))
                .Append(" x ").Append(F2(mSize))
                .Append(" = ").Append(F2(sizeMass)).AppendLine();
            sb.Append("totalMass  <color=#AAEEDD>").Append(F1(live.TotalMass)).Append("</color>")
                .AppendLine();

            ShipStatTooltipChrome.AppendSubDivider(sb);

            float speedDrag = live.TotalMass * speedW;
            float accelDrag = live.TotalMass * accelW;

            if (includeMove)
            {
                // Cruise before territory = chassis - speed drag (floored).
                float cruisePreTerritory = Mathf.Max(
                    minSpeed,
                    live.ChassisMaxSpeed - speedDrag);
                // Prefer live cruise when territory is 1 so we stay honest to the HUD.
                float cruiseShown = live.TerritoryMult > 1.001f
                    ? live.CruiseMaxSpeed / Mathf.Max(0.001f, live.TerritoryMult)
                    : live.CruiseMaxSpeed;

                sb.Append("Speed drag  totalMass x ").Append(F2(speedW))
                    .Append(" = <color=#FFAA66>-").Append(F1(speedDrag)).Append("</color>")
                    .AppendLine();
                sb.Append("Chassis Move  ").Append(FResult(live.ChassisMaxSpeed))
                    .Append(" - ").Append(F1(speedDrag))
                    .Append(" -> cruise <color=#AAEEDD>").Append(FResult(cruiseShown)).Append("</color>")
                    .AppendLine();
                if (Mathf.Abs(cruiseShown - cruisePreTerritory) > 0.05f)
                {
                    sb.Append("<color=#5B7A94>(floor/clamp ").Append(FResult(cruisePreTerritory))
                        .Append(")</color>").AppendLine();
                }

                if (live.TerritoryMult > 1.001f)
                {
                    sb.Append("Territory  x").Append(FDetail(live.TerritoryMult))
                        .Append(" -> cruise ").Append(FResult(live.CruiseMaxSpeed)).AppendLine();
                }
            }

            if (includeAccel)
            {
                float taxed = Mathf.Max(minAccel, live.ChassisAccel - accelDrag);
                // Prefer live TaxedAccel when available (matches grind lever / motor).
                float taxedShown = live.TaxedAccel > 0.01f ? live.TaxedAccel : taxed;

                sb.Append("Accel drag  totalMass x ").Append(F2(accelW))
                    .Append(" = <color=#FFAA66>-").Append(F1(accelDrag)).Append("</color>")
                    .AppendLine();
                sb.Append("Chassis Accel  ").Append(FResult(live.ChassisAccel))
                    .Append(" - ").Append(F1(accelDrag))
                    .Append(" -> <color=#40EB73>").Append(FResult(taxedShown)).Append("</color>")
                    .AppendLine();
            }
        }

        /// <summary>
        /// Level-1 propulsion pool: primary Move/Accel ×1 + each extra × extraStackWeight of its own stats.
        /// </summary>
        static bool TryResolvePoolL1(in PartCache parts, out float moveL1, out float accelL1, out int extras)
        {
            moveL1 = 0f;
            accelL1 = 0f;
            extras = 0;
            if (!parts.Valid
                || parts.Ids == null
                || parts.Stats == null
                || parts.Propulsion.primaryIndex < 0
                || parts.Propulsion.primaryIndex >= parts.Stats.Count
                || parts.Propulsion.propulsionCount <= 0)
            {
                return false;
            }

            int primaryIdx = parts.Propulsion.primaryIndex;
            for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
            {
                if (!ShipComponentAbilityStats.IsPropulsionComponent(parts.Ids[i]))
                    continue;

                bool primary = i == primaryIdx;
                float weight = primary
                    ? 1f
                    : ShipComponentStackAggregation.ResolveExtraStackWeight(parts.Stats[i], parts.Ids[i]);
                if (!primary)
                    extras++;

                moveL1 += Mathf.Max(0f, parts.Stats[i].moveSpeed) * weight;
                accelL1 += Mathf.Max(
                    0f,
                    ShipPropulsionAggregation.GetPropulsionAccelerationContribution(parts.Stats[i], 0)) * weight;
            }

            return moveL1 > 0.01f || accelL1 > 0.01f;
        }

        /// <summary>
        /// Formats an extra's stack weight for tooltip copy (e.g. ×10%, ×25%, ×100%).
        /// </summary>
        static string FormatStackWeightLabel(float weight)
        {
            float pct = weight * 100f;
            if (Mathf.Abs(pct - 10f) < 0.05f)
                return "×10%";
            if (Mathf.Abs(pct - Mathf.Round(pct)) < 0.05f)
                return "×" + Mathf.RoundToInt(pct).ToString(CultureInfo.InvariantCulture) + "%";
            return "×" + pct.ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// Chassis Move pipeline: pool L1 → ship-tier growth → level mobility drag → Move Speed ability.
        /// Final line is the live chassis MaxSpeed the HUD taxes from.
        /// </summary>
        static void AppendChassisMoveBreakdown(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            int shipLevel = Mathf.Max(1, live.Ship.ShipLevel);
            int perLvl = Mathf.Max(0, shipLevel - 1);
            float growth = parts.Family != null
                ? parts.Family.ResolveShipLevelStatGrowthFraction()
                : ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction;
            float growthPct = growth * 100f;

            if (!TryResolvePoolL1(parts, out float moveL1, out _, out int extras))
            {
                sb.Append("Chassis Move  ").Append(FResult(live.ChassisMaxSpeed)).AppendLine();
                return;
            }

            // --- Pool at level 1 (weighted own-stats) — full float math, detail display ---
            sb.Append("Pool Move  <color=#AAEEDD>").Append(FDetail(moveL1)).Append("</color>");
            if (extras > 0)
            {
                sb.Append(" <color=#888888>(primary + ")
                    .Append(extras)
                    .Append("× weighted extras)</color>");
            }

            sb.AppendLine();

            // --- Ship-tier growth ---
            float afterTier = moveL1 * (1f + perLvl * growth);
            if (perLvl > 0)
            {
                sb.Append("Ship Lv ").Append(shipLevel)
                    .Append("  +")
                    .Append(F0(growthPct))
                    .Append("% ×")
                    .Append(perLvl)
                    .Append(" → ")
                    .Append(FDetail(afterTier))
                    .AppendLine();
            }
            else
            {
                sb.Append("Ship Lv 1  ").Append(FDetail(afterTier))
                    .Append(" <color=#888888>(no tier growth yet)</color>")
                    .AppendLine();
            }

            // --- Optional level MaxSpeed drag from mobility settings ---
            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float speedPenalty = mobility != null ? mobility.levelMaxSpeedPenaltyFractionPerLevel : 0f;
            float afterDrag = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                afterTier, perLvl, speedPenalty);
            if (perLvl > 0 && speedPenalty > 0.0001f && !Mathf.Approximately(afterDrag, afterTier))
            {
                sb.Append("Level speed drag  −")
                    .Append(F0(speedPenalty * 100f))
                    .Append("%/Lv → ")
                    .Append(FDetail(afterDrag))
                    .AppendLine();
            }

            // --- Move Speed ability step (how +Move per purchase is built) ---
            AppendMoveSpeedAbilityBreakdown(sb, parts, live, forAccel: false, moveL1);
            sb.Append("<b>Chassis Move</b>  ").Append(FResult(live.ChassisMaxSpeed)).AppendLine();
        }

        /// <summary>
        /// Chassis Accel pipeline: pool L1 → ship-tier growth → level accel drag → Move Speed ability.
        /// </summary>
        static void AppendChassisAccelBreakdown(StringBuilder sb, in PartCache parts, in LiveContext live)
        {
            int shipLevel = Mathf.Max(1, live.Ship.ShipLevel);
            int perLvl = Mathf.Max(0, shipLevel - 1);
            float growth = parts.Family != null
                ? parts.Family.ResolveShipLevelStatGrowthFraction()
                : ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction;
            float growthPct = growth * 100f;

            if (!TryResolvePoolL1(parts, out float moveL1, out float accelL1, out int extras))
            {
                sb.Append("Chassis Accel  ").Append(FResult(live.ChassisAccel)).AppendLine();
                return;
            }

            sb.Append("Pool Accel  <color=#AAEEDD>").Append(FDetail(accelL1)).Append("</color>");
            if (extras > 0)
                sb.Append(" <color=#888888>(primary + ").Append(extras).Append("× weighted extras)</color>");
            sb.AppendLine();

            float afterTier = accelL1 * (1f + perLvl * growth);
            if (perLvl > 0)
            {
                sb.Append("Ship Lv ").Append(shipLevel)
                    .Append("  +")
                    .Append(F0(growthPct))
                    .Append("% ×")
                    .Append(perLvl)
                    .Append(" → ")
                    .Append(FDetail(afterTier))
                    .AppendLine();
            }
            else
            {
                sb.Append("Ship Lv 1  ").Append(FDetail(afterTier))
                    .Append(" <color=#888888>(no tier growth yet)</color>")
                    .AppendLine();
            }

            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float accelPenalty = mobility != null ? mobility.levelAccelPenaltyFractionPerLevel : 0f;
            float afterDrag = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                afterTier, perLvl, accelPenalty);
            if (perLvl > 0 && accelPenalty > 0.0001f && !Mathf.Approximately(afterDrag, afterTier))
            {
                sb.Append("Level accel drag  −")
                    .Append(F0(accelPenalty * 100f))
                    .Append("%/Lv → ")
                    .Append(FDetail(afterDrag))
                    .AppendLine();
            }

            // --- Move Speed ability step (how +Accel per purchase is built) ---
            AppendMoveSpeedAbilityBreakdown(sb, parts, live, forAccel: true, moveL1);
            sb.Append("<b>Chassis Accel</b>  ").Append(FResult(live.ChassisAccel)).AppendLine();
        }

        /// <summary>
        /// Explains one Move Speed ability purchase step: each propulsion part's PerAbilityLevel,
        /// primary at 100% + extras at <c>extraStackWeight</c> of <b>their</b> authored step, then Lv × step.
        /// Same purchase adds Move and Accel together — <paramref name="forAccel"/> picks which step to show.
        /// </summary>
        /// <param name="forAccel">True = Accel/Lvl step; false = Move/Lvl step.</param>
        /// <param name="moveL1">Level-1 pool Move for fallback when PerAbilityLevel is unset.</param>
        static void AppendMoveSpeedAbilityBreakdown(
            StringBuilder sb,
            in PartCache parts,
            in LiveContext live,
            bool forAccel,
            float moveL1)
        {
            sb.AppendLine("<color=#AAAAAA>Move Speed ability: primary PerAbilityLevel at 100%; each extra adds its own step × extraStackWeight.</color>");

            float stepTotal = 0f;
            int lineCount = 0;
            bool usedFallback = false;

            if (parts.Valid && parts.Ids != null && parts.Stats != null)
            {
                for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
                {
                    if (!ShipComponentAbilityStats.IsPropulsionComponent(parts.Ids[i]))
                        continue;

                    ShipComponentAbilityStats comp = parts.Stats[i];
                    bool primary = i == parts.Propulsion.primaryIndex;
                    float weight = primary
                        ? 1f
                        : ShipComponentStackAggregation.ResolveExtraStackWeight(in comp, parts.Ids[i]);

                    float authored;
                    string unitLabel;
                    if (forAccel)
                    {
                        authored = Mathf.Max(0f, comp.accelerationCapPerAbilityLevel);
                        if (authored <= 0.0001f && comp.moveSpeedPerAbilityLevel > 0.0001f)
                        {
                            // Same derivation as ShipPropulsionAggregation when Accel/Lvl is blank.
                            authored = comp.moveSpeedPerAbilityLevel
                                * ShipPropulsionAggregation.SuggestedPropulsionAccelerationFractionOfMoveSpeed;
                        }

                        unitLabel = "Accel/Lvl";
                    }
                    else
                    {
                        authored = Mathf.Max(0f, comp.moveSpeedPerAbilityLevel);
                        unitLabel = "Move/Lvl";
                    }

                    if (authored <= 0.0001f)
                        continue;

                    float weighted = authored * weight;
                    stepTotal += weighted;
                    lineCount++;

                    string name = ResolvePartName(parts.Family, parts.Ids[i]);
                    sb.Append("  • ");
                    if (primary)
                        sb.Append("<color=#AAEEDD>").Append(name).Append("</color>");
                    else
                        sb.Append(name);

                    sb.Append("  ").Append(FDetail(authored)).Append(" ").Append(unitLabel);
                    if (primary)
                        sb.Append(" ×100%");
                    else
                        sb.Append(" ").Append(FormatStackWeightLabel(weight));
                    sb.Append(" = +").Append(FDetail(weighted)).AppendLine();
                }
            }

            // --- Fallback when no part authored PerAbilityLevel (Scan fraction of pool) ---
            if (stepTotal <= 0.0001f)
            {
                usedFallback = true;
                ShipAttributeUpgradeLogic.ResolveMoveSpeedAbilitySteps(
                    BuildLevelOneSumForSteps(parts, moveL1, out _),
                    out float moveStep,
                    out float accelStep,
                    out _);
                stepTotal = forAccel ? accelStep : moveStep;
                float fracPct = ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase * 100f;
                sb.Append("  <color=#888888>No authored PerAbilityLevel — fallback ")
                    .Append(F0(fracPct))
                    .Append("% of pool → +")
                    .Append(FDetail(stepTotal))
                    .Append("/purchase</color>")
                    .AppendLine();
            }
            else if (lineCount > 1)
            {
                sb.Append("  Step/purchase  <color=#AAEEDD>")
                    .Append(FDetail(stepTotal))
                    .Append("</color>")
                    .AppendLine();
            }

            // Prefer aggregated propulsion totals when present (matches motor apply).
            if (!usedFallback)
            {
                float aggregated = forAccel
                    ? parts.Propulsion.accelerationCapPerAbilityLevel
                    : parts.Propulsion.moveSpeedPerAbilityLevel;
                if (aggregated > 0.0001f)
                    stepTotal = aggregated;
            }

            int abilityLv = Mathf.Max(0, live.MoveSpeedAbilityLevel);
            float attrAdd = abilityLv * Mathf.Max(0f, stepTotal);
            string statWord = forAccel ? "Accel" : "Move";

            if (abilityLv > 0 && stepTotal > 0.0001f)
            {
                sb.Append("Purchased  Lv")
                    .Append(abilityLv)
                    .Append(" × +")
                    .Append(FDetail(stepTotal))
                    .Append(" ")
                    .Append(statWord)
                    .Append(" → +")
                    .Append(FResult(attrAdd))
                    .AppendLine();
            }
            else
            {
                sb.Append("Purchased  <color=#888888>0</color>  (step still +")
                    .Append(FDetail(stepTotal))
                    .Append(" ")
                    .Append(statWord)
                    .Append("/buy)")
                    .AppendLine();
            }
        }

        /// <summary>
        /// Minimal level-1 sum for <see cref="ShipAttributeUpgradeLogic.ResolveMoveSpeedAbilitySteps"/>
        /// fallbacks when PerAbilityLevel fields are still 0.
        /// </summary>
        static ShipComponentAbilityStats BuildLevelOneSumForSteps(
            in PartCache parts,
            float moveL1,
            out float accelL1)
        {
            TryResolvePoolL1(parts, out float move, out accelL1, out _);
            if (moveL1 > 0.01f)
                move = moveL1;

            return new ShipComponentAbilityStats
            {
                moveSpeed = move,
                accelerationCap = accelL1,
                moveSpeedPerAbilityLevel = parts.Propulsion.moveSpeedPerAbilityLevel,
                accelerationCapPerAbilityLevel = parts.Propulsion.accelerationCapPerAbilityLevel,
                extraSpeedEnergyDrain = 0f,
                extraSpeedEnergyDrainPerAbilityLevel = 0f,
            };
        }

        /// <summary>
        /// Telemetry-style section title inside a READOUT banner.
        /// [TITAN-ORBIT] Matches ability-chip tip language from <see cref="ShipAbilityStatBreakdown"/>.
        /// </summary>
        static void AppendHeader(StringBuilder sb, string title)
        {
            ShipStatTooltipChrome.AppendSectionBanner(sb, "READOUT", "7EC8FF");
            sb.Append("<b><color=#E8F4FF>").Append(title).Append("</color></b>").AppendLine();
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

        /// <summary>
        /// Intermediate calc display — enough digits that part lines add to the step total.
        /// Math always uses full floats; this is display only.
        /// </summary>
        static string FDetail(float v) =>
            v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>Final / player-facing totals — up to 2 decimal places.</summary>
        static string FResult(float v) =>
            v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>[LEGACY name] Same as <see cref="FResult"/> — prefer FResult for new lines.</summary>
        static string F1(float v) => FResult(v);

        /// <summary>Always two fraction digits (mass weights, etc.).</summary>
        static string F2(float v) =>
            v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
