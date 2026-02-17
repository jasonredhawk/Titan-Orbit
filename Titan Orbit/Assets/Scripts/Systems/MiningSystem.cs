using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles mining mechanics for asteroids
    /// </summary>
    public class MiningSystem : NetworkBehaviour
    {
        public static MiningSystem Instance { get; private set; }

        [Header("Mining Settings")]
        [SerializeField] private float baseMiningRate = 10f;
        [SerializeField] private float miningRange = 3f;

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

        public bool CanMineAsteroid(Starship ship, Asteroid asteroid)
        {
            if (ship == null || asteroid == null) return false;
            if (!asteroid.CanBeMined()) return false;
            if (ship.CurrentGems >= ship.GemCapacity) return false;

            float distance = ToroidalMap.ToroidalDistance(ship.transform.position, asteroid.transform.position);
            return distance <= miningRange;
        }

        [ServerRpc(RequireOwnership = false)]
        public void MineAsteroidServerRpc(ulong asteroidNetworkId, ulong shipNetworkId, float miningRate)
        {
            NetworkObject asteroidNetObj = GetNetworkObject(asteroidNetworkId);
            NetworkObject shipNetObj = GetNetworkObject(shipNetworkId);

            if (asteroidNetObj == null || shipNetObj == null) return;

            Asteroid asteroid = asteroidNetObj.GetComponent<Asteroid>();
            Starship ship = shipNetObj.GetComponent<Starship>();

            if (asteroid == null || ship == null) return;
            if (!CanMineAsteroid(ship, asteroid)) return;

            // Mine gems from asteroid
            float gemsMined = Mathf.Min(
                miningRate * Time.deltaTime,
                asteroid.RemainingGems,
                ship.GemCapacity - ship.CurrentGems
            );
            if (GameManager.Instance != null && GameManager.Instance.DebugMode) gemsMined *= 100f;

            asteroid.MineGemsServerRpc(gemsMined, shipNetworkId);
            ship.AddGemsServerRpc(gemsMined);

            // Visual feedback
            MiningFeedbackClientRpc(asteroidNetworkId, shipNetworkId, gemsMined);
        }

        [ClientRpc]
        private void MiningFeedbackClientRpc(ulong asteroidNetworkId, ulong shipNetworkId, float gemsMined)
        {
            // Visual/audio feedback for mining
            Debug.Log($"Mined {gemsMined} gems");
        }
    }
}
