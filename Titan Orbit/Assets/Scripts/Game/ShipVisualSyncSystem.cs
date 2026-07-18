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
    /// [TITAN-ORBIT] Local owner display: raw-follow when within one tick of sim; on larger gaps
    /// (reconcile pops) use coast + capped correct (H71). Soft-track on NetCode storms.
    /// Hard-snap when the local ship entity is missing or replaced (rejoin / fresh spawn) so the
    /// camera does not pan from a previous session pose. GhostPredictionSmoothing is left off so
    /// this system alone owns local presentation (avoids double-smooth jitter).
    /// </para>
    /// <para>
    /// Evidence: H71 absorbed pops (maxDelta max 0.25). H72 deadzone rejected — correctFrames
    /// stayed ~58 because capped pull could not close a steady &gt;0.08u lag, so the spring ran
    /// every frame. H73 snaps when close (no shimmer) and only coasts on real pops.
    /// </para>
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

        // --- Local-owner display state (not sim) ---
        float3 _smoothPos;
        quaternion _smoothRot = quaternion.identity;
        bool _smoothInitialized;
        Entity _smoothShipEntity;

        /// <summary>
        /// Each presentation frame: publish remotes raw, then soft-track or raw-follow local owner.
        /// </summary>
        protected override void OnUpdate()
        {
            // --- Frame stamp for LateUpdate readers ---
            GhostPresentationTransformCache.BeginPublish(UnityEngine.Time.frameCount);

            Entity localShip = Entity.Null;
            EcsGameBridge.TryGetLocalShipEntityOnWorld(World, out localShip);

            // --- Remotes (and non-local): NetCode presentation LocalTransform as-is ---
            foreach (var (lt, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (entity == localShip)
                    continue;

                PublishShip(entity, lt.ValueRO);
            }

            // --- People transports (same presentation phase) ---
            foreach (var (lt, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                GhostPresentationTransformCache.PublishPeopleTransport(entity, ToSnapshot(lt.ValueRO));
            }

            // --- Local owner: storm soft-track or raw sim follow ---
            PublishLocalShipDisplayPose(localShip);
        }

        /// <summary>
        /// Builds the local owner's camera/mesh pose: soft-track on storms, H73 raw-or-coast cruise.
        /// Hard-snaps when the local ship entity is missing or replaced (rejoin / fresh spawn).
        /// </summary>
        /// <param name="localShip">Local player ship entity, or Null when not spawned.</param>
        void PublishLocalShipDisplayPose(Entity localShip)
        {
            if (localShip == Entity.Null || !EntityManager.Exists(localShip))
            {
                // [TITAN-ORBIT] Drop soft-track state on despawn / leave so the next ship hard-snaps.
                // Keeping _smoothPos caused the camera to slide from the old disconnect location
                // to the new home spawn (especially when choosing "start fresh" instead of rescue).
                ResetLocalDisplaySmoothing();
                ShipDisplayPose.ClearLocalPose();
                return;
            }

            if (!EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            var lt = EntityManager.GetComponentData<LocalTransform>(localShip);
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

            // --- Ship entity changed (rejoin / new spawn): hard-snap display to sim ---
            // Soft-track across entity changes made camera pan from the previous session's pose.
            bool shipChanged = _smoothInitialized && _smoothShipEntity != Entity.Null && localShip != _smoothShipEntity;
            _smoothShipEntity = localShip;

            // --- Step display state ---
            if (!_smoothInitialized || shipChanged)
            {
                _smoothPos = targetPos;
                _smoothRot = targetRot;
                _smoothInitialized = true;
            }
            else if (catchingUp)
            {
                // Soft-track during NetCode catch-up storms only (same ship entity).
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
                    // --- H73 cruise: raw follow when close; coast+correct only on pops ---
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
            if (EntityManager.HasComponent<LocalToWorld>(localShip))
            {
                var shipLtw = new LocalToWorld { Value = displayLt.ToMatrix() };
                EntityManager.SetComponentData(localShip, shipLtw);

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
