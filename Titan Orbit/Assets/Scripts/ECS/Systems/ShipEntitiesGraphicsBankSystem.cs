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
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(ShipEntitiesGraphicsPresentationSystem))]
    public partial class ShipEntitiesGraphicsBankSystem : SystemBase
    {
        const float IdleVisualLinearSpeedThreshold = 0.12f;
        const float IdleBankAngularVelDeadbandDegPerSec = 18f;
        const float BankSmoothing = 8f;

        protected override void OnUpdate()
        {
            if (!TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                return;

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

                var shipTransform = EntityManager.GetComponentData<LocalTransform>(shipEntity);
                float yawDeg = GetPlanarYawDegrees(shipTransform.Rotation);
                SampleYawRate(ref bankState.ValueRW, yawDeg, dt);

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

                float globalMaxTurn = ShipPropulsionAggregation.GetGlobalMaxTurnSpeedDegreesPerSecond();
                float targetBank = ShipPropulsionAggregation.ComputeVisualBankTargetAngle(
                    signedYawRate,
                    ShipPropulsionAggregation.VisualBankReferenceMaxAngleDegrees,
                    globalMaxTurn);

                float bankT = 1f - math.exp(-BankSmoothing * dt);
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

        static void SampleYawRate(ref ShipVisualBankState bankState, float yawDeg, float dt)
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

            float velT = 1f - math.exp(-BankSmoothing * dt);
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
