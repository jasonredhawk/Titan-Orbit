using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Diagnostics;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Collections;
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
    /// [TITAN-ORBIT] Sequence 0 HitRpcs are ram/grind (no tracer). Non-kill pulses drive
    /// <see cref="ShipRamSparksDriver"/> (one looping stream copied from the focused
    /// bank's impact particles). Kill / plow still play the ship's bullet explosion once.
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
            /// <summary>
            /// World Y of the drawn cannon at fire time. Logical flight stays on Y = 0
            /// with the server. Only the tracer transform uses this.
            /// </summary>
            public float VisualMuzzleY;
            public float3 Velocity;
            /// <summary>
            /// Seconds left before cosmetic despawn. When the spawn request Lifetime is ≤ 0
            /// (planetary defense), this is set to +∞ so only <see cref="MaxDistance"/> culls.
            /// </summary>
            public float RemainingLifetime;
            public float MaxDistance;
            public float Traveled;
            public float Damage;
            /// <summary>Extra Level fire power (pre-bank) for the weapon-type pitch piano.</summary>
            public float FirePowerLive;
            /// <summary>Catalog / unique-component base (top C).</summary>
            public float FirePowerBase;
            /// <summary>Catalog Per Extra Level. MEGA unique weapons are 0.</summary>
            public float FirePowerPerExtra;
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
            /// <summary>
            /// 1 when this straight shot was stepped off-screen this frame and must not be
            /// swept or placed again until it enters the camera rectangle.
            /// </summary>
            public byte SweptOffscreen;
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
            /// <summary>Cached so a seam snap does not GetComponentsInChildren every frame.</summary>
            public TrailRenderer Trail;
            /// <summary>
            /// Extra Titan shells after the primary <see cref="Go"/>, one per other cannon on this turret.
            /// Null when this shot is one bullet. Presentation only — the server round is still one.
            /// </summary>
            public GameObject[] SplitExtras;
            /// <summary>How many visuals this shot draws, including <see cref="Go"/>. 1 = single bullet.</summary>
            public int SplitVisualCount;
            /// <summary>
            /// Root scale for every shell before that cannon's hierarchy scale.
            /// Always the full shot size (fire power ÷ the bank reference). Cannon count does not divide it.
            /// </summary>
            public float SplitVisualScale;
            /// <summary>
            /// Shot-local offset from the logical muzzle to each shell.
            /// Set from each cannon's mesh. Null places the one shell on the logical muzzle.
            /// </summary>
            public Vector3[] SplitLocalOffsets;
            /// <summary>
            /// Local-scale chain of each turret mesh, from that mesh up to the ship root.
            /// Null keeps 1, the same size a regular ship bullet uses.
            /// </summary>
            public float[] SplitMeshScales;
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
        /// Cosmetic spawns drained per presentation frame. Shells are pooled, so this is
        /// activate-and-play, not Instantiates. A volley must flash in the same frame as
        /// the shots — a cap of 3 left later barrels without a muzzle until the queue
        /// caught up, which read as a missing flash.
        /// </summary>
        const int MaxSpawnsPerFrame = 24;

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

        /// <summary>
        /// [UNITY] Domain Reload off leaves the join-load VFX enqueue latch sticky.
        /// Pool stacks are also cleared on SubsystemRegistration — without this reset
        /// the next Play would skip enqueue and combat would Instantiates cold again.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetJoinLoadVfxLatch()
        {
            s_JoinLoadVfxPrewarmQueued = false;
        }

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

            // WebGL drops the burst from the frame a shell was enabled. Replay those
            // before this frame's new shots, after the particle update has run.
            VfxUrpCompat.TickWebGlDeferredShotPlay();
            BulletVisualFactory.TickDeferredAudioReplay();

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
            // Drop burn-impact shells whose DoT ticks have stopped.
            BurnImpactLoop.Tick();

            // Return expired muzzle/impact shells so the next shot can Rent without Instantiates.
            BulletOneShotVfxPool.TickReturns();
            // HitRpc flashes parented onto a rock the visualizer already hid this frame.
            VfxUrpCompat.RescueImpactsHiddenWithParent();

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

            // Straight shots outside the camera rectangle step lifetime only — no hit sweep,
            // no transform write. A shot that will enter the rect this frame still sweeps.
            AdvanceOffscreenStraightTracers(dt);

            // --- Cosmetic hit prediction (hybrid-proxy spheres; no map ToEntityArray) ---
            // Skip while join Instantiates are incomplete — HitRpc still destroys tracers.
            bool canPredictHits = !blockInstantiates && BulletCosmeticHitQuery.TryRefresh();
            bool usedGunBurst = TryAdvanceStraightTracersBurst(
                dt, canPredictHits, hasRef, reference);

            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var t = _tracers[i];
                if (t.Go == null)
                {
                    RemoveAtSwap(i);
                    continue;
                }

                if (t.SweptOffscreen != 0)
                    continue;

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
                else if (usedGunBurst)
                {
                    // Straight tracers were advanced + collided in Burst this frame.
                    displayLogical = t.LogicalPos;
                    displayVel = t.Velocity;
                }
                else
                {
                    // --- Gun / drone / PD: variable-dt dead reckon (already a straight line) ---
                    float3 prevPos = t.LogicalPos;
                    t.RemainingLifetime -= dt;
                    float remainingRange = math.max(0f, t.MaxDistance - t.Traveled);
                    BulletFlight.GetStep(
                        prevPos, t.Velocity, dt, remainingRange, out float3 nextPos, out int substeps);
                    float step = math.distance(prevPos, nextPos);
                    t.Traveled += step;

                    float3 cursor = prevPos;
                    bool cosmeticHit = false;
                    float3 hitPoint = nextPos;
                    for (int s = 0; s < substeps; s++)
                    {
                        float3 sample = BulletFlight.SubstepEnd(prevPos, nextPos, s, substeps);
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
                    if (!cosmeticHit && ToroidalMapEcs.HasValidMapSize)
                        t.LogicalPos = ToroidalMapEcs.Wrap(t.LogicalPos);
                    if (cosmeticHit)
                    {
                        ApplyPredictedHit(i, in t, hitPoint);
                        continue;
                    }

                    if (t.RemainingLifetime <= 0f || BulletFlight.IsRangeExpired(t.Traveled, t.MaxDistance))
                    {
                        DestroyTracerGo(t);
                        RemoveAtSwap(i);
                        continue;
                    }

                    displayLogical = t.LogicalPos;
                    displayVel = t.Velocity;
                }

                // --- Display pose ---
                // Logical flight is the server plane (Y = 0). The shell is drawn at the
                // cannon height captured when the shot spawned.
                int stableKey = unchecked((int)t.Sequence) ^ (t.OwnerNetworkId * 397);
                Vector3 displayPos = ResolveTracerDisplayPosition(
                    ref t, displayLogical, hasRef, reference, stableKey, dt,
                    t.HasPrevTick ? math.saturate(t.TickCarry / RocketPresentationTickDt) : 1f);
                Vector3 prevDisplay = t.Go.transform.position;

                displayPos.y = t.VisualMuzzleY;
                if (BulletCosmeticHitQuery.TryGetMegaFlightLiftY(
                        displayLogical, t.OwnerNetworkId, out float megaY, out float lift))
                    displayPos.y = math.lerp(t.VisualMuzzleY, megaY, lift);
                if (CameraGroundView.IsOutside(displayPos, CameraGroundView.BulletMargin))
                {
                    if (t.Go.activeSelf)
                        t.Go.SetActive(false);
                    _tracers[i] = t;
                    continue;
                }

                if (!t.Go.activeSelf)
                    t.Go.SetActive(true);
                bool snapped = (displayPos - prevDisplay).sqrMagnitude > 40f * 40f;
                if (snapped)
                    ResetTrail(in t);
                Quaternion flightRot = math.lengthsq(displayVel) > 0.0001f
                    ? Quaternion.LookRotation(((Vector3)displayVel).normalized, Vector3.up)
                    : t.Go.transform.rotation;
                ApplyTracerRootScale(in t);
                PlaceTracerShells(in t, displayPos, flightRot);

                if (t.Stretch != null)
                {
                    float progress = t.Traveled / math.max(0.5f, t.MaxDistance);
                    t.Stretch.ApplyTravelProgress(progress);
                }

                _tracers[i] = t;
            }

            // Predicted hits can parent onto a body hidden earlier this frame.
            VfxUrpCompat.RescueImpactsHiddenWithParent();
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
                float3 nextPos = prevPos + t.Velocity * tickDt;
                t.Traveled += math.length(t.Velocity) * tickDt;
                t.Age += tickDt;
                t.RemainingLifetime -= tickDt;
                t.TickCarry -= tickDt;
                steps++;

                if (canPredictHits &&
                    TryPredictCosmeticHit(
                        in t, prevPos, nextPos,
                        out hitPoint,
                        out _,
                        out _,
                        out _,
                        out _))
                {
                    t.LogicalPos = hitPoint;
                    hit = true;
                    displayLogical = t.LogicalPos;
                    displayVel = t.Velocity;
                    return false;
                }

                t.LogicalPos = ToroidalMapEcs.HasValidMapSize
                    ? ToroidalMapEcs.Wrap(nextPos)
                    : nextPos;

                if (t.RemainingLifetime <= 0f || BulletFlight.IsRangeExpired(t.Traveled, t.MaxDistance))
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

                // --- Starblast: reproject onto the drawn barrel (local + every remote observer) ---
                // [TITAN-ORBIT] Never drop a server spawn when reproject fails — fall back to
                // server logical pose. MEGA shots resolve the MEGA hull and its live barrels.
                BulletMuzzlePresentation.TryReprojectSpawn(ref req);
                req.IsDisplaySpace = false;

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
                if (t.Traveled < 2f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Spray direction for grind sparks: toward the local hull when this flash is ours,
        /// else outward from the nearest cached obstacle center.
        /// </summary>
        static float3 EstimateRamSparkNormal(float3 displayPos, float3 logicalHit)
        {
            if (ShipDisplayPose.HasLocalPose)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                float3 toShip = new float3(p.x - displayPos.x, 0f, p.z - displayPos.z);
                float distSq = math.lengthsq(toShip);
                if (distSq > 0.05f && distSq < 18f * 18f)
                    return toShip * math.rsqrt(distSq);
            }

            if (BulletCosmeticHitQuery.TryFindNearestObstacle(logicalHit, out var obstacle))
            {
                float3 center = obstacle.LogicalCenter;
                if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                    center = ToroidalDisplay.ToDisplayPosition(center, reference);
                float3 n = displayPos - center;
                n.y = 0f;
                if (math.lengthsq(n) > 1e-6f)
                    return math.normalize(n);
            }

            return new float3(0f, 0f, 1f);
        }

        /// <summary>
        /// Sequence-0 burn tick. Asteroids have no ghosted burn state, so each damage
        /// step replays the bank impact here. Ships replay from
        /// <see cref="ShipStatusLoopVfxApplier"/> (ghosted tick counter) — playing again
        /// would stack a second explosion on the hull. Floats still run for both.
        /// </summary>
        void DrainBurnTick(in BulletVfxBridge.HitRequest hit, Vector3 hitPos)
        {
            var team = (TeamId)hit.OwnerTeam;
            int bank = math.max(0, hit.BankIndex);
            if (hit.AsteroidHealthAfter >= 0f)
            {
                BurnImpactLoop.Pulse(
                    hit.HitPosition,
                    bank,
                    team,
                    preferNonShip: true);
            }

            var synth = new Tracer { OwnerNetworkId = 0, IsAnticipation = false };
            TryShowHitRpcFloats(
                hitPos, hit.HitPosition, hit.Damage, team,
                hit.AsteroidHealthAfter, hit.PlanetaryDefensePlanetId,
                hit.PlanetaryDefenseSlotIndex, hit.PlanetaryDefenseHealthAfter, in synth);
        }

        /// <summary>
        /// Impact VFX + destroy matching tracer by Sequence.
        /// Falls back to nearest same-team tracer (incl. Sequence=0 anticipation) so orphan
        /// cosmetics do not keep flying through the rock after a real server hit.
        /// Skips duplicate impact flash when client already predicted this Sequence / nearby impact.
        /// Mining floats always use HitRpc <c>AsteroidHealthAfter</c> (never cosmetic-predicted HP).
        /// Turret HP is written in <see cref="BulletHitRpcClientSystem"/> (not here).
        /// Sequence 0 with a shooter id is ram/grind: a looping spark stream via
        /// <see cref="ShipRamSparksDriver"/> — never adopts a tracer. Kill / plow still
        /// rent the bullet impact once. Sequence 0 with no shooter id is a burn tick:
        /// asteroid burns replay the bank impact; ship burns replay from the ghosted
        /// tick counter in <see cref="ShipStatusLoopVfxApplier"/>.
        /// </summary>
        void DrainHits()
        {
            EnsureBank();
            while (BulletVfxBridge.TryDequeueHit(out var hit))
            {
                if (hit.TroopHealthAfter >= 0f &&
                    (hit.TroopSequence != 0 || hit.TroopShipNetworkId > 0))
                {
                    if (hit.TroopSequence != 0)
                        PeopleTransportVfxDriver.ApplyTroopHealth(hit.TroopSequence, hit.TroopHealthAfter);
                }

                Vector3 hitPos = hit.HitPosition;
                if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                    hitPos = ToroidalDisplay.ToDisplayPosition(hitPos, reference);
                hitPos.y = 0f;

                // --- Ram / grind: Sequence 0 means there is no tracer ---
                // [TITAN-ORBIT] Non-kill pulses drive one looping stream copied from this
                // ship's focused impact prefab. Renting the full explosion every pulse
                // stacked Sci-Fi shells (GPU 9→26 ms). Kill / plow still play that boom
                // once. Do not nearest-tracer-adopt — that ate live shots while ramming
                // and firing.
                if (hit.Sequence == 0)
                {
                    // Burn ticks reuse this packet and leave OwnerNetworkId at 0.
                    // Ram/grind always carries the ramming ship.
                    if (hit.OwnerNetworkId == 0)
                    {
                        DrainBurnTick(hit, hitPos);
                        continue;
                    }

                    var ramTeam = (TeamId)hit.OwnerTeam;
                    int ramBank = math.max(0, hit.BankIndex);
                    float ramScale = hit.ScaleMultiplier > 0f ? hit.ScaleMultiplier : 1f;
                    bool killBoom = hit.AsteroidHealthAfter >= 0f && hit.AsteroidHealthAfter <= 0.01f;
                    float3 display = new float3(hitPos.x, 0f, hitPos.z);
                    float3 sparkNormal = EstimateRamSparkNormal(display, hit.HitPosition);
                    ShipRamSparksDriver.NotifyRamContact(
                        display, sparkNormal, ramScale, killBoom, ramBank, ramTeam);
                    // Sequence-0 is also the grind metronome (4 Hz). Local hull SFX is predicted
                    // from contacts; remotes hear these pulses (skip-near-local inside the driver).
                    ShipCollisionSfxDriver.NotifyRemoteRamPulse(
                        display, hit.Damage, killBoom, hit.OwnerNetworkId);
                    if (killBoom)
                    {
                        BulletImpactAttach.PlayAtLogicalPoint(
                            hit.HitPosition, _bank, ramBank, ramTeam, hit.Damage, ramScale,
                            BulletVisualFactory.DefaultImpactDuration,
                            preferNonShip: true,
                            surfaceNormal: sparkNormal);
                    }

                    var ramSynth = new Tracer { OwnerNetworkId = 0, IsAnticipation = false };
                    TryShowHitRpcFloats(
                        hitPos, hit.HitPosition, hit.Damage, ramTeam,
                        hit.AsteroidHealthAfter, hit.PlanetaryDefensePlanetId,
                        hit.PlanetaryDefenseSlotIndex, hit.PlanetaryDefenseHealthAfter, in ramSynth);
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
                    TryShowHitRpcFloats(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, hit.PlanetaryDefensePlanetId,
                        hit.PlanetaryDefenseSlotIndex, hit.PlanetaryDefenseHealthAfter, in synth);
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
                    BulletImpactAttach.PlayAtLogicalPoint(
                        hit.HitPosition, _bank, bankIndex, team, hit.Damage, scaleMul);
                }

                // --- Preferred: exact Sequence from server ---
                if (_indexBySequence.TryGetValue(hit.Sequence, out int idx) &&
                    idx >= 0 && idx < _tracers.Count)
                {
                    var tracer = _tracers[idx];
                    // Always show float on HitRpc — even when VFX was client-predicted.
                    TryShowHitRpcFloats(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, hit.PlanetaryDefensePlanetId,
                        hit.PlanetaryDefenseSlotIndex, hit.PlanetaryDefenseHealthAfter, in tracer);

                    DestroyTracerGo(tracer);
                    RemoveAtSwap(idx);
                    // [TITAN-ORBIT] Cull only far leftover anticipations (energy-lag overfire).
                    // Do NOT wipe the whole team — a multi-cannon volley has sibling tracers that
                    // are still valid when one muzzle's bullet hits first.
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                    continue;
                }

                // --- Fallback: owner+mount, then nearest same-team tracer (orphan anticipation) ---
                // [TITAN-ORBIT] A 12u window missed MEGA range and Local Host double-hit used to
                // destroy a sibling volley tracer. Prefer the barrel that fired.
                if (TryFindOwnerMountTracerIndex(hit.OwnerNetworkId, hit.MountIndex, hit.OwnerTeam, out int nearIdx)
                    || TryFindNearestTracerIndex(hitPos, hit.OwnerTeam, maxDistance: 48f, out nearIdx))
                {
                    var nearTracer = _tracers[nearIdx];
                    TryShowHitRpcFloats(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, hit.PlanetaryDefensePlanetId,
                        hit.PlanetaryDefenseSlotIndex, hit.PlanetaryDefenseHealthAfter, in nearTracer);

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
                    TryShowHitRpcFloats(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, hit.PlanetaryDefensePlanetId,
                        hit.PlanetaryDefenseSlotIndex, hit.PlanetaryDefenseHealthAfter, in synth);
                    ClearStaleAnticipationTracers(hit.OwnerTeam);
                }
                else
                {
                    // No tracer to destroy — still apply asteroid teardown from HitRpc.
                    var synth = new Tracer { OwnerNetworkId = 0, IsAnticipation = false };
                    TryShowHitRpcFloats(
                        hitPos, hit.HitPosition, hit.Damage, (TeamId)hit.OwnerTeam,
                        hit.AsteroidHealthAfter, hit.PlanetaryDefensePlanetId,
                        hit.PlanetaryDefenseSlotIndex, hit.PlanetaryDefenseHealthAfter, in synth);
                }
            }
        }

        /// <summary>
        /// HitRpc path: asteroid mining floats, or ship-hull damage floats.
        /// <para>
        /// [TITAN-ORBIT] <paramref name="asteroidHealthAfter"/> comes from the server on
        /// <see cref="BulletHitRpc"/> — do not subtract from lagging ghost Health. Seed-hydrate
        /// asteroids are not ghosts: <see cref="BulletHitRpcClientSystem"/> writes Health /
        /// IsDestroyed; this path shows floats (local shots) and hides/tears down the hybrid GO
        /// on kill. Do not DestroyEntity here — sim-group soft-destroy owns ECS teardown.
        /// Ship hits pass &lt; 0 and <paramref name="planetaryDefensePlanetId"/> 0 — we
        /// surface-fit the hull and show accumulated damage on that ship.
        /// Planetary-defense hits pass a planet id and show the same −damage float on the pad.
        /// </para>
        /// </summary>
        static void TryShowHitRpcFloats(
            Vector3 hitDisplayPos,
            float3 hitLogicalPos,
            float damage,
            TeamId ownerTeam,
            float asteroidHealthAfter,
            int planetaryDefensePlanetId,
            int planetaryDefenseSlotIndex,
            float planetaryDefenseHealthAfter,
            in Tracer tracer)
        {
            // Pad HP is applied in BulletHitRpcClientSystem. The float parks on the hybrid turret.
            if (planetaryDefensePlanetId > 0)
            {
                EcsFloatingCountPresenter.TryNotifyDefenseTurretBulletHit(
                    planetaryDefensePlanetId,
                    planetaryDefenseSlotIndex,
                    damage,
                    planetaryDefenseHealthAfter,
                    ownerTeam,
                    tracer.OwnerNetworkId);
                return;
            }

            if (asteroidHealthAfter < 0f)
            {
                TryShowShipFloatForHitRpc(hitDisplayPos, damage, ownerTeam);
                return;
            }

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
                    authoritativeRemainingHealth: asteroidHealthAfter,
                    impactWorldPosition: hitDisplayPos);
            }

            // --- Kill: do not hide from HitRpc ---
            // [TITAN-ORBIT] Surface-fit on a packed belt can hide a neighbor while DestroyRpc
            // culls the rock the server actually killed (two client hides, one server destroy,
            // leftover invisible hull). Authoritative teardown is AsteroidDestroyedRpc only.
            if (asteroidHealthAfter <= 0.01f)
                return;
        }

        /// <summary>
        /// Ship-hull damage float from HitRpc. Not gated on local-shot — incoming turret
        /// fire must show on the victim, including the local player.
        /// </summary>
        static void TryShowShipFloatForHitRpc(
            Vector3 hitDisplayPos,
            float damage,
            TeamId ownerTeam)
        {
            if (damage <= 0.01f)
                return;
            if (!BulletCosmeticHitQuery.TryFindShipAtImpact(hitDisplayPos, out Entity shipEntity))
                return;

            EcsFloatingCountPresenter.TryNotifyShipBulletHit(shipEntity, damage, ownerTeam);
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
            // --- Bind authority metadata — keep presentation flight for regular ships ---
            // [TITAN-ORBIT] Do not overwrite Velocity with lagged server aim on regular hulls.
            // Server mounts often bake LocalRotation ≈ identity (hull-forward) while the live
            // weapon component faces a different way — adopting that mid-flight made sequential
            // shots look mis-aimed. MEGA auto-aim is the opposite: anticipation is the one that
            // parks hull-forward while the server ray already hit the defense turret.
            if (ShouldPreferServerMegaAim(in req, adopted.Velocity))
            {
                adopted.Velocity = req.Velocity;
                float3 planar = PlanarFlightPos(req.SpawnPosition);
                adopted.LogicalPos = planar;
                adopted.SpawnPos = planar;
                adopted.PrevLogicalPos = planar;
                adopted.PrevVelocity = req.Velocity;
                adopted.Traveled = 0f;
                if (math.abs(adopted.VisualMuzzleY) < 0.001f)
                    adopted.VisualMuzzleY = ResolveVisualCannonY(in req);
            }
            adopted.Sequence = req.Sequence;
            adopted.IsAnticipation = false;
            adopted.OwnerNetworkId = req.OwnerNetworkId > 0 ? req.OwnerNetworkId : adopted.OwnerNetworkId;
            adopted.MountIndex = req.MountIndex;
            adopted.BankIndex = req.BankIndex;
            adopted.ScaleMultiplier = req.ScaleMultiplier > 0f ? req.ScaleMultiplier : adopted.ScaleMultiplier;
            // Cannon count does not shrink the shell. Each mesh keeps the full shot size.
            adopted.SplitVisualScale = adopted.ScaleMultiplier;
            adopted.Damage = req.Damage;
            adopted.FirePowerLive = req.FirePowerLive;
            adopted.FirePowerBase = req.FirePowerBase;
            adopted.FirePowerPerExtra = req.FirePowerPerExtra;
            // [TITAN-ORBIT] Lifetime <= 0 = distance-only (PD turrets); do not clamp to 0.05s.
            adopted.RemainingLifetime = ResolveTracerLifetime(req.Lifetime);
            adopted.MaxDistance = math.max(0.5f, req.MaxDistance);
            // Regular ships keep Traveled / LogicalPos (presentation muzzle). MEGA retarget
            // above already snapped onto the server ray.

            _tracers[bestIndex] = adopted;
            _indexBySequence[req.Sequence] = bestIndex;
            // Anticipation slot consumed — frees a Cap for ClientLocalBulletVfxBridge.
            BulletVfxBridge.NotifyAnticipationConsumed();
            return true;
        }

        /// <summary>
        /// MEGA server ray disagrees with hull-forward anticipation — take the authority heading.
        /// Regular ships keep presentation aim (live GO vs bake LocalRotation).
        /// </summary>
        static bool ShouldPreferServerMegaAim(in BulletVfxBridge.SpawnRequest req, float3 anticipationVel)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;
            if (!BulletMuzzlePresentation.TryResolveShooterHull(
                    world.EntityManager, req.OwnerNetworkId, req.MountIndex,
                    out Entity hull, out _))
                return false;
            if (!world.EntityManager.HasComponent<MegaShipState>(hull)
                || !world.EntityManager.GetComponentData<MegaShipState>(hull).IsMega)
                return false;

            float3 a = anticipationVel;
            float3 b = req.Velocity;
            a.y = 0f;
            b.y = 0f;
            float la = math.length(a);
            float lb = math.length(b);
            if (la < 0.1f || lb < 0.1f)
                return lb > la;
            // ~8° — parked hull-forward vs auto-aim at a pad is typically 20°+.
            return math.dot(a / la, b / lb) < 0.99f;
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
        /// <summary>
        /// Orphan anticipation for this shooter + barrel. Prefer this over nearest-point fallback
        /// so a MEGA volley sibling is not destroyed by the wrong HitRpc.
        /// </summary>
        bool TryFindOwnerMountTracerIndex(int ownerNetworkId, int mountIndex, byte ownerTeam, out int index)
        {
            index = -1;
            if (ownerNetworkId <= 0 || mountIndex < 0)
                return false;

            float bestTravel = float.MaxValue;
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.OwnerTeam != ownerTeam || t.Go == null)
                    continue;
                if (t.OwnerNetworkId != ownerNetworkId || t.MountIndex != mountIndex)
                    continue;
                if (t.Traveled < bestTravel)
                {
                    bestTravel = t.Traveled;
                    index = i;
                }
            }

            return index >= 0;
        }

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
            // Server / cosmetic sweep stays on the hit plane. The shell is drawn at the cannon.
            float cannonY = ResolveVisualCannonY(in req);
            float3 logicalSpawn = PlanarFlightPos(req.SpawnPosition);
            Vector3 spawnDisplay = logicalSpawn;
            if (!req.IsDisplaySpace && ToroidalDisplay.TryGetReferencePosition(out var reference))
                spawnDisplay = ToroidalDisplay.ToDisplayPosition(logicalSpawn, reference);
            spawnDisplay.y = cannonY;

            Quaternion rot = math.lengthsq(req.Velocity) > 0.0001f
                ? Quaternion.LookRotation(((Vector3)req.Velocity).normalized, Vector3.up)
                : Quaternion.identity;

            // Full shot size on every shell. Extra shells exist only when this turret has more cannons.
            float drawScale = scaleMul;
            int splitCount = 1;
            Vector3[] muzzleLocalOffsets = null;
            float[] muzzleMeshScales = null;
            if (TryResolveTurretMuzzles(
                    in req, spawnDisplay, rot, scaleMul,
                    out int turretCount, out float turretScale, out Vector3[] turretOffsets))
            {
                drawScale = turretScale;
                splitCount = turretCount;
                muzzleLocalOffsets = turretOffsets;
                muzzleMeshScales = s_TurretShellScales;
                s_TurretShellScales = null;
            }

            // --- Muzzle flash at fire origin ---
            // Every shot plays it, including nose-touch. Skipping when a 0.5u probe
            // hit nearby geometry dropped the flash on a lot of barrels.
            float cameraScale = ResolveMegaCameraVisualScale();
            float pianoLive = req.FirePowerLive > 0.01f ? req.FirePowerLive : req.Damage;
            BulletVisualFactory.PlayMuzzleVfx(
                spawnDisplay,
                req.Velocity,
                _bank,
                bankIndex,
                team,
                drawScale * cameraScale,
                pianoLive,
                req.FirePowerBase,
                req.FirePowerPerExtra);

            // --- Fire start one-shot ---
            // [TITAN-ORBIT] Shared AudioManager clip. Not the Sci-Fi muzzle prefab —
            // Fireballs / several banks have muzzleParticle = null, so pitching a
            // muzzle AudioSource would play nothing. Pitch uses Extra Level bookends,
            // not bank-scaled plan.Damage.
            AudioManager.GetOrFind()?.PlayWeaponShootSound(
                BulletVisualFactory.GetFirePowerSoundPitch(
                    pianoLive, req.FirePowerBase, req.FirePowerPerExtra),
                BulletVisualFactory.GetFirePowerShootVolume(req.Damage)
                    * GameplaySfxProximity.VolumeAt(spawnDisplay));

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
                    drawScale, bulletSpeed, req.Damage, noTrail: false);
            }
            else if (projectilePrefab != null)
            {
                // Rebuild shells whose particles were rewritten (glow size/speed, or a
                // team-colour restamp over the authored prefab).
                VfxUrpCompat.EnsureAuthoredProjectileVisual(go, projectilePrefab);
            }

            go.transform.SetPositionAndRotation(spawnDisplay, rot);

            // Scale the tracer ROOT (same as muzzle/impact). Scaling only the visual child
            // left World-space particles and trail widths at ship size.
            // Bank prefabs are already coloured per team. Only the procedural sphere
            // (mobile / missing prefab) gets a flat team colour.
            GameObject visual = go.transform.childCount > 0
                ? go.transform.GetChild(0).gameObject
                : go;
            float visualScale = BulletVisualFactory.GetBulletVisualScale(_bank, drawScale, bankIndex)
                                * cameraScale;
            if (projectilePrefab == null)
                BulletVisualFactory.ApplyColorToVisual(visual, BulletVisualFactory.GetTeamBulletColor(team));
            // Root scale is this shell's share of fire power. Bullet type does not change it.
            VfxUrpCompat.ApplyImpactVisualScale(go, visualScale, lockUniformParticleSize: true);

            GameObject[] splitExtras = null;
            if (splitCount > 1)
            {
                splitExtras = RentSplitExtras(
                    projectilePrefab, splitCount - 1, bankIndex, team,
                    drawScale, visualScale, bulletSpeed, req.Damage);
                splitCount = 1 + (splitExtras != null ? splitExtras.Length : 0);
            }

            // Length stays at the prefab size. Stretch-in-flight (0.5 → 2) made a held
            // burst look like small, medium, and large bullets. DPS scale is the root.
            ClientBulletStretchVisual stretch = go.GetComponent<ClientBulletStretchVisual>();
            if (stretch != null)
            {
                stretch.Collapse();
                stretch = null;
            }

            var tracer = new Tracer
            {
                Go = go,
                Sequence = req.Sequence,
                OwnerNetworkId = req.OwnerNetworkId,
                LogicalPos = logicalSpawn,
                SpawnPos = logicalSpawn,
                VisualMuzzleY = cannonY,
                Velocity = req.Velocity,
                // [TITAN-ORBIT] Lifetime <= 0 = distance-only (PD); ship guns keep a positive timer.
                RemainingLifetime = ResolveTracerLifetime(req.Lifetime),
                MaxDistance = math.max(0.5f, req.MaxDistance),
                Traveled = 0f,
                Damage = req.Damage,
                FirePowerLive = req.FirePowerLive,
                FirePowerBase = req.FirePowerBase,
                FirePowerPerExtra = req.FirePowerPerExtra,
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
                Trail = go.GetComponentInChildren<TrailRenderer>(true),
                SplitExtras = splitExtras,
                SplitVisualCount = splitCount,
                // Hit sweep keeps ScaleMultiplier (the full shot). Each shell draws that same size.
                SplitVisualScale = drawScale,
                SplitLocalOffsets = muzzleLocalOffsets,
                SplitMeshScales = muzzleMeshScales,
            };

            if (splitCount > 1 || muzzleLocalOffsets != null)
                PlaceTracerShells(in tracer, spawnDisplay, rot);

            // Enable after the cannon pose so trails do not streak from the pool origin.
            // Audio on before enable so a shell that was a silent sibling can PlayOnAwake again.
            SetShellAudioEnabled(go, true);
            VfxUrpCompat.PrepareVfxInstance(go);
            BulletVisualFactory.SetAudioPitchInHierarchy(
                go, BulletVisualFactory.GetFirePowerSoundPitch(
                    pianoLive, req.FirePowerBase, req.FirePowerPerExtra),
                spawnDisplay);
            if (splitExtras != null)
            {
                for (int s = 0; s < splitExtras.Length; s++)
                {
                    if (splitExtras[s] == null)
                        continue;
                    SetShellAudioEnabled(splitExtras[s], false);
                    VfxUrpCompat.PrepareVfxInstance(splitExtras[s]);
                }
            }

            // Seed observer-hull samples so the first frame can already ride the camera
            // (incoming remote rockets included — no local-owner check).
            if (req.Homing != 0 && TryGetObserverHullDisplay(out float3 hull))
            {
                tracer.PrevLogicalPos = logicalSpawn;
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

        /// <summary>
        /// Process-wide latch so join-load warmup and LateUpdate <see cref="EnsureBank"/>
        /// enqueue the VFX Instantiates queue only once.
        /// </summary>
        static bool s_JoinLoadVfxPrewarmQueued;

        bool _oneShotPoolPrewarmQueued;

        /// <summary>
        /// Hit-plane copy of a spawn. Server collision already ignores barrel height;
        /// cosmetic sweeps use this so they stay on that same Y = 0 plane.
        /// </summary>
        static float3 PlanarFlightPos(float3 pos)
        {
            pos.y = 0f;
            return pos;
        }

        /// <summary>
        /// World Y of the cannon this shot is leaving. Presentation only.
        /// Titans read the live cannon-mesh tip. Every other hull reads the live
        /// weapon transform (BankPivot included). Misses stay on the play plane.
        /// </summary>
        static float ResolveVisualCannonY(in BulletVfxBridge.SpawnRequest req)
        {
            if (req.MountIndex < 0)
                return 0f;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return 0f;

            EntityManager em = world.EntityManager;
            if (!BulletMuzzlePresentation.TryResolveShooterHull(
                    em, req.OwnerNetworkId, req.MountIndex, out Entity hull, out int mountIndex))
                return 0f;
            if (!BulletMuzzlePresentation.TryGetVisualCannonHeight(em, hull, mountIndex, out float worldY))
                return 0f;
            return worldY;
        }

        /// <summary>
        /// Root scale is this shell's share of fire power. A single bullet uses the full shot.
        /// Bullet type does not change it. Split siblings share that same scale.
        /// </summary>
        void ApplyTracerRootScale(in Tracer t)
        {
            if (t.Go == null)
                return;
            float scaleMul = t.SplitVisualScale > 0f
                ? t.SplitVisualScale
                : (t.ScaleMultiplier > 0f ? t.ScaleMultiplier : 1f);
            int bankIndex = math.max(0, t.BankIndex);
            float visual = BulletVisualFactory.GetBulletVisualScale(_bank, scaleMul, bankIndex)
                * ResolveMegaCameraVisualScale();
            ApplyShellScale(t.Go, visual, MeshScaleAt(in t, 0));
            if (t.SplitExtras == null)
                return;
            for (int i = 0; i < t.SplitExtras.Length; i++)
            {
                if (t.SplitExtras[i] != null)
                    ApplyShellScale(t.SplitExtras[i], visual, MeshScaleAt(in t, i + 1));
            }
        }

        /// <summary>
        /// Regular bullets use 1. A turret shell multiplies by the firing mesh's local-scale
        /// chain up to the ship root — the presentation scale on the root is already in the
        /// world position, so it is not applied again here.
        /// </summary>
        static float MeshScaleAt(in Tracer t, int index)
        {
            if (t.SplitMeshScales == null || index < 0 || index >= t.SplitMeshScales.Length)
                return 1f;
            float scale = t.SplitMeshScales[index];
            return scale > 0.01f ? scale : 1f;
        }

        static void ApplyShellScale(GameObject shell, float visual, float meshScale)
        {
            if (shell == null)
                return;
            Vector3 target = Vector3.one * math.max(0.05f, visual * meshScale);
            if ((shell.transform.localScale - target).sqrMagnitude > 0.0001f)
                shell.transform.localScale = target;
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

        /// <summary>
        /// Maps server/RPC Lifetime onto cosmetic RemainingLifetime.
        /// Positive values keep the ship-gun timer (clamped so tiny floats do not despawn instantly).
        /// Lifetime ≤ 0 means distance-only cull (planetary defense) — RemainingLifetime = +∞ so
        /// the age check never fires and MaxDistance alone ends the tracer.
        /// </summary>
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
            // Join-load warmup may have already enqueued — TryEnqueue is idempotent.
            if (!_oneShotPoolPrewarmQueued)
                _oneShotPoolPrewarmQueued = TryEnqueueJoinLoadVfxPrewarm();
        }

        /// <summary>
        /// Enqueues unique muzzle / impact Instantiates and a small tracer prewarm so the
        /// loading overlay can drain <see cref="BulletOneShotVfxPool.TickPrewarm"/> before
        /// Join Team. Safe to call from the warmup worker or from <see cref="EnsureBank"/>.
        /// </summary>
        /// <returns>
        /// True when the queue is latched (including mobile skip). False only when the bank
        /// asset is still missing so the caller can retry next frame.
        /// </returns>
        public static bool TryEnqueueJoinLoadVfxPrewarm()
        {
            if (s_JoinLoadVfxPrewarmQueued)
                return true;

            if (Application.isMobilePlatform)
            {
                s_JoinLoadVfxPrewarmQueued = true;
                return true;
            }

            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return false;

            BulletVisualScale.ActiveUpgradeVisualScaleMultiplier = bank.UpgradeVisualScaleMultiplier;
            EnqueueOneShotVfxPoolPrewarm(bank);
            BulletTracerPool.PrewarmFromBank(bank, categoryCap: 4, perPrefab: 6);
            s_JoinLoadVfxPrewarmQueued = true;
            return true;
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
        /// Straight shots whose current point and this frame's step both sit outside the
        /// camera ground rectangle only age and move. They are not swept and their
        /// transforms are not written. A shot that will cross into the rect is left for the sweep.
        /// </summary>
        void AdvanceOffscreenStraightTracers(float dt)
        {
            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                var t = _tracers[i];
                if (t.Go == null || t.Homing != 0)
                    continue;

                float3 stepEnd = t.LogicalPos + t.Velocity * dt;
                bool outside = CameraGroundView.IsOutside(t.LogicalPos, CameraGroundView.BulletMargin)
                    && CameraGroundView.IsOutside(stepEnd, CameraGroundView.BulletMargin);
                if (!outside)
                {
                    if (t.SweptOffscreen != 0)
                    {
                        t.SweptOffscreen = 0;
                        if (!t.Go.activeSelf)
                            t.Go.SetActive(true);
                        _tracers[i] = t;
                    }

                    continue;
                }

                t.RemainingLifetime -= dt;
                float remainingRange = math.max(0f, t.MaxDistance - t.Traveled);
                BulletFlight.GetStep(
                    t.LogicalPos, t.Velocity, dt, remainingRange, out float3 nextPos, out _);
                t.Traveled += math.distance(t.LogicalPos, nextPos);
                t.LogicalPos = nextPos;
                if (ToroidalMapEcs.HasValidMapSize)
                    t.LogicalPos = ToroidalMapEcs.Wrap(t.LogicalPos);

                if (t.RemainingLifetime <= 0f || BulletFlight.IsRangeExpired(t.Traveled, t.MaxDistance))
                {
                    DestroyTracerGo(t);
                    RemoveAtSwap(i);
                    continue;
                }

                t.SweptOffscreen = 1;
                if (t.Go.activeSelf)
                    t.Go.SetActive(false);
                _tracers[i] = t;
            }
        }

        /// <summary>
        /// Burst-advances every straight tracer (mega volleys). Homing stays in LateUpdate.
        /// High-to-low apply so <see cref="RemoveAtSwap"/> keeps remaining indices stable.
        /// </summary>
        bool TryAdvanceStraightTracersBurst(float dt, bool canPredictHits, bool hasRef, Vector3 reference)
        {
            _ = hasRef;
            _ = reference;
            if (!canPredictHits)
                return false;

            int gunN = 0;
            for (int i = 0; i < _tracers.Count; i++)
            {
                var t = _tracers[i];
                if (t.Go != null && t.Homing == 0 && t.SweptOffscreen == 0)
                    gunN++;
            }

            if (gunN <= 0)
                return false;

            var requests = new NativeArray<CosmeticSweepRequest>(gunN, Allocator.TempJob);
            var results = new NativeArray<CosmeticSweepResult>(gunN, Allocator.TempJob);
            var tracerIndex = new NativeArray<int>(gunN, Allocator.TempJob);
            try
            {
                int w = 0;
                int localNetworkId = EcsGameBridge.GetLocalNetworkId();
                for (int i = 0; i < _tracers.Count; i++)
                {
                    var t = _tracers[i];
                    if (t.Go == null || t.Homing != 0 || t.SweptOffscreen != 0)
                        continue;
                    tracerIndex[w] = i;
                    requests[w] = new CosmeticSweepRequest
                    {
                        Position = t.LogicalPos,
                        Velocity = t.Velocity,
                        Dt = dt,
                        Traveled = t.Traveled,
                        RemainingLifetime = t.RemainingLifetime,
                        MaxDistance = t.MaxDistance,
                        ScaleMultiplier = t.ScaleMultiplier > 0f ? t.ScaleMultiplier : 1f,
                        OwnerNetworkId = t.OwnerNetworkId,
                        LocalNetworkId = localNetworkId,
                        OwnerTeam = t.OwnerTeam,
                        DamageFilter = t.DamageFilter,
                        HealFriendly = BulletBankCombatLogic.HasHealFriendly(t.BankIndex) ? (byte)1 : (byte)0,
                    };
                    w++;
                }

                if (!BulletCosmeticHitQuery.TryAdvanceStraightTracers(requests, results))
                    return false;

                for (int r = gunN - 1; r >= 0; r--)
                {
                    int i = tracerIndex[r];
                    if (i < 0 || i >= _tracers.Count)
                        continue;
                    var t = _tracers[i];
                    if (t.Go == null)
                    {
                        RemoveAtSwap(i);
                        continue;
                    }

                    var outcome = results[r];
                    if (outcome.Outcome == CosmeticSweepResult.Hit)
                    {
                        ApplyPredictedHit(i, in t, outcome.HitPoint);
                        continue;
                    }

                    if (outcome.Outcome == CosmeticSweepResult.Expire)
                    {
                        DestroyTracerGo(t);
                        RemoveAtSwap(i);
                        continue;
                    }

                    t.LogicalPos = ToroidalMapEcs.HasValidMapSize
                        ? ToroidalMapEcs.Wrap(outcome.NewPos)
                        : outcome.NewPos;
                    t.Traveled = outcome.NewTraveled;
                    t.RemainingLifetime = outcome.NewLifetime;
                    _tracers[i] = t;
                }

                return true;
            }
            finally
            {
                if (requests.IsCreated)
                    requests.Dispose();
                if (results.IsCreated)
                    results.Dispose();
                if (tracerIndex.IsCreated)
                    tracerIndex.Dispose();
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
                isDisplaySpace: false,
                out hitPoint,
                out hitKind,
                out hitEntity,
                out hitPlanetId,
                out hitSlotIndex,
                t.DamageFilter,
                t.ScaleMultiplier,
                t.BankIndex,
                allowSelfHarm: TitanOrbitDebugFlags.IsHomingSelfHarmArmed(t.Homing, t.Age));
        }

        /// <summary>
        /// Plays impact VFX, records reconcile keys, destroys the tracer.
        /// Does not write damage / HP — server HitRpc still owns mining floats
        /// (<c>AsteroidHealthAfter</c>) and turret HP (<see cref="PlanetaryDefenseClientHealthSync"/>).
        /// </summary>
        void ApplyPredictedHit(int tracerIndex, in Tracer t, float3 hitPoint)
        {
            EnsureBank();

            var team = (TeamId)t.OwnerTeam;
            int bankIndex = math.max(0, t.BankIndex);
            float scaleMul = t.ScaleMultiplier > 0f ? t.ScaleMultiplier : 1f;

            // --- Surface + follow parent (same tile as the drawn body) ---
            Transform attachParent = null;
            Vector3 hitDisplay;
            if (BulletCosmeticHitQuery.TryFindNearestObstacle(hitPoint, out var obstacle) &&
                BulletImpactAttach.TryResolve(in obstacle, hitPoint, out attachParent, out hitDisplay))
            {
                // resolved
            }
            else
            {
                hitDisplay = hitPoint;
                if (!t.IsDisplaySpace && ToroidalDisplay.TryGetReferencePosition(out var reference))
                    hitDisplay = ToroidalDisplay.ToDisplayPosition(hitPoint, reference);
            }

            // Collapse stretch and snap the slug onto the flash so the tip cannot overshoot.
            if (t.Stretch != null)
                t.Stretch.Collapse();
            if (t.Go != null)
                t.Go.transform.position = hitDisplay;

            float impactPianoLive = t.FirePowerLive > 0.01f ? t.FirePowerLive : t.Damage;
            BulletVisualFactory.SpawnBulletImpactVfx(
                hitDisplay, _bank, bankIndex, team, impactPianoLive, scaleMul, attachParent,
                0f, default, t.FirePowerBase, t.FirePowerPerExtra);

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

            // Spatial match is only for anticipation (Sequence still 0). A sequenced
            // hit is deduped by _clientPredictedHitSequences. Leaving a 14u zone here
            // swallowed the next HitRpc that prediction missed — incoming fire on the
            // local hull, or a cosmetic miss — so that shot never played an impact.
            if (t.Sequence == 0)
            {
                _recentPredictedImpacts.Add(new RecentPredictedImpact
                {
                    DisplayPos = hitDisplay,
                    OwnerTeam = t.OwnerTeam,
                    OwnerNetworkId = t.OwnerNetworkId,
                    ExpireTime = now + PredictedHitTtlSeconds,
                });
            }

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

        static void ResetTrail(in Tracer t)
        {
            if (t.Trail != null)
                t.Trail.Clear();
            if (t.SplitExtras == null)
                return;
            for (int i = 0; i < t.SplitExtras.Length; i++)
            {
                GameObject extra = t.SplitExtras[i];
                if (extra == null)
                    continue;
                TrailRenderer trail = extra.GetComponentInChildren<TrailRenderer>(true);
                if (trail != null)
                    trail.Clear();
            }
        }

        /// <summary>
        /// Returns the tracer shell to <see cref="BulletTracerPool"/> (or Destroy if foreign).
        /// Split siblings are the same kind of shell — still one logical bullet.
        /// </summary>
        static void DestroyTracerGo(in Tracer t)
        {
            if (t.SplitExtras != null)
            {
                for (int i = 0; i < t.SplitExtras.Length; i++)
                {
                    if (t.SplitExtras[i] != null)
                        BulletTracerPool.Return(t.SplitExtras[i]);
                }
            }

            if (t.Go != null)
                BulletTracerPool.Return(t.Go);
        }

        static readonly List<Vector3> s_TurretMuzzleScratch = new List<Vector3>(8);
        static readonly List<float> s_TurretMuzzleScales = new List<float>(8);
        static readonly List<Transform> s_CannonScratch = new List<Transform>(8);

        /// <summary>
        /// One shell per real cannon mesh under this turret. Named gun / cannon / bullet
        /// objects are containers; the shell sits on one mesh inside and keeps the full
        /// shot size times that mesh's hierarchy scale. Still one server bullet.
        /// </summary>
        static bool TryResolveTurretMuzzles(
            in BulletVfxBridge.SpawnRequest req,
            Vector3 spawnDisplay,
            Quaternion shotRotation,
            float fullScale,
            out int spawnCount,
            out float eachScale,
            out Vector3[] localOffsets)
        {
            spawnCount = 1;
            eachScale = fullScale;
            localOffsets = null;
            if (fullScale <= 0.01f || req.Homing != 0 || req.MountIndex < 0
                || RocketHomingFire.IsRocketBank(req.BankIndex))
                return false;

            MegaShipCatalog catalog = MegaShipCatalog.Load();
            if (catalog == null || !catalog.splitShotBulletVisual)
                return false;
            if (!IsTitanShooter(req.OwnerNetworkId))
                return false;
            if (!TryCollectTurretMuzzlePositions(req.OwnerNetworkId, req.MountIndex, s_TurretMuzzleScratch, s_TurretMuzzleScales))
                return false;

            int count = s_TurretMuzzleScratch.Count;
            if (count <= 0)
                return false;

            // The shell that owns the tracer is the cannon this shot is leaving.
            int lead = 0;
            float leadSq = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float sq = (s_TurretMuzzleScratch[i] - spawnDisplay).sqrMagnitude;
                if (sq < leadSq)
                {
                    leadSq = sq;
                    lead = i;
                }
            }

            if (lead != 0)
            {
                Vector3 swapPoint = s_TurretMuzzleScratch[0];
                s_TurretMuzzleScratch[0] = s_TurretMuzzleScratch[lead];
                s_TurretMuzzleScratch[lead] = swapPoint;
                float swapScale = s_TurretMuzzleScales[0];
                s_TurretMuzzleScales[0] = s_TurretMuzzleScales[lead];
                s_TurretMuzzleScales[lead] = swapScale;
            }

            localOffsets = new Vector3[count];
            // Every cannon draws the full shot. Its own hierarchy scale is applied later.
            s_TurretShellScales = new float[count];
            Quaternion inv = Quaternion.Inverse(shotRotation);
            for (int i = 0; i < count; i++)
            {
                localOffsets[i] = inv * (s_TurretMuzzleScratch[i] - spawnDisplay);
                s_TurretShellScales[i] = s_TurretMuzzleScales[i];
            }

            spawnCount = count;
            eachScale = fullScale;
            return true;
        }

        /// <summary>
        /// Display-space tip of the cannon mesh this Titan mount is firing.
        /// Named gun / cannon / bullet objects are not the muzzle — the leading mesh
        /// corner along <paramref name="aimForward"/> is. The yaw pivot is not used.
        /// </summary>
        public static bool TryGetLiveFiringTip(
            EntityManager em,
            Entity shipEntity,
            int mountIndex,
            float3 aimForward,
            out float3 tip,
            out float3 hullDisplay)
        {
            tip = default;
            hullDisplay = default;
            if (!em.Exists(shipEntity))
                return false;

            EcsWorldVisualizer visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null || !visualizer.TryGetProxy(shipEntity, out GameObject proxy) || proxy == null)
                return false;

            MegaShipWeaponVisualBinding binding = MegaShipWeaponVisualBinding.Ensure(proxy, shipEntity);
            if (binding == null || binding.YawRoots == null
                || mountIndex < 0 || mountIndex >= binding.YawRoots.Length)
                return false;

            Transform start = binding.Barrels != null
                && mountIndex < binding.Barrels.Length
                && binding.Barrels[mountIndex] != null
                ? binding.Barrels[mountIndex]
                : binding.YawRoots[mountIndex];
            Transform turret = FindTurretRoot(start, proxy.transform);
            if (turret == null)
                turret = start;

            // The drawn barrel, not the yaw pivot. Aim is only a fallback when that
            // forward is degenerate — the tip has to follow the mesh that is turning.
            Vector3 forward = start.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                forward = new Vector3(aimForward.x, 0f, aimForward.z);
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;
            else
                forward.Normalize();

            var best = new MeshPick();
            WalkMeshes(turret, proxy.transform, 0, turret.position, forward, ref best);
            if (!best.Found)
                return false;

            tip = best.Point;
            hullDisplay = proxy.transform.position;
            return true;
        }

        /// <summary>Per-shell world scale captured with the muzzle list. Read once in CreateTracer.</summary>
        static float[] s_TurretShellScales;

        /// <summary>
        /// One world point and world scale per cannon. Empty named objects are skipped.
        /// </summary>
        static bool TryCollectTurretMuzzlePositions(int ownerNetworkId, int mountIndex, List<Vector3> into, List<float> scales)
        {
            into.Clear();
            scales.Clear();
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            EntityManager em = world.EntityManager;
            if (!BulletMuzzlePresentation.TryResolveShooterHull(
                    em, ownerNetworkId, mountIndex, out Entity hull, out _))
                return false;

            EcsWorldVisualizer visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null || !visualizer.TryGetProxy(hull, out GameObject proxy) || proxy == null)
                return false;

            MegaShipWeaponVisualBinding binding = MegaShipWeaponVisualBinding.Ensure(proxy, hull);
            if (binding == null || binding.YawRoots == null
                || mountIndex < 0 || mountIndex >= binding.YawRoots.Length)
                return false;

            Transform start = binding.Barrels != null
                && mountIndex < binding.Barrels.Length
                && binding.Barrels[mountIndex] != null
                ? binding.Barrels[mountIndex]
                : binding.YawRoots[mountIndex];
            Transform turret = FindTurretRoot(start, proxy.transform);
            if (turret == null)
                return false;

            EmitCannonMuzzles(turret, proxy.transform, into, scales);
            return into.Count > 0;
        }

        /// <summary>
        /// Outermost object named like a turret (Turret, Turret 1, Turret 3) under this barrel.
        /// Stops at the Turrets folder so one shot does not pick up every gun on the hull.
        /// </summary>
        static Transform FindTurretRoot(Transform start, Transform hull)
        {
            Transform found = null;
            Transform t = start;
            while (t != null && t != hull)
            {
                if (IsTurretsFolder(t.name))
                    break;
                if (NameContains(t.name, "turret"))
                    found = t;
                t = t.parent;
            }

            return found;
        }

        /// <summary>The hull folder that holds every turret, not one gun.</summary>
        static bool IsTurretsFolder(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (string.Equals(name, "Turrets", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return name.EndsWith("_Turrets", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One muzzle per cannon. Named gun / cannon / bullet objects are containers:
        /// the walk keeps going, and the shell is placed on a single mesh inside that cannon.
        /// Four mesh parts on one cannon still produce one shell.
        /// </summary>
        static void EmitCannonMuzzles(Transform turret, Transform hull, List<Vector3> into, List<float> scales)
        {
            s_CannonScratch.Clear();
            CollectLeafCannons(turret, true, s_CannonScratch);
            for (int i = 0; i < s_CannonScratch.Count; i++)
            {
                if (!TryPickFiringMesh(s_CannonScratch[i], hull, out Vector3 point, out float meshScale))
                    continue;
                into.Add(point);
                scales.Add(meshScale);
            }
        }

        /// <summary>
        /// Deepest cannon / bullet / gun object in each branch. A parent named Gun with a
        /// child named Cannon is not itself a cannon — the child is.
        /// </summary>
        static bool CollectLeafCannons(Transform node, bool isRoot, List<Transform> into)
        {
            if (node == null || !node.gameObject.activeInHierarchy)
                return false;
            if (!isRoot && node.name == ShipFamilyPartMatch.OriginalStashName)
                return false;

            bool deeper = false;
            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
            {
                if (CollectLeafCannons(node.GetChild(i), false, into))
                    deeper = true;
            }

            if (!isRoot && !deeper && IsMuzzleKeyword(node.name))
            {
                into.Add(node);
                return true;
            }

            return deeper || (!isRoot && IsMuzzleKeyword(node.name));
        }

        /// <summary>
        /// One mesh under <paramref name="cannon"/>. Empty transforms are ignored.
        /// A mesh ends the walk. Several meshes under one cannon still yield one shell:
        /// the one furthest along the cannon forward.
        /// Scale is the local-scale chain from that mesh up to the ship root.
        /// </summary>
        static bool TryPickFiringMesh(Transform cannon, Transform hull, out Vector3 point, out float meshScale)
        {
            point = default;
            meshScale = 1f;
            if (cannon == null)
                return false;

            Vector3 forward = cannon.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;
            else
                forward.Normalize();

            var best = new MeshPick();
            WalkMeshes(cannon, hull, 0, cannon.position, forward, ref best);
            if (!best.Found)
                return false;

            point = best.Point;
            meshScale = best.Scale;
            return true;
        }

        struct MeshPick
        {
            public bool Found;
            public Vector3 Point;
            public float Scale;
            public int Depth;
            public float Forward;
        }

        static void WalkMeshes(
            Transform node, Transform hull, int depth, Vector3 origin, Vector3 forward, ref MeshPick best)
        {
            if (node == null || !node.gameObject.activeInHierarchy)
                return;
            if (depth > 0 && node.name == ShipFamilyPartMatch.OriginalStashName)
                return;

            if (TryReadMesh(node, hull, forward, out Vector3 point, out float scale))
            {
                float ahead = Vector3.Dot(point - origin, forward);
                // One shell per cannon. Nested meshes still count — the base housing must
                // not hide the barrel — and the leading tip along the cannon wins.
                bool better = !best.Found || ahead > best.Forward;
                if (better)
                {
                    best.Found = true;
                    best.Point = point;
                    best.Scale = scale;
                    best.Depth = depth;
                    best.Forward = ahead;
                }
            }

            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
                WalkMeshes(node.GetChild(i), hull, depth + 1, origin, forward, ref best);
        }

        /// <summary>
        /// True when this object has a real mesh. The point is the mesh corner furthest
        /// along <paramref name="forward"/> — the cannon tip, not the pivot or the bounds center.
        /// </summary>
        static bool TryReadMesh(Transform node, Transform hull, Vector3 forward, out Vector3 point, out float meshScale)
        {
            point = default;
            meshScale = 1f;
            if (node == null)
                return false;

            MeshFilter filter = node.GetComponent<MeshFilter>();
            SkinnedMeshRenderer skinned = node.GetComponent<SkinnedMeshRenderer>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null && skinned != null)
                mesh = skinned.sharedMesh;
            if (mesh == null)
                return false;

            if (!TryMeshCornerAlong(node, mesh, forward, out point))
                point = node.position;

            meshScale = LocalScaleUntil(node, hull);
            return true;
        }

        /// <summary>World-space corner of <paramref name="mesh"/> furthest along <paramref name="forward"/>.</summary>
        static bool TryMeshCornerAlong(Transform node, Mesh mesh, Vector3 forward, out Vector3 tip)
        {
            tip = node.position;
            if (node == null || mesh == null)
                return false;

            Bounds local = mesh.bounds;
            Vector3 center = local.center;
            Vector3 extents = local.extents;
            float best = float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center + new Vector3(
                    (i & 1) == 0 ? -extents.x : extents.x,
                    (i & 2) == 0 ? -extents.y : extents.y,
                    (i & 4) == 0 ? -extents.z : extents.z);
                Vector3 world = node.TransformPoint(corner);
                float d = Vector3.Dot(world, forward);
                if (d > best)
                {
                    best = d;
                    tip = world;
                }
            }

            return true;
        }

        /// <summary>
        /// Product of local scales from <paramref name="node"/> up to, but not including, the ship root.
        /// Regular muzzle placement already bakes the root presentation scale into the world position
        /// (the bounds center used here). Multiplying the shell by that root scale again would shrink it.
        /// </summary>
        static float LocalScaleUntil(Transform node, Transform stopExclusive)
        {
            float mul = 1f;
            Transform walk = node;
            while (walk != null && walk != stopExclusive)
            {
                Vector3 s = walk.localScale;
                mul *= (math.abs(s.x) + math.abs(s.y) + math.abs(s.z)) / 3f;
                walk = walk.parent;
            }

            return math.max(0.01f, mul);
        }

        /// <summary>Cannon, bullet, or gun. No other words count as a muzzle.</summary>
        static bool IsMuzzleKeyword(string name)
        {
            return NameContains(name, "cannon")
                || NameContains(name, "bullet")
                || NameContains(name, "gun");
        }

        static bool NameContains(string name, string keyword)
        {
            return !string.IsNullOrEmpty(name)
                && name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when this NetworkId is a live Titan hull. No extra network field.</summary>
        static bool IsTitanShooter(int ownerNetworkId)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            EntityManager em = world.EntityManager;
            if (!BulletMuzzlePresentation.TryResolveShooterHull(
                    em, ownerNetworkId, 0, out Entity hull, out _))
                return false;

            return em.HasComponent<MegaShipState>(hull)
                && em.GetComponentData<MegaShipState>(hull).IsMega;
        }

        /// <summary>
        /// Extra pooled shells for a split shot. They stay inactive until the caller places them.
        /// </summary>
        GameObject[] RentSplitExtras(
            GameObject projectilePrefab,
            int extraCount,
            int bankIndex,
            TeamId team,
            float drawScale,
            float visualScale,
            float bulletSpeed,
            float damage)
        {
            if (extraCount <= 0)
                return null;

            var extras = new GameObject[extraCount];
            for (int i = 0; i < extraCount; i++)
            {
                if (!BulletTracerPool.TryRent(projectilePrefab, out GameObject shell, out _) || shell == null)
                {
                    shell = new GameObject("BulletTracer");
                    BulletVisualFactory.BuildVisual(
                        shell.transform, _bank, bankIndex, team, BulletShape.Sphere,
                        drawScale, bulletSpeed, damage, noTrail: false);
                }
                else if (projectilePrefab != null)
                {
                    VfxUrpCompat.EnsureAuthoredProjectileVisual(shell, projectilePrefab);
                }

                VfxUrpCompat.ApplyImpactVisualScale(shell, visualScale, lockUniformParticleSize: true);
                SetShellAudioEnabled(shell, false);
                extras[i] = shell;
            }

            return extras;
        }

        /// <summary>
        /// Puts each shell on the cannon mesh it left. A shot with no cannon offsets
        /// stays as one shell on the logical muzzle.
        /// </summary>
        static void PlaceTracerShells(in Tracer t, Vector3 center, Quaternion rotation)
        {
            if (t.SplitLocalOffsets != null && t.SplitLocalOffsets.Length > 0)
            {
                PlaceTurretMuzzleShells(in t, center, rotation);
                return;
            }

            if (t.Go != null)
                t.Go.transform.SetPositionAndRotation(center, rotation);
        }

        /// <summary>
        /// Keeps each shell on the barrel it left. Offsets were captured from those
        /// nested objects at fire time and ride the shot's heading.
        /// </summary>
        static void PlaceTurretMuzzleShells(in Tracer t, Vector3 center, Quaternion rotation)
        {
            Vector3[] offsets = t.SplitLocalOffsets;
            if (t.Go != null && offsets.Length > 0)
                t.Go.transform.SetPositionAndRotation(center + rotation * offsets[0], rotation);

            if (t.SplitExtras == null)
                return;

            for (int i = 0; i < t.SplitExtras.Length; i++)
            {
                GameObject extra = t.SplitExtras[i];
                int slot = i + 1;
                if (extra == null || slot >= offsets.Length)
                    continue;
                extra.transform.SetPositionAndRotation(center + rotation * offsets[slot], rotation);
            }
        }

        /// <summary>
        /// Split siblings must not each play the prefab shot. The primary shell and
        /// <see cref="AudioManager"/> keep the one fire sound.
        /// </summary>
        static void SetShellAudioEnabled(GameObject root, bool enabled)
        {
            if (root == null)
                return;

            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] == null)
                    continue;
                if (!enabled)
                    sources[i].Stop();
                sources[i].enabled = enabled;
            }
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
