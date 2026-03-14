using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles combat mechanics including bullet spawning and damage
    /// </summary>
    public class CombatSystem : NetworkBehaviour
    {
        public static CombatSystem Instance { get; private set; }

        [Header("Combat Settings")]
        [SerializeField] private GameObject bulletPrefab;
        [Tooltip("All bullet prefabs that can be used by ships (e.g. from ShipFamilyDefinition). Add your default bullet and any Archanor/other options here. Same list and order on all builds for networking. Prefabs must have Bullet, Rigidbody, and NetworkObject.")]
        [SerializeField] private List<GameObject> bulletPrefabBank = new List<GameObject>();
        [SerializeField] private Transform bulletParent;
        [SerializeField] private GameObject rocketPrefab;
        [SerializeField] private GameObject minePrefab;
        [SerializeField] private int maxBullets = 200; // Limit total bullets to prevent lag
        [Tooltip("Global multiplier for bullet speed. Lower = slower bullets (e.g. 0.4 = 40% of configured speed).")]
        [SerializeField] [Range(0.1f, 2f)] private float bulletSpeedMultiplier = 0.4f;

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
            if (bulletPrefab == null)
                bulletPrefab = Resources.Load<GameObject>("Bullet");
        }

        /// <summary>Number of entries in the bullet prefab bank. ShipFamilyDefinition indices are validated against this.</summary>
        public int BulletPrefabBankCount => bulletPrefabBank != null ? bulletPrefabBank.Count : 0;

        /// <summary>Returns the index of the given prefab in the bullet prefab bank, or -1 if not found. Used so ships can pass an index over the network.</summary>
        public int GetBulletPrefabIndex(GameObject prefab)
        {
            if (prefab == null || bulletPrefabBank == null) return -1;
            for (int i = 0; i < bulletPrefabBank.Count; i++)
            {
                if (bulletPrefabBank[i] == prefab) return i;
            }
            return -1;
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnBulletServerRpc(Vector3 position, Vector3 direction, float speed, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId = 0, float visualScaleMultiplier = 1f, byte bulletShapeIndex = 0, Vector3 shipVelocity = default, int bulletPrefabIndex = -1)
        {
            GameObject prefabToUse = null;
            if (bulletPrefabIndex >= 0 && bulletPrefabBank != null && bulletPrefabIndex < bulletPrefabBank.Count && bulletPrefabBank[bulletPrefabIndex] != null)
                prefabToUse = bulletPrefabBank[bulletPrefabIndex];
            if (prefabToUse == null)
                prefabToUse = bulletPrefab;

            if (prefabToUse == null)
            {
                if (!loggedBulletPrefabNull)
                {
                    loggedBulletPrefabNull = true;
                    Debug.LogWarning("CombatSystem: bulletPrefab is not assigned. Bullets will not spawn. Use menu Titan Orbit > Setup Game Scene (or assign Bullet prefab in Inspector) and save the scene.");
                }
                return;
            }
            bool isAIBullet = false;
            if (ownerShipNetworkId != 0 && NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager != null)
            {
                var spawned = NetworkManager.Singleton.SpawnManager.SpawnedObjects;
                if (spawned != null && spawned.TryGetValue(ownerShipNetworkId, out NetworkObject ownerObj) && ownerObj != null)
                {
                    isAIBullet = ownerObj.GetComponent<TitanOrbit.AI.AIShipMarker>() != null;
                }
            }
            int currentBulletCount = Bullet.ActiveServerBullets;
            if (currentBulletCount >= maxBullets)
            {
                return;
            }

            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();

            float finalSpeed = speed * bulletSpeedMultiplier;

            Quaternion lookRot = Quaternion.LookRotation(dir, Vector3.up);
            GameObject bulletObj = Instantiate(prefabToUse, position, lookRot);
            Bullet bullet = bulletObj.GetComponent<Bullet>();
            Rigidbody bulletRb = bulletObj.GetComponent<Rigidbody>();

            // If bank prefab has no Bullet component, fall back to default so bullets still fire
            if (bullet == null)
            {
                Object.Destroy(bulletObj);
                if (prefabToUse != bulletPrefab && bulletPrefab != null)
                {
                    Debug.LogWarning($"CombatSystem: Prefab '{prefabToUse.name}' has no Bullet component. Using default bullet. Add Bullet, Rigidbody, NetworkObject, and Collider to bank prefabs for damage.");
                    prefabToUse = bulletPrefab;
                    bulletObj = Instantiate(prefabToUse, position, lookRot);
                    bullet = bulletObj.GetComponent<Bullet>();
                    bulletRb = bulletObj.GetComponent<Rigidbody>();
                }
                if (bullet == null) return;
            }
            if (bullet != null && bulletObj.GetComponent<Collider>() == null && bulletObj.GetComponentInChildren<Collider>() == null)
            {
                Debug.LogWarning($"CombatSystem: Prefab '{prefabToUse.name}' has no Collider. Bullets will not detect hits. Add a Collider to the Bullet prefab.");
            }

            bullet.Initialize(finalSpeed, damage, ownerTeam, ownerShipNetworkId, visualScaleMultiplier, bulletShapeIndex, false);

            if (bulletRb != null)
            {
                Vector3 flatShipVel = new Vector3(shipVelocity.x, 0f, shipVelocity.z);
                bulletRb.linearVelocity = dir * finalSpeed + flatShipVel;
            }

            NetworkObject bulletNetObj = bulletObj.GetComponent<NetworkObject>();
            if (bulletNetObj != null)
                bulletNetObj.Spawn();

            if (bulletParent != null && bulletParent.GetComponent<NetworkObject>() != null)
                bulletObj.transform.SetParent(bulletParent);
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
