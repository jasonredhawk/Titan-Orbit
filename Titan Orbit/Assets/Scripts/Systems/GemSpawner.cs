using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
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

        /// <summary>GemPool should use the same resolved prefab as spawning (registered with <see cref="NetworkManager"/>).</summary>
        internal GameObject GetRuntimeGemPrefabForPool() => GetGemPrefab();

        private GameObject GetGemPrefab()
        {
            GameObject raw = gemPrefab;
#if UNITY_EDITOR
            if (raw == null)
                raw = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Gem.prefab");
#endif
            if (raw == null)
                raw = Resources.Load<GameObject>("Gem");
            if (raw == null)
                raw = Resources.Load<GameObject>("Prefabs/Gem");

            GameObject resolved = ResolveRegisteredGemPrefab(raw);
            if (gemPrefab == null && resolved != null)
                gemPrefab = resolved;
            return resolved;
        }

        /// <summary>
        /// Resources or copied prefabs can differ from the asset registered in NetworkPrefabs; clients then cannot spawn the replicated hash.
        /// Map any Gem prefab instance to the first registered prefab that has a <see cref="Gem"/> component.
        /// </summary>
        private static GameObject ResolveRegisteredGemPrefab(GameObject candidate)
        {
            if (candidate == null) return null;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.NetworkConfig?.Prefabs == null)
                return candidate;
            if (nm.NetworkConfig.Prefabs.Contains(candidate))
                return candidate;

            IReadOnlyList<NetworkPrefab> list = nm.NetworkConfig.Prefabs.Prefabs;
            for (int i = 0; i < list.Count; i++)
            {
                GameObject reg = list[i].Prefab;
                if (reg == null || reg.GetComponent<Gem>() == null) continue;
                return reg;
            }

            return candidate;
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
            GameObject raw = peopleTransportPrefab;
#if UNITY_EDITOR
            if (raw == null)
                raw = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PeopleTransport.prefab");
