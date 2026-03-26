using UnityEngine;
using Shapes;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws the gem moon dock zone like <see cref="HomePlanetRingsDrawer"/> orbit fill: flat XZ disc under the moon,
    /// semi-transparent white radial gradient from moon body radius to dock trigger radius.
    /// </summary>
    [ExecuteAlways]
    public class GemMoonOrbitZoneVisual : ImmediateModeShapeDrawer
    {
        [Header("Layout (matches HomePlanetRingsDrawer orbit zone)")]
        [Tooltip("Draw this far below moon center (moon local Y) so ships and pickups render above the fill.")]
        [SerializeField] private float heightBelowMoon = 0.35f;

        [Header("Fill (radial gradient)")]
        [SerializeField] private Color zoneTint = Color.white;
        [Tooltip("Alpha at inner edge (moon body radius).")]
        [Range(0f, 1f)]
        [SerializeField] private float zoneInnerAlpha = 0.28f;

        [Header("Outer rim")]
        [Tooltip("Thin ring at the outer dock radius so the zone edge reads clearly.")]
        [SerializeField] private bool drawOuterRim = true;
        [Range(0.01f, 0.25f)]
        [SerializeField] private float rimThickness = 0.06f;
        [Range(0.1f, 1f)]
        [SerializeField] private float rimAlpha = 0.5f;

        private PlanetGemMoon moon;

        private void Awake()
        {
            moon = GetComponentInParent<PlanetGemMoon>();
        }

        private void OnEnable()
        {
            if (moon == null)
                moon = GetComponentInParent<PlanetGemMoon>();
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (moon == null)
                moon = GetComponentInParent<PlanetGemMoon>();
            if (moon == null) return;

            float outerR = moon.GetMoonDockSnapRadiusWorld();
            if (outerR <= 0.0001f) return;

            float innerR = Mathf.Min(moon.GetMoonBodyRadiusWorld(), outerR * 0.98f);
            innerR = Mathf.Max(0.02f, innerR);
            if (outerR - innerR < 0.02f)
                innerR = Mathf.Max(0.02f, outerR - 0.02f);

            Transform t = moon.transform;
            Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
            Vector3 offsetBelow = new Vector3(0f, -heightBelowMoon, 0f);
            Matrix4x4 worldMatrix = t.localToWorldMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);

            float zoneRadius = (innerR + outerR) * 0.5f;
            float zoneThickness = outerR - innerR;
            Color innerColor = new Color(zoneTint.r, zoneTint.g, zoneTint.b, zoneInnerAlpha);
            Color outerColor = new Color(zoneTint.r, zoneTint.g, zoneTint.b, 0f);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = worldMatrix;

                Draw.Ring(Vector3.zero, Quaternion.identity, zoneRadius, zoneThickness, DiscColors.Radial(innerColor, outerColor));

                if (drawOuterRim)
                {
                    Color rimColor = new Color(zoneTint.r, zoneTint.g, zoneTint.b, rimAlpha);
                    Draw.Ring(Vector3.zero, Quaternion.identity, outerR, rimThickness, rimColor);
                }
            }
        }
    }
}
