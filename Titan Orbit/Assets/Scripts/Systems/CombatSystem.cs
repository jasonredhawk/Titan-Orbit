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
        public void SpawnBulletServerRpc(Vector3 position, Vector3 direction, float speed, float damage, TeamManager.Team ownerTeam)
        {
            if (bulletPrefab == null) return;

            // Ensure direction is normalized in XZ plane
            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();

            Quaternion lookRot = Quaternion.LookRotation(dir, Vector3.up);
            GameObject bulletObj = Instantiate(bulletPrefab, position, lookRot);
            Bullet bullet = bulletObj.GetComponent<Bullet>();
            Rigidbody bulletRb = bulletObj.GetComponent<Rigidbody>();

            if (bullet != null)
            {
                bullet.Initialize(speed, damage, ownerTeam);
            }

            if (bulletRb != null)
            {
                bulletRb.linearVelocity = dir * speed;
            }

            NetworkObject bulletNetObj = bulletObj.GetComponent<NetworkObject>();
            if (bulletNetObj != null)
            {
                bulletNetObj.Spawn();
            }

            // Only parent if bulletParent has NetworkObject (Netcode requirement)
            if (bulletParent != null && bulletParent.GetComponent<NetworkObject>() != null)
            {
                bulletObj.transform.SetParent(bulletParent);
            }
        }
    }
}
