using Shapes;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Orbit-zone fill around the orbiting gem moon (legacy GemMoonOrbitZoneVisual).</summary>
    [ExecuteAlways]
    public class GemMoonOrbitZoneVisual : ImmediateModeShapeDrawer
    {
        const string MoonOrbitZoneName = "MoonOrbitZone";

        [Header("Orbit Zone Fill")]
        [SerializeField] bool drawOrbitZoneFill = true;
        [SerializeField] Color orbitZoneTint = new Color(0.5f, 0.7f, 0.95f);
        [Range(0f, 1f)]
        [SerializeField] float orbitZoneInnerAlpha = 0.3f;
        [SerializeField] float orbitZoneHeightBelowMoon = 0f;

        PlanetGemMoonVisualProxy _moon;

        public void Configure(PlanetGemMoonVisualProxy moon)
        {
            _moon = moon;
        }

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

        public override void DrawShapes(Camera cam)
        {
            if (!drawOrbitZoneFill || _moon == null)
                return;

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
