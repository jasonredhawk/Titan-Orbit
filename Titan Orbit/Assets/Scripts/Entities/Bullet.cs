using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Bullet - hits asteroids and ships, despawns on hit or max distance/lifetime.
    /// Uses path raycast to prevent tunneling when close.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Bullet : NetworkBehaviour
    {
        [Header("Bullet Settings")]
        [SerializeField] private float speed = 20f;
        [SerializeField] private float damage = 10f;
        [SerializeField] private float lifetime = 2f; // Reduced from 10f for better performance
        [SerializeField] private float maxDistance = 30f; // ~3x previous range for space combat
        [SerializeField] private float minTravelBeforeHit = 0.5f;
        [SerializeField] private TeamManager.Team ownerTeam = TeamManager.Team.None;

        [Header("Particle Visual (AllIn1 VFX)")]
        [Tooltip("Optional: particle/projectile effect from AllIn1 VFX Toolkit. If set, replaces the default sphere visual.")]
        [SerializeField] private GameObject bulletVisualPrefab;
        [Tooltip("Per-ship styles: [0]=Digital, [1]=Ice/long trail, [2]=Fire, [3]=Plasma. Leave empty to use bulletVisualPrefab only.")]
        [SerializeField] private GameObject[] bulletVisualPrefabOptions;
        [Tooltip("Base scale of the instantiated particle visual. Final scale = this × visualScaleMultiplier (from ship power).")]
        [SerializeField] private float bulletVisualScale = 0.35f;
        [Tooltip("Optional: explosion/impact effect played when bullet hits. Spawned on all clients.")]
        [SerializeField] private GameObject impactEffectPrefab;
        [SerializeField] private float impactEffectDuration = 3f;
        [SerializeField] private float impactEffectScale = 0.5f;

        private NetworkVariable<float> bulletVisualScaleMultiplier = new NetworkVariable<float>(1f);
        private NetworkVariable<byte> bulletVisualStyleIndex = new NetworkVariable<byte>(0);
        
        // Local cache set immediately (before NetworkVariable syncs) so visual can be created right away
        private byte cachedVisualStyleIndex = 0;
        private float cachedVisualScaleMultiplier = 1f;

        private const float FIXED_Y_POSITION = 0f;
        private Rigidbody rb;
        private float spawnTime;
        private Vector3 spawnPosition;
        private Vector3 lastPosition;
        private GameObject spawnedVisual;

        public float Damage => damage;
        public TeamManager.Team OwnerTeam => ownerTeam;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                // Lock Y position - bullets stay on same plane
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }
            
            // Subscribe to NetworkVariable changes to update visual when style index syncs
            bulletVisualStyleIndex.OnValueChanged += OnVisualStyleIndexChanged;
            bulletVisualScaleMultiplier.OnValueChanged += OnVisualScaleChanged;
        }

        private void OnDestroy()
        {
            bulletVisualStyleIndex.OnValueChanged -= OnVisualStyleIndexChanged;
            bulletVisualScaleMultiplier.OnValueChanged -= OnVisualScaleChanged;
        }

        private void OnVisualStyleIndexChanged(byte oldValue, byte newValue)
        {
            UpdateVisual();
        }

        private void OnVisualScaleChanged(float oldValue, float newValue)
        {
            if (spawnedVisual != null)
            {
                float scale = bulletVisualScale * bulletVisualScaleMultiplier.Value;
                spawnedVisual.transform.localScale = Vector3.one * scale;
            }
        }

        public override void OnNetworkSpawn()
        {
            // Set NetworkVariables after spawn so we don't trigger "written before NetworkObject is spawned" (Initialize runs before Spawn in CombatSystem).
            if (IsServer)
            {
                bulletVisualScaleMultiplier.Value = cachedVisualScaleMultiplier;
                bulletVisualStyleIndex.Value = cachedVisualStyleIndex;
            }

            // Lock Y position to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            
            spawnTime = Time.time;
            spawnPosition = transform.position;
            lastPosition = spawnPosition;

            // Update visual immediately (uses cached values; NetworkVariable sync will update clients)
            UpdateVisual();
            
            // Also schedule a delayed update in case NetworkVariable sync is delayed
            StartCoroutine(DelayedVisualUpdate());
            
            // Check for immediate overlaps when spawning (fixes close-range tunneling)
            if (IsServer)
            {
                CheckImmediateOverlaps();
            }
        }

        private System.Collections.IEnumerator DelayedVisualUpdate()
        {
            yield return null; // Wait one frame for NetworkVariable to sync
            UpdateVisual();
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
            
            // Ensure rigidbody velocity has no Y component
            if (rb != null && Mathf.Abs(rb.linearVelocity.y) > 0.01f)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel;
            }
            
            if (!IsServer) return;

            float dist = Vector3.Distance(transform.position, spawnPosition);
            if (dist > maxDistance || Time.time - spawnTime > lifetime)
            {
                DespawnBullet();
                return;
            }

            // Always check for collisions, even if close (fixes tunneling bug)
            Vector3 to = transform.position;
            float pathLen = Vector3.Distance(lastPosition, to);
            
            if (pathLen > 0.001f)
            {
                // Use SphereCast instead of Raycast for better detection of fast-moving bullets
                float bulletRadius = 0.3f; // Larger radius to reliably hit ships (BoxCollider ~0.5 wide)
                if (Physics.SphereCast(lastPosition, bulletRadius, (to - lastPosition).normalized, out RaycastHit hit, pathLen, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform != transform && !hit.collider.transform.IsChildOf(transform))
                    {
                        if (TryHit(hit.collider))
                            return; // Hit valid target, despawned
                        DespawnBullet(); // Hit something else (planet, etc) - despawn to avoid getting stuck
                        return;
                    }
                }
            }

            lastPosition = transform.position;
        }

        private void CheckImmediateOverlaps()
        {
            // Check if bullet spawned inside or very close to an asteroid/ship
            float checkRadius = 0.5f;
            Collider[] overlaps = Physics.OverlapSphere(transform.position, checkRadius, ~0, QueryTriggerInteraction.Ignore);
            
            foreach (Collider col in overlaps)
            {
                if (col.transform != transform && !col.transform.IsChildOf(transform))
                {
                    // Only hit if it's an asteroid or enemy ship (not the shooter)
                    Asteroid asteroid = col.GetComponentInParent<Asteroid>();
                    if (asteroid != null && !asteroid.IsDestroyed)
                    {
                        TryHit(col);
                        return;
                    }
                    
                    Starship ship = col.GetComponentInParent<Starship>();
                    if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
                    {
                        TryHit(col);
                        return;
                    }
                    DroneBase drone = col.GetComponentInParent<DroneBase>();
                    if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
                    {
                        TryHit(col);
                        return;
                    }
                }
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
            if (collision != null && collision.collider != null)
                TryHit(collision.collider);
        }

        /// <returns>True if we hit a valid target (asteroid, ship, drone) and despawned.</returns>
        private bool TryHit(Collider other)
        {
            if (other == null) return false;

            // Use GetComponentInParent to handle child colliders (e.g. ship sub-meshes)
            Asteroid asteroid = other.GetComponentInParent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                float appliedDamage = damage;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                    appliedDamage = 999999f; // One-shot asteroids in debug mode
                asteroid.TakeDamageServerRpc(appliedDamage);
                DespawnBullet();
                return true;
            }

            Starship ship = other.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                ship.TakeDamageServerRpc(damage, ownerTeam);
                DespawnBullet();
                return true;
            }

            DroneBase drone = other.GetComponentInParent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                drone.TakeDamageServerRpc(damage, ownerTeam);
                DespawnBullet();
                return true;
            }

            return false;
        }

        private void DespawnBullet()
        {
            Vector3 impactPos = transform.position;
            if (impactEffectPrefab != null)
            {
                SpawnImpactEffectClientRpc(impactPos);
                SpawnImpactAt(impactPos); // Server spawns too (ClientRpc doesn't run on server)
            }
            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }

        private void SpawnImpactAt(Vector3 position)
        {
            GameObject go = Instantiate(impactEffectPrefab, position, Quaternion.identity);
            go.transform.localScale = Vector3.one * impactEffectScale;
            DisableGrabPassMaterials(go); // Avoid "GrabPass can't be called from job thread" in URP/SRP
            Destroy(go, impactEffectDuration);
        }

        /// <summary>Prevents GrabPass use in URP/SRP: swap AllIn1VfxGrabPass shader to SRP batch and disable screen-distortion keyword.</summary>
        private static void DisableGrabPassMaterials(GameObject root)
        {
            Shader srpShader = Shader.Find("AllIn1Vfx/AllIn1VfxSRPBatch");
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterials == null) continue;
                foreach (Material mat in r.materials)
                {
                    if (mat == null) continue;
                    if (mat.shader.name == "AllIn1Vfx/AllIn1VfxGrabPass" && srpShader != null)
                        mat.shader = srpShader;
                    if (mat.IsKeywordEnabled("SCREENDISTORTION_ON"))
                        mat.DisableKeyword("SCREENDISTORTION_ON");
                }
            }
        }

        [ClientRpc]
        private void SpawnImpactEffectClientRpc(Vector3 position)
        {
            if (impactEffectPrefab == null) return;
            SpawnImpactAt(position);
        }

        public void Initialize(float bulletSpeed, float bulletDamage, TeamManager.Team team)
        {
            Initialize(bulletSpeed, bulletDamage, team, 1f, 0);
        }

        public void Initialize(float bulletSpeed, float bulletDamage, TeamManager.Team team, float visualScaleMultiplier, byte visualStyleIndex)
        {
            speed = bulletSpeed;
            damage = bulletDamage;
            ownerTeam = team;
            cachedVisualScaleMultiplier = Mathf.Max(0.1f, visualScaleMultiplier);
            cachedVisualStyleIndex = visualStyleIndex;
            // NetworkVariables are set in OnNetworkSpawn (after Spawn) to avoid "written before NetworkObject is spawned" warning.
        }

        private void UpdateVisual()
        {
            // Remove existing visual if any
            if (spawnedVisual != null)
            {
                Destroy(spawnedVisual);
                spawnedVisual = null;
            }

            GameObject visualPrefab = ChooseVisualPrefab();
            float scaleMult = cachedVisualScaleMultiplier != 1f ? cachedVisualScaleMultiplier : bulletVisualScaleMultiplier.Value;
            float scale = bulletVisualScale * scaleMult;
            if (visualPrefab != null)
            {
                foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;
                spawnedVisual = Instantiate(visualPrefab, transform);
                spawnedVisual.transform.localPosition = Vector3.zero;
                spawnedVisual.transform.localRotation = Quaternion.identity;
                spawnedVisual.transform.localScale = Vector3.one * scale;
                
            }
        }

        private GameObject ChooseVisualPrefab()
        {
            // Use cached value first (set immediately), fall back to NetworkVariable if needed
            byte styleIndex = cachedVisualStyleIndex != 0 ? cachedVisualStyleIndex : bulletVisualStyleIndex.Value;
            
            if (bulletVisualPrefabOptions != null && bulletVisualPrefabOptions.Length > 0)
            {
                int idx = Mathf.Clamp(styleIndex, 0, bulletVisualPrefabOptions.Length - 1);
                if (bulletVisualPrefabOptions[idx] != null)
                    return bulletVisualPrefabOptions[idx];
            }
            return bulletVisualPrefab;
        }
    }
}
