using TitanOrbit.Data;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only visual roll banking for Entities Graphics ships. Applies cosmetic Z-roll on
    /// <see cref="ShipVisualBankPivotTag"/> children so hull meshes bank during turns without
    /// affecting physics yaw. Ported from <c>ShipBankVisualApplier</c> (hybrid proxy path).
    /// Reads Max Bank / Sensitivity / Smoothing from <see cref="ShipBankVisualSettingsCache"/>
    /// for regular hulls, and <see cref="MegaShipCatalog.bankVisualSettings"/> for MEGAs.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(ShipEntitiesGraphicsPresentationSystem))]
    public partial class ShipEntitiesGraphicsBankSystem : SystemBase
    {
        const float IdleVisualLinearSpeedThreshold = 0.12f;
        const float IdleBankAngularVelDeadbandDegPerSec = 18f;

        /// <summary>
        /// [ECS/DOTS] Presentation tick: sample yaw rate per bank pivot, map to target roll, lerp.
        /// Skipped under TransformQuarantine (hybrid GO path owns bank instead).
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
                    pivotTransform.ValueRW.Rotation = quaternion.identity;
                    continue;
                }

                bool isMega = EntityManager.HasComponent<MegaShipState>(shipEntity)
                    && EntityManager.GetComponentData<MegaShipState>(shipEntity).IsMega;
                float maxBank = defaultMaxBank;
                float sensitivity = defaultSensitivity;
                float smoothing = defaultSmoothing;
                float referenceTurn = defaultRefTurn;
                if (isMega && megaSettings != null)
                {
                    maxBank = megaSettings.ClampedMaxBankAngleDegrees;
                    sensitivity = megaSettings.ClampedBankSensitivity;
                    smoothing = megaSettings.ClampedBankSmoothing;
                    referenceTurn = megaSettings.ResolveReferenceTurnDegreesPerSecond();
                }

                var shipTransform = EntityManager.GetComponentData<LocalTransform>(shipEntity);
                float yawDeg = GetPlanarYawDegrees(shipTransform.Rotation);
                SampleYawRate(ref bankState.ValueRW, yawDeg, dt, smoothing);

                float signedYawRate = bankState.ValueRO.SmoothedYawRateDegPerSec;
                if (EntityManager.HasComponent<ShipKinematics>(shipEntity))
                {
                    float3 vel = EntityManager.GetComponentData<ShipKinematics>(shipEntity).Velocity;
                    float speedSq = vel.x * vel.x + vel.z * vel.z;
                    if (speedSq < IdleVisualLinearSpeedThreshold * IdleVisualLinearSpeedThreshold
                        && math.abs(signedYawRate) < IdleBankAngularVelDeadbandDegPerSec)
                    {
                        signedYawRate = 0f;
                    }
                }

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

                pivotTransform.ValueRW = LocalTransform.FromPositionRotationScale(
                    pivotTransform.ValueRO.Position,
                    quaternion.RotateZ(math.radians(-bankState.ValueRO.CurrentBankAngleDeg)),
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
            return moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.001f;
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
