using UnityEngine;
using Shapes;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws Saturn-style tilted rings around a regular (non-home) planet using Shapes.
    /// Ring count = planet level (1–6), matching max regular planet level. One ring band per level.
    /// </summary>
    [ExecuteAlways]
    public class PlanetRingsDrawer : ImmediateModeShapeDrawer
    {
        [Header("Ring Layout")]
        [Tooltip("Tilt angle (degrees around X). Negative = tilted down like Saturn.")]
        [SerializeField] private float tiltDegrees = -26.7f;
        [Tooltip("Inner radius of the first ring band (planet unit radius ~0.5).")]
        [SerializeField] private float innerRadius = 0.68f;
        [Tooltip("Radial width of each ring band.")]
        [SerializeField] private float ringThickness = 0.06f;
        [Tooltip("Gap between ring bands.")]
        [SerializeField] private float gapBetweenBands = 0.015f;
        [Header("Appearance")]
        [Tooltip("Opacity of the ring.")]
        [Range(0.2f, 1f)]
        [SerializeField] private float ringOpacity = 0.6f;

        [Header("Orbit Zone Fill")]
        [Tooltip("Draw the orbit zone as a filled ring with gradient: 0.3 alpha at inner edge, 0 at outer edge.")]
        [SerializeField] private bool drawOrbitZoneFill = true;
        [Tooltip("Inner radius of orbit zone (planet local).")]
        [SerializeField] private float orbitZoneInnerRadius = 0.5f;
        [Tooltip("Outer radius of orbit zone (planet local).")]
        [SerializeField] private float orbitZoneOuterRadius = 0.85f;
        [SerializeField] private Color orbitZoneTint = new Color(0.5f, 0.7f, 0.95f);
        [Tooltip("Alpha at inner edge of orbit zone.")]
        [Range(0f, 1f)]
        [SerializeField] private float orbitZoneInnerAlpha = 0.3f;
        [Tooltip("Draw the orbit zone this far below the planet (local Y) so ships and gems render above it.")]
        [SerializeField] private float orbitZoneHeightBelowPlanet = 1f;

        private Planet planet;

        private void Awake()
        {
            planet = GetComponentInParent<Planet>();
        }

        public Vector3 GetRingAxisWorld()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            if (planet == null)
                return transform.up;

            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
            return planet.transform.TransformDirection(tilt * Vector3.forward).normalized;
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            if (planet == null) return;

            int level = planet.PlanetLevel;
            int ringCount = Mathf.Clamp(level, 1, 6);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;

                Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
                Matrix4x4 planetMatrix = planet.transform.localToWorldMatrix;
                Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);

                // Orbit zone: filled ring with radial gradient (alpha 0.3 at inner edge, 0 at outer), flat on ground
                if (drawOrbitZoneFill)
                {
                    Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
                    Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
                    Draw.Matrix = planetMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);
                    float outerRadiusRuntime = planet.GetOrbitZoneOuterRadiusLocal();
                    float zoneRadius = (orbitZoneInnerRadius + outerRadiusRuntime) * 0.5f;
                    float zoneThickness = outerRadiusRuntime - orbitZoneInnerRadius;
                    Color innerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, orbitZoneInnerAlpha);
                    Color outerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, 0f);
                    Draw.Ring(Vector3.zero, Quaternion.identity, zoneRadius, zoneThickness, DiscColors.Radial(innerColor, outerColor));
                }

                // Draw planet rings after orbit fill so ring bands are never visually cut by the orbit zone overlay.
                Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);
                Color baseColor = TeamManager.GetTeamColor(planet.TeamOwnership);
                Color color = new Color(baseColor.r, baseColor.g, baseColor.b, ringOpacity);

                float currentRadius = innerRadius;
                for (int i = 0; i < ringCount; i++)
                {
                    Draw.Ring(Vector3.zero, Quaternion.identity, currentRadius, ringThickness, color);
                    currentRadius += ringThickness + gapBetweenBands;
                }
            }
        }
    }
}
