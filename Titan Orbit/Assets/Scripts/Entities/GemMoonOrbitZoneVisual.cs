using UnityEngine;
using UnityEngine.Serialization;
using TitanOrbit.Core;
using Shapes;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Orbit-zone fill for the gem moon: same Shapes immediate-mode pattern as <see cref="HomePlanetRingsDrawer"/>
    /// (flat XZ disc under the body, radial gradient).
    /// </summary>
    [ExecuteAlways]
    public class GemMoonOrbitZoneVisual : ImmediateModeShapeDrawer
    {
        [Header("Orbit Zone Fill")]
        [Tooltip("Draw the orbit zone as a filled ring with gradient: alpha at inner edge, 0 at outer edge (same idea as HomePlanetRingsDrawer).")]
        [SerializeField] private bool drawOrbitZoneFill = true;
        [FormerlySerializedAs("zoneTint")]
        [Tooltip("Match planet orbit fill (PlanetRingsDrawer): soft blue reads more translucent than white at the same alpha.")]
        [SerializeField] private Color orbitZoneTint = new Color(0.5f, 0.7f, 0.95f);
        [Tooltip("Alpha at inner edge of orbit zone (moon body radius). Same default as planet orbit zone (0.3).")]
        [Range(0f, 1f)]
        [FormerlySerializedAs("zoneInnerAlpha")]
        [SerializeField] private float orbitZoneInnerAlpha = 0.3f;
        [Tooltip("Draw the orbit zone this far below the moon center (moon local Y) so ships and gems render above it.")]
        [FormerlySerializedAs("heightBelowMoon")]
        [SerializeField] private float orbitZoneHeightBelowPlanet = 0.35f;

        private PlanetGemMoon moon;

        private void Awake()
        {
            moon = GetComponentInParent<PlanetGemMoon>();
        }

        public override void OnEnable()
        {
            if (moon == null)
                moon = GetComponentInParent<PlanetGemMoon>();
            // Required on URP/HDRP so Shapes subscribes to beginCameraRendering (see ImmediateModeShapeDrawer).
            base.OnEnable();
        }

        public override void OnDisable()
        {
            base.OnDisable();
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (!drawOrbitZoneFill) return;

            if (moon == null)
                moon = GetComponentInParent<PlanetGemMoon>();
            if (moon == null) return;

            // Always show the dock/orbit zone so players can navigate to it.

            // Radii must be in moon *local* space (collider space), like HomePlanetRingsDrawer uses planet-local radii.
            // World radii × localToWorldMatrix would apply planet/parent scale twice and blow up the disc.
            float outerRadiusRuntime = moon.GetMoonDockSnapRadiusLocal();
            if (outerRadiusRuntime <= 0.0001f) return;

            float innerRadiusRuntime = Mathf.Min(moon.GetMoonBodyRadiusLocal(), outerRadiusRuntime * 0.98f);
            innerRadiusRuntime = Mathf.Max(0.02f, innerRadiusRuntime);
            if (outerRadiusRuntime - innerRadiusRuntime < 0.02f)
                innerRadiusRuntime = Mathf.Max(0.02f, outerRadiusRuntime - 0.02f);

            Transform t = moon.transform;
            Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
            Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
            Matrix4x4 homeMatrix = t.localToWorldMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);

            float zoneRadius = (innerRadiusRuntime + outerRadiusRuntime) * 0.5f;
            float zoneThickness = outerRadiusRuntime - innerRadiusRuntime;
            Color innerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, orbitZoneInnerAlpha);
            Color outerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, 0f);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = homeMatrix;

                Draw.Ring(Vector3.zero, Quaternion.identity, zoneRadius, zoneThickness, DiscColors.Radial(innerColor, outerColor));
            }
        }
    }
}
