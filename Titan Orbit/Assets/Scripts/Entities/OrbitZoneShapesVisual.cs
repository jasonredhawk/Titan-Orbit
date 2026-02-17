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
            // Orbit zone visual disabled — no ring drawn (zone is invisible).
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
