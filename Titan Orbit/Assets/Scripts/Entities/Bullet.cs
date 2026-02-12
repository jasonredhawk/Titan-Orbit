using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Bullet/projectile - uses Raycast for reliable hit detection, despawns on hit or max distance/lifetime
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Bullet : NetworkBehaviour
    {
        [Header("Bullet Settings")]
        [SerializeField] private float speed = 20f;
        [SerializeField] private float damage = 10f;
        [SerializeField] private float lifetime = 10f;
        [SerializeField] private float maxDistance = 200f;
        [SerializeField] private float minTravelBeforeHit = 3f;
        [SerializeField] private TeamManager.Team ownerTeam = TeamManager.Team.None;

        private Rigidbody rb;
        private float spawnTime;
        private Vector3 spawnPosition;

        public float Damage => damage;
        public TeamManager.Team OwnerTeam => ownerTeam;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
        }

        public override void OnNetworkSpawn()
        {
            spawnTime = Time.time;
            spawnPosition = transform.position;
        }

        private void FixedUpdate()
        {
            if (!IsServer) return;

            // Max distance
            float dist = Vector3.Distance(transform.position, spawnPosition);
            if (dist > maxDistance)
            {
                GetComponent<NetworkObject>().Despawn();
                return;
            }

            // Max lifetime
            if (Time.time - spawnTime > lifetime)
            {
                GetComponent<NetworkObject>().Despawn();
                return;
            }

            // Only check for hits after traveling min distance (avoids spawn-area false hits)
            if (dist < minTravelBeforeHit) return;

            // SphereCast - only look ahead one frame + small buffer (prevents tunneling without "reaching" far ahead)
            Vector3 vel = rb.linearVelocity;
            if (vel.sqrMagnitude > 0.01f)
            {
                float castDist = vel.magnitude * Time.fixedDeltaTime;
                float radius = 0.3f;
                if (Physics.SphereCast(transform.position, radius, vel.normalized, out RaycastHit hit, castDist, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider != null && hit.collider.transform != transform && !hit.collider.transform.IsChildOf(transform))
                    {
                        TryHit(hit.collider);
                    }
                }
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;
            float dist = Vector3.Distance(transform.position, spawnPosition);
            if (dist < minTravelBeforeHit) return; // Ignore hits in spawn area
            TryHit(other);
        }

        private void TryHit(Collider other)
        {
            if (other == null) return;

            Asteroid asteroid = other.GetComponent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                asteroid.TakeDamageServerRpc(damage);
                DespawnBullet();
                return;
            }

            Starship ship = other.GetComponent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                ship.TakeDamageServerRpc(damage, ownerTeam);
                DespawnBullet();
            }
        }

        private void DespawnBullet()
        {
            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }

        public void Initialize(float bulletSpeed, float bulletDamage, TeamManager.Team team)
        {
            speed = bulletSpeed;
            damage = bulletDamage;
            ownerTeam = team;
        }
    }
}
