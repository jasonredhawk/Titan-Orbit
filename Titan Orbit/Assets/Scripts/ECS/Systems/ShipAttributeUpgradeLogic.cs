using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Bottom-bar attribute upgrades: gem cost, per-level caps, stat multipliers, and server-side
    /// purchase validation. Most abilities add +10% per purchase; Move Speed instead adds one step
    /// of authored <c>moveSpeedPerAbilityLevel</c>, <c>accelerationCapPerAbilityLevel</c>, and
    /// <c>extraSpeedEnergyDrainPerAbilityLevel</c> together. Purchases call ShipStatApplyLogic to
    /// refresh ShipState / motor / weapon caps. Client sends PurchaseAttributeUpgradeCommand RPC;
    /// ShipAttributeUpgradeSystem invokes TryPurchaseForNetworkId on the server.
    /// </summary>
    public static class ShipAttributeUpgradeLogic
    {
        /// <summary>
        /// Each non-Move-Speed upgrade level multiplies that stat by (1 + level × this value).
        /// Move Speed uses additive *PerAbilityLevel steps — see <see cref="ApplyMoveSpeedAbilitySteps"/>.
        /// </summary>
        public const float MultiplierPerLevel = 0.1f;

        /// <summary>Gem cost per purchase — scales with ship level (stronger ships pay more).</summary>
        public static int GetUpgradeCost(int shipLevel) => shipLevel * 5;

        /// <summary>Max upgrade levels per attribute equals current ship level.</summary>
        public static int GetMaxUpgrades(int shipLevel) => shipLevel;

        /// <summary>Maps attribute index 0–9 to the corresponding field on ShipAttributeUpgradeState.</summary>
        public static int GetAttributeLevel(in ShipAttributeUpgradeState state, int index)
        {
            return index switch
            {
                0 => state.FirePower,
                1 => state.BulletSpeed,
                2 => state.MaxHealth,
                3 => state.HealthRegen,
                4 => state.EnergyCapacity,
                5 => state.EnergyRegen,
                6 => state.MovementSpeed,
                7 => state.RotationSpeed,
                8 => state.GemCapacity,
                9 => state.PeopleCapacity,
                _ => 0,
            };
        }

        /// <summary>Increments one attribute field by index (0–9).</summary>
        public static void IncrementAttribute(ref ShipAttributeUpgradeState state, int index)
        {
            switch (index)
            {
                case 0: state.FirePower++; break;
                case 1: state.BulletSpeed++; break;
                case 2: state.MaxHealth++; break;
                case 3: state.HealthRegen++; break;
                case 4: state.EnergyCapacity++; break;
                case 5: state.EnergyRegen++; break;
                case 6: state.MovementSpeed++; break;
                case 7: state.RotationSpeed++; break;
                case 8: state.GemCapacity++; break;
                case 9: state.PeopleCapacity++; break;
            }
        }

        /// <summary>Clears all upgrade levels (e.g. on chassis level-up).</summary>
        public static void Reset(ref ShipAttributeUpgradeState state) => state = default;

        /// <summary>
        /// Applies +10% per level multipliers onto summed chassis stats (non-Move-Speed abilities).
        /// Call <see cref="ApplyMoveSpeedAbilitySteps"/> afterward for Move Speed purchases.
        /// </summary>
        public static void ApplyMultipliers(ref ShipComponentAbilityStats stats, in ShipAttributeUpgradeState state)
        {
            stats.firePower *= 1f + state.FirePower * MultiplierPerLevel;
            stats.bulletSpeed *= 1f + state.BulletSpeed * MultiplierPerLevel;
            stats.healthCap *= 1f + state.MaxHealth * MultiplierPerLevel;
            stats.healthRegen *= 1f + state.HealthRegen * MultiplierPerLevel;
            stats.energyCap *= 1f + state.EnergyCapacity * MultiplierPerLevel;
            stats.energyRegen *= 1f + state.EnergyRegen * MultiplierPerLevel;
            // [TITAN-ORBIT] Move Speed is additive *PerAbilityLevel — not ×1.1 (see ApplyMoveSpeedAbilitySteps).
            stats.turnSpeed *= 1f + state.RotationSpeed * MultiplierPerLevel;
            stats.maxGems *= 1f + state.GemCapacity * MultiplierPerLevel;
            stats.maxPeople *= 1f + state.PeopleCapacity * MultiplierPerLevel;
        }

        /// <summary>
        /// Bottom-HUD Move Speed ability: each purchase adds one step of authored chassis
        /// <paramref name="moveSpeedPerAbilityLevel"/>, <paramref name="accelerationCapPerAbilityLevel"/>,
        /// and <paramref name="extraSpeedEnergyDrainPerAbilityLevel"/> (all three together).
        /// Steps come from the level-1 summed propulsion stats, not a flat 10%.
        /// </summary>
        public static void ApplyMoveSpeedAbilitySteps(
            ref ShipComponentAbilityStats stats,
            in ShipAttributeUpgradeState state,
            float moveSpeedPerAbilityLevel,
            float accelerationCapPerAbilityLevel,
            float extraSpeedEnergyDrainPerAbilityLevel)
        {
            int n = Mathf.Max(0, state.MovementSpeed);
            if (n <= 0)
                return;

            // --- Additive ability steps (same purchase grows cruise, thrust, and OD drain) ---
            stats.moveSpeed += n * Mathf.Max(0f, moveSpeedPerAbilityLevel);
            stats.accelerationCap += n * Mathf.Max(0f, accelerationCapPerAbilityLevel);
            stats.extraSpeedEnergyDrain += n * Mathf.Max(0f, extraSpeedEnergyDrainPerAbilityLevel);
        }

        /// <summary>
        /// Resolves Move Speed ability step sizes from a level-1 chassis sum.
        /// When a step is still 0, falls back to base × propulsion Scan fraction so HUD purchases
        /// still do something before designers re-Scan.
        /// </summary>
        public static void ResolveMoveSpeedAbilitySteps(
            in ShipComponentAbilityStats levelOneSummed,
            out float moveStep,
            out float accelStep,
            out float odDrainStep)
        {
            float frac = ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase;
            moveStep = levelOneSummed.moveSpeedPerAbilityLevel > 0.0001f
                ? levelOneSummed.moveSpeedPerAbilityLevel
                : Mathf.Max(0f, levelOneSummed.moveSpeed * frac);
            accelStep = levelOneSummed.accelerationCapPerAbilityLevel > 0.0001f
                ? levelOneSummed.accelerationCapPerAbilityLevel
                : Mathf.Max(0f, levelOneSummed.accelerationCap * frac);
            odDrainStep = levelOneSummed.extraSpeedEnergyDrainPerAbilityLevel > 0.0001f
                ? levelOneSummed.extraSpeedEnergyDrainPerAbilityLevel
                : Mathf.Max(0f, levelOneSummed.extraSpeedEnergyDrain * frac);
        }

        /// <summary>Ensures ShipAttributeUpgradeState exists on the ship (added on first purchase).</summary>
        public static void EnsureComponent(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                em.AddComponentData(shipEntity, new ShipAttributeUpgradeState());
        }

        /// <summary>
        /// Server-side purchase: validates gems, cap, and ship eligibility; deducts cost, increments attribute,
        /// and re-applies stats via ShipStatApplyLogic.
        /// </summary>
        public static bool TryPurchase(EntityManager em, Entity shipEntity, int attributeIndex)
        {
            if (attributeIndex < 0 || attributeIndex > 9)
                return false;
            if (!em.HasComponent<ShipState>(shipEntity))
                return false;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            // [TITAN-ORBIT] No upgrades while dead, picking team, or without a team.
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
                return false;

            EnsureComponent(em, shipEntity);
            var attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);

            int current = GetAttributeLevel(attrs, attributeIndex);
            int max = GetMaxUpgrades(ship.ShipLevel);
            if (current >= max)
                return false;

            int cost = GetUpgradeCost(ship.ShipLevel);
            if (ship.CurrentGems < cost - 0.01f)
                return false;

            // --- Deduct gems and bump attribute level ---
            ship.CurrentGems -= cost;
            // [TITAN-ORBIT] Spending the last gems while hull is already 0 is lethal (dual-resource death).
            float h = ship.Health;
            float g = ship.CurrentGems;
            bool dead = ship.IsDead;
            ShipDamageLogic.TryMarkDeadIfHullAndGemsDepleted(ref h, ref g, ref dead);
            ship.Health = h;
            ship.CurrentGems = g;
            ship.IsDead = dead;
            IncrementAttribute(ref attrs, attributeIndex);
            em.SetComponentData(shipEntity, ship);
            em.SetComponentData(shipEntity, attrs);

            // Spent last gems at 0 hull — death recording / respawn will handle cleanup.
            if (ship.IsDead)
                return true;

            int branch = 0;
            if (em.HasComponent<ShipLoadoutState>(shipEntity))
                branch = em.GetComponentData<ShipLoadoutState>(shipEntity).BranchIndex;

            // [TITAN-ORBIT] Refresh motor/weapon/vitals from new effective stats.
            ShipStatApplyLogic.ApplyToShip(em, shipEntity, ship.Team, ship.ShipLevel, branch);
            return true;
        }

        /// <summary>
        /// Finds ship ghost by NetCode NetworkId and runs TryPurchase. Used by ShipAttributeUpgradeSystem RPC handler.
        /// </summary>
        public static bool TryPurchaseForNetworkId(EntityManager em, int networkId, int attributeIndex, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId <= 0)
                return false;

            // [NETCODE] GhostOwner.NetworkId ties RPC sender to their ship entity.
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = entities[i];
                return TryPurchase(em, shipEntity, attributeIndex);
            }

            return false;
        }
    }
}
