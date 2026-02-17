using UnityEngine;
using Shapes;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws Saturn-style tilted rings around a HomePlanet using Shapes immediate mode.
    /// Ring count = Home Planet level (3–6). Starts with 3 rings at level 3, adds one per level.
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

        private HomePlanet homePlanet;

        private void Awake()
        {
            homePlanet = GetComponentInParent<HomePlanet>();
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null) return;

            int level = homePlanet.HomePlanetLevel;
            int ringCount = Mathf.Clamp(level, 3, 6);

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

                float alpha = Mathf.Clamp01(ringOpacity + (level - 3) * opacityPerLevel);
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
