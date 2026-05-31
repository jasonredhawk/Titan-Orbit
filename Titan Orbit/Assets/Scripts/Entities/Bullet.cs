using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using System.Globalization;
using TitanOrbit.Generation;
using TitanOrbit.Audio;
using TitanOrbit.Systems;
namespace TitanOrbit.Entities
{
    /// <summary>Visual shape of the bullet: simple shapes, no long tail. Size is driven by ship visual scale multiplier.</summary>
    public enum BulletShape
    {
        Round,
        Square,
        Zigzag
    }

    /// <summary>
    /// LEGACY: per-bullet NetworkObject + NetworkTransform projectile. Kept for backwards
    /// compatibility with existing prefabs and editor tooling, but no runtime path spawns this
    /// type anymore. New shots run through the lightweight server simulation in
    /// <see cref="TitanOrbit.Systems.CombatSystem"/> (see <c>CombatSystem.ServerBullets.cs</c>)
    /// and are rendered on each client by <see cref="ClientBulletTracer"/>.
    /// Visual shape options (round, square, zigzag) are still consumed via
    /// <see cref="BulletShape"/> below.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Bullet : NetworkBehaviour
    {
        public static int ActiveServerBullets { get; private set; }

        [Header("Bullet Settings")]
        [SerializeField] private float speed = 20f;
        [SerializeField] private float damage = 10f;
        [SerializeField] private float lifetime = 2f; // Reduced from 10f for better performance
        [SerializeField] private float maxDistance = 30f; // ~3x previous range for space combat
        [Tooltip("SphereCast hits on non-target geometry (e.g. planet shell, shooter mesh) right after spawn are ignored until the bullet has moved this far from spawn (toroidal). Prevents instant despawn on dedicated servers.")]
        [SerializeField] private float minTravelBeforeHit = 0.5f;
        [SerializeField] private TeamManager.Team ownerTeam = TeamManager.Team.None;
        private ulong ownerShipNetworkId;

        [Header("Bullet Visual (customizable VFX-style: core + particle tail)")]
        [Tooltip("Use a prefab instead of the built-in VFX. If set, color/scale still apply; tail params below are ignored for prefabs.")]
        [SerializeField] private GameObject bulletVisualPrefab;
        [Tooltip("Optional per-shape prefabs. [0]=Round, [1]=Square, [2]=Zigzag. If null/empty, use built-in VFX style below.")]
        [SerializeField] private GameObject[] bulletVisualPrefabOptions;
        [Tooltip("Bullet color (core and tail). Fully applied in built-in VFX style.")]
        [SerializeField] private Color proceduralBulletColor = new Color(0.75f, 0.88f, 1f); // Bluish white energy
        [Tooltip("Overall scale. Final = this × multiplier from ship (cannon bulletScale × fire power / weapon scaling).")]
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
        private NetworkVariable<bool> bulletVisualNoTrail = new NetworkVariable<bool>(false);
        private NetworkVariable<byte> bulletOwnerTeamByte = new NetworkVariable<byte>((byte)TeamManager.Team.None);
        // Audio tuning inputs for clients (muzzle/projectile/impact particle sound pitch).
        private NetworkVariable<float> syncedBulletSpeed = new NetworkVariable<float>(0f);
        private NetworkVariable<float> syncedBulletDamage = new NetworkVariable<float>(0f);
        /// <summary>Planar velocity from server physics; used on remote clients to extrapolate visuals past network delay.</summary>
        private NetworkVariable<Vector3> syncedPlanarVelocity = new NetworkVariable<Vector3>();
        /// <summary>When >= 0, use CombatSystem.GetBulletPrefabFromBank(this) for visual instead of bulletVisualPrefab. Used when spawning via shell.</summary>
        private NetworkVariable<int> visualPrefabBankIndex = new NetworkVariable<int>(-1);
        private float cachedVisualScaleMultiplier = 1f;
        private byte cachedVisualShapeIndex;
        private bool cachedVisualNoTrail;
        private int cachedVisualPrefabBankIndex = -1;

        private const float FIXED_Y_POSITION = 0f;
        private Rigidbody rb;
        private float spawnTime;
        private Vector3 spawnPosition;
        private Vector3 lastPosition;
        private GameObject spawnedVisual;
        private Material proceduralMaterialInstance; // Instance we create for color; destroyed with bullet
        private TrailRenderer cachedTrail;
        private bool serverCounted;
        private static readonly RaycastHit[] SphereCastHits = new RaycastHit[32];
        private static readonly Collider[] OverlapFallbackHits = new Collider[32];

        public float Damage => damage;
        public TeamManager.Team OwnerTeam => ownerTeam;

        /// <summary>
        /// World-space offset for bullet visuals on remote clients: compensates for NetworkTransform delay + interpolation.
        /// Host/server does not apply this (authoritative simulation is already current).
        /// </summary>
        public Vector3 GetClientVisualExtrapolationOffset()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
                return Vector3.zero;

            Vector3 v = syncedPlanarVelocity.Value;
            v.y = 0f;
            if (v.sqrMagnitude < 0.0001f)
                return Vector3.zero;

            var transport = NetworkManager.Singleton.NetworkConfig?.NetworkTransport;
            float rttSec = 0.1f;
            if (transport != null)
            {
                ulong ms = transport.GetCurrentRtt(NetworkManager.ServerClientId);
                if (ms > 0)
                    rttSec = ms * 0.001f;
            }
            rttSec = Mathf.Clamp(rttSec, 0.02f, 0.35f);
            // Displayed transform lags server sim by roughly one-way latency; scale slightly so trails stay aligned.
            return v * (rttSec * 0.55f);
        }

