using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using SciFiArsenal;

namespace TitanOrbit.Systems
{
    /// <summary>One category (folder name) with prefabs sorted by color. Used when Populate Bullet Bank From Demo Prefabs is used.</summary>
    [Serializable]
    public class BulletBankCategory
    {
        public string categoryName;
        public List<GameObject> prefabs = new List<GameObject>();
    }

    /// <summary>
    /// Handles combat mechanics including bullet spawning and damage.
    /// Always spawns the default bullet prefab; bullet prefab bank is used only for visuals (SciFi projectile particle).
    /// When bulletBankCategories is populated (Demo Prefabs), B key cycles one per category and team color picks the variant (e.g. Red Bullets, Red Sparkler).
    /// </summary>
    public class CombatSystem : NetworkBehaviour
    {
        public static CombatSystem Instance { get; private set; }

        [Header("Default Bullet (always spawned)")]
        [Tooltip("Prefab spawned for every bullet. Must have Bullet, NetworkObject, Rigidbody, Collider. Visual is taken from Bullet Prefab Bank (projectile particle).")]
        [SerializeField] private GameObject defaultBulletPrefab;
        [Header("Bullet Visual Bank (flat, legacy)")]
        [Tooltip("Prefabs with SciFiProjectileScript; only projectileParticle is used as the bullet visual. Run Titan Orbit > Populate Bullet Bank From Folder.")]
        [SerializeField] private List<GameObject> bulletPrefabBank = new List<GameObject>();
        [Header("Bullet Bank Categories (Demo Prefabs)")]
        [Tooltip("When populated via Titan Orbit > Populate Bullet Bank From Demo Prefabs, categories are used. B key cycles one per category; team color selects Red/Blue/Green variant.")]
        [SerializeField] private List<BulletBankCategory> bulletBankCategories = new List<BulletBankCategory>();
        [SerializeField] private Transform bulletParent;
        [SerializeField] private GameObject rocketPrefab;
        [SerializeField] private GameObject minePrefab;
        [SerializeField] private int maxBullets = 200; // Limit total bullets to prevent lag
        [Tooltip("Global multiplier for bullet speed. Lower = slower bullets (e.g. 0.4 = 40% of configured speed).")]
        [SerializeField] [Range(0.1f, 2f)] private float bulletSpeedMultiplier = 0.4f;
        [Tooltip("Spawn offset in front of fire position (Sci-Fi Arsenal style). Bullet spawns at position + direction * this value.")]
        [SerializeField] private float spawnOffset = 0.3f;

        [Header("Ship Death Breakup")]
        [Tooltip("Cap for detached physics pieces so huge modular ships stay affordable.")]
        [SerializeField, Range(8, 256)] private int deathDebrisMaxPieces = 64;
        [Tooltip("Minimum horizontal speed for detached ship debris.")]
        [SerializeField, Range(0f, 10f)] private float deathDebrisMinImpulse = 1f;
        [Tooltip("Maximum horizontal speed for detached ship debris.")]
        [SerializeField, Range(0f, 20f)] private float deathDebrisMaxImpulse = 3f;
        [Tooltip("Each shard multiplies its sampled horizontal speed by a value in this range.")]
        [SerializeField, Range(0.05f, 3f)] private float deathDebrisPieceSpeedMulMin = 0.2f;
        [SerializeField, Range(0.05f, 4f)] private float deathDebrisPieceSpeedMulMax = 1.1f;
        [Tooltip("Minimum upward launch speed for detached debris.")]
        [SerializeField, Range(0f, 10f)] private float deathDebrisUpImpulseMin = 0f;
        [Tooltip("Maximum upward launch speed for detached debris.")]
        [SerializeField, Range(0f, 20f)] private float deathDebrisUpImpulseMax = 1.5f;
        [Tooltip("Minimum angular speed for detached debris.")]
        [SerializeField, Range(0f, 40f)] private float deathDebrisAngularVelMin = 2.5f;
        [Tooltip("Maximum angular speed for detached debris.")]
        [SerializeField, Range(0f, 80f)] private float deathDebrisAngularVelMax = 12f;
        [Tooltip("Linear damping applied to detached debris pieces. Higher values settle faster.")]
        [SerializeField, Range(0f, 10f)] private float deathDebrisLinearDamping = 0f;
        [Tooltip("How long detached debris objects live before being destroyed.")]
        [SerializeField, Range(0.25f, 20f)] private float deathDebrisLifetime = 5f;
        [Tooltip("Velocity retained when debris bounces off asteroids. 1 = full reflection, lower = softer bounce.")]
        [SerializeField, Range(0f, 1.5f)] private float deathDebrisAsteroidBounceMultiplier = 0.9f;
        [Tooltip("Minimum debris speed required before asteroid bounce assist applies.")]
        [SerializeField, Range(0f, 5f)] private float deathDebrisAsteroidBounceMinSpeed = 0.15f;
        [Tooltip("When enabled, death debris can absorb and block enemy bullets.")]
        [SerializeField] private bool deathDebrisBlocksEnemyBullets = true;
        [Tooltip("How many enemy bullet hits each debris piece can absorb before breaking.")]
        [SerializeField, Range(1, 20)] private int deathDebrisBulletHitsToBreak = 3;
        [Tooltip("How long debris acts as a bullet shield after death.")]
        [SerializeField, Range(0.1f, 20f)] private float deathDebrisBulletShieldDuration = 5f;

