using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>Planet orbit ring geometry and motor helpers ported from legacy Planet/Starship.</summary>
    public static class PlanetOrbitMath
    {
        public const float LevelBandsTiltDegrees = -26.7f;
        public const float LevelBandsInnerRadiusLocal = 0.68f;
        public const float LevelBandThicknessLocal = 0.06f;
        public const float LevelBandGapLocal = 0.022f;

        const float OrbitRingHalfThicknessLocal = 0.11f * 0.7f;
        /// <summary>Gap between the outermost level band and the inner edge of the ship orbit ring.</summary>
        const float OrbitRingClearanceFromLevelBandsLocal = LevelBandGapLocal * 2f;
        const float OrbitRadiusPullStrength = 2.5f;
        const float OrbitCaptureResponsiveness = 3.5f;
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
            // Orbit ring size is fixed: always sits outside where all MaxPlanetLevel decorative bands would reach.
            _ = planetLevel;
            float centerLocal = GetOrbitRingCenterRadiusLocal();
            float innerLocal = math.max(0.52f, centerLocal - OrbitRingHalfThicknessLocal);
            float outerLocal = centerLocal + OrbitRingHalfThicknessLocal;
            centerWorld = planetSize * centerLocal;
            innerWorld = planetSize * innerLocal;
            outerWorld = planetSize * outerLocal;
        }

        public static bool IsInOrbitRing(float dist, float innerWorld, float outerWorld)
        {
            return dist >= innerWorld && dist <= outerWorld;
        }

        public static float GetTargetSpeed(float planetSize, float radius, float innerWorld, float outerWorld, float centerWorld)
        {
            float clampedRadius = math.clamp(radius, innerWorld, outerWorld);
            float halfBand = math.max(0.001f, (outerWorld - innerWorld) * 0.5f);
            float radiusFactor = 1f - math.abs(clampedRadius - centerWorld) / halfBand;
            radiusFactor = math.clamp(radiusFactor, 0f, 1f);

            const float minSize = 9f;
            const float maxSize = 18f;
            float sizeNorm = math.clamp((planetSize - minSize) / (maxSize - minSize), 0f, 1f);
            float sizeMultiplier = math.lerp(0.8f, 1.4f, sizeNorm);
            float radiusMultiplier = math.lerp(0.7f, 1.6f, radiusFactor);
            return BaseOrbitSpeed * sizeMultiplier * radiusMultiplier;
        }

        /// <summary>World-space offset for a body orbiting at the ship orbit ring center (clockwise, matching the ship motor).</summary>
        public static float3 GetShipOrbitRingOffset(
            float planetSize,
            int planetLevel,
            float phaseOffsetRadians,
            double elapsedSeconds)
        {
            GetRingRadiiWorld(planetSize, planetLevel, out float innerWorld, out float outerWorld, out float centerWorld);
            float speed = GetTargetSpeed(planetSize, centerWorld, innerWorld, outerWorld, centerWorld);
            float omega = centerWorld > 0.001f ? speed / centerWorld : 0f;
            float theta = phaseOffsetRadians - omega * (float)elapsedSeconds;
            return new float3(math.cos(theta), 0f, math.sin(theta)) * centerWorld;
        }

        public static float GetShipOrbitPhaseOffset(int planetId)
        {
            uint seed = planetId != 0 ? (uint)planetId : 17u;
            return (seed % 6283u) * 0.001f;
        }

        public static float3 GetMoonWorldPosition(
            float3 planetPosition,
            float planetSize,
            int planetLevel,
            int planetId,
            double elapsedSeconds,
            bool isHomePlanet = false)
        {
            float phase = GetShipOrbitPhaseOffset(planetId);
            return planetPosition + GetShipOrbitRingOffset(planetSize, planetLevel, phase, elapsedSeconds);
        }

        public static float GetGravityFactor(float planetSize, float radius, float innerWorld, float outerWorld, float centerWorld)
        {
            float clampedRadius = math.clamp(radius, innerWorld, outerWorld);
            float halfBand = math.max(0.001f, (outerWorld - innerWorld) * 0.5f);
            float radiusFactor = 1f - math.abs(clampedRadius - centerWorld) / halfBand;
            radiusFactor = math.clamp(radiusFactor, 0f, 1f);

            const float minSize = 9f;
            const float maxSize = 18f;
            float sizeNorm = math.clamp((planetSize - minSize) / (maxSize - minSize), 0f, 1f);
            return 1f + 0.7f * sizeNorm + 1.0f * radiusFactor;
        }

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

            float dist = Generation.ToroidalMapEcs.ToroidalDistance(shipPos, planetPos, mapWidth, mapHeight);
            if (dist < 0.01f)
                return;

            GetRingRadiiWorld(planetSize, planetLevel, out float innerWorld, out float outerWorld, out float centerWorld);
            if (!IsInOrbitRing(dist, innerWorld, outerWorld))
                return;

            float3 toShip = Generation.ToroidalMapEcs.ShortestOffsetXZ(planetPos, shipPos, mapWidth, mapHeight);
            float3 radial = math.normalize(new float3(toShip.x, 0f, toShip.z));
            float3 tangent = new float3(radial.z, 0f, -radial.x);

            float targetSpeed = GetTargetSpeed(planetSize, dist, innerWorld, outerWorld, centerWorld);
            float radiusError = dist - centerWorld;
            float3 radialCorrection = float3.zero;
            if (math.abs(radiusError) > 0.02f)
                radialCorrection = -radial * radiusError * OrbitRadiusPullStrength;

            desiredVelocity = tangent * targetSpeed + radialCorrection;

            float gravityFactor = GetGravityFactor(planetSize, dist, innerWorld, outerWorld, centerWorld);
            float massFactor = math.sqrt(math.max(0.5f, shipMass));
            alignRate = (OrbitCaptureResponsiveness * gravityFactor) / massFactor;
        }
    }
}
