using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Generation;
using TitanOrbit.Systems;
using TitanOrbit.Core;

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

        [Header("Orbit Placement")]
        [Tooltip("How far the moon orbit radius sits outside the planet's orbit zone outer edge (nominal). Actual radius is max of this and clearance past rings + dock zone.")]
        [SerializeField] private float moonOrbitOutsideFactor = 1.1f;
        [Tooltip("Extra world-space gap beyond outer rings/orbit zone and moon dock radius so the moon never overlaps level-up visuals.")]
        [SerializeField] private float moonOrbitRingClearanceMarginWorld = 0.4f;

        [Header("Combat")]
        [SerializeField] private float maxShieldPoints = 250f;
        [SerializeField] private float maxGemPoints = 500f;
        [SerializeField] private float gemDrainPerSecondWhenShieldDown = 20f;
        [SerializeField] private float gemSpawnInterval = 0.25f;
        [SerializeField] private float gemSpawnMinValue = 2f;

        [Header("Landing Scatter")]
        [SerializeField] private float landingHemisphereHemisphereBias = 0.6f; // Higher = more likely to pick the “top/front” hemisphere.

        [Header("Spin")]
        [Tooltip("Visual spin around moon local Y axis (degrees/second).")]
        [SerializeField] private float spinDegreesPerSecond = 8.96f; // ~44% slower than previous 16 deg/s (16 * 0.56)

        private SphereCollider _dockTrigger;
        private SphereCollider _bodyCollider;
        private Rigidbody _rb;
        private Transform _visualTransform;

        private float orbitAngle;
        private float spinAngleDegrees;
        private Vector3 cachedWorldVelocity;

        private float shieldPoints;
        private float gemPoints;
        private float gemDrainAccumulator;
        private float gemSpawnTimer;

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

            // Assign dock trigger vs physics body collider.
            // Some prefabs/scenes can end up with duplicate SphereColliders; keep exactly:
            // - 1 trigger (for docking)
            // - 1 non-trigger (for physical collisions)
            SphereCollider[] cols = GetComponents<SphereCollider>();

            SphereCollider keepTrigger = null;
            SphereCollider keepBody = null;
            float keepTriggerRadius = float.MaxValue;
            float keepBodyRadius = float.MaxValue;
            var toDestroy = new System.Collections.Generic.List<SphereCollider>();

            foreach (var c in cols)
            {
                if (c == null) continue;
                if (c.isTrigger)
                {
                    if (keepTrigger == null || c.radius < keepTriggerRadius)
                    {
                        if (keepTrigger != null) toDestroy.Add(keepTrigger);
                        keepTrigger = c;
                        keepTriggerRadius = c.radius;
                    }
                    else
                    {
                        toDestroy.Add(c);
                    }
                }
                else
                {
                    if (keepBody == null || c.radius < keepBodyRadius)
                    {
                        if (keepBody != null) toDestroy.Add(keepBody);
                        keepBody = c;
                        keepBodyRadius = c.radius;
                    }
                    else
                    {
                        toDestroy.Add(c);
                    }
                }
            }

            for (int i = 0; i < toDestroy.Count; i++)
            {
                if (toDestroy[i] != null)
                    Destroy(toDestroy[i]);
            }

            _dockTrigger = keepTrigger;
            _bodyCollider = keepBody;

            EnsureMoonOrbitZoneVisual();

            // Back-compat: if only one collider exists (from earlier moon versions), create the missing counterpart.
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
            EnsureMoonOrbitZoneVisual();

            ulong id = 0;
            if (planet != null)
            {
                var no = planet.GetComponent<NetworkObject>();
                if (no != null) id = no.NetworkObjectId;
            }
            orbitAngle = (id % 6283UL) * 0.001f;
            spinAngleDegrees = (id % 360UL);

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                shieldPoints = maxShieldPoints;
                gemPoints = maxGemPoints;
                gemDrainAccumulator = 0f;
                gemSpawnTimer = 0f;
            }
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

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            TickMoonCombatDrain();
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

        /// <summary>Server-only: called by bullets/rockets/mines when they hit the moon.</summary>
        public void TakeDamageServer(float damage)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (damage <= 0f) return;

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

        /// <summary>World-space radius of the moon collision body.</summary>
        public float GetMoonBodyRadiusWorld()
        {
            float r = GetMoonBodyRadiusLocal();
            Vector3 lossy = transform.lossyScale;
            return r * Mathf.Max(lossy.x, Mathf.Max(lossy.y, lossy.z));
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
            if (planet == null || planet.TeamOwnership == TeamManager.Team.None) return false;

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
