using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared gem coast / idle integrator (PhysX-style damping + toroidal wrap).
    /// Server and client call the same step so event-hydrated gems stay aligned without ghosts.
    /// Tractor pull is applied by the caller before this step (skip linear damping while locked).
    /// </summary>
    public static class GemMotionLogic
    {
        /// <summary>Default sim step when advancing a late-join gem to "now".</summary>
        public const float CatchUpStepSeconds = 1f / 60f;

        /// <summary>Hard cap so a bad clock cannot spin for seconds on the main thread.</summary>
        public const int CatchUpMaxSteps = 60 * 22;

        /// <summary>
        /// One motion tick. Writes pose, velocities, and Coast/Idle phase.
        /// Does not change Tractor phase.
        /// </summary>
        public static void IntegrateStep(
            ref float3 position,
            ref quaternion rotation,
            ref float3 velocity,
            ref float3 angularVelocity,
            ref byte phase,
            bool underTractor,
            float dt,
            float linearDamping,
            float angularDamping,
            float stopSpeed,
            float mapW,
            float mapH,
            bool haveMap)
        {
            if (dt <= 0f)
                return;

            float3 vel = underTractor
                ? velocity
                : GemExplosionMath.IntegrateLinearVelocity(
                    velocity, linearDamping, stopSpeed, dt);
            float3 ang = GemExplosionMath.IntegrateAngularVelocity(
                angularVelocity, angularDamping, dt);

            position += vel * dt;
            if (haveMap && ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                position = ToroidalMapEcs.Wrap(position, mapW, mapH);

            if (math.lengthsq(ang) > 0.0001f)
            {
                float angle = math.length(ang) * dt;
                float3 axis = math.normalizesafe(ang, new float3(0f, 1f, 0f));
                rotation = math.mul(quaternion.AxisAngle(axis, angle), rotation);
            }

            velocity = vel;
            angularVelocity = ang;

            if (underTractor)
                return;

            if (math.lengthsq(vel) < stopSpeed * stopSpeed)
                phase = 2; // GemMotionState.PhaseIdle
            else if (phase == 2)
                phase = 0; // GemMotionState.PhaseCoast
        }

        /// <summary>
        /// Advances a freshly resolved spawn to <paramref name="elapsedSeconds"/> after spawn
        /// (late join / approach a field). Tractor is never applied here.
        /// </summary>
        public static void IntegrateElapsed(
            ref float3 position,
            ref quaternion rotation,
            ref float3 velocity,
            ref float3 angularVelocity,
            ref byte phase,
            float elapsedSeconds,
            float linearDamping,
            float angularDamping,
            float stopSpeed,
            float mapW,
            float mapH,
            bool haveMap)
        {
            if (elapsedSeconds <= 0.0001f)
                return;

            float dt = CatchUpStepSeconds;
            int steps = (int)math.floor(elapsedSeconds / dt);
            if (steps > CatchUpMaxSteps)
                steps = CatchUpMaxSteps;

            for (int i = 0; i < steps; i++)
            {
                IntegrateStep(
                    ref position,
                    ref rotation,
                    ref velocity,
                    ref angularVelocity,
                    ref phase,
                    underTractor: false,
                    dt,
                    linearDamping,
                    angularDamping,
                    stopSpeed,
                    mapW,
                    mapH,
                    haveMap);
            }

            float leftover = elapsedSeconds - steps * dt;
            if (leftover > 0.0001f)
            {
                IntegrateStep(
                    ref position,
                    ref rotation,
                    ref velocity,
                    ref angularVelocity,
                    ref phase,
                    underTractor: false,
                    leftover,
                    linearDamping,
                    angularDamping,
                    stopSpeed,
                    mapW,
                    mapH,
                    haveMap);
            }
        }
    }
}
