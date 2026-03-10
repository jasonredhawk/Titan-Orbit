using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Spawns gem pickups when asteroids are destroyed.
    /// Gems explode outward, slow down, and stop for a visual effect.
    /// </summary>
    public class GemSpawner : NetworkBehaviour
    {
        public static GemSpawner Instance { get; private set; }

        [SerializeField] private GameObject gemPrefab;
        [SerializeField] private GameObject peopleTransportPrefab;
        [SerializeField] private float explosionSpeed = 2f;
        [SerializeField] private float explosionRadius = 1f;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private GameObject GetGemPrefab()
        {
            if (gemPrefab != null) return gemPrefab;
#if UNITY_EDITOR
            gemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Gem.prefab");
            return gemPrefab;
#else
            return null;
#endif
        }

        private GameObject GetPeopleTransportPrefab()
        {
            if (peopleTransportPrefab != null) return peopleTransportPrefab;
#if UNITY_EDITOR
            peopleTransportPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PeopleTransport.prefab");
            return peopleTransportPrefab;
#else
            return null;
#endif
        }

        /// <summary>Spawns people beaming from planet to ship (load).</summary>
        public void SpawnPeopleLoad(Vector3 planetPosition, Vector3 shipPosition, float amount, ulong shipNetworkObjectId, TitanOrbit.Core.TeamManager.Team team)
        {
            SpawnPeopleTransport(planetPosition, shipPosition, amount, shipNetworkObjectId, true, team, 0);
        }

        /// <summary>Spawns people beaming from ship to planet (unload).</summary>
        public void SpawnPeopleUnload(Vector3 shipPosition, Vector3 planetPosition, float amount, ulong planetNetworkObjectId, TitanOrbit.Core.TeamManager.Team team, ulong shipNetworkObjectId)
        {
            SpawnPeopleTransport(shipPosition, planetPosition, amount, planetNetworkObjectId, false, team, shipNetworkObjectId);
        }

        private void SpawnPeopleTransport(Vector3 fromPos, Vector3 toPos, float amount, ulong targetNetworkObjectId, bool isLoad, TitanOrbit.Core.TeamManager.Team team, ulong shipNetworkObjectId)
        {
            GameObject prefab = GetPeopleTransportPrefab();
            if (prefab == null || amount <= 0f) return;

            Vector3 dir = (toPos - fromPos);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            else dir.Normalize();

            Vector3 pos = fromPos;
            float speed = 6f;

            GameObject obj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = dir * speed;
                rb.linearDamping = 0f;
            }

            NetworkObject netObj = obj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                var p = obj.GetComponent<PeopleTransportProjectile>();
                if (p != null) p.Initialize(amount, targetNetworkObjectId, isLoad, team, shipNetworkObjectId);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnGemsServerRpc(Vector3 asteroidCenter, float totalValue, float asteroidSize = 1f, float asteroidPhysicalSize = 0.5f, ulong primaryDamagerShipId = 0)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null) return;

            // asteroidSize and totalValue both 1-70; asteroidPhysicalSize is world scale for gem cap
            if (asteroidSize <= 1.5f && totalValue <= 2f)
            {
                SpawnGem(prefab, asteroidCenter, Mathf.Max(1f, totalValue), 0.3f, asteroidPhysicalSize, primaryDamagerShipId);
                return;
            }

            float normalizedSize = Mathf.Clamp01((asteroidSize - 1f) / (70f - 1f));
            
            int gemCount;
            float minGemValue, maxGemValue;
            
            if (normalizedSize < 0.3f)
            {
                gemCount = Random.Range(2, 5);
                minGemValue = 1f;
                maxGemValue = 22f;
            }
            else if (normalizedSize < 0.7f)
            {
                gemCount = Random.Range(2, 5);
                minGemValue = 1f;
                maxGemValue = 50f;
            }
            else
            {
                if (normalizedSize >= 0.9f)
                {
                    gemCount = Random.Range(1, 4);
                    minGemValue = 28f;
                    maxGemValue = 70f;
                }
                else
                {
                    gemCount = Random.Range(2, 4);
                    minGemValue = 14f;
                    maxGemValue = 63f;
                }
            }
            
            // Distribute totalValue across gems
            float remainingValue = totalValue;
            for (int i = 0; i < gemCount; i++)
            {
                bool isLast = (i == gemCount - 1);
                
                float gemValue;
                if (isLast)
                {
                    gemValue = Mathf.Clamp(remainingValue, minGemValue, Mathf.Min(maxGemValue, 70f));
                }
                else
                {
                    float avgValuePerGem = remainingValue / (gemCount - i);
                    gemValue = Mathf.Clamp(avgValuePerGem * Random.Range(0.7f, 1.3f), minGemValue, Mathf.Min(maxGemValue, 70f));
                }

                gemValue = Mathf.Clamp(gemValue, 1f, 70f);
                
                // Value 1-70: size multipliers scaled up from 1-50
                float sizeMultiplier;
                if (gemValue <= 10f)
                {
                    sizeMultiplier = Mathf.Lerp(0.3f, 0.6f, gemValue / 10f);
                }
                else if (gemValue <= 25f)
                {
                    sizeMultiplier = Mathf.Lerp(0.6f, 1.0f, (gemValue - 10f) / 15f);
                }
                else if (gemValue <= 45f)
                {
                    sizeMultiplier = Mathf.Lerp(1.0f, 1.5f, (gemValue - 25f) / 20f);
                }
                else
                {
                    sizeMultiplier = Mathf.Lerp(1.5f, 2.2f, (gemValue - 45f) / 25f);
                }
                
                // Add some random variation to size
                sizeMultiplier *= Random.Range(0.9f, 1.1f);
                
                SpawnGem(prefab, asteroidCenter, gemValue, sizeMultiplier, asteroidPhysicalSize, primaryDamagerShipId);
                remainingValue -= gemValue;
                
                if (remainingValue <= 0) break;
            }
        }
        
        /// <summary>Spawns gems expelled from a ship when bullets hit after health is zero. Victim ship cannot collect for 3 sec.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void SpawnGemsFromShipServerRpc(Vector3 shipPosition, float totalValue, ulong expelledByShipId)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null || totalValue <= 0f) return;

            // Spawn as one or a few gems (simpler than asteroid distribution)
            float remaining = totalValue;
            int maxGems = Mathf.Min(5, Mathf.CeilToInt(totalValue / 2f));
            if (maxGems < 1) maxGems = 1;
            for (int i = 0; i < maxGems && remaining > 0.1f; i++)
            {
                float gemValue = (i == maxGems - 1) ? remaining : Mathf.Min(remaining, Random.Range(2f, Mathf.Min(remaining, 25f)));
                gemValue = Mathf.Clamp(gemValue, 1f, 50f);
                float sizeMult = Mathf.Lerp(0.4f, 1.2f, Mathf.Clamp01(gemValue / 25f));
                SpawnGemFromShip(prefab, shipPosition, gemValue, sizeMult, expelledByShipId);
                remaining -= gemValue;
            }
        }

        /// <summary>Spawns a gem expelled from ship toward planet for deposit. 1 gem/sec. Value = shipLevel×5; size shows value.</summary>
        public void SpawnDepositGem(Vector3 shipPosition, Vector3 planetPosition, float amount, int shipLevel, ulong planetNetworkObjectId, TitanOrbit.Core.TeamManager.Team depositingTeam, ulong depositingClientId)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null || amount <= 0f) return;

            Vector3 dir = (planetPosition - shipPosition);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            else dir.Normalize();

            Vector3 pos = shipPosition;
            float depositSpeed = 8f;
            // Size scales with gem value (e.g. level 3 = 15 value = 1.5 size)
            float sizeMult = Mathf.Clamp(amount / 10f, 0.5f, 3f);

            GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody rb = gemObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = dir * depositSpeed;
                rb.angularVelocity = new Vector3(Random.Range(-2f, 2f), Random.Range(-2f, 2f), Random.Range(-2f, 2f));
            }

            NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Gem gem = gemObj.GetComponent<Gem>();
                if (gem != null) gem.InitializeForDeposit(amount, sizeMult, planetNetworkObjectId, depositingTeam, depositingClientId);
            }
        }

        private void SpawnGemFromShip(GameObject prefab, Vector3 shipCenter, float gemValue, float sizeMultiplier, ulong expelledByShipId)
        {
            Vector2 dir2 = Random.insideUnitCircle.normalized;
            if (dir2.sqrMagnitude < 0.01f) dir2 = Vector2.up;
            Vector3 dir = new Vector3(dir2.x, 0f, dir2.y);
            Vector3 pos = shipCenter + dir * explosionRadius * Random.Range(0.3f, 1f);

            GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody rb = gemObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = dir * explosionSpeed * Random.Range(0.8f, 1.2f);
                rb.angularVelocity = new Vector3(
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f));
            }

            NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Gem gem = gemObj.GetComponent<Gem>();
                if (gem != null) gem.InitializeFromShip(gemValue, sizeMultiplier, expelledByShipId);
            }
        }

        private void SpawnGem(GameObject prefab, Vector3 asteroidCenter, float gemValue, float sizeMultiplier, float asteroidPhysicalSize, ulong primaryDamagerShipId)
        {
            // Random direction in XZ plane, slightly outward
            Vector2 dir2 = Random.insideUnitCircle.normalized;
            if (dir2.sqrMagnitude < 0.01f) dir2 = Vector2.up;
            Vector3 dir = new Vector3(dir2.x, 0f, dir2.y);
            Vector3 pos = asteroidCenter + dir * explosionRadius * Random.Range(0.3f, 1f);

            GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody rb = gemObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = dir * explosionSpeed * Random.Range(0.8f, 1.2f);
                rb.angularVelocity = new Vector3(
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f));
            }

            NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Gem gem = gemObj.GetComponent<Gem>();
                if (gem != null) gem.Initialize(gemValue, sizeMultiplier, asteroidPhysicalSize, primaryDamagerShipId);
            }
        }
    }
}
