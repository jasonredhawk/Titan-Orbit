using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Gem pickup - spawned when asteroid is destroyed, explodes outward then stops. Collected by flying over.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Gem : NetworkBehaviour
    {
        private static Starship[] cachedShips = new Starship[0];
        private static float nextShipCacheRefreshTime = 0f;
        private const float SHIP_CACHE_REFRESH_INTERVAL = 0.5f; // Was 0.25f - reduce FindObjectsByType cost

        [SerializeField] private float gemValue = 10f;
        [SerializeField] private float pickupRadius = 2f;
        [SerializeField] private float stopSpeedThreshold = 0.05f;
        [SerializeField] private float slowdownDrag = 0.5f;
        [SerializeField] private float baseScale = 0.48f; // Base visual scale; final scale = baseScale * value^(1/3) * ...
        [SerializeField] private float visualScaleMultiplier = 2.2f; // Global scale so value-1 gems are visible; value-70 is larger volume
        [SerializeField] private float lifetimeSeconds = 20f; // Time before gem expires and disappears
        [SerializeField] private float shrinkDuration = 3f; // Shrink from full to zero over this many seconds at end of life
        [SerializeField] private float magnetSpeed = 8f; // Speed when moving toward ship
        [SerializeField] private float collectRadius = 0.6f; // Collect when gem is this close to ship

        private NetworkVariable<float> value = new NetworkVariable<float>(10f);
        private NetworkVariable<float> gemSize = new NetworkVariable<float>(1f); // Size multiplier (affects visual scale and value)
        private NetworkVariable<float> asteroidPhysicalSize = new NetworkVariable<float>(0.5f); // Asteroid scale for "half asteroid" gem size
        private NetworkVariable<float> spawnTime = new NetworkVariable<float>(0f); // Server time when gem was spawned
        private NetworkVariable<ulong> expelledByShipId = new NetworkVariable<ulong>(0); // When non-zero: victim ship cannot collect for 3 sec
        private NetworkVariable<ulong> depositTargetPlanetId = new NetworkVariable<ulong>(0); // When non-zero: deposit gem flying toward planet
        private NetworkVariable<int> depositTeam = new NetworkVariable<int>((int)TeamManager.Team.None);
        private NetworkVariable<ulong> depositClientId = new NetworkVariable<ulong>(0);
        private NetworkVariable<ulong> magnetPriorityShipId = new NetworkVariable<ulong>(0); // Ship that dealt most damage to source asteroid
        private const float EXPELLED_UNCOLLECTABLE_DURATION = 3f;
        private Rigidbody rb;
        private float effectivePickupRadius; // Scaled pickup radius based on gem size

        public float Value => value.Value;
        public float GemSize => gemSize.Value;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            effectivePickupRadius = pickupRadius;
            var renderer = GetComponent<Renderer>();
            if (renderer != null)
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // Avoid overlapping shadow artifacts when gems cluster
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                value.Value = gemValue;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
                if (rb != null) rb.linearDamping = slowdownDrag;
            }
            
            // Update visual scale based on gem size (client-side)
            gemSize.OnValueChanged += OnGemSizeChanged;
            UpdateVisualScale();
        }

        public override void OnNetworkDespawn()
        {
            gemSize.OnValueChanged -= OnGemSizeChanged;
            base.OnNetworkDespawn();
        }

        private void OnGemSizeChanged(float previousSize, float newSize)
        {
            UpdateVisualScale();
        }

        private void UpdateVisualScale()
        {
            // Shrink only in the last shrinkDuration seconds (e.g. 3 sec)
            float lifetimeRemaining = 1f;
            if (NetworkManager.Singleton != null)
            {
                float elapsedTime = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
                if (elapsedTime >= lifetimeSeconds - shrinkDuration)
                    lifetimeRemaining = Mathf.Clamp01((lifetimeSeconds - elapsedTime) / shrinkDuration);
            }
            
            // Scale by value^(1/3) so volume ∝ value (1-70)
            float valueScale = Mathf.Pow(Mathf.Max(1f, value.Value), 1f / 3f);
            float scale = baseScale * valueScale * asteroidPhysicalSize.Value * lifetimeRemaining * visualScaleMultiplier;
            // Cap so gem is never bigger than the asteroid it came from
            if (asteroidPhysicalSize.Value > 0.01f)
                scale = Mathf.Min(scale, asteroidPhysicalSize.Value * 0.85f);
            transform.localScale = Vector3.one * scale;
            
            // Pickup radius scales with value^(1/3) so bigger gems are easier to collect
            effectivePickupRadius = pickupRadius * valueScale * lifetimeRemaining;
        }

        public void Initialize(float gemValue, float sizeMultiplier = 1f, float asteroidScale = 0.5f, ulong priorityShipNetworkId = 0)
        {
            if (IsServer)
            {
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = asteroidScale;
                value.Value = gemValue;
                expelledByShipId.Value = 0;
                depositTargetPlanetId.Value = 0;
                magnetPriorityShipId.Value = priorityShipNetworkId;
            }
        }

        /// <summary>Initialize gem expelled from a ship. Victim (expelledByShipNetworkId) cannot collect for 3 sec; enemies can collect immediately.</summary>
        public void InitializeFromShip(float gemValue, float sizeMultiplier, ulong expelledByShipNetworkId)
        {
            if (IsServer)
            {
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = 0.5f; // Default for ship gems
                value.Value = gemValue;
                expelledByShipId.Value = expelledByShipNetworkId;
                depositTargetPlanetId.Value = 0;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
            }
        }

        /// <summary>Initialize gem for deposit: expelled from ship toward planet, absorbed on contact. sizeMultiplier scales with ship level.</summary>
        public void InitializeForDeposit(float amount, float sizeMultiplier, ulong targetPlanetNetworkObjectId, TeamManager.Team team, ulong clientId)
        {
            if (IsServer)
            {
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = 0.85f * sizeMultiplier; // Scale with ship level
                value.Value = amount;
                expelledByShipId.Value = 0;
                depositTargetPlanetId.Value = targetPlanetNetworkObjectId;
                depositTeam.Value = (int)team;
                depositClientId.Value = clientId;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
                if (rb != null) rb.linearDamping = 0f; // No slowdown - fly straight to planet
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;
            if (value.Value <= 0) return;
            if (depositTargetPlanetId.Value == 0) return;

            // Only absorb when hitting the planet body, not the orbit zone (so gem can travel through orbit zone first)
            if (other.GetComponent<PlanetOrbitZone>() != null || other.GetComponent<HomePlanetOrbitZone>() != null)
                return;

            Planet planet = other.GetComponent<Planet>();
            if (planet == null) return;
            var planetNo = planet.GetComponent<NetworkObject>();
            if (planetNo == null || planetNo.NetworkObjectId != depositTargetPlanetId.Value) return;

            float amount = value.Value;
            var team = (TeamManager.Team)depositTeam.Value;
            ulong clientId = depositClientId.Value;

            if (planet is HomePlanet homePlanet)
            {
                homePlanet.DepositGemsFromServer(amount, team, clientId);
            }
            else
            {
                planet.DepositGemsFromServer(amount, team, clientId);
                HomePlanet shipHome = GetHomePlanetForTeam(team);
                if (shipHome != null)
                    shipHome.AddContributedGemsFromServer(clientId, amount);
            }

            if (ScoreSystem.Instance != null)
            {
                Starship depositor = FindDepositorShip(clientId);
                if (depositor != null)
                    ScoreSystem.Instance.AwardDeposit(depositor, amount);
            }

            value.Value = 0;
            var no = GetComponent<NetworkObject>();
            if (no != null) no.Despawn();
        }

        private static HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var hp in Object.FindObjectsByType<HomePlanet>(FindObjectsSortMode.None))
            {
                if (hp.AssignedTeam == team) return hp;
            }
            return null;
        }

        private static Starship FindDepositorShip(ulong clientId)
        {
            foreach (var ship in Object.FindObjectsByType<Starship>(FindObjectsSortMode.None))
            {
                if (ship.OwnerClientId == clientId) return ship;
            }
            return null;
        }

        private void FixedUpdate()
        {
            // Update visual scale on all clients (for shrinking effect)
            UpdateVisualScale();

            // Never wrap gem position: world position can grow (e.g. 100, 310). ToroidalRenderer
            // displays at the copy closest to the local player's camera for a seamless view.

            if (!IsServer) return;
            if (value.Value <= 0) return;

            float elapsedTime = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;

            // Deposit gems: skip ship attraction, only check expiry (planet contact handled in OnTriggerEnter)
            if (depositTargetPlanetId.Value != 0)
            {
                if (elapsedTime >= lifetimeSeconds)
                {
                    var no = GetComponent<NetworkObject>();
                    if (no != null) no.Despawn();
                }
                return;
            }

            // Check if gem has expired
            if (elapsedTime >= lifetimeSeconds)
            {
                // Gem expired - despawn it
                var no = GetComponent<NetworkObject>();
                if (no != null) no.Despawn();
                return;
            }

            // Run attraction (find nearest ship + magnetic pull) every 2nd FixedUpdate to halve CPU cost; stagger by instance so not all on same frame
            bool runAttractionThisFrame = ((Time.frameCount + GetInstanceID()) & 1) == 0;
            if (!runAttractionThisFrame)
            {
                // Still apply drag so gems slow down when no ship nearby (no ship search this frame)
                if (rb != null)
                {
                    rb.linearDamping = slowdownDrag;
                    if (rb.linearVelocity.magnitude < stopSpeedThreshold)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.linearDamping = 0f;
                    }
                }
                return;
            }

            // Pickup radius and lifetime shrink (same as visual: full until last shrinkDuration sec)
            float lifetimeRemaining = 1f;
            if (elapsedTime >= lifetimeSeconds - shrinkDuration)
                lifetimeRemaining = Mathf.Clamp01((lifetimeSeconds - elapsedTime) / shrinkDuration);
            float currentPickupRadius = effectivePickupRadius;

            // Find nearest valid ship in range using toroidal distance (so pickup works across map edges)
            float elapsed = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
            ulong expelledId = expelledByShipId.Value;
            Vector3 gemPos = rb != null ? rb.position : transform.position;
            Starship nearestShip = null;
            float nearestDist = float.MaxValue;
            bool nearestIsPriority = false;
            ulong priorityShipId = magnetPriorityShipId.Value;
            Starship[] ships = GetCachedShipsForServer();
            foreach (Starship ship in ships)
            {
                if (ship.IsDead || ship.CurrentGems >= ship.GemCapacity) continue;
                if (expelledId != 0)
                {
                    var shipNo = ship.GetComponent<NetworkObject>();
                    if (shipNo != null && shipNo.NetworkObjectId == expelledId && elapsed < EXPELLED_UNCOLLECTABLE_DURATION)
                        continue; // Victim cannot collect yet
                }
                float dist = ToroidalMap.ToroidalDistance(gemPos, ship.transform.position);

                // Base magnet pickup range; doubled if this ship dealt the most damage to the source asteroid.
                float range = currentPickupRadius;
                bool isPriority = false;
                if (priorityShipId != 0)
                {
                    var shipNo = ship.GetComponent<NetworkObject>();
                    if (shipNo != null && shipNo.NetworkObjectId == priorityShipId)
                    {
                        range *= 2f;
                        isPriority = true;
                    }
                }

                if (dist < range && dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestShip = ship;
                    nearestIsPriority = isPriority;
                }
            }

            if (nearestShip != null)
            {
                // Toroidal direction for magnetic pull (correct across wrap)
                Vector3 toShip = ToroidalMap.ToroidalDirection(gemPos, nearestShip.transform.position);
                toShip.y = 0f;
                if (toShip.sqrMagnitude < 0.0001f) toShip = Vector3.forward;
                else toShip.Normalize();
                float dist = nearestDist;

                // Collect when very close (toroidal distance)
                if (dist <= collectRadius)
                {
                    float capacityLeft = Mathf.Max(0f, nearestShip.GemCapacity - nearestShip.CurrentGems);
                    if (capacityLeft <= 0f)
                        return;

                    // Ship only gains up to its remaining capacity, but the entire gem is consumed.
                    float toAdd = Mathf.Min(value.Value, capacityLeft);
                    if (toAdd <= 0f)
                        return;

                    nearestShip.AddGemsServerRpc(toAdd, true);

                    if (ScoreSystem.Instance != null)
                        ScoreSystem.Instance.AwardMining(nearestShip, toAdd);

                    if (VisualEffectsManager.Instance != null)
                        VisualEffectsManager.Instance.SpawnGemPickupTextServerRpc(gemPos, toAdd, nearestShip.ShipTeam);

                    // Consume the whole gem regardless of how much was added to the ship.
                    value.Value = 0f;
                    var no = GetComponent<NetworkObject>();
                    if (no != null) no.Despawn();
                    return;
                }

                // Magnetic pull toward ship (XZ only, toroidal direction)
                if (rb != null)
                {
                    float magnetMultiplier = nearestIsPriority ? 2f : 1f;
                    float speed = magnetSpeed * magnetMultiplier;
                    Vector3 targetVel = toShip * speed;
                    rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, targetVel, speed * Time.fixedDeltaTime * 4f);
                    rb.linearDamping = 0f;
                }
                return;
            }

            // No ship in range: apply drag so gem slows and stops
            if (rb != null)
            {
                rb.linearDamping = slowdownDrag;
                if (rb.linearVelocity.magnitude < stopSpeedThreshold)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.linearDamping = 0f;
                }
            }

        }

        private static Starship[] GetCachedShipsForServer()
        {
            if (Time.unscaledTime >= nextShipCacheRefreshTime || cachedShips == null)
            {
                cachedShips = FindObjectsByType<Starship>(FindObjectsSortMode.None);
                nextShipCacheRefreshTime = Time.unscaledTime + SHIP_CACHE_REFRESH_INTERVAL;
            }
            return cachedShips ?? System.Array.Empty<Starship>();
        }
    }
}
