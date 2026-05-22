using System.Collections;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Projectile that beams people between planet and ship. Load: planet->ship. Unload: ship->planet.
    /// Absorbs on contact with target.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PeopleTransportProjectile : NetworkBehaviour
    {
        private NetworkVariable<float> amount = new NetworkVariable<float>(1f);
        private NetworkVariable<ulong> targetId = new NetworkVariable<ulong>(0);
        private NetworkVariable<bool> isLoad = new NetworkVariable<bool>(true); // true = planet->ship, false = ship->planet
        private NetworkVariable<int> team = new NetworkVariable<int>((int)TeamManager.Team.None);
        private NetworkVariable<ulong> spawningShipId = new NetworkVariable<ulong>(0);
        private NetworkVariable<ulong> sourcePlanetId = new NetworkVariable<ulong>(0);
        private NetworkVariable<Vector3> syncedPlanarVelocity = new NetworkVariable<Vector3>(
            Vector3.zero,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        /// <summary>When true, projectile is pooled (hidden); skip magnet logic and do not despawn.</summary>
        private NetworkVariable<bool> isInPool = new NetworkVariable<bool>(true);
        private Rigidbody rb;
        private NetworkTransform networkTransform;
        private bool serverInitializedBeforeSpawn;
        private Coroutine serverReapplyVelocityRoutine;

        public bool IsInPool => isInPool.Value;
        private bool HasServerAuthority => IsServer || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer);
        private const float magnetSpeed = 11f;
        private const float magnetCloseRangeSpeed = 18f;
        private const float magnetCloseRangeWorld = 5f;
        private const float PeopleAmountScaleMin = 1f;
        private const float PeopleAmountScaleMax = 12f;
        private const float VisualScaleMinMultiplier = 0.9f;
        private const float VisualScaleMaxMultiplier = 2.1f;
        private const float ShipCollectHullMultiplier = 0.42f;
        private const float ShipCollectExtraSlop = 0.3f;
        /// <summary>Unload completes at/near the hull (magnet target is on the surface, not inside the sphere).</summary>
        private const float PlanetSurfaceReachOutwardSlop = 0.06f;
        private const float PlanetUnloadMagnetCollectSlop = 0.55f;
        private const float PlanetUnloadStuckFailsafeSeconds = 1.25f;
        private const float MinVisualTravelSeconds = 0.35f;
        private const float MinVisualTravelDistance = 0.75f;
        private const float ClientNetworkSnapDistance = 10f;
        private const float ClientNetworkBlendRate = 14f;
        private Vector3 baseVisualScale = Vector3.one;
        private Vector3 serverSpawnPosition;
        private float serverSpawnTime;
        private float serverNearPlanetSurfaceSince = -1f;
        private Vector3 clientPredictedPosition;
        private Vector3 clientPredictedVelocity;
        private bool clientPredictionInitialized;

        /// <summary>Remote clients simulate magnet motion locally for smooth visuals; host/server use physics.</summary>
        public bool UsesClientPredictedPosition => IsClient && !IsServer;

        public Vector3 ClientPredictedLogicalPosition => clientPredictedPosition;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            networkTransform = GetComponent<NetworkTransform>();
            baseVisualScale = transform.localScale.sqrMagnitude > 0.0001f ? transform.localScale : Vector3.one;
            EnsureNetworkedMoverComponents();
            if (networkTransform == null)
                networkTransform = GetComponent<NetworkTransform>();
        }

        /// <summary>Match Gem prefab: NetworkTransform + NetworkRigidbody + ToroidalRenderer for client visuals on a toroidal map.</summary>
        private void EnsureNetworkedMoverComponents()
        {
            if (GetComponent<NetworkTransform>() == null)
            {
                var nt = gameObject.AddComponent<NetworkTransform>();
                nt.Interpolate = true;
                nt.UseUnreliableDeltas = true;
            }

            var existingNt = GetComponent<NetworkTransform>();
            if (existingNt != null)
            {
                existingNt.UseUnreliableDeltas = true;
                existingNt.PositionThreshold = 0.02f;
            }

            if (GetComponent<NetworkRigidbody>() == null)
                gameObject.AddComponent<NetworkRigidbody>();

            if (GetComponent<ToroidalRenderer>() == null)
                gameObject.AddComponent<ToroidalRenderer>();

            var netObj = GetComponent<NetworkObject>();
            if (netObj != null)
                netObj.SynchronizeTransform = false;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer && !serverInitializedBeforeSpawn)
                isInPool.Value = false;

            amount.OnValueChanged += OnAmountChanged;
            isInPool.OnValueChanged += OnIsInPoolChanged;
            ApplyVisualScaleFromAmount(amount.Value);
            OnIsInPoolChanged(false, isInPool.Value);
            ResetClientPredictionFromNetwork();
            CatchUpClientPredictionAfterSpawn();
        }

        /// <summary>Advance client visuals by ~half RTT so spheres don't pop in behind the ship/planet.</summary>
        private void CatchUpClientPredictionAfterSpawn()
        {
            if (!UsesClientPredictedPosition || amount.Value <= 0f)
                return;

            float catchUpSec = 0.06f;
            var nm = NetworkManager.Singleton;
            var transport = nm?.NetworkConfig?.NetworkTransport;
            if (transport != null)
            {
                ulong ms = transport.GetCurrentRtt(NetworkManager.ServerClientId);
                if (ms > 0)
                    catchUpSec = ms * 0.0005f;
            }
            catchUpSec = Mathf.Clamp(catchUpSec, 0f, 0.22f);
            if (catchUpSec <= 0.001f || !TryGetMagnetTargetPosition(out Vector3 targetPos))
                return;

            const float step = 1f / 60f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(catchUpSec / step));
            for (int i = 0; i < steps; i++)
            {
                clientPredictedVelocity = ComputeMagnetVelocity(
                    clientPredictedPosition, targetPos, clientPredictedVelocity, step);
                clientPredictedPosition += clientPredictedVelocity * step;
                clientPredictedPosition.y = 0f;
            }
        }

        /// <summary>Legacy RTT extrapolation; unused when <see cref="UsesClientPredictedPosition"/> runs local magnet sim.</summary>
        public Vector3 GetClientVisualExtrapolationOffset() => Vector3.zero;

        private void Update()
        {
            if (isInPool.Value || !UsesClientPredictedPosition || amount.Value <= 0f)
                return;

            if (!clientPredictionInitialized)
                ResetClientPredictionFromNetwork();

            if (!TryGetMagnetTargetPosition(out Vector3 targetPos))
                return;

            clientPredictedVelocity = ComputeMagnetVelocity(
                clientPredictedPosition, targetPos, clientPredictedVelocity, Time.deltaTime);
            clientPredictedPosition += clientPredictedVelocity * Time.deltaTime;
            clientPredictedPosition.y = 0f;

            SoftBlendClientPredictionTowardNetwork();
        }

        private void ResetClientPredictionFromNetwork()
        {
            clientPredictedPosition = rb != null ? rb.position : transform.position;
            clientPredictedPosition.y = 0f;
            clientPredictedVelocity = syncedPlanarVelocity.Value;
            clientPredictedVelocity.y = 0f;
            clientPredictionInitialized = true;
        }

        private void SoftBlendClientPredictionTowardNetwork()
        {
            Vector3 netPos = transform.position;
            netPos.y = 0f;
            float drift = ToroidalMap.ToroidalDistance(clientPredictedPosition, netPos);
            if (drift > ClientNetworkSnapDistance)
            {
                clientPredictedPosition = netPos;
                clientPredictedVelocity = syncedPlanarVelocity.Value;
                clientPredictedVelocity.y = 0f;
                return;
            }

            if (drift < 0.2f)
                return;

            float blend = (1f - Mathf.Exp(-ClientNetworkBlendRate * Time.deltaTime)) * Mathf.Clamp01(drift / 2.5f);
            clientPredictedPosition += ToroidalMap.ShortestWorldOffsetXZ(clientPredictedPosition, netPos) * blend;
            clientPredictedPosition.y = 0f;
            clientPredictedVelocity = Vector3.Lerp(clientPredictedVelocity, syncedPlanarVelocity.Value, blend * 0.35f);
        }

        private bool TryGetMagnetTargetPosition(out Vector3 targetPos)
        {
            targetPos = Vector3.zero;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetObj))
                return false;

            if (isLoad.Value)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                if (ship == null || ship.IsDead)
                    return false;
                targetPos = GetShipWorldPosition(ship);
                return true;
            }

            Planet planet = targetObj.GetComponent<Planet>();
            if (planet == null)
                return false;
            targetPos = GetPlanetSurfaceMagnetTarget(planet, clientPredictedPosition);
            return true;
        }

        private void FixedUpdate()
        {
            if (!IsServer || rb == null || isInPool.Value || amount.Value <= 0f) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetObj))
                return;

            Vector3 myPos = rb.position;
            myPos.y = 0f;

            if (isLoad.Value)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                if (ship == null || ship.IsDead) return;

                Vector3 shipPos = GetShipWorldPosition(ship);
                ApplyMagnetVelocity(myPos, shipPos);

                if (HasMinVisualTravel(myPos) && IsWithinShipCollectRange(myPos, ship))
                    TryDeliverLoadToShip(ship);
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                if (planet == null) return;

                Vector3 magnetTarget = GetPlanetSurfaceMagnetTarget(planet, myPos);
                ApplyMagnetVelocity(myPos, magnetTarget);

                if (CanCompleteUnloadDelivery(myPos, planet))
                {
                    serverNearPlanetSurfaceSince = -1f;
                    TryDeliverUnloadToPlanet(planet, nm);
                }
                else if (ShouldForceUnloadAfterStuckNearSurface(myPos, planet))
                {
                    serverNearPlanetSurfaceSince = -1f;
                    TryDeliverUnloadToPlanet(planet, nm);
                }
            }

            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            syncedPlanarVelocity.Value = vel;
        }

        private static Vector3 GetShipWorldPosition(Starship ship)
        {
            Vector3 shipPos = ship.transform.position;
            var shipRb = ship.GetComponent<Rigidbody>();
            if (shipRb != null)
                shipPos = shipRb.position;
            shipPos.y = 0f;
            return shipPos;
        }

        private void ApplyMagnetVelocity(Vector3 myPos, Vector3 targetPos)
        {
            if (rb == null) return;
            rb.linearVelocity = ComputeMagnetVelocity(myPos, targetPos, rb.linearVelocity, Time.fixedDeltaTime);
            rb.linearDamping = 0f;
        }

        private static Vector3 ComputeMagnetVelocity(Vector3 myPos, Vector3 targetPos, Vector3 currentVel, float dt)
        {
            myPos.y = 0f;
            targetPos.y = 0f;
            Vector3 toTarget = ToroidalMap.ToroidalDirection(myPos, targetPos);
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) toTarget = Vector3.forward;
            else toTarget.Normalize();

            float dist = ToroidalMap.ToroidalDistance(myPos, targetPos);
            float speed = dist <= magnetCloseRangeWorld ? magnetCloseRangeSpeed : magnetSpeed;
            Vector3 targetVel = toTarget * speed;
            return Vector3.MoveTowards(currentVel, targetVel, speed * dt * 4f);
        }

        private float GetShipCollectReach(Starship ship)
        {
            float reach = ShipCollectExtraSlop;
            Collider shipCollider = ship.GetComponent<Collider>();
            if (shipCollider != null && shipCollider.enabled)
            {
                Vector3 e = shipCollider.bounds.extents;
                float colliderRadius = Mathf.Sqrt(e.x * e.x + e.z * e.z);
                if (colliderRadius > 0.01f)
                    reach = colliderRadius * ShipCollectHullMultiplier + ShipCollectExtraSlop;
            }

            return reach;
        }

        private bool IsWithinShipCollectRange(Vector3 projectilePos, Starship ship)
        {
            return ToroidalMap.ToroidalDistance(projectilePos, GetShipWorldPosition(ship)) <= GetShipCollectReach(ship);
        }

        /// <summary>Point on the planet hull facing the projectile (not the core) so visuals meet the mesh.</summary>
        private static Vector3 GetPlanetSurfaceMagnetTarget(Planet planet, Vector3 fromPos)
        {
            Vector3 planetPos = planet.transform.position;
            planetPos.y = 0f;
            Vector3 toCore = ToroidalMap.ToroidalDirection(fromPos, planetPos);
            toCore.y = 0f;
            if (toCore.sqrMagnitude < 0.0001f)
                return planetPos;

            toCore.Normalize();
            float surfaceWorld = planet.PlanetSize * 0.5f;
            return planetPos - toCore * surfaceWorld;
        }

        private static float GetPlanetSurfaceWorldRadius(Planet planet)
        {
            return planet != null ? planet.PlanetSize * 0.5f : 0f;
        }

        private static bool IsWithinPlanetSurfaceReach(Planet planet, Vector3 worldPos)
        {
            if (planet == null) return false;
            worldPos.y = 0f;
            float dist = ToroidalMap.ToroidalDistance(worldPos, planet.transform.position);
            float surfaceWorld = GetPlanetSurfaceWorldRadius(planet);
            float outwardSlop = Mathf.Max(PlanetUnloadMagnetCollectSlop * 0.5f, surfaceWorld * PlanetSurfaceReachOutwardSlop);
            return dist <= surfaceWorld + outwardSlop;
        }

        private static bool IsWithinPlanetUnloadMagnetCollectRange(Planet planet, Vector3 worldPos)
        {
            if (planet == null) return false;
            Vector3 magnetTarget = GetPlanetSurfaceMagnetTarget(planet, worldPos);
            float slop = Mathf.Max(PlanetUnloadMagnetCollectSlop, planet.PlanetSize * 0.035f);
            return ToroidalMap.ToroidalDistance(worldPos, magnetTarget) <= slop;
        }

        private bool ShouldForceUnloadAfterStuckNearSurface(Vector3 projectilePos, Planet planet)
        {
            if (!HasMinVisualTravel(projectilePos) || planet == null)
            {
                serverNearPlanetSurfaceSince = -1f;
                return false;
            }

            if (!IsWithinPlanetSurfaceReach(planet, projectilePos)
                && !IsWithinPlanetUnloadMagnetCollectRange(planet, projectilePos))
            {
                serverNearPlanetSurfaceSince = -1f;
                return false;
            }

            if (serverNearPlanetSurfaceSince < 0f)
                serverNearPlanetSurfaceSince = Time.time;
            return Time.time - serverNearPlanetSurfaceSince >= PlanetUnloadStuckFailsafeSeconds;
        }

        private bool HasMinVisualTravel(Vector3 projectilePos)
        {
            if (Time.time - serverSpawnTime < MinVisualTravelSeconds)
                return false;
            return ToroidalMap.ToroidalDistance(projectilePos, serverSpawnPosition) >= MinVisualTravelDistance;
        }

        private bool IsSameTeamPlanet(Planet planet)
        {
            if (planet == null) return false;
            var sourceTeam = (TeamManager.Team)team.Value;
            return planet.TeamOwnership == sourceTeam
                || (planet is HomePlanet home && home.AssignedTeam == sourceTeam);
        }

        /// <summary>Unload completes on the planet hull after a short visible trip (population may already be applied for invasion).</summary>
        private bool CanCompleteUnloadDelivery(Vector3 projectilePos, Planet planet)
        {
            if (planet == null) return false;
            if (!HasMinVisualTravel(projectilePos))
                return false;
            return IsWithinPlanetSurfaceReach(planet, projectilePos)
                || IsWithinPlanetUnloadMagnetCollectRange(planet, projectilePos);
        }

        private void TryDeliverLoadToShip(Starship ship)
        {
            if (!IsServer || amount.Value <= 0f || ship == null) return;

            float space = ship.PeopleCapacity - ship.CurrentPeople;
            float toAdd = Mathf.Min(amount.Value, space);
            if (toAdd > 0f)
            {
                ship.AddPeopleFromServer(toAdd);
                Vector3 feedbackPos = GetShipWorldPosition(ship);
                ship.OnPeopleLoadArrivedFromProjectile(toAdd, (TeamManager.Team)team.Value, feedbackPos);
                ship.ReleasePeopleInTransit(toAdd);
                if (ScoreSystem.Instance != null)
                    ScoreSystem.Instance.AwardFriendlyLoad(ship, toAdd);
            }
            else
                ship.ReleasePeopleInTransit(amount.Value);

            DespawnProjectile();
        }

        private void TryDeliverUnloadToPlanet(Planet planet, NetworkManager nm)
        {
            if (!IsServer || amount.Value <= 0f || planet == null) return;

            bool sameTeamPlanet = IsSameTeamPlanet(planet);

            // Friendly reinforce unload: apply when the projectile reaches the planet.
            // Hostile invasion: population is applied when the ship spawns the projectile.
            if (sameTeamPlanet)
                planet.AddPopulationFromServer(amount.Value, (TeamManager.Team)team.Value);

            DespawnProjectile();
        }

        private void DespawnProjectile()
        {
            amount.Value = 0f;
            if (PeoplePool.Instance != null && PeoplePool.Instance.ReturnToPool(this))
                return;

            var no = GetComponent<NetworkObject>();
            if (no != null)
                no.Despawn();
        }

        /// <summary>Server only. Recycles projectile without Despawn.</summary>
        public void ServerReturnToPool()
        {
            if (!IsServer) return;
            StopServerReapplyVelocityRoutine();
            amount.Value = 0f;
            targetId.Value = 0;
            isLoad.Value = true;
            team.Value = (int)TeamManager.Team.None;
            spawningShipId.Value = 0;
            sourcePlanetId.Value = 0;
            syncedPlanarVelocity.Value = Vector3.zero;
            serverNearPlanetSurfaceSince = -1f;
            serverInitializedBeforeSpawn = false;
            transform.position = Vector3.zero;
            if (rb != null)
            {
                rb.position = Vector3.zero;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            isInPool.Value = true;
        }

        /// <summary>Server only. Marks projectile active after Initialize.</summary>
        public void ServerActivateFromPool()
        {
            if (IsServer) isInPool.Value = false;
        }

        /// <summary>Server only. Snap NetworkTransform after pool recycle and apply launch velocity.</summary>
        public void ServerFinishPooledSpawn(Vector3 worldPosition, Vector3 linearVelocity)
        {
            if (!IsServer) return;

            worldPosition.y = 0f;
            linearVelocity.y = 0f;
            Quaternion rot = transform.rotation;
            Vector3 scale = transform.localScale;

            if (rb != null)
            {
                rb.isKinematic = false;
                rb.position = worldPosition;
                rb.linearVelocity = linearVelocity;
                rb.angularVelocity = Vector3.zero;
                rb.linearDamping = 0f;
                rb.WakeUp();
            }
            else
            {
                transform.SetPositionAndRotation(worldPosition, rot);
            }

            if (networkTransform != null)
                networkTransform.SetState(worldPosition, rot, scale, teleportDisabled: false);

            serverSpawnPosition = worldPosition;
            serverSpawnTime = Time.time;
            serverNearPlanetSurfaceSince = -1f;
            syncedPlanarVelocity.Value = linearVelocity;

            if (rb != null)
            {
                rb.position = worldPosition;
                rb.linearVelocity = linearVelocity;
                rb.WakeUp();
            }

            StopServerReapplyVelocityRoutine();
            serverReapplyVelocityRoutine = StartCoroutine(ServerReapplyVelocityAfterPhysicsSync(linearVelocity));
        }

        private void StopServerReapplyVelocityRoutine()
        {
            if (serverReapplyVelocityRoutine != null)
            {
                StopCoroutine(serverReapplyVelocityRoutine);
                serverReapplyVelocityRoutine = null;
            }
        }

        private IEnumerator ServerReapplyVelocityAfterPhysicsSync(Vector3 linearVelocity)
        {
            yield return new WaitForFixedUpdate();
            serverReapplyVelocityRoutine = null;
            if (!IsServer || rb == null || isInPool.Value) yield break;
            linearVelocity.y = 0f;
            rb.isKinematic = false;
            rb.linearVelocity = linearVelocity;
            rb.angularVelocity = Vector3.zero;
            syncedPlanarVelocity.Value = linearVelocity;
            rb.WakeUp();
        }

        private void OnIsInPoolChanged(bool previous, bool current)
        {
            gameObject.SetActive(!current);
        }

        public void Initialize(float peopleAmount, ulong targetNetworkObjectId, bool loadingFromPlanet, TeamManager.Team sourceTeam, ulong shipNetworkObjectId = 0, ulong sourcePlanetNetworkObjectId = 0)
        {
            if (HasServerAuthority)
            {
                serverInitializedBeforeSpawn = true;
                amount.Value = peopleAmount;
                targetId.Value = targetNetworkObjectId;
                isLoad.Value = loadingFromPlanet;
                team.Value = (int)sourceTeam;
                spawningShipId.Value = shipNetworkObjectId;
                sourcePlanetId.Value = sourcePlanetNetworkObjectId;

                if (rb != null)
                {
                    rb.linearDamping = 0f;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    serverSpawnPosition = rb.position;
                }
                else
                    serverSpawnPosition = transform.position;
                serverSpawnPosition.y = 0f;
                serverSpawnTime = Time.time;
                serverNearPlanetSurfaceSince = -1f;
                ApplyVisualScaleFromAmount(peopleAmount);

                Vector3 initVel = rb != null ? rb.linearVelocity : Vector3.zero;
                initVel.y = 0f;
                syncedPlanarVelocity.Value = initVel;
            }
        }

        public override void OnNetworkDespawn()
        {
            amount.OnValueChanged -= OnAmountChanged;
            isInPool.OnValueChanged -= OnIsInPoolChanged;
            StopServerReapplyVelocityRoutine();
            serverInitializedBeforeSpawn = false;
            if (IsServer && isLoad.Value && amount.Value > 0f && targetId.Value != 0)
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out var targetObj))
                {
                    var ship = targetObj.GetComponent<Starship>();
                    if (ship != null)
                        ship.ReleasePeopleInTransit(amount.Value);
                }
            }
            base.OnNetworkDespawn();
        }

        private void OnAmountChanged(float previousValue, float newValue)
        {
            ApplyVisualScaleFromAmount(newValue);
        }

        private void ApplyVisualScaleFromAmount(float peopleAmount)
        {
            float clampedAmount = Mathf.Clamp(Mathf.Max(0.001f, peopleAmount), PeopleAmountScaleMin, PeopleAmountScaleMax);
            float normalized = Mathf.InverseLerp(PeopleAmountScaleMin, PeopleAmountScaleMax, clampedAmount);
            float scaleMultiplier = Mathf.Lerp(VisualScaleMinMultiplier, VisualScaleMaxMultiplier, normalized);
            Vector3 scale = baseVisualScale * scaleMultiplier;
            transform.localScale = scale;
            Transform visual = transform.Find("Visual");
            if (visual != null)
                visual.localScale = Vector3.one;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer || isInPool.Value || amount.Value <= 0f) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetObj))
                return;

            if (isLoad.Value)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                Starship hitShip = other.GetComponent<Starship>() ?? other.GetComponentInParent<Starship>();
                Vector3 myPos = rb != null ? rb.position : transform.position;
                if (ship != null && hitShip == ship
                    && HasMinVisualTravel(myPos)
                    && IsWithinShipCollectRange(myPos, ship))
                    TryDeliverLoadToShip(ship);
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                Planet hitPlanet = other.GetComponent<Planet>() ?? other.GetComponentInParent<Planet>();
                Vector3 myPos = rb != null ? rb.position : transform.position;
                if (planet != null && hitPlanet == planet && CanCompleteUnloadDelivery(myPos, planet))
                    TryDeliverUnloadToPlanet(planet, nm);
            }
        }
    }
}
