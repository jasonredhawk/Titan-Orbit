using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using Shapes;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Renders the orbit zone as faint, grooved rings using Shapes.
    /// Much fainter than the old mesh visual; slightly brighter when the local player is in orbit.
    /// </summary>
    [ExecuteAlways]
    public class OrbitZoneShapesVisual : ImmediateModeShapeDrawer
    {
        [Header("Zone Bounds")]
        [Tooltip("Inner radius (planet surface ~0.5).")]
        [SerializeField] private float innerRadius = 0.5f;
        [Tooltip("Outer radius (orbit zone edge).")]
        [SerializeField] private float outerRadius = 0.85f;

        [Header("Grooves")]
        [Tooltip("Number of thin ring bands (grooves) between inner and outer radius.")]
        [SerializeField] private int grooveCount = 14;
        [Tooltip("Gap between groove rings as fraction of groove width (0 = no gap).")]
        [Range(0f, 2f)]
        [SerializeField] private float grooveGapFraction = 0.4f;

        [Header("Appearance")]
        [Tooltip("Spacy tint (soft blue/cyan). Kept faint.")]
        [SerializeField] private Color tint = new Color(0.38f, 0.52f, 0.92f);
        [Tooltip("Opacity when no one is orbiting. Kept very low.")]
        [Range(0.01f, 0.15f)]
        [SerializeField] private float alphaWhenNotOrbiting = 0.032f;
        [Tooltip("Opacity when local player is in orbit. Still faint.")]
        [Range(0.04f, 0.2f)]
        [SerializeField] private float alphaWhenOrbiting = 0.09f;

        private Planet planet;

        private void Awake()
        {
            planet = GetComponentInParent<Planet>();
            HideLegacyMeshVisual();
        }

        private void OnEnable()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            HideLegacyMeshVisual();
        }

        /// <summary>Hide the old mesh-based orbit zone so only Shapes is visible.</summary>
        private void HideLegacyMeshVisual()
        {
            var meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null)
                meshRenderer.enabled = false;
            var meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
                meshFilter.sharedMesh = null;
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            if (planet == null) return;

            bool orbiting = IsLocalPlayerOrbitingThisPlanet();
            float alpha = orbiting ? alphaWhenOrbiting : alphaWhenNotOrbiting;
            Color color = new Color(tint.r, tint.g, tint.b, alpha);

            float totalWidth = outerRadius - innerRadius;
            float grooveWidth = totalWidth / (grooveCount + (grooveCount - 1) * grooveGapFraction);
            float gapWidth = grooveWidth * grooveGapFraction;

            Matrix4x4 worldMatrix = planet.transform.localToWorldMatrix;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = worldMatrix;

                float r = innerRadius;
                for (int i = 0; i < grooveCount && r < outerRadius; i++)
                {
                    float bandWidth = Mathf.Min(grooveWidth, outerRadius - r);
                    if (bandWidth > 0.001f)
                        Draw.Ring(Vector3.zero, Quaternion.identity, r + bandWidth * 0.5f, bandWidth, color);
                    r += grooveWidth + gapWidth;
                }
            }
        }

        private bool IsLocalPlayerOrbitingThisPlanet()
        {
            if (planet == null) return false;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient || NetworkManager.Singleton.SpawnManager == null)
                return false;
            var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            if (localPlayer == null) return false;
            var ship = localPlayer.GetComponent<Starship>();
            return ship != null && ship.IsInOrbit && ship.CurrentOrbitPlanet == planet;
        }
    }
}
