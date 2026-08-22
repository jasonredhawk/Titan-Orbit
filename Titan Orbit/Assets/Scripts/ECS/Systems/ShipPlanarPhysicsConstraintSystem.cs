using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Mario Galaxy pose walk after Unity Physics. Transports the pre-physics shell pose
    /// (position + rotation) along tangent velocity so poles are not a special case.
    /// PhysX chords and leftover Y=0 shoves are discarded. Pipeline: Drive → Physics →
    /// Bounce → Friction → Planar (this) → KinematicsSync.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(ShipKinematicsSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipPlanarPhysicsConstraintSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            float radius = 0f;
            if (SystemAPI.HasSingleton<MapStateSingleton>())
            {
                var map = SystemAPI.GetSingleton<MapStateSingleton>();
                SphericalMapEcs.ResolveRadius(
                    map.MapRadius, math.max(map.MapWidth, map.MapHeight), out radius);
            }

            if (!SphericalMapEcs.IsValidRadius(radius) && !SphericalMapEcs.TryGetRadius(out radius))
                return;

            const float restSpeedSq = 0.0064f; // 0.08 u/s — kill PhysX leftover so parked hulls stay put
            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            foreach (var (transform, velocity, shipState, snapshot) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<ShipState>,
                             RefRO<ShipPreCollisionVelocity>>()
                         .WithAll<ShipTag, Simulate>())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                // Geodesic step from the pre-physics shell pose. PhysX integrates a Euclidean
                // chord and leftover Y=0 colliders can shove the hull; neither is the flight path.
                float3 from = snapshot.ValueRO.Position;
                if (math.lengthsq(from) < 1e-6f)
                    from = transform.ValueRO.Position;
                quaternion fromRot = snapshot.ValueRO.Rotation;
                if (math.lengthsq(fromRot.value) < 1e-8f)
                    fromRot = transform.ValueRO.Rotation;
                SphericalMapEcs.TransportPose(
                    from,
                    fromRot,
                    velocity.ValueRO.Linear,
                    dt,
                    radius,
                    out float3 pos,
                    out quaternion rot,
                    out float3 linear);
                if (math.lengthsq(linear) < restSpeedSq)
                    linear = float3.zero;

                transform.ValueRW.Position = pos;
                transform.ValueRW.Rotation = rot;

                float3 up = SphericalMapEcs.LocalUp(pos);
                float yawRate = math.dot(velocity.ValueRO.Angular, up);
                velocity.ValueRW = new PhysicsVelocity
                {
                    Linear = linear,
                    Angular = up * yawRate,
                };
            }
        }
    }
}
