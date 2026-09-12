using TitanOrbit.Data;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only visual roll + pitch for Entities Graphics ships. Applies cosmetic Z-roll and
    /// X-pitch on <see cref="ShipVisualBankPivotTag"/> children so hull meshes bank during turns
    /// and dip on accel / collisions without affecting physics yaw. Ported from
    /// <c>ShipBankVisualApplier</c> (hybrid proxy path).
    /// Reads knobs from <see cref="ShipBankVisualSettingsCache"/> for regular hulls, and
    /// <see cref="MegaShipCatalog.bankVisualSettings"/> for MEGAs.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(ShipEntitiesGraphicsPresentationSystem))]
    public partial class ShipEntitiesGraphicsBankSystem : SystemBase
    {
        /// <summary>Ignore interpolation noise at rest. Intentional yaw (including slow MEGA turns) is above this.</summary>
        const float RestBankAngularVelDeadbandDegPerSec = 2f;

        /// <summary>
        /// [ECS/DOTS] Presentation tick: sample yaw rate and planar speed per bank pivot,
        /// map to target roll + pitch, lerp. Skipped under TransformQuarantine (hybrid GO path owns attitude).
        /// </summary>
        protected override void OnUpdate()
        {
            // --- Join / presentation gates ---
            // [TITAN-ORBIT] Hybrid proxies draw ships while quarantined; EG bank would fight them.
            if (ClientJoinSettleCache.TransformQuarantine ||
                !TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                return;

            // --- Designer knobs (family cache vs MegaShipCatalog.bankVisualSettings) ---
            float defaultMaxBank = ShipBankVisualSettingsCache.MaxBankAngleDegrees;
            float defaultSensitivity = ShipBankVisualSettingsCache.BankSensitivity;
            float defaultSmoothing = ShipBankVisualSettingsCache.BankSmoothing;
            float defaultRefTurn = ShipBankVisualSettingsCache.ReferenceTurnDegreesPerSecond;
            float defaultMaxPitchDown = ShipBankVisualSettingsCache.MaxPitchDownDegrees;
            float defaultMaxPitchUp = ShipBankVisualSettingsCache.MaxPitchUpDegrees;
            float defaultRefAccel = ShipBankVisualSettingsCache.ReferenceAccel;
            float defaultPitchSensitivity = ShipBankVisualSettingsCache.PitchSensitivity;
            float defaultPitchSmoothing = ShipBankVisualSettingsCache.PitchSmoothing;
            float defaultImpactDelta = ShipBankVisualSettingsCache.ImpactDeltaSpeed;
            float defaultImpactDegPerSpeed = ShipBankVisualSettingsCache.ImpactDegreesPerSpeed;
            float defaultImpactDecay = ShipBankVisualSettingsCache.ImpactDecay;
            ShipBankVisualSettings megaSettings = MegaShipCatalog.Load()?.GetBankVisualSettings();

            foreach (var (pivotTag, bankState, pivotTransform, entity) in SystemAPI
                         .Query<RefRO<ShipVisualBankPivotTag>, RefRW<ShipVisualBankState>, RefRW<LocalTransform>>()
                         .WithEntityAccess())
            {
                Entity shipEntity = pivotTag.ValueRO.ShipEntity;
                if (!EntityManager.Exists(shipEntity)
                    || !EntityManager.HasComponent<LocalTransform>(shipEntity))
                {
                    pivotTransform.ValueRW.Rotation = quaternion.identity;
                    continue;
                }

                if (EntityManager.HasComponent<ShipState>(shipEntity))
                {
                    var ship = EntityManager.GetComponentData<ShipState>(shipEntity);
                    if (ship.IsDead)
                    {
                        pivotTransform.ValueRW.Rotation = quaternion.identity;
                        continue;
                    }
                }

                if (ShouldSuppressForMoonDock(shipEntity))
                {
                    bankState.ValueRW.CurrentBankAngleDeg = 0f;
                    bankState.ValueRW.SmoothedYawRateDegPerSec = 0f;
                    bankState.ValueRW.PrevYawDeg = GetPlanarYawDegrees(
                        EntityManager.GetComponentData<LocalTransform>(shipEntity).Rotation);
                    bankState.ValueRW.YawInitialized = true;
                    ResetPitchState(ref bankState.ValueRW);
                    pivotTransform.ValueRW.Rotation = quaternion.identity;
                    continue;
                }

                bool isMega = EntityManager.HasComponent<MegaShipState>(shipEntity)
                    && EntityManager.GetComponentData<MegaShipState>(shipEntity).IsMega;
                float maxBank = defaultMaxBank;
                float sensitivity = defaultSensitivity;
                float smoothing = defaultSmoothing;
                float referenceTurn = defaultRefTurn;
                float maxPitchDown = defaultMaxPitchDown;
                float maxPitchUp = defaultMaxPitchUp;
                float referenceAccel = defaultRefAccel;
                float pitchSensitivity = defaultPitchSensitivity;
                float pitchSmoothing = defaultPitchSmoothing;
                float impactDelta = defaultImpactDelta;
                float impactDegPerSpeed = defaultImpactDegPerSpeed;
                float impactDecay = defaultImpactDecay;
                if (isMega && megaSettings != null)
                {
                    maxBank = megaSettings.ClampedMaxBankAngleDegrees;
                    sensitivity = megaSettings.ClampedBankSensitivity;
                    smoothing = megaSettings.ClampedBankSmoothing;
                    referenceTurn = megaSettings.ResolveReferenceTurnDegreesPerSecond();
                    maxPitchDown = megaSettings.ClampedMaxPitchDownDegrees;
                    maxPitchUp = megaSettings.ClampedMaxPitchUpDegrees;
                    referenceAccel = megaSettings.ClampedReferenceAccel;
                    pitchSensitivity = megaSettings.ClampedPitchSensitivity;
                    pitchSmoothing = megaSettings.ClampedPitchSmoothing;
                    impactDelta = megaSettings.ClampedImpactDeltaSpeed;
                    impactDegPerSpeed = megaSettings.ClampedImpactDegreesPerSpeed;
                    impactDecay = megaSettings.ClampedImpactDecay;
                }

                var shipTransform = EntityManager.GetComponentData<LocalTransform>(shipEntity);
                float yawDeg = GetPlanarYawDegrees(shipTransform.Rotation);
                SampleYawRate(ref bankState.ValueRW, yawDeg, dt, smoothing);

                float signedYawRate = bankState.ValueRO.SmoothedYawRateDegPerSec;
                // [TITAN-ORBIT] Kill rest-pose interpolation noise only — rotating in place still banks.
                if (math.abs(signedYawRate) < RestBankAngularVelDeadbandDegPerSec)
                    signedYawRate = 0f;

                // --- Target bank (same helper as hybrid ShipBankVisualApplier) ---
                float targetBank = ShipPropulsionAggregation.ComputeVisualBankTargetAngle(
                    signedYawRate,
                    maxBank,
                    referenceTurn,
                    sensitivity);

                float bankT = 1f - math.exp(-smoothing * dt);
                bankState.ValueRW.CurrentBankAngleDeg = math.lerp(
                    bankState.ValueRO.CurrentBankAngleDeg,
                    targetBank,
                    bankT);

                float planarSpeed = SamplePlanarSpeed(shipEntity);
                float prevForwardSpeed = bankState.ValueRO.PrevForwardSpeed;
                bool pitchSpeedInitialized = bankState.ValueRO.PitchSpeedInitialized;
                float smoothedForwardAccel = bankState.ValueRO.SmoothedForwardAccel;
                float accelPitchDeg = bankState.ValueRO.AccelPitchAngleDeg;
                float impactPitchDeg = bankState.ValueRO.ImpactPitchAngleDeg;
                float pitchDeg = ShipPropulsionAggregation.StepVisualPitch(
                    planarSpeed,
                    dt,
                    maxPitchDown,
                    maxPitchUp,
                    referenceAccel,
                    pitchSensitivity,
                    pitchSmoothing,
                    impactDelta,
                    impactDegPerSpeed,
                    impactDecay,
                    ref prevForwardSpeed,
                    ref pitchSpeedInitialized,
                    ref smoothedForwardAccel,
                    ref accelPitchDeg,
                    ref impactPitchDeg);
                bankState.ValueRW.PrevForwardSpeed = prevForwardSpeed;
                bankState.ValueRW.PitchSpeedInitialized = pitchSpeedInitialized;
                bankState.ValueRW.SmoothedForwardAccel = smoothedForwardAccel;
                bankState.ValueRW.AccelPitchAngleDeg = accelPitchDeg;
                bankState.ValueRW.ImpactPitchAngleDeg = impactPitchDeg;
                bankState.ValueRW.CurrentPitchAngleDeg = pitchDeg;

                pivotTransform.ValueRW = LocalTransform.FromPositionRotationScale(
                    pivotTransform.ValueRO.Position,
                    quaternion.EulerZXY(
                        math.radians(bankState.ValueRO.CurrentPitchAngleDeg),
                        0f,
                        math.radians(-bankState.ValueRO.CurrentBankAngleDeg)),
                    pivotTransform.ValueRO.Scale);
                SyncPivotLocalToWorld(entity, pivotTransform.ValueRO);
            }
        }

        void SyncPivotLocalToWorld(Entity pivotEntity, in LocalTransform pivotLocal)
        {
            if (!EntityManager.HasComponent<LocalToWorld>(pivotEntity)
                || !EntityManager.HasComponent<Parent>(pivotEntity))
                return;

            Entity parentEntity = EntityManager.GetComponentData<Parent>(pivotEntity).Value;
            if (!EntityManager.Exists(parentEntity) || !EntityManager.HasComponent<LocalToWorld>(parentEntity))
                return;

            var parentLocalToWorld = EntityManager.GetComponentData<LocalToWorld>(parentEntity).Value;
            EntityManager.SetComponentData(pivotEntity, new LocalToWorld
            {
                Value = math.mul(parentLocalToWorld, pivotLocal.ToMatrix()),
            });
        }

        bool ShouldSuppressForMoonDock(Entity shipEntity)
        {
            if (!EntityManager.HasComponent<ShipMoonDockState>(shipEntity))
                return false;

            var moonDock = EntityManager.GetComponentData<ShipMoonDockState>(shipEntity);
            return moonDock.IsTakingOff ||
                   (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.001f);
        }

        static void ResetPitchState(ref ShipVisualBankState bankState)
        {
            bankState.CurrentPitchAngleDeg = 0f;
            bankState.SmoothedForwardAccel = 0f;
            bankState.AccelPitchAngleDeg = 0f;
            bankState.ImpactPitchAngleDeg = 0f;
            bankState.PrevForwardSpeed = 0f;
            bankState.PitchSpeedInitialized = false;
        }

        /// <summary>
        /// Planar speed from ghosted <see cref="ShipKinematics"/>.
        /// No extra ghost fields — remotes interpolate the same velocity the HUD already shows.
        /// </summary>
        float SamplePlanarSpeed(Entity shipEntity)
        {
            if (!EntityManager.HasComponent<ShipKinematics>(shipEntity))
                return 0f;

            float3 vel = EntityManager.GetComponentData<ShipKinematics>(shipEntity).Velocity;
            return math.sqrt(vel.x * vel.x + vel.z * vel.z);
        }

        /// <summary>Exponentially smooths planar yaw rate (°/s) for stable bank targets.</summary>
        /// <param name="bankState">Mutable yaw sample state on the pivot entity.</param>
        /// <param name="yawDeg">Current planar yaw of the ship ghost.</param>
        /// <param name="dt">Frame delta time (seconds).</param>
        /// <param name="smoothing">Catch-up rate from <see cref="ShipBankVisualSettingsCache"/>.</param>
        static void SampleYawRate(ref ShipVisualBankState bankState, float yawDeg, float dt, float smoothing)
        {
            if (!bankState.YawInitialized)
            {
                bankState.PrevYawDeg = yawDeg;
                bankState.YawInitialized = true;
                bankState.SmoothedYawRateDegPerSec = 0f;
                return;
            }

            dt = math.max(1e-5f, dt);
            float instantRate = DeltaAngleDegrees(bankState.PrevYawDeg, yawDeg) / dt;
            bankState.PrevYawDeg = yawDeg;

            float velT = 1f - math.exp(-smoothing * dt);
            bankState.SmoothedYawRateDegPerSec = math.lerp(
                bankState.SmoothedYawRateDegPerSec,
                instantRate,
                velT);
        }

        static float DeltaAngleDegrees(float fromDeg, float toDeg)
        {
            float delta = toDeg - fromDeg;
            while (delta > 180f)
                delta -= 360f;
            while (delta < -180f)
                delta += 360f;
            return delta;
        }

        static float GetPlanarYawDegrees(quaternion rotation)
        {
            float3 forward = math.mul(rotation, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 1e-8f)
                return 0f;
            return math.degrees(math.atan2(forward.x, forward.z));
        }
    }
}
