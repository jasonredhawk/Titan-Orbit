using Shapes;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Soft deposit/pad disc for one planetary-defense slot.
    /// Tint and peak alpha match the people-transfer orbit ring fill and gem-moon orbit zone
    /// (<see cref="PlanetOrbitRingVisual"/> / <see cref="GemMoonOrbitZoneVisual"/>): cool blue,
    /// translucent, fading out toward the outer rim.
    /// Drawn with Shapes immediate mode (no opaque mesh). Radius is set by
    /// <see cref="PlanetaryDefenseVisualDriver"/> each frame in planet-local units.
    /// </summary>
    [ExecuteAlways]
    public sealed class PlanetaryDefensePadZoneVisual : ImmediateModeShapeDrawer
    {
        /// <summary>Child name under each defense slot root.</summary>
        public const string ZoneObjectName = "PadZone";

        /// <summary>
        /// Shared cool-blue fill used by planet orbit rings and moon orbit zones.
        /// Keep in sync with <see cref="PlanetOrbitRingVisual"/> / <see cref="GemMoonOrbitZoneVisual"/>.
        /// </summary>
        public static readonly Color OrbitZoneTint = new Color(0.5f, 0.7f, 0.95f);

        /// <summary>Peak fill alpha matching orbit-zone soft discs (~0.3).</summary>
        public const float OrbitZonePeakAlpha = 0.3f;

        [Header("Pad Zone Fill")]
        [SerializeField] bool drawPadZone = true;

        /// <summary>Soft blue tint — same as orbit ring / moon orbit zone fills.</summary>
        [SerializeField] Color zoneTint = new Color(0.5f, 0.7f, 0.95f);

        /// <summary>Peak alpha near the mid-core before the outer-edge fade.</summary>
        [Range(0f, 1f)]
        [SerializeField] float peakAlpha = OrbitZonePeakAlpha;

        /// <summary>
        /// Fraction of radius that stays near peak alpha before fading to transparent.
        /// 0.45 ≈ soft core then falloff (readable pad without a hard rim).
        /// </summary>
        [Range(0.1f, 0.95f)]
        [SerializeField] float solidCoreFraction = 0.45f;

        /// <summary>
        /// Outer radius in slot-local units. Under unit-scale planet roots this equals world radius.
        /// Set by <see cref="PlanetaryDefenseVisualDriver"/>.
        /// </summary>
        float _radiusLocal = 1f;

        /// <summary>Creates or reuses the zone drawer under <paramref name="slotRoot"/>.</summary>
        public static PlanetaryDefensePadZoneVisual EnsureOnSlotRoot(Transform slotRoot)
        {
            if (slotRoot == null)
                return null;

            Transform existing = slotRoot.Find(ZoneObjectName);
            GameObject zoneGo;
            if (existing != null)
            {
                zoneGo = existing.gameObject;
            }
            else
            {
                zoneGo = new GameObject(ZoneObjectName);
                zoneGo.transform.SetParent(slotRoot, false);
                zoneGo.transform.localPosition = Vector3.zero;
                zoneGo.transform.localRotation = Quaternion.identity;
                zoneGo.transform.localScale = Vector3.one;
            }

            var visual = zoneGo.GetComponent<PlanetaryDefensePadZoneVisual>();
            if (visual == null)
                visual = zoneGo.AddComponent<PlanetaryDefensePadZoneVisual>();

            // Runtime pads always adopt the shared orbit-zone look (Inspector defaults only
            // apply on first AddComponent; older white/low-alpha instances need a refresh).
            visual.ApplyOrbitZoneAppearance();
            return visual;
        }

        /// <summary>
        /// Forces tint + peak alpha to the shared orbit-zone values used by planet rings and moons.
        /// </summary>
        public void ApplyOrbitZoneAppearance()
        {
            zoneTint = OrbitZoneTint;
            peakAlpha = OrbitZonePeakAlpha;
        }

        /// <summary>
        /// Sets the disc radius in <b>slot-local</b> units (world units under a unit-scale planet root).
        /// </summary>
        public void SetRadiusLocal(float radiusLocal)
        {
            _radiusLocal = Mathf.Max(0.05f, radiusLocal);
        }

        /// <summary>
        /// [HYBRID] Shapes draw pass — soft blue disc on XZ that fades to clear at the outer edge.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            if (!drawPadZone || _radiusLocal <= 0.001f)
                return;

            // Flat on the flight plane (same orientation as GemMoonOrbitZoneVisual).
            Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
            Matrix4x4 zoneMatrix = transform.localToWorldMatrix * Matrix4x4.Rotate(flatXZ);

            DrawSoftDisc(cam, zoneMatrix, _radiusLocal, zoneTint, peakAlpha, solidCoreFraction);
        }

        /// <summary>
        /// Concentric ring steps from center → rim with alpha that holds through the core then
        /// falls to 0 at the outer edge (same stepped style as <see cref="PlanetRingMeshBuilder.DrawShapesOrbitRing"/>).
        /// </summary>
        static void DrawSoftDisc(
            Camera cam,
            Matrix4x4 matrix,
            float outerRadius,
            Color tint,
            float peakAlpha,
            float solidCoreFraction)
        {
            const int steps = 24;
            float step = outerRadius / steps;
            float coreEnd = Mathf.Clamp01(solidCoreFraction);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.BlendMode = ShapesBlendMode.Transparent;
                Draw.Matrix = matrix;

                for (int i = 0; i < steps; i++)
                {
                    float r0 = i * step;
                    float r1 = r0 + step;
                    float mid = (r0 + r1) * 0.5f;
                    float t = mid / outerRadius; // 0 at center → 1 at rim

                    float a;
                    if (t <= coreEnd)
                    {
                        // Soft rise from a dim center so the turret mesh stays readable.
                        float coreT = coreEnd > 0.001f ? t / coreEnd : 1f;
                        a = peakAlpha * Mathf.Lerp(0.55f, 1f, coreT);
                    }
                    else
                    {
                        // Fade to transparent on the outer edge.
                        float fadeT = (t - coreEnd) / Mathf.Max(0.001f, 1f - coreEnd);
                        a = peakAlpha * (1f - fadeT);
                    }

                    if (a < 0.002f)
                        continue;

                    float center = (r0 + r1) * 0.5f;
                    Draw.Ring(
                        Vector3.zero,
                        Quaternion.identity,
                        center,
                        step,
                        new Color(tint.r, tint.g, tint.b, a));
                }
            }
        }
    }
}
