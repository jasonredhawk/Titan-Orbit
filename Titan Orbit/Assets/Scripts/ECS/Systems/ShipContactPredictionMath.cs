using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Cheap presentation lead for interpolated remotes. Ships coast slowly, so a short
    /// velocity × interpolation-delay step is enough — no second collision solver, no
    /// per-rock sweep. PhysX remains the only contact authority.
    /// </summary>
    public static class ShipContactPredictionMath
    {
        /// <summary>Cap so a starved snapshot cannot throw a remote across the chart.</summary>
        public const float MaxRemoteExtrapolateSeconds = 0.12f;

        /// <summary>
        /// Seconds the interpolated ghost sits behind server-now, clamped to
        /// <see cref="MaxRemoteExtrapolateSeconds"/>.
        /// </summary>
        public static float ComputeInterpolationDelaySeconds(in NetworkTime networkTime, int simulationHz)
        {
            if (!networkTime.ServerTick.IsValid || !networkTime.InterpolationTick.IsValid)
                return 0f;
            int hz = math.max(1, simulationHz);
            int ticks = networkTime.ServerTick.TicksSince(networkTime.InterpolationTick);
            float frac = networkTime.ServerTickFraction - networkTime.InterpolationTickFraction;
            return math.clamp((ticks + frac) / hz, 0f, MaxRemoteExtrapolateSeconds);
        }

        static int s_DelayFrame = -1;
        static float s_CachedDelay;

        /// <summary>Once-per-frame interpolation delay. Creating queries only on the first call.</summary>
        public static float GetClientInterpolationDelaySeconds(EntityManager em)
        {
            int frame = Time.frameCount;
            if (frame == s_DelayFrame)
                return s_CachedDelay;

            s_DelayFrame = frame;
            s_CachedDelay = 0f;
            if (!em.World.IsCreated)
                return 0f;

            using var timeQuery = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            if (timeQuery.IsEmptyIgnoreFilter)
                return 0f;

            var networkTime = timeQuery.GetSingleton<NetworkTime>();
            int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            using var rateQuery = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (!rateQuery.IsEmptyIgnoreFilter)
                hz = math.max(1, rateQuery.GetSingleton<ClientServerTickRate>().SimulationTickRate);

            s_CachedDelay = ComputeInterpolationDelaySeconds(networkTime, hz);
            return s_CachedDelay;
        }

        /// <summary>
        /// One mul-add then wrap. Skip callers should already have proven the body is moving
        /// and that this is not a map-crossing wrap jump.
        /// </summary>
        public static float3 ExtrapolateWrapped(
            float3 interpolatedPos,
            float3 velocity,
            float extrapolateSeconds,
            float mapW,
            float mapH)
        {
            float dt = math.clamp(extrapolateSeconds, 0f, MaxRemoteExtrapolateSeconds);
            if (dt <= 1e-5f)
                return interpolatedPos;

            float3 next = interpolatedPos + velocity * dt;
            next.y = 0f;
            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return next;
            return ToroidalMapEcs.Wrap(next, mapW, mapH);
        }
    }
}
