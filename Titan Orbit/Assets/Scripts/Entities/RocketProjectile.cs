using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Rocket projectile - like Bullet but with configurable speed/damage for small vs large rockets.
    /// Hits ships, drones, asteroids. No friendly fire (same team).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class RocketProjectile : NetworkBehaviour
    {
        [Header("Rocket")]
        [SerializeField] private float lifetime = 8f;
        [SerializeField] private float maxDistance = 150f;

        private float speed = 25f;
        private float damage = 25f;
        private TeamManager.Team ownerTeam;
        private float spawnTime;
        private Vector3 spawnPosition;
        private Rigidbody rb;
        private const float FIXED_Y = 0f;

        public void Initialize(float rocketSpeed, float rocketDamage, TeamManager.Team team)
        {
            speed = rocketSpeed;
            damage = rocketDamage;
            ownerTeam = team;
        }

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
        }

        public override void OnNetworkSpawn()
        {
            Vector3 p = transform.position;
            p.y = FIXED_Y;
            transform.position = p;
            spawnTime = Time.time;
            spawnPosition = transform.position;
        }

        private void FixedUpdate()
        {
            Vector3 p = transform.position;
            p.y = FIXED_Y;
            transform.position = p;
            if (rb != null && Mathf.Abs(rb.linearVelocity.y) > 0.01f)
            {
                var v = rb.linearVelocity;
                v.y = 0f;
                rb.linearVelocity = v;
            }
            if (!IsServer) return;

            if (Vector3.Distance(transform.position, spawnPosition) > maxDistance || Time.time - spawnTime > lifetime)
            {
                Despawn();
                return;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;
            TryHit(other);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServer) return;
            TryHit(collision.collider);
        }

        private void TryHit(Collider other)
        {
            if (other == null) return;

            Starship ship = other.GetComponent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                ship.TakeDamageServerRpc(damage, ownerTeam);
                Despawn();
                return;
            }
            DroneBase drone = other.GetComponent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                drone.TakeDamageServerRpc(damage, ownerTeam);
                Despawn();
                return;
            }
            Asteroid asteroid = other.GetComponent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                asteroid.TakeDamageServerRpc(damage);
                Despawn();
            }
        }

        private void Despawn()
        {
            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }
    }
}
