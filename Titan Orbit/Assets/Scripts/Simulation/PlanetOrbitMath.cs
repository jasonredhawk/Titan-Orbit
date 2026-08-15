using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Planet orbit ring geometry for ship passive orbit, gem-moon placement, and decorative level bands.
    /// Ring membership and <see cref="BuildOrbitMotorParams"/> use toroidal distance/offset so
    /// wraparound seams stay correct while ships fly unbounded (see titan-orbit-toroidal-map rule).
    /// <para>
    /// [TITAN-ORBIT] <see cref="GetOrbitRingSpeed"/> is the single tangential speed for a planet's
    /// ring — ships (passive motor) and gem moons (analytic offset) both use it so they co-orbit.
    /// The passive motor also applies a radial spring toward the ring centerline (stronger at the
    /// inner/outer lips) so coasting hulls stay in the zone; thrust still cancels the motor.
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
        /// <summary>
        /// Radial spring toward the orbit-ring centerline (world units/s per world-unit of radius error).
        /// [TITAN-ORBIT] Stronger than the old Starship.orbitRadiusPullStrength (2.5) so a coasting
        /// hull cannot drift through the thin annulus before the lerp captures it. Thrust still
        /// cancels the whole orbit motor — this only holds ships that are already riding the ring.
        /// </summary>
        const float OrbitRadiusPullStrength = 5f;
        /// <summary>
        /// Extra radial-spring scale at the inner/outer lips (1 at centerline, this value at the edge).
        /// Squared with normalized |radiusError| so the middle of the band stays smooth and the
        /// lips yank harder — that is what actually keeps ships from slipping out of the zone.
        /// </summary>
        const float OrbitEdgePullMultiplier = 2.25f;
        /// <summary>
        /// How quickly velocity steers toward ideal orbit (1/s before gravity / mass).
        /// [TITAN-ORBIT] Stronger than the old orbitCaptureResponsiveness (3.5) so the radial
        /// spring actually applies within a few ticks instead of leaking outward first.
        /// Still a continuous lerp (Starblast pillar 3) — not a one-frame snap onto the rail.
        /// </summary>
        const float OrbitCaptureResponsiveness = 5f;
        /// <summary>
        /// Extra align-rate scale at the inner/outer lips (1 at centerline). Dumps leftover
        /// inbound/outbound speed before the hull crosses the visual ring.
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
            return GetLevelBandsOuterRadiusLocal(PlanetEconomyMath.MaxPlanetLevel)
                + OrbitRingClearanceFromLevelBandsLocal
                + OrbitRingHalfThicknessLocal;
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

        /// <summary>True when <paramref name="dist"/> from planet center lies inside the orbit annulus.</summary>
        public static bool IsInOrbitRing(float dist, float innerWorld, float outerWorld)
        {
            return dist >= innerWorld && dist <= outerWorld;
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
        /// when the hull is inside a planet orbit ring. Called from shared
        /// <see cref="TitanOrbit.ECS.ShipPhysicsDriveLogic"/> before Unity Physics integrates position.
        /// Radial spring is stronger near the inner/outer lips so coasting ships stay in the zone;
        /// thrust still cancels this motor entirely (player can always leave).
        /// </summary>
        /// <param name="shipPos">Ship world position (may be unbounded — do not Wrap).</param>
        /// <param name="planetPos">Planet logical world position.</param>
        /// <param name="planetSize">Planet uniform scale (world radius proxy).</param>
        /// <param name="planetLevel">Planet level (ring radii currently ignore level; kept for API stability).</param>
        /// <param name="shipMass">Movement mass — heavier ships settle into orbit slower.</param>
        /// <param name="mapWidth">Toroidal map width from <c>MapStateSingleton</c>.</param>
        /// <param name="mapHeight">Toroidal map height from <c>MapStateSingleton</c>.</param>
        /// <param name="desiredVelocity">Clockwise tangential velocity plus radial spring toward ring centerline (stronger at the lips).</param>
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
            // [TITAN-ORBIT] Never use Euclidean distance here — ships fly unbounded; planets stay in
            // canonical tiles; seams must use shortest path (see titan-orbit-toroidal-map rule).
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

            // --- Radial spring toward ring centerline ---
            // [TITAN-ORBIT] desiredVelocity is a target, not an instant shove. ShipPhysicsDriveLogic
            // lerps current velocity toward this each tick (alignRate). The spring is stronger near
            // the inner/outer lips so leftover inbound speed cannot coast the hull out of the zone.
            // Tangential ring speed is unchanged — only the radial (in/out) component is corrected.
            float radiusError = dist - centerWorld;
            float halfThickness = math.max(0.01f, (outerWorld - innerWorld) * 0.5f);
            // 0 on the centerline, 1 at either lip of the visual annulus.
            float edgeT = math.saturate(math.abs(radiusError) / halfThickness);
            float edgeTSq = edgeT * edgeT;
            float pullScale = math.lerp(1f, OrbitEdgePullMultiplier, edgeTSq);

            float3 radialCorrection = float3.zero;
            if (math.abs(radiusError) > 0.02f)
                radialCorrection = -radial * radiusError * OrbitRadiusPullStrength * pullScale;

            desiredVelocity = tangent * targetSpeed + radialCorrection;

            // --- Align rate: gravity / mass, then extra capture at the lips ---
            // GetGravityFactor is still stronger on large planets and inner orbits (legacy curve).
            // Edge capture is separate so the *outer* lip — historically the weakest — also holds.
            float gravityFactor = GetGravityFactor(planetSize, dist, innerWorld, outerWorld, centerWorld);
            float massFactor = math.sqrt(math.max(0.5f, shipMass));
            float captureScale = math.lerp(1f, OrbitEdgeCaptureMultiplier, edgeT);
            alignRate = (OrbitCaptureResponsiveness * gravityFactor * captureScale) / massFactor;
        }
    }
}
