using Shapes;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Shapes-based filled disc under the gem moon showing the ship orbit capture zone
    /// (legacy GemMoonOrbitZoneVisual). Reads radii from <see cref="PlanetGemMoonVisualProxy"/> and
    /// <see cref="PlanetOrbitMath"/>. ExecuteAlways so zone appears in editor scene view.
    /// </summary>
    [ExecuteAlways]
    public class GemMoonOrbitZoneVisual : ImmediateModeShapeDrawer
    {
        const string MoonOrbitZoneName = "MoonOrbitZone";

        [Header("Orbit Zone Fill")]
        // [TITAN-ORBIT] Off by default so we can evaluate moons without the soft capture-zone disc.
        // Flip back to true (or tick in the Inspector) to restore the graphic.
        [SerializeField] bool drawOrbitZoneFill = false;
        [SerializeField] Color orbitZoneTint = Color.white;
        [Range(0f, 1f)]
        [SerializeField] float orbitZoneInnerAlpha = 0.3f;
        [SerializeField] float orbitZoneHeightBelowMoon = 0f;

        PlanetGemMoonVisualProxy _moon;

        /// <summary>Called when moon proxy spawns — supplies planet size/level for ring radii.</summary>
        public void Configure(PlanetGemMoonVisualProxy moon)
        {
            _moon = moon;
        }

        /// <summary>Idempotent attach of orbit zone child on moon visual root.</summary>
        public static GemMoonOrbitZoneVisual EnsureOnMoonRoot(Transform moonRoot, PlanetGemMoonVisualProxy moon)
        {
            Transform existing = moonRoot.Find(MoonOrbitZoneName);
            GameObject zoneGo;
            if (existing != null)
                zoneGo = existing.gameObject;
            else
            {
                zoneGo = new GameObject(MoonOrbitZoneName);
                zoneGo.transform.SetParent(moonRoot, false);
            }

            var visual = zoneGo.GetComponent<GemMoonOrbitZoneVisual>();
            if (visual == null)
                visual = zoneGo.AddComponent<GemMoonOrbitZoneVisual>();
            visual.Configure(moon);
            return visual;
        }

        void Awake()
        {
            if (_moon == null)
                _moon = GetComponentInParent<PlanetGemMoonVisualProxy>();
        }

        public override void OnEnable()
        {
            if (_moon == null)
                _moon = GetComponentInParent<PlanetGemMoonVisualProxy>();
            base.OnEnable();
        }

        /// <summary>
        /// Skip moon-zone Shapes when far from the camera (presentation only).
        /// Same distance as <see cref="PlanetOrbitRingVisual"/> / pad batch cull.
        /// </summary>
        const float MaxDrawDistance = 90f;

        /// <summary>
        /// [HYBRID] Shapes draw pass — filled annulus under moon showing ship orbit capture shell.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            if (!drawOrbitZoneFill || _moon == null)
                return;

            // --- Distance cull (planar XZ only) ---
            // [TITAN-ORBIT] Avoid map-wide soft discs; nearby moons keep the capture-zone cue.
            // XZ-only so gameplay camera height (turret zoom / ship level) does not hide the zone.
            if (cam != null)
            {
                float maxDistSq = MaxDrawDistance * MaxDrawDistance;
                Vector3 delta = transform.position - cam.transform.position;
                if ((delta.x * delta.x + delta.z * delta.z) > maxDistSq)
                    return;
            }

            // --- Radii from moon proxy (local space) ---
            float outerLocal = _moon.MoonVisualShellOuterRadiusLocal;
            if (outerLocal <= 0.0001f)
                return;

            float innerLocal = Mathf.Min(_moon.MoonBodyRadiusLocal, outerLocal * 0.98f);
            innerLocal = Mathf.Max(0.02f, innerLocal);
            if (outerLocal - innerLocal < 0.02f)
                innerLocal = Mathf.Max(0.02f, outerLocal - 0.02f);

            Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
            Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowMoon, 0f);
            Matrix4x4 zoneMatrix = transform.localToWorldMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);
            PlanetRingMeshBuilder.DrawShapesOrbitRing(cam, zoneMatrix, innerLocal, outerLocal, orbitZoneTint, orbitZoneInnerAlpha);
        }
    }
}
