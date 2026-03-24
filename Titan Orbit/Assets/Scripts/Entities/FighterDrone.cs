using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Drone that attacks only enemy ships.</summary>
    public class FighterDrone : DroneBase
    {
        [Header("Fighter Drone")]
        [SerializeField] private float fireRate = 1.2f;
        [SerializeField] private float firePower = 6f;
        [SerializeField] private float bulletSpeed = 18f;
        [SerializeField] private float targetRange = 25f;
        [SerializeField] private Transform firePoint;
        private float lastFireTime;

        protected override void DroneBehaviourServer()
        {
            UpdateOrbitPosition();
            if (ownerShip == null || TeamManager.Instance == null) return;
            if (firePoint == null) firePoint = transform;
            Starship target = FindNearestEnemyShip();
            if (target != null && Time.time - lastFireTime >= 1f / fireRate)
            {
                Vector3 dir = (target.transform.position - firePoint.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                {
                    dir.Normalize();
                    if (CombatSystem.Instance != null)
                    {
                        CombatSystem.Instance.SpawnBulletServerRpc(firePoint.position, dir, bulletSpeed, firePower, ownerShip.ShipTeam, ownerShip.NetworkObjectId);
                        lastFireTime = Time.time;
                    }
                }
            }
        }

        private Starship FindNearestEnemyShip()
        {
            if (ownerShip == null) return null;
            DroneTargetCache.RefreshIfNeeded();
            TeamManager.Team myTeam = ownerShip.ShipTeam;
            Vector3 myPos = transform.position;
            Starship nearest = null;
            float nearestSq = targetRange * targetRange;
            foreach (var ship in DroneTargetCache.Ships)
            {
                if (ship == null || ship.IsDead || ship.ShipTeam == myTeam) continue;
                float sq = (ToroidalMap.WrapPosition(ship.transform.position - myPos)).sqrMagnitude;
                if (sq < nearestSq) { nearestSq = sq; nearest = ship; }
            }
            return nearest;
        }
    }
}
