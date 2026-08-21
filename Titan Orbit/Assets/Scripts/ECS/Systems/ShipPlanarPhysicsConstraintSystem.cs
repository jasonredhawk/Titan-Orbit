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

                // #region agent log
#if UNITY_EDITOR
                float shove = math.length(transform.ValueRO.Position - pos);
                if (shove > 1f)
                    LogGeodesicCorrect(pos, from, shove, math.length(linear));
                float3 poleUp = SphericalMapEcs.LocalUp(pos);
                if (math.abs(poleUp.y) > 0.95f)
                    LogPoleCrossing(pos, poleUp, math.mul(rot, new float3(0f, 0f, 1f)), linear);
#endif
                // #endregion

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

        // #region agent log
#if UNITY_EDITOR
        static double s_NextGeoLog;

        static double s_NextPoleLog;

        static void LogPoleCrossing(float3 pos, float3 up, float3 forward, float3 vel)
        {
            double now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (now < s_NextPoleLog)
                return;
            s_NextPoleLog = now + 0.2;
            float3 flat = SphericalMapEcs.FlattenToTangent(forward, pos);
            try
            {
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json =
                    "{\"sessionId\":\"07b7b6\",\"hypothesisId\":\"H11\",\"location\":\"ShipPlanarPhysicsConstraintSystem\",\"message\":\"pole-crossing\",\"data\":{\"lat\":"
                    + math.degrees(math.asin(math.clamp(up.y, -1f, 1f))).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"upy\":" + up.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"spd\":" + math.length(vel).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"flatFwdSq\":" + math.lengthsq(flat).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"fwdDotUp\":" + math.dot(math.normalizesafe(forward, up), up).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + "},\"timestamp\":" + ts + "}\n";
                System.IO.File.AppendAllText(@"c:\Users\jason\Documents\repo\Titan-Orbit\debug-07b7b6.log", json);
            }
            catch
            {
            }
        }

        static void LogGeodesicCorrect(float3 pos, float3 from, float shove, float spd)
        {
            double now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (now < s_NextGeoLog)
                return;
            s_NextGeoLog = now + 0.25;
            try
            {
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json =
                    "{\"sessionId\":\"07b7b6\",\"hypothesisId\":\"H10\",\"location\":\"ShipPlanarPhysicsConstraintSystem\",\"message\":\"geodesic-correct\",\"data\":{\"shove\":"
                    + shove.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"fromR\":" + math.length(from).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"toR\":" + math.length(pos).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"lat\":" + (math.degrees(math.asin(math.clamp(SphericalMapEcs.LocalUp(pos).y, -1f, 1f)))).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"spd\":" + spd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + "},\"timestamp\":" + ts + "}\n";
                System.IO.File.AppendAllText(@"c:\Users\jason\Documents\repo\Titan-Orbit\debug-07b7b6.log", json);
            }
            catch
            {
            }
        }
#endif
        // #endregion
    }
}
