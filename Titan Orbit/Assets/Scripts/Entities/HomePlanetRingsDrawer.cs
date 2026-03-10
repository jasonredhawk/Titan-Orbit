using UnityEngine;
using Shapes;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws Saturn-style tilted rings around a HomePlanet using Shapes immediate mode.
    /// Ring count = Home Planet level (1–6). Level 1 has 1 ring, adds one per level up to 6.
    /// </summary>
    [ExecuteAlways]
    public class HomePlanetRingsDrawer : ImmediateModeShapeDrawer
    {
        [Header("Ring Layout")]
        [Tooltip("Tilt angle (degrees around X). Negative = tilted down so rings pass in front of and behind the planet.")]
        [SerializeField] private float tiltDegrees = -26.7f;
        [Tooltip("Inner radius of the first ring band (planet unit radius ~0.5). Larger = more space from the planet.")]
        [SerializeField] private float innerRadius = 0.68f;
        [Tooltip("Radial width of each ring band.")]
        [SerializeField] private float ringThickness = 0.06f;
        [Tooltip("Gap between ring bands.")]
        [SerializeField] private float gapBetweenBands = 0.015f;
        [Header("Appearance")]
        [Tooltip("Base opacity of rings. Slightly transparent so planet shows through.")]
        [Range(0.2f, 1f)]
        [SerializeField] private float ringOpacity = 0.7f;
        [Tooltip("Extra glow/brightness per level (adds to opacity).")]
        [SerializeField] private float opacityPerLevel = 0.05f;

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

        private HomePlanet homePlanet;

        private void Awake()
        {
            homePlanet = GetComponentInParent<HomePlanet>();
        }

        public Vector3 GetRingAxisWorld()
        {
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null)
                return transform.up;

            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
            return homePlanet.transform.TransformDirection(tilt * Vector3.forward).normalized;
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null) return;

            int level = homePlanet.HomePlanetLevel;
            int ringCount = Mathf.Clamp(level, 1, 6);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;

                // Planet transform * tilt (negative X = down) so rings pass in front of and behind the planet
                Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
                Matrix4x4 planetMatrix = homePlanet.transform.localToWorldMatrix;
                Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);

                // Orbit zone: filled ring with radial gradient (alpha 0.3 at inner edge, 0 at outer), flat on ground
                if (drawOrbitZoneFill)
                {
                    Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
                    Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
                    Matrix4x4 homeMatrix = homePlanet.transform.localToWorldMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);
                    Draw.Matrix = homeMatrix;
                    float outerRadiusRuntime = homePlanet.GetOrbitZoneOuterRadiusLocal();
                    float zoneRadius = (orbitZoneInnerRadius + outerRadiusRuntime) * 0.5f;
                    float zoneThickness = outerRadiusRuntime - orbitZoneInnerRadius;
                    Color innerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, orbitZoneInnerAlpha);
                    Color outerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, 0f);
                    Draw.Ring(Vector3.zero, Quaternion.identity, zoneRadius, zoneThickness, DiscColors.Radial(innerColor, outerColor));
                }

                // Draw home-planet rings after orbit fill so ring bands are not visually clipped by the zone overlay.
                Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);
                float alpha = Mathf.Clamp01(ringOpacity + (level - 1) * opacityPerLevel);
                Color baseColor = TeamManager.GetTeamColor(homePlanet.TeamOwnership);
                Color color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);

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
