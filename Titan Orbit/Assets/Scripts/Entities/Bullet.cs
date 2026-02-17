using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Bullet - hits asteroids and ships, despawns on hit or max distance/lifetime.
    /// Uses path raycast to prevent tunneling when close.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Bullet : NetworkBehaviour
    {
        [Header("Bullet Settings")]
        [SerializeField] private float speed = 20f;
        [SerializeField] private float damage = 10f;
        [SerializeField] private float lifetime = 2f; // Reduced from 10f for better performance
        [SerializeField] private float maxDistance = 10f; // Reduced from 200f to match attack range
        [SerializeField] private float minTravelBeforeHit = 0.5f;
        [SerializeField] private TeamManager.Team ownerTeam = TeamManager.Team.None;

        private const float FIXED_Y_POSITION = 0f;
        private Rigidbody rb;
        private float spawnTime;
        private Vector3 spawnPosition;
        private Vector3 lastPosition;

        public float Damage => damage;
        public TeamManager.Team OwnerTeam => ownerTeam;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                // Lock Y position - bullets stay on same plane
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }
        }

        public override void OnNetworkSpawn()
        {
            // Lock Y position to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            
            spawnTime = Time.time;
            spawnPosition = transform.position;
            lastPosition = spawnPosition;
            
            // Check for immediate overlaps when spawning (fixes close-range tunneling)
            if (IsServer)
            {
                CheckImmediateOverlaps();
            }
        }

        private void FixedUpdate()
        {
            // Always lock Y position (prevents drift)
            Vector3 pos = transform.position;
            if (Mathf.Abs(pos.y - FIXED_Y_POSITION) > 0.01f)
            {
                pos.y = FIXED_Y_POSITION;
                transform.position = pos;
            }
            
            // Ensure rigidbody velocity has no Y component
            if (rb != null && Mathf.Abs(rb.linearVelocity.y) > 0.01f)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel;
            }
            
            if (!IsServer) return;

            float dist = Vector3.Distance(transform.position, spawnPosition);
            if (dist > maxDistance || Time.time - spawnTime > lifetime)
            {
                DespawnBullet();
                return;
            }

            // Always check for collisions, even if close (fixes tunneling bug)
            Vector3 to = transform.position;
            float pathLen = Vector3.Distance(lastPosition, to);
            
            if (pathLen > 0.001f)
            {
                // Use SphereCast instead of Raycast for better detection of fast-moving bullets
                float bulletRadius = 0.3f; // Larger radius to reliably hit ships (BoxCollider ~0.5 wide)
                if (Physics.SphereCast(lastPosition, bulletRadius, (to - lastPosition).normalized, out RaycastHit hit, pathLen, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform != transform && !hit.collider.transform.IsChildOf(transform))
                    {
                        if (TryHit(hit.collider))
                            return; // Hit valid target, despawned
                        DespawnBullet(); // Hit something else (planet, etc) - despawn to avoid getting stuck
                        return;
                    }
                }
            }

            lastPosition = transform.position;
        }

        private void CheckImmediateOverlaps()
        {
            // Check if bullet spawned inside or very close to an asteroid/ship
            float checkRadius = 0.5f;
            Collider[] overlaps = Physics.OverlapSphere(transform.position, checkRadius, ~0, QueryTriggerInteraction.Ignore);
            
            foreach (Collider col in overlaps)
            {
                if (col.transform != transform && !col.transform.IsChildOf(transform))
                {
                    // Only hit if it's an asteroid or enemy ship (not the shooter)
                    Asteroid asteroid = col.GetComponentInParent<Asteroid>();
                    if (asteroid != null && !asteroid.IsDestroyed)
                    {
                        TryHit(col);
                        return;
                    }
                    
                    Starship ship = col.GetComponentInParent<Starship>();
                    if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
                    {
                        TryHit(col);
                        return;
                    }
                    DroneBase drone = col.GetComponentInParent<DroneBase>();
                    if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
                    {
                        TryHit(col);
                        return;
                    }
                }
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
            if (collision != null && collision.collider != null)
                TryHit(collision.collider);
        }

        /// <returns>True if we hit a valid target (asteroid, ship, drone) and despawned.</returns>
        private bool TryHit(Collider other)
        {
            if (other == null) return false;

            // Use GetComponentInParent to handle child colliders (e.g. ship sub-meshes)
            Asteroid asteroid = other.GetComponentInParent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                float appliedDamage = damage;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                    appliedDamage = 999999f; // One-shot asteroids in debug mode
                asteroid.TakeDamageServerRpc(appliedDamage);
                DespawnBullet();
                return true;
            }

            Starship ship = other.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                ship.TakeDamageServerRpc(damage, ownerTeam);
                DespawnBullet();
                return true;
            }

            DroneBase drone = other.GetComponentInParent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                drone.TakeDamageServerRpc(damage, ownerTeam);
                DespawnBullet();
                return true;
            }

            return false;
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
