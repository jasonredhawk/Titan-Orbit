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
    /// Owns people-transport GameObject VFX (load / unload).
    /// <para>
    /// Server remains authoritative: non-ghost transport entities move, take bullet hits, and
    /// deliver people. This driver only Instantiates cosmetic spheres and mirrors
    /// <see cref="PeopleTransportPoseRpc"/> (same positions the server uses for combat).
    /// Between pose RPCs the sphere dead-reckons with the last server velocity — it does
    /// <b>not</b> independently magnet-chase a local ship pose (that caused Windows clients to
    /// fly toward a stale orbit point).
    /// </para>
    /// <para>
    /// Windows-safe: no map-body <c>ToEntityArray</c>, Instantiates 1/frame after settle.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66200)]
    public class PeopleTransportVfxDriver : MonoBehaviour
    {
        /// <summary>One active cosmetic flight keyed by server <see cref="PeopleTransportState.Sequence"/>.</summary>
        struct Flight
        {
            public GameObject Go;
            public uint Sequence;
            public float3 LogicalPos;
            public float3 Velocity;
            public byte IsLoad;
            public int SourcePlanetId;
            public int TargetPlanetId;
            public float Amount;
            public byte Team;
            public float RemainingLifetime;
            public int TileK;
            public int TileM;
            public bool LeavePopupShown;
            /// <summary>True after at least one Active pose RPC — suppress local arrive guesses.</summary>
            public bool HasServerPose;
        }

        const float LiftY = 1.0f;
        /// <summary>Safety despawn if pose RPCs stop (disconnect / lost end packet).</summary>
        const float MaxLifetimeSeconds = 20f;
        /// <summary>Windows: Instantiates 1/frame — same discipline as GhostSpawn Instantiates cap.</summary>
        const int MaxSpawnsPerFrame = 1;
        /// <summary>Blend toward server pose so packet jitter does not teleport the sphere.</summary>
        const float ServerPoseBlend = 0.65f;

        readonly List<Flight> _flights = new List<Flight>(32);
        readonly Dictionary<uint, int> _indexBySequence = new Dictionary<uint, int>(32);

        /// <summary>
        /// Pose/end RPCs that arrived before the Instantiates budget created the GO (1/frame).
        /// Applied as soon as the matching Sequence spawns.
        /// </summary>
        readonly Dictionary<uint, PeopleTransportVfxBridge.PoseUpdate> _pendingPoses =
            new Dictionary<uint, PeopleTransportVfxBridge.PoseUpdate>(16);

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
            _indexBySequence.Clear();
            _pendingPoses.Clear();
        }

        /// <summary>
        /// LateUpdate: drain spawns/poses, dead-reckon, place GOs. Never Instantiates in onBeforeRender.
        /// </summary>
        void LateUpdate()
        {
            if (_lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;

            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            // [TITAN-ORBIT] Instantiates GO flights only when Settling AND GhostSpawnBacklog are
            // clear — TeamChoice ship Instantiates keeps Settling OFF; backlog covers that window.
            if (!ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                DrainSpawns();

            DrainPoses();

            if (_flights.Count == 0)
                return;

            float dt = math.min(0.05f, math.max(0f, Time.deltaTime));
            float mapW = math.max(100f, ToroidalMapEcs.MapWidth);
            float mapH = math.max(100f, ToroidalMapEcs.MapHeight);

            if (!ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
                reference = Vector3.zero;

            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];
                if (f.Go == null)
                {
                    RemoveFlightAt(i);
                    continue;
                }

                // --- Lifetime safety (server end RPC is the real despawn) ---
                f.RemainingLifetime -= dt;
                if (f.RemainingLifetime <= 0f)
                {
                    DestroyFlightAt(i, showArrivePopup: false);
                    continue;
                }

                // --- Dead-reckon with last server velocity between pose RPCs ---
                f.LogicalPos += f.Velocity * dt;
                f.LogicalPos.y = 0f;

                // --- Display unwrap (cosmetic only) ---
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

            RebuildSequenceIndex();
        }

        /// <summary>Applies queued server pose / end updates (authoritative).</summary>
        void DrainPoses()
        {
            while (PeopleTransportVfxBridge.TryDequeuePose(out var pose))
            {
                if (pose.Sequence == 0)
                    continue;

                if (!_indexBySequence.TryGetValue(pose.Sequence, out int index) ||
                    index < 0 || index >= _flights.Count ||
                    _flights[index].Sequence != pose.Sequence)
                {
                    // Instantiates lag (1/frame) — keep latest pose until the GO exists.
                    _pendingPoses[pose.Sequence] = pose;
                    continue;
                }

                ApplyPoseToFlight(index, in pose);
            }
        }

        /// <summary>Applies any buffered pose for a Sequence that just Instantiated.</summary>
        void ApplyPendingPoseForSequence(uint sequence)
        {
            if (sequence == 0 || !_pendingPoses.TryGetValue(sequence, out var pose))
                return;

            _pendingPoses.Remove(sequence);
            if (!_indexBySequence.TryGetValue(sequence, out int index))
                return;
            ApplyPoseToFlight(index, in pose);
        }

        /// <summary>Snaps / ends one flight from an authoritative pose update.</summary>
        void ApplyPoseToFlight(int index, in PeopleTransportVfxBridge.PoseUpdate pose)
        {
            if (index < 0 || index >= _flights.Count)
                return;

            var f = _flights[index];
            if (f.Sequence != pose.Sequence)
                return;

            if (pose.Status == PeopleTransportPoseStatus.Consumed)
            {
                if (f.Go != null)
                    ShowPeoplePopupAt(f.Go.transform.position, f.Amount, (TeamId)f.Team, in f);
                DestroyFlightAt(index, showArrivePopup: false);
                return;
            }

            if (pose.Status == PeopleTransportPoseStatus.Destroyed)
            {
                DestroyFlightAt(index, showArrivePopup: false);
                return;
            }

            // --- Active: snap / blend to server combat position ---
            float3 serverPos = pose.Position;
            serverPos.y = 0f;
            if (f.HasServerPose)
                f.LogicalPos = math.lerp(f.LogicalPos, serverPos, ServerPoseBlend);
            else
                f.LogicalPos = serverPos;

            f.Velocity = pose.Velocity;
            f.Velocity.y = 0f;
            f.HasServerPose = true;
            _flights[index] = f;
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

                var flight = new Flight
                {
                    Go = go,
                    Sequence = req.Sequence,
                    LogicalPos = spawn,
                    Velocity = req.Velocity,
                    IsLoad = req.IsLoad,
                    SourcePlanetId = req.SourcePlanetId,
                    TargetPlanetId = req.TargetPlanetId,
                    Amount = math.max(1f, req.Amount),
                    Team = req.Team,
                    RemainingLifetime = MaxLifetimeSeconds,
                    TileK = int.MinValue,
                    TileM = int.MinValue,
                    LeavePopupShown = false,
                    HasServerPose = false,
                };

                // Leave popup: planet lost people (load) or ship lost people (unload).
                ShowPeoplePopupAt(displayPos, -flight.Amount, (TeamId)flight.Team, in flight);
                flight.LeavePopupShown = true;

                _flights.Add(flight);
                RebuildSequenceIndex();
                // Pose RPCs may have arrived while waiting on Instantiates=1/frame.
                ApplyPendingPoseForSequence(flight.Sequence);
            }

            RebuildSequenceIndex();
        }

        /// <summary>Rebuilds sequence → list index after add/remove.</summary>
        void RebuildSequenceIndex()
        {
            _indexBySequence.Clear();
            for (int i = 0; i < _flights.Count; i++)
            {
                uint seq = _flights[i].Sequence;
                if (seq != 0)
                    _indexBySequence[seq] = i;
            }
        }

        void DestroyFlightAt(int index, bool showArrivePopup)
        {
            var f = _flights[index];
            if (showArrivePopup && f.Go != null)
                ShowPeoplePopupAt(f.Go.transform.position, f.Amount, (TeamId)f.Team, in f);

            if (f.Go != null)
                Destroy(f.Go);
            RemoveFlightAt(index);
        }

        void RemoveFlightAt(int index)
        {
            _flights.RemoveAt(index);
            RebuildSequenceIndex();
        }

        /// <summary>
        /// Compact ±N near the transport. Nudges outside a nearby planet body when needed.
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
        /// center + radius so floating text parks in empty space.
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
            if (Vector3.Distance(tip, planetPos) > bodyRadius + 4f)
                return;

            avoidCenter = planetPos;
            avoidRadius = bodyRadius;
        }

        void ClearAllFlights()
        {
            for (int i = 0; i < _flights.Count; i++)
            {
                if (_flights[i].Go != null)
                    Destroy(_flights[i].Go);
            }

            _flights.Clear();
            _indexBySequence.Clear();
            _pendingPoses.Clear();
        }
    }
}
