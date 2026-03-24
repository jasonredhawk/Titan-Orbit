using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

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
        /// <summary>Global registry of all active asteroids to avoid repeated FindObjectsOfType scans.</summary>
        public static readonly System.Collections.Generic.List<Asteroid> AllAsteroids = new System.Collections.Generic.List<Asteroid>();
        [Header("Asteroid Settings")]
        [SerializeField] private float baseGemCount = 100f;
        [SerializeField] private float baseHealth = 50f;
        [SerializeField] private float respawnTime = 30f;
        [SerializeField] private float healthScalingMultiplier = 3f; // Multiplier for HP scaling (larger asteroids get much more HP)

        private NetworkVariable<float> remainingGems = new NetworkVariable<float>(0f);
        private NetworkVariable<float> maxGems = new NetworkVariable<float>(100f);
        private NetworkVariable<float> health = new NetworkVariable<float>(50f);
        private NetworkVariable<bool> isDestroyed = new NetworkVariable<bool>(false);

        [Header("Visual")]
        [SerializeField] private Renderer asteroidRenderer;

        private Vector3 spawnPosition;
        private Vector3 spawnScale;
        private float asteroidSize = 1f;
        private Rigidbody rb;
        private Collider col;
        private Vector3 rotationAxis;
        private float rotationSpeed;

        private Color? originalColor;
        private bool hasAppliedTextureTiling;

        /// <summary>Base tiling for asteroid texture (material default). Smaller asteroids get this; larger scale up.</summary>
        private const float BASE_TEXTURE_TILING = 8f;

        private static readonly int ShaderIdTiling = Shader.PropertyToID("_Tiling");

        public float RemainingGems => remainingGems.Value;
        public float MaxGems => maxGems.Value;
        public float AsteroidSize => asteroidSize;
        public bool IsDestroyed => isDestroyed.Value;

        public bool CanBeMined() => !isDestroyed.Value && remainingGems.Value > 0;

        // Tracks how much damage each ship dealt to this asteroid (server only).
        private readonly Dictionary<ulong, float> damageByShip = new Dictionary<ulong, float>();

        [ServerRpc(RequireOwnership = false)]
        public void MineGemsServerRpc(float amount, ulong minerNetworkId)
        {
            if (isDestroyed.Value) return;
            remainingGems.Value = Mathf.Max(0, remainingGems.Value - amount);
        }

        private const float FIXED_Y_POSITION = 0f;

        /// <summary>Radius at gem value 1 (matches MapGenerator MIN_ASTEROID_RADIUS).</summary>
        private const float MIN_ASTEROID_RADIUS = 0.35f;
        /// <summary>Radius at gem value 70 = 10x smallest (matches MapGenerator MAX_ASTEROID_RADIUS).</summary>
        private const float MAX_ASTEROID_RADIUS = 0.35f * 10f;
        private const float MAX_GEM_VALUE = 70f;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            col = GetComponent<Collider>();

            if (asteroidRenderer == null)
                asteroidRenderer = GetComponentInChildren<Renderer>();
            CacheOriginalColor();
            
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
                // High-friction material so ships can ram and sustain pressure without slipping off
                if (col.sharedMaterial == null)
                {
                    col.sharedMaterial = GetOrCreateAsteroidRammingMaterial();
                }
            }
        }

        private void CacheOriginalColor()
        {
            if (asteroidRenderer == null)
                return;
            var mat = asteroidRenderer.sharedMaterial;
            if (mat == null)
                return;
            if (mat.HasProperty("_Color"))
                originalColor = mat.GetColor("_Color");
            else if (mat.HasProperty("_BaseColor"))
                originalColor = mat.GetColor("_BaseColor");
        }

        private static PhysicsMaterial asteroidRammingMaterial;
        private static PhysicsMaterial GetOrCreateAsteroidRammingMaterial()
        {
            if (asteroidRammingMaterial != null) return asteroidRammingMaterial;
            asteroidRammingMaterial = new PhysicsMaterial("AsteroidRamming")
            {
                dynamicFriction = 0.9f,
                staticFriction = 0.9f,
                frictionCombine = PhysicsMaterialCombine.Maximum,
                bounceCombine = PhysicsMaterialCombine.Minimum,
                bounciness = 0f
            };
            return asteroidRammingMaterial;
        }

        public override void OnNetworkSpawn()
        {
            if (!AllAsteroids.Contains(this))
                AllAsteroids.Add(this);
            // Lock Y position to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            
            if (IsServer)
            {
                spawnPosition = transform.position;
                spawnScale = transform.localScale;
                float rawSize = Mathf.Max(0.01f, (spawnScale.x + spawnScale.y + spawnScale.z) / 3f);
                // Gem value 1-70: map radius [MIN_ASTEROID_RADIUS, MAX_ASTEROID_RADIUS] to [1, 70]
                float radiusSpan = MAX_ASTEROID_RADIUS - MIN_ASTEROID_RADIUS;
                float normalizedSize = 1f + (MAX_GEM_VALUE - 1f) * (Mathf.Clamp(rawSize, MIN_ASTEROID_RADIUS, MAX_ASTEROID_RADIUS) - MIN_ASTEROID_RADIUS) / radiusSpan;
                asteroidSize = normalizedSize;

                // Total gem value = asteroid size (1-70)
                maxGems.Value = normalizedSize;
                remainingGems.Value = maxGems.Value;

                // HP scales with physical size (proportionate to radius / volume)
                float healthMultiplier = rawSize * (1f + rawSize * (healthScalingMultiplier - 1f));
                health.Value = baseHealth * healthMultiplier;
                isDestroyed.Value = false;
                damageByShip.Clear();
                
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

        private void Update()
        {
            ApplyTextureTilingByScale();
        }

        private void ApplyTextureTilingByScale()
        {
            if (hasAppliedTextureTiling || isDestroyed.Value) return;
            var sgt = GetComponent<SpaceGraphicsToolkit.SgtPlanet>();
            if (sgt == null || sgt.Material == null || !sgt.Material.HasProperty(ShaderIdTiling)) return;
            Vector3 scale = transform.localScale;
            float rawSize = (scale.x + scale.y + scale.z) / 3f;
            if (rawSize < 0.01f) return;
            // Scale tiling by asteroid size: small asteroids stay soft (low tiling), large get more detail (higher tiling)
            float tiling = BASE_TEXTURE_TILING * (rawSize / MIN_ASTEROID_RADIUS);
            sgt.Properties.SetFloat(ShaderIdTiling, tiling);
            hasAppliedTextureTiling = true;
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
            
            // Position is set by ToroidalRenderer in LateUpdate (display copy closest to camera).
            // Do not wrap here or entities will disappear at edges.

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

        private void OnDestroy()
        {
            AllAsteroids.Remove(this);
        }

        /// <summary>
        /// Client-side visual highlight for asteroids under a team's triangle territory.
        /// Does not affect gameplay, only tint. Pass Team.None to clear highlight.
        /// Asteroids use SgtPlanet (Graphics.DrawMesh + MaterialPropertyBlock); we set _Color on its Properties.
        /// </summary>
        public void SetTerritoryHighlight(TeamManager.Team team)
        {
            var sgt = GetComponent<SpaceGraphicsToolkit.SgtPlanet>();
            if (sgt != null)
            {
                if (originalColor == null && sgt.Material != null)
                {
                    if (sgt.Material.HasProperty("_Color"))
                        originalColor = sgt.Material.GetColor("_Color");
                    else
                        originalColor = new Color(0.5f, 0.5f, 0.5f);
                }
                if (originalColor == null) return;

                Color color = team == TeamManager.Team.None
                    ? originalColor.Value
                    : Color.Lerp(originalColor.Value, TeamManager.GetTeamColor(team), 0.7f);
                int id = Shader.PropertyToID("_Color");
                sgt.Properties.SetColor(id, color);
                return;
            }

            if (asteroidRenderer == null)
                asteroidRenderer = GetComponentInChildren<Renderer>();
            if (asteroidRenderer == null)
                return;

            if (originalColor == null)
                CacheOriginalColor();
            if (originalColor == null)
                return;

            Material mat = asteroidRenderer.material;
            Color baseCol = originalColor.Value;

            if (team == TeamManager.Team.None)
            {
                if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", baseCol);
                else if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", baseCol);
                return;
            }

            Color teamCol = TeamManager.GetTeamColor(team);
            Color tinted = Color.Lerp(baseCol, teamCol, 0.7f);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", tinted);
            else if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", tinted);
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
        public void TakeDamageServerRpc(float damage, ulong attackerShipNetworkId = 0)
        {
            if (isDestroyed.Value) return;

            if (damage > 0f && attackerShipNetworkId != 0)
            {
                if (damageByShip.TryGetValue(attackerShipNetworkId, out float existing))
                    damageByShip[attackerShipNetworkId] = existing + damage;
                else
                    damageByShip[attackerShipNetworkId] = damage;
            }

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
            float physicalSize = (scale.x + scale.y + scale.z) / 3f;

            // Determine which ship dealt the most damage to this asteroid.
            ulong topDamagerShipId = 0;
            if (damageByShip.Count > 0)
            {
                float maxDamage = 0f;
                foreach (var kvp in damageByShip)
                {
                    if (kvp.Value > maxDamage)
                    {
                        maxDamage = kvp.Value;
                        topDamagerShipId = kvp.Key;
                    }
                }
            }
            damageByShip.Clear();

            // Spawn gems (100x value in debug mode for faster testing)
            if (GemSpawner.Instance != null)
            {
                float gemValue = remainingGems.Value;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                    gemValue *= 100f;

                // Bonus only for same team as triangle: 5% per home planet level. Enemies get no bonus.
                float bonusMultiplier = 1f;
                var conn = Systems.PlanetConnectionSystem.Instance;
                if (conn != null)
                {
                    TeamManager.Team asteroidTeam = conn.GetTeamAtPosition(pos);
                    if (asteroidTeam != TeamManager.Team.None && topDamagerShipId != 0)
                    {
                        var nm = Unity.Netcode.NetworkManager.Singleton;
                        if (nm != null && nm.SpawnManager != null && nm.SpawnManager.SpawnedObjects.TryGetValue(topDamagerShipId, out Unity.Netcode.NetworkObject netObj))
                        {
                            var ship = netObj != null ? netObj.GetComponent<Starship>() : null;
                            if (ship != null && ship.ShipTeam == asteroidTeam)
                                bonusMultiplier = 1f + 0.05f * Systems.PlanetConnectionSystem.GetHomePlanetLevelForTeam(asteroidTeam);
                        }
                    }
                }

                gemValue *= Mathf.Max(1f, bonusMultiplier);
                GemSpawner.Instance.SpawnGemsServerRpc(pos, gemValue, asteroidSize, physicalSize, topDamagerShipId);
            }

            // Schedule respawn and despawn - fresh instance avoids state corruption (same delay as release; debug does not shorten it).
            if (AsteroidRespawnManager.Instance != null)
                AsteroidRespawnManager.Instance.ScheduleRespawn(pos, scale, respawnTime);

            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }
    }
}
