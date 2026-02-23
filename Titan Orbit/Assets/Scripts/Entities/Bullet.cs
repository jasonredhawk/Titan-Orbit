using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Visual shape of the bullet: simple shapes, no long tail. Size is driven by damage/scale.</summary>
    public enum BulletShape
    {
        Round,
        Square,
        Zigzag
    }

    /// <summary>
    /// Bullet - hits asteroids and ships, despawns on hit or max distance/lifetime.
    /// Uses path raycast to prevent tunneling when close.
    /// Simple visual shapes (round, square, zigzag); size reflects damage.
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

        [Header("Bullet Visual (customizable VFX-style: core + particle tail)")]
        [Tooltip("Use a prefab instead of the built-in VFX. If set, color/scale still apply; tail params below are ignored for prefabs.")]
        [SerializeField] private GameObject bulletVisualPrefab;
        [Tooltip("Optional per-shape prefabs. [0]=Round, [1]=Square, [2]=Zigzag. If null/empty, use built-in VFX style below.")]
        [SerializeField] private GameObject[] bulletVisualPrefabOptions;
        [Tooltip("Bullet color (core and tail). Fully applied in built-in VFX style.")]
        [SerializeField] private Color proceduralBulletColor = new Color(0.75f, 0.88f, 1f); // Bluish white energy
        [Tooltip("Overall scale. Final = this × scale from cannon damage.")]
        [SerializeField] private float bulletVisualScale = 1.2f;
        [Header("Core (front of bullet)")]
        [Tooltip("Core shape: Round (sphere) or Square (cube).")]
        [SerializeField] private BulletShape defaultShape = BulletShape.Round;
        [Tooltip("Core size relative to bullet scale. 1 = same as scale.")]
        [SerializeField] [Range(0.2f, 2f)] private float coreSize = 0.5f;
        [Header("Tail (particle trail behind)")]
        [Tooltip("Approximate length of the tail in world units (before scale). Set 0 to disable tail.")]
        [SerializeField] [Range(0f, 3f)] private float tailLength = 0.8f;
        [Tooltip("Thickness of tail near the core (particle start size).")]
        [SerializeField] [Range(0.02f, 0.5f)] private float tailWidth = 0.12f;
        [Tooltip("How quickly the trail fades along its length (0 = sharp end, 1 = long smooth fade).")]
        [SerializeField] [Range(0f, 1f)] private float tailFade = 0.7f;
        [Tooltip("Material for core/tail when using built-in VFX. If null, uses default.")]
        [SerializeField] private Material proceduralBulletMaterial;
        [Tooltip("Optional: impact effect when bullet hits.")]
        [SerializeField] private GameObject impactEffectPrefab;
        [SerializeField] private float impactEffectDuration = 3f;
        [SerializeField] private float impactEffectScale = 0.5f;

        private NetworkVariable<float> bulletVisualScaleMultiplier = new NetworkVariable<float>(1f);
        private NetworkVariable<byte> bulletVisualShapeIndex = new NetworkVariable<byte>(0);
        private float cachedVisualScaleMultiplier = 1f;
        private byte cachedVisualShapeIndex;

        private const float FIXED_Y_POSITION = 0f;
        private Rigidbody rb;
        private float spawnTime;
        private Vector3 spawnPosition;
        private Vector3 lastPosition;
        private GameObject spawnedVisual;
        private Material proceduralMaterialInstance; // Instance we create for color; destroyed with bullet

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
            
            bulletVisualScaleMultiplier.OnValueChanged += OnVisualScaleChanged;
            bulletVisualShapeIndex.OnValueChanged += OnVisualShapeChanged;
        }

        private void OnDestroy()
        {
            bulletVisualScaleMultiplier.OnValueChanged -= OnVisualScaleChanged;
            bulletVisualShapeIndex.OnValueChanged -= OnVisualShapeChanged;
            if (proceduralMaterialInstance != null)
            {
                Destroy(proceduralMaterialInstance);
                proceduralMaterialInstance = null;
            }
        }

        private void OnVisualShapeChanged(byte oldValue, byte newValue)
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
            if (IsServer)
            {
                bulletVisualScaleMultiplier.Value = cachedVisualScaleMultiplier;
                bulletVisualShapeIndex.Value = cachedVisualShapeIndex;
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

            // Use toroidal distance so bullets don't despawn when crossing map edge
            float dist = ToroidalMap.ToroidalDistance(transform.position, spawnPosition);
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

        public void Initialize(float bulletSpeed, float bulletDamage, TeamManager.Team team, float visualScaleMultiplier, byte shapeIndex = 0)
        {
            speed = bulletSpeed;
            damage = bulletDamage;
            ownerTeam = team;
            cachedVisualScaleMultiplier = Mathf.Max(0.1f, visualScaleMultiplier);
            cachedVisualShapeIndex = shapeIndex;
            // NetworkVariables set in OnNetworkSpawn
        }

        private void UpdateVisual()
        {
            if (spawnedVisual != null)
            {
                Destroy(spawnedVisual);
                spawnedVisual = null;
            }

            byte shapeIdx = cachedVisualShapeIndex != 0 ? cachedVisualShapeIndex : bulletVisualShapeIndex.Value;
            BulletShape shape = shapeIdx == 0 ? defaultShape : (BulletShape)Mathf.Clamp(shapeIdx, 0, 2);
            float scaleMult = cachedVisualScaleMultiplier != 1f ? cachedVisualScaleMultiplier : bulletVisualScaleMultiplier.Value;
            float scale = bulletVisualScale * scaleMult;

            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                r.enabled = false;

            GameObject visualPrefab = null;
            if (bulletVisualPrefabOptions != null && shapeIdx < bulletVisualPrefabOptions.Length && bulletVisualPrefabOptions[shapeIdx] != null)
                visualPrefab = bulletVisualPrefabOptions[shapeIdx];
            else if (bulletVisualPrefab != null)
                visualPrefab = bulletVisualPrefab;

            if (visualPrefab != null)
            {
                spawnedVisual = Instantiate(visualPrefab, transform);
                FixVfxForUrp(spawnedVisual);
                ApplyColorToVisual(spawnedVisual, proceduralBulletColor);
            }
            else
            {
                spawnedVisual = CreateCustomizableVfxStyle(shape, scale, speed);
                if (spawnedVisual != null)
                    spawnedVisual.transform.SetParent(transform, false);
            }

            if (spawnedVisual != null)
            {
                spawnedVisual.transform.localPosition = Vector3.zero;
                spawnedVisual.transform.localRotation = Quaternion.identity;
                spawnedVisual.transform.localScale = Vector3.one * scale;
            }
        }

        /// <summary>Apply procedural bullet color to VFX prefab instance (Renderers and ParticleSystem colors).</summary>
        private static void ApplyColorToVisual(GameObject root, Color color)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterials == null) continue;
                for (int i = 0; i < r.sharedMaterials.Length; i++)
                {
                    Material mat = r.materials[i]; // instance
                    if (mat == null) continue;
                    mat.color = color;
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                    if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", color);
                    if (mat.HasProperty("_MainColor")) mat.SetColor("_MainColor", color);
                }
            }
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.startColor = color;
            }
        }

        /// <summary>Make AllIn1 VFX prefabs work in URP (GrabPass fix).</summary>
        private static void FixVfxForUrp(GameObject root)
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

        /// <summary>Builds a VFX-style bullet: core + smooth TrailRenderer tail (no dotted particles).</summary>
        private GameObject CreateCustomizableVfxStyle(BulletShape shape, float scale, float bulletSpeed)
        {
            if (proceduralMaterialInstance != null)
            {
                Destroy(proceduralMaterialInstance);
                proceduralMaterialInstance = null;
            }
            Material baseMat = proceduralBulletMaterial != null ? proceduralBulletMaterial : CreateDefaultBulletMaterial();
            proceduralMaterialInstance = new Material(baseMat);
            proceduralMaterialInstance.color = proceduralBulletColor;
            if (proceduralMaterialInstance.HasProperty("_BaseColor"))
                proceduralMaterialInstance.SetColor("_BaseColor", proceduralBulletColor);

            GameObject root = new GameObject("BulletVisual");

            // Core (bright front)
            GameObject core = shape == BulletShape.Square
                ? GameObject.CreatePrimitive(PrimitiveType.Cube)
                : GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(core.GetComponent<Collider>());
            core.transform.SetParent(root.transform, false);
            core.transform.localPosition = Vector3.zero;
            core.transform.localScale = Vector3.one * coreSize;
            var coreMr = core.GetComponent<Renderer>();
            if (coreMr != null) coreMr.sharedMaterial = proceduralMaterialInstance;

            // Tail: smooth ribbon via TrailRenderer (follows bullet, no dots)
            if (tailLength > 0.01f)
            {
                TrailRenderer trail = root.AddComponent<TrailRenderer>();
                trail.time = tailLength / Mathf.Max(5f, bulletSpeed); // so trail length in world ≈ tailLength
                trail.minVertexDistance = 0.03f;
                trail.widthMultiplier = tailWidth * scale;
                trail.autodestruct = false;
                trail.emitting = true;
                trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                trail.receiveShadows = false;
                trail.numCornerVertices = 8;
                trail.numCapVertices = 4;
                trail.widthCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.02f);
                Gradient grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] { new GradientColorKey(proceduralBulletColor, 0f), new GradientColorKey(proceduralBulletColor, 1f) },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(0.95f, 0f),
                        new GradientAlphaKey(0.5f, Mathf.Clamp01(1f - tailFade * 0.5f)),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                trail.colorGradient = grad;
                trail.material = GetTrailMaterial();
                trail.sortingOrder = 0;
            }

            return root;
        }

        private static Material trailMat;

        private static Material GetTrailMaterial()
        {
            if (trailMat != null) return trailMat;
            Shader s = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            trailMat = new Material(s);
            trailMat.renderQueue = 3000;
            if (trailMat.HasProperty("_BaseColor")) trailMat.SetColor("_BaseColor", Color.white);
            return trailMat;
        }

        private static Material defaultBulletMat;

        private static Material CreateDefaultBulletMaterial()
        {
            if (defaultBulletMat != null) return defaultBulletMat;
            defaultBulletMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            defaultBulletMat.color = new Color(0.75f, 0.88f, 1f); // Bluish white, energy weapon
            defaultBulletMat.enableInstancing = true;
            return defaultBulletMat;
        }

        private static GameObject CreateZigzagMesh()
        {
            var go = new GameObject("Zigzag");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            float h = 0.08f;
            float w = 0.12f;
            var verts = new Vector3[]
            {
                new Vector3(-w, 0f, -h), new Vector3(0f, 0f, 0f), new Vector3(w, 0f, -h),
                new Vector3(0f, 0f, 0f), new Vector3(-w, 0f, h), new Vector3(w, 0f, h)
            };
            var norms = new Vector3[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            var tris = new int[] { 0, 1, 2, 1, 4, 5 };
            var mesh = new Mesh { name = "BulletZigzag" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            return go;
        }
    }
}
