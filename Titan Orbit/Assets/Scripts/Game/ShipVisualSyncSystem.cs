using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Publishes NetCode presentation-phase ship poses once per frame for camera, hybrid leftovers,
    /// and parallax. Remotes use NetCode interpolation as-is.
    /// <para>
    /// [TITAN-ORBIT] Local owner display (basics67 / H75 client motor apply): raw-follow when within one tick of sim;
    /// on larger gaps (reconcile pops) use H71 coast + capped correct. Soft-track on NetCode storms.
    /// Hard-snap when the local ship entity is missing or replaced (rejoin / fresh spawn) so the
    /// camera does not pan from a previous session pose. GhostPredictionSmoothing off (H47).
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

        /// <summary>
        /// Raw frame step above expected * this counts as one "chop" event (cadence test).
        /// </summary>
        const float ChopVsExpectedRatio = 1.35f;

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

        // #region agent log
        float3 _dbgLastDisplayPos;
        float3 _dbgLastRawPos;
        float3 _dbgLastRawVel;
        bool _dbgHasDisplayPos;
        bool _dbgHasRawPos;
        bool _dbgHasRawVel;
        float2 _dbgLastMoveXz;
        bool _dbgHasMoveXz;
        int _dbgMicroReverseWindow;
        int _dbgMicroReverseCount;
        float _dbgDeltaSum;
        float _dbgDeltaMax;
        float _dbgRawDeltaMax;
        int _dbgDeltaSamples;
        int _dbgSimBatchMax;
        float _dbgExpectedDeltaSum;
        float _dbgStepRatioMax;
        float _dbgDtSum;
        int _dbgTickFracSamples;
        float _dbgTickFracSum;
        int _dbgHoldFrames;
        int _dbgCorrectFrames;
        int _dbgRawFollowFrames;
        float _dbgPullMax;
        int _dbgChopCount;
        int _dbgChopBatchSum;
        int _dbgBatch1;
        int _dbgBatch2;
        int _dbgBatch3;
        int _dbgBatch4Plus;
        int _dbgSnapAdvances;
        uint _dbgLastSnapTick;
        bool _dbgHasSnapTick;
        float _dbgSurpriseSum;
        float _dbgSurpriseMax;
        int _dbgSimBacksteps;
        int _dbgGc0;
        int _dbgGc1;
        int _dbgGc2;
        float _dbgFrameDtMax;
        float _dbgSpeedMin;
        float _dbgSpeedMax;
        float _dbgSpeedSum;
        bool _dbgHasSpeedSample;
        long _dbgGcAllocBytesSum;
        long _dbgGcAllocBytesMax;
        double _dbgNextAggLog;
        ProfilerRecorder _dbgGcAllocRecorder;
        // #endregion

        /// <summary>Starts GC.Alloc byte recorder used by the 1 Hz aggregate.</summary>
        protected override void OnCreate()
        {
            // #region agent log
            // [UNITY] Memory module marker — bytes allocated on the managed heap this frame.
            _dbgGcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            // #endregion
        }

        /// <summary>Disposes the GC.Alloc recorder.</summary>
        protected override void OnDestroy()
        {
            // #region agent log
            if (_dbgGcAllocRecorder.Valid)
                _dbgGcAllocRecorder.Dispose();
            // #endregion
        }

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

            // #region agent log
            AgentLogLocalShipPresentation(localShip);
            // #endregion
        }

        // #region agent log
        /// <summary>
        /// 1 Hz aggregate: display/raw steps, frame-rate caps, and chop cadence.
        /// </summary>
        void AgentLogLocalShipPresentation(Entity localShip)
        {
            if (localShip == Entity.Null || !EntityManager.Exists(localShip))
                return;
            if (!EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            float3 pos = _smoothInitialized ? _smoothPos : EntityManager.GetComponentData<LocalTransform>(localShip).Position;
            float3 rawPos = EntityManager.GetComponentData<LocalTransform>(localShip).Position;
            float3 vel = float3.zero;
            if (EntityManager.HasComponent<ShipKinematics>(localShip))
                vel = EntityManager.GetComponentData<ShipKinematics>(localShip).Velocity;
            // H75: bake MaxSpeed was 35 on client; after ShipStatApply should match cruise ~13.5.
            float motorMaxSpeed = -1f;
            if (EntityManager.HasComponent<ShipMotorConfig>(localShip))
                motorMaxSpeed = EntityManager.GetComponentData<ShipMotorConfig>(localShip).MaxSpeed;

            float displayDelta = 0f;
            float rawDelta = 0f;
            bool microReverse = false;
            if (_dbgHasDisplayPos)
            {
                float3 delta3 = pos - _dbgLastDisplayPos;
                displayDelta = math.length(delta3);
                float2 moveXz = new float2(delta3.x, delta3.z);
                if (_dbgHasMoveXz && math.lengthsq(moveXz) > 1e-6f && math.lengthsq(_dbgLastMoveXz) > 1e-6f)
                    microReverse = math.dot(math.normalize(moveXz), math.normalize(_dbgLastMoveXz)) < -0.2f;
                if (math.lengthsq(moveXz) > 1e-6f)
                {
                    _dbgLastMoveXz = moveXz;
                    _dbgHasMoveXz = true;
                }
            }

            if (_dbgHasRawPos)
                rawDelta = math.distance(rawPos, _dbgLastRawPos);

            float dt = UnityEngine.Time.unscaledDeltaTime;
            int batchSize = 0;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var ntBatch))
                batchSize = ntBatch.SimulationStepBatchSize;

            // --- Sim surprise vs dead-reckon from previous raw pose ---
            if (_dbgHasRawPos && _dbgHasRawVel && dt > 1e-5f)
            {
                float3 expected = _dbgLastRawPos + _dbgLastRawVel * dt;
                float surprise = math.distance(rawPos, expected);
                _dbgSurpriseSum += surprise;
                if (surprise > _dbgSurpriseMax)
                    _dbgSurpriseMax = surprise;

                float3 rawStep = rawPos - _dbgLastRawPos;
                float speedXZ = math.length(new float2(_dbgLastRawVel.x, _dbgLastRawVel.z));
                if (speedXZ > 0.5f)
                {
                    float2 dir = math.normalize(new float2(_dbgLastRawVel.x, _dbgLastRawVel.z));
                    float along = rawStep.x * dir.x + rawStep.z * dir.y;
                    if (along < -0.02f)
                        _dbgSimBacksteps++;
                }
            }

            float speedNow = math.length(vel);
            // --- H65: intra-second speed wobble (proves whether cruise is flat or hunting MaxSpeed) ---
            if (!_dbgHasSpeedSample)
            {
                _dbgSpeedMin = speedNow;
                _dbgSpeedMax = speedNow;
                _dbgHasSpeedSample = true;
            }
            else
            {
                if (speedNow < _dbgSpeedMin)
                    _dbgSpeedMin = speedNow;
                if (speedNow > _dbgSpeedMax)
                    _dbgSpeedMax = speedNow;
            }
            _dbgSpeedSum += speedNow;

            float expectedNow = speedNow * math.max(0f, dt);
            if (expectedNow > 0.05f && rawDelta > expectedNow * ChopVsExpectedRatio)
            {
                _dbgChopCount++;
                _dbgChopBatchSum += batchSize;
            }

            _dbgLastDisplayPos = pos;
            _dbgHasDisplayPos = true;
            _dbgLastRawPos = rawPos;
            _dbgHasRawPos = true;
            _dbgLastRawVel = vel;
            _dbgHasRawVel = true;

            int maxSteps = 0;
            float speed = speedNow;
            float commandAge = 0f;
            float estimatedRtt = 0f;
            int numPredicted = 0;
            uint targetSlack = 0;
            uint lastSnapLocal = 0;
            uint predictTarget = 0;
            float tickFrac = 0f;
            bool hasServer = ClientServerBootstrap.HasServerWorld;
            bool relay = TitanOrbitRelayState.HasClientRelay;
            bool dedicatedOnline = TitanOrbitSessionManager.IsDedicatedOnlineClient;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var nt))
            {
                batchSize = nt.SimulationStepBatchSize;
                numPredicted = nt.NumPredictedTicksExpected;
                tickFrac = nt.ServerTickFraction;
            }

            if (batchSize <= 1)
                _dbgBatch1++;
            else if (batchSize == 2)
                _dbgBatch2++;
            else if (batchSize == 3)
                _dbgBatch3++;
            else
                _dbgBatch4Plus++;

            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var csr))
                maxSteps = csr.MaxSimulationStepsPerFrame;

            if (SystemAPI.TryGetSingleton<ClientTickRate>(out var ctr))
                targetSlack = ctr.TargetCommandSlack;

            if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
            {
                commandAge = ack.ServerCommandAge / 256f;
                estimatedRtt = ack.EstimatedRTT;
                if (ack.LastReceivedSnapshotByLocal.IsValid)
                {
                    lastSnapLocal = ack.LastReceivedSnapshotByLocal.TickIndexForValidTick;
                    if (_dbgHasSnapTick && lastSnapLocal != _dbgLastSnapTick)
                        _dbgSnapAdvances++;
                    _dbgLastSnapTick = lastSnapLocal;
                    _dbgHasSnapTick = true;
                }
            }

            if (SystemAPI.TryGetSingleton<NetworkTimeSystemData>(out var ntsd) && ntsd.predictTargetTick.IsValid)
                predictTarget = ntsd.predictTargetTick.TickIndexForValidTick;

            float expectedDelta = speed * math.max(0f, dt);
            float stepRatio = expectedDelta > 1e-4f ? displayDelta / expectedDelta : 0f;

            _dbgMicroReverseWindow++;
            _dbgDeltaSum += displayDelta;
            if (displayDelta > _dbgDeltaMax)
                _dbgDeltaMax = displayDelta;
            if (rawDelta > _dbgRawDeltaMax)
                _dbgRawDeltaMax = rawDelta;
            _dbgDeltaSamples++;
            if (batchSize > _dbgSimBatchMax)
                _dbgSimBatchMax = batchSize;
            if (microReverse)
                _dbgMicroReverseCount++;
            _dbgExpectedDeltaSum += expectedDelta;
            if (stepRatio > _dbgStepRatioMax)
                _dbgStepRatioMax = stepRatio;
            _dbgDtSum += dt;
            if (dt > _dbgFrameDtMax)
                _dbgFrameDtMax = dt;
            _dbgTickFracSum += tickFrac;
            _dbgTickFracSamples++;

            // --- H66: managed bytes allocated this frame (0 = no GC.Alloc pressure) ---
            if (_dbgGcAllocRecorder.Valid && _dbgGcAllocRecorder.Count > 0)
            {
                long alloc = _dbgGcAllocRecorder.LastValue;
                _dbgGcAllocBytesSum += alloc;
                if (alloc > _dbgGcAllocBytesMax)
                    _dbgGcAllocBytesMax = alloc;
            }

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < _dbgNextAggLog || _dbgDeltaSamples <= 0)
                return;

            int gc0 = System.GC.CollectionCount(0);
            int gc1 = System.GC.CollectionCount(1);
            int gc2 = System.GC.CollectionCount(2);
            int dGc0 = gc0 - _dbgGc0;
            int dGc1 = gc1 - _dbgGc1;
            int dGc2 = gc2 - _dbgGc2;
            _dbgGc0 = gc0;
            _dbgGc1 = gc1;
            _dbgGc2 = gc2;

            _dbgNextAggLog = now + 1.0;
            float avgDelta = _dbgDeltaSum / _dbgDeltaSamples;
            float avgExpected = _dbgExpectedDeltaSum / _dbgDeltaSamples;
            float avgFps = _dbgDtSum > 1e-6f ? _dbgDeltaSamples / _dbgDtSum : 0f;
            float avgTickFrac = _dbgTickFracSamples > 0 ? _dbgTickFracSum / _dbgTickFracSamples : 0f;
            float avgSurprise = _dbgDeltaSamples > 0 ? _dbgSurpriseSum / _dbgDeltaSamples : 0f;
            float avgChopBatch = _dbgChopCount > 0 ? (float)_dbgChopBatchSum / _dbgChopCount : 0f;
            // H64: prove whether player build reaches ~60 FPS (Editor stays ~30 with target=60/vSync=0).
            string hyp = dedicatedOnline || relay ? "H75" : "H42";
            float speedAvg = _dbgDeltaSamples > 0 ? _dbgSpeedSum / _dbgDeltaSamples : 0f;
            float speedRange = _dbgHasSpeedSample ? (_dbgSpeedMax - _dbgSpeedMin) : 0f;
            long gcAllocAvg = _dbgDeltaSamples > 0 ? _dbgGcAllocBytesSum / _dbgDeltaSamples : 0;
            string agg =
                "{\"hasServer\":" + (hasServer ? "true" : "false") +
                ",\"relay\":" + (relay ? "true" : "false") +
                ",\"dedicatedOnline\":" + (dedicatedOnline ? "true" : "false") +
                ",\"mode\":\"rawOrCoast\"" +
                ",\"rawFollow\":" + _dbgRawFollowFrames +
                ",\"correctFrames\":" + _dbgCorrectFrames +
                ",\"pullMax\":" + _dbgPullMax.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"motorMax\":" + motorMaxSpeed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"targetFps\":" + Application.targetFrameRate +
                ",\"vSync\":" + QualitySettings.vSyncCount +
                ",\"speedMin\":" + _dbgSpeedMin.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"speedMax\":" + _dbgSpeedMax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"speedAvg\":" + speedAvg.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"speedRange\":" + speedRange.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"microRev\":" + _dbgMicroReverseCount +
                ",\"frames\":" + _dbgMicroReverseWindow +
                ",\"holdFrames\":" + _dbgHoldFrames +
                ",\"gc0\":" + dGc0 +
                ",\"gc1\":" + dGc1 +
                ",\"gc2\":" + dGc2 +
                ",\"gcAllocAvg\":" + gcAllocAvg +
                ",\"gcAllocMax\":" + _dbgGcAllocBytesMax +
                ",\"monoUsed\":" + UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() +
                ",\"frameDtMax\":" + _dbgFrameDtMax.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"chopCount\":" + _dbgChopCount +
                ",\"avgChopBatch\":" + avgChopBatch.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"b1\":" + _dbgBatch1 +
                ",\"b2\":" + _dbgBatch2 +
                ",\"b3\":" + _dbgBatch3 +
                ",\"b4p\":" + _dbgBatch4Plus +
                ",\"snapAdv\":" + _dbgSnapAdvances +
                ",\"avgSurprise\":" + avgSurprise.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"maxSurprise\":" + _dbgSurpriseMax.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"simBack\":" + _dbgSimBacksteps +
                ",\"avgDelta\":" + avgDelta.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"maxDelta\":" + _dbgDeltaMax.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"rawMaxDelta\":" + _dbgRawDeltaMax.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"avgExpected\":" + avgExpected.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"stepRatioMax\":" + _dbgStepRatioMax.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"cmdAge\":" + commandAge.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"targetSlack\":" + targetSlack +
                ",\"predictLead\":" + (predictTarget > 0 && lastSnapLocal > 0
                    ? ((int)predictTarget - (int)lastSnapLocal).ToString()
                    : "na") +
                ",\"rttMs\":" + estimatedRtt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"numPred\":" + numPredicted +
                ",\"maxSteps\":" + maxSteps +
                ",\"simBatchMax\":" + _dbgSimBatchMax +
                ",\"tickFracAvg\":" + avgTickFrac.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"speed\":" + speed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"fps\":" + avgFps.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
            ShipFlightSmoothDebugLog.Write(hyp, "ShipVisualSyncSystem.agg", "1s presentation jitter aggregate", agg);
            _dbgMicroReverseWindow = 0;
            _dbgMicroReverseCount = 0;
            _dbgDeltaSum = 0f;
            _dbgDeltaMax = 0f;
            _dbgRawDeltaMax = 0f;
            _dbgDeltaSamples = 0;
            _dbgSimBatchMax = 0;
            _dbgExpectedDeltaSum = 0f;
            _dbgStepRatioMax = 0f;
            _dbgDtSum = 0f;
            _dbgTickFracSum = 0f;
            _dbgTickFracSamples = 0;
            _dbgHoldFrames = 0;
            _dbgCorrectFrames = 0;
            _dbgRawFollowFrames = 0;
            _dbgPullMax = 0f;
            _dbgChopCount = 0;
            _dbgChopBatchSum = 0;
            _dbgBatch1 = 0;
            _dbgBatch2 = 0;
            _dbgBatch3 = 0;
            _dbgBatch4Plus = 0;
            _dbgSnapAdvances = 0;
            _dbgSurpriseSum = 0f;
            _dbgSurpriseMax = 0f;
            _dbgSimBacksteps = 0;
            _dbgFrameDtMax = 0f;
            _dbgSpeedMin = 0f;
            _dbgSpeedMax = 0f;
            _dbgSpeedSum = 0f;
            _dbgHasSpeedSample = false;
            _dbgGcAllocBytesSum = 0;
            _dbgGcAllocBytesMax = 0;
        }
        // #endregion

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
                // #region agent log
                _dbgHoldFrames++;
                // #endregion
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
                // #region agent log
                _dbgRawFollowFrames++;
                // #endregion
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

            // #region agent log
            _dbgCorrectFrames++;
            if (pullLen > _dbgPullMax)
                _dbgPullMax = math.min(pullLen, pullCap);
            // #endregion

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
