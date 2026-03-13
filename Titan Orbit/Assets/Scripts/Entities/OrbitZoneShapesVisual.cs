using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using Shapes;

namespace TitanOrbit.Entities
{
        /// <summary>
        /// Renders a circular border at the outer edge of the orbit zone using Shapes (faint but visible).
        /// Slightly brighter when the local player is in orbit.
        /// </summary>
    [ExecuteAlways]
    public class OrbitZoneShapesVisual : ImmediateModeShapeDrawer
    {
        [Header("Zone Bounds")]
        [Tooltip("Outer radius (orbit zone edge) in planet local space. Overridden at runtime by planet level (1.5x base + 5% per level).")]
        [SerializeField] private float outerRadius = 0.85f;
        [Tooltip("Draw the orbit zone this far below the planet (local Y) so ships and gems render above it.")]
        [SerializeField] private float heightBelowPlanet = 1f;

        [Header("Border")]
        [Tooltip("Thickness of the border ring. Very thick by default so orbit zone is unmissable.")]
        [Range(0.02f, 0.5f)]
        [SerializeField] private float borderThickness = 0.22f;

        [Header("Appearance")]
        [Tooltip("Border color. Bright so orbit zone is obvious.")]
        [SerializeField] private Color tint = new Color(1f, 1f, 1f);
        [Tooltip("Opacity when no one is orbiting.")]
        [Range(0.5f, 1f)]
        [SerializeField] private float alphaWhenNotOrbiting = 1f;
        [Tooltip("Opacity when local player is in orbit.")]
        [Range(0.5f, 1f)]
        [SerializeField] private float alphaWhenOrbiting = 1f;

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

            float radius = planet.GetOrbitZoneOuterRadiusLocal();

            // Ring in XZ plane (flat on ground) so it's visible from top-down camera; offset below planet so gameplay is above it
            Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
            Vector3 offsetBelow = new Vector3(0f, -heightBelowPlanet, 0f);
            Matrix4x4 worldMatrix = planet.transform.localToWorldMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = worldMatrix;

                // Single circular border at orbit zone outer edge (clearly visible)
                Draw.Ring(Vector3.zero, Quaternion.identity, radius, borderThickness, color);
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
