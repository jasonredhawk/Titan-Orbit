using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Keeps seed-hydrated planets and asteroids on the playable shell. Leftover Y=0
    /// poses from the old flat map sit as an equatorial collider disk the ship hits
    /// but cannot see from a radial camera.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct MapBodyShellSnapSystem : ISystem
    {
        const float MaxRadiusError = 0.25f;

        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipMapBodyQueries)
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

            foreach (var transform in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<PlanetTag>())
            {
                float3 pos = transform.ValueRO.Position;
                if (math.abs(math.length(pos) - radius) > MaxRadiusError)
                {
                    pos = SphericalMapEcs.ProjectToSphere(pos, radius);
                    transform.ValueRW.Position = pos;
                }

                quaternion sit = SphericalMapEcs.SurfaceSitRotation(pos);
                if (math.degrees(math.angle(transform.ValueRO.Rotation, sit)) > 0.5f)
                    transform.ValueRW.Rotation = sit;
            }

            foreach (var transform in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<AsteroidTag>())
            {
                float3 pos = transform.ValueRO.Position;
                if (math.abs(math.length(pos) - radius) <= MaxRadiusError)
                    continue;
                transform.ValueRW.Position = SphericalMapEcs.ProjectToSphere(pos, radius);
            }

            // #region agent log
#if UNITY_EDITOR
            {
                int offPlanets = 0, planetCount = 0, offAsteroids = 0, asteroidCount = 0;
                float minR = float.MaxValue, maxR = 0f;
                foreach (var transform in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<PlanetTag>())
                {
                    planetCount++;
                    float len = math.length(transform.ValueRO.Position);
                    minR = math.min(minR, len);
                    maxR = math.max(maxR, len);
                    if (math.abs(len - radius) > MaxRadiusError)
                        offPlanets++;
                }

                foreach (var transform in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<AsteroidTag>())
                {
                    asteroidCount++;
                    float len = math.length(transform.ValueRO.Position);
                    minR = math.min(minR, len);
                    maxR = math.max(maxR, len);
                    if (math.abs(len - radius) > MaxRadiusError)
                        offAsteroids++;
                }

                double now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                if (now >= s_NextSnapLog)
                {
                    s_NextSnapLog = now + 0.5;
                    try
                    {
                        long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        string json =
                            "{\"sessionId\":\"07b7b6\",\"hypothesisId\":\"H7\",\"location\":\"MapBodyShellSnapSystem\",\"message\":\"shell-census\",\"data\":{\"r\":"
                            + radius.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                            + ",\"planets\":" + planetCount
                            + ",\"offP\":" + offPlanets
                            + ",\"roids\":" + asteroidCount
                            + ",\"offA\":" + offAsteroids
                            + ",\"minR\":" + (minR < float.MaxValue ? minR : 0f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                            + ",\"maxR\":" + maxR.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                            + "},\"timestamp\":" + ts + "}\n";
                        System.IO.File.AppendAllText(@"c:\Users\jason\Documents\repo\Titan-Orbit\debug-07b7b6.log", json);
                    }
                    catch
                    {
                    }
                }
            }
#endif
            // #endregion
        }

#if UNITY_EDITOR
        static double s_NextSnapLog;
#endif
    }
}
