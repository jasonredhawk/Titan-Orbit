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
    [DefaultExecutionOrder(32500)] // After ToroidalRenderer repositions the Visual child
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
        private Rigidbody rb;
        private const float magnetSpeed = 11f;
        private const float magnetCloseRangeSpeed = 18f;
        private const float magnetCloseRangeWorld = 5f;
        private const float PeopleAmountScaleMin = 1f;
        private const float PeopleAmountScaleMax = 12f;
        private const float VisualScaleMinMultiplier = 0.9f;
        private const float VisualScaleMaxMultiplier = 2.1f;
        private const float ShipCollectHullMultiplier = 0.42f;
        private const float ShipCollectExtraSlop = 0.3f;
        private const float PlanetSurfaceReachFraction = 0.96f;
        private const float MinVisualTravelSeconds = 0.35f;
        private const float MinVisualTravelDistance = 0.75f;
        private const float ClientVisualApproachLerp = 24f;
        private const float ClientVelocityLeadMultiplier = 1.15f;
        private const float ClientApproachDisplayMax = 28f;
        private Vector3 baseVisualScale = Vector3.one;
        private Vector3 serverSpawnPosition;
        private float serverSpawnTime;
        private Transform visualChild;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            baseVisualScale = transform.localScale.sqrMagnitude > 0.0001f ? transform.localScale : Vector3.one;
            EnsureNetworkedMoverComponents();
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
            amount.OnValueChanged += OnAmountChanged;
            ApplyVisualScaleFromAmount(amount.Value);
        }

        /// <summary>
        /// World-space offset for visuals on remote clients (same idea as legacy bullets).
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
            return v * (rttSec * ClientVelocityLeadMultiplier);
        }

        private void LateUpdate()
        {
            if (!IsClient || amount.Value <= 0f)
                return;

            CacheVisualChild();
            if (visualChild == null)
                return;

            Vector3? destDisplay = TryGetDestinationDisplayPosition();
            if (!destDisplay.HasValue)
                return;

            float displayDist = Vector3.Distance(visualChild.position, destDisplay.Value);
            if (displayDist > ClientApproachDisplayMax)
                return;

            float t = 1f - Mathf.Exp(-ClientVisualApproachLerp * Time.deltaTime);
            visualChild.position = Vector3.Lerp(visualChild.position, destDisplay.Value, t);
        }

        private void CacheVisualChild()
        {
            if (visualChild == null)
                visualChild = transform.Find("Visual");
        }

        private Vector3? TryGetDestinationDisplayPosition()
        {
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null)
                return null;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetObj))
                return null;

            if (isLoad.Value)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                if (ship == null) return null;
                Vector3 logical = GetShipWorldPosition(ship);
                return ToroidalMap.GetDisplayPosition(logical, cam.transform.position);
            }

            Planet planet = targetObj.GetComponent<Planet>();
            if (planet == null) return null;
            Vector3 from = rb != null ? rb.position : transform.position;
            Vector3 surfaceLogical = GetPlanetSurfaceMagnetTarget(planet, from);
            return ToroidalMap.GetDisplayPosition(surfaceLogical, cam.transform.position);
        }

        private void FixedUpdate()
        {
            if (!IsServer || rb == null || amount.Value <= 0f) return;

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
                    TryDeliverUnloadToPlanet(planet, nm);
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
            Vector3 toTarget = ToroidalMap.ToroidalDirection(myPos, targetPos);
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) toTarget = Vector3.forward;
            else toTarget.Normalize();

            float dist = ToroidalMap.ToroidalDistance(myPos, targetPos);
            float speed = dist <= magnetCloseRangeWorld ? magnetCloseRangeSpeed : magnetSpeed;
            Vector3 targetVel = toTarget * speed;
            rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, targetVel, speed * Time.fixedDeltaTime * 4f);
            rb.linearDamping = 0f;
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

        private static bool IsWithinPlanetSurfaceReach(Planet planet, Vector3 worldPos)
        {
            if (planet == null) return false;
            worldPos.y = 0f;
            float dist = ToroidalMap.ToroidalDistance(worldPos, planet.transform.position);
            float surfaceWorld = planet.PlanetSize * 0.5f;
            return dist <= surfaceWorld * PlanetSurfaceReachFraction;
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
            return IsWithinPlanetSurfaceReach(planet, projectilePos);
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
            var no = GetComponent<NetworkObject>();
            if (no != null)
                no.Despawn();
        }

        public void Initialize(float peopleAmount, ulong targetNetworkObjectId, bool loadingFromPlanet, TeamManager.Team sourceTeam, ulong shipNetworkObjectId = 0, ulong sourcePlanetNetworkObjectId = 0)
        {
            if (IsServer)
            {
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
                ApplyVisualScaleFromAmount(peopleAmount);
                syncedPlanarVelocity.Value = Vector3.zero;
            }
        }

        public override void OnNetworkDespawn()
        {
            amount.OnValueChanged -= OnAmountChanged;
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
            if (!IsServer || amount.Value <= 0f) return;

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
