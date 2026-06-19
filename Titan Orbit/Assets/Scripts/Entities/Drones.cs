using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Shared cache for drone target lookups. Refreshes periodically to avoid FindObjectsOfType every FixedUpdate per drone.
    /// Bullet threats are sourced from <see cref="CombatSystem.CopyActiveBulletSnapshots"/> (struct-based simulation),
    /// not from per-bullet NetworkObjects, since the server-authoritative bullet path has no GameObject per bullet.
    /// </summary>
    public static class DroneTargetCache
    {
        private const int MaxBulletSnapshots = 512;

        private static Starship[] cachedShips = new Starship[0];
        private static Asteroid[] cachedAsteroids = new Asteroid[0];
        private static readonly ServerBulletSnapshot[] bulletScratch = new ServerBulletSnapshot[MaxBulletSnapshots];
        private static int bulletSnapshotCount;
        private static float lastRefreshTime = -999f;
        private const float RefreshInterval = 0.25f;

        public static void RefreshIfNeeded()
        {
            if (Time.time - lastRefreshTime < RefreshInterval) return;
            lastRefreshTime = Time.time;
            cachedShips = Object.FindObjectsByType<Starship>(FindObjectsSortMode.None);
            cachedAsteroids = Object.FindObjectsByType<Asteroid>(FindObjectsSortMode.None);
            bulletSnapshotCount = CombatSystem.Instance != null
                ? CombatSystem.Instance.CopyActiveBulletSnapshots(bulletScratch)
                : 0;
        }

        public static Starship[] Ships => cachedShips;
        public static Asteroid[] Asteroids => cachedAsteroids;
        public static int BulletSnapshotCount => bulletSnapshotCount;
        public static ServerBulletSnapshot GetBulletSnapshot(int index) => bulletScratch[index];
    }
}
