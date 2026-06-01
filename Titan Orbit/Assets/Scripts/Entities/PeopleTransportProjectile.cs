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
        private Rigidbody rb;
        /// <summary>Base time for a sphere to travel spawn→target at normal visual speed (spawn rate is unchanged).</summary>
        public const float TargetVisualTravelSeconds = 3f;
        /// <summary>Multiplies travel duration only (5 = five times slower sphere movement).</summary>
        public const float VisualTravelDurationMultiplier = 5f;
        /// <summary>Additional sphere speed multiplier (2.4 = 100% faster than the prior 1.2 tuning).</summary>
        public const float VisualTravelSpeedBonus = 2.4f;
        /// <summary>Push load spawns slightly outside the hull so the sphere is visible immediately.</summary>
        public const float SurfaceSpawnOutwardNudge = 0.45f;
        /// <summary>Planet→ship load spheres move this much faster than unload (1.5 = 50% faster).</summary>
        public const float LoadMagnetSpeedMultiplier = 1.5f;
        public static float EffectiveVisualTravelSeconds =>
            TargetVisualTravelSeconds * VisualTravelDurationMultiplier / VisualTravelSpeedBonus;
        private const float magnetSpeed = 11f;
        private const float magnetCloseRangeSpeed = 18f;
        private const float MagnetCloseRangeSpeedRatio = magnetCloseRangeSpeed / magnetSpeed;
        private const float magnetCloseRangeWorld = 5f;
        private const float PeopleAmountScaleMin = 1f;
        private const float PeopleAmountScaleMax = 12f;
        private const float VisualScaleMinMultiplier = 0.9f;
        private const float VisualScaleMaxMultiplier = 2.1f;
        private const float ShipLoadCollectPadding = 0.22f;
        private const float ShipLoadCollectMinDistance = 0.4f;
        private const float ShipHullMagnetInset = 0.12f;
        private const float LoadDeliveryMinSeconds = 0.22f;
        private const float LoadDeliveryMinSpawnDistance = 0.35f;
        private const float PlanetSurfaceReachSlopFraction = 0.12f;
        private const float PlanetSurfaceReachSlopMinWorld = 0.85f;
        private const float MinVisualTravelDistance = 0.75f;
        private const float UnloadDeliveryMinSeconds = 0.18f;
        private const float UnloadDeliveryMinTravelDistance = 0.3f;
        private const float ForeignPlanetImpactMinSeconds = 0.08f;
        private NetworkVariable<float> magnetCruiseSpeed = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        private NetworkVariable<float> health = new NetworkVariable<float>(
            1f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        /// <summary>Hostile invasion applies planet population when the sphere spawns; cleared on delivery or reverted on destroy.</summary>
        private NetworkVariable<bool> hostileInvasionAppliedOnSpawn = new NetworkVariable<bool>(false);
        /// <summary>HP per person in this sphere (5 people = 5× HP of 1 person).</summary>
        public const float HealthPerPerson = 4f;
        private const float ClientNetworkSnapDistance = 10f;
        private const float ClientNetworkBlendRate = 14f;
        private Vector3 baseVisualScale = Vector3.one;
        private Vector3 serverSpawnPosition;
        private float serverSpawnTime;
        private Vector3 clientPredictedPosition;
        private Vector3 clientPredictedVelocity;
        private bool clientPredictionInitialized;
        private ulong ignoredCollisionShipNetworkId;

        /// <summary>Remote clients simulate magnet motion locally for smooth visuals; host/server use physics.</summary>
        public bool UsesClientPredictedPosition => IsClient && !IsServer;

        public Vector3 ClientPredictedLogicalPosition => clientPredictedPosition;

        public float CurrentHealth => health.Value;
        public float PeopleAmount => amount.Value;
        public TeamManager.Team SourceTeam => (TeamManager.Team)team.Value;
        public bool IsLoadTransfer => isLoad.Value;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            baseVisualScale = transform.localScale.sqrMagnitude > 0.0001f ? transform.localScale : Vector3.one;
            EnsureNetworkedMoverComponents();
            ConfigureTransportRigidbody();
        }

        /// <summary>Kinematic + trigger so magnet motion does not fight the ship hull collider.</summary>
        private void ConfigureTransportRigidbody()
        {
            if (rb == null) return;
            rb.useGravity = false;
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearDamping = 0f;
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
                    clientPredictedPosition, targetPos, clientPredictedVelocity, step, GetCruiseSpeed(), GetCloseRangeSpeed());
                clientPredictedPosition += clientPredictedVelocity * step;
                clientPredictedPosition.y = 0f;
            }
        }

        /// <summary>Legacy RTT extrapolation; unused when <see cref="UsesClientPredictedPosition"/> runs local magnet sim.</summary>
        public Vector3 GetClientVisualExtrapolationOffset() => Vector3.zero;

        private void Update()
        {
            if (!UsesClientPredictedPosition || amount.Value <= 0f)
                return;

            if (!clientPredictionInitialized)
                ResetClientPredictionFromNetwork();

            if (!TryGetMagnetTargetPosition(out Vector3 targetPos))
                return;

            clientPredictedVelocity = ComputeMagnetVelocity(
                clientPredictedPosition, targetPos, clientPredictedVelocity, Time.deltaTime, GetCruiseSpeed(), GetCloseRangeSpeed());
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
                if (ship == null)
                    return false;
                if (ship.IsDead)
                {
                    if (TryGetSourcePlanet(out Planet deadOrbitPlanet))
                    {
                        targetPos = GetSurfacePointToward(deadOrbitPlanet, clientPredictedPosition);
                        return true;
                    }
                    return false;
                }

                if (!IsShipEligibleForLoadFromSourcePlanet(ship) && TryGetSourcePlanet(out Planet sourcePlanet))
                {
                    targetPos = GetSurfacePointToward(sourcePlanet, clientPredictedPosition);
                    return true;
                }

                targetPos = GetShipMagnetTarget(ship, clientPredictedPosition);
                return true;
            }

            Planet planet = targetObj.GetComponent<Planet>();
            if (planet == null)
                return false;
            targetPos = GetSurfacePointToward(planet, clientPredictedPosition);
            return true;
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
                if (ship == null) return;

                if (ship.IsDead || !IsShipEligibleForLoadFromSourcePlanet(ship))
                {
                    if (TryGetSourcePlanet(out Planet sourcePlanet))
                    {
                        Vector3 surfaceTarget = GetSurfacePointToward(sourcePlanet, myPos);
                        ApplyMagnetVelocity(myPos, surfaceTarget);
                        if (CanCompleteReturnToSourcePlanet(myPos, sourcePlanet))
                            TryCompleteReturnToSourcePlanet(ship, sourcePlanet);
                    }
                }
                else
                {
                    EnsureIgnoredShipCollisions(ship);

                    if (CanDeliverLoadToShip(myPos, ship) && HasBriefTravelBeforeLoad(myPos))
                    {
                        StopTransportMotion(myPos);
                        TryDeliverLoadToShip(ship);
                    }
                    else
                    {
                        Vector3 shipTarget = GetShipMagnetTarget(ship, myPos);
                        ApplyMagnetVelocity(myPos, shipTarget);
                    }
                }

                TryDestroyOnForeignPlanetSurface(myPos, null);
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                if (planet == null) return;

                if (TryResolveShip(spawningShipId.Value, out Starship unloadShip))
                    EnsureIgnoredShipCollisions(unloadShip);

                Vector3 magnetTarget = GetSurfacePointToward(planet, myPos);
                ApplyMagnetVelocity(myPos, magnetTarget);

                if (CanCompleteUnloadDelivery(myPos, planet))
                    TryDeliverUnloadToPlanet(planet, nm);
                else
                    TryDestroyOnForeignPlanetSurface(myPos, planet);
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

        private static float GetShipHullRadiusXZ(Starship ship)
        {
            if (ship == null) return 1f;
            Collider shipCollider = ship.GetComponent<Collider>();
            if (shipCollider != null && shipCollider.enabled)
            {
                Vector3 e = shipCollider.bounds.extents;
                float colliderRadius = Mathf.Sqrt(e.x * e.x + e.z * e.z);
                if (colliderRadius > 0.01f)
                    return colliderRadius;
            }
            return 1f;
        }

        /// <summary>Approach point on the ship hull facing the projectile (not the ship center).</summary>
        private static Vector3 GetShipMagnetTarget(Starship ship, Vector3 fromWorldPos)
        {
            if (ship == null)
                return fromWorldPos;

            Vector3 shipCenter = GetShipWorldPosition(ship);
            Vector3 fromPos = fromWorldPos;
            fromPos.y = 0f;

            Vector3 toCenter = ToroidalMap.ToroidalDirection(fromPos, shipCenter);
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude < 0.0001f)
                return shipCenter;

            toCenter.Normalize();
            float hullRadius = GetShipHullRadiusXZ(ship);
            float inset = Mathf.Clamp(hullRadius * ShipHullMagnetInset, 0.05f, 0.45f);
            Vector3 hullPoint = shipCenter - toCenter * Mathf.Max(0.2f, hullRadius - inset);
            hullPoint.y = 0f;
            return hullPoint;
        }

        private float GetPeopleTransportWorldRadius()
        {
            SphereCollider sphere = GetComponent<SphereCollider>();
            float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.z);
            if (sphere != null)
                return Mathf.Max(0.12f, sphere.radius * scale);
            return 0.25f * scale;
        }

        private float GetShipLoadCollectDistance(Starship ship)
        {
            return Mathf.Max(ShipLoadCollectMinDistance, GetPeopleTransportWorldRadius() + ShipLoadCollectPadding);
        }

        private bool CanDeliverLoadToShip(Vector3 projectilePos, Starship ship)
        {
            if (ship == null) return false;
            Vector3 hullPoint = GetShipMagnetTarget(ship, projectilePos);
            float distToHull = ToroidalMap.ToroidalDistance(projectilePos, hullPoint);
            return distToHull <= GetShipLoadCollectDistance(ship);
        }

        /// <summary>Short anti-pop-in gate only; full cinematic travel time does not block close-range pickup.</summary>
        private bool HasBriefTravelBeforeLoad(Vector3 projectilePos)
        {
            if (Time.time - serverSpawnTime < LoadDeliveryMinSeconds)
                return false;
            return ToroidalMap.ToroidalDistance(projectilePos, serverSpawnPosition) >= LoadDeliveryMinSpawnDistance;
        }

        private void StopTransportMotion(Vector3 holdPos)
        {
            holdPos.y = 0f;
            if (rb == null) return;
            rb.linearVelocity = Vector3.zero;
            rb.MovePosition(holdPos);
            syncedPlanarVelocity.Value = Vector3.zero;
        }

        private void EnsureIgnoredShipCollisions(Starship ship)
        {
            if (ship == null) return;
            var shipNo = ship.GetComponent<NetworkObject>();
            if (shipNo == null || shipNo.NetworkObjectId == ignoredCollisionShipNetworkId)
                return;

            Collider transportCollider = GetComponent<Collider>();
            if (transportCollider == null) return;

            Collider[] shipColliders = ship.GetComponentsInChildren<Collider>();
            for (int i = 0; i < shipColliders.Length; i++)
            {
                Collider shipCol = shipColliders[i];
                if (shipCol == null || shipCol == transportCollider) continue;
                Physics.IgnoreCollision(transportCollider, shipCol, true);
            }

            ignoredCollisionShipNetworkId = shipNo.NetworkObjectId;
        }

        private void ApplyMagnetVelocity(Vector3 myPos, Vector3 targetPos)
        {
            if (rb == null) return;
            Vector3 vel = ComputeMagnetVelocity(myPos, targetPos, rb.linearVelocity, Time.fixedDeltaTime);
            if (rb.isKinematic)
            {
                Vector3 next = myPos + vel * Time.fixedDeltaTime;
                next.y = 0f;
                rb.MovePosition(next);
            }
            else
                rb.linearVelocity = vel;
            rb.linearDamping = 0f;
        }

        private float GetCruiseSpeed()
        {
            return magnetCruiseSpeed.Value > 0.01f ? magnetCruiseSpeed.Value : magnetSpeed;
        }

        private float GetCloseRangeSpeed()
        {
            return GetCruiseSpeed() * MagnetCloseRangeSpeedRatio;
        }

        private static Vector3 ComputeMagnetVelocity(
            Vector3 myPos,
            Vector3 targetPos,
            Vector3 currentVel,
            float dt,
            float cruiseSpeed,
            float closeRangeSpeed)
        {
            myPos.y = 0f;
            targetPos.y = 0f;
            Vector3 toTarget = ToroidalMap.ToroidalDirection(myPos, targetPos);
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) toTarget = Vector3.forward;
            else toTarget.Normalize();

            float dist = ToroidalMap.ToroidalDistance(myPos, targetPos);
            float speed = dist <= magnetCloseRangeWorld ? closeRangeSpeed : cruiseSpeed;
            Vector3 targetVel = toTarget * speed;
            return Vector3.MoveTowards(currentVel, targetVel, speed * dt * 4f);
        }

        private Vector3 ComputeMagnetVelocity(Vector3 myPos, Vector3 targetPos, Vector3 currentVel, float dt)
        {
            return ComputeMagnetVelocity(myPos, targetPos, currentVel, dt, GetCruiseSpeed(), GetCloseRangeSpeed());
        }

        /// <summary>Point on the planet hull facing a world position (uses gameplay center, not display-tile transform).</summary>
        public static Vector3 GetSurfacePointToward(Planet planet, Vector3 fromWorldPos)
        {
            if (planet == null)
                return fromWorldPos;

            Vector3 planetPos = planet.GetOrbitGameplayCenterWorld();
            Vector3 fromPos = fromWorldPos;
            fromPos.y = 0f;
            fromPos = ToroidalMap.WrapPosition(fromPos);

            Vector3 toCore = ToroidalMap.ToroidalDirection(fromPos, planetPos);
            toCore.y = 0f;
            if (toCore.sqrMagnitude < 0.0001f)
                return planetPos;

            toCore.Normalize();
            float surfaceWorld = planet.PlanetSize * 0.5f;
            Vector3 surface = planetPos - toCore * surfaceWorld;
            surface.y = 0f;
            return surface;
        }

        /// <summary>Surface spawn point nudged outward toward <paramref name="towardWorldPos"/>.</summary>
        public static Vector3 GetSurfaceSpawnPointToward(Planet planet, Vector3 towardWorldPos)
        {
            Vector3 surface = GetSurfacePointToward(planet, towardWorldPos);
            if (planet == null)
                return surface;

            Vector3 planetCenter = planet.GetOrbitGameplayCenterWorld();
            Vector3 outward = ToroidalMap.ToroidalDirection(planetCenter, surface);
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.0001f)
                outward = ToroidalMap.ToroidalDirection(surface, towardWorldPos);

            float nudge = Mathf.Max(SurfaceSpawnOutwardNudge, planet.PlanetSize * 0.045f);
            surface += outward.normalized * nudge;
            surface.y = 0f;
            return surface;
        }

        /// <summary>Unload spawn on the ship hull facing the target planet (not ship center).</summary>
        public static Vector3 GetShipUnloadSpawnPointToward(Starship ship, Vector3 towardWorldPos)
        {
            if (ship == null)
                return towardWorldPos;

            Vector3 shipCenter = GetShipWorldPosition(ship);
            towardWorldPos.y = 0f;
            Vector3 outward = ToroidalMap.ToroidalDirection(shipCenter, towardWorldPos);
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.0001f)
                outward = Vector3.forward;
            else
                outward.Normalize();

            float hullRadius = GetShipHullRadiusXZ(ship);
            float nudge = Mathf.Max(0.08f, hullRadius * 0.06f);
            Vector3 spawn = shipCenter + outward * (hullRadius + nudge);
            spawn.y = 0f;
            return spawn;
        }

        public static bool TryResolveShip(ulong shipNetworkObjectId, out Starship ship)
        {
            ship = null;
            if (shipNetworkObjectId == 0) return false;

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.SpawnManager.SpawnedObjects.TryGetValue(shipNetworkObjectId, out NetworkObject shipObj))
            {
                ship = shipObj.GetComponent<Starship>();
                if (ship != null) return true;
            }

            return false;
        }

        public static bool TryResolvePlanet(ulong planetNetworkObjectId, out Planet planet)
        {
            planet = null;
            if (planetNetworkObjectId == 0) return false;

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.SpawnManager.SpawnedObjects.TryGetValue(planetNetworkObjectId, out NetworkObject planetObj))
            {
                planet = planetObj.GetComponent<Planet>();
                if (planet != null) return true;
            }

            for (int i = 0; i < Planet.AllPlanets.Count; i++)
            {
                Planet candidate = Planet.AllPlanets[i];
                if (candidate == null) continue;
                var candidateNo = candidate.GetComponent<NetworkObject>();
                if (candidateNo != null && candidateNo.NetworkObjectId == planetNetworkObjectId)
                {
                    planet = candidate;
                    return true;
                }
            }

            return false;
        }

        private bool TryGetSourcePlanet(out Planet planet)
        {
            return TryResolvePlanet(sourcePlanetId.Value, out planet);
        }

        /// <summary>Geometry-based: ship must sit in the source planet orbit ring (not only cached <see cref="Starship.CurrentOrbitPlanet"/>).</summary>
        private bool IsShipEligibleForLoadFromSourcePlanet(Starship ship)
        {
            if (ship == null || sourcePlanetId.Value == 0) return false;
            if (!TryGetSourcePlanet(out Planet sourcePlanet)) return false;

            Vector3 rbPos = GetShipWorldPosition(ship);
            Vector3 transformPos = ship.transform.position;
            transformPos.y = 0f;
            return sourcePlanet.IsWorldPositionInOrbitRingRelaxed(rbPos, 0.12f)
                || sourcePlanet.IsWorldPositionInOrbitRingRelaxed(transformPos, 0.12f);
        }

        private static bool IsWithinPlanetSurfaceReach(Planet planet, Vector3 worldPos)
        {
            if (planet == null) return false;
            worldPos.y = 0f;
            float surfaceWorld = planet.PlanetSize * 0.5f;
            float slop = Mathf.Max(PlanetSurfaceReachSlopMinWorld, surfaceWorld * PlanetSurfaceReachSlopFraction);
            Vector3 surfaceTarget = GetSurfacePointToward(planet, worldPos);
            return ToroidalMap.ToroidalDistance(worldPos, surfaceTarget) <= slop;
        }

        private bool CanCompleteReturnToSourcePlanet(Vector3 projectilePos, Planet sourcePlanet)
        {
            if (sourcePlanet == null) return false;
            if (!HasMinVisualTravel(projectilePos)) return false;
            return IsWithinPlanetSurfaceReach(sourcePlanet, projectilePos);
        }

        /// <summary>Load sphere returns to planet when the ship leaves orbit; restores population and releases in-transit.</summary>
        private void TryCompleteReturnToSourcePlanet(Starship ship, Planet sourcePlanet)
        {
            if (!IsServer || amount.Value <= 0f || sourcePlanet == null) return;

            float refundAmount = amount.Value;
            sourcePlanet.AddPopulationFromServer(refundAmount, SourceTeam);
            if (ship != null)
                ship.ReleasePeopleInTransit(refundAmount);

            DespawnProjectile(successfulDelivery: true);
        }

        private bool HasMinVisualTravel(Vector3 projectilePos)
        {
            if (Time.time - serverSpawnTime < EffectiveVisualTravelSeconds)
                return false;
            return ToroidalMap.ToroidalDistance(projectilePos, serverSpawnPosition) >= MinVisualTravelDistance;
        }

        private float ComputeTravelDistanceToTarget()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetObj))
                return MinVisualTravelDistance;

            Vector3 targetPos;
            if (isLoad.Value)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                if (ship == null || ship.IsDead)
                    return MinVisualTravelDistance;
                targetPos = GetShipMagnetTarget(ship, serverSpawnPosition);
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                if (planet == null)
                    return MinVisualTravelDistance;
                targetPos = GetSurfacePointToward(planet, serverSpawnPosition);
            }

            return Mathf.Max(MinVisualTravelDistance, ToroidalMap.ToroidalDistance(serverSpawnPosition, targetPos));
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
            if (Time.time - serverSpawnTime < UnloadDeliveryMinSeconds)
                return false;
            if (ToroidalMap.ToroidalDistance(projectilePos, serverSpawnPosition) < UnloadDeliveryMinTravelDistance)
                return false;
            return IsWithinPlanetSurfaceReach(planet, projectilePos);
        }

        private bool CanDestroyOnForeignPlanetSurface(Vector3 projectilePos, Planet planet)
        {
            if (planet == null) return false;
            if (Time.time - serverSpawnTime < ForeignPlanetImpactMinSeconds)
                return false;
            return IsWithinPlanetSurfaceReach(planet, projectilePos);
        }

        /// <summary>Despawn when an unload sphere hits a planet that is not its target (or any non-source planet during load).</summary>
        private void TryDestroyOnForeignPlanetSurface(Vector3 projectilePos, Planet intendedTargetPlanet)
        {
            if (!IsServer || amount.Value <= 0f) return;

            for (int i = 0; i < Planet.AllPlanets.Count; i++)
            {
                Planet candidate = Planet.AllPlanets[i];
                if (candidate == null || candidate == intendedTargetPlanet) continue;

                if (isLoad.Value && TryGetSourcePlanet(out Planet sourcePlanet) && candidate == sourcePlanet)
                    continue;

                if (!CanDestroyOnForeignPlanetSurface(projectilePos, candidate))
                    continue;

                DestroyOnForeignPlanetSurface();
                return;
            }
        }

        /// <summary>Visual cleanup on wrong planet; do not refund hostile invasion already applied to the intended target.</summary>
        private void DestroyOnForeignPlanetSurface()
        {
            if (!IsServer || amount.Value <= 0f) return;

            hostileInvasionAppliedOnSpawn.Value = false;
            amount.Value = 0f;
            var no = GetComponent<NetworkObject>();
            if (no != null)
                no.Despawn();
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

            DespawnProjectile(successfulDelivery: true);
        }

        private void TryDeliverUnloadToPlanet(Planet planet, NetworkManager nm)
        {
            if (!IsServer || amount.Value <= 0f || planet == null) return;

            bool sameTeamPlanet = IsSameTeamPlanet(planet);

            // Friendly reinforce unload: apply when the projectile reaches the planet.
            // Hostile invasion: population is applied when the ship spawns the projectile.
            if (sameTeamPlanet)
                planet.AddPopulationFromServer(amount.Value, (TeamManager.Team)team.Value);

            hostileInvasionAppliedOnSpawn.Value = false;
            DespawnProjectile(successfulDelivery: true);
        }

        /// <summary>Server-only damage from hostile weapons. Same-team shots are ignored.</summary>
        public void ApplyDamageFromBulletServer(float damage, TeamManager.Team attackerTeam, Vector3 impactWorldPos)
        {
            if (!IsServer || amount.Value <= 0f || damage <= 0f) return;
            if (attackerTeam == TeamManager.Team.None || attackerTeam == SourceTeam)
                return;

            health.Value = Mathf.Max(0f, health.Value - damage);
            if (health.Value <= 0f)
                DestroyFromDamage(impactWorldPos, attackerTeam);
        }

        private void DestroyFromDamage(Vector3 impactWorldPos, TeamManager.Team attackerTeam)
        {
            if (!IsServer || amount.Value <= 0f) return;

            RefundDestroyedTransport();
            amount.Value = 0f;
            hostileInvasionAppliedOnSpawn.Value = false;

            var no = GetComponent<NetworkObject>();
            if (no != null)
                no.Despawn();
        }

        private void RefundDestroyedTransport()
        {
            if (!IsServer || amount.Value <= 0f) return;

            float refundAmount = amount.Value;
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            if (isLoad.Value)
            {
                if (nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject shipObj))
                {
                    var ship = shipObj.GetComponent<Starship>();
                    if (ship != null)
                        ship.ReleasePeopleInTransit(refundAmount);
                }

                if (sourcePlanetId.Value != 0
                    && nm.SpawnManager.SpawnedObjects.TryGetValue(sourcePlanetId.Value, out NetworkObject planetObj))
                {
                    var planet = planetObj.GetComponent<Planet>();
                    if (planet != null)
                        planet.AddPopulationFromServer(refundAmount, SourceTeam);
                }
                return;
            }

            if (hostileInvasionAppliedOnSpawn.Value
                && nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetPlanetObj))
            {
                var planet = targetPlanetObj.GetComponent<Planet>();
                if (planet != null)
                    planet.RevertHostileUnloadImpactFromServer(refundAmount);
                return;
            }

            if (spawningShipId.Value != 0
                && nm.SpawnManager.SpawnedObjects.TryGetValue(spawningShipId.Value, out NetworkObject shipSpawnerObj))
            {
                var ship = shipSpawnerObj.GetComponent<Starship>();
                if (ship != null)
                    ship.AddPeopleFromServer(refundAmount);
            }
        }

        private void DespawnProjectile(bool successfulDelivery = false)
        {
            if (!successfulDelivery && IsServer && amount.Value > 0f)
                RefundDestroyedTransport();

            amount.Value = 0f;
            hostileInvasionAppliedOnSpawn.Value = false;
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

                var nm = NetworkManager.Singleton;

                ConfigureTransportRigidbody();
                if (rb != null)
                {
                    serverSpawnPosition = rb.position;
                }
                else
                    serverSpawnPosition = transform.position;
                serverSpawnPosition.y = 0f;
                serverSpawnTime = Time.time;
                ApplyVisualScaleFromAmount(peopleAmount);

                if (loadingFromPlanet && nm != null
                    && nm.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkObjectId, out NetworkObject shipObj))
                {
                    var spawnShip = shipObj.GetComponent<Starship>();
                    if (spawnShip != null)
                    {
                        EnsureIgnoredShipCollisions(spawnShip);
                        if (TryGetSourcePlanet(out Planet sourcePlanetForSpawn) && rb != null)
                        {
                            Vector3 shipPos = GetShipWorldPosition(spawnShip);
                            Vector3 surfaceSpawn = GetSurfaceSpawnPointToward(sourcePlanetForSpawn, shipPos);
                            rb.position = surfaceSpawn;
                            transform.position = surfaceSpawn;
                            serverSpawnPosition = surfaceSpawn;
                            serverSpawnPosition.y = 0f;
                        }
                    }
                }
                else if (!loadingFromPlanet && rb != null
                    && TryResolveShip(spawningShipId.Value, out Starship unloadShip)
                    && nm != null
                    && nm.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkObjectId, out NetworkObject planetObj))
                {
                    var targetPlanet = planetObj.GetComponent<Planet>();
                    if (targetPlanet != null)
                    {
                        EnsureIgnoredShipCollisions(unloadShip);
                        Vector3 planetSurface = GetSurfacePointToward(targetPlanet, GetShipWorldPosition(unloadShip));
                        Vector3 hullSpawn = GetShipUnloadSpawnPointToward(unloadShip, planetSurface);
                        rb.position = hullSpawn;
                        transform.position = hullSpawn;
                        serverSpawnPosition = hullSpawn;
                        serverSpawnPosition.y = 0f;
                    }
                }

                float travelDist = ComputeTravelDistanceToTarget();
                magnetCruiseSpeed.Value = Mathf.Max(0.08f, travelDist / EffectiveVisualTravelSeconds);
                if (loadingFromPlanet)
                    magnetCruiseSpeed.Value *= LoadMagnetSpeedMultiplier;

                if (!loadingFromPlanet && rb != null
                    && nm != null
                    && nm.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkObjectId, out NetworkObject planetObjForVel))
                {
                    var planetForVel = planetObjForVel.GetComponent<Planet>();
                    if (planetForVel != null)
                    {
                        Vector3 planetTarget = GetSurfacePointToward(planetForVel, serverSpawnPosition);
                        Vector3 dir = ToroidalMap.ToroidalDirection(serverSpawnPosition, planetTarget);
                        dir.y = 0f;
                        if (dir.sqrMagnitude > 0.0001f)
                        {
                            dir.Normalize();
                            Vector3 vel = dir * (magnetCruiseSpeed.Value * 0.35f);
                            rb.linearVelocity = vel;
                        }
                    }
                }

                float maxHp = Mathf.Max(HealthPerPerson, peopleAmount * HealthPerPerson);
                health.Value = maxHp;

                if (!loadingFromPlanet && targetNetworkObjectId != 0 && nm != null
                    && nm.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkObjectId, out NetworkObject targetObj))
                {
                    var planet = targetObj.GetComponent<Planet>();
                    hostileInvasionAppliedOnSpawn.Value = planet != null && !IsSameTeamPlanet(planet);
                }
                else
                    hostileInvasionAppliedOnSpawn.Value = false;

                Vector3 initVel = rb != null ? rb.linearVelocity : Vector3.zero;
                initVel.y = 0f;
                syncedPlanarVelocity.Value = initVel;
            }
        }

        public override void OnNetworkDespawn()
        {
            amount.OnValueChanged -= OnAmountChanged;
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

            Vector3 myPos = rb != null ? rb.position : transform.position;
            myPos.y = 0f;
            Planet hitPlanet = other.GetComponent<Planet>() ?? other.GetComponentInParent<Planet>();

            if (isLoad.Value)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                Starship hitShip = other.GetComponent<Starship>() ?? other.GetComponentInParent<Starship>();
                if (ship != null && hitShip == ship
                    && IsShipEligibleForLoadFromSourcePlanet(ship)
                    && CanDeliverLoadToShip(myPos, ship)
                    && HasBriefTravelBeforeLoad(myPos))
                    TryDeliverLoadToShip(ship);

                if (TryGetSourcePlanet(out Planet sourcePlanet)
                    && hitPlanet == sourcePlanet
                    && CanCompleteReturnToSourcePlanet(myPos, sourcePlanet))
                    TryCompleteReturnToSourcePlanet(ship, sourcePlanet);

                if (hitPlanet != null
                    && TryGetSourcePlanet(out Planet sourcePlanetForImpact)
                    && hitPlanet != sourcePlanetForImpact)
                    DestroyOnForeignPlanetSurface();
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                if (planet != null && hitPlanet == planet && CanCompleteUnloadDelivery(myPos, planet))
                    TryDeliverUnloadToPlanet(planet, nm);
                else if (hitPlanet != null && hitPlanet != planet)
                    DestroyOnForeignPlanetSurface();
            }
        }

        private void OnTriggerStay(Collider other)
        {
            if (!IsServer || amount.Value <= 0f) return;
            OnTriggerEnter(other);
        }
    }
}
