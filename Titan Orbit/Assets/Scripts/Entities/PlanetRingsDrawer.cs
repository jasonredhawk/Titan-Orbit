using UnityEngine;
using Shapes;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws a single Saturn-style tilted ring around a regular (non-home) planet using Shapes.
    /// Same visual style as home planet rings but only one ring; white.
    /// </summary>
    [ExecuteAlways]
    public class PlanetRingsDrawer : ImmediateModeShapeDrawer
    {
        [Header("Ring Layout")]
        [Tooltip("Tilt angle (degrees around X). Negative = tilted down like Saturn.")]
        [SerializeField] private float tiltDegrees = -26.7f;
        [Tooltip("Radius of the ring center (planet unit radius ~0.5).")]
        [SerializeField] private float ringRadius = 0.68f;
        [Tooltip("Radial width of the ring band.")]
        [SerializeField] private float ringThickness = 0.06f;
        [Header("Appearance")]
        [Tooltip("Opacity of the ring.")]
        [Range(0.2f, 1f)]
        [SerializeField] private float ringOpacity = 0.6f;

        private Planet planet;

        private void Awake()
        {
            planet = GetComponentInParent<Planet>();
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            if (planet == null) return;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;

                Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
                Matrix4x4 planetMatrix = planet.transform.localToWorldMatrix;
                Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);

                Color color = new Color(1f, 1f, 1f, ringOpacity);
                Draw.Ring(Vector3.zero, Quaternion.identity, ringRadius, ringThickness, color);
            }
        }
    }
}
