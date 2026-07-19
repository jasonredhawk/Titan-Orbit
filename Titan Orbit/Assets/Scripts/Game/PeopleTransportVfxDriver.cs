using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Owns people-transport GameObject VFX (load planet→ship, unload ship→planet).
    /// <para>
    /// Instantiates proxies from <see cref="PeopleTransportVfxBridge"/> and magnet-steers them with
    /// toroidal display unwrap. Load flights re-resolve the destination ship every frame from live
    /// ECS pose; when the ship leaves the orbit ring (or thrusts/fires), the same sphere flies home
    /// to the source planet and can retarget the ship again if the player re-enters. Floating ±N
    /// popups spawn at the sphere leave and consume positions (not on the ship hull).
    /// </para>
    /// <para>
    /// Windows-safe: no map-body <c>ToEntityArray</c>, no <c>Application.onBeforeRender</c> Instantiates.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66200)]
    public class PeopleTransportVfxDriver : MonoBehaviour
    {
        /// <summary>One active cosmetic flight + its GameObject.</summary>
        struct Flight
        {
            public GameObject Go;
            public float3 LogicalPos;
            public float3 Velocity;
            public float3 TargetPos;
            public float Cruise;
            public byte IsLoad;
            public int TargetShipNetworkId;
            public int SourcePlanetId;
            public int TargetPlanetId;
            public float Amount;
            public byte Team;
            public float3 SpawnPos;
            public float RemainingLifetime;
            public int TileK;
            public int TileM;
        }

        const float MinTravelBeforeArrive = 0.4f;
        const float ArriveDistance = 0.55f;
        const float LiftY = 1.0f;

        /// <summary>Windows: Instantiates 1/frame — same discipline as GhostSpawn Instantiates cap.</summary>
        const int MaxSpawnsPerFrame = 1;

        readonly List<Flight> _flights = new List<Flight>(32);

        /// <summary>
        /// Per-LateUpdate cache of ship poses by <see cref="GhostOwner.NetworkId"/>.
        /// Avoids repeating the tiny ship query once per in-flight load sphere.
        /// </summary>
        readonly Dictionary<int, LocalTransform> _shipPoseByNetworkId = new Dictionary<int, LocalTransform>(8);

        int _lastTickFrame = -1;

        /// <summary>[UNITY] Attach to session manager when the scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<PeopleTransportVfxDriver>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
            {
                session.gameObject.AddComponent<PeopleTransportVfxDriver>();
                return;
            }

            var go = new GameObject("PeopleTransportVfxDriver");
            DontDestroyOnLoad(go);
            go.AddComponent<PeopleTransportVfxDriver>();
        }

        void OnDisable()
        {
            ClearAllFlights();
            PeopleTransportVfxBridge.Clear();
            _shipPoseByNetworkId.Clear();
        }

        /// <summary>
        /// LateUpdate only — never <c>onBeforeRender</c> (Instantiates during render crashed Windows).
        /// Drains spawn queue, live-retargets load magnets (ship ↔ home planet), steers, and places GO.
        /// </summary>
        void LateUpdate()
        {
            if (_lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;

            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            // Join settle: no Instantiates while GhostSpawn is still flooding.
            // TransformQuarantine stays on for the whole Windows session — Instantiates of map
            // bodies are forbidden there, but lightweight transport primitives are OK after settle.
            if (!ClientJoinSettleCache.Settling)
                DrainSpawns();

            if (_flights.Count == 0)
                return;

            // --- Frame setup ---
            float dt = math.min(0.05f, math.max(0f, Time.deltaTime));
            float mapW = math.max(100f, ToroidalMapEcs.MapWidth);
            float mapH = math.max(100f, ToroidalMapEcs.MapHeight);

            if (!ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
                reference = Vector3.zero;

            // [TITAN-ORBIT] Fresh ship poses each tick — baked RPC TargetPosition is spawn-time only.
            _shipPoseByNetworkId.Clear();

            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];
                if (f.Go == null)
                {
                    _flights.RemoveAt(i);
                    continue;
                }

                // --- Lifetime ---
                f.RemainingLifetime -= dt;
                if (f.RemainingLifetime <= 0f)
                {
                    DestroyFlightAt(i, showArrivePopup: false);
                    continue;
                }

                // --- Magnet target ---
                // Load: chase ship while eligible; otherwise fly home to source planet surface.
                // Unload: keep / refresh planet-surface TargetPos.
                float3 target = f.TargetPos;
                if (f.IsLoad != 0)
                    ResolveLoadMagnetTarget(ref f, mapW, mapH, out target);
                else
                    ResolveUnloadMagnetTarget(ref f, mapW, mapH, out target);

                f.TargetPos = target;

                // --- Steer + integrate (logical / unbounded XZ) ---
                float cruise = math.max(0.08f, f.Cruise);
                f.Velocity = PeopleTransportMath.SteerMagnetVelocity(
                    f.LogicalPos, target, f.Velocity, dt, cruise, mapW, mapH);
                if (math.lengthsq(f.Velocity) < 0.01f)
                    f.Velocity = ToroidalMapEcs.ToroidalDirection(f.LogicalPos, target, mapW, mapH) * cruise;

                f.LogicalPos += f.Velocity * dt;
                f.LogicalPos.y = 0f;

                // --- Display unwrap (cosmetic only — sim stays logical) ---
                int k = f.TileK;
                int m = f.TileM;
                float3 display = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                    f.LogicalPos, (float3)reference, ref k, ref m, mapW, mapH);
                f.TileK = k;
                f.TileM = m;
                display.y = LiftY;
                f.Go.transform.position = display;

                float3 flatVel = f.Velocity;
                flatVel.y = 0f;
                if (math.lengthsq(flatVel) > 0.01f)
                {
                    float3 forward = math.normalize(flatVel);
                    f.Go.transform.rotation = Quaternion.LookRotation(
                        new Vector3(forward.x, 0f, forward.z), Vector3.up);
                }

                // --- Arrive (consume / land / return home) ---
                float traveled = ToroidalMapEcs.ToroidalDistance(f.LogicalPos, f.SpawnPos, mapW, mapH);
                float dist = ToroidalMapEcs.ToroidalDistance(f.LogicalPos, target, mapW, mapH);
                if (traveled >= MinTravelBeforeArrive && dist <= ArriveDistance)
                {
                    _flights[i] = f;
                    DestroyFlightAt(i, showArrivePopup: true);
                    continue;
                }

                _flights[i] = f;
            }
        }

        /// <summary>
        /// Load magnet: ship hull while eligible; source planet surface when the ship leaves orbit
        /// (or thrusts/fires). Same sphere can flip destination again if the ship returns.
        /// </summary>
        void ResolveLoadMagnetTarget(ref Flight f, float mapW, float mapH, out float3 target)
        {
            target = f.TargetPos;

            bool shipEligible = false;
            bool knowEligibility = f.TargetShipNetworkId != 0 &&
                                   f.SourcePlanetId != 0 &&
                                   EcsGameBridge.TryIsShipEligibleForPeopleLoad(
                                       f.TargetShipNetworkId, f.SourcePlanetId, out shipEligible);

            float3 liveShipTarget = default;
            bool haveShipMagnet = f.TargetShipNetworkId != 0 &&
                                  TryGetLoadMagnetTarget(f.TargetShipNetworkId, f.LogicalPos, mapW, mapH,
                                      out liveShipTarget);

            // Known eligible → chase ship. Unknown (lookup miss) → keep chasing if we can see the hull
            // so a single failed query does not falsely send every sphere home.
            if (haveShipMagnet && (!knowEligibility || shipEligible))
            {
                target = liveShipTarget;
                return;
            }

            // Known ineligible (left ring / thrusting / firing) — steer home; can retarget later.
            if (f.SourcePlanetId != 0 &&
                EcsGameBridge.TryGetPlanetPoseByPlanetId(f.SourcePlanetId, out float3 planetPos, out float planetScale, out _))
            {
                float planetSize = math.max(0.5f, planetScale);
                target = PeopleTransportMath.GetPlanetSurfaceToward(
                    planetPos, planetSize, f.LogicalPos, mapW, mapH);
            }
        }

        /// <summary>Unload magnet: live planet surface toward the sphere (baked TargetPos as fallback).</summary>
        void ResolveUnloadMagnetTarget(ref Flight f, float mapW, float mapH, out float3 target)
        {
            target = f.TargetPos;
            int planetId = f.TargetPlanetId != 0 ? f.TargetPlanetId : f.SourcePlanetId;
            if (planetId == 0)
                return;

            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out float3 planetPos, out float planetScale, out _))
                return;

            float planetSize = math.max(0.5f, planetScale);
            target = PeopleTransportMath.GetPlanetSurfaceToward(
                planetPos, planetSize, f.LogicalPos, mapW, mapH);
        }

        /// <summary>
        /// Resolves the current magnet point on the destination ship hull.
        /// </summary>
        bool TryGetLoadMagnetTarget(
            int targetShipNetworkId,
            float3 fromLogicalPos,
            float mapW,
            float mapH,
            out float3 magnetTarget)
        {
            magnetTarget = default;

            if (!TryGetCachedShipTransform(targetShipNetworkId, out LocalTransform shipLt))
                return false;

            float3 shipCenter = shipLt.Position;
            shipCenter.y = 0f;
            float hull = PeopleTransportMath.GetShipHullRadius(shipLt.Scale);
            magnetTarget = PeopleTransportMath.GetShipMagnetTarget(
                shipCenter, hull, fromLogicalPos, mapW, mapH);
            return true;
        }

        /// <summary>
        /// Returns a ship pose for <paramref name="networkId"/>, filling
        /// <see cref="_shipPoseByNetworkId"/> on first use this LateUpdate.
        /// <para>
        /// [TITAN-ORBIT] Local owner prefers <see cref="ShipDisplayPose"/> / hybrid hull — the same
        /// presentation pose the Windows client renders. Raw predicted <see cref="LocalTransform"/>
        /// can lag behind <see cref="ShipVisualSyncSystem"/> coast during NetCode reconcile, which
        /// made load spheres chase an old point on the orbit ring in player builds (Editor host
        /// rarely shows it because RTT/reconcile is tiny).
        /// </para>
        /// Remotes stay on ECS <see cref="LocalTransform"/> (logical); their hybrid hulls are
        /// toroidal display copies and must not feed magnet math.
        /// </summary>
        bool TryGetCachedShipTransform(int networkId, out LocalTransform shipLt)
        {
            if (_shipPoseByNetworkId.TryGetValue(networkId, out shipLt))
                return true;

            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            if (localNetworkId > 0 && networkId == localNetworkId)
            {
                // --- Local owner: presentation pose first (matches camera / hybrid hull) ---
                float scale = 1f;
                bool haveSimScale = EcsGameBridge.TryGetLocalShipTransform(out var simLt);
                if (haveSimScale)
                    scale = simLt.Scale;

                if (ShipDisplayPose.HasLocalPose)
                {
                    shipLt = LocalTransform.FromPositionRotationScale(
                        (float3)ShipDisplayPose.LocalPosition,
                        ShipDisplayPose.LocalRotation,
                        math.max(0.25f, scale));
                    _shipPoseByNetworkId[networkId] = shipLt;
                    return true;
                }

                // Hull registry is also presentation space for the local ship (unbounded logical).
                if (ShipWeaponProxyRegistry.TryGetHull(networkId, out Transform hull) && hull != null)
                {
                    if (!haveSimScale)
                    {
                        float presentationScale = math.max(0.0001f, hull.lossyScale.x);
                        scale = presentationScale / BodyCollisionMath.ShipPresentationScale;
                    }

                    shipLt = LocalTransform.FromPositionRotationScale(
                        (float3)hull.position,
                        hull.rotation,
                        math.max(0.25f, scale));
                    _shipPoseByNetworkId[networkId] = shipLt;
                    return true;
                }

                if (haveSimScale)
                {
                    shipLt = simLt;
                    _shipPoseByNetworkId[networkId] = shipLt;
                    return true;
                }
            }

            // --- Remotes / fallback: GhostOwner scan — ships only, not map bodies ---
            if (EcsGameBridge.TryGetShipSimTransformByNetworkId(networkId, out shipLt))
            {
                _shipPoseByNetworkId[networkId] = shipLt;
                return true;
            }

            shipLt = default;
            return false;
        }

        /// <summary>Budgeted Instantiates from the VFX bridge; shows leave (−N) at spawn.</summary>
        void DrainSpawns()
        {
            int spawnedThisFrame = 0;
            while (spawnedThisFrame < MaxSpawnsPerFrame &&
                   PeopleTransportVfxBridge.TryDequeue(out var req))
            {
                float3 spawn = req.SpawnPosition;
                spawn.y = 0f;
                float3 target = req.TargetPosition;
                target.y = 0f;

                var go = PeopleTransportVisualApplier.CreateVisual(
                    null,
                    math.max(1f, req.Amount),
                    (TeamId)req.Team);
                if (go == null)
                    continue;

                go.name = req.IsLoad != 0 ? "PeopleTransportProxy_Load" : "PeopleTransportProxy_Unload";
                spawnedThisFrame++;

                Vector3 displayPos;
                if (ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
                {
                    int k = int.MinValue;
                    int m = int.MinValue;
                    float3 display = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                        spawn, (float3)reference, ref k, ref m);
                    display.y = LiftY;
                    displayPos = display;
                    go.transform.position = displayPos;
                }
                else
                {
                    displayPos = new Vector3(spawn.x, LiftY, spawn.z);
                    go.transform.position = displayPos;
                }

                float cruise = req.CruiseSpeed > 0.01f
                    ? req.CruiseSpeed
                    : PeopleTransportMath.ComputeCruiseSpeed(
                        spawn, target, req.IsLoad != 0,
                        ToroidalMapEcs.MapWidth, ToroidalMapEcs.MapHeight);
                cruise = math.max(0.08f, cruise);

                var flight = new Flight
                {
                    Go = go,
                    LogicalPos = spawn,
                    Velocity = req.Velocity,
                    TargetPos = target,
                    Cruise = cruise,
                    IsLoad = req.IsLoad,
                    TargetShipNetworkId = req.TargetShipNetworkId,
                    SourcePlanetId = req.SourcePlanetId,
                    TargetPlanetId = req.TargetPlanetId,
                    Amount = math.max(1f, req.Amount),
                    Team = req.Team,
                    SpawnPos = spawn,
                    RemainingLifetime = PeopleTransportMath.EffectiveVisualTravelSeconds + 4f,
                    TileK = int.MinValue,
                    TileM = int.MinValue,
                };

                // Leave popup: planet lost people (load) or ship lost people (unload).
                ShowPeoplePopupAt(displayPos, -math.max(1f, req.Amount), (TeamId)req.Team, in flight);
                _flights.Add(flight);
            }
        }

        /// <summary>
        /// Destroys one flight GameObject. When <paramref name="showArrivePopup"/> is true, shows +N
        /// at the sphere (ship consume, planet land, or return-home refund).
        /// </summary>
        void DestroyFlightAt(int index, bool showArrivePopup)
        {
            var f = _flights[index];
            if (showArrivePopup && f.Go != null)
            {
                Vector3 pos = f.Go.transform.position;
                ShowPeoplePopupAt(pos, f.Amount, (TeamId)f.Team, in f);
            }

            if (f.Go != null)
                Destroy(f.Go);
            _flights.RemoveAt(index);
        }

        /// <summary>
        /// Shows a compact ±N people popup near the transport. When the sphere is close to a planet,
        /// nudges the popup outside the planet body so the mesh does not clip the text.
        /// </summary>
        void ShowPeoplePopupAt(Vector3 worldPosition, float signedAmount, TeamId team, in Flight flight)
        {
            if (WorldFloatingCountManager.Instance == null)
                return;

            var channel = flight.IsLoad != 0
                ? FloatingCountChannel.PeopleLoad
                : FloatingCountChannel.PeopleUnload;

            TryGetNearbyPlanetAvoidance(in flight, worldPosition, out Vector3 avoidCenter, out float avoidRadius);
            WorldFloatingCountManager.Instance.ShowFloatingCountAtWorldPosition(
                worldPosition, channel, signedAmount, team, avoidCenter, avoidRadius);
        }

        /// <summary>
        /// If the transport display point sits near a planet body, returns that planet's display
        /// center + radius so the floating-count manager can park the popup in empty space.
        /// </summary>
        static void TryGetNearbyPlanetAvoidance(
            in Flight flight,
            Vector3 transportDisplayPos,
            out Vector3 avoidCenter,
            out float avoidRadius)
        {
            avoidCenter = default;
            avoidRadius = 0f;

            int planetId = flight.IsLoad != 0
                ? flight.SourcePlanetId
                : (flight.TargetPlanetId != 0 ? flight.TargetPlanetId : flight.SourcePlanetId);
            if (planetId == 0)
                return;

            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(
                    planetId, out float3 logicalPlanet, out float planetScale, out _))
                return;

            // --- Display unwrap (same tile family as the transport GO) ---
            float3 planetDisplay = logicalPlanet;
            if (ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
            {
                int k = int.MinValue;
                int m = int.MinValue;
                planetDisplay = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                    logicalPlanet, (float3)reference, ref k, ref m);
            }

            float bodyRadius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetScale);
            Vector3 planetPos = new Vector3(planetDisplay.x, 0f, planetDisplay.z);
            Vector3 tip = transportDisplayPos;
            tip.y = 0f;
            float dist = Vector3.Distance(tip, planetPos);
            // Only nudge when the popup would otherwise sit inside / against the planet shell.
            if (dist > bodyRadius + 4f)
                return;

            avoidCenter = planetPos;
            avoidRadius = bodyRadius;
        }

        /// <summary>Destroys every active flight (leave match / disable).</summary>
        void ClearAllFlights()
        {
            for (int i = 0; i < _flights.Count; i++)
            {
                if (_flights[i].Go != null)
                    Destroy(_flights[i].Go);
            }

            _flights.Clear();
        }
    }
}
