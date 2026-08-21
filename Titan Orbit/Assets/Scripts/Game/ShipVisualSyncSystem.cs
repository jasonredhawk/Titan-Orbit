using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Publishes NetCode presentation-phase ship poses once per frame for camera, hybrid leftovers,
    /// and parallax. Remotes use NetCode interpolation as-is.
    /// <para>
    /// [TITAN-ORBIT] Local ship wraps in sim; this system coasts with
    /// <see cref="ToroidalMapEcs.LerpWrapped"/> so display never lerps across the map.
    /// World bodies reposition individually via <see cref="ToroidalDisplay"/> relative to this ship.
    /// Soft-track on NetCode storms; H73 cruise for reconcile pops. Death→alive hard-snaps to
    /// the home orbit ring so the hull does not crawl across the map. While the hull is grinding an
    /// asteroid, display raw-follows sim (0-speed bounce nibbles used to coast and step the map).
    /// After contact ends, post-kill thrust ramps ~0.4→8 u/s; raw-following hitch-sized 0.22–0.30u
    /// sim steps (still under the 0.45 grind floor) looked stepped. Those frames coast + capped
    /// pull instead. GhostPredictionSmoothing is left
    /// off so this system alone owns local presentation (avoids double-smooth jitter).
    /// </para>
    /// <para>
    /// Evidence: H71 absorbed pops (maxDelta max 0.25). H72 deadzone rejected — correctFrames
    /// stayed ~58 because capped pull could not close a steady &gt;0.08u lag, so the spring ran
    /// every frame. H73 snaps when close (no shimmer) and only coasts on real pops.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Asteroid destroy / Join Team eye-blink (2026-07-20 Editor.log): any miss from
    /// <see cref="EcsGameBridge"/> (GhostSpawnBacklog ToEntityArray gate OR team suppress) used to
    /// call <see cref="ToroidalDisplay.ResetSession"/> every frame and wipe ~283 tiles — whole-map
    /// blink. Fix: reuse cached ship when it still exists; never ResetSession during backlog /
    /// team suppress; debounce confirmed despawn (~45 frames) and clear tiles/pose only once.
    /// Join Settling still skips everything. Backlog still skips remote <c>WithEntityAccess</c>.
    /// </para>
    /// People-transport float poses are published afterward by
    /// <see cref="PeopleTransportVisualSyncSystem"/> (not from raw ghost LT).
    /// World: ClientSimulation. Group: PresentationSystemGroup (OrderLast).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class ShipVisualSyncSystem : SystemBase
    {
        /// <summary>Rotation slerp rate during soft-track (1/seconds).</summary>
        const float DisplayRotationSharpness = 12f;

        /// <summary>
        /// Hard-snap when the display-to-sim gap exceeds this (abandon / missed death edge).
        /// Death→alive always snaps, even under this threshold — otherwise a 20–50u crawl
        /// to the home ring looks like the hull flying home instead of respawning.
        /// </summary>
        const float DisplayRespawnSnapDistance = 50f;

        /// <summary>
        /// When |ServerCommandAge| exceeds this, soft-track (join / catch-up storms).
        /// </summary>
        const float CommandAgeHoldThreshold = 8f;

        /// <summary>Max visual catch-up speed during command-age storms (world units/sec).</summary>
        const float CatchUpDisplayMaxSpeed = 22f;

        /// <summary>Cap hitch dt so a stall does not overshoot soft-track.</summary>
        const float MaxSmoothDeltaTime = 0.05f;

        /// <summary>
        /// [TITAN-ORBIT] H71: exponential blend rate toward sim error after coast (1/seconds).
        /// </summary>
        const float CruiseCorrectSharpness = 8f;

        /// <summary>
        /// [TITAN-ORBIT] H71: max correction on top of coast, as a fraction of one vel×dt step.
        /// Keeps display step ≈ steady cruise even while closing reconcile error.
        /// </summary>
        const float CruiseCorrectPullCapRatio = 0.25f;

        /// <summary>
        /// [TITAN-ORBIT] H73: if display-to-sim gap ≤ this × (speed·dt), snap to sim (raw follow).
        /// Larger gaps are treated as reconcile pops and use coast + capped correct.
        /// </summary>
        const float CruiseRawFollowSlopRatio = 1.15f;

        /// <summary>
        /// [TITAN-ORBIT] Floor for raw-follow even when speed is ~0 (grind / ram stuck on a rock).
        /// H73 used max(speed·dt, 0.02) — bounce/reconcile of a few centimeters then looked like a
        /// pop, so coast ran every frame and the toroidal map stepped with the camera.
        /// </summary>
        const float CruiseMinRawFollowDistance = 0.45f;

        /// <summary>
        /// InContact is cleared every physics tick then set from events, so it can drop for a
        /// frame while still grinding. Keep raw-follow this many presentation frames so flicker
        /// does not toggle H73 coast mid-grind.
        /// </summary>
        const int PostGrindFlickerRawFrames = 3;

        /// <summary>
        /// After grind contact ends, force H73 coast (tiny nibble floor) for this many frames.
        /// Post-kill thrust ramps ~0.4→8 u/s; hitch-sized 0.22–0.30u sim steps sit under
        /// <see cref="CruiseMinRawFollowDistance"/> (0.45), so H73 would snap them and the ship
        /// looks stepped. Coasting those frames hides the hitch; the 0.45 floor stays for live grind.
        /// </summary>
        const int PostGrindCoastFrames = 24;

        /// <summary>
        /// Nibble floor used during <see cref="PostGrindCoastFrames"/> (original H73 0-speed floor).
        /// Large enough to snap true sub-tick noise; small enough that 0.22u hitch steps coast.
        /// </summary>
        const float PostGrindCoastNibble = 0.02f;

        /// <summary>
        /// [TITAN-ORBIT] Frames the local ship must stay unresolved before we treat it as a real
        /// despawn. Editor.log 2026-07-20: calling ResetSession every Null frame wiped ~283 tiles
        /// and blinked the whole map (Join Team suppress / brief lookup misses).
        /// </summary>
        const int MissingShipClearDelayFrames = 45;

        // --- Local-owner display state (not sim) ---
        float3 _smoothPos;
        quaternion _smoothRot = quaternion.identity;
        bool _smoothInitialized;
        Entity _smoothShipEntity;

        /// <summary>Consecutive frames where local ship could not be resolved (despawn debounce).</summary>
        int _missingShipFrames;

        /// <summary>True after we already ran one ResetSession/ClearLocalPose for this missing streak.</summary>
        bool _missingShipCleared;

        /// <summary>Raw-follow frames left to cover InContact flicker (not post-kill accel).</summary>
        int _postGrindRawFrames;

        /// <summary>Frames left to force H73 coast after grind so hitch-sized sim steps do not snap.</summary>
        int _postGrindCoastFrames;

        /// <summary>
        /// Previous presentation frame's local <see cref="ShipState.IsDead"/>. Detects the
        /// death→alive edge so we hard-snap to the home orbit ring instead of soft-tracking
        /// across the map (catch-up storms used to skip the 50u snap entirely).
        /// </summary>
        bool _localShipWasDead;

        /// <summary>
        /// Each presentation frame: publish remotes raw (when safe), then soft-track or raw-follow
        /// local owner. Local pose keeps updating during gem Instantiates backlog so the camera
        /// does not freeze then jump.
        /// </summary>
        protected override void OnUpdate()
        {
            // --- Frame stamp for LateUpdate readers ---
            GhostPresentationTransformCache.BeginPublish(UnityEngine.Time.frameCount);

            // --- Join / TeamChoice Instantiates: skip ship archetype gathers ---
            // [TITAN-ORBIT] Settling-only is NOT enough after Join Team (Settling stays OFF).
            // ShouldSkipShipEntityQueries = Settling OR GhostSpawnBacklog OR post–TeamChoice hold
            // OR deferred Confirm. Remotes / BankPivot WithEntityAccess Crash!!! in that window
            // (Player.log 2026-07-19 / 2026-07-28). Local pose still updates from a known entity.
            bool skipShipGathers = ClientJoinSettleCache.ShouldSkipShipEntityQueries;
            if (ClientJoinSettleCache.Settling)
                return;

            // --- Resolve local ship (safe during gem Instantiates / brief lookup misses) ---
            // [TITAN-ORBIT] EcsGameBridge.TryGetLocalShipEntity returns false while
            // ShouldSkipShipEntityQueries OR while team suppress is on. Neither is a
            // despawn. ResetSession every miss wiped ~283 tiles → whole-map blink.
            // Keep using the cached ship entity whenever it still exists (not only during backlog).
            bool teamSuppress = ClientTeamFlowState.ShouldSuppressLocalPlayerControl();
            Entity localShip = Entity.Null;
            bool resolved = !skipShipGathers &&
                            EcsGameBridge.TryGetLocalShipEntityOnWorld(World, out localShip);
            if (!resolved &&
                !teamSuppress &&
                _smoothShipEntity != Entity.Null &&
                EntityManager.Exists(_smoothShipEntity) &&
                EntityManager.HasComponent<ShipTag>(_smoothShipEntity))
            {
                localShip = _smoothShipEntity;
                resolved = true;
            }

            // --- Instantiates-hook seed (post–Join Team ship while gathers are gated) ---
            // [TITAN-ORBIT] Without a seed, hold leaves hasPose=false then camera snaps on first pose.
            if (!resolved &&
                !teamSuppress &&
                LocalShipEntitySeed.TryGetSeededShip(EntityManager, out var seededShip))
            {
                localShip = seededShip;
                resolved = true;
            }

            // --- Remotes: never WithEntityAccess while ShouldSkipShipEntityQueries ---
            // [TITAN-ORBIT] Player.log 2026-07-19 / 2026-07-28: hand-rolled GhostSpawnBacklog alone
            // missed deferred Confirm / TeamChoice hold folds. Use the helper API.
            if (!skipShipGathers)
            {
                foreach (var (lt, entity) in SystemAPI
                             .Query<RefRO<LocalTransform>>()
                             .WithAll<ShipTag>()
                             .WithEntityAccess())
                {
                    if (entity == localShip)
                        continue;

                    PublishShip(entity, lt.ValueRO);
                }
            }
            else if (TitanOrbitDebugFlags.LogAsteroidDestroyPerf)
            {
                // [TITAN-ORBIT] Rate-limit — backlog can last many frames during map Instantiates;
                // logging every frame floods the Console and looks like a hard error storm.
                if (UnityEngine.Time.frameCount % 60 == 0)
                {
                    Debug.Log(
                        $"[AsteroidDestroy] ShipVisualSync skipGathers: localShipCached={resolved} " +
                        $"entityIndex={localShip.Index} frameDtMs={UnityEngine.Time.deltaTime * 1000f:F1}");
                }
            }

            // --- People transports ---
            // [TITAN-ORBIT] Owned by PeopleTransportVisualSyncSystem (UpdateAfter this).

            // --- Local owner: GetComponentData on known entity — no ship gather ---
            PublishLocalShipDisplayPose(localShip, skipBankPivotQuery: skipShipGathers);
        }

        /// <summary>
        /// Builds the local owner's camera/mesh pose: soft-track on storms, H73 raw-or-coast cruise.
        /// Hard-snaps on death, respawn, ship replace, and gaps beyond
        /// <see cref="DisplayRespawnSnapDistance"/>.
        /// </summary>
        /// <param name="localShip">Local player ship entity, or Null when not spawned.</param>
        /// <param name="skipBankPivotQuery">
        /// When true (<see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>), skip the
        /// BankPivot <c>WithEntityAccess</c> walk — still write ship LocalToWorld + ShipDisplayPose
        /// so the camera keeps tracking.
        /// </param>
        void PublishLocalShipDisplayPose(Entity localShip, bool skipBankPivotQuery = false)
        {
            if (localShip == Entity.Null || !EntityManager.Exists(localShip))
            {
                HandleMissingLocalShip();
                return;
            }

            // --- Ship is back — clear missing-ship debounce ---
            _missingShipFrames = 0;
            _missingShipCleared = false;

            if (!EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            var lt = EntityManager.GetComponentData<LocalTransform>(localShip);
            // --- Wrapped sim pose — camera follows this; other bodies retile around it ---
            float3 targetPos = lt.Position;
            quaternion targetRot = lt.Rotation;
            bool crossedSeam = false;
            if (_smoothInitialized &&
                ToroidalMapEcs.TryGetMapSize(out float wrapW, out float wrapH) &&
                ToroidalMapEcs.CrossedSeam(_smoothPos, targetPos, wrapW, wrapH))
            {
                // Snap display onto the new cell along the short path, then force world tiles.
                _smoothPos = targetPos;
                crossedSeam = true;
                ToroidalDisplay.NotifyReferenceWrapped();
            }

            // --- Read command age for catch-up hold ---
            float commandAge = 0f;
            if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
                commandAge = ack.ServerCommandAge / 256f;

            bool catchingUp = math.abs(commandAge) > CommandAgeHoldThreshold;
            float dt = math.min(math.max(0f, UnityEngine.Time.unscaledDeltaTime), MaxSmoothDeltaTime);

            float3 simVel = float3.zero;
            if (EntityManager.HasComponent<ShipKinematics>(localShip))
                simVel = EntityManager.GetComponentData<ShipKinematics>(localShip).Velocity;

            bool shipChanged = _smoothInitialized && _smoothShipEntity != Entity.Null && localShip != _smoothShipEntity;
            _smoothShipEntity = localShip;
            if (shipChanged)
            {
                _postGrindRawFrames = 0;
                _postGrindCoastFrames = 0;
            }

            // --- Asteroid grind / ram: raw-follow sim ---
            // [TITAN-ORBIT] ShipAsteroidContactState is written this physics step (AfterPhysics).
            // Presentation runs after that. InContact means bounce is shoving the hull every tick;
            // coasting those micro-corrections steps the camera and every toroidal world body.
            bool asteroidContact = EntityManager.HasComponent<ShipAsteroidContactState>(localShip) &&
                                   EntityManager.GetComponentData<ShipAsteroidContactState>(localShip).InContact != 0;

            // --- Grind flicker vs post-kill coast ---
            // [TITAN-ORBIT] InContact flickers (cleared every physics tick). A few raw-follow
            // frames cover that. After a kill, hitch-sized 0.22–0.30u/frame steps at cruise
            // speed sit under the 0.45 grind floor — snapping them looks stepped. Force H73
            // coast for a short window so those render steps blend.
            if (asteroidContact)
            {
                _postGrindRawFrames = PostGrindFlickerRawFrames;
                _postGrindCoastFrames = PostGrindCoastFrames;
            }
            else
            {
                if (_postGrindRawFrames > 0)
                    _postGrindRawFrames--;
                if (_postGrindCoastFrames > 0)
                    _postGrindCoastFrames--;
            }
            bool grindRawFollow = asteroidContact || _postGrindRawFrames > 0;
            bool postGrindCoast = !grindRawFollow && _postGrindCoastFrames > 0;

            // --- Death / respawn edge ---
            // [TITAN-ORBIT] Server teleports LocalTransform to the home orbit ring. Display
            // _smoothPos stays at the wreck for the 10s countdown. Soft-track then crawled
            // the visible hull (and camera) across the map at 22 u/s — worse when catchingUp
            // skipped the snap-distance check. Snap on alive-again; stay glued while dead.
            bool isDead = EntityManager.HasComponent<ShipState>(localShip) &&
                          EntityManager.GetComponentData<ShipState>(localShip).IsDead;
            bool justRespawned = _localShipWasDead && !isDead;
            _localShipWasDead = isDead;

            float displayErr = 0f;
            if (_smoothInitialized && ToroidalMapEcs.TryGetMapSize(out float errW, out float errH))
                displayErr = ToroidalMapEcs.ToroidalDistance(_smoothPos, targetPos, errW, errH);
            else if (_smoothInitialized)
                displayErr = math.distance(_smoothPos, targetPos);
            bool hardSnap = TitanOrbitDebugFlags.IsolateDisableShipSoftTrack
                            || grindRawFollow
                            || !_smoothInitialized
                            || shipChanged
                            || isDead
                            || justRespawned
                            || crossedSeam
                            || displayErr > DisplayRespawnSnapDistance;

            // --- Step display state ---
            // [TITAN-ORBIT] Isolation F4: raw sim pose only — if destroy stutter vanishes, soft-track
            // was amplifying physics reconcile pops from phantom asteroid hulls.
            if (hardSnap)
            {
                if (shipChanged)
                    ToroidalDisplay.ResetSession("ShipVisualSync.shipChanged");
                else if (justRespawned && displayErr > DisplayRespawnSnapDistance)
                    ToroidalDisplay.ResetSession("ShipVisualSync.respawn");
                _smoothPos = targetPos;
                _smoothRot = targetRot;
                _smoothInitialized = true;
            }
            else if (catchingUp || displayErr > 2f)
            {
                StepDisplayToward(targetPos, targetRot, dt, CatchUpDisplayMaxSpeed);
            }
            else
            {
                StepCruiseRawOrCoast(targetPos, targetRot, simVel, dt, postGrindCoast);
            }

            // --- Publish pose for camera / hybrid / EG LocalToWorld ---
            var displayLt = lt;
            displayLt.Position = _smoothPos;
            displayLt.Rotation = _smoothRot;
            PublishShip(localShip, displayLt);
            ShipDisplayPose.SetLocalPose((Vector3)_smoothPos, (Quaternion)_smoothRot);

            // [ECS/DOTS] Entities Graphics reads LocalToWorld — override after transform systems this frame.
            if (!EntityManager.HasComponent<LocalToWorld>(localShip))
                return;

            var shipLtw = new LocalToWorld { Value = displayLt.ToMatrix() };
            EntityManager.SetComponentData(localShip, shipLtw);

            // --- Bank pivots: full query is unsafe during ShouldSkipShipEntityQueries Instantiates ---
            // Camera already has ShipDisplayPose; pivot LTW can wait a frame.
            if (skipBankPivotQuery)
                return;

            foreach (var (pivotTag, pivotLt, pivotEntity) in SystemAPI
                         .Query<RefRO<ShipVisualBankPivotTag>, RefRO<LocalTransform>>()
                         .WithEntityAccess())
            {
                if (pivotTag.ValueRO.ShipEntity != localShip)
                    continue;
                if (!EntityManager.HasComponent<LocalToWorld>(pivotEntity))
                    continue;

                EntityManager.SetComponentData(pivotEntity, new LocalToWorld
                {
                    Value = math.mul(shipLtw.Value, pivotLt.ValueRO.ToMatrix()),
                });
            }
        }

        /// <summary>
        /// H73 cruise: snap to sim when within one tick or a grind-sized nibble; otherwise coast
        /// + capped soft correct (H71). The nibble floor stops ~0-speed ram contact from looking
        /// like a teleport pop (which used to step the whole toroidal map).
        /// </summary>
        /// <param name="simPos">Predicted / reconciled pose this frame.</param>
        /// <param name="simRot">Sim rotation.</param>
        /// <param name="simVel">Kinematics velocity (world units/sec).</param>
        /// <param name="dt">Clamped unscaled frame delta.</param>
        /// <param name="postGrindCoast">
        /// True for a short window after asteroid contact: use the tiny nibble floor so hitch-sized
        /// 0.22–0.30u sim steps coast instead of snapping (the 0.45 grind floor would snap them).
        /// </param>
        void StepCruiseRawOrCoast(float3 simPos, quaternion simRot, float3 simVel, float dt, bool postGrindCoast = false)
        {
            float expected = math.max(math.length(simVel) * math.max(0f, dt), 0.02f);
            float dist = math.distance(_smoothPos, simPos);
            if (ToroidalMapEcs.TryGetMapSize(out float cruiseW, out float cruiseH))
                dist = ToroidalMapEcs.ToroidalDistance(_smoothPos, simPos, cruiseW, cruiseH);
            float minNibble = postGrindCoast ? PostGrindCoastNibble : CruiseMinRawFollowDistance;
            float rawFollowSlop = math.max(expected * CruiseRawFollowSlopRatio, minNibble);

            // --- Healthy frame: within one tick (+slop) or a grind-sized nibble — raw follow ---
            // H72 failed because capped pull left a permanent >deadzone lag and corrected forever.
            // Grind at ~0 speed used the 0.02 floor, so 5–20 cm bounce corrections coasted every frame.
            if (dist <= rawFollowSlop)
            {
                _smoothPos = simPos;
                _smoothRot = simRot;
                return;
            }

            // --- Reconcile pop / large gap: coast then capped soft correct (wrap-aware) ---
            float3 coasted = _smoothPos + simVel * dt;
            float3 err = simPos - coasted;
            err.y = 0f;
            if (ToroidalMapEcs.TryGetMapSize(out float pullW, out float pullH))
            {
                coasted = ToroidalMapEcs.Wrap(coasted, pullW, pullH);
                err = ToroidalMapEcs.ShortestOffsetXZ(coasted, simPos, pullW, pullH);
            }
            float t = 1f - math.exp(-CruiseCorrectSharpness * math.max(0f, dt));
            float3 pull = err * t;
            float pullLen = math.length(pull);
            float pullCap = expected * CruiseCorrectPullCapRatio;
            if (pullLen > pullCap && pullLen > 1e-6f)
                pull *= pullCap / pullLen;

            _smoothPos = coasted + pull;
            _smoothPos.y = simPos.y;
            if (ToroidalMapEcs.TryGetMapSize(out float snapW, out float snapH))
                _smoothPos = ToroidalMapEcs.Wrap(_smoothPos, snapW, snapH);
            _smoothRot = math.slerp(_smoothRot, simRot, 1f - math.exp(-DisplayRotationSharpness * dt));
        }

        /// <summary>
        /// Local ship entity is Null / destroyed this frame. Keep pose + tile memory across
        /// GhostSpawnBacklog, team-suppress, and brief lookup misses. Only after a sustained gap
        /// do we clear once (not every frame — that wiped hundreds of tiles and blinked the map).
        /// </summary>
        void HandleMissingLocalShip()
        {
            // --- Gated lookup / Join Team UI — never thrash tiles ---
            // [TITAN-ORBIT] Backlog: gem Instantiates after asteroid destroy. Suppress: map visible
            // before TeamChoiceConfirmed. Both used to call ResetSession every frame (Editor.log).
            // Prefer ShouldSkipShipEntityQueries (includes post–TeamChoice hold + deferred Confirm)
            // over GhostSpawnBacklog alone — a stale backlog=false during the hold used to start the
            // despawn debounce and Clear() the Instantiates-hook seed before Confirm flushed.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries ||
                ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                _missingShipFrames = 0;
                return;
            }

            _missingShipFrames++;

            // --- Still holding last pose for camera / toroidal reference ---
            if (_missingShipFrames < MissingShipClearDelayFrames)
                return;

            // --- Confirmed leave / despawn: clear once per streak ---
            if (_missingShipCleared)
                return;

            _missingShipCleared = true;
            ResetLocalDisplaySmoothing();
            LocalShipEntitySeed.Clear();
            ToroidalDisplay.ResetSession("ShipVisualSync.PublishLocalShip_missingShip_confirmed");
            ShipDisplayPose.ClearLocalPose("ShipVisualSync.PublishLocalShip_missingShip_confirmed");
        }

        /// <summary>
        /// Clears local soft-track state so the next ship entity hard-snaps instead of panning
        /// from a previous disconnect / abandon pose.
        /// </summary>
        void ResetLocalDisplaySmoothing()
        {
            _smoothInitialized = false;
            _smoothShipEntity = Entity.Null;
            _smoothPos = float3.zero;
            _smoothRot = quaternion.identity;
            _localShipWasDead = false;
        }

        /// <summary>
        /// Moves the display pose toward a target with a hard speed cap (catch-up / soft reacquire).
        /// </summary>
        void StepDisplayToward(float3 targetPos, quaternion targetRot, float dt, float maxSpeed)
        {
            float3 delta = targetPos - _smoothPos;
            if (ToroidalMapEcs.TryGetMapSize(out float towardW, out float towardH))
                delta = ToroidalMapEcs.ShortestOffsetXZ(_smoothPos, targetPos, towardW, towardH);
            float dist = math.length(delta);
            float maxStep = math.max(0f, maxSpeed) * math.max(0f, dt);
            if (dist <= maxStep || dist < 1e-5f)
                _smoothPos = targetPos;
            else
                _smoothPos += delta * (maxStep / dist);
            if (ToroidalMapEcs.TryGetMapSize(out float wrapTowardW, out float wrapTowardH))
                _smoothPos = ToroidalMapEcs.Wrap(_smoothPos, wrapTowardW, wrapTowardH);

            float rotT = dist > 1e-5f ? math.saturate(maxStep / dist) : 1f;
            _smoothRot = math.slerp(_smoothRot, targetRot, math.max(rotT, 1f - math.exp(-DisplayRotationSharpness * dt)));
        }

        /// <summary>Writes one ship snapshot into the presentation cache.</summary>
        static void PublishShip(Entity entity, in LocalTransform transform) =>
            GhostPresentationTransformCache.PublishShip(entity, ToSnapshot(transform));

        /// <summary>Maps <see cref="LocalTransform"/> into the hybrid presentation cache format.</summary>
        static GhostPresentationTransformCache.Snapshot ToSnapshot(in LocalTransform transform) =>
            new GhostPresentationTransformCache.Snapshot
            {
                Position = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale,
            };
    }
}