        private static bool loggedBulletPrefabNull;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        /// <summary>When using categories: number of categories (B key cycles these). Otherwise count of flat bullet prefab bank.</summary>
        public int BulletPrefabBankCount => UseCategories ? (bulletBankCategories != null ? bulletBankCategories.Count : 0) : (bulletPrefabBank != null ? bulletPrefabBank.Count : 0);

        private bool UseCategories => bulletBankCategories != null && bulletBankCategories.Count > 0;

        public int DeathDebrisMaxPieces => Mathf.Max(1, deathDebrisMaxPieces);
        public float DeathDebrisMinImpulse => Mathf.Max(0f, deathDebrisMinImpulse);
        public float DeathDebrisMaxImpulse => Mathf.Max(DeathDebrisMinImpulse, deathDebrisMaxImpulse);
        public float DeathDebrisPieceSpeedMulMin => Mathf.Max(0.01f, deathDebrisPieceSpeedMulMin);
        public float DeathDebrisPieceSpeedMulMax => Mathf.Max(DeathDebrisPieceSpeedMulMin, deathDebrisPieceSpeedMulMax);
        public float DeathDebrisUpImpulseMin => Mathf.Max(0f, deathDebrisUpImpulseMin);
        public float DeathDebrisUpImpulseMax => Mathf.Max(DeathDebrisUpImpulseMin, deathDebrisUpImpulseMax);
        public float DeathDebrisAngularVelMin => Mathf.Max(0f, deathDebrisAngularVelMin);
        public float DeathDebrisAngularVelMax => Mathf.Max(DeathDebrisAngularVelMin, deathDebrisAngularVelMax);
        public float DeathDebrisLinearDamping => Mathf.Max(0f, deathDebrisLinearDamping);
        public float DeathDebrisLifetime => Mathf.Max(0.1f, deathDebrisLifetime);
        public float DeathDebrisAsteroidBounceMultiplier => Mathf.Clamp(deathDebrisAsteroidBounceMultiplier, 0f, 2f);
        public float DeathDebrisAsteroidBounceMinSpeed => Mathf.Max(0f, deathDebrisAsteroidBounceMinSpeed);
        public bool DeathDebrisBlocksEnemyBullets => deathDebrisBlocksEnemyBullets;
        public int DeathDebrisBulletHitsToBreak => Mathf.Max(1, deathDebrisBulletHitsToBreak);
        public float DeathDebrisBulletShieldDuration => Mathf.Max(0.1f, deathDebrisBulletShieldDuration);

        /// <summary>Team A=Red, B=Blue, C=Green. Used to pick the matching prefab in each category (e.g. Red Bullets for red team).</summary>
        public static string GetColorNameForTeam(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return "Red";
                case TeamManager.Team.TeamB: return "Blue";
                case TeamManager.Team.TeamC: return "Green";
                case TeamManager.Team.TeamD: return "Red";
                case TeamManager.Team.TeamE: return "Blue";
                default: return "Blue";
            }
        }

