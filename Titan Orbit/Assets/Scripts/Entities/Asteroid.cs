using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Asteroid - can be mined, destroyed by bullets, collision damage with ships.
    /// When destroyed: despawn and respawn a fresh instance after delay (avoids state corruption).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class Asteroid : NetworkBehaviour
    {
        [Header("Asteroid Settings")]
        [SerializeField] private float baseGemCount = 100f;
        [SerializeField] private float baseHealth = 50f;
        [SerializeField] private float respawnTime = 30f;

        private NetworkVariable<float> remainingGems = new NetworkVariable<float>(0f);
        private NetworkVariable<float> maxGems = new NetworkVariable<float>(100f);
        private NetworkVariable<float> health = new NetworkVariable<float>(50f);
        private NetworkVariable<bool> isDestroyed = new NetworkVariable<bool>(false);

        private Vector3 spawnPosition;
        private Vector3 spawnScale;
        private float asteroidSize = 1f;
        private Rigidbody rb;
        private Collider col;
        private Vector3 rotationAxis;
        private float rotationSpeed;

        public float RemainingGems => remainingGems.Value;
        public float MaxGems => maxGems.Value;
        public float AsteroidSize => asteroidSize;
        public bool IsDestroyed => isDestroyed.Value;

        public bool CanBeMined() => !isDestroyed.Value && remainingGems.Value > 0;

        [ServerRpc(RequireOwnership = false)]
        public void MineGemsServerRpc(float amount, ulong minerNetworkId)
        {
            if (isDestroyed.Value) return;
            remainingGems.Value = Mathf.Max(0, remainingGems.Value - amount);
        }

        private const float FIXED_Y_POSITION = 0f;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            col = GetComponent<Collider>();
            
            // Ensure proper collision detection for kinematic objects to detect fast-moving bullets/ships
            if (rb != null)
            {
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                // Lock Y position - asteroids stay on same plane
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }
            
            // Ensure collider is enabled and not a trigger (for ship collisions)
            if (col != null)
            {
                col.enabled = true;
                if (col is SphereCollider sphereCol)
                {
                    sphereCol.isTrigger = false;
                }
            }
        }

        public override void OnNetworkSpawn()
        {
            // Lock Y position to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            
            if (IsServer)
            {
                spawnPosition = transform.position;
                spawnScale = transform.localScale;
                asteroidSize = Mathf.Max(0.3f, (spawnScale.x + spawnScale.y + spawnScale.z) / 3f);
                float sizeMultiplier = asteroidSize;
                maxGems.Value = baseGemCount * sizeMultiplier;
                remainingGems.Value = maxGems.Value;
                health.Value = baseHealth * sizeMultiplier;
                isDestroyed.Value = false;
                
                // Set up rotation - deterministic based on position (same for all clients)
                // Use position hash to ensure same rotation for all clients
                int hash = (int)(spawnPosition.x * 1000 + spawnPosition.z * 1000);
                System.Random rng = new System.Random(hash);
                rotationAxis = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    0f, // Keep rotation in XZ plane
                    (float)(rng.NextDouble() * 2 - 1)
                ).normalized;
                rotationSpeed = 20f + (float)(rng.NextDouble() * 30f); // Faster rotation speed (20-50 degrees per second)
                
                // Ensure physics state is correct
                EnsurePhysicsState();
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
            
            // Gentle rotation - all clients can see it
            if (!isDestroyed.Value && rotationAxis.sqrMagnitude > 0.01f)
            {
                transform.Rotate(rotationAxis, rotationSpeed * Time.fixedDeltaTime, Space.World);
            }
            
            if (!IsServer) return;
            
            // Safeguard: ensure collider stays enabled (prevents corruption bug)
            if (col != null && !col.enabled && !isDestroyed.Value)
            {
                col.enabled = true;
            }
        }

        private void EnsurePhysicsState()
        {
            if (rb != null)
            {
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.isKinematic = true;
            }
            if (col != null)
            {
                col.enabled = true;
                if (col is SphereCollider sphereCol)
                {
                    sphereCol.isTrigger = false;
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage)
        {
            if (isDestroyed.Value) return;

            health.Value = Mathf.Max(0, health.Value - damage);
            if (health.Value <= 0)
            {
                DestroyAsteroidServerRpc();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void DestroyAsteroidServerRpc()
        {
            if (isDestroyed.Value) return;
            isDestroyed.Value = true;

            Vector3 pos = transform.position;
            Vector3 scale = transform.localScale;

            // Spawn gems
            if (GemSpawner.Instance != null)
            {
                GemSpawner.Instance.SpawnGemsServerRpc(pos, remainingGems.Value, asteroidSize);
            }

            // Schedule respawn and despawn - fresh instance avoids state corruption
            if (AsteroidRespawnManager.Instance != null)
            {
                AsteroidRespawnManager.Instance.ScheduleRespawn(pos, scale, respawnTime);
            }

            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }
    }
}
