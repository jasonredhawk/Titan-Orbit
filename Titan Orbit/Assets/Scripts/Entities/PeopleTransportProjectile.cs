using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using System.Globalization;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;
using TitanOrbit.Debugging;

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
        private Rigidbody rb;
        private const float magnetSpeed = 10f;
        private const float PeopleAmountScaleMin = 1f;
        private const float PeopleAmountScaleMax = 12f;
        private const float VisualScaleMinMultiplier = 0.9f;
        private const float VisualScaleMaxMultiplier = 2.1f;
        private const float ShipCollectSlop = 0.5f;
        private const float MinInvasionVisualSeconds = 0.4f;
        private const float MinInvasionVisualTravel = 2.5f;
        private Vector3 baseVisualScale = Vector3.one;
        private Vector3 serverSpawnPosition;
        private float serverSpawnTime;

        #region agent log
        private static float _agentDbgLastProjectileClientLog = -999f;
        #endregion

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

            #region agent log
            if (Time.unscaledTime - _agentDbgLastProjectileClientLog >= 0.15f)
            {
                _agentDbgLastProjectileClientLog = Time.unscaledTime;
                if (!IsServer && IsClient)
                {
                    AgentDebugNdjson7964bb.Log(
                        "H_visual",
                        "PeopleTransportProjectile.OnNetworkSpawn",
                        "projectile_on_client",
                        "{\"isLoad\":" + (isLoad.Value ? "true" : "false")
                        + ",\"amount\":" + amount.Value.ToString(CultureInfo.InvariantCulture) + "}");
                }
                else if (IsServer)
                {
                    AgentDebugNdjson7964bb.Log(
                        "H_unload",
                        "PeopleTransportProjectile.OnNetworkSpawn",
                        "projectile_on_server",
                        "{\"isLoad\":" + (isLoad.Value ? "true" : "false")
                        + ",\"amount\":" + amount.Value.ToString(CultureInfo.InvariantCulture) + "}");
                }
            }
            #endregion
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
            return v * (rttSec * 0.55f);
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

                if (IsWithinShipCollectRange(myPos, ship))
                    TryDeliverLoadToShip(ship);
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                if (planet == null) return;

                Vector3 planetPos = planet.transform.position;
                planetPos.y = 0f;
                ApplyMagnetVelocity(myPos, planetPos);

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

            Vector3 targetVel = toTarget * magnetSpeed;
            rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, targetVel, magnetSpeed * Time.fixedDeltaTime * 4f);
            rb.linearDamping = 0f;
        }

        private bool IsWithinShipCollectRange(Vector3 projectilePos, Starship ship)
        {
            float hullRadius = ShipCollectSlop;
            Collider shipCollider = ship.GetComponent<Collider>();
            if (shipCollider != null && shipCollider.enabled)
            {
                Vector3 e = shipCollider.bounds.extents;
                float colliderRadius = Mathf.Sqrt(e.x * e.x + e.z * e.z);
                if (colliderRadius > 0.01f)
                    hullRadius = Mathf.Max(hullRadius, colliderRadius * 0.45f);
            }

            return ToroidalMap.ToroidalDistance(projectilePos, GetShipWorldPosition(ship)) <= hullRadius;
        }

        private static bool IsWithinPlanetOrbitShell(Planet planet, Vector3 worldPos)
        {
            if (planet == null) return false;
            worldPos.y = 0f;
            float dist = ToroidalMap.ToroidalDistance(worldPos, planet.transform.position);
            float inner = planet.PlanetSize * 0.5f * 0.9f;
            float outer = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal() * 1.1f;
            return dist >= inner && dist <= outer;
        }

        private bool IsSameTeamPlanet(Planet planet)
        {
            if (planet == null) return false;
            var sourceTeam = (TeamManager.Team)team.Value;
            return planet.TeamOwnership == sourceTeam
                || (planet is HomePlanet home && home.AssignedTeam == sourceTeam);
        }

        /// <summary>Friendly unload: orbit shell. Invasion unload: must travel from ship then reach near the planet surface (population already applied at spawn).</summary>
        private bool CanCompleteUnloadDelivery(Vector3 projectilePos, Planet planet)
        {
            if (planet == null) return false;

            if (IsSameTeamPlanet(planet))
                return IsWithinPlanetOrbitShell(planet, projectilePos);

            if (Time.time - serverSpawnTime < MinInvasionVisualSeconds)
                return false;
            if (ToroidalMap.ToroidalDistance(projectilePos, serverSpawnPosition) < MinInvasionVisualTravel)
                return false;

            float distToPlanet = ToroidalMap.ToroidalDistance(projectilePos, planet.transform.position);
            float surfaceRadius = planet.PlanetSize * 0.5f + planet.PlanetSize * 0.35f;
            return distToPlanet <= surfaceRadius;
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

            #region agent log
            AgentDebugNdjson7964bb.Log(
                "H_unload",
                "PeopleTransportProjectile.TryDeliverUnloadToPlanet",
                "delivered",
                "{\"amount\":" + amount.Value.ToString(CultureInfo.InvariantCulture)
                + ",\"sameTeam\":" + (sameTeamPlanet ? "true" : "false") + "}");
            #endregion

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
                if (ship != null && hitShip == ship)
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