        /// <summary>Returns the bullet prefab for the given category index and team. In category mode, picks the prefab whose name contains the team color (e.g. Red). In flat mode, index is direct and team is ignored.</summary>
        public GameObject GetBulletPrefabFromBank(int index, TeamManager.Team team)
        {
            if (UseCategories)
            {
                if (bulletBankCategories == null || index < 0 || index >= bulletBankCategories.Count) return null;
                var cat = bulletBankCategories[index];
                if (cat.prefabs == null || cat.prefabs.Count == 0) return null;
                string colorName = GetColorNameForTeam(team);
                foreach (GameObject p in cat.prefabs)
                {
                    if (p != null && p.name.IndexOf(colorName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return p;
                }
                return cat.prefabs[0];
            }
            return GetBulletPrefabFromBankFlat(index);
        }

        /// <summary>Returns the bullet prefab at the given flat bank index. Used when not using categories.</summary>
        public GameObject GetBulletPrefabFromBankFlat(int index)
        {
            if (bulletPrefabBank == null || index < 0 || index >= bulletPrefabBank.Count) return null;
            return bulletPrefabBank[index];
        }

        /// <summary>Returns the index of the given prefab in the bullet prefab bank, or -1 if not found. In category mode returns category index if prefab belongs to that category.</summary>
        public int GetBulletPrefabIndex(GameObject prefab)
        {
            if (prefab == null) return -1;
            if (UseCategories && bulletBankCategories != null)
            {
                for (int i = 0; i < bulletBankCategories.Count; i++)
                {
                    if (bulletBankCategories[i].prefabs != null && bulletBankCategories[i].prefabs.Contains(prefab))
                        return i;
                }
                return -1;
            }
            if (bulletPrefabBank == null) return -1;
            for (int i = 0; i < bulletPrefabBank.Count; i++)
            {
                if (bulletPrefabBank[i] == prefab) return i;
            }
            return -1;
        }

        /// <summary>Returns the bullet prefab at the given bank index (and team when using categories). Kept for backward compatibility; prefer GetBulletPrefabFromBank(index, team).</summary>
        public GameObject GetBulletPrefabFromBank(int index)
        {
            if (UseCategories)
                return GetBulletPrefabFromBank(index, TeamManager.Team.TeamA);
            return GetBulletPrefabFromBankFlat(index);
        }

        /// <summary>
        /// Returns the display name for a bullet at the given bank index.
        /// In category mode this is the categoryName (e.g. \"Sparkler\", \"Railgun\").
        /// In flat mode this falls back to the prefab name without \"(Clone)\".
        /// </summary>
        public string GetBulletDisplayName(int index)
        {
            if (UseCategories && bulletBankCategories != null && index >= 0 && index < bulletBankCategories.Count)
            {
                var cat = bulletBankCategories[index];
                if (cat != null && !string.IsNullOrEmpty(cat.categoryName))
                    return cat.categoryName;
            }

            // Flat bank or missing category: use prefab name as a fallback
            GameObject prefab = GetBulletPrefabFromBankFlat(index);
            if (prefab != null && !string.IsNullOrEmpty(prefab.name))
            {
                string name = prefab.name;
                int cloneIdx = name.IndexOf("(Clone)", StringComparison.Ordinal);
                if (cloneIdx > 0) name = name.Substring(0, cloneIdx).TrimEnd();
                return name;
            }

            return $"Bullet {index + 1}";
        }

        /// <summary>Returns the visual prefab for a bank index (and team when using categories). Used by Bullet for its visual.</summary>
        public GameObject GetVisualPrefabFromBank(int index, TeamManager.Team team)
        {
            GameObject bankPrefab = GetBulletPrefabFromBank(index, team);
            if (bankPrefab == null) return null;
            var sciFi = bankPrefab.GetComponent<SciFiProjectileScript>();
            if (sciFi != null && sciFi.projectileParticle != null)
                return sciFi.projectileParticle;
            return bankPrefab;
        }

        /// <summary>Legacy single-arg form; uses TeamA when in category mode.</summary>
        public GameObject GetVisualPrefabFromBank(int index)
        {
            return GetVisualPrefabFromBank(index, TeamManager.Team.TeamA);
        }

        /// <summary>Returns the impact effect prefab for a bank index (and team when using categories). Used by Bullet on hit.</summary>
        public GameObject GetImpactPrefabFromBank(int index, TeamManager.Team team)
        {
            GameObject bankPrefab = GetBulletPrefabFromBank(index, team);
            if (bankPrefab == null) return null;
            var sciFi = bankPrefab.GetComponent<SciFiProjectileScript>();
            return (sciFi != null && sciFi.impactParticle != null) ? sciFi.impactParticle : null;
        }

        /// <summary>Server-only spawn used by <see cref="FireServerRpc"/> logic. Returns false if no NetworkObject was spawned (cap, prefab, etc.).</summary>
        public bool TrySpawnBulletOnServer(Vector3 position, Vector3 direction, float speed, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId = 0, float visualScaleMultiplier = 1f, byte bulletShapeIndex = 0, Vector3 shipVelocity = default, int bulletPrefabIndex = -1)
        {
            if (!IsServer) return false;
            // #region agent log 065367
            DebugNdjson065367.Write("H3", "CombatSystem.TrySpawnBulletOnServer", "entry",
                "{\"activeBullets\":" + Bullet.ActiveServerBullets + ",\"maxBullets\":" + maxBullets + "}");
            // #endregion agent log 065367
            GameObject prefabToUse = defaultBulletPrefab != null && defaultBulletPrefab.GetComponent<NetworkObject>() != null
                ? defaultBulletPrefab
                : Resources.Load<GameObject>("Bullet");
            if (prefabToUse == null) prefabToUse = Resources.Load<GameObject>("Prefabs/Bullet");
            if (prefabToUse == null || prefabToUse.GetComponent<NetworkObject>() == null)
            {
                // #region agent log 065367
                DebugNdjson065367.Write("H3", "CombatSystem.TrySpawnBulletOnServer", "early_exit", "{\"reason\":\"prefab_missing_or_no_netobj\"}");
                // #endregion agent log 065367
                if (!loggedBulletPrefabNull)
                {
                    loggedBulletPrefabNull = true;
                    Debug.LogWarning("CombatSystem: Default Bullet Prefab is missing or has no NetworkObject. Assign a prefab with Bullet + NetworkObject + Rigidbody + Collider to CombatSystem Default Bullet Prefab, or add one at Resources/Bullet.prefab.");
                }
                return false;
            }

            int bankCount = BulletPrefabBankCount;
            int requestedBankIndex = (bankCount > 0)
                ? (bulletPrefabIndex >= 0 && bulletPrefabIndex < bankCount ? bulletPrefabIndex : 0)
                : -1;

            int currentBulletCount = Bullet.ActiveServerBullets;
            if (currentBulletCount >= maxBullets)
            {
                // #region agent log 065367
                DebugNdjson065367.Write("H3", "CombatSystem.TrySpawnBulletOnServer", "early_exit",
                    "{\"reason\":\"max_bullets\",\"active\":" + currentBulletCount + ",\"max\":" + maxBullets + "}");
                // #endregion agent log 065367
                return false;
            }

            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();

            float finalSpeed = speed * bulletSpeedMultiplier;

            Vector3 spawnPos = position + dir * spawnOffset;
            Vector3 lookAtTarget = spawnPos + dir * 10f;
            lookAtTarget.y = spawnPos.y;
            Quaternion lookRot = Quaternion.LookRotation((lookAtTarget - spawnPos).normalized, Vector3.up);
            GameObject bulletObj = Instantiate(prefabToUse, spawnPos, lookRot);
            Bullet bullet = bulletObj.GetComponent<Bullet>();
            Rigidbody bulletRb = bulletObj.GetComponent<Rigidbody>();

            if (bullet == null || bulletRb == null)
            {
                // #region agent log 065367
                DebugNdjson065367.Write("H3", "CombatSystem.TrySpawnBulletOnServer", "early_exit", "{\"reason\":\"missing_bullet_or_rb\"}");
                // #endregion agent log 065367
                UnityEngine.Object.Destroy(bulletObj);
                return false;
            }
            if (bulletObj.GetComponent<Collider>() == null && bulletObj.GetComponentInChildren<Collider>() == null)
                Debug.LogWarning($"CombatSystem: Default bullet prefab has no Collider. Bullets will not detect hits.");

            bullet.Initialize(finalSpeed, damage, ownerTeam, ownerShipNetworkId, visualScaleMultiplier, bulletShapeIndex, false, requestedBankIndex);

            bulletRb.useGravity = false;
            bulletRb.isKinematic = false;
            bulletRb.interpolation = RigidbodyInterpolation.Interpolate;
            bulletRb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            Vector3 flatShipVel = new Vector3(shipVelocity.x, 0f, shipVelocity.z);
            Vector3 totalVelocity = dir * finalSpeed + flatShipVel;
            bulletRb.AddForce(totalVelocity, ForceMode.VelocityChange);

            NetworkObject bulletNetObj = bulletObj.GetComponent<NetworkObject>();
            if (bulletNetObj == null)
            {
                // #region agent log 065367
                DebugNdjson065367.Write("H3", "CombatSystem.TrySpawnBulletOnServer", "early_exit", "{\"reason\":\"bullet_no_netobj\"}");
                // #endregion agent log 065367
                Debug.LogWarning($"CombatSystem: Default bullet prefab has no NetworkObject. Assign Default Bullet Prefab on CombatSystem or run Titan Orbit > Populate Bullet Bank From Folder.");
                UnityEngine.Object.Destroy(bulletObj);
                return false;
            }
            bulletNetObj.Spawn();
            // #region agent log 065367
            DebugNdjson065367.Write("H1", "CombatSystem.TrySpawnBulletOnServer", "spawn_ok",
                "{\"bulletNetId\":" + bulletNetObj.NetworkObjectId + ",\"ownerShipNetworkId\":" + ownerShipNetworkId + "}");
            // #endregion agent log 065367
            if (bulletParent != null && bulletParent.GetComponent<NetworkObject>() != null)
                bulletObj.transform.SetParent(bulletParent);
            return true;
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnBulletServerRpc(Vector3 position, Vector3 direction, float speed, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId = 0, float visualScaleMultiplier = 1f, byte bulletShapeIndex = 0, Vector3 shipVelocity = default, int bulletPrefabIndex = -1)
        {
            TrySpawnBulletOnServer(position, direction, speed, damage, ownerTeam, ownerShipNetworkId, visualScaleMultiplier, bulletShapeIndex, shipVelocity, bulletPrefabIndex);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnRocketServerRpc(Vector3 position, Vector3 direction, bool isLarge, TeamManager.Team ownerTeam, ulong ownerShipNetworkId = 0)
        {
            if (rocketPrefab == null) return;
            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();
            float speed = isLarge ? 20f : 24f;
            float damage = isLarge ? 55f : 25f;
            Quaternion lookRot = Quaternion.LookRotation(dir, Vector3.up);
            GameObject go = Instantiate(rocketPrefab, position, lookRot);
            var rocket = go.GetComponent<RocketProjectile>();
            if (rocket != null) rocket.Initialize(speed, damage, ownerTeam, ownerShipNetworkId);
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) rb.linearVelocity = dir * speed;
            var no = go.GetComponent<NetworkObject>();
            if (no != null) no.Spawn();
            if (bulletParent != null && bulletParent.GetComponent<NetworkObject>() != null)
                go.transform.SetParent(bulletParent);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnMineServerRpc(Vector3 position, bool isLarge, TeamManager.Team ownerTeam, ulong ownerShipNetworkId = 0)
        {
            if (minePrefab == null) return;
            Vector3 pos = position;
            pos.y = 0f;
            GameObject go = Instantiate(minePrefab, pos, Quaternion.identity);
            var mine = go.GetComponent<Mine>();
            if (mine != null)
            {
                float damage = isLarge ? 70f : 35f;
                float radius = isLarge ? 7f : 4f;
                mine.Initialize(damage, radius, ownerTeam, ownerShipNetworkId);
            }
            var no = go.GetComponent<NetworkObject>();
            if (no != null) no.Spawn();
        }
    }
}
