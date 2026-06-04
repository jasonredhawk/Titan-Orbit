using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Entities;

namespace TitanOrbit.Systems
{
    /// <summary>Shared knockback / pull impulses for bullet-bank on-hit effects.</summary>
    public static class BulletImpactForceUtility
    {
        public static void ApplyKnockbackFromImpact(
            Collider hitCollider,
            Vector3 impactWorldPos,
            float impulse,
            bool pull,
            TeamManager.Team ownerTeam)
        {
            if (hitCollider == null || impulse <= 0f) return;

            Starship ship = hitCollider.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                ship.ApplyBulletKnockbackOnServer(impactWorldPos, impulse, pull);
                return;
            }

            DroneBody drone = hitCollider.GetComponentInParent<DroneBody>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam) && drone.Loot == null)
            {
                drone.Swarm?.ApplyKnockbackFromBullet(drone.EquipmentSlotIndex, impactWorldPos, impulse, pull);
                return;
            }

            Gem gem = hitCollider.GetComponentInParent<Gem>();
            if (gem != null && !gem.IsInPool)
                gem.ApplyBulletKnockbackOnServer(impactWorldPos, impulse, pull);
        }
    }
}
