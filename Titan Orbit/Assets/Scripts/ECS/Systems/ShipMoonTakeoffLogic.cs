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
        /// <summary>
        /// Gentle leave, unit slope at the end. Smoothstep (end slope 0) parked the hull
        /// then thrust had to restart from moon-orbit tangent — a hitch at full size.
        /// p(t) = t²(2−t); p'(0)=0, p'(1)=1.
        /// </summary>
        public static float EaseLeave(float t)
        {
            t = math.saturate(t);
            return t * t * (2f - t);
        }

        /// <summary>d/dt of <see cref="EaseLeave"/> on 0–1 (4t − 3t²).</summary>
        public static float EaseLeaveDerivative(float t)
        {
            t = math.saturate(t);
            return t * (4f - 3f * t);
        }

        /// <summary>
        /// Advances takeoff and writes planar pose. Velocity is moon orbit plus the
        /// leave-curve radial speed so free flight continues outward. Start and exit
        /// radii use the same covering hull as dock attach so MEGA boxes do not snap
        /// inward then get PhysX-yeeted out again.
        /// Clears <see cref="ShipMoonDockState.TakeoffPlanetId"/> on the tick after the
        /// lerp finishes (so AfterPhysics can still restore a PhysX yeet) or when the
        /// planet snapshot is missing.
        /// </summary>
        /// <param name="moonDock">Dock/takeoff state (takeoff fields are written here).</param>
        /// <param name="transform">Ship pose — position and yaw are overwritten while taking off.</param>
        /// <param name="physicsVelocity">Linear velocity handed to Unity Physics.</param>
        /// <param name="planets">Per-tick planet snapshots (toroidal moon pose + zone radius).</param>
        /// <param name="dt">Fixed prediction step delta time.</param>
        /// <param name="mapW">Toroidal map width from <c>MapStateSingleton</c>.</param>
        /// <param name="mapH">Toroidal map height from <c>MapStateSingleton</c>.</param>
        /// <param name="elapsedSeconds">Shared moon orbit clock (ServerTick seconds).</param>
        /// <param name="shipPhysicsRadius">
        /// Live PhysX covering radius (same sentinel as dock attach). Presentation
        /// fallback when unset or smaller than the visual sphere.
        /// </param>
        /// <returns>True while takeoff still owns the motor this tick (including the finish tick).</returns>
        public static bool TryApply(
            ref ShipMoonDockState moonDock,
            ref LocalTransform transform,
            ref PhysicsVelocity physicsVelocity,
            in NativeArray<PlanetMotorSnapshot> planets,
            float dt,
            float mapW,
            float mapH,
            double elapsedSeconds,
            float shipPhysicsRadius = -1f)
        {
            int planetId = moonDock.TakeoffPlanetId;
            if (planetId == 0)
                return false;

            // Finished the authored lerp last tick. Release so thrust owns flight.
            // AfterPhysics on the finish tick still saw IsTakingOff and restored any PhysX yeet.
            if (moonDock.TakeoffProgress >= 1f)
            {
                moonDock.TakeoffPlanetId = 0;
                moonDock.TakeoffProgress = 0f;
                return false;
            }

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
            ComputeOutward(moonPos, planetXform.Position, mapW, mapH, out float3 outward);

            // Same covering hull as dock attach. Presentation-only start used to snap MEGA
            // centers inward, then the short exit left the box in the zone so AfterPhysics
            // yeeted them out — two hops off the moon.
            float shipRadius = ShipPhysicsDriveLogic.ResolveMoonAttachHullRadius(
                shipPhysicsRadius, transform);
            float zoneRadius = math.max(snapshot.MoonBodyRadiusWorld, snapshot.ShieldOuterRadiusWorld);
            ComputeRadii(
                snapshot.MoonBodyRadiusWorld,
                zoneRadius,
                shipRadius,
                out float startRadius,
                out float exitRadius);

            float duration = math.max(0.2f, GemEconomyConstants.MoonTakeoffDurationSeconds);
            moonDock.TakeoffProgress = math.min(1f, moonDock.TakeoffProgress + dt / duration);
            float eased = EaseLeave(moonDock.TakeoffProgress);

            float radius = math.lerp(startRadius, exitRadius, eased);
            float3 pos = moonPos + outward * radius;
            pos.y = 0f;
            transform.Position = pos;
            transform.Rotation = quaternion.LookRotationSafe(outward, math.up());

            float3 moonVel = PlanetOrbitMath.GetMoonOrbitalVelocity(
                planetSize,
                planet.PlanetLevel,
                planet.PlanetId,
                elapsedSeconds);
            moonVel.y = 0f;
            // Authored pose still owns position this tick (AfterPhysics restores). Write the
            // leave-curve radial speed so the first free-flight tick continues outward
            // instead of inheriting only moon tangent and hitching when thrust rebuilds.
            float radialSpeed = (exitRadius - startRadius)
                * EaseLeaveDerivative(moonDock.TakeoffProgress) / duration;
            physicsVelocity = new PhysicsVelocity
            {
                Linear = moonVel + outward * radialSpeed,
                Angular = float3.zero,
            };

            // Keep TakeoffPlanetId this tick so AfterPhysics still treats us as taking off
            // and restores moon/shield depenetration. Next drive tick releases (progress >= 1).
            return true;
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

        /// <summary>
        /// Contact and exit distances from the moon center. Start matches dock attach
        /// (body + covering hull + standoff). Exit clears the drawn orbit shell by the
        /// same hull plus <see cref="GemEconomyConstants.MoonTakeoffExitPadWorld"/>.
        /// </summary>
        public static void ComputeRadii(
            float moonBodyRadiusWorld,
            float zoneRadiusWorld,
            float shipRadius,
            out float startRadius,
            out float exitRadius)
        {
            float hull = math.max(0.05f, shipRadius);
            startRadius = moonBodyRadiusWorld + hull
                + GemEconomyConstants.MoonTakeoffSurfaceStandoffWorld;
            exitRadius = math.max(zoneRadiusWorld, moonBodyRadiusWorld) + hull
                + GemEconomyConstants.MoonTakeoffExitPadWorld;
            if (exitRadius < startRadius + 0.25f)
                exitRadius = startRadius + 0.25f;
        }

        /// <summary>
        /// Unit XZ planet→moon ray on the near tile (takeoff always leaves on the far side).
        /// </summary>
        public static void ComputeOutward(
            float3 moonPos,
            float3 planetPos,
            float mapW,
            float mapH,
            out float3 outward)
        {
            float3 planetNear = moonPos + ToroidalMapEcs.ShortestOffsetXZ(
                moonPos, planetPos, mapW, mapH);
            planetNear.y = 0f;
            outward = moonPos - planetNear;
            outward.y = 0f;
            float outwardLen = math.length(outward);
            if (outwardLen < 1e-4f)
                outward = new float3(1f, 0f, 0f);
            else
                outward /= outwardLen;
        }

        /// <summary>
        /// Planar pose at takeoff progress 1 — moon + outward × exit radius, Y = 0.
        /// Client cinematic aims here so it does not chase the mid-lerp ECS pose.
        /// </summary>
        public static float3 EvaluateExitPosition(
            float3 moonPos,
            float3 outward,
            float exitRadius)
        {
            float3 pos = moonPos + outward * exitRadius;
            pos.y = 0f;
            return pos;
        }
    }
}
