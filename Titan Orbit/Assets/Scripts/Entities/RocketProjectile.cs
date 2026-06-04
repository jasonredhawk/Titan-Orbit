using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

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
        private ulong ownerShipNetworkId;
        private float spawnTime;
        private Vector3 spawnPosition;
        private Rigidbody rb;
        private const float FIXED_Y = 0f;

        public void Initialize(float rocketSpeed, float rocketDamage, TeamManager.Team team, ulong ownerShipId)
        {
            speed = rocketSpeed;
            damage = rocketDamage;
            ownerTeam = team;
            ownerShipNetworkId = ownerShipId;
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
                ship.TakeDamageServerRpc(damage, ownerTeam, ownerShipNetworkId);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        transform.position,
                        (int)FloatingCountChannel.DamageShipOrDrone,
                        damage,
                        (int)ownerTeam
                    );

                Despawn();
                return;
            }
            DroneBody drone = other.GetComponentInParent<DroneBody>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                drone.Swarm?.ApplyDamageFromBullet(drone.EquipmentSlotIndex, damage, ownerTeam, ownerShipNetworkId, transform.position);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        transform.position,
                        (int)FloatingCountChannel.DamageShipOrDrone,
                        damage,
                        (int)ownerTeam
                    );

                Despawn();
                return;
            }
            Asteroid asteroid = other.GetComponent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                asteroid.TakeDamageServerRpc(damage, ownerShipNetworkId);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        transform.position,
                        (int)FloatingCountChannel.DamageAsteroid,
                        damage,
                        (int)ownerTeam
                    );

                Despawn();
            }

            PlanetGemMoon moon = other.GetComponentInParent<PlanetGemMoon>();
            if (moon != null)
            {
                if (moon.IsTeamFriendlyToThisMoon(ownerTeam))
                    return;

                moon.TakeDamageServer(damage, ownerTeam);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        transform.position,
                        (int)FloatingCountChannel.DamageMoon,
                        damage,
                        (int)ownerTeam
                    );

                Despawn();
                return;
            }

            PeopleTransportProjectile peopleTransport = other.GetComponentInParent<PeopleTransportProjectile>();
            if (peopleTransport != null && peopleTransport.PeopleAmount > 0f && peopleTransport.SourceTeam != ownerTeam)
            {
                peopleTransport.ApplyDamageFromBulletServer(damage, ownerTeam, transform.position);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        transform.position,
                        (int)FloatingCountChannel.DamageShipOrDrone,
                        damage,
                        (int)ownerTeam
                    );

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
