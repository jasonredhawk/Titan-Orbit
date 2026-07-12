using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Planet orbit ring geometry for gem-moon visuals and decorative level bands.
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
        /// <summary>Radial pull strength when ship drifts off orbit ring centerline.</summary>
        const float OrbitRadiusPullStrength = 2.5f;
        /// <summary>How quickly ship velocity aligns to tangential orbit speed.</summary>
        const float OrbitCaptureResponsiveness = 3.5f;
        /// <summary>Base tangential speed factor before planet size and radius modifiers.</summary>
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
        /// Tangential orbit speed at <paramref name="radius"/>. Larger planets and centerline radius
        /// increase speed; edge of band slows slightly for readable capture feel.
        /// </summary>
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

        /// <summary>Stronger pull near ring center and on large planets — used for align rate scaling.</summary>
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
    }
}
