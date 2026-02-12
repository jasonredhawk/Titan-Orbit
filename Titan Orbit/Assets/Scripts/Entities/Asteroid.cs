using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Represents an asteroid that can be mined for gems
    /// </summary>
    public class Asteroid : NetworkBehaviour
    {
        [Header("Asteroid Settings")]
        [SerializeField] private float baseGemCount = 100f;
        [SerializeField] private float asteroidSize = 1f;
        [SerializeField] private float miningRadius = 3f;

        private NetworkVariable<float> remainingGems = new NetworkVariable<float>(0f);
        private NetworkVariable<float> maxGems = new NetworkVariable<float>(100f);
        private NetworkVariable<bool> isDepleted = new NetworkVariable<bool>(false);

        public float RemainingGems => remainingGems.Value;
        public float MaxGems => maxGems.Value;
        public bool IsDepleted => isDepleted.Value;
        public float AsteroidSize => asteroidSize;
        public float MiningRadius => miningRadius;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                // Initialize gem count based on size
                float sizeMultiplier = asteroidSize;
                maxGems.Value = baseGemCount * sizeMultiplier;
                remainingGems.Value = maxGems.Value;
                isDepleted.Value = false;
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void MineGemsServerRpc(float miningRate, ulong clientId)
        {
            if (isDepleted.Value) return;

            float gemsToMine = Mathf.Min(miningRate * Time.deltaTime, remainingGems.Value);
            remainingGems.Value -= gemsToMine;

            if (remainingGems.Value <= 0f)
            {
                remainingGems.Value = 0f;
                isDepleted.Value = true;
                DepleteAsteroidClientRpc();
            }

            // Return gems to client (this would typically be handled by a mining system)
            // For now, we'll just update the value
        }

        [ClientRpc]
        private void DepleteAsteroidClientRpc()
        {
            // Visual feedback for depleted asteroid
            if (TryGetComponent<Renderer>(out var renderer))
            {
                renderer.material.color = Color.gray;
            }
        }

        public float GetMiningRate(float baseMiningRate, float shipMiningMultiplier)
        {
            return baseMiningRate * shipMiningMultiplier;
        }

        public bool CanBeMined()
        {
            return !isDepleted.Value && remainingGems.Value > 0f;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, miningRadius);
        }
    }
}
