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
    /// Bottom-bar attribute upgrades: gem cost, per-level caps, and server-side purchase validation.
    /// Ability purchase counts feed <see cref="ShipComponentExtraLevelMath"/> —
    /// non-weapons <c>+(N−1)</c>, weapons ship+ability only per barrel — applied in
    /// <see cref="ShipStatApplyLogic"/>. Client sends PurchaseAttributeUpgradeCommand RPC;
    /// ShipAttributeUpgradeSystem invokes TryPurchaseForNetworkId on the server.
    /// </summary>
    public static class ShipAttributeUpgradeLogic
    {
        /// <summary>
        /// [LEGACY] Old ×1.1-per-purchase multiplier. Extra Level formula replaced this;
        /// kept only for any stray VFX references that still read the constant.
        /// </summary>
        public const float MultiplierPerLevel = 0.1f;

        /// <summary>
        /// Copies ghosted ability purchases into the Data-layer struct used by Extra Level math.
        /// </summary>
        public static ShipAbilityLevelCounts ToAbilityLevelCounts(in ShipAttributeUpgradeState state) =>
            new ShipAbilityLevelCounts
            {
                FirePower = state.FirePower,
                BulletSpeed = state.BulletSpeed,
                MaxHealth = state.MaxHealth,
                HealthRegen = state.HealthRegen,
                EnergyCapacity = state.EnergyCapacity,
                EnergyRegen = state.EnergyRegen,
                MovementSpeed = state.MovementSpeed,
                RotationSpeed = state.RotationSpeed,
                GemCapacity = state.GemCapacity,
                PeopleCapacity = state.PeopleCapacity,
            };

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
        /// [LEGACY] No-op — Extra Level formula already includes ability purchases.
        /// </summary>
        public static void ApplyMultipliers(ref ShipComponentAbilityStats stats, in ShipAttributeUpgradeState state)
        {
            _ = stats;
            _ = state;
        }

        /// <summary>
        /// [LEGACY] No-op — Move Speed ability is part of Extra Level evaluation.
        /// </summary>
        public static void ApplyMoveSpeedAbilitySteps(
            ref ShipComponentAbilityStats stats,
            in ShipAttributeUpgradeState state,
            float moveSpeedPerExtraLevel,
            float accelerationCapPerExtraLevel,
            float extraSpeedEnergyDrainPerExtraLevel)
        {
            _ = stats;
            _ = state;
            _ = moveSpeedPerExtraLevel;
            _ = accelerationCapPerExtraLevel;
            _ = extraSpeedEnergyDrainPerExtraLevel;
        }

        /// <summary>
        /// Resolves authored Move Speed Per Extra Level steps from a primary chassis sum (HUD tooltips).
        /// </summary>
        public static void ResolveMoveSpeedAbilitySteps(
            in ShipComponentAbilityStats levelOneSummed,
            out float moveStep,
            out float accelStep,
            out float odDrainStep)
        {
            float frac = ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase;
            moveStep = levelOneSummed.moveSpeedPerExtraLevel > 0.0001f
                ? levelOneSummed.moveSpeedPerExtraLevel
                : Mathf.Max(0f, levelOneSummed.moveSpeed * frac);
            accelStep = levelOneSummed.accelerationCapPerExtraLevel > 0.0001f
                ? levelOneSummed.accelerationCapPerExtraLevel
                : Mathf.Max(0f, levelOneSummed.accelerationCap * frac);
            odDrainStep = levelOneSummed.extraSpeedEnergyDrainPerExtraLevel > 0.0001f
                ? levelOneSummed.extraSpeedEnergyDrainPerExtraLevel
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

            // [TITAN-ORBIT] MEGA hulls are static — no bottom-bar attribute upgrades.
            if (em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
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
