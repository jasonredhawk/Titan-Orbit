using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;
using System.Collections;
using System.Collections.Generic;

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
        public static readonly List<Gem> AllGems = new List<Gem>();

        [SerializeField] private float gemValue = 10f;
        [SerializeField] private float pickupRadius = 2f;
        [SerializeField] private float stopSpeedThreshold = 0.05f;
        [SerializeField] private float slowdownDrag = 0.5f;
        [SerializeField] private float baseScale = 0.48f; // Used on non-asteroid gem paths; scales with linear value mapping
        [SerializeField] private float visualScaleMultiplier = 2.2f; // Global scale so value-1 gems are visible; value-70 is larger volume
        [SerializeField] private float lifetimeSeconds = 20f; // Time before gem expires and disappears
        [SerializeField] private float shrinkDuration = 3f; // Shrink from full to zero over this many seconds at end of life
        [SerializeField] private float magnetSpeed = 8f; // Speed when moving toward ship
        [SerializeField] private float collectRadius = 0.6f; // Collect when gem is this close to ship
        [Tooltip("Minimum ship hull radius used by center-distance pickup checks when collider bounds are unavailable.")]
        [SerializeField] private float shipProximitySlop = 0.35f;
        [Tooltip("Scales ship collider radius contribution for proximity collection (lower = tighter pickup).")]
        [SerializeField] private float shipProximityRadiusMultiplier = 0.45f;
        [Header("Ship-expelled pickup")]
        [Tooltip("Seconds before the ship that dropped these gems (e.g. hull breakup) can collect them again. Other ships are unaffected.")]
        [SerializeField] private float selfPickupDelaySeconds = 2f;
        [Header("Visuals")]
        [SerializeField] private Color gemTintColor = new Color(1f, 0f, 0f, 0.45f);
        [SerializeField] private Color bonusGemTintColor = new Color(1f, 0.9f, 0.15f, 0.55f);

        private NetworkVariable<float> value = new NetworkVariable<float>(10f);
        private NetworkVariable<float> gemSize = new NetworkVariable<float>(1f); // Size multiplier (affects visual scale and value)
        private NetworkVariable<float> asteroidPhysicalSize = new NetworkVariable<float>(0.5f); // Asteroid scale for "half asteroid" gem size
        private NetworkVariable<float> spawnTime = new NetworkVariable<float>(0f); // Server time when gem was spawned
        private NetworkVariable<ulong> expelledByShipId = new NetworkVariable<ulong>(0); // When non-zero: victim ship cannot collect for this many sec
        private NetworkVariable<ulong> depositTargetPlanetId = new NetworkVariable<ulong>(0); // When non-zero: deposit gem flying toward planet
        private NetworkVariable<int> depositTeam = new NetworkVariable<int>((int)TeamManager.Team.None);
        private NetworkVariable<ulong> depositClientId = new NetworkVariable<ulong>(0);
        private NetworkVariable<ulong> magnetPriorityShipId = new NetworkVariable<ulong>(0); // Ship that dealt most damage to source asteroid
        /// <summary>Server-only pickup gate: used only for hull-expelled gems so the victim ship cannot instantly re-collect.</summary>
        private ulong serverNoImmediatePickupShipId;
        private float serverNoImmediatePickupUntilTime;
        private bool serverInitializedBeforeSpawn;
        private Rigidbody rb;
        private NetworkTransform networkTransform;
        private Renderer gemRenderer;
        private float effectivePickupRadius; // Scaled pickup radius based on gem size
        /// <summary>When true, gem is in pool (disabled); skip logic and do not run attraction.</summary>
        private NetworkVariable<bool> isInPool = new NetworkVariable<bool>(true);
        /// <summary>When true, gem is a "team bonus" gem (spawned from asteroids in triangle territory).</summary>
        private NetworkVariable<bool> isBonusGem = new NetworkVariable<bool>(false);

        public float Value => value.Value;
        public float GemSize => gemSize.Value;
        public bool IsInPool => isInPool.Value;
        public bool IsDepositGem => depositTargetPlanetId.Value != 0;
        // Initialize(...) can run before NetworkObject.Spawn(); in that window IsServer may still be false.
        private bool HasServerAuthority => IsServer || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer);

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            networkTransform = GetComponent<NetworkTransform>();
            effectivePickupRadius = pickupRadius;
            var renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                gemRenderer = renderer;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // Avoid overlapping shadow artifacts when gems cluster
                ApplyGemVisualTint(renderer);
            }
        }

        private void ApplyGemVisualTint(Renderer renderer)
        {
            if (renderer == null) return;

            Material material = renderer.material;
            if (material == null) return;

            // Standard shader style transparency controls.
            if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3f);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            // URP Lit style transparency controls.
            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f); // Transparent
                if (material.HasProperty("_Blend"))
                    material.SetFloat("_Blend", 0f); // Alpha blend
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }

            Color tint = isBonusGem.Value ? bonusGemTintColor : gemTintColor;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", tint);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                // For freshly instantiated networked gems we may initialize before Spawn()
                // to avoid immediate pickup in the spawn frame. Preserve that data if present.
                if (!serverInitializedBeforeSpawn)
                {
                    value.Value = gemValue;
                    spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
                }
                if (rb != null) rb.linearDamping = slowdownDrag;
                // Default to active (not in pool) for normal spawned gems; pooled gems are immediately returned to pool on server, which sets isInPool true.
                isInPool.Value = false;
            }

            if (!AllGems.Contains(this))
                AllGems.Add(this);
            
            gemSize.OnValueChanged += OnGemSizeChanged;
            value.OnValueChanged += OnGemValueChanged;
            asteroidPhysicalSize.OnValueChanged += OnAsteroidPhysicalSizeChanged;
            spawnTime.OnValueChanged += OnSpawnTimeChanged;
            isInPool.OnValueChanged += OnIsInPoolChanged;
            isBonusGem.OnValueChanged += OnIsBonusGemChanged;
            UpdateVisualScale();
            // Ensure correct tint even if value replicated after OnNetworkSpawn.
            if (gemRenderer != null) ApplyGemVisualTint(gemRenderer);
        }

        public override void OnNetworkDespawn()
        {
            AllGems.Remove(this);
            gemSize.OnValueChanged -= OnGemSizeChanged;
            value.OnValueChanged -= OnGemValueChanged;
            asteroidPhysicalSize.OnValueChanged -= OnAsteroidPhysicalSizeChanged;
            spawnTime.OnValueChanged -= OnSpawnTimeChanged;
            isInPool.OnValueChanged -= OnIsInPoolChanged;
            isBonusGem.OnValueChanged -= OnIsBonusGemChanged;
            base.OnNetworkDespawn();
        }

        private void OnIsInPoolChanged(bool previous, bool current)
        {
            gameObject.SetActive(!current);
        }

        private void OnGemSizeChanged(float previousSize, float newSize)
        {
            UpdateVisualScale();
        }

        private void OnGemValueChanged(float previous, float current) => UpdateVisualScale();

        private void OnAsteroidPhysicalSizeChanged(float previous, float current) => UpdateVisualScale();

        private void OnSpawnTimeChanged(float previous, float current) => UpdateVisualScale();

        private void OnIsBonusGemChanged(bool previous, bool current)
        {
            if (gemRenderer != null) ApplyGemVisualTint(gemRenderer);
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
            
            // Visual size is linear in gem value (1–70). Cube-root was legacy “volume ∝ value” and made sizes too subtle.
            float valueClamped = Mathf.Max(1f, value.Value);
            float tValue = Mathf.InverseLerp(1f, 70f, Mathf.Min(valueClamped, 70f));
            float gemSizeMult = gemSize.Value > 0.001f ? gemSize.Value : 1f;

            float scale;
            if (asteroidPhysicalSize.Value > 0.01f)
            {
                // Slightly above 0.85× mesh so gems read clearly; still bounded by asteroid scale.
                float maxScale = asteroidPhysicalSize.Value * 1.05f;
                float minScale = maxScale * 0.52f;
                scale = Mathf.Lerp(minScale, maxScale, tValue);
                scale *= Mathf.Lerp(0.96f, 1.04f, Mathf.InverseLerp(0.25f, 2.2f, gemSizeMult));
                scale = Mathf.Min(scale, maxScale);
                scale *= lifetimeRemaining;
            }
            else
            {
                float baseLinear = Mathf.Lerp(0.74f, 2.2f, tValue);
                scale = baseScale * baseLinear * gemSizeMult * lifetimeRemaining * visualScaleMultiplier;
            }

            transform.localScale = Vector3.one * scale;

            effectivePickupRadius = pickupRadius * Mathf.Lerp(1f, 1.5f, tValue) * lifetimeRemaining;
        }

        public void Initialize(float gemValue, float sizeMultiplier = 1f, float asteroidScale = 0.5f, ulong priorityShipNetworkId = 0, bool bonusGem = false)
        {
            if (HasServerAuthority)
            {
                serverInitializedBeforeSpawn = true;
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = asteroidScale;
                value.Value = gemValue;
                expelledByShipId.Value = 0;
                depositTargetPlanetId.Value = 0;
                magnetPriorityShipId.Value = priorityShipNetworkId;
                isBonusGem.Value = bonusGem;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
                // Asteroid gems are immediately collectible; only hull-expelled gems are gated.
                ClearServerPickupGate();
            }
            UpdateVisualScale();
        }

        /// <summary>Initialize gem expelled from a ship. Victim (expelledByShipNetworkId) cannot collect until <see cref="selfPickupDelaySeconds"/> elapses; enemies can collect immediately.</summary>
        public void InitializeFromShip(float gemValue, float sizeMultiplier, ulong expelledByShipNetworkId)
        {
            if (HasServerAuthority)
            {
                serverInitializedBeforeSpawn = true;
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = 0.5f; // Default for ship gems
                value.Value = gemValue;
                expelledByShipId.Value = expelledByShipNetworkId;
                depositTargetPlanetId.Value = 0;
                isBonusGem.Value = false;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
                float t = (float)NetworkManager.Singleton.ServerTime.Time;
                if (expelledByShipNetworkId != 0)
                {
                    serverNoImmediatePickupShipId = expelledByShipNetworkId;
                    serverNoImmediatePickupUntilTime = t + Mathf.Max(0f, selfPickupDelaySeconds);
                }
                else
                    ClearServerPickupGate();
            }
            UpdateVisualScale();
        }

        /// <summary>Initialize gem for deposit: expelled from ship toward planet, absorbed on contact. sizeMultiplier scales with ship level.</summary>
        public void InitializeForDeposit(float amount, float sizeMultiplier, ulong targetPlanetNetworkObjectId, TeamManager.Team team, ulong clientId)
        {
            if (HasServerAuthority)
            {
                serverInitializedBeforeSpawn = true;
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = 0.85f * sizeMultiplier; // Scale with ship level
                value.Value = amount;
                expelledByShipId.Value = 0;
                depositTargetPlanetId.Value = targetPlanetNetworkObjectId;
                depositTeam.Value = (int)team;
                depositClientId.Value = clientId;
                isBonusGem.Value = false;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
                ClearServerPickupGate();
                if (rb != null) rb.linearDamping = 0f; // No slowdown - fly straight to planet
            }
            UpdateVisualScale();
        }

        /// <summary>Server only. Puts gem back in pool (recycled); no Despawn. Call from pool or when gem is collected/expired.</summary>
        public void ServerReturnToPool()
        {
            if (!IsServer) return;
            StopCoroutineSafe_ServerReapplyExplosionVelocity();
            value.Value = 0f;
            depositTargetPlanetId.Value = 0;
            magnetPriorityShipId.Value = 0;
            expelledByShipId.Value = 0;
            isBonusGem.Value = false;
            ClearServerPickupGate();
            transform.position = Vector3.zero;
            if (rb != null)
            {
                rb.position = Vector3.zero;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            isInPool.Value = true;
        }

        /// <summary>Server only. Marks gem as active (taken from pool). Call after setting position and Initialize.</summary>
        public void ServerActivateFromPool()
        {
            if (IsServer) isInPool.Value = false;
        }

        /// <summary>
        /// Server only. After <see cref="ServerActivateFromPool"/>, snap <see cref="NetworkTransform"/> internal state
        /// (pooled gems were at origin) and apply explosion velocity.
        /// Order matters: moving the transform via Netcode can sync after physics and clear rigidbody velocity in the same frame,
        /// so we re-apply velocity and once more after the next physics step.
        /// </summary>
        /// <param name="linearDamping">Pass null for default slowdown drag; deposit gems use 0.</param>
        public void ServerFinishPooledSpawn(Vector3 worldPosition, Vector3 linearVelocity, Vector3 angularVelocity, float? linearDamping = null)
        {
            if (!IsServer) return;

            Quaternion rot = transform.rotation;
            Vector3 scale = transform.localScale;
            float damp = linearDamping ?? slowdownDrag;

            if (rb != null)
            {
                // Server must simulate; proxies stay kinematic via NetworkRigidbody.
                rb.isKinematic = false;
                rb.position = worldPosition;
                rb.linearVelocity = linearVelocity;
                rb.angularVelocity = angularVelocity;
                rb.linearDamping = damp;
                rb.WakeUp();
            }
            else
            {
                transform.SetPositionAndRotation(worldPosition, rot);
            }

            if (networkTransform != null)
                networkTransform.SetState(worldPosition, rot, scale, teleportDisabled: false);

            if (rb != null)
            {
                rb.position = worldPosition;
                rb.linearVelocity = linearVelocity;
                rb.angularVelocity = angularVelocity;
                rb.linearDamping = damp;
                rb.WakeUp();
            }

            StopCoroutineSafe_ServerReapplyExplosionVelocity();
            _serverReapplyExplosionVelocityRoutine = StartCoroutine(ServerReapplyExplosionVelocityAfterPhysicsSync(linearVelocity, angularVelocity, damp));
        }

        private Coroutine _serverReapplyExplosionVelocityRoutine;

        private void StopCoroutineSafe_ServerReapplyExplosionVelocity()
        {
            if (_serverReapplyExplosionVelocityRoutine != null)
            {
                StopCoroutine(_serverReapplyExplosionVelocityRoutine);
                _serverReapplyExplosionVelocityRoutine = null;
            }
        }

        private IEnumerator ServerReapplyExplosionVelocityAfterPhysicsSync(Vector3 linearVelocity, Vector3 angularVelocity, float damp)
        {
            yield return new WaitForFixedUpdate();
            _serverReapplyExplosionVelocityRoutine = null;
            if (!IsServer || rb == null || isInPool.Value) yield break;
            rb.isKinematic = false;
            rb.linearVelocity = linearVelocity;
            rb.angularVelocity = angularVelocity;
            rb.linearDamping = damp;
            rb.WakeUp();
        }

        private void ClearServerPickupGate()
        {
            serverNoImmediatePickupShipId = 0;
            serverNoImmediatePickupUntilTime = 0f;
        }

        private void OnTriggerEnter(Collider other) => TryHandlePickupTrigger(other);

        /// <summary>
        /// Gems that spawn already overlapping the ship often never get OnTriggerEnter; Stay keeps collection working while overlapped.
        /// </summary>
        private void OnTriggerStay(Collider other) => TryHandlePickupTrigger(other);

        private void TryHandlePickupTrigger(Collider other)
        {
            if (!IsServer) return;
            if (value.Value <= 0) return;
            
            // Deposit gems: handle planet collision
            if (depositTargetPlanetId.Value != 0)
            {
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
                Vector3 depositPopupPos = rb != null ? rb.position : transform.position;
                depositPopupPos.y = 0f;

                if (planet is HomePlanet homePlanet)
                {
                    homePlanet.DepositGemsFromServer(amount, team, clientId, depositPopupPos);
                }
                else
                {
                    planet.DepositGemsFromServer(amount, team, clientId, depositPopupPos);
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
                if (GemPool.Instance != null && GemPool.Instance.ReturnToPool(this))
                    return;
                var no = GetComponent<NetworkObject>();
                if (no != null) no.Despawn();
                return;
            }

            // Non-deposit gems: collect when colliding with a ship
            Starship ship = other.GetComponent<Starship>();
            if (ship == null) return;

            if (IsShipTemporarilyBlockedFromPickup(ship))
                return;

            CollectToShip(ship);
        }

        /// <summary>Server: blocks only the victim ship for hull-expelled gem cooldown (authoritative fields, not NetworkVariables).</summary>
        private bool IsShipTemporarilyBlockedFromPickup(Starship ship)
        {
            if (serverNoImmediatePickupShipId == 0) return false;
            var shipNo = ship.NetworkObject;
            if (shipNo == null || shipNo.NetworkObjectId != serverNoImmediatePickupShipId) return false;
            return (float)NetworkManager.Singleton.ServerTime.Time < serverNoImmediatePickupUntilTime;
        }

        /// <summary>
        /// Fallback when trigger enter/stay never run (e.g. spawned deep inside hull, or trigger pairs not registered yet).
        /// </summary>
        private void TryProximityCollectShip()
        {
            if (value.Value <= 0f) return;
            if (depositTargetPlanetId.Value != 0) return;

            Vector3 gemPos = rb != null ? rb.position : transform.position;
            gemPos.y = 0f;

            foreach (var ship in GetCachedShipsForServer())
            {
                if (ship == null || ship.IsDead) continue;
                if (ship.IsGemCollectionSuppressed) continue;
                Vector3 shipPos = ship.transform.position;
                var srb = ship.GetComponent<Rigidbody>();
                if (srb != null) shipPos = srb.position;
                shipPos.y = 0f;

                float maxDist = GetShipProximityCollectDistance(ship);
                if (ToroidalMap.ToroidalDistance(gemPos, shipPos) > maxDist)
                    continue;

                if (IsShipTemporarilyBlockedFromPickup(ship))
                    continue;

                CollectToShip(ship);
                return;
            }
        }

        /// <summary>
        /// Uses the ship's current collider footprint to allow reliable "within range" pickup,
        /// including gems that spawn already inside the hull where trigger enter can be missed.
        /// </summary>
        private float GetShipProximityCollectDistance(Starship ship)
        {
            float hullRadius = shipProximitySlop;
            Collider shipCollider = ship.GetComponent<Collider>();
            if (shipCollider != null && shipCollider.enabled)
            {
                Vector3 e = shipCollider.bounds.extents;
                float colliderRadius = Mathf.Sqrt(e.x * e.x + e.z * e.z);
                if (colliderRadius > 0.01f)
                    hullRadius = Mathf.Max(hullRadius, colliderRadius * shipProximityRadiusMultiplier);
            }

            return collectRadius + hullRadius;
        }

        private void CollectToShip(Starship ship)
        {
            if (!IsServer || ship == null) return;
            if (value.Value <= 0f) return;
            if (ship.IsDead || ship.IsGemCollectionSuppressed || ship.CurrentGems >= ship.GemCapacity) return;

            Vector3 gemPos = rb != null ? rb.position : transform.position;

            float capacityLeft = Mathf.Max(0f, ship.GemCapacity - ship.CurrentGems);
            if (capacityLeft <= 0f)
                return;

            float toAdd = Mathf.Min(value.Value, capacityLeft);
            if (toAdd <= 0f)
                return;

            ship.AddGemsFromPickupServer(toAdd, true);

            if (ScoreSystem.Instance != null)
                ScoreSystem.Instance.AwardMining(ship, toAdd);

            if (VisualEffectsManager.Instance != null)
                VisualEffectsManager.Instance.SpawnGemPickupTextServerRpc(gemPos, toAdd, ship.ShipTeam);

            value.Value = 0f;
            if (GemPool.Instance != null && GemPool.Instance.ReturnToPool(this))
                return;
            var no = GetComponent<NetworkObject>();
            if (no != null) no.Despawn();
        }

        private static HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var hp in HomePlanet.AllHomePlanets)
            {
                if (hp != null && hp.AssignedTeam == team) return hp;
            }
            return null;
        }

        private static Starship FindDepositorShip(ulong clientId)
        {
            foreach (var ship in Starship.AllStarships)
            {
                if (ship != null && ship.OwnerClientId == clientId) return ship;
            }
            return null;
        }

        private void FixedUpdate()
        {
            // Throttle visual scale updates: every 5th FixedUpdate (staggered by instance) to cut cost when many gems exist
            int frameMod = (Time.frameCount + GetInstanceID()) % 5;
            if (frameMod == 0)
                UpdateVisualScale();

            if (isInPool.Value) return;

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
                    if (GemPool.Instance != null && GemPool.Instance.ReturnToPool(this))
                        return;
                    var no = GetComponent<NetworkObject>();
                    if (no != null) no.Despawn();
                }
                return;
            }

            // Check if gem has expired
            if (elapsedTime >= lifetimeSeconds)
            {
                if (GemPool.Instance != null && GemPool.Instance.ReturnToPool(this))
                    return;
                var no = GetComponent<NetworkObject>();
                if (no != null) no.Despawn();
                return;
            }
            
            // No automatic ship attraction: just apply drag so gems slow down and stop.
            if (rb != null)
            {
                rb.linearDamping = slowdownDrag;
                if (rb.linearVelocity.magnitude < stopSpeedThreshold)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.linearDamping = 0f;
                }
            }

            TryProximityCollectShip();
        }

        private static Starship[] GetCachedShipsForServer()
        {
            if (Time.unscaledTime >= nextShipCacheRefreshTime || cachedShips == null)
            {
                var allShips = Starship.AllStarships;
                if (allShips == null || allShips.Count == 0)
                {
                    cachedShips = System.Array.Empty<Starship>();
                }
                else
                {
                    int count = allShips.Count;
                    if (cachedShips == null || cachedShips.Length != count)
                        cachedShips = new Starship[count];
                    allShips.CopyTo(cachedShips);
                }
                nextShipCacheRefreshTime = Time.unscaledTime + SHIP_CACHE_REFRESH_INTERVAL;
            }
            return cachedShips ?? System.Array.Empty<Starship>();
        }
    }
}
