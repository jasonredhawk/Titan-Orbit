using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.ECS;
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
    /// [TITAN-ORBIT] Local ship does <b>not</b> wrap — it flies unbound; camera follows that pose.
    /// World bodies reposition individually via <see cref="ToroidalDisplay"/> relative to this ship.
    /// Soft-track on NetCode storms; H73 cruise for reconcile pops. GhostPredictionSmoothing is left
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
        /// Only hard-snap beyond this (respawn / abandon). Smaller gaps soft-reacquire.
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
        /// Each presentation frame: publish remotes raw (when safe), then soft-track or raw-follow
        /// local owner. Local pose keeps updating during gem Instantiates backlog so the camera
        /// does not freeze then jump.
        /// </summary>
        protected override void OnUpdate()
        {
            // --- Frame stamp for LateUpdate readers ---
            GhostPresentationTransformCache.BeginPublish(UnityEngine.Time.frameCount);

            // --- Join Instantiates storm: skip all ship queries (Windows Crash!!! path) ---
            // [TITAN-ORBIT] Settling = initial map Instantiates. Do not touch ship queries here.
            if (ClientJoinSettleCache.Settling)
                return;

            // --- Resolve local ship (safe during gem Instantiates / brief lookup misses) ---
            // [TITAN-ORBIT] EcsGameBridge.TryGetLocalShipEntity returns false while
            // GhostSpawnBacklog (ToEntityArray gate) OR while team suppress is on. Neither is a
            // despawn. Editor.log: ResetSession every miss wiped ~283 tiles → whole-map blink.
            // Keep using the cached ship entity whenever it still exists (not only during backlog).
            bool backlog = ClientJoinSettleCache.GhostSpawnBacklog;
            bool teamSuppress = ClientTeamFlowState.ShouldSuppressLocalPlayerControl();
            Entity localShip = Entity.Null;
            bool resolved = EcsGameBridge.TryGetLocalShipEntityOnWorld(World, out localShip);
            string resolvePath = resolved ? "bridge" : "none";
            if (!resolved &&
                !teamSuppress &&
                _smoothShipEntity != Entity.Null &&
                EntityManager.Exists(_smoothShipEntity) &&
                EntityManager.HasComponent<ShipTag>(_smoothShipEntity))
            {
                localShip = _smoothShipEntity;
                resolved = true;
                resolvePath = "cache";
            }

            // --- Instantiates-hook seed (post–Join Team ship while backlog gates bridge lookup) ---
            // [TITAN-ORBIT] debug-604d3d frame 1782: cacheEmpty + backlog → hasPose false → CAM_JUMP.
            if (!resolved &&
                !teamSuppress &&
                LocalShipEntitySeed.TryGetSeededShip(EntityManager, out var seededShip))
            {
                localShip = seededShip;
                resolved = true;
                resolvePath = "instantiateSeed";
            }

            // #region agent log
            // Log failures too — 604d3d had silent "none" for 35 frames then POSE_GAINED 113m jump.
            if (backlog || resolvePath == "instantiateSeed" || (!resolved && !teamSuppress))
            {
                AsteroidDestroyBlinkProbe.NotifyResolvePath(
                    resolvePath, localShip, ShipDisplayPose.HasLocalPose);
            }
            // #endregion

            // --- Remotes: skip WithEntityAccess while GhostSpawnBacklog ---
            // [TITAN-ORBIT] Player.log 2026-07-19: ship WithEntityAccess during Instantiates → Crash!!!.
            if (!backlog)
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
            else
            {
                // [DIAGNOSTIC] GhostSpawnBacklog after asteroid destroy — gem Instantiates window.
                if (!resolved)
                {
                    AsteroidDestroyBlinkProbe.NotifyLocalShipUnresolved(
                        $"backlog lookup failed cacheEmpty={_smoothShipEntity == Entity.Null}");
                }
                else if (TitanOrbitDebugFlags.LogAsteroidDestroyPerf)
                {
                    Debug.Log(
                        $"[AsteroidDestroy] ShipVisualSync backlog: localShipCached={resolved} " +
                        $"entityIndex={localShip.Index} frameDtMs={UnityEngine.Time.deltaTime * 1000f:F1}");
                }
            }

            // --- People transports ---
            // [TITAN-ORBIT] Owned by PeopleTransportVisualSyncSystem (UpdateAfter this).

            // --- Local owner: GetComponentData on known entity — no ship gather ---
            PublishLocalShipDisplayPose(localShip, skipBankPivotQuery: backlog);
        }

        /// <summary>
        /// Builds the local owner's camera/mesh pose: soft-track on storms, H73 raw-or-coast cruise.
        /// Hard-snaps when the local ship entity is missing or replaced (rejoin / fresh spawn).
        /// </summary>
        /// <param name="localShip">Local player ship entity, or Null when not spawned.</param>
        /// <param name="skipBankPivotQuery">
        /// When true (GhostSpawnBacklog), skip the BankPivot <c>WithEntityAccess</c> walk — still
        /// write ship LocalToWorld + ShipDisplayPose so the camera keeps tracking.
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
            // --- Unbounded sim pose — ship never wraps; camera follows this ---
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

            // --- Step display state ---
            if (!_smoothInitialized || shipChanged)
            {
                if (shipChanged)
                    ToroidalDisplay.ResetSession("ShipVisualSync.shipChanged");
                _smoothPos = targetPos;
                _smoothRot = targetRot;
                _smoothInitialized = true;
            }
            else if (catchingUp)
            {
                StepDisplayToward(targetPos, targetRot, dt, CatchUpDisplayMaxSpeed);
            }
            else
            {
                float err = math.distance(_smoothPos, targetPos);
                if (err > DisplayRespawnSnapDistance)
                {
                    _smoothPos = targetPos;
                    _smoothRot = targetRot;
                }
                else if (err > 2f)
                {
                    StepDisplayToward(targetPos, targetRot, dt, CatchUpDisplayMaxSpeed);
                }
                else
                {
                    StepCruiseRawOrCoast(targetPos, targetRot, simVel, dt);
                }
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

            // --- Bank pivots: full query is unsafe during GhostSpawnBacklog Instantiates ---
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
        /// H73 cruise: snap to sim when within one tick; otherwise coast + capped soft correct (H71).
        /// </summary>
        /// <param name="simPos">Predicted / reconciled pose this frame.</param>
        /// <param name="simRot">Sim rotation.</param>
        /// <param name="simVel">Kinematics velocity (world units/sec).</param>
        /// <param name="dt">Clamped unscaled frame delta.</param>
        void StepCruiseRawOrCoast(float3 simPos, quaternion simRot, float3 simVel, float dt)
        {
            float expected = math.max(math.length(simVel) * math.max(0f, dt), 0.02f);
            float dist = math.distance(_smoothPos, simPos);

            // --- Healthy frame: within one tick (+slop) — raw follow, no spring shimmer ---
            // H72 failed because capped pull left a permanent >deadzone lag and corrected forever.
            if (dist <= expected * CruiseRawFollowSlopRatio)
            {
                _smoothPos = simPos;
                _smoothRot = simRot;
                return;
            }

            // --- Reconcile pop / large gap: coast then capped soft correct ---
            float3 coasted = _smoothPos + simVel * dt;
            float3 err = simPos - coasted;
            err.y = 0f;
            float t = 1f - math.exp(-CruiseCorrectSharpness * math.max(0f, dt));
            float3 pull = err * t;
            float pullLen = math.length(pull);
            float pullCap = expected * CruiseCorrectPullCapRatio;
            if (pullLen > pullCap && pullLen > 1e-6f)
                pull *= pullCap / pullLen;

            _smoothPos = coasted + pull;
            _smoothPos.y = simPos.y;
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
            if (ClientJoinSettleCache.GhostSpawnBacklog ||
                ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                _missingShipFrames = 0;
                return;
            }

            _missingShipFrames++;
            if (_missingShipFrames == 1)
            {
                AsteroidDestroyBlinkProbe.NotifyLocalShipUnresolved(
                    $"missing ship streak started hasPose={ShipDisplayPose.HasLocalPose} " +
                    $"cacheEmpty={_smoothShipEntity == Entity.Null}");
            }

            // --- Still holding last pose for camera / toroidal reference ---
            if (_missingShipFrames < MissingShipClearDelayFrames)
                return;

            // --- Confirmed leave / despawn: clear once per streak ---
            if (_missingShipCleared)
                return;

            _missingShipCleared = true;
            AsteroidDestroyBlinkProbe.NotifyLocalShipUnresolved(
                $"confirmed missing after {_missingShipFrames} frames — clearing pose+tiles once");
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
