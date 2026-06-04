using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>Local hit proxy on a drone visual; not networked. Combat authority lives on <see cref="DroneSwarmController"/>.</summary>
    public sealed class DroneBody : MonoBehaviour
    {
        private DroneSwarmController swarm;
        private LootableDrone loot;
        private int equipmentSlotIndex = -1;

        public DroneSwarmController Swarm => swarm;
        public LootableDrone Loot => loot;
        public int EquipmentSlotIndex => equipmentSlotIndex;
        public Starship OwnerShip => swarm != null ? swarm.OwnerShip : null;

        public void Initialize(DroneSwarmController controller, int slotIndex)
        {
            swarm = controller;
            loot = null;
            equipmentSlotIndex = slotIndex;
        }

        public void InitializeAsLoot(LootableDrone lootDrone)
        {
            loot = lootDrone;
            swarm = null;
            equipmentSlotIndex = -1;
        }

        public bool IsDestroyed =>
            loot != null ? loot.IsDestroyed : swarm == null || swarm.IsSlotDestroyed(equipmentSlotIndex);

        public bool IsEnemyTeam(TeamManager.Team team)
        {
            if (loot != null)
                return loot.IsEnemyTeam(team);
            return swarm != null && swarm.IsEnemyTeam(team);
        }
    }
}
