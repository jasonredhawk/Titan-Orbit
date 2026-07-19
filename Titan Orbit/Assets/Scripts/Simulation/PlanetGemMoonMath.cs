using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// [TITAN-ORBIT] Shared math for gem moons orbiting planets — visual scale, dock/shield radii, orbit offset,
    /// and combat constants (shield regen, gem drain, enemy repel). Used by server
    /// <c>PlanetGemMoonCombatLogic</c>, client moon proxies, and map generation placement. Pure static helpers —
    /// no ECS or Unity lifecycle.
    /// </summary>
    public static class PlanetGemMoonMath
    {
        /// <summary>Reference planet diameter for inverse-size moon scaling curve.</summary>
        const float GemMoonReferencePlanetSize = 20f;
        const float GemMoonInversePlanetSizeCap = 10f;
        const float GemMoonDockOrbitZoneRadiusOverBody = 1.95f * 1.2f;
        public const float BaseMaxShieldPoints = 250f;
        public const float ShieldBarrierRadiusOverDock = 1.06f;
        public const float MatrixShieldOrbitZoneEdgeExpandMultiplier = 1.35f;
        public const float MatrixShieldRadiusReference = 5.5f;
        public const float ShieldRegenDelaySeconds = 1.5f;
        public const float ShieldRegenSecondsToFull = 30f;
        public const float BaseMaxMoonGemPoints = 500f;
        public const float GemDrainPerSecondWhenShieldDown = 20f;
        public const float GemSpawnInterval = 0.25f;
        public const float GemSpawnMinValue = 2f;
        public const float EnemyShieldRepelMinSpeed = 8f;
        public const float EnemyShieldRepelMaxSpeed = 22f;

        /// <summary>[TITAN-ORBIT] Max shield HP scales linearly with planet level.</summary>
        public static float GetMaxShieldForLevel(int planetLevel) =>
            math.max(0.001f, BaseMaxShieldPoints * math.max(1, planetLevel));

        /// <summary>
        /// Uniform local scale for moon mesh — larger on small planets (inverse size cap), boosted for homeworld.
        /// </summary>
        public static float ComputeVisualUniformScale(float planetSize, float homeScaleMultiplier = 1f)
        {
            // --- Compute value ---
            planetSize = Mathf.Max(0.01f, planetSize);
            float baseAtRef = Mathf.Clamp(GemMoonReferencePlanetSize * 0.0035f, 0.02f, 0.1f) * 2.5f;
            float inv = GemMoonReferencePlanetSize / planetSize;
            inv = Mathf.Min(inv, GemMoonInversePlanetSizeCap);
            return Mathf.Clamp(baseAtRef * inv * Mathf.Max(0.01f, homeScaleMultiplier), 0.02f, 1.25f);
        }

        public static float GetRingsOuterEdgeRadiusLocal(int level) =>
            PlanetOrbitMath.GetLevelBandsOuterRadiusLocal(level);

        public static float EstimateOrbitRadiusWorld(float planetSize, int planetLevel, float homeScaleMultiplier = 1f)
        {
            // --- EstimateOrbitRadiusWorld ---
            const float moonOrbitOutsideFactor = 1.1f;
            const float clearanceMarginWorld = 0.4f;

            planetSize = Mathf.Max(0.01f, planetSize);
            _ = planetLevel;
            PlanetOrbitMath.GetRingRadiiWorld(planetSize, 1, out _, out _, out float centerWorld);
            float rNominal = centerWorld * Mathf.Max(1.01f, moonOrbitOutsideFactor);

            float gemMoonUniformScale = ComputeVisualUniformScale(planetSize, homeScaleMultiplier);
            float bodyLocalRadius = 0.5f * gemMoonUniformScale;
            float dockLocalRadius = bodyLocalRadius * GemMoonDockOrbitZoneRadiusOverBody;
            float moonDock = dockLocalRadius * planetSize;

            float ringsOuter = planetSize * GetRingsOuterEdgeRadiusLocal(PlanetEconomyMath.MaxPlanetLevel);
            float rClear = ringsOuter + moonDock + clearanceMarginWorld;
            return Mathf.Max(rNominal, rClear);
        }

        /// <summary>World-space radius used to keep map spawns outside a planet's orbit ring.</summary>
        public static float ComputeMapPlacementInfluenceRadiusWorld(float planetSize, int planetLevel, float homeScaleMultiplier = 1f)
        {
            // --- Compute value ---
            const float orbitRingHalfThicknessLocal = 0.055f;
            float moonOrbitWorld = EstimateOrbitRadiusWorld(planetSize, planetLevel, homeScaleMultiplier);
            return moonOrbitWorld + Mathf.Max(0.01f, planetSize) * orbitRingHalfThicknessLocal;
        }

        public static float GetMoonDockRadiusWorld(float planetSize, bool isHomePlanet)
        {
            // --- Compute value ---
            float homeMul = isHomePlanet ? 1.5f : 1f;
            float uniform = ComputeVisualUniformScale(Mathf.Max(0.01f, planetSize), homeMul);
            float bodyLocalRadius = 0.5f * uniform;
            float dockLocalRadius = bodyLocalRadius * GemMoonDockOrbitZoneRadiusOverBody;
            return dockLocalRadius * Mathf.Max(0.01f, planetSize);
        }

        public static float GetMoonBodyRadiusWorld(float planetSize, bool isHomePlanet)
        {
            // --- Compute value ---
            float homeMul = isHomePlanet ? 1.5f : 1f;
            float uniform = ComputeVisualUniformScale(Mathf.Max(0.01f, planetSize), homeMul);
            return 0.5f * uniform * Mathf.Max(0.01f, planetSize);
        }

        /// <summary>Moon body radius in moon-root local space (collider / visual mesh space).</summary>
        public static float GetMoonBodyRadiusLocal(float planetSize, bool isHomePlanet)
        {
            float homeMul = isHomePlanet ? 1.5f : 1f;
            return 0.5f * ComputeVisualUniformScale(Mathf.Max(0.01f, planetSize), homeMul);
        }

        /// <summary>Dock / orbit-zone outer radius in moon-root local space.</summary>
        public static float GetMoonDockSnapRadiusLocal(float planetSize, bool isHomePlanet) =>
            GetMoonBodyRadiusLocal(planetSize, isHomePlanet) * GemMoonDockOrbitZoneRadiusOverBody;

        /// <summary>Shared outer shell radius for moon orbit-zone fill and matrix-shield VFX.</summary>
        public static float GetMoonVisualShellOuterRadiusLocal(float planetSize, bool isHomePlanet) =>
            GetMoonShieldOuterRadiusLocal(planetSize, isHomePlanet);

        /// <summary>Outer shield barrier radius in moon-root local space (gameplay repulsion trigger).</summary>
        public static float GetMoonShieldOuterRadiusLocal(float planetSize, bool isHomePlanet) =>
            GetMoonDockSnapRadiusLocal(planetSize, isHomePlanet) * math.max(1f, ShieldBarrierRadiusOverDock);

        /// <summary>World-space outer shield barrier (bullet hits + enemy ship repulsion when shield is up).</summary>
        public static float GetMoonShieldOuterRadiusWorld(float planetSize, bool isHomePlanet) =>
            GetMoonShieldOuterRadiusLocal(planetSize, isHomePlanet) * math.max(0.01f, planetSize);

        /// <summary>World-space bullet hit radius: shield shell when up, body when down.</summary>
        public static float GetMoonBulletHitRadiusWorld(float planetSize, bool isHomePlanet, float currentShield) =>
            currentShield > 0.001f
                ? GetMoonShieldOuterRadiusWorld(planetSize, isHomePlanet)
                : GetMoonBodyRadiusWorld(planetSize, isHomePlanet);

        public static float GetMoonSurfaceLandingRangeWorld(float planetSize, bool isHomePlanet, float shipRadiusEstimate = 0.8f)
        {
            // --- Compute value ---
            float moonRadius = GetMoonBodyRadiusWorld(planetSize, isHomePlanet);
            const float surfaceStandoffOverMoonRadius = 0.08f;
            return moonRadius + shipRadiusEstimate + moonRadius * surfaceStandoffOverMoonRadius;
        }

        /// <summary>
        /// Gameplay moon orbit / dock shell (legacy GetMoonDockSnapRadiusWorld × ship radius).
        /// Ships inside this zone can begin landing even while the moon orbits.
        /// </summary>
        public static float GetMoonDockZoneRadiusWorld(
            float planetSize,
            bool isHomePlanet,
            float shipRadiusEstimate = 0.8f,
            float zoneMultiplier = 1.05f)
        {
            float zoneRadius = GetMoonDockRadiusWorld(planetSize, isHomePlanet);
            return zoneRadius * math.max(0.01f, zoneMultiplier) + math.max(0f, shipRadiusEstimate);
        }

        /// <summary>
        /// World-space offset for the gem moon on the planet orbit ring (same radius as people-transfer ring).
        /// <paramref name="elapsedSeconds"/> must be the shared ServerTick orbit clock
        /// (<c>PlanetGemMoonOrbitClock</c>), not per-world <c>ElapsedTime</c>.
        /// </summary>
        public static float3 GetMoonOrbitOffset(
            float planetSize,
            int planetLevel,
            bool isHomePlanet,
            int planetId,
            double elapsedSeconds)
        {
            float phase = PlanetOrbitMath.GetShipOrbitPhaseOffset(planetId);
            return PlanetOrbitMath.GetShipOrbitRingOffset(planetSize, planetLevel, phase, elapsedSeconds);
        }
    }
}
