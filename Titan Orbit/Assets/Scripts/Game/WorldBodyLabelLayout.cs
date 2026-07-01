using TMPro;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Snug world-space label placement just above body meshes.</summary>
    public static class WorldBodyLabelLayout
    {
        public const float MoonPaddingAboveSurfaceLocal = 0.02f;
        public const float PlanetPaddingAboveSurfaceLocal = 0.04f;
        /// <summary>Extra lift above mesh top for displaced SGT terrain (mountains).</summary>
        public const float PlanetTerrainClearanceOverRadius = 0.12f;
        public const float TextWorldScale = 0.04f;

        public static bool ShouldSkipRenderer(Renderer renderer)
        {
            if (renderer == null)
                return true;

            string name = renderer.gameObject.name;
            if (name.Contains("PopulationText") ||
                name.Contains("GemsLabel") ||
                name.Contains("GemMoon") ||
                name.Contains("GemMoonStats") ||
                name.Contains("PlanetRings") ||
                name.Contains("PlanetOrbit"))
                return true;

            return renderer is ParticleSystemRenderer;
        }

        static bool IsUnderMoonVisual(Transform rendererTransform, Transform planetRoot)
        {
            if (rendererTransform == null || planetRoot == null)
                return false;

            Transform moonRoot = planetRoot.Find("GemMoonVisual");
            return moonRoot != null && rendererTransform.IsChildOf(moonRoot);
        }

        /// <summary>Top of planet body geometry in planet-root local space (excludes orbiting moon).</summary>
        public static float GetPlanetSurfaceYLocal(Transform planetRoot)
        {
            if (planetRoot == null)
                return 0.5f;

            float maxY = 0f;
            bool found = false;
            var renderers = planetRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (ShouldSkipRenderer(renderer) || IsUnderMoonVisual(renderer.transform, planetRoot))
                    continue;

                Bounds bounds = renderer.bounds;
                Vector3 topWorld = bounds.center + Vector3.up * bounds.extents.y;
                Vector3 topLocal = planetRoot.InverseTransformPoint(topWorld);
                if (topLocal.y > maxY)
                {
                    maxY = topLocal.y;
                    found = true;
                }
            }

            if (found)
                return maxY;

            foreach (var col in planetRoot.GetComponents<SphereCollider>())
            {
                if (!col.isTrigger)
                    return col.radius;
            }

            return 0.5f;
        }

        /// <summary>Top of moon body geometry in moon-root local space.</summary>
        public static float GetMoonSurfaceYLocal(Transform moonRoot, float fallbackMoonRadius = 0.25f)
        {
            if (moonRoot == null)
                return fallbackMoonRadius;

            Transform spin = moonRoot.Find("GemMoonSpinMesh");
            if (spin != null)
            {
                var renderer = spin.GetComponent<Renderer>();
                if (renderer != null)
                {
                    Vector3 topWorld = renderer.bounds.center + Vector3.up * renderer.bounds.extents.y;
                    Vector3 topLocal = moonRoot.InverseTransformPoint(topWorld);
                    if (topLocal.y > 0.001f)
                        return topLocal.y;
                }

                return 0.5f * Mathf.Max(spin.localScale.x, spin.localScale.y, spin.localScale.z);
            }

            return fallbackMoonRadius;
        }

        public static float GetMoonLabelAnchorYLocal(float surfaceY, float fallbackSurfaceRadius = 0.25f)
        {
            if (surfaceY <= 0.001f)
                surfaceY = fallbackSurfaceRadius;
            return surfaceY + MoonPaddingAboveSurfaceLocal;
        }

        public static float GetPlanetLabelAnchorYLocal(float surfaceY, float fallbackSurfaceRadius = 0.5f)
        {
            if (surfaceY <= 0.001f)
                surfaceY = fallbackSurfaceRadius;
            return surfaceY + PlanetPaddingAboveSurfaceLocal + surfaceY * PlanetTerrainClearanceOverRadius;
        }

        public static void ApplySnugPlanetLabel(TextMeshPro label, Transform planetRoot)
        {
            if (label == null || planetRoot == null)
                return;

            float anchorY = GetPlanetLabelAnchorYLocal(GetPlanetSurfaceYLocal(planetRoot));
            Transform t = label.transform;
            Vector3 localPos = t.localPosition;
            localPos.x = 0f;
            localPos.y = anchorY;
            localPos.z = 0f;
            t.localPosition = localPos;
            label.verticalAlignment = VerticalAlignmentOptions.Bottom;
            label.enabled = true;
            label.ForceMeshUpdate();
        }

        public static void ApplySnugMoonLabel(TextMeshPro label, Transform moonRoot, float fallbackMoonRadius = 0.25f)
        {
            if (label == null || moonRoot == null)
                return;

            float anchorY = GetMoonLabelAnchorYLocal(GetMoonSurfaceYLocal(moonRoot, fallbackMoonRadius), fallbackMoonRadius);
            label.transform.localPosition = new Vector3(0f, anchorY, 0f);
            label.verticalAlignment = VerticalAlignmentOptions.Bottom;
            label.ForceMeshUpdate();
        }
    }
}
