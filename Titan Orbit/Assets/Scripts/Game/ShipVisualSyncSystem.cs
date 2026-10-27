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
    /// and parallax. Remote ships are OwnerPredicted ghosts in interpolated mode: NetCode already
    /// keeps the snapshot buffer and blends LocalTransform on the interpolation tick (two ticks,
    /// about 33 ms at 60 Hz). This system publishes that pose. It does not keep a second buffer.
    /// <para>
    /// [TITAN-ORBIT] Local ship wraps onto the canonical chart; camera follows that pose.
    /// A ±map-size jump hard-snaps display (no long lerp / H73 coast across the rectangle).
    /// Soft-track on NetCode storms; H73 cruise for reconcile pops. Death→alive hard-snaps to
    /// the home orbit ring so the hull does not crawl across the map. While dead, display stays
    /// on the wreck — a one-frame LocalTransform hop to the home-ring spawn (asteroid death)
    /// must not drag the camera there and back. Asteroid grind and hull contacts do not spring
    /// the hull pose (that coast chased every PhysX bounce and looked jittery). The hull stays
    /// on H73 cruise; <see cref="CameraFollowEcs"/> trails the published pose while
    /// <see cref="ShipDisplayPose.LocalHullContact"/> is set. A held predicted tick glides
    /// at kinematics speed for at most one simulation step so formation flight does not
    /// shuffle against interpolated remote and AI ships. Ship-vs-ship separation is the
    /// simulated pose from the shared tick, not a display offset. Wrap / respawn / 50u
    /// abandon still hard-snap, and that glide is not applied on wrap, death, catch-up,
    /// or overlap. GhostPredictionSmoothing is left off so this system alone
    /// owns local presentation (avoids double-smooth jitter).
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
        /// While <see cref="ShipState.IsDead"/>, ignore sim poses farther than this from the
        /// wreck. Asteroid death can present one frame at the home-ring spawn, then the
        /// ghost returns to the field — following that frame cuts the camera there and back.
        /// </summary>
        const float DeathWreckHoldDistance = 40f;

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
        /// Contact shoves under this floor stay on the hull. Camera lag lives in
        /// <see cref="CameraFollowEcs"/>, not a second spring here.
        /// </summary>
        const float CruiseMinRawFollowDistance = 0.45f;

        /// <summary>
        /// Sim pose closer than this to the last cruise sample is a held tick (same predicted
        /// pose across extra render frames). Partial-tick motion is centimeters, so it still
        /// counts as a new sample and does not add a second lead.
        /// </summary>
        const float CruiseHeldTickPositionEpsilon = 0.001f;

        /// <summary>
        /// Below this planar speed a held tick stays on the sim pose. Stops and grind noise
        /// must not walk the camera; cruise speeds still glide between snapshots.
        /// </summary>
        const float CruiseHeldTickMinPlanarSpeed = 1f;

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

        /// <summary>
        /// Previous presentation frame's local <see cref="ShipState.IsDead"/>. Detects the
        /// death→alive edge so we hard-snap to the home orbit ring instead of soft-tracking
        /// across the map (catch-up storms used to skip the 50u snap entirely).
        /// </summary>
        bool _localShipWasDead;

        /// <summary>Last sim position consumed by cruise. Held ticks glide ahead of this sample.</summary>
        float3 _cruiseSimPos;

        /// <summary>Unscaled time when <see cref="_cruiseSimPos"/> was sampled.</summary>
        float _cruiseSimSampleTime;

        /// <summary>False until cruise has sampled a pose, and after snap / overlap / catch-up.</summary>
        bool _cruiseSimSampleValid;

        /// <summary>
        /// Each presentation frame: publish the interpolated remote pose, then soft-track
        /// or raw-follow the local owner. Local pose keeps updating during gem Instantiates
        /// backlog so the camera does not freeze then jump.
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
                EntityManager.HasComponent<ShipTag>(_smoothShipEntity) &&
                LocalShipEntitySeed.EntityMatchesLocalOwner(EntityManager, _smoothShipEntity))
            {
                localShip = _smoothShipEntity;
                resolved = true;
            }

            // --- Instantiates-hook seed (post–Join Team ship while gathers are gated) ---
            // [TITAN-ORBIT] Without a seed, hold leaves hasPose=false then camera snaps on first pose.
            if (!resolved &&
                !teamSuppress &&
                LocalShipEntitySeed.TryGetSeededShip(EntityManager, out var seededShip) &&
                LocalShipEntitySeed.EntityMatchesLocalOwner(EntityManager, seededShip))
            {
                localShip = seededShip;
                resolved = true;
            }

            if (resolved &&
                !LocalShipEntitySeed.EntityMatchesLocalOwner(EntityManager, localShip))
            {
                localShip = Entity.Null;
                resolved = false;
                _smoothShipEntity = Entity.Null;
            }

            // --- Remotes: never WithEntityAccess while ShouldSkipShipEntityQueries ---
            // [TITAN-ORBIT] Player.log 2026-07-19 / 2026-07-28: hand-rolled GhostSpawnBacklog alone
            // missed deferred Confirm / TeamChoice hold folds. Use the helper API.
            if (!skipShipGathers)
            {
                // Remote pose is already the interpolated snapshot. Publishing it again through
                // a wall-clock buffer collapsed bursts (several ticks, one arrival time) back
                // to the newest pose, which is the chop the buffer was meant to hide.
                foreach (var (lt, entity) in SystemAPI
                             .Query<RefRO<LocalTransform>>()
                             .WithAll<ShipTag>()
                             .WithEntityAccess())
                {
                    if (entity == localShip)
                        continue;
                    if (SystemAPI.HasComponent<GhostOwnerIsLocal>(entity) &&
                        SystemAPI.IsComponentEnabled<GhostOwnerIsLocal>(entity))
                        continue;

                    PublishShip(entity, lt.ValueRO);
                    if (EntityManager.HasComponent<LocalToWorld>(entity))
                    {
                        EntityManager.SetComponentData(entity, new LocalToWorld
                        {
                            Value = lt.ValueRO.ToMatrix(),
                        });
                    }
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
            // --- Canonical wrapped sim pose — camera follows this ---
            float3 targetPos = lt.Position;
            quaternion targetRot = lt.Rotation;

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

            // --- Asteroid / hull contact flag for camera trail (hull pose stays on H73) ---
            // [TITAN-ORBIT] A contact spring on this pose chased every PhysX bounce (velocity
            // coast + capped pull) and the camera, hard-locked to the pose, jittered worse.
            // Publish the contact; CameraFollowEcs lags its own XZ anchor.
            bool liveContact = false;
            if (EntityManager.HasComponent<ShipAsteroidContactState>(localShip))
            {
                var contact = EntityManager.GetComponentData<ShipAsteroidContactState>(localShip);
                liveContact = contact.InContact != 0 || contact.InContactHull != 0;
            }

            ShipDisplayPose.SetLocalHullContact(liveContact);

            bool dampShipShipCorrection = false;
            if (SystemAPI.TryGetSingleton<LocalShipShipOverlapLatch>(out var shipOverlap)
                && shipOverlap.Active != 0)
                dampShipShipCorrection = true;

            // --- Death / respawn edge ---
            // [TITAN-ORBIT] Respawn teleports LocalTransform to the home orbit ring. Display
            // stays on the wreck for the countdown. A one-frame hop to that spawn (asteroid
            // death) used to hard-snap the camera home, then the next frame snapped back to
            // the field. Hold the wreck across that spike. Snap on alive-again.
            bool isDead = EntityManager.HasComponent<ShipState>(localShip) &&
                          EntityManager.GetComponentData<ShipState>(localShip).IsDead;
            bool justRespawned = _localShipWasDead && !isDead;
            _localShipWasDead = isDead;

            float displayErr = _smoothInitialized ? math.distance(_smoothPos, targetPos) : 0f;
            bool holdWreck = isDead
                             && _smoothInitialized
                             && !shipChanged
                             && displayErr > DeathWreckHoldDistance;
            bool wrapJump = _smoothInitialized
                            && !holdWreck
                            && ToroidalMapEcs.IsWrapJump(_smoothPos, targetPos);
            if (wrapJump)
                MapWrapTransition.NotifyWrap();
            bool hardSnap = TitanOrbitDebugFlags.IsolateDisableShipSoftTrack
                            || !_smoothInitialized
                            || shipChanged
                            || (isDead && !holdWreck)
                            || justRespawned
                            || wrapJump
                            || (!isDead && displayErr > DisplayRespawnSnapDistance);

            // --- Step display state ---
            // [TITAN-ORBIT] Isolation F4: raw sim pose only — if destroy stutter vanishes, soft-track
            // was amplifying physics reconcile pops from phantom asteroid hulls.
            // holdWreck leaves _smoothPos on the death pose (published below).
            if (!holdWreck && hardSnap)
            {
                if (shipChanged)
                    ToroidalDisplay.ResetSession("ShipVisualSync.shipChanged");
                else if (justRespawned && displayErr > DisplayRespawnSnapDistance)
                    ToroidalDisplay.ResetSession("ShipVisualSync.respawn");
                InvalidateCruiseSimSample();
                _smoothPos = targetPos;
                _smoothRot = targetRot;
                _smoothInitialized = true;
            }
            else if (!holdWreck && (catchingUp || displayErr > 2f))
            {
                InvalidateCruiseSimSample();
                StepDisplayToward(targetPos, targetRot, dt, CatchUpDisplayMaxSpeed);
            }
            else if (!holdWreck && dampShipShipCorrection)
            {
                InvalidateCruiseSimSample();
                StepShipContactDamped(targetPos, targetRot, dt);
            }
            else if (!holdWreck)
            {
                StepCruiseRawOrCoast(targetPos, targetRot, simVel, dt);
            }
            else
            {
                InvalidateCruiseSimSample();
            }

            // --- Publish pose for camera / hybrid / EG LocalToWorld ---
            // Cruise may sit up to one tick ahead of a held sim pose. Remotes stay on the
            // interpolated snapshot published above.
            float3 published = _smoothPos;
            var displayLt = lt;
            displayLt.Position = published;
            displayLt.Rotation = _smoothRot;
            PublishShip(localShip, displayLt);
            ShipDisplayPose.SetLocalPose((Vector3)published, (Quaternion)_smoothRot);

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
        /// H73 cruise: snap to the render target when within one tick or the cruise floor;
        /// otherwise coast + capped soft correct (H71). A held predicted pose (same sim
        /// position across extra render frames) aims one step ahead at kinematics velocity
        /// so the camera matches interpolated remotes. Grind / hull bounce is not filtered
        /// here — the camera trails that pose on its own anchor.
        /// </summary>
        /// <param name="simPos">Predicted / reconciled pose this frame.</param>
        /// <param name="simRot">Sim rotation.</param>
        /// <param name="simVel">Kinematics velocity (world units/sec).</param>
        /// <param name="dt">Clamped unscaled frame delta.</param>
        void StepCruiseRawOrCoast(float3 simPos, quaternion simRot, float3 simVel, float dt)
        {
            float3 renderTarget = ResolveCruiseRenderTarget(simPos, simVel);
            float expected = math.max(math.length(simVel) * math.max(0f, dt), 0.02f);
            float dist = math.distance(_smoothPos, renderTarget);
            float rawFollowSlop = math.max(expected * CruiseRawFollowSlopRatio, CruiseMinRawFollowDistance);

            // --- Healthy frame: within one tick (+slop) or the cruise floor — raw follow ---
            // H72 failed because capped pull left a permanent >deadzone lag and corrected forever.
            if (dist <= rawFollowSlop)
            {
                _smoothPos = renderTarget;
                _smoothRot = simRot;
                return;
            }

            // --- Reconcile pop: coast then capped soft correct ---
            float3 coasted = _smoothPos + simVel * dt;
            float3 err = renderTarget - coasted;
            err.y = 0f;
            float t = 1f - math.exp(-CruiseCorrectSharpness * math.max(0f, dt));
            float3 pull = err * t;
            float pullLen = math.length(pull);
            float pullCap = expected * CruiseCorrectPullCapRatio;
            if (pullLen > pullCap && pullLen > 1e-6f)
                pull *= pullCap / pullLen;

            _smoothPos = coasted + pull;
            _smoothPos.y = renderTarget.y;
            _smoothRot = math.slerp(_smoothRot, simRot, 1f - math.exp(-DisplayRotationSharpness * dt));
        }

        /// <summary>
        /// Sim pose, or that pose plus at most one tick of kinematics lead while the predicted
        /// position is held. A moving sim sample (Relay partial ticks) resets the clock so
        /// lead stays zero and prediction is not stepped twice.
        /// </summary>
        /// <param name="simPos">Predicted / reconciled pose this frame.</param>
        /// <param name="simVel">Kinematics velocity (world units/sec).</param>
        /// <returns>Position H73 should follow this frame.</returns>
        float3 ResolveCruiseRenderTarget(float3 simPos, float3 simVel)
        {
            float now = UnityEngine.Time.unscaledTime;
            float heldEpsilonSq = CruiseHeldTickPositionEpsilon * CruiseHeldTickPositionEpsilon;
            bool heldTick = _cruiseSimSampleValid &&
                            math.distancesq(_cruiseSimPos, simPos) <= heldEpsilonSq;
            if (!heldTick)
            {
                _cruiseSimPos = simPos;
                _cruiseSimSampleTime = now;
                _cruiseSimSampleValid = true;
                return simPos;
            }

            float planarSpeed = math.length(simVel.xz);
            if (planarSpeed <= CruiseHeldTickMinPlanarSpeed)
                return simPos;

            float tickDt = 1f / TitanOrbitServerTickRateSystem.SimulationHz;
            float lead = math.clamp(now - _cruiseSimSampleTime, 0f, tickDt);
            return simPos + simVel * lead;
        }

        /// <summary>
        /// Drops the held-tick sample so the next cruise frame starts on the sim pose.
        /// Wrap, death, respawn, catch-up, overlap, and the wreck hold must not inherit a lead.
        /// </summary>
        void InvalidateCruiseSimSample()
        {
            _cruiseSimSampleValid = false;
        }

        /// <summary>
        /// While the local hull is against an interpolated enemy, raw-follow turns each
        /// snapshot step into a visible reverse. Ease onto the sim pose instead.
        /// Wrap, death, and respawn still hard-snap before this runs.
        /// </summary>
        void StepShipContactDamped(float3 simPos, quaternion simRot, float dt)
        {
            float3 err = simPos - _smoothPos;
            err.y = 0f;
            float dist = math.length(err);
            float t = 1f - math.exp(-18f * math.max(0f, dt));
            if (dist < 0.02f)
            {
                _smoothPos = simPos;
                _smoothRot = simRot;
                return;
            }

            _smoothPos += err * t;
            _smoothPos.y = simPos.y;
            _smoothRot = math.slerp(_smoothRot, simRot, t);
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
            InvalidateCruiseSimSample();
        }

        /// <summary>
        /// Moves the display pose toward a target with a hard speed cap (catch-up / soft reacquire).
        /// </summary>
        void StepDisplayToward(float3 targetPos, quaternion targetRot, float dt, float maxSpeed)
        {
            float3 delta = targetPos - _smoothPos;
            float dist = math.length(delta);
            float maxStep = math.max(0f, maxSpeed) * math.max(0f, dt);
            if (dist <= maxStep || dist < 1e-5f)
                _smoothPos = targetPos;
            else
                _smoothPos += delta * (maxStep / dist);

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
