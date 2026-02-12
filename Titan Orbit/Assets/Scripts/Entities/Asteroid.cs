using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Asteroid - can be mined, destroyed by bullets, collision damage with ships
    /// Spawns gems when destroyed, respawns after delay
    /// </summary>
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
        private float destroyTime;
        private float asteroidSize = 1f;

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

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                spawnPosition = transform.position;
                asteroidSize = Mathf.Max(0.3f, (transform.localScale.x + transform.localScale.y + transform.localScale.z) / 3f);
                float sizeMultiplier = asteroidSize;
                maxGems.Value = baseGemCount * sizeMultiplier;
                remainingGems.Value = maxGems.Value;
                health.Value = baseHealth * sizeMultiplier;
                isDestroyed.Value = false;
            }
        }

        private void Update()
        {
            if (!IsServer) return;
            if (!isDestroyed.Value) return;

            if (Time.time - destroyTime >= respawnTime)
            {
                RespawnServerRpc();
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
            destroyTime = Time.time;

            // Spawn gems
            if (GemSpawner.Instance != null)
            {
                GemSpawner.Instance.SpawnGemsServerRpc(transform.position, remainingGems.Value);
            }

            // Hide asteroid (will respawn)
            SetVisibleClientRpc(false);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RespawnServerRpc()
        {
            transform.position = spawnPosition;
            float sizeMultiplier = asteroidSize;
            maxGems.Value = baseGemCount * sizeMultiplier;
            remainingGems.Value = maxGems.Value;
            health.Value = baseHealth * sizeMultiplier;
            isDestroyed.Value = false;
            SetVisibleClientRpc(true);
        }

        [ClientRpc]
        private void SetVisibleClientRpc(bool visible)
        {
            foreach (var r in GetComponentsInChildren<Renderer>())
                r.enabled = visible;
            foreach (var c in GetComponentsInChildren<Collider>())
                c.enabled = visible;
        }
    }
}
