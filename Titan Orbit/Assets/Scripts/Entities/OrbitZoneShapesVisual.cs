using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using Shapes;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Renders a thin border ring at the outer edge of the orbit zone using Shapes.
    /// Slightly brighter when the local player is in orbit.
    /// </summary>
    [ExecuteAlways]
    public class OrbitZoneShapesVisual : ImmediateModeShapeDrawer
    {
        [Header("Zone Bounds")]
        [Tooltip("Outer radius (orbit zone edge).")]
        [SerializeField] private float outerRadius = 0.85f;

        [Header("Border")]
        [Tooltip("Thickness of the border ring.")]
        [Range(0.001f, 0.1f)]
        [SerializeField] private float borderThickness = 0.02f;

        [Header("Appearance")]
        [Tooltip("Border color tint.")]
        [SerializeField] private Color tint = new Color(0.38f, 0.52f, 0.92f);
        [Tooltip("Opacity when no one is orbiting.")]
        [Range(0.1f, 0.6f)]
        [SerializeField] private float alphaWhenNotOrbiting = 0.3f;
        [Tooltip("Opacity when local player is in orbit.")]
        [Range(0.3f, 0.9f)]
        [SerializeField] private float alphaWhenOrbiting = 0.6f;

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

            Matrix4x4 worldMatrix = planet.transform.localToWorldMatrix;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = worldMatrix;

                // Draw a thin border ring at the outer edge of the orbit zone
                Draw.Ring(Vector3.zero, Quaternion.identity, outerRadius, borderThickness, color);
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
