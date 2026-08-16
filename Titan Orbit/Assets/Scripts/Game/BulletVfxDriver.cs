using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Owns bullet muzzle / tracer / impact GameObjects for all ships.
    /// <para>
    /// Server remains authoritative (<see cref="BulletSimulationSystem"/>). This driver only
    /// Instantiates cosmetics from <see cref="BulletVfxBridge"/> (host in-process +
    /// <see cref="BulletSpawnRpc"/> / <see cref="BulletHitRpc"/>). Windows-safe: no map-body
    /// <c>ToEntityArray</c>; Instantiates gated while Settling / GhostSpawnBacklog.
    /// TransformQuarantine is OK (session-long) — same pattern as <see cref="PeopleTransportVfxDriver"/>.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Starblast local fire: anticipation + reproject use predicted/presentation muzzle
    /// and <c>shipVel + aim * BulletSpeed</c>. Reproject is best-effort — if it fails (empty client
    /// mounts), server SpawnPosition/Velocity still Instantiates so the player never sees silent fire.
    /// Remotes keep server SpawnPosition.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Client-predicted impact: while tracers fly, swept-collide against hybrid-proxy
    /// spheres (<see cref="BulletCosmeticHitQuery"/>) so cosmetics stop at the rock / ship /
    /// planetary-defense turret surface immediately (no visual tunnel waiting for RTT).
    /// <see cref="BulletHitRpc"/> then reconciles (skip duplicate impact flash / destroy late
    /// misses) and applies authoritative mining floats via <c>AsteroidHealthAfter</c>. Turret HP
    /// is applied in <see cref="BulletHitRpcClientSystem"/> via
    /// <see cref="PlanetaryDefenseClientHealthSync"/> — this driver does not write pad Health.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Sequence 0 HitRpcs are ram/grind explosions (no tracer). They must play
    /// impact VFX and must not adopt/destroy a nearby flying tracer.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Homing rockets (local-fired and incoming remote) dead-reckon on the
    /// client from spawn: 60 Hz step + one-tick lerp + client homing steer. Server still
    /// owns hits. Display is observer-hull-relative (<see cref="ShipDisplayPose"/>) so a
    /// 60 Hz camera snap does not make any on-screen rocket look stepped while dodging.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(67010)]
    public class BulletVfxDriver : MonoBehaviour
    {
        /// <summary>One active cosmetic tracer keyed by server Sequence (or anticipation slot).</summary>
        struct Tracer
        {
            public GameObject Go;
            public uint Sequence;
            public int OwnerNetworkId;
            public float3 LogicalPos;
            public float3 SpawnPos;
            public float3 Velocity;
            /// <summary>
            /// Seconds left before cosmetic despawn. When the spawn request Lifetime is ≤ 0
            /// (planetary defense), this is set to +∞ so only <see cref="MaxDistance"/> culls.
            /// </summary>
            public float RemainingLifetime;
            public float MaxDistance;
            public float Traveled;
            public float Damage;
            public byte OwnerTeam;
            public int BankIndex;
            public float ScaleMultiplier;
            /// <summary>[TITAN-ORBIT] Weapon mount index — volley adopt / duplicate gate use this.</summary>
            public int MountIndex;
            public bool IsDisplaySpace;
            public bool IsAnticipation;
            /// <summary>Monotonic fire order for FIFO adopt (RemoveAtSwap shuffles list indices).</summary>
            public int AnticipationOrder;
            /// <summary>[TITAN-ORBIT] Cosmetic collide filter (mining / fighter pass-through).</summary>
            public byte DamageFilter;
            /// <summary>1 = store rocket — cosmetic tracer steers toward the closest enemy.</summary>
            public byte Homing;
            /// <summary>Max yaw rate in degrees per second while Homing is set.</summary>
            public float TurnSpeedDeg;
            /// <summary>Toroidal acquire radius. 0 uses the catalog default.</summary>
            public float AcquireRange;
            /// <summary>Seconds since spawn — self-harm debug arms rockets after 2s.</summary>
            public float Age;
            /// <summary>Last raw lock (sticky). Not a lagged steer point.</summary>
            public float3 RawLock;
            public bool HasRawLock;
            /// <summary>Logical pose at the previous fixed tick — display lerps from here.</summary>
            public float3 PrevLogicalPos;
            public float3 PrevVelocity;
            /// <summary>
            /// Observer hull (<see cref="ShipDisplayPose"/>) sampled on the same 60 Hz ticks
            /// as this rocket — camera frame for every tracer, not the firing ship.
            /// </summary>
            public float3 PrevHullDisplay;
            public float3 CurrHullDisplay;
            public bool HasHullTick;
            /// <summary>Smoothed hull-relative XZ offset (hides leftover 60 Hz relative steps).</summary>
            public float3 SmoothedOffset;
            public float3 OffsetSmoothVel;
            public bool HasSmoothedOffset;
            /// <summary>Leftover seconds toward the next 60 Hz rocket tick.</summary>
            public float TickCarry;
            public bool HasPrevTick;
            public ClientBulletStretchVisual Stretch;
        }

        /// <summary>
        /// Server spawn that should not create a new tracer because anticipation already
        /// predicted-hit and was destroyed before <see cref="BulletSpawnRpc"/> arrived.
        /// </summary>
        struct PendingPredictedAdoptSkip
        {
            public int OwnerNetworkId;
            public int MountIndex;
            public byte OwnerTeam;
            public float ExpireTime;
            public float3 HitDisplayPos;
            public float Damage;
            public int BankIndex;
            public float ScaleMultiplier;
        }

        /// <summary>
        /// Recent cosmetic impact used to suppress a late HitRpc double-flash.
        /// Stores OwnerNetworkId so mining floats can still apply after the tracer is gone.
        /// </summary>
        struct RecentPredictedImpact
        {
            public float3 DisplayPos;
            public byte OwnerTeam;
            /// <summary>Shooter NetworkId — used for HitRpc mining floats after the tracer is gone.</summary>
            public int OwnerNetworkId;
            public float ExpireTime;
        }

        /// <summary>
        /// Cosmetic Instantiates per frame (not GhostSpawn). Kept modest so fire-while-flying
        /// does not Instantiates a whole volley of tracer meshes in one LateUpdate
        /// (session 74383c: spawnMs ~16 ms at MaxSpawnsPerFrame=8 before muzzle pool warm).
        /// </summary>
        const int MaxSpawnsPerFrame = 3;

        /// <summary>How long a predicted Sequence suppresses a duplicate HitRpc impact.</summary>
        const float PredictedHitTtlSeconds = 2f;

        /// <summary>How long a mount skip waits for the matching server spawn after anticipation predicted-hit.</summary>
        const float PredictedAdoptSkipTtlSeconds = 1.25f;

        /// <summary>
        /// Homing rockets (local and remote) dead-reckon at sim rate, then the mesh
        /// lerps one tick behind (Fix Your Timestep). Display is hull-relative so a
        /// 60 Hz camera snap does not make the tracer look stepped while chasing.
        /// </summary>
        const float RocketPresentationTickDt = 1f / TitanOrbitServerTickRateSystem.SimulationHz;

        /// <summary>Catch-up cap so a hitch does not spiral rocket ticks.</summary>
        const int MaxRocketPresentationTicksPerFrame = 4;

        /// <summary>
        /// Offset-space SmoothDamp. Short enough that homing turns stay readable;
        /// long enough to hide leftover tick-rate relative steps while dodging.
        /// </summary>
        const float RocketOffsetSmoothTime = 0.04f;

        /// <summary>Display-space radius for matching HitRpc to a recent predicted impact (no Sequence yet).</summary>
        const float PredictedImpactMatchRadius = 14f;

        readonly List<Tracer> _tracers = new List<Tracer>(64);
        readonly Dictionary<uint, int> _indexBySequence = new Dictionary<uint, int>(64);

        /// <summary>Sequences that already played client-predicted impact — HitRpc must not flash again.</summary>
        readonly HashSet<uint> _clientPredictedHitSequences = new HashSet<uint>();
        /// <summary>OwnerNetworkId for each predicted Sequence (mining float after tracer destroy).</summary>
        readonly Dictionary<uint, int> _clientPredictedHitOwners = new Dictionary<uint, int>(64);
        readonly Queue<(uint Sequence, float ExpireTime)> _clientPredictedHitExpiry =
            new Queue<(uint, float)>(64);

        /// <summary>Anticipation predicted-hit before adopt — next SpawnRpc for this mount is consumed silently.</summary>
        readonly List<PendingPredictedAdoptSkip> _pendingPredictedAdoptSkips =
            new List<PendingPredictedAdoptSkip>(8);

        /// <summary>Display impacts for HitRpc dedupe when Sequence was never bound (anticipation-only).</summary>
        readonly List<RecentPredictedImpact> _recentPredictedImpacts = new List<RecentPredictedImpact>(16);

        BulletVfxBank _bank;
        int _lastTickFrame = -1;
        /// <summary>Last observer hull XZ — fallback velocity when kinematics are gated.</summary>
        float3 _lastObserverHull;
        bool _hasLastObserverHull;
        /// <summary>Increments per anticipation CreateTracer so FIFO adopt survives RemoveAtSwap.</summary>
        int _nextAnticipationOrder;

        /// <summary>[UNITY] Attach to session manager when the scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<BulletVfxDriver>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
            {
                session.gameObject.AddComponent<BulletVfxDriver>();
                return;
            }

            var go = new GameObject("BulletVfxDriver");
            DontDestroyOnLoad(go);
            go.AddComponent<BulletVfxDriver>();
        }

        void OnDisable()
        {
            ClearAllTracers();
            BulletVfxBridge.Clear();
            BulletCosmeticHitQuery.Clear();
            _indexBySequence.Clear();
            _clientPredictedHitSequences.Clear();
            _clientPredictedHitOwners.Clear();
            _clientPredictedHitExpiry.Clear();
            _pendingPredictedAdoptSkips.Clear();
            _recentPredictedImpacts.Clear();
            _hasLastObserverHull = false;
        }

        /// <summary>
        /// LateUpdate after <see cref="CameraFollowEcs"/> (67001): drain spawns/hits, dead-reckon,
        /// place GOs. Never Instantiates in onBeforeRender.
        /// After the camera so every rocket (local-fired and incoming remote) is placed in this
        /// frame's <see cref="ShipDisplayPose"/> — the pose the camera hard-locks to.
        /// Anticipation is still queued first by <see cref="ClientLocalBulletVfxBridge"/> (66100).
        /// </summary>
        void LateUpdate()
        {
            if (_lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;

            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            // [TITAN-ORBIT] Skip Instantiates during join Instantiates storm / post–Join Team backlog.
            // TransformQuarantine stays on for the session — that alone must NOT block bullets.
            bool blockInstantiates = ClientJoinSettleCache.ShouldSkipShipEntityQueries;

            PrunePredictedReconcileState();

            // --- Resolve bank + queue prewarm jobs before budgeted Instantiates ---
            EnsureBank();

            // --- Budgeted VFX prewarm (spread Instantiates; keep combat warm) ---
            // [TITAN-ORBIT] 4 Sci-Fi VFX Instantiates/frame keeps load hitch off the kill path
            // (kill-impact-fix: sync 1204 Instantiates = spawnMs 531 ms).
            if (!BulletOneShotVfxPool.PrewarmComplete)
                BulletOneShotVfxPool.TickPrewarm(4);

            // --- Drain spawn Instantiates (muzzle + tracer visual) ---
            if (!blockInstantiates)
                DrainSpawns();

            // --- Drain HitRpc impacts (pooled one-shot VFX) + mining floats / gem burst ---
            DrainHits();

            // Return expired muzzle/impact shells so the next shot can Rent without Instantiates.
            BulletOneShotVfxPool.TickReturns();

            if (_tracers.Count == 0)
                return;

            float dt = math.min(0.05f, math.max(0f, Time.deltaTime));
            bool hasRef = ToroidalDisplay.TryGetReferencePosition(out Vector3 reference);
            bool hasHull = TryGetObserverHullDisplay(out float3 frameHull);
            float3 frameHullVel = float3.zero;
            if (hasHull)
            {
                if (!blockInstantiates && EcsGameBridge.TryGetLocalShipVelocity(out var shipVel))
                {
                    frameHullVel = new float3(shipVel.x, 0f, shipVel.z);
                }
                else if (_hasLastObserverHull && dt > 1e-5f)
                {
                    frameHullVel = (frameHull - _lastObserverHull) / dt;
                    frameHullVel.y = 0f;
                }

                _lastObserverHull = frameHull;
                _hasLastObserverHull = true;
            }

            // --- Cosmetic hit prediction (hybrid-proxy spheres; no map ToEntityArray) ---
            // Skip while join Instantiates are incomplete — HitRpc still destroys tracers.
            bool canPredictHits = !blockInstantiates && BulletCosmeticHitQuery.TryRefresh();

            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var t = _tracers[i];
                if (t.Go == null)
                {
                    RemoveAtSwap(i);
                    continue;
                }

                float3 displayLogical;
                float3 displayVel;
                if (t.Homing != 0)
                {
                    // --- Fixed 60 Hz step + one-tick-behind lerp (homing / coast rockets) ---
                    if (!AdvanceRocketPresentation(
                            ref t, dt, !blockInstantiates, canPredictHits,
                            hasHull, frameHull, frameHullVel,
                            out displayLogical, out displayVel, out float3 hitPoint, out bool hit))
                    {
                        if (hit)
                            ApplyPredictedHit(i, in t, hitPoint);
                        else
                        {
                            DestroyTracerGo(t);
                            RemoveAtSwap(i);
                        }
                        continue;
                    }
                }
                else
                {
                    // --- Gun / drone / PD: variable-dt dead reckon (already a straight line) ---
                    float3 prevPos = t.LogicalPos;
                    t.RemainingLifetime -= dt;
                    float3 nextPos = prevPos + t.Velocity * dt;
                    float step = math.distance(prevPos, nextPos);
                    t.Traveled += step;

                    // Same substep budget as server Phase A — one 20 FPS frame at MEGA
                    // bulletSpeed can be longer than a small rock if we only test the full segment
                    // after an interior-start ignore (see BulletCollision.SegmentHitsSphere).
                    int substeps = BulletCollision.ComputeAdvanceSubstepCount(step);
                    float3 cursor = prevPos;
                    bool cosmeticHit = false;
                    float3 hitPoint = nextPos;
                    for (int s = 0; s < substeps; s++)
                    {
                        float3 sample = math.lerp(prevPos, nextPos, (s + 1) / (float)substeps);
                        if (canPredictHits &&
                            TryPredictCosmeticHit(
                                in t, cursor, sample,
                                out hitPoint,
                                out _,
                                out _,
                                out _,
                                out _))
                        {
                            cosmeticHit = true;
                            break;
                        }

                        cursor = sample;
                    }

                    t.LogicalPos = cosmeticHit ? hitPoint : nextPos;
                    if (cosmeticHit)
                    {
                        ApplyPredictedHit(i, in t, hitPoint);
                        continue;
                    }

                    if (t.RemainingLifetime <= 0f || t.Traveled >= math.max(0.5f, t.MaxDistance))
                    {
                        DestroyTracerGo(t);
                        RemoveAtSwap(i);
                        continue;
                    }

                    displayLogical = t.LogicalPos;
                    displayVel = t.Velocity;
                }

                // --- Display pose ---
                // Keep mount-height Y — unwrap helpers are XZ-only; restore after.
                float mountY = displayLogical.y;
                int stableKey = unchecked((int)t.Sequence) ^ (t.OwnerNetworkId * 397);
                Vector3 displayPos = ResolveTracerDisplayPosition(
                    ref t, displayLogical, hasRef, reference, stableKey, dt,
                    t.HasPrevTick ? math.saturate(t.TickCarry / RocketPresentationTickDt) : 1f);
                Vector3 prevDisplay = t.Go.transform.position;
                if ((displayPos - prevDisplay).sqrMagnitude > 40f * 40f)
                    ResetTrail(t.Go);

                displayPos.y = LiftTracerDisplayY(mountY);
                t.Go.transform.position = displayPos;
                if (math.lengthsq(displayVel) > 0.0001f)
                    t.Go.transform.rotation = Quaternion.LookRotation(((Vector3)displayVel).normalized, Vector3.up);

                if (t.Stretch != null)
                {
                    float progress = t.Traveled / math.max(0.5f, t.MaxDistance);
                    t.Stretch.ApplyTravelProgress(progress);
                }

                _tracers[i] = t;
            }
        }

        /// <summary>
        /// Every rocket (own shot or incoming) rides the observer camera hull.
        /// Offset is <c>lerp(shortest(rocket − hull))</c> from poses sampled on the
        /// <b>same</b> 60 Hz ticks — never interpolated-rocket minus live sim
        /// (that cancelled out under H73 raw-follow and jittered while chasing).
        /// <c>display = shipDisplayNow + offset</c> so a camera snap moves the tracer with you.
        /// </summary>
        static Vector3 ResolveTracerDisplayPosition(
            ref Tracer t,
            float3 displayLogical,
            bool hasRef,
            Vector3 reference,
            int stableKey,
            float dt,
            float tickAlpha)
        {
            if (t.IsDisplaySpace)
                return displayLogical;

            if (t.Homing != 0 &&
                ShipDisplayPose.HasLocalPose &&
                t.HasHullTick)
            {
                Vector3 shipDisp = ShipDisplayPose.LocalPosition;
                float3 rocketInterp = t.HasPrevTick
                    ? math.lerp(t.PrevLogicalPos, t.LogicalPos, tickAlpha)
                    : displayLogical;
                float3 hullInterp = math.lerp(t.PrevHullDisplay, t.CurrHullDisplay, tickAlpha);
                float3 targetOffset = HullRelativeOffset(hullInterp, rocketInterp);
                float3 offset = SmoothHullOffset(ref t, targetOffset, dt);
                return new Vector3(shipDisp.x + offset.x, displayLogical.y, shipDisp.z + offset.z);
            }

            if (hasRef)
            {
                return ToroidalDisplay.ToDisplayPositionWithHysteresis(
                    stableKey, displayLogical, reference);
            }

            return displayLogical;
        }

        /// <summary>
        /// Camera / presentation hull XZ — the same pose <see cref="CameraFollowEcs"/> hard-locks to.
        /// No ship-entity query (join-safe). Used as the attachment frame for all rocket tracers.
        /// </summary>
        static bool TryGetObserverHullDisplay(out float3 hull)
        {
            hull = default;
            if (!ShipDisplayPose.HasLocalPose)
                return false;
            Vector3 p = ShipDisplayPose.LocalPosition;
            hull = new float3(p.x, 0f, p.z);
            return true;
        }

        /// <summary>
        /// Near-tile XZ from observer hull to rocket. Raw subtract misses the seam
        /// when the unbounded hull and a spawn on the canonical tile differ by a map width.
        /// </summary>
        static float3 HullRelativeOffset(float3 hull, float3 rocket)
        {
            if (ToroidalMapEcs.HasValidMapSize)
                return ToroidalMapEcs.ShortestOffsetXZ(
                    hull, rocket, ToroidalMapEcs.MapWidth, ToroidalMapEcs.MapHeight);

            float3 d = rocket - hull;
            d.y = 0f;
            return d;
        }

        /// <summary>
        /// Smooths only the hull-relative offset. World-space SmoothDamp would fight camera snaps.
        /// </summary>
        static float3 SmoothHullOffset(ref Tracer t, float3 targetOffset, float dt)
        {
            if (!t.HasSmoothedOffset ||
                math.distancesq(t.SmoothedOffset, targetOffset) > 40f * 40f)
            {
                t.SmoothedOffset = targetOffset;
                t.OffsetSmoothVel = float3.zero;
                t.HasSmoothedOffset = true;
                return targetOffset;
            }

            Vector3 vel = t.OffsetSmoothVel;
            Vector3 smoothed = Vector3.SmoothDamp(
                t.SmoothedOffset,
                targetOffset,
                ref vel,
                RocketOffsetSmoothTime,
                Mathf.Infinity,
                dt);
            t.SmoothedOffset = smoothed;
            t.OffsetSmoothVel = vel;
            return t.SmoothedOffset;
        }

        /// <summary>
        /// Steps a rocket at 60 Hz, then returns a pose lerped from the previous tick
        /// (up to one sim tick behind). Hit tests use the discrete tick segments.
        /// </summary>
        /// <returns>False when the tracer should be removed (hit or lifetime/range).</returns>
        bool AdvanceRocketPresentation(
            ref Tracer t,
            float dt,
            bool canSteer,
            bool canPredictHits,
            bool hasHull,
            float3 frameHull,
            float3 frameHullVel,
            out float3 displayLogical,
            out float3 displayVel,
            out float3 hitPoint,
            out bool hit)
        {
            hitPoint = default;
            hit = false;
            float tickDt = RocketPresentationTickDt;
            t.TickCarry += dt;

            int steps = 0;
            while (t.TickCarry >= tickDt && steps < MaxRocketPresentationTicksPerFrame)
            {
                t.PrevLogicalPos = t.LogicalPos;
                t.PrevVelocity = t.Velocity;
                if (hasHull)
                {
                    t.PrevHullDisplay = t.HasHullTick ? t.CurrHullDisplay : frameHull;
                    t.CurrHullDisplay = steps == 0
                        ? frameHull
                        : t.CurrHullDisplay + frameHullVel * tickDt;
                    t.HasHullTick = true;
                }
                t.HasPrevTick = true;

                if (canSteer && t.TurnSpeedDeg > 0.01f)
                    TrySteerHomingTracer(ref t, tickDt);

                float3 prevPos = t.LogicalPos;
                t.LogicalPos += t.Velocity * tickDt;
                t.Traveled += math.length(t.Velocity) * tickDt;
                t.Age += tickDt;
                t.RemainingLifetime -= tickDt;
                t.TickCarry -= tickDt;
                steps++;

                if (canPredictHits &&
                    TryPredictCosmeticHit(
                        in t, prevPos, t.LogicalPos,
                        out hitPoint,
                        out _,
                        out _,
                        out _,
                        out _))
                {
                    hit = true;
                    displayLogical = t.LogicalPos;
                    displayVel = t.Velocity;
                    return false;
                }

                if (t.RemainingLifetime <= 0f || t.Traveled >= math.max(0.5f, t.MaxDistance))
                {
                    displayLogical = t.LogicalPos;
                    displayVel = t.Velocity;
                    return false;
                }
            }

            if (steps >= MaxRocketPresentationTicksPerFrame)
                t.TickCarry = math.min(t.TickCarry, tickDt);

            if (t.HasPrevTick)
            {
                float alpha = math.saturate(t.TickCarry / tickDt);
                displayLogical = math.lerp(t.PrevLogicalPos, t.LogicalPos, alpha);
                displayVel = math.lerp(t.PrevVelocity, t.Velocity, alpha);
            }
            else
            {
                displayLogical = t.LogicalPos;
                displayVel = t.Velocity;
            }

            return true;
        }

        /// <summary>
        /// Steers a homing tracer toward the closest enemy ship or turret.
        /// Uses the client world ghost poses. Skips when join gates block ship queries.
        /// </summary>
        static void TrySteerHomingTracer(ref Tracer t, float dt)
        {
            // --- Join safety ---
            // [TITAN-ORBIT] Ship + planet gathers Crash!!! during Join Team Instantiates.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;
            if (!ToroidalMapEcs.HasValidMapSize)
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (!RocketHomingTargeting.TryFindClosestTarget(
                    world.EntityManager, t.LogicalPos, t.OwnerTeam, t.OwnerNetworkId,
                    t.AcquireRange, mapW, mapH,
                    t.RawLock, t.HasRawLock, out float3 lockPos,
                    includeOwner: TitanOrbitDebugFlags.IsSelfHarmArmed(t.Age)))
            {
                t.HasRawLock = false;
                return;
            }

            t.RawLock = lockPos;
            t.HasRawLock = true;
            float3 vel = t.Velocity;
            RocketHomingLogic.TrySteerToward(
                t.LogicalPos, ref vel, lockPos, t.TurnSpeedDeg, dt, mapW, mapH);
            t.Velocity = vel;
        }

        /// <summary>Creates tracers from the bridge (budgeted Instantiates).</summary>
        void DrainSpawns()
        {
            EnsureBank();
            int spawned = 0;
            while (spawned < MaxSpawnsPerFrame && BulletVfxBridge.TryDequeueSpawn(out var req))
            {
                // --- Consume server spawn when anticipation already predicted-hit this mount ---
                // [TITAN-ORBIT] Without this, SpawnRpc Instantiates a twin that tunnels after the
                // early impact destroyed Sequence=0 — looks like a second bullet ghosting through.
                if (!req.IsAnticipation && req.Sequence != 0 &&
                    TryConsumePredictedAdoptSkip(in req))
                {
                    spawned++;
                    continue;
                }

                // --- Adopt local anticipation when server spawn arrives (no pose snap) ---
                // Prevents a second tracer: orphan Sequence=0 cosmetics keep flying through rocks
                // after HitRpc kills only the sequenced server twin.
                if (!req.IsAnticipation && req.Sequence != 0 &&
                    TryAdoptAnticipation(req))
                {
                    spawned++;
                    continue;
                }

                // --- Local owner: drop redundant anticipation if THAT muzzle already has a fresh tracer ---
                // [TITAN-ORBIT] Mount-aware — a flying bullet from mount 0 must not kill anticipations
                // for mounts 1–3 in the same multi-cannon volley.
                if (req.IsAnticipation &&
                    BulletMuzzlePresentation.IsLocalOwner(req.OwnerNetworkId) &&
                    HasFreshLocalPresentationTracer(req.OwnerNetworkId, req.MountIndex))
                    continue;

                // --- Starblast: reproject local muzzle/velocity at CreateTracer time (best-effort) ---
                // [TITAN-ORBIT] Never drop a server spawn when reproject fails — client ECS mounts are
                // often empty under hybrid/TransformQuarantine while the server still fires (energy +
                // hits work). Falling back to server pose/vel restores visible tracers; reproject
                // (ECS or GO mounts) still corrects feel when it succeeds.
                bool localShot = req.IsAnticipation ||
                                 BulletMuzzlePresentation.IsLocalOwner(req.OwnerNetworkId);
                if (localShot)
                    BulletMuzzlePresentation.TryReprojectLocalOwnerSpawn(ref req);

                CreateTracer(req);
                spawned++;
            }
        }

        /// <summary>
        /// True when a non-anticipation local tracer for this owner+mount just spawned (presentation-locked).
        /// Used to drop duplicate anticipation after a reprojected server spawn for the same barrel.
        /// </summary>
        bool HasFreshLocalPresentationTracer(int ownerNetworkId, int mountIndex)
        {
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.IsAnticipation || t.OwnerNetworkId != ownerNetworkId)
                    continue;
                if (t.MountIndex != mountIndex)
                    continue;
                if (t.IsDisplaySpace && t.Traveled < 2f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Impact VFX + destroy matching tracer by Sequence.
        /// Falls back to nearest same-team tracer (incl. Sequence=0 anticipation) so orphan
        /// cosmetics do not keep flying through the rock after a real server hit.
        /// Skips duplicate impact flash when client already predicted this Sequence / nearby impact.
        /// Mining floats always use HitRpc <c>AsteroidHealthAfter</c> (never cosmetic-predicted HP).
        /// Turret HP is written in <see cref="BulletHitRpcClientSystem"/> (not here).
        /// Sequence 0 (ram/grind) plays VFX only — never adopts a tracer.
        /// </summary>
        void DrainHits()
        {
            EnsureBank();
            while (BulletVfxBridge.TryDequeueHit(out var hit))
            {
                Vector3 hitPos = hit.HitPosition;
                if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                    hitPos = ToroidalDisplay.ToDisplayPosition(hitPos, reference);
                hitPos.y = 0f;

                // --- Ram / grind: Sequence 0 means there is no tracer ---
                // [TITAN-ORBIT] Reusing BulletHitRpc so every client gets the ship's bullet
                // explosion scaled by ram damage. Nearest-tracer fallback would eat a live shot
                // if you ram while firing. Predicted-bullet suppress would hide the ram boom.
                if (hit.Sequence == 0)
                {
                    var ramTeam = (TeamId)hit.OwnerTeam;
                    int ramBank = math.max(0, hit.BankIndex);
                    float ramScale = hit.ScaleMultiplier > 0f ? hit.ScaleMultiplier : 1f;
                    BulletVisualFactory.SpawnBulletImpactVfx(
                        hitPos, _bank, ramBank, ramTeam, hit.Damage, ramScale);

                    var ramSynth = new Tracer { OwnerNetworkId = 0, IsAnticipation = false };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, ramTeam,
                        hit.AsteroidHealthAfter, in ramSynth);
                    continue;
                }

                // --- Reconcile: client already showed impact for this Sequence ---
                // Tracer + impact VFX already done; still apply authoritative mining float.
                if (hit.Sequence != 0 && _clientPredictedHitSequences.Remove(hit.Sequence))
                {
                    int ownerNetworkId = 0;
                    if (_clientPredictedHitOwners.TryGetValue(hit.Sequence, out int storedOwner))
                    {
                        ownerNetworkId = storedOwner;
                        _clientPredictedHitOwners.Remove(hit.Sequence);
                    }

                    var synth = new Tracer
                    {
                        OwnerNetworkId = ownerNetworkId,
                        IsAnticipation = false,
                    };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in synth);
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                    continue;
                }

                // --- Reconcile: anticipation predicted-hit before Sequence was bound ---
                // Suppresses duplicate flash only — mining floats still run below.
                bool suppressImpactVfx = TryConsumeRecentPredictedImpact(
                    hitPos, hit.OwnerTeam, out int recentOwnerNetworkId);

                if (!suppressImpactVfx)
                {
                    var team = (TeamId)hit.OwnerTeam;
                    int bankIndex = math.max(0, hit.BankIndex);
                    float scaleMul = hit.ScaleMultiplier > 0f ? hit.ScaleMultiplier : 1f;
                    BulletVisualFactory.SpawnBulletImpactVfx(
                        hitPos, _bank, bankIndex, team, hit.Damage, scaleMul);
                }

                // --- Preferred: exact Sequence from server ---
                if (_indexBySequence.TryGetValue(hit.Sequence, out int idx) &&
                    idx >= 0 && idx < _tracers.Count)
                {
                    var tracer = _tracers[idx];
                    // Always show float on HitRpc — even when VFX was client-predicted.
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in tracer);

                    DestroyTracerGo(tracer);
                    RemoveAtSwap(idx);
                    // [TITAN-ORBIT] Cull only far leftover anticipations (energy-lag overfire).
                    // Do NOT wipe the whole team — a multi-cannon volley has sibling tracers that
                    // are still valid when one muzzle's bullet hits first.
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                    continue;
                }

                // --- Fallback: nearest same-team tracer near the impact (orphan anticipation) ---
                // [TITAN-ORBIT] Adopt can miss when OwnerNetworkId was 0 on enqueue; HitRpc still
                // has a Sequence the client never bound — without this, the cosmetic tunnels.
                if (TryFindNearestTracerIndex(hitPos, hit.OwnerTeam, maxDistance: 12f, out int nearIdx))
                {
                    var nearTracer = _tracers[nearIdx];
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in nearTracer);

                    DestroyTracerGo(nearTracer);
                    RemoveAtSwap(nearIdx);
                }
                else if (suppressImpactVfx)
                {
                    // Anticipation already gone — still apply mining float with stored owner.
                    var synth = new Tracer
                    {
                        OwnerNetworkId = recentOwnerNetworkId,
                        IsAnticipation = true,
                    };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in synth);
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                }
                else
                {
                    // No tracer to destroy — still apply asteroid teardown from HitRpc.
                    var synth = new Tracer { OwnerNetworkId = 0, IsAnticipation = false };
                    TryShowAsteroidFloatForHitRpc(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, in synth);
                }
            }
        }

        /// <summary>
        /// HitRpc path: local asteroid impact → +Damage and server-authored HP Left.
        /// <para>
        /// [TITAN-ORBIT] <paramref name="asteroidHealthAfter"/> comes from the server on
        /// <see cref="BulletHitRpc"/> — do not subtract from lagging ghost Health. Seed-hydrate
        /// asteroids are not ghosts: <see cref="BulletHitRpcClientSystem"/> writes Health /
        /// IsDestroyed; this path shows floats (local shots) and hides/tears down the hybrid GO
        /// on kill. Do not DestroyEntity here — sim-group soft-destroy owns ECS teardown.
        /// Non-asteroid hits pass &lt; 0 and skip mining floats entirely.
        /// </para>
        /// </summary>
        static void TryShowAsteroidFloatForHitRpc(
            Vector3 hitDisplayPos,
            float3 hitLogicalPos,
            float damage,
            TeamId ownerTeam,
            float asteroidHealthAfter,
            in Tracer tracer)
        {
            // Not an asteroid impact — do not attribute mining floats to a nearby rock.
            if (asteroidHealthAfter < 0f)
                return;

            bool localShot = tracer.IsAnticipation ||
                             BulletMuzzlePresentation.IsLocalOwner(tracer.OwnerNetworkId);

            // Impact must land on this rock’s hit sphere (cluster-safe surface fit).
            // Kill hits also match just-culled seed-hydrated rocks (HP already written in ECS).
            BulletCosmeticHitQuery.TryFindAsteroidAtImpact(
                hitDisplayPos, out Entity asteroidEntity, asteroidHealthAfter);

            // --- Mining floats (local shots only — cosmetics) ---
            if (localShot && asteroidEntity != Entity.Null)
            {
                EcsFloatingCountPresenter.TryNotifyLocalAsteroidBulletHit(
                    asteroidEntity,
                    damage,
                    ownerTeam,
                    authoritativeRemainingHealth: asteroidHealthAfter);
            }

            // --- Kill: do not hide from HitRpc ---
            // [TITAN-ORBIT] Surface-fit on a packed belt can hide a neighbor while DestroyRpc
            // culls the rock the server actually killed (two client hides, one server destroy,
            // leftover invisible hull). Authoritative teardown is AsteroidDestroyedRpc only.
            if (asteroidHealthAfter <= 0.01f)
                return;
        }

        /// <summary>
        /// Binds server Sequence / lifetime / bank onto an existing anticipation tracer.
        /// [TITAN-ORBIT] Does <b>not</b> relocate to lagged server SpawnPosition — keeps presentation muzzle flight.
        /// Owner match is loose so NetworkId=0 anticipation still adopts local server spawns.
        /// </summary>
        bool TryAdoptAnticipation(in BulletVfxBridge.SpawnRequest req)
        {
            // --- World / planetary-defense spawns (MountIndex < 0) ---
            // [TITAN-ORBIT] Never adopt ship-mount anticipation. Local Fire while piloting a pad
            // used to leave ship-gun tracers that stole PD SpawnRpcs and kept the wrong velocity
            // (turret aimed at mouse, bullets flew hull-forward).
            if (req.MountIndex < 0)
                return false;

            // --- FIFO adopt (oldest AnticipationOrder for this owner + mount) ---
            // [TITAN-ORBIT] Prefer matching MountIndex so a 4-gun volley binds each server Sequence
            // onto the anticipation that left that barrel. Fall back to any mount if none match
            // (older clients / missing MountIndex on RPC).
            if (!TryFindAdoptIndex(req, preferMountMatch: true, out int bestIndex))
                TryFindAdoptIndex(req, preferMountMatch: false, out bestIndex);

            if (bestIndex < 0)
                return false;

            var adopted = _tracers[bestIndex];
            // --- Bind authority metadata — keep presentation flight ---
            // [TITAN-ORBIT] Do not overwrite Velocity with lagged server aim. Server mounts often
            // bake LocalRotation ≈ identity (hull-forward) while the live weapon component faces
            // a different way — adopting that mid-flight made sequential shots look mis-aimed.
            // Position was already correct from the live muzzle; keep that aim too.
            adopted.Sequence = req.Sequence;
            adopted.IsAnticipation = false;
            adopted.OwnerNetworkId = req.OwnerNetworkId > 0 ? req.OwnerNetworkId : adopted.OwnerNetworkId;
            adopted.MountIndex = req.MountIndex;
            adopted.BankIndex = req.BankIndex;
            adopted.ScaleMultiplier = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : adopted.ScaleMultiplier;
            adopted.Damage = req.Damage;
            // [TITAN-ORBIT] Lifetime <= 0 = distance-only (PD turrets); do not clamp to 0.05s.
            adopted.RemainingLifetime = ResolveTracerLifetime(req.Lifetime);
            adopted.MaxDistance = math.max(0.5f, req.MaxDistance);
            // Do not reset Traveled / LogicalPos — stretch/trail continue from presentation muzzle.

            _tracers[bestIndex] = adopted;
            _indexBySequence[req.Sequence] = bestIndex;
            // Anticipation slot consumed — frees a Cap for ClientLocalBulletVfxBridge.
            BulletVfxBridge.NotifyAnticipationConsumed();
            return true;
        }

        /// <summary>
        /// Finds the oldest anticipation tracer for adopt, optionally requiring MountIndex match.
        /// </summary>
        bool TryFindAdoptIndex(in BulletVfxBridge.SpawnRequest req, bool preferMountMatch, out int bestIndex)
        {
            bestIndex = -1;
            int bestOrder = int.MaxValue;
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (!t.IsAnticipation || t.Sequence != 0)
                    continue;
                if (!OwnersMatchForAdopt(t.OwnerNetworkId, req.OwnerNetworkId))
                    continue;
                if (preferMountMatch && t.MountIndex != req.MountIndex)
                    continue;
                if (t.AnticipationOrder >= bestOrder)
                    continue;

                bestOrder = t.AnticipationOrder;
                bestIndex = i;
            }

            return bestIndex >= 0;
        }

        /// <summary>
        /// Destroys far-traveled still-pending anticipation tracers for a team.
        /// Fresh volley siblings (near the muzzle) are kept so multi-cannon cosmetics survive
        /// when one barrel scores a hit first.
        /// </summary>
        void ClearStaleAnticipationTracers(byte ownerTeam)
        {
            // Beyond this travel, an unadopted anticipation is almost certainly a leftover from
            // energy-lag overfire — safe to cull without killing a just-fired volley mate.
            const float StaleTravel = 8f;

            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var t = _tracers[i];
                if (!t.IsAnticipation || t.Sequence != 0)
                    continue;
                if (t.OwnerTeam != ownerTeam)
                    continue;
                if (t.Traveled < StaleTravel)
                    continue;

                DestroyTracerGo(t);
                RemoveAtSwap(i);
            }
        }

        /// <summary>
        /// True when spawn/anticipation owners refer to the same ship (incl. id-not-ready edge cases).
        /// </summary>
        static bool OwnersMatchForAdopt(int anticipationOwnerId, int serverOwnerId)
        {
            // --- Both ids known ---
            if (anticipationOwnerId > 0 && serverOwnerId > 0)
                return anticipationOwnerId == serverOwnerId;

            // --- One id missing: only adopt when the known id is the local player ---
            // Prevents binding local Sequence=0 cosmetics onto a remote spawn with OwnerNetworkId=0.
            int known = anticipationOwnerId > 0 ? anticipationOwnerId : serverOwnerId;
            if (known > 0)
                return BulletMuzzlePresentation.IsLocalOwner(known);

            // Both missing — anticipation is local-only; allow adopt.
            return true;
        }

        /// <summary>
        /// Finds the tracer GameObject closest to a display-space impact for the firing team.
        /// Used when HitRpc Sequence was never bound (orphan anticipation).
        /// </summary>
        bool TryFindNearestTracerIndex(Vector3 hitDisplayPos, byte ownerTeam, float maxDistance, out int index)
        {
            index = -1;
            float bestDistSq = maxDistance * maxDistance;
            float3 hit = new float3(hitDisplayPos.x, 0f, hitDisplayPos.z);

            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.OwnerTeam != ownerTeam || t.Go == null)
                    continue;

                Vector3 p = t.Go.transform.position;
                float distSq = math.distancesq(new float3(p.x, 0f, p.z), hit);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    index = i;
                }
            }

            return index >= 0;
        }

        void CreateTracer(in BulletVfxBridge.SpawnRequest req)
        {
            EnsureBank();
            var team = (TeamId)req.OwnerTeam;
            int bankIndex = math.max(0, req.BankIndex);
            float scaleMul = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : 1f;
            float bulletSpeed = math.length(req.Velocity);

            Vector3 spawnDisplay = req.SpawnPosition;
            float mountY = req.SpawnPosition.y;
            if (!req.IsDisplaySpace && ToroidalDisplay.TryGetReferencePosition(out var reference))
                spawnDisplay = ToroidalDisplay.ToDisplayPosition(req.SpawnPosition, reference);
            // Keep weapon-mount height (display unwrap is XZ-only), then lift above a MEGA hull.
            spawnDisplay.y = LiftTracerDisplayY(mountY);

            // --- Muzzle flash at fire origin ---
            float cameraScale = ResolveMegaCameraVisualScale();
            BulletVisualFactory.PlayMuzzleVfx(
                spawnDisplay,
                req.Velocity,
                _bank,
                bankIndex,
                team,
                scaleMul * cameraScale,
                bulletSpeed);
            AudioManager.Instance?.PlayWeaponShootSound(
                BulletVisualFactory.GetProjectileSoundPitchBySpeed(bulletSpeed));

            // --- Pooled tracer shell (destroy-probe: spawnMs ~14 ms was Instantiates here) ---
            GameObject projectilePrefab = null;
            if (!Application.isMobilePlatform && _bank != null)
                projectilePrefab = _bank.GetProjectileVisualPrefab(bankIndex, team);

            if (!BulletTracerPool.TryRent(projectilePrefab, out GameObject go, out _) || go == null)
            {
                // Last resort — should be rare (pool CreateShell failed).
                go = new GameObject("BulletTracer");
                BulletVisualFactory.BuildVisual(
                    go.transform, _bank, bankIndex, team, BulletShape.Sphere,
                    scaleMul, bulletSpeed, noTrail: false);
            }

            Quaternion rot = math.lengthsq(req.Velocity) > 0.0001f
                ? Quaternion.LookRotation(((Vector3)req.Velocity).normalized, Vector3.up)
                : Quaternion.identity;
            go.transform.SetPositionAndRotation(spawnDisplay, rot);

            // Refresh tint / scale / particles on a recycled shell.
            // Scale the tracer ROOT (same as muzzle/impact). Scaling only the visual child
            // left World-space particles and trail widths at ship size.
            GameObject visual = go.transform.childCount > 0
                ? go.transform.GetChild(0).gameObject
                : go;
            float visualScale = BulletVisualFactory.GetBulletVisualScale(_bank, scaleMul, bankIndex)
                                * cameraScale;
            BulletVisualFactory.ApplyColorToVisual(visual, BulletVisualFactory.GetTeamBulletColor(team));
            VfxUrpCompat.ApplyImpactVisualScale(go, visualScale);
            VfxUrpCompat.PrepareVfxInstance(go);
            BulletVisualFactory.SetAudioPitchInHierarchy(
                go, BulletVisualFactory.GetProjectileSoundPitchBySpeed(bulletSpeed));

            ClientBulletStretchVisual stretch = go.GetComponent<ClientBulletStretchVisual>();
            if (_bank != null
                && _bank.TryGetProfile(bankIndex, out var profile)
                && profile != null
                && profile.TryGetStretchLengthFactors(out float startFactor, out float endFactor))
            {
                // Root already carries drone/ship shot scale — do not shrink length again.
                if (stretch == null)
                {
                    if (ClientBulletStretchVisual.TryAttach(go.transform, visual, startFactor, endFactor))
                        stretch = go.GetComponent<ClientBulletStretchVisual>();
                }
                else
                    stretch.Rebind(visual, startFactor, endFactor);
            }

            var tracer = new Tracer
            {
                Go = go,
                Sequence = req.Sequence,
                OwnerNetworkId = req.OwnerNetworkId,
                LogicalPos = req.SpawnPosition,
                SpawnPos = req.SpawnPosition,
                Velocity = req.Velocity,
                // [TITAN-ORBIT] Lifetime <= 0 = distance-only (PD); ship guns keep a positive timer.
                RemainingLifetime = ResolveTracerLifetime(req.Lifetime),
                MaxDistance = math.max(0.5f, req.MaxDistance),
                Traveled = 0f,
                Damage = req.Damage,
                OwnerTeam = req.OwnerTeam,
                BankIndex = bankIndex,
                ScaleMultiplier = scaleMul,
                MountIndex = req.MountIndex,
                IsDisplaySpace = req.IsDisplaySpace,
                IsAnticipation = req.IsAnticipation,
                // Only anticipations need order; server-only tracers keep 0.
                AnticipationOrder = req.IsAnticipation ? _nextAnticipationOrder++ : 0,
                DamageFilter = req.DamageFilter,
                Homing = req.Homing,
                TurnSpeedDeg = req.TurnSpeedDeg,
                AcquireRange = req.AcquireRange,
                Stretch = stretch,
            };

            // Seed observer-hull samples so the first frame can already ride the camera
            // (incoming remote rockets included — no local-owner check).
            if (req.Homing != 0 && TryGetObserverHullDisplay(out float3 hull))
            {
                tracer.PrevLogicalPos = req.SpawnPosition;
                tracer.PrevVelocity = req.Velocity;
                tracer.PrevHullDisplay = hull;
                tracer.CurrHullDisplay = hull;
                tracer.HasHullTick = true;
                tracer.HasPrevTick = true;
            }

            _tracers.Add(tracer);
            if (req.Sequence != 0)
                _indexBySequence[req.Sequence] = _tracers.Count - 1;
            else if (req.IsAnticipation)
                BulletVfxBridge.NotifyAnticipationCreated();
        }

        bool _oneShotPoolPrewarmQueued;

        /// <summary>
        /// Maps server/RPC Lifetime onto cosmetic RemainingLifetime.
        /// Positive values keep the ship-gun timer (clamped so tiny floats do not despawn instantly).
        /// Lifetime ≤ 0 means distance-only cull (planetary defense) — RemainingLifetime = +∞ so
        /// the age check never fires and MaxDistance alone ends the tracer.
        /// </summary>
        /// <param name="lifetimeSeconds">Authoritative Lifetime from the spawn request / RPC.</param>
        /// <returns>Seconds for RemainingLifetime, or <see cref="float.PositiveInfinity"/> when unused.</returns>
        /// <summary>
        /// MEGA hulls sit on the play plane and hide Y=0 tracers. Lift cosmetics just above
        /// the local MEGA box when the follow camera has a hull-top sample.
        /// </summary>
        static float LiftTracerDisplayY(float logicalY)
        {
            var follow = CameraFollowEcs.Instance;
            if (follow == null || follow.MegaHullTopDisplayY <= 0.01f)
                return logicalY;
            return math.max(logicalY, follow.MegaHullTopDisplayY + 1.25f);
        }

        /// <summary>
        /// Grow tracers with MEGA camera height so they stay readable when the lens pulls back.
        /// Regular L7 height (~43) stays near 1×; taller MEGA framing scales up to 4×.
        /// </summary>
        static float ResolveMegaCameraVisualScale()
        {
            var follow = CameraFollowEcs.Instance;
            if (follow == null || follow.MegaHullTopDisplayY <= 0.01f)
                return 1f;
            float height = follow.CurrentHeight;
            const float referenceHeight = 25f;
            return math.clamp(height / referenceHeight, 1f, 4f);
        }

        static float ResolveTracerLifetime(float lifetimeSeconds)
        {
            // [TITAN-ORBIT] Mirror BulletSimulationSystem: Lifetime <= 0 skips the age timer.
            if (lifetimeSeconds <= 0f)
                return float.PositiveInfinity;
            return math.max(0.1f, lifetimeSeconds);
        }

        void EnsureBank()
        {
            if (_bank == null)
                _bank = BulletVfxBank.LoadDefault();
            if (_bank != null)
                BulletVisualScale.ActiveUpgradeVisualScaleMultiplier = _bank.UpgradeVisualScaleMultiplier;

            // --- Queue muzzle/impact prewarm (drained a few Instantiates/frame) ---
            // [TITAN-ORBIT] Sync Prewarm of 17×5×12 shells cost spawnMs 531 ms (kill-impact-fix).
            // Enqueue + TickPrewarm keeps combat warm without one giant hitch.
            if (!_oneShotPoolPrewarmQueued && _bank != null)
            {
                _oneShotPoolPrewarmQueued = true;
                EnqueueOneShotVfxPoolPrewarm(_bank);
                BulletTracerPool.PrewarmFromBank(_bank, categoryCap: 4, perPrefab: 6);
            }
        }

        /// <summary>
        /// Enqueues unique muzzle/impact prefabs for budgeted Instantiates.
        /// Depth 4 is enough once PrepareVfxInstance is paid at create (cold Instantiates avoided on kill).
        /// </summary>
        static void EnqueueOneShotVfxPoolPrewarm(BulletVfxBank bank)
        {
            if (bank == null || Application.isMobilePlatform)
                return;

            int catCount = bank.CategoryCount;
            for (int i = 0; i < catCount; i++)
            {
                for (int t = (int)TeamId.TeamA; t <= (int)TeamId.TeamE; t++)
                {
                    var team = (TeamId)t;
                    BulletOneShotVfxPool.EnqueuePrewarm(bank.GetMuzzlePrefab(i, team), 3);
                    BulletOneShotVfxPool.EnqueuePrewarm(bank.GetImpactPrefab(i, team), 4);
                }
            }
        }

        /// <summary>
        /// Swept cosmetic collide for one tracer step using <see cref="BulletCosmeticHitQuery"/>.
        /// Passes <see cref="Tracer.ScaleMultiplier"/> so turret spheres match server
        /// <c>ExpandRadiusForBulletScale</c> (heavy bolts connect like hull hits).
        /// </summary>
        static bool TryPredictCosmeticHit(
            in Tracer t,
            float3 from,
            float3 to,
            out float3 hitPoint,
            out BulletCosmeticHitQuery.ObstacleKind hitKind,
            out Entity hitEntity,
            out int hitPlanetId,
            out int hitSlotIndex)
        {
            // [HYBRID] Same obstacle set as server TryResolveBulletHit, including derived
            // planetary-defense pad spheres. Without those, tracers tunnel through guns.
            return BulletCosmeticHitQuery.TryHitSegment(
                from,
                to,
                t.OwnerTeam,
                t.OwnerNetworkId,
                t.IsDisplaySpace,
                out hitPoint,
                out hitKind,
                out hitEntity,
                out hitPlanetId,
                out hitSlotIndex,
                t.DamageFilter,
                t.ScaleMultiplier);
        }

        /// <summary>
        /// Plays impact VFX, records reconcile keys, destroys the tracer.
        /// Does not write damage / HP — server HitRpc still owns mining floats
        /// (<c>AsteroidHealthAfter</c>) and turret HP (<see cref="PlanetaryDefenseClientHealthSync"/>).
        /// </summary>
        void ApplyPredictedHit(int tracerIndex, in Tracer t, float3 hitPoint)
        {
            EnsureBank();

            // --- Display-space impact position for the VFX prefab ---
            Vector3 hitDisplay = hitPoint;
            if (!t.IsDisplaySpace && ToroidalDisplay.TryGetReferencePosition(out var reference))
                hitDisplay = ToroidalDisplay.ToDisplayPosition(hitPoint, reference);
            hitDisplay.y = 0f;

            var team = (TeamId)t.OwnerTeam;
            int bankIndex = math.max(0, t.BankIndex);
            float scaleMul = t.ScaleMultiplier > 0f ? t.ScaleMultiplier : 1f;
            BulletVisualFactory.SpawnBulletImpactVfx(
                hitDisplay, _bank, bankIndex, team, t.Damage, scaleMul);

            // --- Remember for HitRpc / SpawnRpc reconcile ---
            // Mining floats and turret HP wait for HitRpc (authoritative remaining Health).
            float now = Time.unscaledTime;
            if (t.Sequence != 0)
                RememberPredictedSequence(t.Sequence, t.OwnerNetworkId, now);
            else if (t.IsAnticipation)
            {
                // Anticipation died before adopt — next SpawnRpc for this mount must not twin.
                _pendingPredictedAdoptSkips.Add(new PendingPredictedAdoptSkip
                {
                    OwnerNetworkId = t.OwnerNetworkId,
                    MountIndex = t.MountIndex,
                    OwnerTeam = t.OwnerTeam,
                    ExpireTime = now + PredictedAdoptSkipTtlSeconds,
                    HitDisplayPos = hitDisplay,
                    Damage = t.Damage,
                    BankIndex = bankIndex,
                    ScaleMultiplier = scaleMul,
                });
            }

            _recentPredictedImpacts.Add(new RecentPredictedImpact
            {
                DisplayPos = hitDisplay,
                OwnerTeam = t.OwnerTeam,
                OwnerNetworkId = t.OwnerNetworkId,
                ExpireTime = now + PredictedHitTtlSeconds,
            });

            DestroyTracerGo(t);
            RemoveAtSwap(tracerIndex);
        }

        /// <summary>
        /// When anticipation already predicted-hit, bind the arriving server Sequence into the
        /// predicted-hit set and skip CreateTracer (no twin).
        /// </summary>
        bool TryConsumePredictedAdoptSkip(in BulletVfxBridge.SpawnRequest req)
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < _pendingPredictedAdoptSkips.Count; i++)
            {
                var skip = _pendingPredictedAdoptSkips[i];
                if (now > skip.ExpireTime)
                    continue;
                if (!OwnersMatchForAdopt(skip.OwnerNetworkId, req.OwnerNetworkId))
                    continue;
                if (skip.MountIndex != req.MountIndex)
                    continue;

                // Bind server Sequence so the later HitRpc reconciles cleanly.
                RememberPredictedSequence(req.Sequence, skip.OwnerNetworkId, now);
                // Drop the spatial recent-impact entry — Sequence owns reconcile from here.
                TryConsumeRecentPredictedImpact(skip.HitDisplayPos, skip.OwnerTeam, out _);
                _pendingPredictedAdoptSkips.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>Records a sequenced predicted hit for HitRpc dedupe (+ owner for mining floats).</summary>
        void RememberPredictedSequence(uint sequence, int ownerNetworkId, float now)
        {
            if (sequence == 0)
                return;
            if (_clientPredictedHitSequences.Add(sequence))
            {
                _clientPredictedHitOwners[sequence] = ownerNetworkId;
                _clientPredictedHitExpiry.Enqueue((sequence, now + PredictedHitTtlSeconds));
            }
        }

        /// <summary>
        /// True when a recent predicted impact sits near this HitRpc display position
        /// (anticipation predicted before Sequence was known).
        /// </summary>
        /// <param name="ownerNetworkId">Shooter id from the matched prediction (0 if none).</param>
        bool TryConsumeRecentPredictedImpact(
            Vector3 hitDisplayPos,
            byte ownerTeam,
            out int ownerNetworkId)
        {
            ownerNetworkId = 0;
            float now = Time.unscaledTime;
            float maxDistSq = PredictedImpactMatchRadius * PredictedImpactMatchRadius;
            float3 hit = new float3(hitDisplayPos.x, 0f, hitDisplayPos.z);

            for (int i = _recentPredictedImpacts.Count - 1; i >= 0; i--)
            {
                var recent = _recentPredictedImpacts[i];
                if (now > recent.ExpireTime)
                {
                    _recentPredictedImpacts.RemoveAt(i);
                    continue;
                }

                if (recent.OwnerTeam != ownerTeam)
                    continue;

                float distSq = math.distancesq(recent.DisplayPos, hit);
                if (distSq > maxDistSq)
                    continue;

                ownerNetworkId = recent.OwnerNetworkId;
                _recentPredictedImpacts.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>Expires stale predicted-hit reconcile entries so HashSets cannot grow forever.</summary>
        void PrunePredictedReconcileState()
        {
            float now = Time.unscaledTime;

            while (_clientPredictedHitExpiry.Count > 0)
            {
                var head = _clientPredictedHitExpiry.Peek();
                if (now <= head.ExpireTime)
                    break;
                _clientPredictedHitExpiry.Dequeue();
                _clientPredictedHitSequences.Remove(head.Sequence);
                _clientPredictedHitOwners.Remove(head.Sequence);
            }

            for (int i = _pendingPredictedAdoptSkips.Count - 1; i >= 0; i--)
            {
                if (now > _pendingPredictedAdoptSkips[i].ExpireTime)
                    _pendingPredictedAdoptSkips.RemoveAt(i);
            }

            for (int i = _recentPredictedImpacts.Count - 1; i >= 0; i--)
            {
                if (now > _recentPredictedImpacts[i].ExpireTime)
                    _recentPredictedImpacts.RemoveAt(i);
            }
        }

        static void ResetTrail(GameObject go)
        {
            if (go == null) return;
            var trails = go.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
            {
                if (trails[i] != null)
                    trails[i].Clear();
            }
        }

        /// <summary>
        /// Returns the tracer shell to <see cref="BulletTracerPool"/> (or Destroy if foreign).
        /// </summary>
        static void DestroyTracerGo(in Tracer t)
        {
            if (t.Go != null)
                BulletTracerPool.Return(t.Go);
        }

        void RemoveAtSwap(int index)
        {
            var removed = _tracers[index];
            if (removed.Sequence != 0)
                _indexBySequence.Remove(removed.Sequence);
            // Orphan anticipation destroyed without adopt — free the cap slot.
            else if (removed.IsAnticipation)
                BulletVfxBridge.NotifyAnticipationConsumed();

            int last = _tracers.Count - 1;
            if (index != last)
            {
                var moved = _tracers[last];
                _tracers[index] = moved;
                if (moved.Sequence != 0)
                    _indexBySequence[moved.Sequence] = index;
            }

            _tracers.RemoveAt(last);
        }

        void ClearAllTracers()
        {
            for (int i = 0; i < _tracers.Count; i++)
                DestroyTracerGo(_tracers[i]);
            _tracers.Clear();
        }
    }
}
