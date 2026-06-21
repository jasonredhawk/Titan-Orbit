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
        [Header("Visual scale")]
        [Tooltip("Game value mapped to the smallest visual size. Matches Asteroid.MAX_GEM_VALUE lower bound.")]
        [SerializeField] private float minGemValue = 1f;
        [Tooltip("Game value mapped to the largest visual size. Matches Asteroid.MAX_GEM_VALUE upper bound.")]
        [SerializeField] private float maxGemValue = 100f;
        [Tooltip("World localScale used when value <= minGemValue.")]
        [SerializeField] private float scaleAtMinValue = 1f;
        [Tooltip("World localScale used when value >= maxGemValue.")]
        [SerializeField] private float scaleAtMaxValue = 3.5f;
        [Tooltip("Pickup radius multiplier at minGemValue.")]
        [SerializeField] private float pickupRadiusMinMul = 1f;
        [Tooltip("Pickup radius multiplier at maxGemValue.")]
        [SerializeField] private float pickupRadiusMaxMul = 2f;
        [SerializeField] private float lifetimeSeconds = 20f; // Time before gem expires and disappears
        [SerializeField] private float shrinkDuration = 3f; // Shrink from full to zero over this many seconds at end of life
        [SerializeField] private float magnetSpeed = 8f; // Speed when moving toward ship
        [SerializeField] private float collectRadius = 0.6f; // Collect when gem is this close to ship
        [Tooltip("After tractor pickup credits gems, the gem glides to the collecting wing and despawn when within this distance.")]
        [SerializeField] private float tractorAbsorbCompleteRadius = 0.12f;
        [Tooltip("Minimum ship hull radius used by center-distance pickup checks when collider bounds are unavailable.")]
        [SerializeField] private float shipProximitySlop = 0.35f;
        [Tooltip("Scales ship collider radius contribution for proximity collection (lower = tighter pickup).")]
        [SerializeField] private float shipProximityRadiusMultiplier = 0.45f;
        [Header("Pickup priority")]
        [Tooltip("Asteroid gems: non-top-damager ships must wait this long before pickup (top damager is immediate).")]
        [SerializeField] private float asteroidNonPriorityPickupDelaySeconds = 1f;
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
        /// <summary>Server-only: gems credited; gem keeps gliding to the collecting wing before despawn (tractor beam pickup).</summary>
        private ulong serverAbsorbTargetShipId;
        private int serverAbsorbTargetWingIndex = -1;
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
        public bool IsBonusGem => isBonusGem.Value;
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
            expelledByShipId.OnValueChanged += OnExpelledByShipIdChanged;
            UpdateVisualScale();
            // Ensure correct tint even if value replicated after OnNetworkSpawn.
            if (gemRenderer != null) ApplyGemVisualTint(gemRenderer);
            if (!IsServer)
                TryRepositionOwnerExpelledGem();
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
            expelledByShipId.OnValueChanged -= OnExpelledByShipIdChanged;
            base.OnNetworkDespawn();
        }

        private void OnIsInPoolChanged(bool previous, bool current)
        {
            gameObject.SetActive(!current);
        }

        /// <summary>
        /// Owner client: gems expelled from the local ship spawn at server pose; nudge to predicted ship center
        /// while preserving scatter direction so ram/grind expulsion stays visually on the hull.
        /// </summary>
        private void OnExpelledByShipIdChanged(ulong previous, ulong current)
        {
            TryRepositionOwnerExpelledGem();
        }

        private void TryRepositionOwnerExpelledGem()
        {
            if (IsServer || expelledByShipId.Value == 0) return;

            ulong localShipId = ClientBulletTracer.GetLocalPlayerOwnedShipNetworkObjectId();
            if (expelledByShipId.Value != localShipId) return;

            NetworkObject localPlayer = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if (localPlayer == null) return;
            Starship ship = localPlayer.GetComponent<Starship>();
            if (ship == null) return;

            Vector3 shipCenter = ship.GetGameplayShipCenterWorld();
            Vector3 toGem = transform.position - shipCenter;
            toGem.y = 0f;
            float dist = toGem.magnitude;
            const float maxRepositionRadius = 4f;
            if (dist > maxRepositionRadius) return;

            Vector3 newPos = dist > 0.001f ? shipCenter + toGem : shipCenter;
            transform.position = newPos;
            if (rb != null)
                rb.position = newPos;
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

            float tValue = ComputeValueT(value.Value);
            float scale = Mathf.Lerp(scaleAtMinValue, scaleAtMaxValue, tValue) * lifetimeRemaining;

            transform.localScale = Vector3.one * scale;

            effectivePickupRadius = pickupRadius * Mathf.Lerp(pickupRadiusMinMul, pickupRadiusMaxMul, tValue) * lifetimeRemaining;
        }

        /// <summary>Linear 0..1 ramp from <see cref="minGemValue"/> to <see cref="maxGemValue"/>; clamps outside the range so deposits >100 still hit max.</summary>
        private float ComputeValueT(float gemValue)
        {
            float lo = Mathf.Min(minGemValue, maxGemValue);
            float hi = Mathf.Max(minGemValue, maxGemValue);
            if (hi - lo <= 0.0001f) return 0f;
            return Mathf.InverseLerp(lo, hi, Mathf.Clamp(gemValue, lo, hi));
        }

        /// <summary>0 = smallest value gem, 1 = largest value gem (matches visual scale). Used by tractor beam pull speed.</summary>
        public float GetValueSizeT() => ComputeValueT(value.Value);

        public void Initialize(float gemValue, float sizeMultiplier = 1f, float asteroidScale = 0.5f, ulong priorityShipNetworkId = 0, bool bonusGem = false)
        {
            if (HasServerAuthority)
            {
                serverInitializedBeforeSpawn = true;
                // sizeMultiplier / asteroidScale are kept for API compatibility but no longer drive visual scale;
                // value alone maps to size via ComputeValueT. Neutral values keep replicated state clean.
                gemSize.Value = 1f;
                asteroidPhysicalSize.Value = 0f;
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

        /// <summary>Initialize gem expelled from a ship. Victim cannot collect until <see cref="selfPickupDelaySeconds"/>; other ships may collect immediately.</summary>
        public void InitializeFromShip(float gemValue, float sizeMultiplier, ulong expelledByShipNetworkId)
        {
            if (HasServerAuthority)
            {
                serverInitializedBeforeSpawn = true;
                gemSize.Value = 1f;
                asteroidPhysicalSize.Value = 0f;
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

        /// <summary>Initialize gem for deposit: expelled from ship toward planet, absorbed on contact. Visual size derives from <paramref name="amount"/>.</summary>
        public void InitializeForDeposit(float amount, float sizeMultiplier, ulong targetPlanetNetworkObjectId, TeamManager.Team team, ulong clientId)
        {
            if (HasServerAuthority)
            {
                serverInitializedBeforeSpawn = true;
                gemSize.Value = 1f;
                asteroidPhysicalSize.Value = 0f;
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

        /// <summary>Server: impulse from bullet concussive / gravity effects (gems are always pushed, not team-filtered).</summary>
        public void ApplyBulletKnockbackOnServer(Vector3 impactWorldPos, float force, bool pull)
        {
            if (!HasServerAuthority || IsInPool || rb == null || force <= 0f) return;
            Vector3 dir = rb.position - impactWorldPos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            dir.Normalize();
            if (!pull)
                dir = -dir;
            rb.AddForce(dir * force, ForceMode.Impulse);
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
            ClearServerAbsorbState();
        }

        private void ClearServerAbsorbState()
        {
            serverAbsorbTargetShipId = 0;
            serverAbsorbTargetWingIndex = -1;
        }

        private static Starship FindShipByNetworkObjectId(ulong networkObjectId)
        {
            if (networkObjectId == 0) return null;
            foreach (var ship in Starship.AllStarships)
            {
                if (ship == null) continue;
                var shipNo = ship.NetworkObject;
                if (shipNo != null && shipNo.NetworkObjectId == networkObjectId)
                    return ship;
            }
            return null;
        }

        private void DespawnCollectedGem()
        {
            value.Value = 0f;
            ClearServerAbsorbState();
            if (GemPool.Instance != null && GemPool.Instance.ReturnToPool(this))
                return;
            var no = GetComponent<NetworkObject>();
            if (no != null) no.Despawn();
        }

        /// <summary>Server: after tractor pickup credits gems, glide toward the collecting wing then despawn.</summary>
        private void ServerTickTractorAbsorbGlide()
        {
            Starship ship = FindShipByNetworkObjectId(serverAbsorbTargetShipId);
            if (ship == null || !ship.IsSpawned || ship.IsDead)
            {
                DespawnCollectedGem();
                return;
            }

            Vector3 gemPos = rb != null ? rb.position : transform.position;
            gemPos.y = 0f;
            Vector3 targetPos = GetAbsorbTargetWorldPosition(ship);

            float dist = ToroidalMap.ToroidalDistance(gemPos, targetPos);
            if (dist <= tractorAbsorbCompleteRadius)
            {
                DespawnCollectedGem();
                return;
            }

            if (rb == null)
                return;

            Vector3 toTarget = ToroidalMap.ToroidalDirection(gemPos, targetPos);
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                DespawnCollectedGem();
                return;
            }

            float pullSpeed = GemTractorBeamSettings.GetGameplayPullSpeed(ship, this);
            rb.linearVelocity = toTarget.normalized * pullSpeed;
            rb.linearDamping = 0f;
        }

        private Vector3 GetAbsorbTargetWorldPosition(Starship ship)
        {
            if (serverAbsorbTargetWingIndex >= 0)
                return GemTractorBeamSettings.GetWingBeamOrigin(ship, serverAbsorbTargetWingIndex);

            var shipRb = ship.GetComponent<Rigidbody>();
            Vector3 shipPos = shipRb != null ? shipRb.position : ship.transform.position;
            shipPos.y = 0f;
            return shipPos;
        }

        private float GetServerTime()
        {
            return NetworkManager.Singleton != null
                ? (float)NetworkManager.Singleton.ServerTime.Time
                : Time.time;
        }

        private ulong GetShipNetworkObjectId(Starship ship)
        {
            var shipNo = ship != null ? ship.NetworkObject : null;
            return shipNo != null ? shipNo.NetworkObjectId : 0ul;
        }

        /// <summary>Server: asteroid gems grant immediate pickup to top damager; other ships wait <see cref="asteroidNonPriorityPickupDelaySeconds"/>.</summary>
        private bool IsShipBlockedByAsteroidPriority(Starship ship)
        {
            if (depositTargetPlanetId.Value != 0) return false;
            if (expelledByShipId.Value != 0) return false;
            ulong priorityId = magnetPriorityShipId.Value;
            if (priorityId == 0) return false;

            ulong shipId = GetShipNetworkObjectId(ship);
            if (shipId == 0 || shipId == priorityId) return false;

            float elapsed = GetServerTime() - spawnTime.Value;
            return elapsed < Mathf.Max(0f, asteroidNonPriorityPickupDelaySeconds);
        }

        /// <summary>Whether this ship may collect or magnet this gem (all peers for visuals; server uses extra gates).</summary>
        public bool IsCollectibleByShip(Starship ship)
        {
            if (ship == null) return false;
            if (IsShipBlockedBySelfExpulsion(ship)) return false;
            if (IsShipBlockedByAsteroidPriority(ship)) return false;
            return true;
        }

        /// <summary>Server: whether this ship may collect or magnet this gem right now.</summary>
        public bool CanShipCollect(Starship ship)
        {
            if (!IsServer || ship == null) return false;
            if (serverAbsorbTargetShipId != 0) return false;
            if (IsShipTemporarilyBlockedFromPickup(ship)) return false;
            return IsCollectibleByShip(ship);
        }

        private bool IsShipBlockedBySelfExpulsion(Starship ship)
        {
            ulong expelled = expelledByShipId.Value;
            if (expelled == 0) return false;
            ulong shipId = GetShipNetworkObjectId(ship);
            if (shipId == 0 || shipId != expelled) return false;
            float elapsed = GetServerTime() - spawnTime.Value;
            return elapsed < Mathf.Max(0f, selfPickupDelaySeconds);
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

            if (!CanShipCollect(ship))
                return;

            int wingIndex = GemTractorBeamSettings.GetPullWingIndex(ship, this);
            CollectToShip(ship, wingIndex);
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
                if (!CanShipCollect(ship))
                    continue;

                if (TryCollectToShipFromWingProximity(ship, gemPos))
                    return;

                Vector3 shipPos = ship.transform.position;
                var srb = ship.GetComponent<Rigidbody>();
                if (srb != null) shipPos = srb.position;
                shipPos.y = 0f;

                float maxDist = GetShipProximityCollectDistance(ship);
                if (ToroidalMap.ToroidalDistance(gemPos, shipPos) > maxDist)
                    continue;

                CollectToShip(ship, -1);
                return;
            }
        }

        /// <summary>Collect when the gem is near any wing tractor anchor (ships with wing beams).</summary>
        private bool TryCollectToShipFromWingProximity(Starship ship, Vector3 gemPos)
        {
            var wings = ship.WingTractorBeams;
            if (wings == null || wings.Count == 0)
                return false;

            float maxDist = collectRadius + shipProximitySlop;
            int bestWing = -1;
            float bestDist = float.MaxValue;

            for (int wi = 0; wi < wings.Count; wi++)
            {
                if (wings[wi].wingTransform == null)
                    continue;

                float dist = ToroidalMap.ToroidalDistance(gemPos, wings[wi].GetWorldPosition());
                if (dist > maxDist || dist >= bestDist)
                    continue;

                bestDist = dist;
                bestWing = wi;
            }

            if (bestWing < 0)
                return false;

            CollectToShip(ship, bestWing);
            return true;
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

        private void CollectToShip(Starship ship, int collectingWingIndex)
        {
            if (!IsServer || ship == null) return;
            if (value.Value <= 0f) return;
            if (!CanShipCollect(ship)) return;
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
                VisualEffectsManager.Instance.SpawnFloatingCountFromServerAuthority(
                    gemPos, FloatingCountChannel.GemPickup, toAdd, ship.ShipTeam);

            // Tractor-pulled gems credit immediately but keep gliding to the collecting wing before despawn.
            if (GemTractorBeamSettings.IsPulledByAnyShip(this))
            {
                serverAbsorbTargetShipId = GetShipNetworkObjectId(ship);
                if (collectingWingIndex < 0)
                    collectingWingIndex = GemTractorBeamSettings.GetPullWingIndex(ship, this);
                serverAbsorbTargetWingIndex = collectingWingIndex;
                return;
            }

            DespawnCollectedGem();
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

        /// <summary>Server: stop coasting toward ships that moved out of magnetic pull range.</summary>
        private void ServerClipMagneticVelocityTowardOutOfRangeShips()
        {
            if (rb == null || depositTargetPlanetId.Value != 0)
                return;

            Vector3 gemPos = rb.position;
            gemPos.y = 0f;
            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;

            var ships = Starship.AllStarships;
            if (ships == null)
                return;

            for (int i = 0; i < ships.Count; i++)
            {
                Starship ship = ships[i];
                if (ship == null || !ship.IsSpawned || ship.IsDead)
                    continue;
                if (GemTractorBeamSettings.IsWithinMagneticPullRange(ship, this))
                    continue;
                if (!GemTractorBeamSettings.HasTractorInvolvement(ship, this))
                    continue;
                if (!GemTractorBeamSettings.TryGetPullTowardDirection(ship, this, out Vector3 pullDir))
                    continue;

                float toward = Vector3.Dot(vel, pullDir);
                if (toward > 0f)
                    vel -= pullDir * toward;
            }

            rb.linearVelocity = new Vector3(vel.x, rb.linearVelocity.y, vel.z);
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

            if (serverAbsorbTargetShipId != 0)
            {
                float absorbElapsed = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
                if (absorbElapsed >= lifetimeSeconds)
                {
                    DespawnCollectedGem();
                    return;
                }

                ServerTickTractorAbsorbGlide();
                return;
            }

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
            
            // While magnetically pulled, the attracting ship drives velocity — skip idle slowdown drag.
            if (GemTractorBeamSettings.IsPulledByAnyShip(this))
            {
                TryProximityCollectShip();
                return;
            }

            ServerClipMagneticVelocityTowardOutOfRangeShips();

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
