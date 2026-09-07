using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Tracks which chassis stats were last written to a ship entity. ShipStatApplySystem compares
    /// AppliedShipLevel / AppliedBranchIndex / AppliedAttributeSum against live state to decide when to re-apply.
    /// Local-only bookkeeping (not ghosted) — both server and client keep their own copy after ApplyToShip.
    /// </summary>
    public struct ShipChassisState : IComponentData
    {
        public FixedString64Bytes ChassisId;
        public int AppliedShipLevel;
        public int AppliedBranchIndex;
        /// <summary>
        /// [TITAN-ORBIT] Last applied <see cref="ShipState.ShipFamilyConfigIndex"/> so switching
        /// from AstroEagle to a captured-neutral family re-runs ApplyToShip at the same level/branch.
        /// </summary>
        public byte AppliedShipFamilyConfigIndex;
        /// <summary>
        /// Sum of ghosted <see cref="ShipAttributeUpgradeState"/> levels at last apply.
        /// Client re-applies motor when attribute RPCs land without a level change.
        /// </summary>
        public int AppliedAttributeSum;

        /// <summary>
        /// Fingerprint of equipped store components + cards at last apply.
        /// Orbit purchases / removes change loadout without level/branch change — this forces re-apply.
        /// </summary>
        public int AppliedEquipmentFingerprint;
    }

    /// <summary>
    /// Shared stat-application pipeline: resolves a chassis id from team + level + branch, sums
    /// ship-family component stats, applies attribute upgrades (×10% for most; additive Move Speed
    /// PerExtraLevel), and writes untaxed MaxSpeed / EngineThrust (= accel) / RotationSpeed onto
    /// ShipMotorConfig. Live subtractive mass tax
    /// (<see cref="ShipMobilityResolution"/> / <see cref="ShipCargoMobilitySettings"/>) runs each
    /// drive tick from current gems/people + ComponentSize. Also writes ShipState, ShipWeaponConfig,
    /// and ShipVitalsConfig. Called by ShipStatApplySystem (server + client prediction),
    /// ShipAttributeUpgradeLogic (purchase), and respawn/rejoin flows.
    /// <para>
    /// [NETCODE] <see cref="ShipMotorConfig"/> is not ghost-serialized. The client must run the same
    /// ApplyToShip path (motor/weapon/vitals only) or owner prediction keeps bake defaults
    /// (MaxSpeed=35) while the server uses chassis ~13 — HUD lies and prediction fights reconcile.
    /// </para>
    /// </summary>
    public static class ShipStatApplyLogic
    {
        static PlanetShipFamilyConfig s_config;

        /// <summary>Lazily loads PlanetShipFamilyConfig from Resources (cached until InvalidateConfigCache).</summary>
        public static PlanetShipFamilyConfig Config
        {
            get
            {
                if (s_config == null)
                    s_config = LoadConfig();
                return s_config;
            }
        }

        /// <summary>
        /// [UNITY] Sole load path — <c>Assets/Resources/PlanetShipFamilyConfig.asset</c>.
        /// </summary>
        static PlanetShipFamilyConfig LoadConfig()
        {
            return PlanetShipFamilyConfig.LoadDefault();
        }

        /// <summary>Clears cached config — call after hot-reload or editor asset changes.</summary>
        public static void InvalidateConfigCache() => s_config = null;

        /// <summary>
        /// Maps ship family + level + branch to a chassis id from <see cref="PlanetShipFamilyConfig"/>.
        /// Home family index 0 is AstroEagle; captured-neutral purchases write a non-zero index onto
        /// <see cref="ShipState.ShipFamilyConfigIndex"/> so this resolves Cosmic Shark / etc.
        /// When <paramref name="allowFallback"/> is true and the slot is empty, falls back to that
        /// family's starter chassis (linear index 0). Purchase validation should pass false so missing
        /// L7/MEGA slots do not silently become the starter hull.
        /// </summary>
        /// <param name="team">Ship team (unused for ladder pick; kept for call-site compatibility).</param>
        /// <param name="shipLevel">Upgrade ladder level 1–7.</param>
        /// <param name="branchIndex">Branch within that level (0-based).</param>
        /// <param name="chassisId">Resolved chassis string (e.g. CosmicShark_01), or null on failure.</param>
        /// <param name="allowFallback">When true, empty ladder slots fall back to family starter.</param>
        /// <param name="shipFamilyConfigIndex">
        /// Index into PlanetShipFamilyConfig.families. Prefer the ghosted value on <see cref="ShipState"/>.
        /// </param>
        public static bool TryResolveChassisId(
            TeamId team,
            int shipLevel,
            int branchIndex,
            out string chassisId,
            bool allowFallback = true,
            int shipFamilyConfigIndex = -1)
        {
            chassisId = null;
            var config = Config;
            if (config == null)
                return false;

            // --- Resolve family slot ---
            // [TITAN-ORBIT] Negative / unset → home AstroEagle (legacy callers + fresh spawns).
            int familyIndex = shipFamilyConfigIndex >= 0
                ? shipFamilyConfigIndex
                : PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
            bool isHomeFamily = familyIndex == PlanetShipFamilyAssignment.HomeFamilyConfigIndex;

            // [TITAN-ORBIT] planetId argument is only used when configIndex is invalid; pass 0 for home
            // and a synthetic non-home id otherwise so ResolveConfigIndex does not force AstroEagle.
            int planetIdHint = isHomeFamily ? 0 : 100;

            chassisId = config.GetChassisIdForLadderSlot(
                planetIdHint,
                shipLevel,
                branchIndex,
                isHomePlanet: isHomeFamily,
                shipFamilyConfigIndex: familyIndex);

            // [STANDARD] Starter hull only — never map a missing MEGA/L7 click onto Hawk by accident.
            if (string.IsNullOrEmpty(chassisId) && allowFallback)
            {
                chassisId = config.GetChassisIdForPlanetAndIndex(
                    planetIdHint, 0, isHomePlanet: isHomeFamily, shipFamilyConfigIndex: familyIndex);
            }

            return !string.IsNullOrEmpty(chassisId);
        }

        /// <summary>
        /// Overload that reads <see cref="ShipState.ShipFamilyConfigIndex"/> from the ship entity.
        /// Prefer this from systems that already hold the ship entity.
        /// </summary>
        public static bool TryResolveChassisId(
            EntityManager em,
            Entity shipEntity,
            TeamId team,
            int shipLevel,
            int branchIndex,
            out string chassisId,
            bool allowFallback = true)
        {
            int familyIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
            if (em.Exists(shipEntity) && em.HasComponent<ShipState>(shipEntity))
                familyIndex = em.GetComponentData<ShipState>(shipEntity).ShipFamilyConfigIndex;

            if (em.Exists(shipEntity)
                && em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
            {
                chassisId = MegaShipCatalog.FormatChassisId(
                    em.GetComponentData<MegaShipState>(shipEntity).CatalogIndex);
                return true;
            }

            return TryResolveChassisId(team, shipLevel, branchIndex, out chassisId, allowFallback, familyIndex);
        }

        /// <summary>
        /// Resolves <see cref="ShipFamilyDefinition"/> from chassis id prefix
        /// (e.g. <c>AstroEagle_T2</c> → familyId <c>AstroEagle</c>).
        /// </summary>
        public static bool TryResolveFamilyForChassisId(string chassisId, out ShipFamilyDefinition family)
        {
            family = null;
            var config = Config;
            if (config?.families == null || string.IsNullOrEmpty(chassisId))
                return false;

            int underscore = chassisId.IndexOf('_');
            if (underscore <= 0)
                return false;

            string prefix = chassisId.Substring(0, underscore);
            for (int i = 0; i < config.families.Count; i++)
            {
                var entry = config.families[i];
                if (entry?.shipFamilyDefinition != null &&
                    string.Equals(entry.shipFamilyDefinition.familyId, prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    family = entry.shipFamilyDefinition;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sums component stats from the chassis prefab (or tier breakdown / family fallback)
        /// at <b>level 1</b>. Callers must apply
        /// <see cref="ShipComponentStoreData.GetEffectiveStatsAtShipLevel"/> for the live ship level.
        /// <paramref name="shipLevel"/> is kept for API stability; scaling is intentionally not
        /// applied here (double-apply bug: level 6 Weapon 3+1×5 became 13 instead of 8).
        /// </summary>
        public static bool TryGetBaseStatsForChassis(string chassisId, int shipLevel, out ShipComponentAbilityStats baseStats)
        {
            baseStats = default;
            _ = shipLevel;
            if (MegaShipCatalog.IsMegaChassisId(chassisId))
            {
                var megaCatalog = MegaShipCatalog.Load();
                if (megaCatalog != null
                    && MegaShipCatalog.TryParseCatalogIndex(chassisId, out ushort megaIndex)
                    && megaCatalog.TryGetEntry(megaIndex, out MegaShipCatalogEntry megaEntry)
                    && megaEntry != null)
                {
                    return MegaShipStatsCalculator.SumFromEntry(megaEntry, megaCatalog, out baseStats);
                }

                return false;
            }

            var config = Config;
            if (config == null || string.IsNullOrEmpty(chassisId))
                return false;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier == null)
                return false;

            // --- Resolve ship family from chassis id prefix (e.g. "AstroEagle_T2" → AstroEagle) ---
            TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family);

            // [TITAN-ORBIT] Prefer summing stats from the baked chassis prefab hierarchy.
            // Always sum at level 1 here — ApplyToShip / HUD call GetEffectiveStatsAtShipLevel
            // once. Passing shipLevel into TrySumFromPrefab used to double-apply per-level growth
            // (e.g. level 6 AstroEagle Weapon 3+1×5 became 13 instead of 8).
            if (tier.prefab != null && family != null &&
                ShipFamilyStatsCalculator.TrySumFromPrefab(tier.prefab, family, shipLevel: 1, out baseStats))
                return true;

            // Fallback: use tier power-score breakdown or family default stats.
            if (tier.powerScoreBreakdown.HasDisplayStats)
            {
                baseStats = ShipFamilyStatsCalculator.BreakdownToBaseStats(tier.powerScoreBreakdown);
                if (family != null)
                {
                    // [TITAN-ORBIT] Older baked breakdowns summed bulletSpeed across every Weapon
                    // child (e.g. 4×12 speed). Prefab sum now uses max speed; until the catalog is
                    // re-baked, force bullet speed (and leave firePower/fireRate as authored
                    // breakdown totals — live shots use per-mount combat, not this hull block).
                    // Same for bulletRange: display breakdown never stored range; use family defaults.
                    var familyDefaults = family.GetEffectiveDefaultFallbackStats();
                    if (familyDefaults.bulletSpeed > 0.01f)
                        baseStats.bulletSpeed = familyDefaults.bulletSpeed;
                    if (familyDefaults.bulletRange > 0.01f)
                        baseStats.bulletRange = familyDefaults.bulletRange;
                    if (familyDefaults.bulletRangePerExtraLevel > 0.01f)
                        baseStats.bulletRangePerExtraLevel = familyDefaults.bulletRangePerExtraLevel;
                    baseStats = family.ApplyStatFallbacks(baseStats);
                }
                return true;
            }

            if (family != null)
            {
                baseStats = family.GetEffectiveDefaultFallbackStats();
                return true;
            }

            return false;
        }

        /// <summary>Convenience overload without EntityCommandBuffer (no structural changes queued).</summary>
        public static void ApplyToShip(EntityManager em, Entity shipEntity, TeamId team, int shipLevel, int branchIndex)
        {
            ApplyToShip(em, shipEntity, team, shipLevel, branchIndex, default, queueStructuralChanges: false, writeGhostedShipState: true);
        }

        /// <summary>
        /// Full stat apply: resolve chassis → sum stats → attribute multipliers → write ship components.
        /// When queueStructuralChanges is true, missing vitals/chassis components are added via ECB
        /// (safe during iteration in ShipStatApplySystem).
        /// </summary>
        /// <param name="writeGhostedShipState">
        /// Server: true — write Health/MaxHealth/caps on <see cref="ShipState"/>.
        /// Client: false — those fields are [GhostField]; only motor/weapon/vitals/chassis bookkeeping.
        /// </param>
        public static void ApplyToShip(
            EntityManager em,
            Entity shipEntity,
            TeamId team,
            int shipLevel,
            int branchIndex,
            EntityCommandBuffer ecb,
            bool queueStructuralChanges,
            bool writeGhostedShipState = true)
        {
            // --- Family from ship ghost ---
            // [TITAN-ORBIT] Captured-neutral moon purchases stamp ShipFamilyConfigIndex; resolve with it
            // so Cosmic Shark / etc. stats apply instead of always AstroEagle.
            int familyIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
            if (em.Exists(shipEntity) && em.HasComponent<ShipState>(shipEntity))
                familyIndex = em.GetComponentData<ShipState>(shipEntity).ShipFamilyConfigIndex;

            // --- MEGA hulls use a frozen stat table (no Extra Level / attributes) ---
            if (em.Exists(shipEntity)
                && em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
            {
                MegaShipStatApplyLogic.ApplyToShip(
                    em,
                    shipEntity,
                    em.GetComponentData<MegaShipState>(shipEntity),
                    familyIndex,
                    writeGhostedShipState);
                return;
            }

            if (!TryResolveChassisId(team, shipLevel, branchIndex, out string chassisId, allowFallback: true, familyIndex))
                return;

            // --- Chassis parts (primary pools + counts) then Extra Level evaluate ---
            // [TITAN-ORBIT] Non-weapons: Base + PerExtra × ((shipLevel−1) + ability + (N−1)).
            // Weapons: Base + PerExtra × ((shipLevel−1) + ability) per barrel (no N stack).
            if (!TryGetChassisPartSum(em, shipEntity, chassisId, out ShipFamilyStatsCalculator.SumResult partSum))
            {
                // Fallback: baked breakdown / family defaults when prefab parts are unavailable.
                if (!TryGetBaseStatsForChassis(chassisId, shipLevel, out ShipComponentAbilityStats summedFallback))
                    return;
                partSum = new ShipFamilyStatsCalculator.SumResult
                {
                    TotalStats = summedFallback,
                    MatchedComponentIds = new System.Collections.Generic.List<string>(),
                    PerComponentStats = new System.Collections.Generic.List<ShipComponentAbilityStats>(),
                };
                Debug.LogWarning(
                    "[ShipStatApply] chassis=" + chassisId +
                    " used fallback stats (0 prefab parts). move=" +
                    summedFallback.moveSpeed.ToString("F1") +
                    " hp=" + summedFallback.healthCap.ToString("F1") +
                    " gems=" + summedFallback.maxGems.ToString("F1") +
                    " — expected familyId_* children on the chassis prefab.");
            }

            ShipComponentAbilityStats summed = partSum.TotalStats;

            int attributeSum = 0;
            ShipAttributeUpgradeState attrs = default;
            bool hasAttrs = false;
            if (em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
            {
                attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
                hasAttrs = true;
                attributeSum = SumAttributeLevels(attrs);
            }

            ShipAbilityLevelCounts abilityCounts = hasAttrs
                ? ShipAttributeUpgradeLogic.ToAbilityLevelCounts(in attrs)
                : default;

            ShipComponentAbilityStats chassisBaseline;
            if (partSum.MatchedComponentIds != null && partSum.MatchedComponentIds.Count > 0)
            {
                chassisBaseline = ShipComponentExtraLevelMath.AggregateAndEvaluate(
                    partSum.MatchedComponentIds,
                    partSum.PerComponentStats,
                    shipLevel,
                    in abilityCounts);
                chassisBaseline = ShipComponentExtraLevelMath.ApplyMobilityPenalties(chassisBaseline, shipLevel);
                if (TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition evalFamily) && evalFamily != null)
                {
                    chassisBaseline = evalFamily.ApplyStatFallbacks(chassisBaseline);
                    chassisBaseline = evalFamily.ApplySpecialBonuses(chassisBaseline);
                }
            }
            else
            {
                // No part list — treat fallback block as a single pool (count 1).
                chassisBaseline = ShipComponentExtraLevelMath.EvaluatePool(
                    summed, 1, shipLevel, in abilityCounts, isWeaponPool: false);
                chassisBaseline = ShipComponentExtraLevelMath.ApplyMobilityPenalties(chassisBaseline, shipLevel);
            }

            // --- Equipped upgrade cards (flat / multiplier modifiers on top of chassis+store) ---
            TryAddEquippedCardStatModifiers(em, shipEntity, chassisId, ref chassisBaseline);

            ShipComponentAbilityStats effective = chassisBaseline;

            // --- Detect chassis identity change (level / branch / id) before we overwrite bookkeeping ---
            // [TITAN-ORBIT] Upgrade-tree purchases jump firePower while sticky ReferenceBulletDamage
            // stayed at starter 8 → BulletVisualScale upgradeMul exploded. Reset references only
            // when the hull changes; attribute-only applies keep sticky refs so size still grows.
            bool chassisIdentityChanged = true;
            if (em.HasComponent<ShipChassisState>(shipEntity))
            {
                var prevChassis = em.GetComponentData<ShipChassisState>(shipEntity);
                var chassisKey = new FixedString64Bytes(chassisId);
                chassisIdentityChanged = !prevChassis.ChassisId.Equals(chassisKey)
                    || prevChassis.AppliedShipLevel != shipLevel
                    || prevChassis.AppliedBranchIndex != branchIndex;
            }

            int equipmentFingerprint = ComputeEquippedLoadoutFingerprint(em, shipEntity);

            // --- ShipState caps (health, gems, energy, people) — server / authoritative only ---
            // [NETCODE] Client must not overwrite ghosted ShipState; snapshot owns Health/caps.
            if (writeGhostedShipState && em.HasComponent<ShipState>(shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(shipEntity);
                // [STANDARD] Preserve health ratio on re-apply unless dead or awaiting team pick.
                float prevHealthRatio = ship.MaxHealth > 0.01f ? ship.Health / ship.MaxHealth : 1f;

                ship.MaxHealth = Mathf.Max(1f, effective.healthCap);
                ship.GemCapacity = Mathf.Max(0f, effective.maxGems);
                ship.MaxEnergy = Mathf.Max(1f, effective.energyCap);
                ship.PeopleCapacity = Mathf.Max(0, Mathf.RoundToInt(effective.maxPeople));
                ship.ShipLevel = shipLevel;
                ship.BranchIndex = branchIndex;
                ship.Health = Mathf.Clamp(ship.Health, 0f, ship.MaxHealth);
                if (ship.Health <= 0.01f || ship.AwaitingTeamSelection)
                    ship.Health = ship.MaxHealth;
                else
                    ship.Health = Mathf.Clamp(ship.MaxHealth * prevHealthRatio, 1f, ship.MaxHealth);

                ship.CurrentEnergy = Mathf.Min(ship.CurrentEnergy, ship.MaxEnergy);
                // [TITAN-ORBIT] Seed full energy only at team-select / spawn — never refill when
                // OVERDRIVE (or combat) emptied the pool to ~0, or re-engage hysteresis breaks.
                if (ship.AwaitingTeamSelection)
                    ship.CurrentEnergy = ship.MaxEnergy;

                ship.CurrentGems = Mathf.Min(ship.CurrentGems, ship.GemCapacity);
                ship.CurrentPeople = Mathf.Min(ship.CurrentPeople, ship.PeopleCapacity);
                em.SetComponentData(shipEntity, ship);
            }

            // --- Weapon tuning (server-authoritative bullet sim reads these) ---
            // Hull BulletDamage / FireRate are averages for HUD + fallback; live shots use
            // per-mount FirePower / FireRate from ShipWeaponMountCombatLogic after this block.
            if (em.HasComponent<ShipWeaponConfig>(shipEntity))
            {
                float firePower = Mathf.Max(0.1f, effective.firePower);
                float fireRate = Mathf.Max(0.1f, effective.fireRate);
                float bulletSpeed = Mathf.Max(0.1f, effective.bulletSpeed);
                // Chassis baseline (no attribute mul) — visual "level 1 for this hull".
                float baselineDamage = Mathf.Max(0.1f, chassisBaseline.firePower);
                float baselineSpeed = Mathf.Max(0.1f, chassisBaseline.bulletSpeed);
                var weapon = em.GetComponentData<ShipWeaponConfig>(shipEntity);
                weapon.FireRate = fireRate;
                weapon.BulletSpeed = bulletSpeed;
                weapon.BulletDamage = firePower;
                weapon.EnergyCostPerShot = firePower
                    * CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.WeaponEnergyCostMul);
                // [TITAN-ORBIT] Bullet travel range from family stats (ship-level scaled).
                // Fallback to DefaultBulletMaxDistance when authored range is zero/missing.
                // Lifetime is derived so MaxDistance wins before the timer for normal bullet speeds.
                weapon.BulletMaxDistance = Mathf.Max(
                    1f,
                    effective.bulletRange > 0.01f
                        ? effective.bulletRange
                        : ShipWeaponConfig.DefaultBulletMaxDistance);
                weapon.BulletLifetime = Mathf.Max(0.25f, weapon.BulletMaxDistance / Mathf.Max(1f, bulletSpeed));
                // [TITAN-ORBIT] Reset VFX baselines on hull swap so upgradeMul ≈ 1 until attributes climb.
                if (chassisIdentityChanged || weapon.ReferenceBulletDamage <= 0.01f)
                    weapon.ReferenceBulletDamage = baselineDamage;
                if (chassisIdentityChanged || weapon.ReferenceBulletSpeed <= 0.01f)
                    weapon.ReferenceBulletSpeed = baselineSpeed;
                // [TITAN-ORBIT] Hull-wide volley vs round-robin policy from the family asset.
                // Default EnergyHybrid when family resolve fails so older paths keep legacy feel.
                weapon.FireMode = ShipWeaponFireMode.EnergyHybrid;
                if (TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition fireModeFamily) &&
                    fireModeFamily != null)
                    weapon.FireMode = fireModeFamily.weaponFireMode;
                em.SetComponentData(shipEntity, weapon);
            }

            // --- Per-barrel combat (own firePower / fireRate — not hull average) ---
            // [TITAN-ORBIT] Must run after ShipWeaponConfig write; overwrites BulletDamage/FireRate
            // with averages of the mounts for HUD while each mount keeps its own shot strength.
            TryApplyPerMountWeaponCombat(em, shipEntity, chassisId, shipLevel);

            // --- Bullet VFX bank from ShipFamilyDefinition.bulletPrefabIndex ---
            // [NETCODE] RuntimeBulletIndex is ghosted — server writes; clients read replica / predict.
            // [TITAN-ORBIT] Reset ONLY when hull family identity changes (ChassisId / branch),
            // not on ship level or attribute re-applies — otherwise B-key cycle is wiped every
            // level tick. ShipCycleBulletSystem owns mid-flight index changes.
            bool bulletBankIdentityChanged = true;
            if (em.HasComponent<ShipChassisState>(shipEntity))
            {
                var prevForBank = em.GetComponentData<ShipChassisState>(shipEntity);
                var chassisKeyForBank = new FixedString64Bytes(chassisId);
                bulletBankIdentityChanged = !prevForBank.ChassisId.Equals(chassisKeyForBank)
                    || prevForBank.AppliedBranchIndex != branchIndex;
            }

            if (writeGhostedShipState &&
                bulletBankIdentityChanged &&
                em.HasComponent<ShipLoadoutState>(shipEntity) &&
                TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition bankFamily))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                int familyBank = BulletBankProfileUtility.ResolveBankIndexForFamily(bankFamily);
                int[] owned = new int[16];
                int ownedCount = BulletBankOwnership.CollectOwnedDamageBanks(em, shipEntity, owned);
                bool stillOwned = false;
                for (int i = 0; i < ownedCount; i++)
                {
                    if (owned[i] == loadout.RuntimeBulletIndex)
                    {
                        stillOwned = true;
                        break;
                    }
                }

                if (!stillOwned)
                    loadout.RuntimeBulletIndex = familyBank;
                em.SetComponentData(shipEntity, loadout);
            }

            // --- Physics tuning (ShipPhysicsDriveSystem reads these) ---
            if (em.HasComponent<ShipMotorConfig>(shipEntity))
            {
                // --- Untaxed motor baselines (drive applies live subtractive mass tax each tick) ---
                // [TITAN-ORBIT] No ×10 EngineThrustVisibility — EngineThrust stores acceleration.
                // No bake-time capacity tax — collecting gems/people updates Speed/Accel/Turn live.
                float moveVal = Mathf.Max(0.1f, effective.moveSpeed);
                float turnVal = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(effective.turnSpeed);
                float accel = Mathf.Max(0.1f, effective.accelerationCap > 0f
                    ? effective.accelerationCap
                    : moveVal);

                // --- ComponentSize (box × attribute grow × tier → HullMassReference) ---
                // Dedicated: never Instantiate the chassis (80–200ms, stripped meshes).
                // Walk the prefab-asset transforms instead.
                float liveComponentSize = 0f;
#if !UNITY_SERVER || UNITY_EDITOR
                liveComponentSize = TryGetLiveHullComponentMass(
                    chassisId, hasAttrs ? attrs : default, shipLevel, applyAttributeScale: true);
#endif
                if (liveComponentSize <= 0.0001f)
                    liveComponentSize = TryGetChassisComponentMass(chassisId);

                float liveHullSize = ShipMassLogic.ComputeHullMassReference(
                    liveComponentSize, ShipMassLogic.DefaultBaseMass);

                // [TITAN-ORBIT] Mass reference uses level-1 Extra Level health (ability 0).
                ShipComponentAbilityStats levelOneStats =
                    partSum.MatchedComponentIds != null && partSum.MatchedComponentIds.Count > 0
                        ? ShipComponentExtraLevelMath.AggregateAndEvaluate(
                            partSum.MatchedComponentIds, partSum.PerComponentStats, shipLevel: 1)
                        : ShipComponentStoreData.GetEffectiveStatsAtShipLevel(summed, 1);
                float referenceHealth = Mathf.Max(1f, levelOneStats.healthCap);

                var motor = em.GetComponentData<ShipMotorConfig>(shipEntity);
                motor.MaxSpeed = moveVal;
                motor.EngineThrust = accel;
                motor.RotationSpeed = turnVal;
                motor.BrakeDeceleration = ShipMassLogic.DefaultBrakeDeceleration;
                motor.HullMassReference = liveHullSize;
                motor.ChassisReferenceHealth = referenceHealth;
                // [TITAN-ORBIT] Ram/grind damage rating — level-scaled family sum (HUD uses the same field).
                motor.RammingPower = Mathf.Max(0f, effective.rammingPower);

                // [TITAN-ORBIT] OVERDRIVE: ExtraSpeedPercent (speed mul) from engines; absolute OD drain
                // from effective.extraSpeedEnergyDrain (ship-tier + Move Speed ability steps).
                TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition drainFamily);
                var bonuses = drainFamily != null
                    ? drainFamily.specialBonuses
                    : ShipFamilySpecialBonuses.Identity;
                ShipPropulsionAggregation.ResolveOverdriveFromEngines(
                    drainFamily,
                    shipLevel,
                    bonuses,
                    out float odSpeed,
                    out float odThrust,
                    out float _);
                motor.ThrustEnergyDrainPerSecond = Mathf.Max(0f, effective.extraSpeedEnergyDrain);
                motor.OverdriveSpeedMultiplier = odSpeed;
                motor.OverdriveThrustMultiplier = odThrust;
                // Absolute rate already baked into ThrustEnergyDrainPerSecond — mul stays 1.
                motor.OverdriveEnergyDrainMultiplier = 1f;
                motor.SkipMassTax = 0;

                em.SetComponentData(shipEntity, motor);
            }

            // --- Regen rates (ShipVitalsRegenSystem consumes these) ---
            var vitals = new ShipVitalsConfig
            {
                HealthRegenPerSecond = Mathf.Max(0f, effective.healthRegen),
                EnergyRegenPerSecond = Mathf.Max(0f, effective.energyRegen),
                HealthRegenDelayAfterDamage = 0.35f,
            };
            if (em.HasComponent<ShipVitalsConfig>(shipEntity))
                em.SetComponentData(shipEntity, vitals);
            else if (queueStructuralChanges)
                ecb.AddComponent(shipEntity, vitals);
            else
                em.AddComponentData(shipEntity, vitals);

            if (!em.HasComponent<ShipVitalsState>(shipEntity))
            {
                if (queueStructuralChanges)
                    ecb.AddComponent(shipEntity, new ShipVitalsState());
                else
                    em.AddComponentData(shipEntity, new ShipVitalsState());
            }

            // --- Whole-hull tier size (+10% per ship level above 1) ---
            // [TITAN-ORBIT] Uniform LocalTransform.Scale — not per-component mesh grow.
            // Visual proxies use Scale × ShipPresentationScale; muzzles / hit radii read Scale too.
            // Fire power is unchanged by this (family catalog stats + attrs).
            if (em.HasComponent<LocalTransform>(shipEntity))
            {
                var lt = em.GetComponentData<LocalTransform>(shipEntity);
                float tierScale = BodyCollisionMath.GetShipTierScale(shipLevel);
                if (!Mathf.Approximately(lt.Scale, tierScale))
                {
                    lt.Scale = tierScale;
                    em.SetComponentData(shipEntity, lt);
                }
            }

            // --- Bookkeeping so ShipStatApplySystem skips unchanged ships ---
            var chassisState = new ShipChassisState
            {
                ChassisId = chassisId,
                AppliedShipLevel = shipLevel,
                AppliedBranchIndex = branchIndex,
                AppliedShipFamilyConfigIndex = (byte)familyIndex,
                AppliedAttributeSum = attributeSum,
                AppliedEquipmentFingerprint = equipmentFingerprint,
            };
            if (em.HasComponent<ShipChassisState>(shipEntity))
                em.SetComponentData(shipEntity, chassisState);
            else if (queueStructuralChanges)
                ecb.AddComponent(shipEntity, chassisState);
            else
                em.AddComponentData(shipEntity, chassisState);
        }

        /// <summary>
        /// Hash of equipped store components + cards. Orbit purchases call ApplyToShip immediately;
        /// ShipStatApplySystem does <b>not</b> poll this every frame (that ToString/buffer walk lagged).
        /// Kept for diagnostics and future dirty-bit hooks.
        /// </summary>
        public static int ComputeEquippedLoadoutFingerprint(EntityManager em, Entity shipEntity)
        {
            // --- Fingerprint equipment + cards ---
            unchecked
            {
                int hash = 17;
                if (em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                {
                    var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
                    hash = hash * 31 + buf.Length;
                    for (int i = 0; i < buf.Length; i++)
                    {
                        var e = buf[i];
                        hash = hash * 31 + e.ItemType;
                        hash = hash * 31 + e.RemainingCharges;
                        hash = hash * 31 + e.ItemLevel;
                        hash = hash * 31 + e.ComponentId.GetHashCode();
                    }
                }

                if (em.HasBuffer<EquippedCardElement>(shipEntity))
                {
                    var cards = em.GetBuffer<EquippedCardElement>(shipEntity);
                    hash = hash * 31 + cards.Length;
                    for (int i = 0; i < cards.Length; i++)
                        hash = hash * 31 + cards[i].CardId.GetHashCode();
                }

                return hash;
            }
        }

        /// <summary>
        /// Collects chassis prefab parts (plus moon-store ship components when equipped) as a
        /// raw <see cref="ShipFamilyStatsCalculator.SumResult"/> for Extra Level evaluation.
        /// Aggregation here is primary-only; ship/ability scaling happens in ApplyToShip.
        /// </summary>
        static bool TryGetChassisPartSum(
            EntityManager em,
            Entity shipEntity,
            string chassisId,
            out ShipFamilyStatsCalculator.SumResult sum)
        {
            sum = default;
            var config = Config;
            if (config == null || string.IsNullOrEmpty(chassisId))
                return false;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier?.prefab == null)
                return false;
            if (!TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family) || family == null)
                return false;

            // --- Raw prefab children (no Extra Level yet) ---
            var raw = ShipFamilyStatsCalculator.SumFromPrefabHierarchy(
                tier.prefab, family, shipLevel: 1, applyPropulsionAndWeaponRules: false);

            // --- Optional moon-store equipment appended into the same part lists ---
            var extraIds = new System.Collections.Generic.List<string>(4);
            if (em.HasBuffer<EquippedEquipmentElement>(shipEntity))
            {
                var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    if ((StoreItemType)e.ItemType != StoreItemType.ShipComponent)
                        continue;
                    string id = e.ComponentId.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                        extraIds.Add(id);
                }
            }

            if (extraIds.Count > 0)
            {
                sum = ShipFamilyStatsCalculator.AppendExtraComponentsAndAggregate(
                    raw, family, extraIds, shipLevel: 1);
            }
            else
            {
                sum = raw;
                ShipFamilyStatsCalculator.ApplySharedAggregationRules(ref sum, family, shipLevel: 1);
            }

            return sum.MatchedComponentIds != null && sum.MatchedComponentIds.Count > 0;
        }

        /// <summary>
        /// Adds flat CardData stat modifiers from equipped upgrade cards onto the chassis baseline.
        /// </summary>
        static void TryAddEquippedCardStatModifiers(
            EntityManager em,
            Entity shipEntity,
            string chassisId,
            ref ShipComponentAbilityStats baseline)
        {
            // --- Equipped upgrade cards ---
            if (!em.HasBuffer<EquippedCardElement>(shipEntity))
                return;
            if (!TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family) || family == null)
                return;

            var cards = em.GetBuffer<EquippedCardElement>(shipEntity);
            for (int i = 0; i < cards.Length; i++)
            {
                string cardId = cards[i].CardId.ToString();
                if (string.IsNullOrWhiteSpace(cardId))
                    continue;

                CardData card = FindCardInFamily(family, cardId);
                if (card == null)
                    card = FindCardAnywhere(cardId);
                if (card == null)
                    continue;

                // [TITAN-ORBIT] CardData flat adds + combat multipliers (pre-ECS card cache parity).
                baseline.moveSpeed += card.movementSpeedAdd;
                baseline.turnSpeed += card.rotationSpeedAdd;
                baseline.healthCap += card.maxHealthAdd;
                baseline.healthRegen += card.healthRegenAdd;
                baseline.energyCap += card.energyCapacityAdd;
                baseline.energyRegen += card.energyRegenAdd;
                baseline.maxGems += card.gemCapacityAdd;
                baseline.maxPeople += card.peopleCapacityAdd;

                if (card.damageMultiplier > 0.01f && !Mathf.Approximately(card.damageMultiplier, 1f))
                    baseline.firePower *= card.damageMultiplier;
                if (card.fireRateMultiplier > 0.01f && !Mathf.Approximately(card.fireRateMultiplier, 1f))
                    baseline.fireRate *= card.fireRateMultiplier;
                if (card.bulletSpeedMultiplier > 0.01f && !Mathf.Approximately(card.bulletSpeedMultiplier, 1f))
                    baseline.bulletSpeed *= card.bulletSpeedMultiplier;

                // Family-style overlay (only ≠1 fields change the hull). Stacks after family specialBonuses.
                if (!card.familyBonusOverlay.IsIdentity)
                    baseline = card.familyBonusOverlay.Apply(baseline);
            }

            // Named CardEffect rows that map onto chassis stats (range, ram, tractor, overdrive).
            baseline.bulletRange *= CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.BulletRangeMul);
            baseline.fireRate *= CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.FireRateMul);
            baseline.rammingPower *= CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.RammingMul);
            baseline.tractorBeamDistance *= CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.TractorRangeMul);
            baseline.tractorBeamPower *= CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.TractorPowerMul);
            baseline.extraSpeedPercent *= CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.OverdriveSpeedMul);
            baseline.extraSpeedEnergyDrain *= CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.OverdriveDrainMul);
        }

        /// <summary>Looks up a CardData by stable id inside one ship family's upgrade deck.</summary>
        public static CardData FindCardInFamily(ShipFamilyDefinition family, string cardId)
        {
            if (family == null || string.IsNullOrEmpty(cardId))
                return null;

            foreach (var card in family.GetUpgradeCards())
            {
                if (card == null)
                    continue;
                if (string.Equals(card.GetStableCardId(), cardId, System.StringComparison.OrdinalIgnoreCase))
                    return card;
                if (string.Equals(card.cardId, cardId, System.StringComparison.OrdinalIgnoreCase))
                    return card;
                if (string.Equals(card.name, cardId, System.StringComparison.OrdinalIgnoreCase))
                    return card;
            }

            return null;
        }

        /// <summary>Looks up a CardData across all families in PlanetShipFamilyConfig (fallback).</summary>
        public static CardData FindCardAnywhere(string cardId)
        {
            if (string.IsNullOrEmpty(cardId))
                return null;

            var config = Config;
            if (config?.families == null)
                return null;

            for (int i = 0; i < config.families.Count; i++)
            {
                var def = config.families[i]?.shipFamilyDefinition;
                if (def == null)
                    continue;
                var card = FindCardInFamily(def, cardId);
                if (card != null)
                    return card;
            }

            return null;
        }

        /// <summary>
        /// Fingerprint of attribute upgrade levels so client/server re-apply when HUD purchases land.
        /// </summary>
        public static int SumAttributeLevels(in ShipAttributeUpgradeState attrs)
        {
            return attrs.FirePower
                   + attrs.BulletSpeed
                   + attrs.MaxHealth
                   + attrs.HealthRegen
                   + attrs.EnergyCapacity
                   + attrs.EnergyRegen
                   + attrs.MovementSpeed
                   + attrs.RotationSpeed
                   + attrs.GemCapacity
                   + attrs.PeopleCapacity;
        }

        /// <summary>
        /// Writes each weapon mount’s own firePower / fireRate from the chassis prefab Weapon
        /// children (scale × ship level × Fire Power attributes). No-ops when mounts are missing —
        /// <see cref="ShipChassisCatalogApplySystem"/> rebuilds poses after this system, then
        /// refreshes combat again.
        /// </summary>
        public static void TryApplyPerMountWeaponCombat(
            EntityManager em,
            Entity shipEntity,
            string chassisId,
            int shipLevel)
        {
            if (string.IsNullOrEmpty(chassisId) || !em.HasBuffer<ShipWeaponMountElement>(shipEntity))
                return;

            var config = Config;
            if (config == null)
                return;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier?.prefab == null)
                return;

            if (!TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family) || family == null)
                return;

            ShipAttributeUpgradeState attrs = default;
            if (em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);

            float fallbackDamage = 3f;
            float fallbackRate = 3f;
            if (em.HasComponent<ShipWeaponConfig>(shipEntity))
            {
                var weapon = em.GetComponentData<ShipWeaponConfig>(shipEntity);
                fallbackDamage = Mathf.Max(0.1f, weapon.BulletDamage);
                fallbackRate = Mathf.Max(0.1f, weapon.FireRate);
            }

            ShipWeaponMountCombatLogic.TryApplyCombatStatsToMounts(
                em,
                shipEntity,
                tier.prefab,
                family,
                shipLevel,
                in attrs,
                fallbackDamage,
                fallbackRate);
        }

        /// <summary>
        /// Computes chassis component mass from prefab transform hierarchy for ShipMassLogic hull reference.
        /// Legacy fallback when box-extent mass returns 0.
        /// </summary>
        static float TryGetChassisComponentMass(string chassisId)
        {
            var config = Config;
            if (config == null || string.IsNullOrEmpty(chassisId))
                return 0f;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier?.prefab == null)
                return 0f;

            string familyPrefix = "AstroEagle";
            int underscore = chassisId.IndexOf('_');
            if (underscore > 0)
                familyPrefix = chassisId.Substring(0, underscore);

            return ChassisComponentStats.ComputeComponentMassFromTransform(tier.prefab.transform, familyPrefix);
        }

        /// <summary>
        /// Live hull component mass from box extents × attribute grow × tier scale.
        /// Used for <see cref="ShipMotorConfig.HullMassReference"/> and capacity mass tax.
        /// </summary>
        static float TryGetLiveHullComponentMass(
            string chassisId,
            in ShipAttributeUpgradeState attrs,
            int shipLevel,
            bool applyAttributeScale)
        {
            var config = Config;
            if (config == null || string.IsNullOrEmpty(chassisId))
                return 0f;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier?.prefab == null)
                return 0f;

            string familyPrefix = "AstroEagle";
            int underscore = chassisId.IndexOf('_');
            if (underscore > 0)
                familyPrefix = chassisId.Substring(0, underscore);

            return ShipHullColliderLogic.ComputeLiveHullComponentMass(
                tier.prefab,
                attrs,
                familyPrefix,
                shipLevel,
                applyAttributeScale);
        }
    }
}
