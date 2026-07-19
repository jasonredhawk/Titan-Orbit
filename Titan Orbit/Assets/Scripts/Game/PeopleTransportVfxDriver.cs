using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
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
    /// ECS <see cref="LocalTransform"/> (not the spawn-time baked target) so floats follow a moving
    /// hull. Windows-safe: no map-body <c>ToEntityArray</c>, no <c>Application.onBeforeRender</c>
    /// Instantiates.
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
        /// Drains spawn queue, live-retargets load magnets to current ship pose, steers, and places GO.
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
                    DestroyFlightAt(i);
                    continue;
                }

                // --- Magnet target ---
                // Load: chase live ship hull (server StepTransportMotion does the same).
                // Unload: keep baked planet-surface TargetPos from spawn RPC.
                float3 target = f.TargetPos;
                if (f.IsLoad != 0 &&
                    f.TargetShipNetworkId != 0 &&
                    TryGetLoadMagnetTarget(f.TargetShipNetworkId, f.LogicalPos, mapW, mapH, out float3 liveTarget))
                {
                    target = liveTarget;
                    f.TargetPos = target;
                }

                // --- Steer + integrate (logical / unbounded XZ) ---
                float cruise = math.max(0.08f, f.Cruise);
                f.Velocity = PeopleTransportMath.SteerMagnetVelocity(
                    f.LogicalPos, target, f.Velocity, dt, cruise, mapW, mapH);
                if (math.lengthsq(f.Velocity) < 0.01f)
                    f.Velocity = ToroidalMapEcs.ToroidalDirection(f.LogicalPos, target, mapW, mapH) * cruise;

                f.LogicalPos += f.Velocity * dt;
                f.LogicalPos.y = 0f;

                // --- Arrive ---
                float traveled = ToroidalMapEcs.ToroidalDistance(f.LogicalPos, f.SpawnPos, mapW, mapH);
                float dist = ToroidalMapEcs.ToroidalDistance(f.LogicalPos, target, mapW, mapH);
                if (traveled >= MinTravelBeforeArrive && dist <= ArriveDistance)
                {
                    DestroyFlightAt(i);
                    continue;
                }

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

                _flights[i] = f;
            }
        }

        /// <summary>
        /// Resolves the current magnet point on the destination ship hull.
        /// Prefers predicted local-ship <see cref="LocalTransform"/> when this flight targets us;
        /// otherwise looks up any ship ghost by network id (tiny query, cached per frame).
        /// </summary>
        /// <param name="targetShipNetworkId">Load destination from spawn RPC / bridge.</param>
        /// <param name="fromLogicalPos">Transport logical position (for hull inset direction).</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="magnetTarget">Hull point to steer toward.</param>
        /// <returns>False when the ship ghost is missing — caller keeps baked TargetPos.</returns>
        bool TryGetLoadMagnetTarget(
            int targetShipNetworkId,
            float3 fromLogicalPos,
            float mapW,
            float mapH,
            out float3 magnetTarget)
        {
            magnetTarget = default;

            // --- Resolve live ship pose (cached) ---
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
        /// Returns a ship <see cref="LocalTransform"/> for <paramref name="networkId"/>, filling
        /// <see cref="_shipPoseByNetworkId"/> on first use this LateUpdate.
        /// </summary>
        bool TryGetCachedShipTransform(int networkId, out LocalTransform shipLt)
        {
            if (_shipPoseByNetworkId.TryGetValue(networkId, out shipLt))
                return true;

            // --- Local owner: predicted ClientWorld pose (freshest; matches delivery) ---
            // [TITAN-ORBIT] Do not use ShipDisplayPose here — soft-track / sticky HasLocalPose can
            // leave the magnet aimed at where the hull was, while the proxy has already moved.
            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            if (localNetworkId > 0 &&
                networkId == localNetworkId &&
                EcsGameBridge.TryGetLocalShipTransform(out shipLt))
            {
                _shipPoseByNetworkId[networkId] = shipLt;
                return true;
            }

            // --- Any ship (local fallback or remote): GhostOwner scan — ships only, not map bodies ---
            if (EcsGameBridge.TryGetShipSimTransformByNetworkId(networkId, out shipLt))
            {
                _shipPoseByNetworkId[networkId] = shipLt;
                return true;
            }

            shipLt = default;
            return false;
        }

        /// <summary>Budgeted Instantiates from the VFX bridge.</summary>
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

                if (ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
                {
                    int k = int.MinValue;
                    int m = int.MinValue;
                    float3 display = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                        spawn, (float3)reference, ref k, ref m);
                    display.y = LiftY;
                    go.transform.position = display;
                }
                else
                {
                    go.transform.position = new Vector3(spawn.x, LiftY, spawn.z);
                }

                float cruise = req.CruiseSpeed > 0.01f
                    ? req.CruiseSpeed
                    : PeopleTransportMath.ComputeCruiseSpeed(
                        spawn, target, req.IsLoad != 0,
                        ToroidalMapEcs.MapWidth, ToroidalMapEcs.MapHeight);
                cruise = math.max(0.08f, cruise);

                _flights.Add(new Flight
                {
                    Go = go,
                    LogicalPos = spawn,
                    Velocity = req.Velocity,
                    TargetPos = target,
                    Cruise = cruise,
                    IsLoad = req.IsLoad,
                    TargetShipNetworkId = req.TargetShipNetworkId,
                    SpawnPos = spawn,
                    RemainingLifetime = PeopleTransportMath.EffectiveVisualTravelSeconds + 4f,
                    TileK = int.MinValue,
                    TileM = int.MinValue,
                });
            }
        }

        /// <summary>Destroys one flight GameObject and removes it from the active list.</summary>
        void DestroyFlightAt(int index)
        {
            var f = _flights[index];
            if (f.Go != null)
                Destroy(f.Go);
            _flights.RemoveAt(index);
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
