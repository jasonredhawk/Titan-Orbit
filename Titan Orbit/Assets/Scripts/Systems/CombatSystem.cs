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
        [SerializeField] private Transform bulletParent;

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

        [ServerRpc(RequireOwnership = false)]
        public void SpawnBulletServerRpc(Vector3 position, Quaternion rotation, float speed, float damage, TeamManager.Team ownerTeam)
        {
            if (bulletPrefab == null) return;

            GameObject bulletObj = Instantiate(bulletPrefab, position, rotation);
            NetworkObject bulletNetObj = bulletObj.GetComponent<NetworkObject>();
            
            if (bulletNetObj != null)
            {
                bulletNetObj.Spawn();
                
                Bullet bullet = bulletObj.GetComponent<Bullet>();
                if (bullet != null)
                {
                    bullet.Initialize(speed, damage, ownerTeam);
                }
            }

            if (bulletParent != null)
            {
                bulletObj.transform.SetParent(bulletParent);
            }
        }
    }
}
