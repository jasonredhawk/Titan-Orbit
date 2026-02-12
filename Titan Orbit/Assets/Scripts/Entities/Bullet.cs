using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Bullet/projectile that deals damage on collision
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Bullet : NetworkBehaviour
    {
        [Header("Bullet Settings")]
        [SerializeField] private float speed = 20f;
        [SerializeField] private float damage = 10f;
        [SerializeField] private float lifetime = 5f;
        [SerializeField] private TeamManager.Team ownerTeam = TeamManager.Team.None;

        private Rigidbody rb;
        private float spawnTime;

        public float Damage => damage;
        public TeamManager.Team OwnerTeam => ownerTeam;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
        }

        public override void OnNetworkSpawn()
        {
            spawnTime = Time.time;
            
            if (IsServer)
            {
                // Set velocity
                rb.linearVelocity = transform.forward * speed;
            }
        }

        private void Update()
        {
            if (IsServer && Time.time - spawnTime > lifetime)
            {
                // Destroy bullet after lifetime
                GetComponent<NetworkObject>().Despawn();
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;

            // Check if hit a starship
            Starship ship = other.GetComponent<Starship>();
            if (ship != null && ship.ShipTeam != ownerTeam)
            {
                // Deal damage
                ship.TakeDamageServerRpc(damage, ownerTeam);
                
                // Destroy bullet
                GetComponent<NetworkObject>().Despawn();
            }
        }

        public void Initialize(float bulletSpeed, float bulletDamage, TeamManager.Team team)
        {
            speed = bulletSpeed;
            damage = bulletDamage;
            ownerTeam = team;
        }
    }
}
