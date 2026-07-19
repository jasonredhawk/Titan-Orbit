using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
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
        /// Sum of ghosted <see cref="ShipAttributeUpgradeState"/> levels at last apply.
        /// Client re-applies motor when attribute RPCs land without a level change.
        /// </summary>
        public int AppliedAttributeSum;
    }

    /// <summary>
    /// Shared stat-application pipeline: resolves a chassis id from team + level + branch, sums
    /// ship-family component stats, applies attribute-upgrade multipliers, and writes the result
    /// onto ShipState, ShipWeaponConfig, ShipMotorConfig, and ShipVitalsConfig. Called by
    /// ShipStatApplySystem (server + client prediction), ShipAttributeUpgradeLogic (purchase),
    /// and respawn/rejoin flows. Does not run movement — only updates numeric caps and motor tuning.
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

        static PlanetShipFamilyConfig LoadConfig()
        {
            var config = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            if (config != null)
                return config;
            return Resources.Load<PlanetShipFamilyConfig>("Data/PlanetShipFamilyConfig");
        }

        /// <summary>Clears cached config — call after hot-reload or editor asset changes.</summary>
        public static void InvalidateConfigCache() => s_config = null;

        /// <summary>
        /// Maps team + ship level + branch index to a chassis id string from the home-planet ladder.
        /// Falls back to planet 0 / index 0 when lookup fails.
        /// </summary>
        public static bool TryResolveChassisId(
            TeamId team,
            int shipLevel,
            int branchIndex,
            out string chassisId)
        {
            chassisId = null;
            var config = Config;
            if (config == null)
                return false;

            // [TITAN-ORBIT] Home planet id drives which ship-family ladder slot is used.
            int homePlanetId = FindHomePlanetIdForTeam(team);
            if (homePlanetId <= 0)
                homePlanetId = 0;

            chassisId = config.GetChassisIdForLadderSlot(
                homePlanetId,
                shipLevel,
                branchIndex,
                isHomePlanet: true,
                shipFamilyConfigIndex: PlanetShipFamilyAssignment.HomeFamilyConfigIndex);

            // [STANDARD] Fallback chassis when ladder lookup returns empty.
            if (string.IsNullOrEmpty(chassisId))
            {
                chassisId = config.GetChassisIdForPlanetAndIndex(
                    0, 0, isHomePlanet: true, shipFamilyConfigIndex: PlanetShipFamilyAssignment.HomeFamilyConfigIndex);
            }

            return !string.IsNullOrEmpty(chassisId);
        }

        /// <summary>
        /// [TITAN-ORBIT] Default home planet id when team-specific lookup is unavailable.
        /// Bootstrap assigns planet 1 as the generic home for any non-None team.
        /// </summary>
        static int FindHomePlanetIdForTeam(TeamId team)
        {
            return team != TeamId.None ? 1 : 0;
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
        /// at the given ship level. Output is base stats before attribute-upgrade multipliers.
        /// </summary>
        public static bool TryGetBaseStatsForChassis(string chassisId, int shipLevel, out ShipComponentAbilityStats baseStats)
        {
            baseStats = default;
            var config = Config;
            if (config == null || string.IsNullOrEmpty(chassisId))
                return false;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier == null)
                return false;

            // --- Resolve ship family from chassis id prefix (e.g. "AstroEagle_T2" → AstroEagle) ---
            TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family);

            // [TITAN-ORBIT] Prefer summing stats from the baked chassis prefab hierarchy.
            if (tier.prefab != null && family != null &&
                ShipFamilyStatsCalculator.TrySumFromPrefab(tier.prefab, family, shipLevel, out baseStats))
                return true;

            // Fallback: use tier power-score breakdown or family default stats.
            if (tier.powerScoreBreakdown.HasDisplayStats)
            {
                baseStats = ShipFamilyStatsCalculator.BreakdownToBaseStats(tier.powerScoreBreakdown);
                if (family != null)
                    baseStats = family.ApplyStatFallbacks(baseStats);
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
            if (!TryResolveChassisId(team, shipLevel, branchIndex, out string chassisId))
                return;

            if (!TryGetBaseStatsForChassis(chassisId, shipLevel, out ShipComponentAbilityStats summed))
                return;

            // [TITAN-ORBIT] Level scaling curve applied before attribute multipliers.
            ShipComponentAbilityStats effective = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(summed, shipLevel);

            int attributeSum = 0;
            // --- Attribute upgrades (+10% per level from bottom HUD) ---
            if (em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
            {
                var attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
                attributeSum = SumAttributeLevels(attrs);
                ShipAttributeUpgradeLogic.ApplyMultipliers(ref effective, attrs);
            }

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
                ship.Health = Mathf.Clamp(ship.Health, 0f, ship.MaxHealth);
                if (ship.Health <= 0.01f || ship.AwaitingTeamSelection)
                    ship.Health = ship.MaxHealth;
                else
                    ship.Health = Mathf.Clamp(ship.MaxHealth * prevHealthRatio, 1f, ship.MaxHealth);

                ship.CurrentEnergy = Mathf.Min(ship.CurrentEnergy, ship.MaxEnergy);
                if (ship.CurrentEnergy <= 0.01f)
                    ship.CurrentEnergy = ship.MaxEnergy;

                ship.CurrentGems = Mathf.Min(ship.CurrentGems, ship.GemCapacity);
                ship.CurrentPeople = Mathf.Min(ship.CurrentPeople, ship.PeopleCapacity);
                em.SetComponentData(shipEntity, ship);
            }

            // --- Weapon tuning (server-authoritative bullet sim reads these) ---
            if (em.HasComponent<ShipWeaponConfig>(shipEntity))
            {
                float firePower = Mathf.Max(0.1f, effective.firePower);
                float fireRate = Mathf.Max(0.1f, effective.fireRate);
                float bulletSpeed = Mathf.Max(0.1f, effective.bulletSpeed);
                var weapon = em.GetComponentData<ShipWeaponConfig>(shipEntity);
                weapon.FireRate = fireRate;
                weapon.BulletSpeed = bulletSpeed;
                weapon.BulletDamage = firePower;
                weapon.EnergyCostPerShot = firePower;
                if (weapon.ReferenceBulletDamage <= 0.01f)
                    weapon.ReferenceBulletDamage = firePower;
                if (weapon.ReferenceBulletSpeed <= 0.01f)
                    weapon.ReferenceBulletSpeed = bulletSpeed;
                em.SetComponentData(shipEntity, weapon);
            }

            // --- Bullet VFX bank from ShipFamilyDefinition.bulletPrefabIndex ---
            // [NETCODE] RuntimeBulletIndex is ghosted — server only; clients read replica for anticipation.
            if (writeGhostedShipState &&
                em.HasComponent<ShipLoadoutState>(shipEntity) &&
                TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition bankFamily))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                loadout.RuntimeBulletIndex = BulletBankProfileUtility.ResolveBankIndexForFamily(bankFamily);
                em.SetComponentData(shipEntity, loadout);
            }

            // --- Physics tuning (ShipPhysicsDriveSystem reads these) ---
            if (em.HasComponent<ShipMotorConfig>(shipEntity))
            {
                float moveVal = Mathf.Max(0.1f, effective.moveSpeed);
                float turnVal = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(effective.turnSpeed);
                float thrust = Mathf.Max(0.1f, effective.accelerationCap > 0f
                    ? effective.accelerationCap
                    : moveVal);
                thrust *= ShipPropulsionAggregation.EngineThrustVisibility;

                // [TITAN-ORBIT] Mass reference uses level-1 health so upgrades change weight feel.
                ShipComponentAbilityStats levelOneStats =
                    ShipComponentStoreData.GetEffectiveStatsAtShipLevel(summed, 1);
                float referenceHealth = Mathf.Max(1f, levelOneStats.healthCap);
                float componentMass = TryGetChassisComponentMass(chassisId);
                float hullMassReference = ShipMassLogic.ComputeHullMassReference(
                    componentMass,
                    ShipMassLogic.DefaultBaseMass);

                var motor = em.GetComponentData<ShipMotorConfig>(shipEntity);
                motor.MaxSpeed = moveVal;
                motor.EngineThrust = thrust;
                motor.RotationSpeed = turnVal;
                motor.BrakeDeceleration = ShipMassLogic.DefaultBrakeDeceleration;
                motor.HullMassReference = hullMassReference;
                motor.ChassisReferenceHealth = referenceHealth;
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

            // --- Bookkeeping so ShipStatApplySystem skips unchanged ships ---
            var chassisState = new ShipChassisState
            {
                ChassisId = chassisId,
                AppliedShipLevel = shipLevel,
                AppliedBranchIndex = branchIndex,
                AppliedAttributeSum = attributeSum,
            };
            if (em.HasComponent<ShipChassisState>(shipEntity))
                em.SetComponentData(shipEntity, chassisState);
            else if (queueStructuralChanges)
                ecb.AddComponent(shipEntity, chassisState);
            else
                em.AddComponentData(shipEntity, chassisState);
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
        /// Computes chassis component mass from prefab transform hierarchy for ShipMassLogic hull reference.
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
    }
}
