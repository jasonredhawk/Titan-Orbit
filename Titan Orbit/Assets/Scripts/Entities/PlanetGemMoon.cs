using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Generation;
using TitanOrbit.Systems;
using TitanOrbit.Core;
using TitanOrbit.UI;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Moon that lives just outside a planet's normal orbit band.
    /// Orbits counter to ship orbit direction.
    /// Docking uses a trigger collider; collisions use a non-trigger collider on the same Rigidbody.
    /// After the moon's shield reaches 0, it drains planet gems and expels them as collectible gems.
    /// </summary>
    public class PlanetGemMoon : MonoBehaviour
    {
        [SerializeField] private Planet planet;
        [SerializeField] private GemMoonStatsDisplay statsDisplay;

        [Header("Orbit Placement")]
        [Tooltip("How far the moon orbit radius sits outside the planet's orbit zone outer edge (nominal). Actual radius is max of this and clearance past rings + dock zone.")]
        [SerializeField] private float moonOrbitOutsideFactor = 1.1f;
        [Tooltip("Extra world-space gap beyond outer rings/orbit zone and moon dock radius so the moon never overlaps level-up visuals.")]
        [SerializeField] private float moonOrbitRingClearanceMarginWorld = 0.4f;

        [Header("Combat")]
        [Tooltip("Base shield at planet level 1. Effective max shield scales with planet level.")]
        [SerializeField] private float maxShieldPoints = 250f;
        [Header("Shield Regeneration")]
        [Tooltip("How long after the last shield hit the moon waits before starting to regenerate.")]
        [SerializeField] private float shieldRegenDelaySeconds = 1.5f;
        [Tooltip("Time (seconds) for the shield to fully regenerate back to max after regen starts.")]
        [SerializeField] private float shieldRegenSecondsToFull = 30f;
        
        [Header("Matrix Shield Visuals")]
        [SerializeField] private GameObject matrixShieldRedPrefab;
        [SerializeField] private GameObject matrixShieldBluePrefab;
        [SerializeField] private GameObject matrixShieldGreenPrefab;
        [SerializeField] private GameObject matrixShieldModularPrefab;
        [Tooltip("Approximate outer shell radius of the MatrixShield prefab at local scale 1 (moon-local space). " +
                 "MatrixShield particles are much larger than 1 unit; ~5–6 matches dock/orbit zone when outer = GetMoonDockSnapRadiusLocal().")]
        [SerializeField] private float matrixShieldRadiusReference = 5.5f;
        [Tooltip("If the shield reads slightly inside the orbit zone edge, increase this to push the shield outer edge out. (Default is a small nudge.)")]
        [SerializeField] private float matrixShieldOrbitZoneEdgeExpandMultiplier = 1.35f;
        [Tooltip("Fine-tune after setting radius reference (1 = match orbit zone outer edge).")]
        [SerializeField] private float matrixShieldScaleMultiplier = 1f;
        [Tooltip("Enemy repulsion trigger radius = moon dock/orbit radius × this. Independent of MatrixShield VFX scale (VFX is often much larger than gameplay). Use ~1.0–1.12 so bounce matches the visible shell.")]
        [SerializeField] private float shieldBarrierRadiusOverDock = 1.06f;
        [SerializeField] private float maxGemPoints = 500f;
        [SerializeField] private float gemDrainPerSecondWhenShieldDown = 20f;
        [SerializeField] private float gemSpawnInterval = 0.25f;
        [SerializeField] private float gemSpawnMinValue = 2f;
        [Header("Collision Damage")]
        [Tooltip("Minimum interval between repeated collision-damage ticks against the same object.")]
        [SerializeField] private float collisionDamageTickInterval = 0.25f;
        [SerializeField] private float moonVsAsteroidBaseDamage = 8f;
        [SerializeField] private float moonVsAsteroidDamagePerSpeed = 1.75f;
        [SerializeField] private float moonVsAsteroidMaxDamage = 35f;
        [SerializeField] private float moonVsMoonBaseDamage = 12f;
        [SerializeField] private float moonVsMoonDamagePerSpeed = 2.25f;
        [SerializeField] private float moonVsMoonMaxDamage = 45f;
        [SerializeField] private float moonVsPlanetBaseDamage = 10f;
        [SerializeField] private float moonVsPlanetDamagePerSpeed = 1.5f;
        [SerializeField] private float moonVsPlanetMaxDamage = 30f;

        [Header("Landing Scatter")]
        [SerializeField] private float landingHemisphereHemisphereBias = 0.6f; // Higher = more likely to pick the “top/front” hemisphere.

        [Header("Spin")]
        [Tooltip("Visual spin around moon local Y axis (degrees/second).")]
        [SerializeField] private float spinDegreesPerSecond = 8.96f; // ~44% slower than previous 16 deg/s (16 * 0.56)

        private SphereCollider _dockTrigger;
        private SphereCollider _shieldTrigger;
        private SphereCollider _bodyCollider;
        private Rigidbody _rb;
        private Transform _visualTransform;

        private float orbitAngle;
        private float spinAngleDegrees;
        private Vector3 cachedWorldVelocity;

        private float shieldPoints;
        private float runtimeMaxShieldPoints;
        private double lastShieldHitServerTime;

        private GameObject _matrixShieldInstance;
        private TeamManager.Team _matrixShieldTeam = TeamManager.Team.None;
        private Quaternion _matrixShieldBaseLocalRotation = Quaternion.identity;
        private float _matrixShieldBaseXScale = 1f;
        private float _matrixShieldBaseYScale = 1f;
        private float _lastShieldDockLocalRadius = -1f;
        private float _lastShieldEdgeExpandMultiplier = -1f;
        private ParticleSystem[] _matrixShieldParticles;

        private float gemPoints;
        /// <summary>Authoritative on server; updated on clients via <see cref="Planet.GemMoonShieldClientRpc"/>.</summary>
        private float gemPointsClientDisplay;
        private const float MoonGemSyncInterval = 0.25f;
        private float lastMoonGemSyncTime = -999f;
        private float gemDrainAccumulator;
        private float gemSpawnTimer;
        private readonly Dictionary<int, float> _lastAsteroidImpactTimeByInstanceId = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _lastMoonImpactTimeByInstanceId = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _lastPlanetImpactTimeByInstanceId = new Dictionary<int, float>();
        private static readonly List<PlanetGemMoon> ActiveMoons = new List<PlanetGemMoon>();
        private static readonly Collider[] PlanetOverlapBuffer = new Collider[32];

        [Header("Enemy Shield Barrier")]
        [Tooltip("When an enemy/non-friendly ship enters the shield area while the shield has points, push it outward to prevent passing through.")]
        [SerializeField] private float enemyShieldRepelMinSpeed = 8f;
        [SerializeField] private float enemyShieldRepelMaxSpeed = 22f;

        public Planet Planet => planet;
        public Vector3 WorldOrbitVelocity => cachedWorldVelocity;
        public float SpinAngleDegrees => spinAngleDegrees;
        public float SpinDegreesPerSecond => spinDegreesPerSecond;
        public Transform LandingParentTransform
        {
            get
            {
                if (_visualTransform == null) _visualTransform = transform.Find("GemMoonVisual");
                return _visualTransform != null ? _visualTransform : transform;
            }
        }
        public Vector3 SpinAxisWorld
        {
            get
            {
                if (planet == null) return transform.up;
                Vector3 axis = planet.GetSpinAxisWorld();
                if (axis.sqrMagnitude < 0.0001f) return transform.up;
                return axis.normalized;
            }
        }

        /// <summary>Moon gem reservoir for UI (server: live <see cref="gemPoints"/>; clients: last synced value).</summary>
        public float GetMoonGemsForDisplay()
        {
            if (planet != null && planet.IsServer)
                return gemPoints;
            return gemPointsClientDisplay;
        }

        /// <summary>Current shield points for UI.</summary>
        public float GetShieldPointsForDisplay() => Mathf.Max(0f, shieldPoints);

        /// <summary>Max shield points for UI.</summary>
        public float GetMaxShieldPointsForDisplay() => Mathf.Max(0f, runtimeMaxShieldPoints);

        private float GetScaledMaxShieldPoints()
        {
            int level = planet != null ? Mathf.Max(1, planet.PlanetLevel) : 1;
            return Mathf.Max(0.001f, maxShieldPoints * level);
        }

        private bool RefreshScaledShieldCapacityServer()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return false;

            float scaledMax = GetScaledMaxShieldPoints();
            if (Mathf.Abs(scaledMax - runtimeMaxShieldPoints) <= 0.001f)
                return false;

            float prevMax = Mathf.Max(0.001f, runtimeMaxShieldPoints);
            float ratio = Mathf.Clamp01(shieldPoints / prevMax);
            runtimeMaxShieldPoints = scaledMax;
            shieldPoints = Mathf.Clamp(ratio * runtimeMaxShieldPoints, 0f, runtimeMaxShieldPoints);
            return true;
        }

        private void Awake()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();

            _rb = GetComponent<Rigidbody>();
            if (_rb == null)
                _rb = gameObject.AddComponent<Rigidbody>();
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.constraints = RigidbodyConstraints.FreezeRotation;

            // Assign dock trigger + shield trigger vs physics body collider.
            // Some prefabs/scenes can end up with duplicate SphereColliders; keep exactly:
            // - 1 dock trigger (smallest trigger radius)
            // - 1 shield trigger (largest trigger radius)
            // - 1 non-trigger (smallest body radius)
            SphereCollider[] cols = GetComponents<SphereCollider>();

            SphereCollider keepDockTrigger = null;
            SphereCollider keepShieldTrigger = null;
            SphereCollider keepBody = null;
            float keepDockRadius = float.MaxValue;
            float keepShieldRadius = 0f;
            float keepBodyRadius = float.MaxValue;
            var toDestroy = new System.Collections.Generic.List<SphereCollider>();

            foreach (var c in cols)
            {
                if (c == null) continue;
                if (c.isTrigger)
                {
                    if (keepDockTrigger == null || c.radius < keepDockRadius)
                    {
                        keepDockTrigger = c;
                        keepDockRadius = c.radius;
                    }
                    if (keepShieldTrigger == null || c.radius > keepShieldRadius)
                    {
                        keepShieldTrigger = c;
                        keepShieldRadius = c.radius;
                    }
                }
                else
                {
                    if (keepBody == null || c.radius < keepBodyRadius)
                    {
                        keepBody = c;
                        keepBodyRadius = c.radius;
                    }
                }
            }

            // Destroy duplicates: keep only dock trigger, shield trigger, and one body collider.
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null) continue;
                bool isDock = c == keepDockTrigger;
                bool isShield = c == keepShieldTrigger;
                bool isBody = c == keepBody;

                if (c.isTrigger)
                {
                    if (!isDock && !isShield)
                        toDestroy.Add(c);
                }
                else
                {
                    if (!isBody)
                        toDestroy.Add(c);
                }
            }

            for (int i = 0; i < toDestroy.Count; i++)
            {
                if (toDestroy[i] != null)
                    Destroy(toDestroy[i]);
            }

            _dockTrigger = keepDockTrigger;
            _shieldTrigger = (keepShieldTrigger != null && keepShieldTrigger != keepDockTrigger) ? keepShieldTrigger : null;
            _bodyCollider = keepBody;

            EnsureMoonOrbitZoneVisual();

            // Back-compat: if only one collider exists (from earlier moon versions), create the missing counterparts.
            if (_dockTrigger == null && _bodyCollider != null)
            {
                _dockTrigger = gameObject.AddComponent<SphereCollider>();
                _dockTrigger.isTrigger = true;
                _dockTrigger.radius = _bodyCollider.radius;
            }
            else if (_bodyCollider == null && _dockTrigger != null)
            {
                _bodyCollider = gameObject.AddComponent<SphereCollider>();
                _bodyCollider.isTrigger = false;
                _bodyCollider.radius = _dockTrigger.radius;
            }

            if (_shieldTrigger == null && _dockTrigger != null)
            {
                // Shield trigger is what we'll use to block enemy ships with repulsion.
                _shieldTrigger = gameObject.AddComponent<SphereCollider>();
                _shieldTrigger.isTrigger = true;
                _shieldTrigger.radius = GetMoonShieldBarrierRadiusLocalFromDockRadius(_dockTrigger.radius);
            }
        }

        private void EnsureMoonOrbitZoneVisual()
        {
            if (transform.Find("MoonOrbitZone") != null) return;
            GameObject oz = new GameObject("MoonOrbitZone");
            oz.transform.SetParent(transform, false);
            oz.transform.localPosition = Vector3.zero;
            oz.transform.localRotation = Quaternion.identity;
            oz.transform.localScale = Vector3.one;
            oz.AddComponent<GemMoonOrbitZoneVisual>();
        }

        private void OnEnable()
        {
            if (!ActiveMoons.Contains(this))
                ActiveMoons.Add(this);

            EnsureMoonOrbitZoneVisual();
            EnsureMatrixShieldVisual();

            ulong id = 0;
            if (planet != null)
            {
                var no = planet.GetComponent<NetworkObject>();
                if (no != null) id = no.NetworkObjectId;
            }
            orbitAngle = (id % 6283UL) * 0.001f;
            spinAngleDegrees = (id % 360UL);

            runtimeMaxShieldPoints = GetScaledMaxShieldPoints();
            shieldPoints = runtimeMaxShieldPoints;
            lastShieldHitServerTime = GetServerTimeNowSeconds();

            // Only the server tracks/updates gem drain & spawning logic.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                gemPoints = maxGemPoints;
                gemDrainAccumulator = 0f;
                gemSpawnTimer = 0f;
            }
            else
            {
                // Client receives authoritative shield state through RPC shortly after spawn.
                // Until then, assume full shield so the matrix VFX isn't hidden at 0 (RPC will correct).
                runtimeMaxShieldPoints = Mathf.Max(0.001f, maxShieldPoints);
                shieldPoints = runtimeMaxShieldPoints;
                lastShieldHitServerTime = GetServerTimeNowSeconds();
            }

            UpdateMatrixShieldVisual();
            EnsureGemMoonStatsDisplay();
        }

        private void OnDisable()
        {
            ActiveMoons.Remove(this);
        }

        private void Start()
        {
            StartCoroutine(DeferredMatrixShieldTeamRefresh());
            if (planet != null && planet.IsServer)
                StartCoroutine(PushInitialMoonStateAfterSpawn());
        }

        /// <summary>
        /// After spawn, planet team ownership may sync one frame later than the moon enables.
        /// Recreate the matrix prefab if <see cref="EnsureMatrixShieldVisual"/> first ran with the wrong team.
        /// </summary>
        private IEnumerator DeferredMatrixShieldTeamRefresh()
        {
            yield return null;
            UpdateMatrixShieldVisual();
        }

        private IEnumerator PushInitialMoonStateAfterSpawn()
        {
            yield return null;
            PushFullStateToClients();
        }

        /// <summary>Call when this moon's planet <see cref="Planet.TeamOwnership"/> changes so the matrix shield uses the correct team prefab.</summary>
        public void RefreshMatrixShieldForPlanetTeam()
        {
            UpdateMatrixShieldVisual();
        }

        private void EnsureGemMoonStatsDisplay()
        {
            if (statsDisplay == null)
                statsDisplay = GetComponent<GemMoonStatsDisplay>();
            if (statsDisplay == null)
                statsDisplay = gameObject.AddComponent<GemMoonStatsDisplay>();
            statsDisplay.Init(this,
                planet != null ? planet.GemMoonHudGemIcon : null,
                planet != null ? planet.GemMoonHudShieldIcon : null);
        }

        /// <summary>Server-only: push shield + moon gems to all clients (e.g. after spawn or combat).</summary>
        public void PushFullStateToClients()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || planet == null) return;
            planet.GemMoonShieldClientRpc(shieldPoints, runtimeMaxShieldPoints, (float)lastShieldHitServerTime, gemPoints);
        }

        private void MaybeSyncMoonGemsToClientsThrottled()
        {
            if (Time.time - lastMoonGemSyncTime < MoonGemSyncInterval) return;
            lastMoonGemSyncTime = Time.time;
            PushFullStateToClients();
        }

        private void FixedUpdate()
        {
            if (planet == null) return;

            Vector3 center = planet.transform.position;
            center.y = 0f;

            float rNominal = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal() * Mathf.Max(1.01f, moonOrbitOutsideFactor);
            float moonDock = GetMoonDockSnapRadiusWorld();
            float rClear = planet.GetGemMoonStructuralOuterRadiusWorld() + moonDock + Mathf.Max(0f, moonOrbitRingClearanceMarginWorld);
            float r = Mathf.Max(rNominal, rClear);
            float speed = planet.GetStandardOrbitSpeedAtOuterOrbit();
            float omega = r > 0.001f ? speed / r : 0f;

            // Counter to ship orbit direction
            orbitAngle -= omega * Time.fixedDeltaTime;

            Vector3 offset = new Vector3(Mathf.Cos(orbitAngle), 0f, Mathf.Sin(orbitAngle)) * r;
            Vector3 pos = center + offset;
            pos.y = 0f;

            Vector3 radial = r > 0.001f ? offset / r : Vector3.forward;
            cachedWorldVelocity = new Vector3(-radial.z, 0f, radial.x) * speed;

            if (_rb != null && _rb.isKinematic)
            {
                _rb.MovePosition(pos);
            }
            else
            {
                transform.position = pos;
            }

            // Visual moon spin around same tilted axis as the parent planet/rings.
            spinAngleDegrees = (spinAngleDegrees + Mathf.Max(0f, spinDegreesPerSecond) * Time.fixedDeltaTime) % 360f;
            if (_visualTransform == null) _visualTransform = transform.Find("GemMoonVisual");
            if (_visualTransform != null)
                _visualTransform.RotateAround(transform.position, SpinAxisWorld, Mathf.Max(0f, spinDegreesPerSecond) * Time.fixedDeltaTime);

            // Keep moon shield capacity scaled with current planet level.
            if (RefreshScaledShieldCapacityServer())
                PushFullStateToClients();

            TickMoonShieldRegen();
            UpdateMatrixShieldVisual();

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            TickMoonCombatDrain();
            TickMoonCollisionDamageServer();
        }

        private void TickMoonCollisionDamageServer()
        {
            float now = Time.time;

            Vector3 moonPos = transform.position;
            moonPos.y = 0f;
            float moonRadius = GetMoonBodyRadiusWorld();
            if (moonRadius <= 0.0001f) return;

            TickMoonVsAsteroidCollisionDamage(now, moonPos, moonRadius);
            TickMoonVsMoonCollisionDamage(now, moonPos, moonRadius);
            TickMoonVsPlanetCollisionDamage(now, moonPos, moonRadius);
        }

        private void TickMoonVsAsteroidCollisionDamage(float now, Vector3 moonPos, float moonRadius)
        {
            for (int i = 0; i < Asteroid.AllAsteroids.Count; i++)
            {
                Asteroid asteroid = Asteroid.AllAsteroids[i];
                if (asteroid == null || asteroid.IsDestroyed) continue;

                int id = asteroid.GetInstanceID();
                if (_lastAsteroidImpactTimeByInstanceId.TryGetValue(id, out float lastHitAt)
                    && now - lastHitAt < collisionDamageTickInterval)
                    continue;

                Vector3 asteroidPos = asteroid.transform.position;
                asteroidPos.y = 0f;
                float asteroidRadius = asteroid.GetCollisionRadiusWorld();
                float overlapDistance = moonRadius + asteroidRadius;
                if (overlapDistance <= 0.0001f) continue;

                float dist = ToroidalMap.ToroidalDistance(moonPos, asteroidPos);
                if (dist > overlapDistance) continue;

                Vector3 relativeVelocity = cachedWorldVelocity - asteroid.WorldVelocity;
                relativeVelocity.y = 0f;
                float impactSpeed = relativeVelocity.magnitude;
                float damage = moonVsAsteroidBaseDamage + moonVsAsteroidDamagePerSpeed * impactSpeed;
                damage = Mathf.Clamp(damage, 0f, moonVsAsteroidMaxDamage);
                if (damage <= 0.0001f) continue;

                _lastAsteroidImpactTimeByInstanceId[id] = now;
                TakeDamageServer(damage);
                asteroid.TakeDamageServerRpc(damage, 0ul);
            }
        }

        private void TickMoonVsMoonCollisionDamage(float now, Vector3 moonPos, float moonRadius)
        {
            for (int i = 0; i < ActiveMoons.Count; i++)
            {
                PlanetGemMoon other = ActiveMoons[i];
                if (other == null || other == this) continue;
                if (!other.isActiveAndEnabled) continue;
                if (GetInstanceID() >= other.GetInstanceID()) continue; // Pair is handled once.

                int id = other.GetInstanceID();
                if (_lastMoonImpactTimeByInstanceId.TryGetValue(id, out float lastHitAt)
                    && now - lastHitAt < collisionDamageTickInterval)
                    continue;

                Vector3 otherPos = other.transform.position;
                otherPos.y = 0f;
                float otherRadius = other.GetMoonBodyRadiusWorld();
                float overlapDistance = moonRadius + otherRadius;
                if (overlapDistance <= 0.0001f) continue;

                float dist = ToroidalMap.ToroidalDistance(moonPos, otherPos);
                if (dist > overlapDistance) continue;

                Vector3 relativeVelocity = cachedWorldVelocity - other.cachedWorldVelocity;
                relativeVelocity.y = 0f;
                float impactSpeed = relativeVelocity.magnitude;
                float damage = moonVsMoonBaseDamage + moonVsMoonDamagePerSpeed * impactSpeed;
                damage = Mathf.Clamp(damage, 0f, moonVsMoonMaxDamage);
                if (damage <= 0.0001f) continue;

                _lastMoonImpactTimeByInstanceId[id] = now;
                other._lastMoonImpactTimeByInstanceId[GetInstanceID()] = now;
                TakeDamageServer(damage);
                other.TakeDamageServer(damage);
            }
        }

        private void TickMoonVsPlanetCollisionDamage(float now, Vector3 moonPos, float moonRadius)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                transform.position,
                Mathf.Max(0.01f, moonRadius),
                PlanetOverlapBuffer,
                ~0,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                Collider c = PlanetOverlapBuffer[i];
                if (c == null) continue;

                Planet hitPlanet = c.GetComponentInParent<Planet>();
                if (hitPlanet == null) continue;
                if (planet != null && hitPlanet == planet) continue;

                int id = hitPlanet.GetInstanceID();
                if (_lastPlanetImpactTimeByInstanceId.TryGetValue(id, out float lastHitAt)
                    && now - lastHitAt < collisionDamageTickInterval)
                    continue;

                Vector3 planetPos = hitPlanet.transform.position;
                planetPos.y = 0f;
                float planetRadius = Mathf.Max(0.01f, hitPlanet.PlanetSize * 0.5f);
                float overlapDistance = moonRadius + planetRadius;
                float dist = ToroidalMap.ToroidalDistance(moonPos, planetPos);
                if (dist > overlapDistance) continue;

                float impactSpeed = cachedWorldVelocity.magnitude;
                float damage = moonVsPlanetBaseDamage + moonVsPlanetDamagePerSpeed * impactSpeed;
                damage = Mathf.Clamp(damage, 0f, moonVsPlanetMaxDamage);
                if (damage <= 0.0001f) continue;

                _lastPlanetImpactTimeByInstanceId[id] = now;
                TakeDamageServer(damage);
            }
        }

        private void TickMoonCombatDrain()
        {
            // When shield is down, drain planet gems and expel them as collectible gems.
            if (shieldPoints > 0f) return;
            if (gemPoints <= 0.0001f) return;
            if (planet == null) return;

            float planetGems = planet.CurrentGems;
            if (planetGems <= 0.0001f) return;

            float drain = gemDrainPerSecondWhenShieldDown * Time.fixedDeltaTime;
            drain = Mathf.Min(drain, gemPoints, planetGems);
            if (drain <= 0.0001f) return;

            gemPoints -= drain;
            planet.DrainGemsFromServer(drain);
            MaybeSyncMoonGemsToClientsThrottled();

            gemDrainAccumulator += drain;
            gemSpawnTimer += Time.fixedDeltaTime;

            if (GemSpawner.Instance == null) return;
            if (gemSpawnTimer < gemSpawnInterval) return;
            if (gemDrainAccumulator < gemSpawnMinValue) return;

            float spawnValue = gemDrainAccumulator;
            gemDrainAccumulator = 0f;
            gemSpawnTimer = 0f;
            // expelledByShipId = 0 => no expelled cooldown block
            GemSpawner.Instance.SpawnGemsFromShipServerRpc(transform.position, spawnValue, 0ul);
        }

        private void TickMoonShieldRegen()
        {
            if (shieldPoints >= runtimeMaxShieldPoints - 0.001f) return;

            double now = GetServerTimeNowSeconds();
            double timeSinceHit = now - lastShieldHitServerTime;
            if (timeSinceHit < shieldRegenDelaySeconds) return;

            float regenRatePerSecond = runtimeMaxShieldPoints / Mathf.Max(0.01f, shieldRegenSecondsToFull);
            shieldPoints = Mathf.Min(runtimeMaxShieldPoints, shieldPoints + regenRatePerSecond * Time.fixedDeltaTime);
        }

        /// <summary>
        /// Gem moons created at runtime have no prefab asset; <see cref="Planet"/> assigns Archanor MatrixShield prefabs here so builds (and Team D/E) get VFX.
        /// </summary>
        public void InjectMatrixShieldPrefabsIfMissing(GameObject red, GameObject blue, GameObject green, GameObject modular)
        {
            bool added = false;
            if (matrixShieldRedPrefab == null && red != null) { matrixShieldRedPrefab = red; added = true; }
            if (matrixShieldBluePrefab == null && blue != null) { matrixShieldBluePrefab = blue; added = true; }
            if (matrixShieldGreenPrefab == null && green != null) { matrixShieldGreenPrefab = green; added = true; }
            if (matrixShieldModularPrefab == null && modular != null) { matrixShieldModularPrefab = modular; added = true; }
            if (!added) return;

            if (_matrixShieldInstance != null)
            {
                Destroy(_matrixShieldInstance);
                _matrixShieldInstance = null;
                _matrixShieldParticles = null;
                _matrixShieldTeam = TeamManager.Team.None;
            }
            UpdateMatrixShieldVisual();
        }

        public void ApplyShieldClientSync(float currentShieldPoints, float syncMaxShieldPoints, float lastHitServerTimeSeconds, float currentMoonGemPoints)
        {
            shieldPoints = Mathf.Max(0f, currentShieldPoints);
            runtimeMaxShieldPoints = Mathf.Max(0.001f, syncMaxShieldPoints);
            lastShieldHitServerTime = lastHitServerTimeSeconds;
            gemPointsClientDisplay = Mathf.Max(0f, currentMoonGemPoints);
            UpdateMatrixShieldVisual();
            if (statsDisplay == null)
                statsDisplay = GetComponent<GemMoonStatsDisplay>();
            if (statsDisplay != null)
                statsDisplay.Refresh();
        }

        private void EnsureMatrixShieldVisual()
        {
            // Neutral planets (Team.None) still get a shield visual; they just use the white modular prefab.
            TeamManager.Team team = planet != null ? planet.TeamOwnership : TeamManager.Team.None;
            if (_matrixShieldInstance != null && _matrixShieldTeam == team) return;

            if (_matrixShieldInstance != null)
                Destroy(_matrixShieldInstance);

            GameObject prefab = GetMatrixShieldPrefab(team);
            if (prefab == null) return;

            _matrixShieldInstance = Instantiate(prefab, transform);
            _matrixShieldInstance.transform.localPosition = Vector3.zero;
            // Preserve prefab-authored local rotation as a base orientation offset.
            _matrixShieldBaseLocalRotation = _matrixShieldInstance.transform.localRotation;

            _matrixShieldTeam = team;
            Vector3 baseScale = _matrixShieldInstance.transform.localScale;
            _matrixShieldBaseXScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.x));
            _matrixShieldBaseYScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.y));
            _lastShieldDockLocalRadius = -1f;
            _lastShieldEdgeExpandMultiplier = -1f;

            _matrixShieldParticles = _matrixShieldInstance.GetComponentsInChildren<ParticleSystem>(true);
        }

        private void UpdateMatrixShieldVisual()
        {
            EnsureMatrixShieldVisual();
            if (_matrixShieldInstance == null) return;

            // Match moon tilt: align shield "up" to the same tilted spin axis used by the moon.
            Vector3 axisLocal = transform.InverseTransformDirection(SpinAxisWorld);
            if (axisLocal.sqrMagnitude < 0.0001f) axisLocal = Vector3.up;
            axisLocal.Normalize();
            Quaternion alignToMoonAxis = Quaternion.FromToRotation(Vector3.up, axisLocal);
            _matrixShieldInstance.transform.localRotation = alignToMoonAxis * _matrixShieldBaseLocalRotation;

            // Match Shapes orbit zone: outer edge = moon-local dock trigger radius (same as GemMoonOrbitZoneVisual outer).
            float orbitOuterLocal = GetMoonDockSnapRadiusLocal();
            float edgeExpand = Mathf.Max(0.0001f, matrixShieldOrbitZoneEdgeExpandMultiplier);
            if (_lastShieldDockLocalRadius < 0f
                || Mathf.Abs(orbitOuterLocal - _lastShieldDockLocalRadius) > 0.001f
                || Mathf.Abs(edgeExpand - _lastShieldEdgeExpandMultiplier) > 0.0001f)
            {
                _lastShieldDockLocalRadius = orbitOuterLocal;
                _lastShieldEdgeExpandMultiplier = edgeExpand;
                float denom = Mathf.Max(0.001f, matrixShieldRadiusReference);
                // matrixShieldRadiusReference is "radius at localScale = 1" (prefab-authored scale).
                // The prefab may have a non-1 base scale, so we apply the multiplier on top of the prefab base scale.
                float targetOuterLocal = orbitOuterLocal * edgeExpand;
                float scaleMultiplier = (targetOuterLocal / denom) * Mathf.Max(0.0001f, matrixShieldScaleMultiplier);
                float scaleX = _matrixShieldBaseXScale * scaleMultiplier;
                // Keep the shield's aspect ratio from the prefab, but scale it consistently as the moon radius changes.
                float y = _matrixShieldBaseYScale * scaleMultiplier;
                _matrixShieldInstance.transform.localScale = new Vector3(scaleX, y, scaleX);
            }

            bool shouldBeActive = shieldPoints > 0.001f;
            SetShieldBarrierColliderEnabled(shouldBeActive);
            _matrixShieldInstance.SetActive(shouldBeActive);

            // Show depletion as reduced particle emission (instead of only toggling on/off).
            if (_matrixShieldParticles != null && shouldBeActive)
            {
                float ratio = Mathf.Clamp01(shieldPoints / Mathf.Max(0.001f, runtimeMaxShieldPoints));
                foreach (var ps in _matrixShieldParticles)
                {
                    if (ps == null) continue;
                    var emission = ps.emission;
                    emission.rateOverTimeMultiplier = ratio;
                }
            }
        }

        /// <summary>
        /// Shield barrier collider is active only while shield points remain.
        /// When shield is down, bullets can travel past the outer barrier and hit deeper moon colliders.
        /// </summary>
        private void SetShieldBarrierColliderEnabled(bool enabled)
        {
            if (_shieldTrigger == null) return;
            if (_shieldTrigger.enabled == enabled) return;
            _shieldTrigger.enabled = enabled;
        }

        private double GetServerTimeNowSeconds()
        {
            if (NetworkManager.Singleton != null)
                return NetworkManager.Singleton.ServerTime.Time;
            return Time.timeAsDouble;
        }

        private GameObject GetMatrixShieldPrefab(TeamManager.Team team)
        {
            GameObject prefab = null;
            switch (team)
            {
                case TeamManager.Team.None:
                    prefab = matrixShieldModularPrefab;
                    break;
                case TeamManager.Team.TeamA:
                    prefab = matrixShieldRedPrefab;
                    break;
                case TeamManager.Team.TeamB:
                    prefab = matrixShieldBluePrefab;
                    break;
                case TeamManager.Team.TeamC:
                    prefab = matrixShieldGreenPrefab;
                    break;
                case TeamManager.Team.TeamD:
                case TeamManager.Team.TeamE:
                    prefab = matrixShieldModularPrefab;
                    break;
            }

#if UNITY_EDITOR
            if (prefab == null)
            {
                // Editor fallback: load the prefabs by their asset-path so you don't need to manually assign them.
                // (Runtime builds still require the serialized fields to be assigned unless you add an Addressables/Resources solution.)
                string path = team switch
                {
                    TeamManager.Team.None => "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldModular.prefab",
                    TeamManager.Team.TeamA => "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldRed.prefab",
                    TeamManager.Team.TeamB => "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldBlue.prefab",
                    TeamManager.Team.TeamC => "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldGreen.prefab",
                    TeamManager.Team.TeamD => "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldModular.prefab",
                    TeamManager.Team.TeamE => "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldModular.prefab",
                    _ => null
                };

                if (!string.IsNullOrEmpty(path))
                    prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
#endif

            return prefab;
        }

        /// <summary>Server-only: called by bullets/rockets/mines when they hit the moon.</summary>
        /// <param name="attackerTeam">If set, damage is skipped when it matches this moon's owning team (no friendly fire on own moon).</param>
        public void TakeDamageServer(float damage, TeamManager.Team attackerTeam = TeamManager.Team.None)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (damage <= 0f) return;
            if (attackerTeam != TeamManager.Team.None && IsTeamFriendlyToThisMoon(attackerTeam))
                return;

            lastShieldHitServerTime = GetServerTimeNowSeconds();

            float remaining = damage;

            if (shieldPoints > 0f)
            {
                float used = Mathf.Min(shieldPoints, remaining);
                shieldPoints -= used;
                remaining -= used;
                if (shieldPoints < 0f) shieldPoints = 0f;
            }

            if (remaining > 0f)
                gemPoints = Mathf.Max(0f, gemPoints - remaining);

            // Sync shield state + moon gems to clients so visuals and UI stay correct.
            if (planet != null)
                planet.GemMoonShieldClientRpc(shieldPoints, runtimeMaxShieldPoints, (float)lastShieldHitServerTime, gemPoints);
        }

        /// <summary>Dock trigger radius in moon local space (same space as <see cref="SphereCollider.radius"/> on this object).</summary>
        public float GetMoonDockSnapRadiusLocal()
        {
            if (_dockTrigger != null)
                return Mathf.Max(0.0001f, _dockTrigger.radius);
            if (_bodyCollider != null)
                return Mathf.Max(0.0001f, _bodyCollider.radius);
            return 0.25f;
        }

        /// <summary>Physics body radius in moon local space.</summary>
        public float GetMoonBodyRadiusLocal()
        {
            if (_bodyCollider != null)
                return Mathf.Max(0.0001f, _bodyCollider.radius);
            return GetMoonDockSnapRadiusLocal();
        }

        /// <summary>World-space radius of the docking trigger; snap only applies while within this distance of the moon.</summary>
        public float GetMoonDockSnapRadiusWorld()
        {
            float r = GetMoonDockSnapRadiusLocal();
            Vector3 lossy = transform.lossyScale;
            return r * Mathf.Max(lossy.x, Mathf.Max(lossy.y, lossy.z));
        }

        /// <summary>World-space radius of the outer shield barrier (may be larger than the dock trigger).</summary>
        public float GetMoonShieldOuterRadiusWorld()
        {
            float r = GetMoonShieldOuterRadiusLocal();
            Vector3 lossy = transform.lossyScale;
            return r * Mathf.Max(lossy.x, Mathf.Max(lossy.y, lossy.z));
        }

        /// <summary>World-space radius of the moon collision body.</summary>
        public float GetMoonBodyRadiusWorld()
        {
            float r = GetMoonBodyRadiusLocal();
            Vector3 lossy = transform.lossyScale;
            return r * Mathf.Max(lossy.x, Mathf.Max(lossy.y, lossy.z));
        }

        public float GetMoonShieldOuterRadiusMultiplierFromDockRadius()
        {
            return Mathf.Max(0.0001f, matrixShieldOrbitZoneEdgeExpandMultiplier) * Mathf.Max(0.0001f, matrixShieldScaleMultiplier);
        }

        /// <summary>Radius multiplier for the *physics* shield barrier (enemy repulsion). Based on dock radius only, not VFX edge expand.</summary>
        public float GetMoonShieldBarrierRadiusMultiplierFromDockRadius()
        {
            // Must be >= 1 so shield trigger stays outside the dock trigger (see Planet.RefreshGemMoonDockTriggerRadius).
            return Mathf.Clamp(shieldBarrierRadiusOverDock, 1f, 1.45f);
        }

        private float GetMoonShieldOuterRadiusLocal()
        {
            if (_shieldTrigger != null)
                return Mathf.Max(0.0001f, _shieldTrigger.radius);
            if (_dockTrigger != null)
                return GetMoonShieldBarrierRadiusLocalFromDockRadius(_dockTrigger.radius);
            return 0.25f;
        }

        private float GetMoonShieldBarrierRadiusLocalFromDockRadius(float dockLocalRadius)
        {
            return Mathf.Max(0.0001f, dockLocalRadius * GetMoonShieldBarrierRadiusMultiplierFromDockRadius());
        }

        /// <summary>
        /// Computes the closest point on the moon body surface to the ship in XZ.
        /// </summary>
        public Vector3 GetShipSurfaceLandingPointWorld(Starship ship)
        {
            if (ship == null) return transform.position;
            if (planet == null) return transform.position;

            Vector3 moonPos = transform.position;
            moonPos.y = 0f;

            float radius = GetMoonBodyRadiusWorld();
            // Closest surface point from current ship approach direction.
            Vector3 shipPos = ship.transform.position;
            shipPos.y = 0f;
            Vector3 dir = ToroidalMap.ToroidalDirection(moonPos, shipPos);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            dir.Normalize();

            // Land on the moon body surface from center->radius (tiny epsilon avoids overlap jitter).
            float landingRadius = radius + Mathf.Max(0.005f, radius * 0.02f);
            Vector3 landing = moonPos + dir * landingRadius;
            landing.y = 0f;
            return landing;
        }

        private static float Hash01(ulong x)
        {
            x = Mix(x);
            // 24-bit mantissa gives good distribution with stable float conversion.
            return (x & 0xFFFFFFUL) / (float)0x1000000UL;
        }

        private static ulong Mix(ulong x)
        {
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33;
            x *= 0xc4ceb9fe1a85ec53UL;
            x ^= x >> 33;
            return x;
        }

        private void OnTriggerStay(Collider other)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (planet == null) return;

            var ship = other.GetComponent<Starship>();
            if (ship == null || ship.IsDead) return;

            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            if (ship.GemMoonDockIgnoreUntilServerTime > now)
            {
                ship.ServerSetGemMoonDocked(false, null);
                return;
            }

            if (TryRepelEnemyShipWithShield(ship))
                return;

            bool isAi = ship.GetComponent<TitanOrbit.AI.AIStarshipController>() != null;
            if (isAi && (!ship.WantToDepositGems || ship.CurrentGems < 0.01f))
            {
                ship.ServerSetGemMoonDocked(false, null);
                return;
            }

            // Moon orbit-zone behavior: once inside moon zone and mostly idle, start landing sequence.
            if (!IsShipReadyToLandInMoonZone(ship))
            {
                ship.ServerSetGemMoonDocked(false, null);
                return;
            }

            ship.ServerSetGemMoonDocked(true, planet);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (planet == null) return;

            var ship = other.GetComponent<Starship>();
            if (ship == null || ship.IsDead) return;

            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            if (ship.GemMoonDockIgnoreUntilServerTime > now)
                return;

            // Repel enemy ships as soon as they enter the shield area, before docking logic can run.
            TryRepelEnemyShipWithShield(ship);
        }

        private void OnTriggerExit(Collider other)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            var ship = other.GetComponent<Starship>();
            if (ship == null) return;

            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            if (ship.GemMoonDockIgnoreUntilServerTime > now)
            {
                // Player/ship explicitly undocking; don't keep it docked.
                ship.ServerSetGemMoonDocked(false, null);
                return;
            }

            // The ship intentionally lifts in Y when it snaps on top of the moon surface.
            // That can momentarily leave the spherical trigger volume, so we keep docked
            // based on XZ proximity to the moon body instead of relying purely on trigger overlap.
            bool isAi = ship.GetComponent<TitanOrbit.AI.AIStarshipController>() != null;
            if (isAi && (!ship.WantToDepositGems || ship.CurrentGems < 0.01f))
            {
                ship.ServerSetGemMoonDocked(false, null);
                return;
            }

            Vector3 moonPos = transform.position;
            moonPos.y = 0f;
            Vector3 shipPos = ship.transform.position;
            shipPos.y = 0f;

            float xzDist = ToroidalMap.ToroidalDistance(shipPos, moonPos);
            float keepDockRadiusWorld = GetMoonDockSnapRadiusWorld() * 1.05f;
            bool shouldStayDocked = keepDockRadiusWorld > 0.0001f && xzDist <= keepDockRadiusWorld;

            ship.ServerSetGemMoonDocked(shouldStayDocked, shouldStayDocked ? planet : null);
        }

        /// <summary>True when <paramref name="team"/> owns this moon's planet (same rule as shield barrier friendlies).</summary>
        public bool IsTeamFriendlyToThisMoon(TeamManager.Team team)
        {
            if (planet == null) return false;

            TeamManager.Team planetTeam = planet.TeamOwnership;
            if (planetTeam == TeamManager.Team.None) return false; // Neutral moons are treated as hostile for the shield barrier.

            if (team == TeamManager.Team.None) return false;

            return team == planetTeam;
        }

        private bool IsShipFriendlyToThisMoon(Starship ship)
        {
            if (ship == null) return false;
            return IsTeamFriendlyToThisMoon(ship.ShipTeam);
        }

        private bool TryRepelEnemyShipWithShield(Starship ship)
        {
            if (ship == null) return false;
            if (shieldPoints <= 0.001f) return false;
            if (IsShipFriendlyToThisMoon(ship)) return false;

            Rigidbody shipRb = ship.GetComponent<Rigidbody>();
            if (shipRb == null) return false;

            float shieldRadiusWorld = GetMoonShieldOuterRadiusWorld();
            if (shieldRadiusWorld <= 0.0001f) return false;

            Vector3 moonPos = transform.position;
            moonPos.y = 0f;
            Vector3 shipPos = ship.transform.position;
            shipPos.y = 0f;

            float dist = ToroidalMap.ToroidalDistance(shipPos, moonPos);
            Vector3 dir = ToroidalMap.ToroidalDirection(moonPos, shipPos);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            dir.Normalize();

            // 0 at the shield edge, 1 at the center.
            float penetration = Mathf.Clamp01(1f - (dist / Mathf.Max(0.0001f, shieldRadiusWorld)));

            float repelSpeed = Mathf.Lerp(enemyShieldRepelMinSpeed, enemyShieldRepelMaxSpeed, penetration);
            Vector3 outwardVel = dir * repelSpeed;
            outwardVel.y = 0f;

            // Prevent accidental dock state while shield is active for non-friendly ships.
            ship.ServerSetGemMoonDocked(false, null);

            shipRb.linearVelocity = outwardVel;
            shipRb.angularVelocity = Vector3.zero;
            return true;
        }

        /// <summary>
        /// AI may clear <see cref="Starship.CurrentOrbitPlanet"/> while flying to the moon; use geometry instead of trigger-only orbit state.
        /// </summary>
        private bool IsShipInThisPlanetsOrbitBand(Starship ship)
        {
            if (planet == null || ship == null) return false;
            Vector3 shipPos = ship.transform.position;
            shipPos.y = 0f;
            Vector3 center = planet.transform.position;
            center.y = 0f;
            float dist = ToroidalMap.ToroidalDistance(shipPos, center);
            float inner = planet.PlanetSize * 0.5f;
            float outer = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal();
            return dist >= inner && dist <= outer;
        }

        private bool IsShipReadyToLandInMoonZone(Starship ship)
        {
            if (ship == null) return false;
            if (planet == null) return false;

            // Players (and AI) can only dock/land on planets that are owned by their own team.
            TeamManager.Team planetTeam = planet.TeamOwnership;
            if (planetTeam == TeamManager.Team.None) return false;

            TeamManager.Team shipTeam = ship.ShipTeam;
            if (shipTeam == TeamManager.Team.None) return false;
            if (shipTeam != planetTeam) return false;

            Vector3 moonPos = transform.position;
            moonPos.y = 0f;
            Vector3 shipPos = ship.transform.position;
            shipPos.y = 0f;

            float dist = ToroidalMap.ToroidalDistance(shipPos, moonPos);
            float zoneRadius = GetMoonDockSnapRadiusWorld();
            if (zoneRadius <= 0.0001f) return false;
            if (dist > zoneRadius) return false;

            Rigidbody shipRb = ship.GetComponent<Rigidbody>();
            if (shipRb == null) return false;

            Vector3 vel = shipRb.linearVelocity;
            vel.y = 0f;
            float speed = vel.magnitude;

            // Mostly stationary — allow light drift so docking is easier to trigger.
            return speed <= 1.85f;
        }
    }
}
