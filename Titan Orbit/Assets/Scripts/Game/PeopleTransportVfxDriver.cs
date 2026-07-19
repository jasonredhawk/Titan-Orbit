using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Owns people-transport GameObject VFX (load planet→ship, unload ship→planet).
    /// <para>
    /// Instantiates proxies from <see cref="PeopleTransportVfxBridge"/> and magnet-steers them with
    /// toroidal display unwrap. Windows-safe: no ECS <c>ToEntityArray</c>, no
    /// <c>Application.onBeforeRender</c> Instantiates, no nested Spaceship prefab in player builds.
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
        }

        /// <summary>
        /// LateUpdate only — never <c>onBeforeRender</c> (Instantiates during render crashed Windows).
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

            float dt = math.min(0.05f, math.max(0f, Time.deltaTime));
            float mapW = math.max(100f, ToroidalMapEcs.MapWidth);
            float mapH = math.max(100f, ToroidalMapEcs.MapHeight);

            if (!ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
                reference = Vector3.zero;

            // Local ship presentation only — no EntityManager ToEntityArray (Windows Crash!!! risk).
            int localNetworkId = EcsGameBridge.GetLocalNetworkId();
            bool haveLocalShip = EcsGameBridge.TryGetLocalShipPresentationPosition(out Vector3 localShipPos);
            float3 localShip = haveLocalShip
                ? new float3(localShipPos.x, 0f, localShipPos.z)
                : float3.zero;

            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];
                if (f.Go == null)
                {
                    _flights.RemoveAt(i);
                    continue;
                }

                f.RemainingLifetime -= dt;
                if (f.RemainingLifetime <= 0f)
                {
                    DestroyFlightAt(i);
                    continue;
                }

                float3 target = f.TargetPos;
                if (f.IsLoad != 0 &&
                    f.TargetShipNetworkId != 0 &&
                    haveLocalShip &&
                    f.TargetShipNetworkId == localNetworkId)
                {
                    float hull = PeopleTransportMath.GetShipHullRadius(1f);
                    target = PeopleTransportMath.GetShipMagnetTarget(
                        localShip, hull, f.LogicalPos, mapW, mapH);
                    f.TargetPos = target;
                }

                float cruise = math.max(0.08f, f.Cruise);
                f.Velocity = PeopleTransportMath.SteerMagnetVelocity(
                    f.LogicalPos, target, f.Velocity, dt, cruise, mapW, mapH);
                if (math.lengthsq(f.Velocity) < 0.01f)
                    f.Velocity = ToroidalMapEcs.ToroidalDirection(f.LogicalPos, target, mapW, mapH) * cruise;

                f.LogicalPos += f.Velocity * dt;
                f.LogicalPos.y = 0f;

                float traveled = ToroidalMapEcs.ToroidalDistance(f.LogicalPos, f.SpawnPos, mapW, mapH);
                float dist = ToroidalMapEcs.ToroidalDistance(f.LogicalPos, target, mapW, mapH);
                if (traveled >= MinTravelBeforeArrive && dist <= ArriveDistance)
                {
                    DestroyFlightAt(i);
                    continue;
                }

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

        void DestroyFlightAt(int index)
        {
            var f = _flights[index];
            if (f.Go != null)
                Destroy(f.Go);
            _flights.RemoveAt(index);
        }

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
