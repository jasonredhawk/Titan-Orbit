using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>Tracks which chassis stats were last applied to a ship.</summary>
    public struct ShipChassisState : IComponentData
    {
        public FixedString64Bytes ChassisId;
        public int AppliedShipLevel;
        public int AppliedBranchIndex;
    }

    /// <summary>Applies summed ship-family stats to ECS ship components.</summary>
    public static class ShipStatApplyLogic
    {
        static PlanetShipFamilyConfig s_config;

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

        public static void InvalidateConfigCache() => s_config = null;

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

            int homePlanetId = FindHomePlanetIdForTeam(team);
            if (homePlanetId <= 0)
                homePlanetId = 0;

            chassisId = config.GetChassisIdForLadderSlot(
                homePlanetId,
                shipLevel,
                branchIndex,
                isHomePlanet: true,
                shipFamilyConfigIndex: PlanetShipFamilyAssignment.HomeFamilyConfigIndex);

            if (string.IsNullOrEmpty(chassisId))
            {
                chassisId = config.GetChassisIdForPlanetAndIndex(
                    0, 0, isHomePlanet: true, shipFamilyConfigIndex: PlanetShipFamilyAssignment.HomeFamilyConfigIndex);
            }

            return !string.IsNullOrEmpty(chassisId);
        }

        static int FindHomePlanetIdForTeam(TeamId team)
        {
            // Default home planet id used by bootstrap when team-specific lookup is unavailable.
            return team != TeamId.None ? 1 : 0;
        }

        public static bool TryGetBaseStatsForChassis(string chassisId, int shipLevel, out ShipComponentAbilityStats baseStats)
        {
            baseStats = default;
            var config = Config;
            if (config == null || string.IsNullOrEmpty(chassisId))
                return false;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier == null)
                return false;

            ShipFamilyDefinition family = null;
            if (config.families != null)
            {
                int underscore = chassisId.IndexOf('_');
                if (underscore > 0)
                {
                    string prefix = chassisId.Substring(0, underscore);
                    for (int i = 0; i < config.families.Count; i++)
                    {
                        var entry = config.families[i];
                        if (entry?.shipFamilyDefinition != null &&
                            string.Equals(entry.shipFamilyDefinition.familyId, prefix, System.StringComparison.OrdinalIgnoreCase))
                        {
                            family = entry.shipFamilyDefinition;
                            break;
                        }
                    }
                }
            }

            if (tier.prefab != null && family != null &&
                ShipFamilyStatsCalculator.TrySumFromPrefab(tier.prefab, family, shipLevel, out baseStats))
                return true;

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

        public static void ApplyToShip(EntityManager em, Entity shipEntity, TeamId team, int shipLevel, int branchIndex)
        {
            ApplyToShip(em, shipEntity, team, shipLevel, branchIndex, default, queueStructuralChanges: false);
        }

        public static void ApplyToShip(
            EntityManager em,
            Entity shipEntity,
            TeamId team,
            int shipLevel,
            int branchIndex,
            EntityCommandBuffer ecb,
            bool queueStructuralChanges)
        {
            if (!TryResolveChassisId(team, shipLevel, branchIndex, out string chassisId))
                return;

            if (!TryGetBaseStatsForChassis(chassisId, shipLevel, out ShipComponentAbilityStats summed))
                return;

            ShipComponentAbilityStats effective = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(summed, shipLevel);

            if (em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
            {
                var attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
                ShipAttributeUpgradeLogic.ApplyMultipliers(ref effective, attrs);
            }

            if (em.HasComponent<ShipState>(shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(shipEntity);
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

            if (em.HasComponent<ShipMotorConfig>(shipEntity))
            {
                float moveVal = Mathf.Max(0.1f, effective.moveSpeed);
                float turnVal = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(effective.turnSpeed);
                float thrust = Mathf.Max(0.1f, effective.accelerationCap > 0f
                    ? effective.accelerationCap
                    : moveVal);
                thrust *= ShipPropulsionAggregation.EngineThrustVisibility;

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

            var chassisState = new ShipChassisState
            {
                ChassisId = chassisId,
                AppliedShipLevel = shipLevel,
                AppliedBranchIndex = branchIndex,
            };
            if (em.HasComponent<ShipChassisState>(shipEntity))
                em.SetComponentData(shipEntity, chassisState);
            else if (queueStructuralChanges)
                ecb.AddComponent(shipEntity, chassisState);
            else
                em.AddComponentData(shipEntity, chassisState);
        }

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
