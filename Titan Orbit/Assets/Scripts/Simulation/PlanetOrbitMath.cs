using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Planet orbit ring geometry for ship passive orbit, gem-moon placement, and decorative level bands.
    /// Ring membership uses the ship pivot (toroidal distance from planet center), not the
    /// covering collider — a wing in the annulus is not enough. Range tests stay toroidal
    /// (see titan-orbit-toroidal-map rule).
    /// <para>
    /// [TITAN-ORBIT] <see cref="GetOrbitRingSpeed"/> is the single tangential speed for a planet's
    /// ring — ships (passive motor) and gem moons (analytic offset) both use it so they co-orbit.
    /// Coasting hulls ease onto the centerline by position. Orbit velocity stays tangential so a
    /// radial spring cannot fling the hull out of the annulus and a later correction cannot snap
    /// it back. Thrust still cancels the motor.
    /// </para>
    /// </summary>
    public static class PlanetOrbitMath
    {
        /// <summary>Tilt of decorative Saturn-style level bands around local X (degrees).</summary>
        public const float LevelBandsTiltDegrees = -26.7f;
        /// <summary>Inner radius of first level band in planet local space (before world scale).</summary>
        public const float LevelBandsInnerRadiusLocal = 0.68f;
        public const float LevelBandThicknessLocal = 0.06f;
        public const float LevelBandGapLocal = 0.022f;

        const float OrbitRingHalfThicknessLocal = 0.11f * 0.7f;
        /// <summary>Gap between the outermost level band and the inner edge of the ship orbit ring.</summary>
        const float OrbitRingClearanceFromLevelBandsLocal = LevelBandGapLocal * 2f;
        /// <summary>Scale of the ship/moon orbit ring radius from planet center (1 = sit just outside max-level bands).</summary>
        const float OrbitRingRadiusFromPlanetCenterScale = 1.1f;
        /// <summary>
        /// How quickly a coasting hull eases toward the ring centerline (world units/s).
        /// One tick never crosses the centerline. The step cap stops a long frame from
        /// sliding the hull across the annulus.
        /// </summary>
        const float OrbitCenterlineEaseSpeed = 1.6f;
        /// <summary>Largest centerline ease applied in one tick while already inside the ring.</summary>
        const float OrbitCenterlineEaseMaxStep = 0.06f;
        /// <summary>
        /// How quickly velocity steers toward ideal tangential orbit (1/s before gravity / mass).
        /// Still a continuous lerp (Starblast pillar 3) — not a one-frame snap onto the rail.
        /// </summary>
        const float OrbitCaptureResponsiveness = 5f;
        /// <summary>
        /// Extra align-rate scale at the inner/outer lips (1 at centerline). Matches
        /// tangential ring speed sooner near the edge. Radius is held separately.
        /// </summary>
        const float OrbitEdgeCaptureMultiplier = 1.6f;
        /// <summary>
        /// Base tangential speed at the orbit-ring centerline (world units/s).
        /// [TITAN-ORBIT] Ships and gem moons share this ring speed — one value per planet ring.
        /// </summary>
        const float BaseOrbitSpeed = 0.8f;

        /// <summary>
        /// Local spin axis shared by the planet body, gem moon, and decorative level bands.
        /// Derived from the level-band ring tilt (XY disc rotated around local X).
        /// </summary>
        public static float3 GetLevelBandsSpinAxisLocal()
        {
            var tilt = quaternion.EulerXYZ(math.radians(LevelBandsTiltDegrees), 0f, 0f);
            return math.normalize(math.mul(tilt, new float3(0f, 0f, 1f)));
        }

        public static quaternion GetLevelBandsTiltRotation() =>
            quaternion.EulerXYZ(math.radians(LevelBandsTiltDegrees), 0f, 0f);

        /// <summary>Local-space outer edge of the decorative Saturn-style level bands (1 band per level).</summary>
        public static float GetLevelBandsOuterRadiusLocal(int planetLevel)
        {
            int bandCount = math.clamp(planetLevel, 1, PlanetEconomyMath.MaxPlanetLevel);
            float step = LevelBandThicknessLocal + LevelBandGapLocal;
            float lastCenter = LevelBandsInnerRadiusLocal + (bandCount - 1) * step;
            return lastCenter + LevelBandThicknessLocal * 0.5f;
        }

        /// <summary>Local-space center radius of the ship orbit ring (fixed for all planet levels).</summary>
        public static float GetOrbitRingCenterRadiusLocal()
        {
            float baseCenter = GetLevelBandsOuterRadiusLocal(PlanetEconomyMath.MaxPlanetLevel)
                + OrbitRingClearanceFromLevelBandsLocal
                + OrbitRingHalfThicknessLocal;
            return baseCenter * OrbitRingRadiusFromPlanetCenterScale;
        }

        public static void GetRingRadiiWorld(float planetSize, int planetLevel, out float innerWorld, out float outerWorld, out float centerWorld)
        {
            // [TITAN-ORBIT] Orbit ring size is fixed for all levels — sits outside max-level decorative bands.
            _ = planetLevel;
            float centerLocal = GetOrbitRingCenterRadiusLocal();
            float innerLocal = math.max(0.52f, centerLocal - OrbitRingHalfThicknessLocal);
            float outerLocal = centerLocal + OrbitRingHalfThicknessLocal;
            centerWorld = planetSize * centerLocal;
            innerWorld = planetSize * innerLocal;
            outerWorld = planetSize * outerLocal;
        }

        /// <summary>
        /// True when the ship pivot distance from planet center lies inside the orbit annulus.
        /// </summary>
        public static bool IsInOrbitRing(float dist, float innerWorld, float outerWorld)
        {
            return dist >= innerWorld && dist <= outerWorld;
        }

        /// <summary>
        /// Planet collision keep-out for a ship center. Natural hull+planet spheres must never
        /// sit outside the orbit-ring inner radius — MEGA covering spheres are larger than the
        /// surface→ring gap on small neutrals (size 9), which blocked ring entry.
        /// </summary>
        /// <param name="shipRadius">Ship collision sphere (world).</param>
        /// <param name="planetRadius">Planet body radius (world).</param>
        /// <param name="planetScale">Planet <c>LocalTransform.Scale</c> (orbit ring uses this).</param>
        /// <returns>Distance the ship center must stay from the planet center.</returns>
        public static float GetPlanetCollisionKeepOut(float shipRadius, float planetRadius, float planetScale)
        {
            float natural = math.max(0.01f, shipRadius + planetRadius);
            GetRingRadiiWorld(math.max(0.25f, planetScale), 0, out float innerWorld, out _, out _);
            return math.min(natural, innerWorld);
        }

        /// <summary>
        /// Canonical tangential speed for a planet's ship/moon orbit ring (centerline).
        /// Larger planets get a slightly faster ring; position inside the thin annulus does
        /// <b>not</b> change speed — ships and moons must share one speed so they co-orbit.
        /// </summary>
        /// <param name="planetSize">Planet uniform scale (world radius proxy).</param>
        /// <returns>Clockwise tangential speed in world units/sec at the ring centerline.</returns>
        public static float GetOrbitRingSpeed(float planetSize)
        {
            // --- Size curve (same endpoints as legacy Starship.GetOrbitTargetSpeed) ---
            // [TITAN-ORBIT] Intentionally ignores radius-within-band. Old 0.7–1.6× inner/outer
            // multiplier made ships lap or lag the moon while both rode the same ring.
            const float minSize = 9f;
            const float maxSize = 18f;
            float sizeNorm = math.clamp((planetSize - minSize) / (maxSize - minSize), 0f, 1f);
            float sizeMultiplier = math.lerp(0.8f, 1.4f, sizeNorm);
            return BaseOrbitSpeed * sizeMultiplier;
        }

        /// <summary>
        /// Tangential orbit speed for this planet's ring. Prefer <see cref="GetOrbitRingSpeed"/>.
        /// Extra radius args are ignored so older call sites stay source-compatible.
        /// </summary>
        public static float GetTargetSpeed(
            float planetSize,
            float radius,
            float innerWorld,
            float outerWorld,
            float centerWorld)
        {
            _ = radius;
            _ = innerWorld;
            _ = outerWorld;
            _ = centerWorld;
            return GetOrbitRingSpeed(planetSize);
        }

        /// <summary>
        /// World-space offset for a body orbiting at the ship orbit ring center (clockwise, matching the ship motor).
        /// <para>
        /// [TITAN-ORBIT] <paramref name="elapsedSeconds"/> must be the shared NetCode ServerTick clock
        /// (<c>PlanetGemMoonOrbitClock</c> in TitanOrbit.ECS) — not <c>World.Time.ElapsedTime</c>.
        /// Moons are not ghosted; client and server both evaluate this formula. Divergent clocks put the
        /// visual moon at one angle and the kinematic collider / shield at another along the same ring.
        /// </para>
        /// </summary>
        public static float3 GetShipOrbitRingOffset(
            float planetSize,
            int planetLevel,
            float phaseOffsetRadians,
            double elapsedSeconds)
        {
            GetRingRadiiWorld(planetSize, planetLevel, out _, out _, out float centerWorld);
            // [TITAN-ORBIT] Same GetOrbitRingSpeed as the ship motor — one ω for this ring radius.
            float speed = GetOrbitRingSpeed(planetSize);
            float omega = centerWorld > 0.001f ? speed / centerWorld : 0f;
            // θ decreases with time → clockwise on XZ when looking down +Y (matches ship orbit motor).
            float theta = phaseOffsetRadians - omega * (float)elapsedSeconds;
            return new float3(math.cos(theta), 0f, math.sin(theta)) * centerWorld;
        }

        /// <summary>Deterministic phase offset per planet so moons do not stack on the same angle.</summary>
        public static float GetShipOrbitPhaseOffset(int planetId)
        {
            uint seed = planetId != 0 ? (uint)planetId : 17u;
            return (seed % 6283u) * 0.001f;
        }

        /// <summary>Gem moon world position orbiting planet center (no toroidal unwrap).</summary>
        public static float3 GetMoonWorldPosition(
            float3 planetPosition,
            float planetSize,
            int planetLevel,
            int planetId,
            double elapsedSeconds,
            bool isHomePlanet = false)
        {
            _ = isHomePlanet;
            float phase = GetShipOrbitPhaseOffset(planetId);
            return planetPosition + GetShipOrbitRingOffset(planetSize, planetLevel, phase, elapsedSeconds);
        }

        /// <summary>
        /// Instantaneous world-space velocity of the gem moon on the ship orbit ring (clockwise).
        /// [TITAN-ORBIT] Derivative of <see cref="GetShipOrbitRingOffset"/> — used to co-orbit a
        /// docked ship so freezing hull velocity does not leave the moon behind.
        /// </summary>
        /// <param name="planetSize">Planet uniform scale (world radius proxy).</param>
        /// <param name="planetLevel">Planet level (ring radii currently ignore level; kept for API stability).</param>
        /// <param name="planetId">Planet id — seeds the same phase offset as moon placement.</param>
        /// <param name="elapsedSeconds">Shared ServerTick orbit clock (same as moon position).</param>
        /// <returns>XZ velocity in world units/sec; Y is always 0.</returns>
        public static float3 GetMoonOrbitalVelocity(
            float planetSize,
            int planetLevel,
            int planetId,
            double elapsedSeconds)
        {
            // --- Match GetShipOrbitRingOffset kinematics ---
            // Position = (cos θ, 0, sin θ) * centerWorld where θ = phase − ω t.
            // d/dt → (sin θ, 0, −cos θ) * speed  (clockwise tangent × ring speed).
            GetRingRadiiWorld(planetSize, planetLevel, out _, out _, out float centerWorld);
            float speed = GetOrbitRingSpeed(planetSize);
            float omega = centerWorld > 0.001f ? speed / centerWorld : 0f;
            float phase = GetShipOrbitPhaseOffset(planetId);
            float theta = phase - omega * (float)elapsedSeconds;
            return new float3(math.sin(theta), 0f, -math.cos(theta)) * speed;
        }

        /// <summary>
        /// Moon world position on the map tile nearest <paramref name="nearPosition"/>.
        /// Unwraps the planet first, then applies orbit offset (matches gem-moon visuals and toroidal display).
        /// </summary>
        public static float3 GetMoonWorldPositionNear(
            float3 nearPosition,
            float3 planetPosition,
            float planetSize,
            int planetLevel,
            int planetId,
            double elapsedSeconds,
            float mapW,
            float mapH)
        {
            float3 planetNear = nearPosition + ToroidalMapEcs.ShortestOffsetXZ(nearPosition, planetPosition, mapW, mapH);
            planetNear.y = 0f;
            float phase = GetShipOrbitPhaseOffset(planetId);
            float3 moon = planetNear + GetShipOrbitRingOffset(planetSize, planetLevel, phase, elapsedSeconds);
            moon.y = 0f;
            return moon;
        }

        /// <summary>
        /// Align-rate scale. [LEGACY] Same as Starship.GetOrbitGravityFactor — stronger on large
        /// planets and closer (inner) orbits.
        /// </summary>
        public static float GetGravityFactor(float planetSize, float radius, float innerWorld, float outerWorld, float centerWorld)
        {
            _ = centerWorld;
            float clampedRadius = math.clamp(radius, innerWorld, outerWorld);
            float radiusFactor = math.saturate(math.unlerp(outerWorld, innerWorld, clampedRadius));

            const float minSize = 9f;
            const float maxSize = 18f;
            float sizeNorm = math.clamp((planetSize - minSize) / (maxSize - minSize), 0f, 1f);
            return 1f + 0.7f * sizeNorm + 1.0f * radiusFactor;
        }

        /// <summary>
        /// Builds desired tangential velocity and alignment rate for the passive ship orbit motor
        /// when the ship pivot is inside a planet orbit ring. Called from shared
        /// <see cref="TitanOrbit.ECS.ShipPhysicsDriveLogic"/> before Unity Physics integrates position.
        /// Desired velocity is tangential only. Radius is held by
        /// <see cref="StabilizeOrbitRail"/> after the solver, so a radial speed target cannot
        /// shove the hull out of the ring. Thrust still cancels this motor entirely.
        /// </summary>
        /// <param name="shipPos">Ship world position (canonical wrap; still use toroidal range).</param>
        /// <param name="planetPos">Planet logical world position.</param>
        /// <param name="planetSize">Planet uniform scale (world radius proxy).</param>
        /// <param name="planetLevel">Planet level (ring radii currently ignore level; kept for API stability).</param>
        /// <param name="shipMass">Movement mass — heavier ships settle into orbit slower.</param>
        /// <param name="mapWidth">Toroidal map width from <c>MapStateSingleton</c>.</param>
        /// <param name="mapHeight">Toroidal map height from <c>MapStateSingleton</c>.</param>
        /// <param name="desiredVelocity">Clockwise tangential velocity at <see cref="GetOrbitRingSpeed"/>. No radial component.</param>
        /// <param name="alignRate">Lerp rate toward desired velocity (1/s), scaled by gravity, edge capture, and 1/sqrt(mass).</param>
        public static void BuildOrbitMotorParams(
            float3 shipPos,
            float3 planetPos,
            float planetSize,
            int planetLevel,
            float shipMass,
            float mapWidth,
            float mapHeight,
            out float3 desiredVelocity,
            out float alignRate)
        {
            desiredVelocity = float3.zero;
            alignRate = 0f;

            // --- Toroidal distance into the annulus ---
            // [TITAN-ORBIT] Never use Euclidean distance here — a planet on +X still owns a ship on −X.
            float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetPos, mapWidth, mapHeight);
            if (dist < 0.01f)
                return;

            GetRingRadiiWorld(planetSize, planetLevel, out float innerWorld, out float outerWorld, out float centerWorld);
            if (!IsInOrbitRing(dist, innerWorld, outerWorld))
                return;

            // --- Clockwise tangent from shortest planet→ship offset ---
            // [TITAN-ORBIT] ShortestOffsetXZ(planet, ship) is the radial vector on the torus.
            float3 toShip = ToroidalMapEcs.ShortestOffsetXZ(planetPos, shipPos, mapWidth, mapHeight);
            float3 radial = math.normalize(new float3(toShip.x, 0f, toShip.z));
            // Perpendicular on XZ: (x,z) → (z, -x) is clockwise when looking down +Y.
            float3 tangent = new float3(radial.z, 0f, -radial.x);

            // --- Same ring speed as the gem moon on this planet ---
            // [TITAN-ORBIT] GetOrbitRingSpeed is the single source of truth for this ring radius.
            // Do not scale by position-in-band or territory — that made ships drift vs the moon.
            float targetSpeed = GetOrbitRingSpeed(planetSize);

            desiredVelocity = tangent * targetSpeed;

            // --- Align rate: gravity / mass, then extra capture at the lips ---
            // GetGravityFactor is still stronger on large planets and inner orbits (legacy curve).
            // Edge capture is separate so the outer lip matches tangential speed quickly.
            // Radius itself is not a velocity — see StabilizeOrbitRail.
            float halfThickness = math.max(0.01f, (outerWorld - innerWorld) * 0.5f);
            float edgeT = math.saturate(math.abs(dist - centerWorld) / halfThickness);
            float gravityFactor = GetGravityFactor(planetSize, dist, innerWorld, outerWorld, centerWorld);
            float massFactor = math.sqrt(math.max(0.5f, shipMass));
            float captureScale = math.lerp(1f, OrbitEdgeCaptureMultiplier, edgeT);
            alignRate = (OrbitCaptureResponsiveness * gravityFactor * captureScale) / massFactor;
        }

        /// <summary>
        /// Drop the planet-radial part of <paramref name="planarVelocity"/> so leftover
        /// inbound or outbound speed cannot carry a coasting hull out of the ring.
        /// Tangential velocity is unchanged. Y is left to the caller.
        /// </summary>
        public static void RemoveOrbitRadialVelocity(
            ref float3 planarVelocity,
            float3 shipPos,
            float3 planetPos,
            float mapWidth,
            float mapHeight)
        {
            if (!TryOrbitRadial(shipPos, planetPos, mapWidth, mapHeight, out float3 radial, out _))
                return;

            float radialSpeed = math.dot(planarVelocity, radial);
            planarVelocity -= radial * radialSpeed;
            planarVelocity.y = 0f;
        }

        /// <summary>
        /// Keep a coasting hull on the orbit centerline. Inside the annulus the pivot
        /// eases inward or outward without crossing the centerline. A solver shove that
        /// lands outside the ring is placed back on the centerline before presentation,
        /// so the hull does not fly out and then snap back.
        /// </summary>
        public static void StabilizeOrbitRail(
            ref float3 shipPos,
            ref float3 planarVelocity,
            float3 planetPos,
            float planetSize,
            int planetLevel,
            float mapWidth,
            float mapHeight,
            float dt)
        {
            RemoveOrbitRadialVelocity(ref planarVelocity, shipPos, planetPos, mapWidth, mapHeight);
            if (!TryOrbitRadial(shipPos, planetPos, mapWidth, mapHeight, out float3 radial, out float dist))
                return;

            GetRingRadiiWorld(planetSize, planetLevel, out float innerWorld, out float outerWorld, out float centerWorld);
            float newDist;
            if (dist < innerWorld || dist > outerWorld)
            {
                // Solver shove left the annulus. Put the pivot back on the centerline
                // before presentation so the outward pop is not shown and the next
                // tick has nothing to snap back from.
                newDist = centerWorld;
            }
            else
            {
                float error = dist - centerWorld;
                float step = math.min(math.abs(error), OrbitCenterlineEaseSpeed * math.max(0f, dt));
                step = math.min(step, OrbitCenterlineEaseMaxStep);
                newDist = dist - math.sign(error) * step;
            }

            if (math.abs(newDist - dist) < 1e-5f)
                return;

            float3 newPos = planetPos + radial * newDist;
            newPos.y = 0f;
            if (ToroidalMapEcs.IsValidMapSize(mapWidth, mapHeight))
                newPos = ToroidalMapEcs.Wrap(newPos, mapWidth, mapHeight);
            shipPos = newPos;
        }

        static bool TryOrbitRadial(
            float3 shipPos,
            float3 planetPos,
            float mapWidth,
            float mapHeight,
            out float3 radial,
            out float dist)
        {
            radial = new float3(0f, 0f, 1f);
            dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetPos, mapWidth, mapHeight);
            if (dist < 0.01f)
                return false;

            float3 toShip = ToroidalMapEcs.ShortestOffsetXZ(planetPos, shipPos, mapWidth, mapHeight);
            radial = math.normalize(new float3(toShip.x, 0f, toShip.z));
            return math.lengthsq(radial) > 1e-8f;
        }

        /// <summary>
        /// True when the hull has actually captured the ring — velocity is close to the
        /// motor's desired orbit velocity, not merely coasting through the annulus.
        /// Hysteresis stops the lock from flickering while the radial spring trims.
        /// </summary>
        /// <param name="useOrbit">Passive orbit motor is running this tick (in ring, no thrust).</param>
        /// <param name="velocity">Current planar ship velocity after the orbit lerp.</param>
        /// <param name="desiredVelocity">Target from <see cref="BuildOrbitMotorParams"/>.</param>
        /// <param name="wasLocked">Previous tick's lock (for hysteresis).</param>
        public static bool EvaluatePositiveOrbitLock(
            bool useOrbit,
            float3 velocity,
            float3 desiredVelocity,
            bool wasLocked)
        {
            if (!useOrbit)
                return false;

            float desiredSpeed = math.length(desiredVelocity);
            if (desiredSpeed < 0.05f)
                return false;

            float err = math.length(velocity - desiredVelocity);
            // Acquire is tighter than release so a ship still dumping inbound speed
            // does not light the ring, but a captured hull stays locked through small spring trims.
            float acquire = math.max(0.12f, desiredSpeed * 0.18f);
            float release = math.max(0.22f, desiredSpeed * 0.38f);
            return wasLocked ? err <= release : err <= acquire;
        }
    }
}
