using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Bottom-bar attribute upgrades: cost, caps, multipliers, and server purchase validation.</summary>
    public static class ShipAttributeUpgradeLogic
    {
        public const float MultiplierPerLevel = 0.1f;

        public static int GetUpgradeCost(int shipLevel) => shipLevel * 5;

        public static int GetMaxUpgrades(int shipLevel) => shipLevel;

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

        public static void Reset(ref ShipAttributeUpgradeState state) => state = default;

        public static void ApplyMultipliers(ref ShipComponentAbilityStats stats, in ShipAttributeUpgradeState state)
        {
            stats.firePower *= 1f + state.FirePower * MultiplierPerLevel;
            stats.bulletSpeed *= 1f + state.BulletSpeed * MultiplierPerLevel;
            stats.healthCap *= 1f + state.MaxHealth * MultiplierPerLevel;
            stats.healthRegen *= 1f + state.HealthRegen * MultiplierPerLevel;
            stats.energyCap *= 1f + state.EnergyCapacity * MultiplierPerLevel;
            stats.energyRegen *= 1f + state.EnergyRegen * MultiplierPerLevel;
            stats.moveSpeed *= 1f + state.MovementSpeed * MultiplierPerLevel;
            stats.turnSpeed *= 1f + state.RotationSpeed * MultiplierPerLevel;
            stats.maxGems *= 1f + state.GemCapacity * MultiplierPerLevel;
            stats.maxPeople *= 1f + state.PeopleCapacity * MultiplierPerLevel;
        }

        public static void EnsureComponent(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                em.AddComponentData(shipEntity, new ShipAttributeUpgradeState());
        }

        public static bool TryPurchase(EntityManager em, Entity shipEntity, int attributeIndex)
        {
            if (attributeIndex < 0 || attributeIndex > 9)
                return false;
            if (!em.HasComponent<ShipState>(shipEntity))
                return false;

            var ship = em.GetComponentData<ShipState>(shipEntity);
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

            ship.CurrentGems -= cost;
            IncrementAttribute(ref attrs, attributeIndex);
            em.SetComponentData(shipEntity, ship);
            em.SetComponentData(shipEntity, attrs);

            int branch = 0;
            if (em.HasComponent<ShipLoadoutState>(shipEntity))
                branch = em.GetComponentData<ShipLoadoutState>(shipEntity).BranchIndex;

            ShipStatApplyLogic.ApplyToShip(em, shipEntity, ship.Team, ship.ShipLevel, branch);
            return true;
        }

        public static bool TryPurchaseForNetworkId(EntityManager em, int networkId, int attributeIndex, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId <= 0)
                return false;

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