        private static Color GetTeamBulletColor(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None)
                return new Color(0.75f, 0.88f, 1f); // neutral bluish white
            if (TeamManager.Instance != null)
                return TeamManager.GetTeamColor(team);
            // Fallback when TeamManager not ready (e.g. in tests)
            switch (team)
            {
                case TeamManager.Team.TeamA: return new Color(1f, 0.3f, 0.3f);
                case TeamManager.Team.TeamB: return new Color(0.3f, 0.5f, 1f);
                case TeamManager.Team.TeamC: return new Color(0.2f, 0.7f, 0.28f);
                case TeamManager.Team.TeamD: return new Color(0.95f, 0.55f, 0.12f);
                case TeamManager.Team.TeamE: return new Color(0.65f, 0.25f, 0.85f);
                default: return new Color(0.75f, 0.88f, 1f);
            }
        }

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
            bulletVisualNoTrail.OnValueChanged += OnVisualNoTrailChanged;
            bulletOwnerTeamByte.OnValueChanged += OnOwnerTeamChanged;
            visualPrefabBankIndex.OnValueChanged += OnVisualPrefabBankIndexChanged;
        }
        private void OnVisualPrefabBankIndexChanged(int prev, int next) { UpdateVisual(); }

        private void OnOwnerTeamChanged(byte oldVal, byte newVal)
        {
            UpdateVisual();
        }

        private void OnDestroy()
        {
            if (serverCounted)
            {
                ActiveServerBullets = Mathf.Max(0, ActiveServerBullets - 1);
                serverCounted = false;
            }
            bulletVisualScaleMultiplier.OnValueChanged -= OnVisualScaleChanged;
            bulletVisualShapeIndex.OnValueChanged -= OnVisualShapeChanged;
            bulletVisualNoTrail.OnValueChanged -= OnVisualNoTrailChanged;
            bulletOwnerTeamByte.OnValueChanged -= OnOwnerTeamChanged;
            visualPrefabBankIndex.OnValueChanged -= OnVisualPrefabBankIndexChanged;
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

        private void OnVisualNoTrailChanged(bool oldValue, bool newValue)
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
                if (!serverCounted)
                {
                    ActiveServerBullets++;
                    serverCounted = true;
                }
                bulletVisualScaleMultiplier.Value = cachedVisualScaleMultiplier;
                bulletVisualShapeIndex.Value = cachedVisualShapeIndex;
                bulletVisualNoTrail.Value = cachedVisualNoTrail;
                bulletOwnerTeamByte.Value = (byte)ownerTeam;
                visualPrefabBankIndex.Value = cachedVisualPrefabBankIndex;
                syncedBulletSpeed.Value = Mathf.Max(0f, speed);
                syncedBulletDamage.Value = Mathf.Max(0f, damage);
                if (rb != null)
                {
                    Vector3 pv = rb.linearVelocity;
                    pv.y = 0f;
                    syncedPlanarVelocity.Value = pv;
                }
            }

            // Lock Y position to 0 (keep RB and transform aligned; Auto Sync Transforms is off in project settings).
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            if (rb != null) rb.position = pos;

            spawnTime = Time.time;
            spawnPosition = rb != null ? rb.position : transform.position;
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

        public override void OnNetworkDespawn()
        {
            if (serverCounted)
            {
                ActiveServerBullets = Mathf.Max(0, ActiveServerBullets - 1);
                serverCounted = false;
            }
            base.OnNetworkDespawn();
        }

        private System.Collections.IEnumerator DelayedVisualUpdate()
        {
            yield return null; // Wait one frame for NetworkVariable to sync
            UpdateVisual();
        }

        private void FixedUpdate()
        {
            // Physics queries must follow Rigidbody pose (ProjectSettings Auto Sync Transforms is off).
            Vector3 physicsPos = rb != null ? rb.position : transform.position;
            if (Mathf.Abs(physicsPos.y - FIXED_Y_POSITION) > 0.01f)
            {
                physicsPos.y = FIXED_Y_POSITION;
                if (rb != null) rb.position = physicsPos;
                transform.position = physicsPos;
            }
            
            // Ensure rigidbody velocity has no Y component
            if (rb != null && Mathf.Abs(rb.linearVelocity.y) > 0.01f)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel;
            }

            if (!IsServer) return;

            physicsPos = rb != null ? rb.position : transform.position;

            // Use toroidal distance so bullets don't despawn when crossing map edge
            float dist = ToroidalMap.ToroidalDistance(physicsPos, spawnPosition);
            if (dist > maxDistance || Time.time - spawnTime > lifetime)
            {
                DespawnBullet();
                return;
            }

            // Always check for collisions, even if close (fixes tunneling bug)
            Vector3 to = physicsPos;
            float pathLen = Vector3.Distance(lastPosition, to);
            
            if (pathLen > 0.001f)
            {
                // Use SphereCastNonAlloc + distance sort so the nearest valid target wins (single SphereCast can return an arbitrary hit when volumes overlap).
                float bulletRadius = 0.3f; // Larger radius to reliably hit ships (BoxCollider ~0.5 wide)
                Vector3 dir = (to - lastPosition).normalized;
                int n = Physics.SphereCastNonAlloc(lastPosition, bulletRadius, dir, SphereCastHits, pathLen, ~0, QueryTriggerInteraction.Ignore);
                if (n > 1)
                {
                    for (int i = 1; i < n; i++)
                    {
                        RaycastHit key = SphereCastHits[i];
                        float kd = key.distance;
                        int j = i - 1;
                        while (j >= 0 && SphereCastHits[j].distance > kd)
                        {
                            SphereCastHits[j + 1] = SphereCastHits[j];
                            j--;
                        }
                        SphereCastHits[j + 1] = key;
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    RaycastHit hit = SphereCastHits[i];
                    if (hit.collider == null) continue;
                    if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) continue;
                    if (IsColliderOnFiringShipNetworkObject(hit.collider)) continue;
                    if (TryHit(hit.collider, hit.point)) return;
                    float travelledFromSpawn = ToroidalMap.ToroidalDistance(physicsPos, spawnPosition);
                    if (travelledFromSpawn >= minTravelBeforeHit)
                    {
                        DespawnBullet(hit.point);
                        return;
                    }
                }

                // Fallback: spherecast can miss thin windows vs large kinematic hulls; overlap current pose for valid targets.
                int m = Physics.OverlapSphereNonAlloc(physicsPos, bulletRadius + 0.85f, OverlapFallbackHits, ~0, QueryTriggerInteraction.Ignore);
                for (int a = 0; a < m; a++)
                {
                    int best = -1;
                    float bestD = float.MaxValue;
                    for (int b = a; b < m; b++)
                    {
                        Collider c = OverlapFallbackHits[b];
                        if (c == null) continue;
                        if (c.transform == transform || c.transform.IsChildOf(transform)) continue;
                        if (IsColliderOnFiringShipNetworkObject(c)) continue;
                        Vector3 cp = c.ClosestPoint(physicsPos);
                        float d = (cp - physicsPos).sqrMagnitude;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = b;
                        }
                    }
                    if (best < 0) break;
                    Collider chosen = OverlapFallbackHits[best];
                    OverlapFallbackHits[best] = OverlapFallbackHits[a];
                    OverlapFallbackHits[a] = chosen;
                    if (TryHit(chosen, chosen.ClosestPoint(physicsPos))) return;
                }

                // Toroidal fallback for asteroids: physics queries operate in one world copy, but gameplay space is periodic.
                // When ship/bullet coordinates drift many map tiles from asteroid logical coords, SphereCast misses.
                if (TryToroidalAsteroidFallbackHit(lastPosition, to, bulletRadius))
                    return;
            }

            lastPosition = rb != null ? rb.position : transform.position;
        }

        /// <summary>
        /// Checks asteroid hits in toroidal space when world-space physics casts miss due to map tiling copies.
        /// </summary>
        private bool TryToroidalAsteroidFallbackHit(Vector3 from, Vector3 to, float bulletRadius)
        {
            if (Asteroid.AllAsteroids == null || Asteroid.AllAsteroids.Count == 0)
                return false;

            float mapW = Mathf.Max(1f, ToroidalMap.GetMapWidth());
            float mapH = Mathf.Max(1f, ToroidalMap.GetMapHeight());
            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;
            float bestDistSq = float.MaxValue;
            Asteroid bestAsteroid = null;
            Vector3 bestImpact = to;

            for (int i = 0; i < Asteroid.AllAsteroids.Count; i++)
            {
                Asteroid asteroid = Asteroid.AllAsteroids[i];
                if (asteroid == null || asteroid.IsDestroyed)
                    continue;

                float combinedRadius = asteroid.GetCollisionRadiusWorld() + Mathf.Max(0.01f, bulletRadius);
                Vector3 center = asteroid.transform.position;

                Vector3 fromLocal = ToroidalMap.ShortestWorldOffsetXZ(center, from);
                Vector3 toLocal = ToroidalMap.ShortestWorldOffsetXZ(center, to);

                // Unwrap endpoint so the segment is the shortest path in toroidal local coordinates.
                Vector3 seg = toLocal - fromLocal;
                if (seg.x > halfW) seg.x -= mapW;
                else if (seg.x < -halfW) seg.x += mapW;
                if (seg.z > halfH) seg.z -= mapH;
                else if (seg.z < -halfH) seg.z += mapH;
                Vector3 toLocalUnwrapped = fromLocal + seg;

                Vector3 closest = ClosestPointOnSegment(fromLocal, toLocalUnwrapped, Vector3.zero);
                float distSq = closest.sqrMagnitude;
                if (distSq > combinedRadius * combinedRadius)
                    continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestAsteroid = asteroid;
                    bestImpact = new Vector3(center.x + closest.x, FIXED_Y_POSITION, center.z + closest.z);
                }
            }

            if (bestAsteroid == null)
                return false;

            float appliedDamage = damage;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                appliedDamage = 999999f;

            bestAsteroid.ApplyDamageFromBulletServer(appliedDamage, ownerShipNetworkId);

            if (VisualEffectsManager.Instance != null)
            {
                VisualEffectsManager.Instance.SpawnAsteroidFeedbackFromServerAuthority(
                    bestImpact,
                    new AsteroidFloatingFeedback
                    {
                        Team = ownerTeam,
                        Damage = appliedDamage,
                        RemainingHealth = bestAsteroid.RemainingHealth,
                        RemainingGems = bestAsteroid.RemainingGems,
                    });
            }

            DespawnBullet(bestImpact);
            return true;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float denom = Vector3.Dot(ab, ab);
            if (denom <= 1e-6f)
                return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / denom);
            return a + ab * t;
        }

        /// <summary>True if the collider belongs to the firing ship's <see cref="NetworkObject"/> hierarchy (any child mesh/collider).</summary>
        private bool IsColliderOnFiringShipNetworkObject(Collider col)
        {
            if (col == null || ownerShipNetworkId == 0) return false;
            NetworkObject hitNo = col.GetComponentInParent<NetworkObject>();
            return hitNo != null && hitNo.NetworkObjectId == ownerShipNetworkId;
        }

        private void CheckImmediateOverlaps()
        {
            // Check if bullet spawned inside or very close to an asteroid/ship
            float checkRadius = 0.5f;
            Vector3 overlapCenter = rb != null ? rb.position : transform.position;
            Collider[] overlaps = Physics.OverlapSphere(overlapCenter, checkRadius, ~0, QueryTriggerInteraction.Ignore);
            
            foreach (Collider col in overlaps)
            {
                if (col.transform != transform && !col.transform.IsChildOf(transform))
                {
                    // Only hit if it's an asteroid or enemy ship (not the shooter)
                    Asteroid asteroid = col.GetComponentInParent<Asteroid>();
                    if (asteroid != null && !asteroid.IsDestroyed)
                    {
                        TryHit(col, col.ClosestPoint(transform.position));
                        return;
                    }
                    
                    Starship ship = col.GetComponentInParent<Starship>();
                    if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
                    {
                        TryHit(col, col.ClosestPoint(transform.position));
                        return;
                    }
                    DroneBase drone = col.GetComponentInParent<DroneBase>();
                    if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
                    {
                        TryHit(col, col.ClosestPoint(transform.position));
                        return;
                    }
                }
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;
            Vector3 impact = other != null ? other.ClosestPoint(transform.position) : transform.position;
            TryHit(other, impact);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServer) return;
            if (collision == null || collision.collider == null) return;
            Vector3 impact = transform.position;
            if (collision.contactCount > 0)
                impact = collision.GetContact(0).point;
            else if (collision.collider != null)
                impact = collision.collider.ClosestPoint(transform.position);
            TryHit(collision.collider, impact);
        }

        /// <returns>True if we hit a valid target (asteroid, ship, drone) and despawned.</returns>
        private bool TryHit(Collider other, Vector3 impactWorldPos)
        {
            if (other == null) return false;

            // Use GetComponentInParent to handle child colliders (e.g. ship sub-meshes)
                Asteroid asteroid = other.GetComponentInParent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                float appliedDamage = damage;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                    appliedDamage = 999999f; // One-shot asteroids in debug mode
                asteroid.ApplyDamageFromBulletServer(appliedDamage, ownerShipNetworkId);

                if (VisualEffectsManager.Instance != null)
                {
                    VisualEffectsManager.Instance.SpawnAsteroidFeedbackFromServerAuthority(
                        impactWorldPos,
                        new AsteroidFloatingFeedback
                        {
                            Team = ownerTeam,
                            Damage = appliedDamage,
                            RemainingHealth = asteroid.RemainingHealth,
                            RemainingGems = asteroid.RemainingGems,
                        });
                }

                DespawnBullet(impactWorldPos);
                return true;
            }

            PlanetGemMoon moon = other.GetComponentInParent<PlanetGemMoon>();
            if (moon != null)
            {
                if (moon.IsTeamFriendlyToThisMoon(ownerTeam))
                    return false;

                float appliedDamage = damage;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                    appliedDamage = 999999f;
                moon.TakeDamageServer(appliedDamage, ownerTeam);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        impactWorldPos,
                        (int)FloatingCountChannel.DamageMoon,
                        appliedDamage,
                        (int)ownerTeam
                    );

                DespawnBullet(impactWorldPos);
                return true;
            }

            ShipDeathDebris debrisShield = other.GetComponentInParent<ShipDeathDebris>();
            if (debrisShield != null && debrisShield.TryAbsorbBullet(ownerTeam))
            {
                DespawnBullet(impactWorldPos);
                return true;
            }

            Starship ship = other.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                ship.TakeDamageServerRpc(damage, ownerTeam, ownerShipNetworkId);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        impactWorldPos,
                        (int)FloatingCountChannel.DamageShipOrDrone,
                        damage,
                        (int)ownerTeam
                    );

                DespawnBullet(impactWorldPos);
                return true;
            }

            DroneBase drone = other.GetComponentInParent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                drone.TakeDamageServerRpc(damage, ownerTeam, ownerShipNetworkId);

                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        impactWorldPos,
                        (int)FloatingCountChannel.DamageShipOrDrone,
                        damage,
                        (int)ownerTeam
                    );

                DespawnBullet(impactWorldPos);
                return true;
            }

            return false;
        }

        private void DespawnBullet() => DespawnBullet(transform.position);

        private void DespawnBullet(Vector3 impactWorldPos)
        {
            Vector3 impactPos = impactWorldPos;
            impactPos.y = FIXED_Y_POSITION;
            int bankIdx = cachedVisualPrefabBankIndex >= 0 ? cachedVisualPrefabBankIndex : visualPrefabBankIndex.Value;
            float impactPitch = GetImpactSoundPitch();
            GameObject impactPrefab = GetResolvedImpactPrefab(bankIdx);
            // Mobile uses procedural URP particle burst in SpawnImpactAt; still spawn if bank prefab missing.
            bool spawnImpactFx = impactPrefab != null || Application.isMobilePlatform;
            if (spawnImpactFx)
            {
                SpawnImpactEffectClientRpc(impactPos, bankIdx, impactPitch);
                // Mobile impact VFX runs only in ClientRpc (avoids double spawn on host); desktop keeps server-side for listen-server visibility.
                if (!Application.isMobilePlatform)
                    SpawnImpactAt(impactPos, impactPrefab, impactPitch);
            }
            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }

        /// <summary>
        /// Stronger projectile impact (higher bullet damage / fire power) = lower pitch.
        /// </summary>
        private float GetImpactSoundPitch()
        {
            // Bullet damage per hit scales with fire power upgrades/cards. Use a log curve for audible separation.
            float firePower = Mathf.Max(0.1f, damage);
            const float minFirePower = 1f;
            const float maxFirePower = 80f;
            const float highPitch = 2.4f;
            const float lowPitch = 0.35f;

            float clampedPower = Mathf.Clamp(firePower, minFirePower, maxFirePower);
            float minLog = Mathf.Log10(minFirePower);
            float maxLog = Mathf.Log10(maxFirePower);
            float powerLog = Mathf.Log10(clampedPower);
            float normalized = Mathf.InverseLerp(minLog, maxLog, powerLog);
            return Mathf.Lerp(highPitch, lowPitch, normalized);
        }

        /// <summary>Impact prefab from SciFiProjectileScript.impactParticle when using bank, else the default impactEffectPrefab.</summary>
        private GameObject GetResolvedImpactPrefab(int bankIndex)
        {
            if (bankIndex >= 0 && CombatSystem.Instance != null)
            {
                TeamManager.Team teamForResolve = (TeamManager.Team)bulletOwnerTeamByte.Value;
                if (teamForResolve == TeamManager.Team.None) teamForResolve = ownerTeam;
                GameObject fromBank = CombatSystem.Instance.GetImpactPrefabFromBank(bankIndex, teamForResolve);
                if (fromBank != null) return fromBank;
            }
            return impactEffectPrefab;
        }

        private void SpawnImpactAt(Vector3 position, GameObject prefab = null, float pitch = 1f)
        {
            if (Application.isMobilePlatform)
            {
                TeamManager.Team t = (TeamManager.Team)bulletOwnerTeamByte.Value;
                if (t == TeamManager.Team.None) t = ownerTeam;
                VfxUrpCompat.SpawnMobileImpactBurst(position, GetTeamBulletColor(t), impactEffectScale);
                return;
            }

            GameObject usePrefab = prefab != null ? prefab : impactEffectPrefab;
            if (usePrefab == null) return;
            GameObject go = Instantiate(usePrefab, position, Quaternion.identity);
            go.transform.localScale = Vector3.one * impactEffectScale;
            SetAudioPitchInHierarchy(go, pitch);
            VfxUrpCompat.FixAllIn1MaterialsForUrp(go);
            VfxUrpCompat.PlayParticleSystemsInHierarchy(go);
            Destroy(go, impactEffectDuration);
        }

        private static void SetAudioPitchInHierarchy(GameObject root, float pitch)
        {
            if (root == null) return;
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            if (sources == null || sources.Length == 0) return;
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource src = sources[i];
                if (src == null) continue;
                src.pitch = pitch;
            }
        }

        [ClientRpc]
        private void SpawnImpactEffectClientRpc(Vector3 position, int impactPrefabBankIndex, float pitch)
        {
            TeamManager.Team teamForResolve = (TeamManager.Team)bulletOwnerTeamByte.Value;
            if (teamForResolve == TeamManager.Team.None) teamForResolve = ownerTeam;
            if (Application.isMobilePlatform)
            {
                SpawnImpactAt(position, null, pitch);
                if (AudioManager.Instance != null)
                    AudioManager.Instance.PlayImpactSound(pitch);
                return;
            }

            GameObject prefab = impactPrefabBankIndex >= 0 && CombatSystem.Instance != null
                ? CombatSystem.Instance.GetImpactPrefabFromBank(impactPrefabBankIndex, teamForResolve)
                : null;
            if (prefab == null) prefab = impactEffectPrefab;
            if (prefab != null)
                SpawnImpactAt(position, prefab, pitch);
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayImpactSound(pitch);
        }

        public void Initialize(float bulletSpeed, float bulletDamage, TeamManager.Team team)
        {
            Initialize(bulletSpeed, bulletDamage, team, 0, 1f, 0, false);
        }

        public void Initialize(float bulletSpeed, float bulletDamage, TeamManager.Team team, ulong ownerShipId, float visualScaleMultiplier, byte shapeIndex = 0, bool noTrailVisual = false, int visualPrefabBankIndexArg = -1)
        {
            speed = bulletSpeed;
            damage = bulletDamage;
            ownerTeam = team;
            ownerShipNetworkId = ownerShipId;
            cachedVisualScaleMultiplier = Mathf.Max(0.1f, visualScaleMultiplier);
            cachedVisualShapeIndex = shapeIndex;
            cachedVisualNoTrail = noTrailVisual;
            cachedVisualPrefabBankIndex = visualPrefabBankIndexArg;
            // Do not write NetworkVariables here — CombatSystem calls Initialize before NetworkObject.Spawn(),
            // which triggers "doesn't know its NetworkBehaviour yet". OnNetworkSpawn applies cached fields to NVs.
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
            bool noTrailVisual = cachedVisualNoTrail || bulletVisualNoTrail.Value;
            TeamManager.Team teamForColor = (TeamManager.Team)bulletOwnerTeamByte.Value;
            if (teamForColor == TeamManager.Team.None) teamForColor = ownerTeam;
            Color bulletColor = teamForColor != TeamManager.Team.None ? GetTeamBulletColor(teamForColor) : proceduralBulletColor;

            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                r.enabled = false;

            // Mobile: Sci-Fi Arsenal / AllIn1 particle prefabs often fail to render or crash when shaders are forced into the build.
            // Use the built-in URP Lit core + TrailRenderer path only (see CreateCustomizableVfxStyle).
            bool allowBankOrPrefabVisual = !Application.isMobilePlatform;

            GameObject visualPrefab = null;
            int bankIdx = cachedVisualPrefabBankIndex >= 0 ? cachedVisualPrefabBankIndex : visualPrefabBankIndex.Value;
            if (allowBankOrPrefabVisual)
            {
                if (bankIdx >= 0 && CombatSystem.Instance != null)
                    visualPrefab = CombatSystem.Instance.GetVisualPrefabFromBank(bankIdx, teamForColor);
                if (visualPrefab == null && bulletVisualPrefabOptions != null && shapeIdx < bulletVisualPrefabOptions.Length && bulletVisualPrefabOptions[shapeIdx] != null)
                    visualPrefab = bulletVisualPrefabOptions[shapeIdx];
                if (visualPrefab == null && bulletVisualPrefab != null)
                    visualPrefab = bulletVisualPrefab;
            }

            if (visualPrefab != null)
            {
                spawnedVisual = Instantiate(visualPrefab, transform);
                VfxUrpCompat.FixAllIn1MaterialsForUrp(spawnedVisual);
                ApplyColorToVisual(spawnedVisual, bulletColor);
                VfxUrpCompat.PlayParticleSystemsInHierarchy(spawnedVisual);
                float spdForAudio = syncedBulletSpeed.Value > 0.001f ? syncedBulletSpeed.Value : speed;
                SetAudioPitchInHierarchy(spawnedVisual, GetProjectileSoundPitchBySpeed(spdForAudio));
                if (noTrailVisual)
                {
                    var trails = spawnedVisual.GetComponentsInChildren<TrailRenderer>(true);
                    for (int i = 0; i < trails.Length; i++)
                    {
                        trails[i].enabled = false;
                    }
                }
            }
            else
            {
                spawnedVisual = CreateCustomizableVfxStyle(shape, scale, speed, noTrailVisual, bulletColor);
                if (spawnedVisual != null)
                {
                    spawnedVisual.transform.SetParent(transform, false);
                    float spdForAudio = syncedBulletSpeed.Value > 0.001f ? syncedBulletSpeed.Value : speed;
                    SetAudioPitchInHierarchy(spawnedVisual, GetProjectileSoundPitchBySpeed(spdForAudio));
                }
            }

            if (spawnedVisual != null)
            {
                spawnedVisual.transform.localPosition = Vector3.zero;
                spawnedVisual.transform.localRotation = Quaternion.identity;
                spawnedVisual.transform.localScale = Vector3.one * scale;
                cachedTrail = spawnedVisual.GetComponentInChildren<TrailRenderer>(true);
            }
        }

        /// <summary>
        /// Projectile travel sound (if the projectileParticle prefab contains AudioSources):
        /// higher bullet speed = higher pitch.
        /// </summary>
        private float GetProjectileSoundPitchBySpeed(float projectileSpeed)
        {
            float s = Mathf.Max(0.01f, projectileSpeed);
            // Typical runtime speeds are often scaled by CombatSystem.bulletSpeedMultiplier (default 0.4),
            // so use conservative bounds to avoid clamping most shots to the same pitch.
            const float minSpeed = 1f;
            const float maxSpeed = 30f;
            const float lowPitch = 0.35f;
            const float highPitch = 2.2f;

            float clamped = Mathf.Clamp(s, minSpeed, maxSpeed);
            float minLog = Mathf.Log10(minSpeed);
            float maxLog = Mathf.Log10(maxSpeed);
            float sLog = Mathf.Log10(clamped);
            float normalized = Mathf.InverseLerp(minLog, maxLog, sLog);
            // Emphasize contrast (small speeds high, large speeds low doesn't apply here; projectile is opposite).
            float emphasized = Mathf.Pow(normalized, 0.85f);
            return Mathf.Lerp(lowPitch, highPitch, emphasized);
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

        /// <summary>Builds a VFX-style bullet: core + smooth TrailRenderer tail (no dotted particles).</summary>
        private GameObject CreateCustomizableVfxStyle(BulletShape shape, float scale, float bulletSpeed, bool noTrailVisual, Color color)
        {
            if (proceduralMaterialInstance != null)
            {
                Destroy(proceduralMaterialInstance);
                proceduralMaterialInstance = null;
            }
            Material baseMat = proceduralBulletMaterial != null ? proceduralBulletMaterial : CreateDefaultBulletMaterial();
            proceduralMaterialInstance = new Material(baseMat);
            proceduralMaterialInstance.color = color;
            if (proceduralMaterialInstance.HasProperty("_BaseColor"))
                proceduralMaterialInstance.SetColor("_BaseColor", color);

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
            if (!noTrailVisual && tailLength > 0.01f)
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
                    new GradientColorKey[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
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
