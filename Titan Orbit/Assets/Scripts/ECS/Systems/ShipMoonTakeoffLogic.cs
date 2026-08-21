using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared moon-takeoff motor: force the hull out of the gem-moon orbit zone along the
    /// planet → moon ray (away from the planet, into open space). Same math on server
    /// authority and client owner prediction so reconciliation stays quiet.
    /// <para>
    /// [TITAN-ORBIT] Thrust used to clear dock and leave the ship on whatever side it landed.
    /// Landing on the planet-facing side dropped the hull into the moon/planet sandwich —
    /// orbit motor, moon body, and planet keep-out then fought the player. Takeoff always
    /// exits on the far side of the moon. mapW/mapH come from <c>MapStateSingleton</c> /
    /// <see cref="ToroidalMapEcs"/> (same sources as dock attach).
    /// </para>
    /// Paired with <see cref="ShipPhysicsDriveLogic"/> and <see cref="ShipMoonDockState"/>.
    /// </summary>
    public static class ShipMoonTakeoffLogic
    {
        /// <summary>Smoothstep ease so takeoff starts and finishes without a pop.</summary>
        static float EaseInOut(float t)
        {
            t = math.saturate(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Advances takeoff and writes planar pose + velocity. Clears
        /// <see cref="ShipMoonDockState.TakeoffPlanetId"/> when the hull is outside the zone
        /// or the planet snapshot is missing.
        /// </summary>
        /// <param name="moonDock">Dock/takeoff state (takeoff fields are written here).</param>
        /// <param name="transform">Ship pose — position and yaw are overwritten while taking off.</param>
        /// <param name="physicsVelocity">Linear velocity handed to Unity Physics.</param>
        /// <param name="planets">Per-tick planet snapshots (toroidal moon pose + zone radius).</param>
        /// <param name="dt">Fixed prediction step delta time.</param>
        /// <param name="mapW">Toroidal map width from <c>MapStateSingleton</c>.</param>
        /// <param name="mapH">Toroidal map height from <c>MapStateSingleton</c>.</param>
        /// <param name="elapsedSeconds">Shared moon orbit clock (ServerTick seconds).</param>
        /// <param name="takeoffSpeed">Outward speed after exit (world units/s).</param>
        /// <param name="isMegaShip">MEGAs get a larger exit pad so the long hull clears the disc.</param>
        /// <returns>True while takeoff still owns the motor this tick.</returns>
        public static bool TryApply(
            ref ShipMoonDockState moonDock,
            ref LocalTransform transform,
            ref PhysicsVelocity physicsVelocity,
            in NativeArray<PlanetMotorSnapshot> planets,
            float dt,
            float mapW,
            float mapH,
            double elapsedSeconds,
            float takeoffSpeed,
            bool isMegaShip)
        {
            int planetId = moonDock.TakeoffPlanetId;
            if (planetId == 0)
                return false;

            if (!TryFindPlanetById(planetId, in planets, out PlanetMotorSnapshot snapshot))
            {
                moonDock.TakeoffPlanetId = 0;
                moonDock.TakeoffProgress = 0f;
                return false;
            }

            var planet = snapshot.Planet;
            var planetXform = snapshot.Transform;
            float planetSize = math.max(0.25f, planetXform.Scale);

            // [TITAN-ORBIT] Near-tile moon — same unwrap as dock attach / combat.
            float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                transform.Position,
                planetXform.Position,
                planetSize,
                planet.PlanetLevel,
                planet.PlanetId,
                elapsedSeconds,
                mapW,
                mapH);

            // Planet copy on the same tile as the moon so planet→moon is the short outward ray.
            float3 planetNear = moonPos + ToroidalMapEcs.ShortestOffsetXZ(
                moonPos, planetXform.Position, mapW, mapH);
            planetNear.y = 0f;
            float3 outward = moonPos - planetNear;
            outward.y = 0f;
            float outwardLen = math.length(outward);
            if (outwardLen < 1e-4f)
                outward = new float3(1f, 0f, 0f);
            else
                outward /= outwardLen;

            float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(transform.Scale);
            float exitPad = GemEconomyConstants.MoonTakeoffExitPadWorld;
            if (isMegaShip)
                exitPad += shipRadius;

            // Drawn moon orbit shell (same radius collected for shield / zone visuals).
            float zoneRadius = math.max(snapshot.MoonBodyRadiusWorld, snapshot.ShieldOuterRadiusWorld);
            float startRadius = snapshot.MoonBodyRadiusWorld + shipRadius
                + GemEconomyConstants.MoonTakeoffSurfaceStandoffWorld;
            float exitRadius = zoneRadius + shipRadius + exitPad;
            if (exitRadius < startRadius + 0.25f)
                exitRadius = startRadius + 0.25f;

            float duration = math.max(0.2f, GemEconomyConstants.MoonTakeoffDurationSeconds);
            moonDock.TakeoffProgress = math.min(1f, moonDock.TakeoffProgress + dt / duration);
            float eased = EaseInOut(moonDock.TakeoffProgress);

            float radius = math.lerp(startRadius, exitRadius, eased);
            float3 pos = moonPos + outward * radius;
            pos.y = 0f;
            transform.Position = ToroidalMapEcs.Wrap(pos, mapW, mapH);
            transform.Rotation = quaternion.LookRotationSafe(outward, math.up());

            float3 moonVel = PlanetOrbitMath.GetMoonOrbitalVelocity(
                planetSize,
                planet.PlanetLevel,
                planet.PlanetId,
                elapsedSeconds);
            moonVel.y = 0f;
            float radialSpeed = math.max(takeoffSpeed, (exitRadius - startRadius) / duration);
            physicsVelocity = new PhysicsVelocity
            {
                Linear = moonVel + outward * radialSpeed,
                Angular = float3.zero,
            };

            if (moonDock.TakeoffProgress < 1f)
                return true;

            moonDock.TakeoffPlanetId = 0;
            moonDock.TakeoffProgress = 0f;
            return false;
        }

        /// <summary>
        /// Looks up a planet snapshot by <see cref="PlanetState.PlanetId"/>.
        /// </summary>
        static bool TryFindPlanetById(
            int planetId,
            in NativeArray<PlanetMotorSnapshot> planets,
            out PlanetMotorSnapshot snapshot)
        {
            snapshot = default;
            if (planetId == 0)
                return false;

            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Planet.PlanetId != planetId)
                    continue;
                snapshot = planets[i];
                return true;
            }

            return false;
        }
    }
}
