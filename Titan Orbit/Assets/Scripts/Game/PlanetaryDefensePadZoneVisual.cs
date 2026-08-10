using System.Collections.Generic;
using Shapes;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Soft deposit/pad disc for one planetary-defense slot — data + registry only.
    /// <para>
    /// Tint and peak alpha match the people-transfer orbit ring fill and gem-moon orbit zone
    /// (<see cref="PlanetOrbitRingVisual"/> / <see cref="GemMoonOrbitZoneVisual"/>): cool blue,
    /// translucent, fading out toward the outer rim.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Pads are <b>not</b> individual <see cref="ImmediateModeShapeDrawer"/>s.
    /// Profiler evidence: ~81 per-pad drawers (24 rings each) pushed Shapes drawer count
    /// ~53→134 and held Editor FPS ~25. All pads register here; one
    /// <see cref="PlanetaryDefensePadZoneBatchDrawer"/> issues a single <c>Draw.Command</c>.
    /// </para>
    /// </summary>
    public sealed class PlanetaryDefensePadZoneVisual : MonoBehaviour
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

        /// <summary>
        /// Soft-disc ring count. Was 24 per pad × ~81 pads ≈ 2k rings/frame; 8 still reads as a soft pad.
        /// </summary>
        public const int SoftDiscSteps = 8;

        /// <summary>
        /// Skip drawing pads farther than this from the camera (world units).
        /// Presentation cull only — does not affect sim / deposit gameplay.
        /// </summary>
        public const float MaxDrawDistance = 90f;

        /// <summary>Live pads for the batch drawer (OnEnable / OnDisable maintained).</summary>
        static readonly List<PlanetaryDefensePadZoneVisual> s_ActivePads =
            new List<PlanetaryDefensePadZoneVisual>(128);

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

        /// <summary>Read-only view for the batch drawer (no copy).</summary>
        internal static List<PlanetaryDefensePadZoneVisual> ActivePads => s_ActivePads;

        /// <summary>Current local/world pad radius for the batch draw.</summary>
        internal float RadiusLocal => _radiusLocal;

        /// <summary>Tint used when this pad is drawn.</summary>
        internal Color ZoneTint => zoneTint;

        /// <summary>Peak alpha used when this pad is drawn.</summary>
        internal float PeakAlpha => peakAlpha;

        /// <summary>Core fraction used when this pad is drawn.</summary>
        internal float SolidCoreFraction => solidCoreFraction;

        /// <summary>Creates or reuses the zone marker under <paramref name="slotRoot"/>.</summary>
        public static PlanetaryDefensePadZoneVisual EnsureOnSlotRoot(Transform slotRoot)
        {
            if (slotRoot == null)
                return null;

            // --- Ensure batch drawer exists (one ImmediateModeShapeDrawer for all pads) ---
            PlanetaryDefensePadZoneBatchDrawer.EnsureExists();

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

            // Legacy: older pads were ImmediateModeShapeDrawer subclasses. Destroy any leftover
            // drawer component if Unity still has a mismatched script on a recycled child.
            var legacyDrawer = zoneGo.GetComponent<ImmediateModeShapeDrawer>();
            if (legacyDrawer != null && legacyDrawer is not PlanetaryDefensePadZoneBatchDrawer)
                Object.Destroy(legacyDrawer);

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

        void OnEnable()
        {
            if (!s_ActivePads.Contains(this))
                s_ActivePads.Add(this);
        }

        void OnDisable()
        {
            s_ActivePads.Remove(this);
        }

        /// <summary>
        /// Concentric ring steps for one pad inside an already-open <see cref="Draw.Command"/>.
        /// Caller sets blend / radius space; this only sets <see cref="Draw.Matrix"/> and rings.
        /// </summary>
        internal static void DrawSoftDiscIntoOpenCommand(
            Matrix4x4 matrix,
            float outerRadius,
            Color tint,
            float peakAlpha,
            float solidCoreFraction)
        {
            const int steps = SoftDiscSteps;
            float step = outerRadius / steps;
            float coreEnd = Mathf.Clamp01(solidCoreFraction);

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

    /// <summary>
    /// [HYBRID] Single Shapes immediate-mode drawer for every planetary-defense pad.
    /// One <c>Draw.Command</c> per camera instead of one command (and callback) per pad.
    /// </summary>
    [DefaultExecutionOrder(66290)]
    public sealed class PlanetaryDefensePadZoneBatchDrawer : ImmediateModeShapeDrawer
    {
        /// <summary>Singleton instance created by <see cref="EnsureExists"/>.</summary>
        static PlanetaryDefensePadZoneBatchDrawer s_Instance;

        /// <summary>
        /// Creates a DontDestroyOnLoad host with this drawer when missing.
        /// Safe to call from pad Ensure — idempotent.
        /// </summary>
        public static void EnsureExists()
        {
            if (s_Instance != null)
                return;

            var existing = Object.FindAnyObjectByType<PlanetaryDefensePadZoneBatchDrawer>();
            if (existing != null)
            {
                s_Instance = existing;
                return;
            }

            var go = new GameObject("PlanetaryDefensePadZoneBatchDrawer");
            Object.DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<PlanetaryDefensePadZoneBatchDrawer>();
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
        }

        /// <summary>
        /// [HYBRID] Draws every registered pad that is within camera range.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            var pads = PlanetaryDefensePadZoneVisual.ActivePads;
            if (pads == null || pads.Count == 0 || cam == null)
                return;

            float maxDistSq = PlanetaryDefensePadZoneVisual.MaxDrawDistance
                              * PlanetaryDefensePadZoneVisual.MaxDrawDistance;
            Vector3 camPos = cam.transform.position;
            Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.BlendMode = ShapesBlendMode.Transparent;

                for (int i = 0; i < pads.Count; i++)
                {
                    var pad = pads[i];
                    if (pad == null || !pad.isActiveAndEnabled)
                        continue;

                    float radius = pad.RadiusLocal;
                    if (radius <= 0.001f)
                        continue;

                    // --- Distance cull (presentation only) ---
                    Vector3 worldPos = pad.transform.position;
                    if ((worldPos - camPos).sqrMagnitude > maxDistSq)
                        continue;

                    Matrix4x4 zoneMatrix = pad.transform.localToWorldMatrix * Matrix4x4.Rotate(flatXZ);
                    PlanetaryDefensePadZoneVisual.DrawSoftDiscIntoOpenCommand(
                        zoneMatrix,
                        radius,
                        pad.ZoneTint,
                        pad.PeakAlpha,
                        pad.SolidCoreFraction);
                }
            }
        }
    }
}
