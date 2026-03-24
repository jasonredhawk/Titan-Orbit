using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Drone that shoots at asteroids to mine them.</summary>
    public class MiningDrone : DroneBase
    {
        [Header("Mining Drone")]
        [SerializeField] private float fireRate = 1f;
        [SerializeField] private float firePower = 8f;
        [SerializeField] private float bulletSpeed = 16f;
        [SerializeField] private float targetRange = 15f;
        [SerializeField] private Transform firePoint;
        private float lastFireTime;

        protected override void DroneBehaviourServer()
        {
            UpdateOrbitPosition();
            if (ownerShip == null) return;
            if (firePoint == null) firePoint = transform;
            Asteroid target = FindNearestAsteroid();
            if (target != null && !target.IsDestroyed && Time.time - lastFireTime >= 1f / fireRate)
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

        private Asteroid FindNearestAsteroid()
        {
            DroneTargetCache.RefreshIfNeeded();
            Vector3 myPos = transform.position;
            Asteroid nearest = null;
            float nearestSq = targetRange * targetRange;
            foreach (var ast in DroneTargetCache.Asteroids)
            {
                if (ast == null || ast.IsDestroyed) continue;
                float sq = (ToroidalMap.WrapPosition(ast.transform.position - myPos)).sqrMagnitude;
                if (sq < nearestSq) { nearestSq = sq; nearest = ast; }
            }
            return nearest;
        }
    }
}