#endif
            if (raw == null)
                raw = Resources.Load<GameObject>("PeopleTransport");
            if (raw == null)
                raw = Resources.Load<GameObject>("Prefabs/PeopleTransport");

            GameObject resolved = ResolveRegisteredPeopleTransportPrefab(raw);
            if (peopleTransportPrefab == null && resolved != null)
                peopleTransportPrefab = resolved;
            return resolved;
        }

        private static GameObject ResolveRegisteredPeopleTransportPrefab(GameObject candidate)
        {
            if (candidate == null) return null;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.NetworkConfig?.Prefabs == null)
                return candidate;
            if (nm.NetworkConfig.Prefabs.Contains(candidate))
                return candidate;

            IReadOnlyList<NetworkPrefab> list = nm.NetworkConfig.Prefabs.Prefabs;
            for (int i = 0; i < list.Count; i++)
            {
                GameObject reg = list[i].Prefab;
                if (reg == null || reg.GetComponent<PeopleTransportProjectile>() == null) continue;
                return reg;
            }

            return candidate;
        }

        /// <summary>Spawns people beaming from planet surface to ship (load).</summary>
        public void SpawnPeopleLoad(Vector3 planetPosition, Vector3 shipPosition, float amount, ulong shipNetworkObjectId, ulong sourcePlanetNetworkObjectId, TitanOrbit.Core.TeamManager.Team team)
        {
            Vector3 spawnPos = ResolvePlanetSurfaceSpawn(sourcePlanetNetworkObjectId, planetPosition, shipPosition);
            SpawnPeopleTransport(spawnPos, shipPosition, amount, shipNetworkObjectId, true, team, 0, sourcePlanetNetworkObjectId);
        }

        /// <summary>Spawns people beaming from ship to planet surface (unload).</summary>
        public void SpawnPeopleUnload(Vector3 shipPosition, Vector3 planetPosition, float amount, ulong planetNetworkObjectId, TitanOrbit.Core.TeamManager.Team team, ulong shipNetworkObjectId)
        {
            Vector3 planetSurface = ResolvePlanetSurfaceSpawn(planetNetworkObjectId, planetPosition, shipPosition);
            Vector3 spawnPos = shipPosition;
            if (PeopleTransportProjectile.TryResolveShip(shipNetworkObjectId, out Starship ship))
                spawnPos = PeopleTransportProjectile.GetShipUnloadSpawnPointToward(ship, planetSurface);
            SpawnPeopleTransport(spawnPos, planetSurface, amount, planetNetworkObjectId, false, team, shipNetworkObjectId, 0);
        }

        private static Vector3 ResolvePlanetSurfaceSpawn(ulong planetNetworkObjectId, Vector3 planetCenterFallback, Vector3 towardWorldPos)
        {
            if (PeopleTransportProjectile.TryResolvePlanet(planetNetworkObjectId, out Planet planet))
                return PeopleTransportProjectile.GetSurfaceSpawnPointToward(planet, towardWorldPos);
            return planetCenterFallback;
        }

        private void SpawnPeopleTransport(Vector3 fromPos, Vector3 toPos, float amount, ulong targetNetworkObjectId, bool isLoad, TitanOrbit.Core.TeamManager.Team team, ulong shipNetworkObjectId, ulong sourcePlanetNetworkObjectId)
        {
            if (!IsServer) return;
            GameObject prefab = GetPeopleTransportPrefab();
            if (prefab == null || amount <= 0f)
                return;

            Vector3 dir = ToroidalMap.ToroidalDirection(fromPos, toPos);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            else dir.Normalize();
            // Nudge spawns slightly off the hull so the sphere is visible immediately.
            Vector3 pos = fromPos;
            if (isLoad)
                pos += dir * Mathf.Max(0.2f, PeopleTransportProjectile.SurfaceSpawnOutwardNudge * 0.35f);

            float travelDist = ToroidalMap.ToroidalDistance(fromPos, toPos);
            float cruiseSpeed = Mathf.Max(0.08f, travelDist / PeopleTransportProjectile.EffectiveVisualTravelSeconds);
            if (isLoad)
                cruiseSpeed *= PeopleTransportProjectile.LoadMagnetSpeedMultiplier;
            float initialSpeed = cruiseSpeed * (isLoad ? 0.55f : 0.3f);

            GameObject obj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.position = pos;
                rb.linearVelocity = dir * initialSpeed;
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

        /// <summary>Server-only burst from asteroid death (same logic as RPC; avoids invoking a ServerRpc from server destroy path).</summary>
        public void SpawnGemsFromAsteroidDestroyOnServer(
            Vector3 asteroidCenter,
            float regularValue,
            float bonusValue,
            float asteroidSize = 1f,
            float asteroidPhysicalSize = 0.5f,
            ulong primaryDamagerShipId = 0)
        {
            if (!IsServer) return;
            SpawnGemsAsteroidBurstImpl(asteroidCenter, regularValue, bonusValue, asteroidSize, asteroidPhysicalSize, primaryDamagerShipId);
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
            SpawnGemsAsteroidBurstImpl(asteroidCenter, regularValue, bonusValue, asteroidSize, asteroidPhysicalSize, primaryDamagerShipId);
        }

        private void SpawnGemsAsteroidBurstImpl(
            Vector3 asteroidCenter,
            float regularValue,
            float bonusValue,
            float asteroidSize,
            float asteroidPhysicalSize,
            ulong primaryDamagerShipId)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null)
            {
                return;
            }
            regularValue = Mathf.Max(0f, regularValue);
            bonusValue = Mathf.Max(0f, bonusValue);
            if (regularValue <= 0f && bonusValue <= 0f)
            {
                return;
            }

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

            // Visual size is fully derived from gem value inside Gem.UpdateVisualScale; spawner only forwards value.
            for (int i = 0; i < redGemCount; i++)
                SpawnGem(prefab, asteroidCenter, gemWorth, 1f, asteroidPhysicalSize, primaryDamagerShipId, false);

            for (int i = 0; i < bonusGemCount; i++)
                SpawnGem(prefab, asteroidCenter, gemWorth, 1f, asteroidPhysicalSize, primaryDamagerShipId, true);
        }
        
        /// <summary>Launch speed and spawn offset scale with ship level so larger ships expel gems farther.</summary>
        public static float GetShipExpulsionLevelSpeedMultiplier(int shipLevel)
        {
            int level = Mathf.Max(1, shipLevel);
            return 1f + (level - 1) * 0.12f;
        }

        /// <summary>Server-only gem expulsion (hull breakup, ramming self-damage, etc.). Avoids nested ServerRpc when invoked from server damage/collision paths.</summary>
        /// <param name="expulsionIntensity">Scales launch speed (0 = softer eject, 1 = harder impact).</param>
        public void SpawnGemsFromShipOnServer(Vector3 shipPosition, float totalValue, ulong expelledByShipId, float expulsionIntensity = 0.5f, int shipLevel = 1)
        {
            if (!IsServer) return;
            SpawnGemsFromShipImpl(shipPosition, totalValue, expelledByShipId, expulsionIntensity, shipLevel);
        }

        /// <summary>Spawns gems expelled from a ship when bullets hit after health is zero. Victim ship cannot re-collect for a short cooldown.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void SpawnGemsFromShipServerRpc(Vector3 shipPosition, float totalValue, ulong expelledByShipId, float expulsionIntensity = 0.5f, int shipLevel = 1)
        {
            SpawnGemsFromShipImpl(shipPosition, totalValue, expelledByShipId, expulsionIntensity, shipLevel);
        }

        private void SpawnGemsFromShipImpl(Vector3 shipPosition, float totalValue, ulong expelledByShipId, float expulsionIntensity, int shipLevel)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null || totalValue <= 0f) return;

            float intensity = Mathf.Clamp01(expulsionIntensity);
            float levelSpeedMul = GetShipExpulsionLevelSpeedMultiplier(shipLevel);
            float launchSpeedMul = Mathf.Lerp(0.7f, 1.25f, intensity) * levelSpeedMul;

            List<float> chunks = SplitExpulsionValueIntoRandomGemChunks(totalValue);
            for (int i = 0; i < chunks.Count; i++)
            {
                float gemValue = chunks[i];
                if (gemValue <= 0.001f) continue;
                SpawnGemFromShip(prefab, shipPosition, gemValue, 1f, expelledByShipId, launchSpeedMul, levelSpeedMul);
            }
        }

        /// <summary>Randomly splits expelled value into 1–3 positive chunks that sum to <paramref name="totalValue"/> exactly.</summary>
        private static List<float> SplitExpulsionValueIntoRandomGemChunks(float totalValue)
        {
            var chunks = new List<float>(3);
            if (totalValue <= 0.001f) return chunks;

            int gemCount = Random.Range(1, 4);
            if (gemCount <= 1)
            {
                chunks.Add(totalValue);
                return chunks;
            }

            float[] cutPoints = new float[gemCount - 1];
            for (int i = 0; i < cutPoints.Length; i++)
                cutPoints[i] = Random.Range(0f, totalValue);
            System.Array.Sort(cutPoints);

            float previous = 0f;
            for (int i = 0; i < cutPoints.Length; i++)
            {
                chunks.Add(cutPoints[i] - previous);
                previous = cutPoints[i];
            }
            chunks.Add(totalValue - previous);
            return chunks;
        }

        /// <summary>Server: player voluntarily expels gems forward from the ship (V key, 2 shots/sec). Each shot splits into 1–3 gems totaling the shot value.</summary>
        public void SpawnVoluntaryGemFromShipOnServer(
            Vector3 shipPosition,
            Vector3 forwardDir,
            float gemValue,
            int shipLevel,
            ulong expelledByShipId,
            Vector3 shipVelocity,
            int shotIndex)
        {
            if (!IsServer || gemValue <= 0.001f) return;

            GameObject prefab = GetGemPrefab();
            if (prefab == null) return;

            forwardDir.y = 0f;
            if (forwardDir.sqrMagnitude < 0.01f)
                forwardDir = Vector3.forward;
            else
                forwardDir.Normalize();

            Vector3 lateral = Vector3.Cross(Vector3.up, forwardDir);
            float levelSpeedMul = GetShipExpulsionLevelSpeedMultiplier(shipLevel);
            float launchSpeed = explosionSpeed * 2f * levelSpeedMul;

            List<float> chunks = SplitExpulsionValueIntoRandomGemChunks(gemValue);
            for (int i = 0; i < chunks.Count; i++)
            {
                float chunkValue = chunks[i];
                if (chunkValue <= 0.001f) continue;
                float lateralOffset = chunks.Count > 1
                    ? Mathf.Lerp(-0.35f, 0.35f, i / (float)(chunks.Count - 1))
                    : 0f;
                SpawnGemFromShipDirectional(
                    prefab,
                    shipPosition,
                    forwardDir,
                    lateral,
                    lateralOffset,
                    chunkValue,
                    expelledByShipId,
                    launchSpeed,
                    shipVelocity,
                    shotIndex + i,
                    levelSpeedMul);
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
            // Visual size is derived from `amount` inside Gem.UpdateVisualScale; spawner forwards a neutral multiplier.
            float sizeMult = 1f;
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

        private void SpawnGemFromShip(GameObject prefab, Vector3 shipCenter, float gemValue, float sizeMultiplier, ulong expelledByShipId, float launchSpeedMultiplier = 1f, float spawnRadiusMultiplier = 1f)
        {
            Vector2 dir2 = Random.insideUnitCircle.normalized;
            if (dir2.sqrMagnitude < 0.01f) dir2 = Vector2.up;
            Vector3 dir = new Vector3(dir2.x, 0f, dir2.y);
            float radiusMul = Mathf.Max(0.1f, spawnRadiusMultiplier);
            Vector3 pos = shipCenter + dir * explosionRadius * radiusMul * Random.Range(0.3f, 1f);
            Vector3 vel = dir * explosionSpeed * Mathf.Max(0.1f, launchSpeedMultiplier) * Random.Range(0.8f, 1.2f);
            Vector3 angVel = new Vector3(Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f));
            SpawnGemFromShipObject(prefab, pos, vel, angVel, gemValue, sizeMultiplier, expelledByShipId);
        }

        private void SpawnGemFromShipDirectional(
            GameObject prefab,
            Vector3 shipCenter,
            Vector3 forwardDir,
            Vector3 lateralDir,
            float lateralOffset,
            float gemValue,
            ulong expelledByShipId,
            float launchSpeed,
            Vector3 shipVelocity,
            int indexInVolley,
            float spawnRadiusMultiplier = 1f)
        {
            float radiusMul = Mathf.Max(0.1f, spawnRadiusMultiplier);
            float forwardOffset = explosionRadius * radiusMul * (0.55f + indexInVolley * 0.12f);
            Vector3 pos = shipCenter + forwardDir * forwardOffset + lateralDir * lateralOffset;
            pos.y = 0f;
            Vector3 vel = forwardDir * launchSpeed * Random.Range(0.95f, 1.05f) + shipVelocity;
            vel.y = 0f;
            Vector3 angVel = new Vector3(Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f), Random.Range(-1.5f, 1.5f));
            SpawnGemFromShipObject(prefab, pos, vel, angVel, gemValue, 1f, expelledByShipId);
        }

        private void SpawnGemFromShipObject(
            GameObject prefab,
            Vector3 pos,
            Vector3 vel,
            Vector3 angVel,
            float gemValue,
            float sizeMultiplier,
            ulong expelledByShipId)
        {
            // Do not use GemPool here: recycling + NetworkTransform/physics ordering was leaving hull-expelled gems at zero velocity.
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
