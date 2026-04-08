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
        [Tooltip("When false, asteroid gems use Instantiate+Spawn (original behavior). Set true to use GemPool for fewer allocations.")]
        [SerializeField] private bool useGemPool = false;
        [SerializeField] private float explosionSpeed = 2f;
        [SerializeField] private float explosionRadius = 1f;
        [Tooltip("Asteroid gem burst - kept much lower so gems don't fly away.")]
        [SerializeField] private float asteroidExplosionSpeed = 2.2f;
        [SerializeField] private float asteroidExplosionRadius = 1.4f;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private void Start()
        {
            if (GemPool.Instance != null)
                GemPool.Instance.SetPrefab(GetGemPrefab());
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

        /// <summary>
        /// Must run after <see cref="NetworkObject.Spawn"/> (and Gem Initialize*) so <see cref="Unity.Netcode.Components.NetworkRigidbody"/>
        /// has applied server authority; velocity set before spawn does not reliably persist.
        /// </summary>
        private static void ApplyGemLaunchVelocityAfterSpawn(Rigidbody r, Vector3 linearVelocity, Vector3 angularVelocity)
        {
            if (r == null) return;
            r.isKinematic = false;
            r.linearVelocity = linearVelocity;
            r.angularVelocity = angularVelocity;
            r.WakeUp();
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
        public void SpawnPeopleLoad(Vector3 planetPosition, Vector3 shipPosition, float amount, ulong shipNetworkObjectId, ulong sourcePlanetNetworkObjectId, TitanOrbit.Core.TeamManager.Team team)
        {
            SpawnPeopleTransport(planetPosition, shipPosition, amount, shipNetworkObjectId, true, team, 0, sourcePlanetNetworkObjectId);
        }

        /// <summary>Spawns people beaming from ship to planet (unload).</summary>
        public void SpawnPeopleUnload(Vector3 shipPosition, Vector3 planetPosition, float amount, ulong planetNetworkObjectId, TitanOrbit.Core.TeamManager.Team team, ulong shipNetworkObjectId)
        {
            SpawnPeopleTransport(shipPosition, planetPosition, amount, planetNetworkObjectId, false, team, shipNetworkObjectId, 0);
        }

        private void SpawnPeopleTransport(Vector3 fromPos, Vector3 toPos, float amount, ulong targetNetworkObjectId, bool isLoad, TitanOrbit.Core.TeamManager.Team team, ulong shipNetworkObjectId, ulong sourcePlanetNetworkObjectId)
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
                if (p != null) p.Initialize(amount, targetNetworkObjectId, isLoad, team, shipNetworkObjectId, sourcePlanetNetworkObjectId);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnGemsServerRpc(
            Vector3 asteroidCenter,
            float regularValue,
            float bonusValue,
            float asteroidSize = 1f,
            float asteroidPhysicalSize = 0.5f,
            ulong primaryDamagerShipId = 0)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null) return;
            regularValue = Mathf.Max(0f, regularValue);
            bonusValue = Mathf.Max(0f, bonusValue);
            if (regularValue <= 0f && bonusValue <= 0f) return;

            // 1–3 red gems whose values sum exactly to regularValue (asteroid's remaining gem worth).
            // Cannot split into more physical gems than floor(value) when value < 3 (each gem keeps positive share).
            int maxRedByValue = Mathf.Max(1, Mathf.Min(3, Mathf.FloorToInt(regularValue)));
            int redGemCount = Random.Range(1, maxRedByValue + 1);
            // Reserve room for at least one yellow gem when a triangle bonus applies (still ≤ 3 gems total burst).
            if (bonusValue > 0f && redGemCount >= 3)
                redGemCount = 2;

            float gemWorth = regularValue / redGemCount; // Red gems sum to regularValue exactly.
            if (gemWorth <= 0.001f) return;

            // Bonus gem count is derived from bonusValue but capped so asteroid bursts stay at 1-3 gems max.
            int maxBonusGems = Mathf.Max(0, 3 - redGemCount);
            int bonusGemCount = 0;
            if (bonusValue > 0f && gemWorth > 0.0001f && maxBonusGems > 0)
            {
                float rawBonusGems = bonusValue / gemWorth;
                bonusGemCount = Mathf.Clamp(Mathf.RoundToInt(rawBonusGems), 0, maxBonusGems);
                // Make the visual bonus show up whenever there is bonus.
                if (bonusGemCount == 0 && rawBonusGems > 0f)
                    bonusGemCount = 1;
            }

            // Visual size comes from Gem (linear in value); tiny jitter only.
            for (int i = 0; i < redGemCount; i++)
            {
                float sizeMultiplier = Random.Range(0.96f, 1.04f);
                SpawnGem(prefab, asteroidCenter, gemWorth, sizeMultiplier, asteroidPhysicalSize, primaryDamagerShipId, false);
            }

            for (int i = 0; i < bonusGemCount; i++)
            {
                float sizeMultiplier = Random.Range(0.96f, 1.04f);
                SpawnGem(prefab, asteroidCenter, gemWorth, sizeMultiplier, asteroidPhysicalSize, primaryDamagerShipId, true);
            }
        }
        
        /// <summary>Spawns gems expelled from a ship when bullets hit after health is zero. Victim ship cannot re-collect for a short cooldown.</summary>
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
                float sizeMult = Mathf.Lerp(0.58f, 1.2f, Mathf.Clamp01(gemValue / 25f));
                SpawnGemFromShip(prefab, shipPosition, gemValue, sizeMult, expelledByShipId);
                remaining -= gemValue;
            }
        }

        /// <summary>Spawns a gem expelled from ship toward planet for deposit. Value = amount passed in; size shows value.</summary>
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
            float sizeMult = Mathf.Clamp(amount / 10f, 0.5f, 3f);
            Vector3 vel = dir * depositSpeed;
            Vector3 angVel = new Vector3(Random.Range(-2f, 2f), Random.Range(-2f, 2f), Random.Range(-2f, 2f));

            Gem gem = null;
            if (GemPool.Instance != null)
                gem = GemPool.Instance.GetNext();
            if (gem != null)
            {
                gem.InitializeForDeposit(amount, sizeMult, planetNetworkObjectId, depositingTeam, depositingClientId);
                gem.ServerActivateFromPool();
                gem.ServerFinishPooledSpawn(pos, vel, angVel, 0f);
                return;
            }

            GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody r = gemObj.GetComponent<Rigidbody>();

            NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Gem g = gemObj.GetComponent<Gem>();
                if (g != null) g.InitializeForDeposit(amount, sizeMult, planetNetworkObjectId, depositingTeam, depositingClientId);
                ApplyGemLaunchVelocityAfterSpawn(r, vel, angVel);
            }
        }

        private void SpawnGemFromShip(GameObject prefab, Vector3 shipCenter, float gemValue, float sizeMultiplier, ulong expelledByShipId)
        {
            Vector2 dir2 = Random.insideUnitCircle.normalized;
            if (dir2.sqrMagnitude < 0.01f) dir2 = Vector2.up;
            Vector3 dir = new Vector3(dir2.x, 0f, dir2.y);
            Vector3 pos = shipCenter + dir * explosionRadius * Random.Range(0.3f, 1f);
            Vector3 vel = dir * explosionSpeed * Random.Range(0.8f, 1.2f);
            Vector3 angVel = new Vector3(Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f));

            // Do not use GemPool here: recycling + NetworkTransform/physics ordering was leaving hull-expelled gems at zero velocity.
            // New instance matches original pre-pool behavior (explosionSpeed / explosionRadius apply to a fresh spawned gem).
            GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody r = gemObj.GetComponent<Rigidbody>();

            NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Gem g = gemObj.GetComponent<Gem>();
                if (g != null) g.InitializeFromShip(gemValue, sizeMultiplier, expelledByShipId);
                ApplyGemLaunchVelocityAfterSpawn(r, vel, angVel);
            }
        }

        private void SpawnGem(GameObject prefab, Vector3 asteroidCenter, float gemValue, float sizeMultiplier, float asteroidPhysicalSize, ulong primaryDamagerShipId, bool isBonusGem)
        {
            Vector2 dir2 = Random.insideUnitCircle.normalized;
            if (dir2.sqrMagnitude < 0.01f) dir2 = Vector2.up;
            Vector3 dir = new Vector3(dir2.x, 0f, dir2.y);
            Vector3 pos = asteroidCenter + dir * asteroidExplosionRadius * Random.Range(0.3f, 1f);
            // Always push outward (never zero); keep variation so burst feels lively.
            Vector3 vel = dir * asteroidExplosionSpeed * Random.Range(0.45f, 1f);
            Vector3 angVel = new Vector3(Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f));

            // Use pool only when explicitly enabled and available; otherwise use original spawn path so magnetism/collection work.
            Gem gem = null;
            if (useGemPool && GemPool.Instance != null)
                gem = GemPool.Instance.GetNext();
            if (gem != null)
            {
                gem.Initialize(gemValue, sizeMultiplier, asteroidPhysicalSize, primaryDamagerShipId, isBonusGem);
                gem.ServerActivateFromPool();
                gem.ServerFinishPooledSpawn(pos, vel, angVel);
                return;
            }

            GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody r = gemObj.GetComponent<Rigidbody>();

            NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Gem g = gemObj.GetComponent<Gem>();
                if (g != null) g.Initialize(gemValue, sizeMultiplier, asteroidPhysicalSize, primaryDamagerShipId, isBonusGem);
                ApplyGemLaunchVelocityAfterSpawn(r, vel, angVel);
            }
        }
    }
}
