using Shapes;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Shared ring/orbit mesh geometry and planet orbit-ring drawing helpers (Shapes immediate mode).</summary>
    internal static class PlanetRingMeshBuilder
    {
        /// <summary>
        /// Soft fill ring count for orbit / moon zones.
        /// [TITAN-ORBIT] Was 32 — dozens of ImmediateMode drawers × 32 rings dominated Render Shapes.
        /// 12 keeps a readable soft falloff at far less cost.
        /// </summary>
        internal const int OrbitRingShapeGradientSteps = 12;
        const int MinSubRingsPerLevelBand = 2;
        const int MaxSubRingsPerLevelBand = 5;

        internal static void DrawSaturnStyleLevelBands(
            float innerRadius, float bandThickness, float bandGap, int bandCount,
            Color baseColor, float baseAlpha, int visualSeed)
        {
            float currentCenter = innerRadius;
            for (int band = 0; band < bandCount; band++)
            {
                DrawSaturnStyleLevelBand(baseColor, baseAlpha, currentCenter, bandThickness, band, visualSeed);
                currentCenter += bandThickness + bandGap;
            }
        }

        static void DrawSaturnStyleLevelBand(
            Color baseColor, float baseAlpha, float centerRadius, float bandThickness, int bandIndex, int visualSeed)
        {
            int seed = visualSeed * 997 + bandIndex * 131;
            float bandInner = centerRadius - bandThickness * 0.5f;
            float bandOuter = centerRadius + bandThickness * 0.5f;
            float radialSpan = bandOuter - bandInner;
            if (radialSpan < 0.001f) return;

            float edgeThickness = Mathf.Max(0.0016f, bandThickness * 0.1f);
            Color edgeColor = WithAlpha(baseColor, baseAlpha * 0.72f);
            Draw.Ring(Vector3.zero, Quaternion.identity, bandInner + edgeThickness * 0.5f, edgeThickness, edgeColor);
            Draw.Ring(Vector3.zero, Quaternion.identity, bandOuter - edgeThickness * 0.5f, edgeThickness, edgeColor);

            Color bandFillInner = WithAlpha(baseColor, baseAlpha * 0.14f);
            Color bandFillOuter = WithAlpha(baseColor, baseAlpha * 0.22f);
            Draw.Ring(Vector3.zero, Quaternion.identity, centerRadius, bandThickness * 0.88f,
                DiscColors.Radial(bandFillInner, bandFillOuter));

            int lineCount = MinSubRingsPerLevelBand +
                (int)(RingHash(seed, 1) * (MaxSubRingsPerLevelBand - MinSubRingsPerLevelBand + 0.999f));

            const float thinMin = 0.0008f;
            float thinMax = bandThickness * 0.12f;
            float inset = edgeThickness * 1.2f;
            float detailInner = bandInner + inset;
            float detailOuter = bandOuter - inset;
            float detailSpan = detailOuter - detailInner;
            if (detailSpan < 0.001f)
            {
                detailInner = bandInner;
                detailOuter = bandOuter;
                detailSpan = radialSpan;
            }

            for (int s = 0; s < lineCount; s++)
            {
                float radialPos = detailInner + RingHash(seed, s + 10) * detailSpan;
                float thinRoll = RingHash(seed, s + 82);
                float thinThickness = Mathf.Lerp(thinMin, thinMax, thinRoll);
                float widthRoll = RingHash(seed, s + 80);
                bool isWide = widthRoll > 0.45f;
                float subThickness = isWide
                    ? thinThickness * Mathf.Lerp(1.15f, 3f, RingHash(seed, s + 81))
                    : thinThickness;
                float alphaRoll = RingHash(seed, s + 120);
                float subAlpha = isWide
                    ? baseAlpha * Mathf.Lerp(0.22f, 0.48f, alphaRoll)
                    : baseAlpha * Mathf.Lerp(0.32f, 0.68f, alphaRoll);
                float brightRoll = RingHash(seed, s + 160);
                float brightness = Mathf.Lerp(0.78f, 1.18f, brightRoll);
                Color tinted = ScaleRgb(baseColor, brightness);

                if (isWide)
                {
                    Color edge = WithAlpha(tinted, subAlpha * 0.25f);
                    Color core = WithAlpha(tinted, subAlpha * 0.75f);
                    Draw.Ring(Vector3.zero, Quaternion.identity, radialPos, subThickness, DiscColors.Radial(core, edge));
                }
                else
                {
                    Color streakBright = WithAlpha(ScaleRgb(tinted, 1.05f), subAlpha);
                    Color streakDim = WithAlpha(ScaleRgb(tinted, 0.72f), subAlpha * Mathf.Lerp(0.45f, 0.85f, RingHash(seed, s + 200)));
                    float angularOffset = RingHash(seed, s + 280) * 360f;
                    Draw.Ring(Vector3.zero, Quaternion.Euler(0f, 0f, angularOffset), radialPos, subThickness,
                        DiscColors.Angular(streakBright, streakDim));
                }
            }
        }

        static float RingHash(int seed, int index)
        {
            float n = seed * 0.1031f + index * 0.0973f;
            return Mathf.Repeat(Mathf.Sin(n) * 43758.5453123f, 1f);
        }

        static Color WithAlpha(Color c, float alpha) => new Color(c.r, c.g, c.b, alpha);

        static Color ScaleRgb(Color c, float scale) =>
            new Color(Mathf.Clamp01(c.r * scale), Mathf.Clamp01(c.g * scale), Mathf.Clamp01(c.b * scale), c.a);

        internal static void DrawShapesOrbitRing(Camera cam, Matrix4x4 matrix, float inner, float outer, Color tint, float peakAlpha)
        {
            // --- DrawShapesOrbitRing ---
            float band = outer - inner;
            if (band < 0.001f) return;
            float step = band / OrbitRingShapeGradientSteps;
            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = matrix;
                for (int i = 0; i < OrbitRingShapeGradientSteps; i++)
                {
                    float r0 = inner + i * step;
                    float r1 = r0 + step;
                    float mid = (r0 + r1) * 0.5f;
                    float t = (mid - inner) / band;
                    float a = peakAlpha * Mathf.Sin(t * Mathf.PI);
                    float center = (r0 + r1) * 0.5f;
                    Draw.Ring(Vector3.zero, Quaternion.identity, center, step, new Color(tint.r, tint.g, tint.b, a));
                }
            }
        }
    }
}
