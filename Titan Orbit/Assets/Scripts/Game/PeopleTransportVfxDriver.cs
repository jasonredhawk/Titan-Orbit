using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Owns people-transport GameObject VFX (load / unload) and the arrive transfer SFX.
    /// <para>
    /// Server remains authoritative: non-ghost transport entities move, take bullet hits, and
    /// deliver people. This driver Instantiates cosmetic spheres from
    /// <see cref="PeopleTransportSpawnRpc"/> and dead-reckons with spawn velocity.
    /// End-of-life (Consumed / Destroyed) still arrives as a one-off
    /// <see cref="PeopleTransportPoseRpc"/>. It does <b>not</b> independently magnet-chase a
    /// local ship pose (that caused Windows clients to fly toward a stale orbit point).
    /// </para>
    /// <para>
    /// On <see cref="PeopleTransportPoseStatus.Consumed"/> we play the people transfer one-shot
    /// through <see cref="AudioManager"/> (pitch scales with N / <see cref="Flight.Amount"/>),
    /// matching the legacy NGO ClientRpc timing that fired on delivery — not on spawn.
    /// </para>
    /// <para>
    /// [HYBRID] <see cref="CopyAimFlights"/> lets <see cref="PlanetaryDefenseVisualDriver"/>
    /// lead-aim cosmetic turrets at the same display pose + server velocity the spheres use —
    /// Dictionary/list walk only (no map-body ECS gathers).
    /// </para>
    /// <para>
    /// Windows-safe: no map-body <c>ToEntityArray</c>, Instantiates 1/frame after settle.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66200)]
    public class PeopleTransportVfxDriver : MonoBehaviour
    {
        /// <summary>
        /// One in-flight transport sample for cosmetic turret lead aim (presentation only).
        /// </summary>
        public struct AimFlightSample
        {
            /// <summary>Display-space world position of the VFX sphere (flattened to Y=0 for aim).</summary>
            public float3 DisplayPos;

            /// <summary>
            /// Last server planar velocity (world units/sec) — same vector used for dead-reckon
            /// and for <see cref="PlanetaryDefenseAimMath"/> lead on the client.
            /// </summary>
            public float3 Velocity;

            /// <summary>Owning team as byte (cast to <see cref="TeamId"/>).</summary>
            public byte Team;
        }

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
            public int TargetShipNetworkId;
            public float Amount;
            public byte Team;
            public float RemainingLifetime;
            public int TileK;
            public int TileM;
            public bool LeavePopupShown;
            /// <summary>True after the load flight turned around (ship left orbit) and +N was shown.</summary>
            public bool ReturnPopupShown;
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

        /// <summary>Singleton for other client bridges (turret aim) without FindObject each frame.</summary>
        static PeopleTransportVfxDriver s_Instance;

        readonly List<Flight> _flights = new List<Flight>(32);
        readonly Dictionary<uint, int> _indexBySequence = new Dictionary<uint, int>(32);

        /// <summary>
        /// Pose/end RPCs that arrived before the Instantiates budget created the GO (1/frame).
        /// Applied as soon as the matching Sequence spawns.
        /// </summary>
        readonly Dictionary<uint, PeopleTransportVfxBridge.PoseUpdate> _pendingPoses =
            new Dictionary<uint, PeopleTransportVfxBridge.PoseUpdate>(16);

        int _lastTickFrame = -1;

        /// <summary>Live driver instance, or null when disabled.</summary>
        public static PeopleTransportVfxDriver Active => s_Instance;

        /// <summary>
        /// Join-safe cosmetic spheres for bullet tracers. Transports are not client ghosts —
        /// this is the pose the observer actually sees.
        /// </summary>
        public static void AppendBulletObstacles(List<BulletCosmeticHitQuery.Obstacle> into)
        {
            if (into == null || s_Instance == null)
                return;

            var flights = s_Instance._flights;
            for (int i = 0; i < flights.Count; i++)
            {
                var f = flights[i];
                if (f.Amount <= 0.01f)
                    continue;
                into.Add(new BulletCosmeticHitQuery.Obstacle
                {
                    Kind = BulletCosmeticHitQuery.ObstacleKind.Transport,
                    SourceEntity = Entity.Null,
                    LogicalCenter = f.LogicalPos,
                    Radius = PeopleTransportMath.GetBulletHitRadius(PeopleTransportMath.TransportRadius),
                    TeamOrOwnership = f.Team,
                });
            }
        }

        /// <summary>
        /// Nearest live transport VFX transform to a logical point (join-safe list walk).
        /// </summary>
        public static bool TryGetNearestFlightTransform(
            float3 logicalPos,
            float maxDistance,
            out Transform root)
        {
            root = null;
            if (s_Instance == null)
                return false;

            float best = maxDistance * maxDistance;
            var flights = s_Instance._flights;
            for (int i = 0; i < flights.Count; i++)
            {
                var f = flights[i];
                if (f.Go == null || f.Amount <= 0.01f)
                    continue;
                float distSq = math.distancesq(f.LogicalPos, logicalPos);
                if (distSq < best)
                {
                    best = distSq;
                    root = f.Go.transform;
                }
            }

            return root != null;
        }

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

        /// <summary>[UNITY] Publish singleton so turret aim can find flights without a scene scan.</summary>
        void OnEnable()
        {
            s_Instance = this;
        }

        void OnDisable()
        {
            if (s_Instance == this)
                s_Instance = null;
            ClearAllFlights();
            PeopleTransportVfxBridge.Clear();
            _indexBySequence.Clear();
            _pendingPoses.Clear();
        }

        /// <summary>
        /// Copies active VFX flights for cosmetic planetary-defense lead aim.
        /// [HYBRID] List walk only — never queries ECS transport archetypes (Windows join-safe).
        /// </summary>
        /// <param name="dst">Cleared and filled with display pose + velocity + team.</param>
        public void CopyAimFlights(List<AimFlightSample> dst)
        {
            if (dst == null)
                return;
            dst.Clear();
            for (int i = 0; i < _flights.Count; i++)
            {
                var f = _flights[i];
                if (f.Go == null)
                    continue;

                // Prefer the GO display position (already unwraped for the local camera tile).
                float3 display = (float3)f.Go.transform.position;
                display.y = 0f;
                float3 vel = f.Velocity;
                vel.y = 0f;
                dst.Add(new AimFlightSample
                {
                    DisplayPos = display,
                    Velocity = vel,
                    Team = f.Team,
                });
            }
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
            // Missing map period → skip display unwrap (never invent 1000).
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

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

                TryShowReturnToPlanetPopup(ref f);
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
                    _pendingPoses[pose.Sequence] = pose;
                    // Late join: SpawnRpc already fired before this client existed. Instantiates
                    // a capsule from the live pose so the flight is visible.
                    if (pose.Status == PeopleTransportPoseStatus.Active)
                    {
                        PeopleTransportVfxBridge.TryEnqueue(new PeopleTransportVfxBridge.SpawnRequest
                        {
                            Sequence = pose.Sequence,
                            SpawnPosition = pose.Position,
                            TargetPosition = pose.Position + pose.Velocity,
                            Velocity = pose.Velocity,
                            CruiseSpeed = math.length(pose.Velocity),
                            Amount = 1f,
                            TargetShipNetworkId = 0,
                            SourcePlanetId = 0,
                            TargetPlanetId = 0,
                            IsLoad = 0,
                            Team = 0,
                        });
                    }

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

        /// <summary>
        /// Snaps / ends one flight from an authoritative pose update.
        /// On Consumed: shows the arrive +N float and plays the people transfer SFX
        /// (load vs unload + N-based pitch) before destroying the cosmetic sphere.
        /// </summary>
        /// <param name="index">Index into <see cref="_flights"/>.</param>
        /// <param name="pose">Latest pose / end packet from the VFX bridge.</param>
        void ApplyPoseToFlight(int index, in PeopleTransportVfxBridge.PoseUpdate pose)
        {
            if (index < 0 || index >= _flights.Count)
                return;

            var f = _flights[index];
            if (f.Sequence != pose.Sequence)
                return;

            // --- Consumed: delivery complete (legacy NGO timing for float + SFX) ---
            // [NETCODE] Consumed arrives via PeopleTransportPoseRpc after the server applies people.
            // [TITAN-ORBIT] Pitch math lives in AudioManager.PlayPeopleLoad/UnloadSound(amount) —
            // higher N → slightly lower pitch. Destroyed (shot down) stays silent like before.
            if (pose.Status == PeopleTransportPoseStatus.Consumed)
            {
                // Return-to-planet already showed +N on the planet — don't also +N the ship.
                if (f.Go != null && !f.ReturnPopupShown)
                    ShowArrivePeoplePopup(in f);

                PlayPeopleArriveSound(in f);
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
            TryShowReturnToPlanetPopup(ref f);
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
                    TargetShipNetworkId = req.TargetShipNetworkId,
                    Amount = math.max(1f, req.Amount),
                    Team = req.Team,
                    RemainingLifetime = MaxLifetimeSeconds,
                    TileK = int.MinValue,
                    TileM = int.MinValue,
                    LeavePopupShown = false,
                    HasServerPose = true,
                };

                // Leave: planet −N (load) or ship −N (unload). Arrive is a separate target.
                if (flight.SourcePlanetId != 0 || flight.TargetPlanetId != 0 || flight.TargetShipNetworkId != 0)
                {
                    ShowLeavePeoplePopup(in flight, displayPos);
                    flight.LeavePopupShown = true;
                }

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
            if (showArrivePopup && f.Go != null && !f.ReturnPopupShown)
                ShowArrivePeoplePopup(in f);

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
        /// When a load flight turns around (ship left orbit), the planet is refunding people.
        /// Show +N once and replace the live −N streak on that planet.
        /// </summary>
        void TryShowReturnToPlanetPopup(ref Flight f)
        {
            if (f.ReturnPopupShown || f.IsLoad == 0 || f.Amount < 0.01f)
                return;
            if (!IsLoadReturningToPlanet(in f))
                return;

            Vector3 hint = f.Go != null
                ? f.Go.transform.position
                : new Vector3(f.LogicalPos.x, LiftY, f.LogicalPos.z);
            ShowPlanetPeoplePopup(f.Amount, (TeamId)f.Team, in f, hint);
            f.ReturnPopupShown = true;
        }

        static bool IsLoadReturningToPlanet(in Flight f)
        {
            if (!f.HasServerPose || f.SourcePlanetId == 0)
                return false;

            bool notEligible = false;
            if (f.TargetShipNetworkId > 0 &&
                EcsGameBridge.TryIsShipEligibleForPeopleLoad(
                    f.TargetShipNetworkId, f.SourcePlanetId, out bool eligible))
            {
                if (eligible)
                    return false;
                notEligible = true;
            }

            if (math.lengthsq(f.Velocity) < 0.05f)
                return notEligible;
            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(f.SourcePlanetId, out float3 planetPos, out _, out _))
                return notEligible;
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return notEligible;

            float3 toPlanet = ToroidalMapEcs.ShortestOffsetXZ(f.LogicalPos, planetPos, mapW, mapH);
            toPlanet.y = 0f;
            if (math.lengthsq(toPlanet) < 1e-6f)
                return false;
            float3 vel = f.Velocity;
            vel.y = 0f;
            bool towardPlanet = math.dot(math.normalizesafe(vel), math.normalizesafe(toPlanet)) > 0.45f;
            return notEligible && towardPlanet;
        }

        void ShowLeavePeoplePopup(in Flight flight, Vector3 hintPos)
        {
            if (flight.IsLoad != 0)
                ShowPlanetPeoplePopup(-flight.Amount, (TeamId)flight.Team, in flight, hintPos);
            else
                ShowShipPeoplePopup(-flight.Amount, (TeamId)flight.Team, in flight);
        }

        void ShowArrivePeoplePopup(in Flight flight)
        {
            Vector3 hint = flight.Go != null
                ? flight.Go.transform.position
                : new Vector3(flight.LogicalPos.x, LiftY, flight.LogicalPos.z);
            if (flight.IsLoad != 0)
                ShowShipPeoplePopup(flight.Amount, (TeamId)flight.Team, in flight);
            else
                ShowPlanetPeoplePopup(flight.Amount, (TeamId)flight.Team, in flight, hint);
        }

        /// <summary>People leaving or landing on the planet — parked on the play-plane rim.</summary>
        void ShowPlanetPeoplePopup(float signedAmount, TeamId team, in Flight flight, Vector3 hintPos)
        {
            if (WorldFloatingCountManager.Instance == null)
                return;

            int planetId = flight.IsLoad != 0
                ? flight.SourcePlanetId
                : (flight.TargetPlanetId != 0 ? flight.TargetPlanetId : flight.SourcePlanetId);
            if (planetId == 0)
                return;

            var channel = flight.IsLoad != 0
                ? FloatingCountChannel.PeopleLoad
                : FloatingCountChannel.PeopleUnload;

            if (!TryGetPlanetAvoidance(planetId, out Vector3 avoidCenter, out float avoidRadius))
                return;

            WorldFloatingCountManager.Instance.ShowFloatingCountAtWorldPosition(
                hintPos, channel, signedAmount, team, avoidCenter, avoidRadius,
                WorldFloatingCountManager.TargetIdForPlanet(planetId));
        }

        /// <summary>People loading onto or unloading from the ship — follows the hull.</summary>
        void ShowShipPeoplePopup(float signedAmount, TeamId team, in Flight flight)
        {
            if (WorldFloatingCountManager.Instance == null)
                return;

            int shipId = flight.TargetShipNetworkId;
            if (shipId <= 0)
                shipId = EcsGameBridge.GetLocalNetworkId();
            if (shipId <= 0 || !ShipWeaponProxyRegistry.TryGetHull(shipId, out Transform hull) || hull == null)
                return;

            var channel = flight.IsLoad != 0
                ? FloatingCountChannel.PeopleLoad
                : FloatingCountChannel.PeopleUnload;

            WorldFloatingCountManager.Instance.ShowOrAccumulateOnShip(
                shipId, hull, channel, signedAmount, team);
        }

        /// <summary>
        /// Plays the people transfer one-shot at arrive (Consumed).
        /// Load and unload share one clip; <see cref="AudioManager"/> picks base pitch from
        /// direction and scales further by N (<see cref="Flight.Amount"/>).
        /// </summary>
        /// <param name="flight">Flight that just delivered — Amount and IsLoad drive pitch.</param>
        static void PlayPeopleArriveSound(in Flight flight)
        {
            // --- Arrive transfer SFX ---
            // [HYBRID] Presentation-only — server never plays audio in headless builds.
            // [TITAN-ORBIT] GetOrFind matches gem deposit: Windows player Awake order can leave
            // AudioManager.Instance null for a frame even when the component is in the scene.
            var audio = AudioManager.GetOrFind();
            if (audio == null)
                return;

            if (flight.IsLoad != 0)
                audio.PlayPeopleLoadSound(flight.Amount);
            else
                audio.PlayPeopleUnloadSound(flight.Amount);
        }

        /// <summary>
        /// Display-space planet center + radius for rim-parked people floats.
        /// </summary>
        static bool TryGetPlanetAvoidance(int planetId, out Vector3 avoidCenter, out float avoidRadius)
        {
            avoidCenter = default;
            avoidRadius = 0f;
            if (planetId == 0)
                return false;

            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(
                    planetId, out float3 logicalPlanet, out float planetScale, out _))
                return false;

            float3 planetDisplay = logicalPlanet;
            if (ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
            {
                int k = int.MinValue;
                int m = int.MinValue;
                planetDisplay = ToroidalMapEcs.GetDisplayPositionWithHysteresis(
                    logicalPlanet, (float3)reference, ref k, ref m);
            }

            avoidCenter = new Vector3(planetDisplay.x, 0f, planetDisplay.z);
            avoidRadius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetScale);
            return avoidRadius > 0.01f;
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
