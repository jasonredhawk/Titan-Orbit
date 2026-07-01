using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>Gem-moon orbit and scale helpers ported from legacy Planet.</summary>
    public static class PlanetGemMoonMath
    {
        const float OrbitZoneBaseOuterRadiusLocal = 0.85f * 1.5f * 0.75f;
        const float OrbitRingGrowthPerLevel = 0.05f;
        const float GemMoonReferencePlanetSize = 20f;
        const float GemMoonInversePlanetSizeCap = 10f;
        const float GemMoonDockOrbitZoneRadiusOverBody = 1.95f * 1.2f;
        const float GemMoonRingsInnerRadiusLocal = 0.68f;
        const float GemMoonRingThicknessLocal = 0.06f;
        const float GemMoonRingGapLocal = 0.022f;

        public static float ComputeVisualUniformScale(float planetSize, float homeScaleMultiplier = 1f)
        {
            planetSize = Mathf.Max(0.01f, planetSize);
            float baseAtRef = Mathf.Clamp(GemMoonReferencePlanetSize * 0.0035f, 0.02f, 0.1f) * 2.5f;
            float inv = GemMoonReferencePlanetSize / planetSize;
            inv = Mathf.Min(inv, GemMoonInversePlanetSizeCap);
            return Mathf.Clamp(baseAtRef * inv * Mathf.Max(0.01f, homeScaleMultiplier), 0.02f, 1.25f);
        }

        public static float GetRingsOuterEdgeRadiusLocal(int level)
        {
            int n = Mathf.Clamp(level, 1, 6);
            float step = GemMoonRingThicknessLocal + GemMoonRingGapLocal;
            float lastCenter = GemMoonRingsInnerRadiusLocal + (n - 1) * step;
            return lastCenter + GemMoonRingThicknessLocal * 0.5f;
        }

        public static float EstimateOrbitRadiusWorld(float planetSize, int planetLevel, float homeScaleMultiplier = 1f)
        {
            const float moonOrbitOutsideFactor = 1.1f;
            const float clearanceMarginWorld = 0.4f;

            planetSize = Mathf.Max(0.01f, planetSize);
            int level = Mathf.Max(1, planetLevel);
            float moonNominalLocal = OrbitZoneBaseOuterRadiusLocal * Mathf.Pow(1f + OrbitRingGrowthPerLevel, level - 1);
            float rNominal = planetSize * moonNominalLocal * Mathf.Max(1.01f, moonOrbitOutsideFactor);

            float gemMoonUniformScale = ComputeVisualUniformScale(planetSize, homeScaleMultiplier);
            float bodyLocalRadius = 0.5f * gemMoonUniformScale;
            float dockLocalRadius = bodyLocalRadius * GemMoonDockOrbitZoneRadiusOverBody;
            float moonDock = dockLocalRadius * planetSize;

            float ringsOuter = planetSize * GetRingsOuterEdgeRadiusLocal(level);
            float rClear = ringsOuter + moonDock + clearanceMarginWorld;
            return Mathf.Max(rNominal, rClear);
        }
    }
}
