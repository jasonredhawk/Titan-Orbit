using System.Collections.Generic;
using System.Collections;
using System.Globalization;
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
        [SerializeField] private float respawnTime = 30f;

        private NetworkVariable<float> remainingGems = new NetworkVariable<float>(0f);
        private NetworkVariable<float> maxGems = new NetworkVariable<float>(100f);
        private NetworkVariable<float> health = new NetworkVariable<float>(50f);
        private NetworkVariable<bool> isDestroyed = new NetworkVariable<bool>(false);

        [Header("Visual")]
        [SerializeField] private Renderer asteroidRenderer;
        [SerializeField] private float respawnScaleAnimDuration = 0.45f;
        [SerializeField, Range(0.01f, 1f)] private float respawnScaleAnimStartMultiplier = 0.08f;

        private Vector3 spawnPosition;
        private Vector3 spawnScale;
        private float asteroidSize = 1f;
        private Rigidbody rb;
        private Collider col;
        private Vector3 rotationAxis;
        private float rotationSpeed;
        private Coroutine respawnScaleAnimRoutine;

        private Color? originalColor;
        private bool hasAppliedSurfaceVariation;

        /// <summary>Base tiling for asteroid texture (material default). Smaller asteroids get this; larger scale up.</summary>
        private const float BASE_TEXTURE_TILING = 8f;

        /// <summary>Extra UV scale randomness on top of size-based tiling (same albedo/height textures, very different apparent grain).</summary>
        private const float TEXTURE_SCALE_RANDOM_MIN = 0.12f;
        private const float TEXTURE_SCALE_RANDOM_MAX = 7f;

        /// <summary>Normal map strength (Planet shader Range 0–5).</summary>
        private const float BUMP_SCALE_MIN = 0.15f;
        private const float BUMP_SCALE_MAX = 5f;

        /// <summary>Heightmap vertex displacement (SgtPlanet); geometric lumpiness independent of normals.</summary>
        private const float DISPLACEMENT_MIN = 0.025f;
        private const float DISPLACEMENT_MAX = 0.32f;

        /// <summary>Optional detail-layer tiling spread when material exposes _DetailTiling (Barren5).</summary>
        private const float DETAIL_TILING_MIN = 12f;
        private const float DETAIL_TILING_MAX = 140f;

        private static readonly int ShaderIdTiling = Shader.PropertyToID("_Tiling");
        private static readonly int ShaderIdBumpScale = Shader.PropertyToID("_BumpScale");
        private static readonly int ShaderIdDetailTiling = Shader.PropertyToID("_DetailTiling");

        public float RemainingGems => remainingGems.Value;
        public float MaxGems => maxGems.Value;
        public float RemainingHealth => health.Value;
        public float AsteroidSize => asteroidSize;
        public bool IsDestroyed => isDestroyed.Value;
        public Vector3 WorldVelocity => rb != null ? rb.linearVelocity : Vector3.zero;

        public bool CanBeMined() => !isDestroyed.Value && remainingGems.Value > 0;

        public float GetCollisionRadiusWorld()
        {
            if (col != null)
            {
                Vector3 e = col.bounds.extents;
                return Mathf.Max(0.01f, Mathf.Max(e.x, Mathf.Max(e.y, e.z)));
            }

            Vector3 s = transform.lossyScale;
            float avg = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            return Mathf.Max(0.01f, avg * 0.5f);
        }

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

                // Keep asteroid HP at a fixed 3:1 ratio with gem value (e.g. 50 gems => 150 HP).
                health.Value = maxGems.Value * 3f;
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
            ApplyAsteroidSurfaceVariation();
        }

        /// <summary>
        /// Same Barren/planet textures per asteroid, but strong per-instance variation: UV scale, normal strength,
        /// displacement, and optional detail tiling. Seeded from world position so all clients match.
        /// </summary>
        private void ApplyAsteroidSurfaceVariation()
        {
            if (hasAppliedSurfaceVariation || isDestroyed.Value) return;
            var sgt = GetComponent<SpaceGraphicsToolkit.SgtPlanet>();
            if (sgt == null || sgt.Material == null || !sgt.Material.HasProperty(ShaderIdTiling)) return;
            Vector3 scale = transform.localScale;
            float rawSize = (scale.x + scale.y + scale.z) / 3f;
            if (rawSize < 0.01f) return;

            Vector3 p = transform.position;
            int seed = unchecked((int)((long)(p.x * 1000) * 73856093 ^ (long)(p.z * 1000) * 19349663 ^ (long)(p.y * 100) * 83492791));
            var rng = new System.Random(seed);

            float sizeTiling = BASE_TEXTURE_TILING * (rawSize / MIN_ASTEROID_RADIUS);
            float scaleMul = Mathf.Lerp(TEXTURE_SCALE_RANDOM_MIN, TEXTURE_SCALE_RANDOM_MAX, (float)rng.NextDouble());
            sgt.Properties.SetFloat(ShaderIdTiling, sizeTiling * scaleMul);

            if (sgt.Material.HasProperty(ShaderIdBumpScale))
            {
                float bump = Mathf.Lerp(BUMP_SCALE_MIN, BUMP_SCALE_MAX, (float)rng.NextDouble());
                sgt.Properties.SetFloat(ShaderIdBumpScale, bump);
            }

            if (sgt.Material.HasProperty(ShaderIdDetailTiling))
            {
                float detailTiling = Mathf.Lerp(DETAIL_TILING_MIN, DETAIL_TILING_MAX, (float)rng.NextDouble());
                sgt.Properties.SetFloat(ShaderIdDetailTiling, detailTiling);
            }

            float displacement = Mathf.Lerp(DISPLACEMENT_MIN, DISPLACEMENT_MAX, (float)rng.NextDouble());
            sgt.Displacement = displacement;
            sgt.DirtyMesh();

            hasAppliedSurfaceVariation = true;
            SyncSphereColliderToDisplacedPlanet();
            StartCoroutine(CoSyncColliderAfterMesh());
        }

        /// <summary>Mesh bounds update one frame after <see cref="SpaceGraphicsToolkit.SgtPlanet.DirtyMesh"/> so hit volume matches displaced visuals.</summary>
        private IEnumerator CoSyncColliderAfterMesh()
        {
            yield return null;
            SyncSphereColliderToDisplacedPlanet();
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

        public void TriggerRespawnScaleAnimation()
        {
            if (!IsServer || !IsSpawned) return;
            PlayRespawnScaleAnimationClientRpc(transform.localScale);
        }

        [ClientRpc]
        private void PlayRespawnScaleAnimationClientRpc(Vector3 targetScale)
        {
            StartRespawnScaleAnimation(targetScale);
        }

        private void StartRespawnScaleAnimation(Vector3 targetScale)
        {
            if (respawnScaleAnimRoutine != null)
                StopCoroutine(respawnScaleAnimRoutine);

            if (respawnScaleAnimDuration <= 0f || respawnScaleAnimStartMultiplier >= 1f)
            {
                transform.localScale = targetScale;
                return;
            }

            respawnScaleAnimRoutine = StartCoroutine(AnimateRespawnScale(targetScale));
        }

        private IEnumerator AnimateRespawnScale(Vector3 targetScale)
        {
            Vector3 startScale = targetScale * Mathf.Max(0.001f, respawnScaleAnimStartMultiplier);
            transform.localScale = startScale;

            float elapsed = 0f;
            while (elapsed < respawnScaleAnimDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / respawnScaleAnimDuration);
                // Smooth out the first/last few frames so scale-in feels less mechanical.
                t = t * t * (3f - 2f * t);
                transform.localScale = Vector3.LerpUnclamped(startScale, targetScale, t);
                yield return null;
            }

            transform.localScale = targetScale;
            respawnScaleAnimRoutine = null;
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
            SyncSphereColliderToDisplacedPlanet();
        }

        /// <summary>
        /// SgtPlanet displaces vertices up to <see cref="SpaceGraphicsToolkit.SgtPlanet.Displacement"/> beyond <see cref="SpaceGraphicsToolkit.SgtPlanet.Radius"/>.
        /// The default <see cref="SphereCollider"/> radius matched only the base radius, so bullets (and traces) missed the visible rock.
        /// </summary>
        private void SyncSphereColliderToDisplacedPlanet()
        {
            if (col is not SphereCollider sphereCol) return;
            var sgt = GetComponent<SpaceGraphicsToolkit.SgtPlanet>();
            if (sgt == null) return;
            float outer = sgt.Radius;
            if (sgt.Displace)
                outer += Mathf.Max(0f, sgt.Displacement);
            outer *= 1.12f;
            sphereCol.radius = Mathf.Max(0.05f, outer);

            Renderer rend = asteroidRenderer != null ? asteroidRenderer : GetComponentInChildren<Renderer>();
            if (rend != null)
            {
                Bounds wb = rend.bounds;
                float worldR = Mathf.Max(wb.extents.x, wb.extents.z);
                float maxAxis = Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Max(Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z)));
                float localFromBounds = worldR / Mathf.Max(0.01f, maxAxis);
                sphereCol.radius = Mathf.Max(sphereCol.radius, localFromBounds * 1.06f);
            }
        }

        /// <summary>Server-only damage from bullets (same rules as <see cref="TakeDamageServerRpc"/>; avoids nested ServerRpc from another NetworkBehaviour).</summary>
        public void ApplyDamageFromBulletServer(float damage, ulong attackerShipNetworkId = 0)
        {
            if (!IsServer) return;
            ApplyIncomingDamageServer(damage, attackerShipNetworkId, "Asteroid.ApplyDamageFromBulletServer");
        }

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, ulong attackerShipNetworkId = 0)
        {
            ApplyIncomingDamageServer(damage, attackerShipNetworkId, "Asteroid.TakeDamageServerRpc");
        }

        private void ApplyIncomingDamageServer(float damage, ulong attackerShipNetworkId, string logLocation)
        {
            if (isDestroyed.Value) return;
            // #region agent log 065367
            if (IsServer)
            {
                var no = GetComponent<NetworkObject>();
                ulong nid = no != null ? no.NetworkObjectId : 0UL;
                DebugNdjson065367.Write("AST", logLocation, "entry",
                    "{\"netId\":" + nid + ",\"dmg\":" + damage.ToString("0.###", CultureInfo.InvariantCulture) + ",\"hpBefore\":" + health.Value.ToString("0.###", CultureInfo.InvariantCulture) + "}");
            }
            // #endregion agent log 065367

            if (damage > 0f && attackerShipNetworkId != 0)
            {
                if (damageByShip.TryGetValue(attackerShipNetworkId, out float existing))
                    damageByShip[attackerShipNetworkId] = existing + damage;
                else
                    damageByShip[attackerShipNetworkId] = damage;
            }

            health.Value = Mathf.Max(0, health.Value - damage);
            if (health.Value <= 0)
                ApplyAsteroidDestroyedServer();
        }

        /// <summary>Server-only destroy path (must not be a nested ServerRpc call).</summary>
        private void ApplyAsteroidDestroyedServer()
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
                float regularValue = remainingGems.Value;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                    regularValue *= 100f;

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

                bonusMultiplier = Mathf.Max(1f, bonusMultiplier);
                float bonusValue = regularValue * Mathf.Max(0f, bonusMultiplier - 1f);
                // #region agent log 065367
                if (IsServer)
                {
                    var asteroidNo = GetComponent<NetworkObject>();
                    ulong nid = asteroidNo != null ? asteroidNo.NetworkObjectId : 0UL;
                    DebugNdjson065367.Write("AST-destroy", "Asteroid.ApplyAsteroidDestroyedServer", "before_spawn_gems",
                        "{\"netId\":" + nid
                        + ",\"remGems\":" + remainingGems.Value.ToString("0.###", CultureInfo.InvariantCulture)
                        + ",\"regular\":" + regularValue.ToString("0.###", CultureInfo.InvariantCulture)
                        + ",\"bonus\":" + bonusValue.ToString("0.###", CultureInfo.InvariantCulture)
                        + ",\"topDamager\":" + topDamagerShipId + "}");
                }
                // #endregion agent log 065367
                GemSpawner.Instance.SpawnGemsFromAsteroidDestroyOnServer(pos, regularValue, bonusValue, asteroidSize, physicalSize, topDamagerShipId);
            }
            else
            {
                // #region agent log 065367
                if (IsServer)
                    DebugNdjson065367.Write("AST-destroy", "Asteroid.ApplyAsteroidDestroyedServer", "gemspawner_null", "{}");
                // #endregion agent log 065367
                Debug.LogWarning("[Asteroid] GemSpawner.Instance is null — no gems spawned. Ensure a GemSpawner is in the gameplay scene with gem prefab assigned, or ship Gem under Assets/Resources/Gem.prefab for headless builds.");
            }

            // Schedule respawn and despawn - fresh instance avoids state corruption (same delay as release; debug does not shorten it).
            if (AsteroidRespawnManager.Instance != null)
                AsteroidRespawnManager.Instance.ScheduleRespawn(pos, scale, respawnTime);

            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }
    }
}
