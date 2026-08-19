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

        /// <summary>
        /// Legacy TMP root scale when labels were children of a scaled planet root
        /// (effective world size ≈ this × planetSize). Kept for size-matching helpers.
        /// </summary>
        public const float TextWorldScaleLegacyLocal = 0.04f;

        /// <summary>
        /// Default readable world scale for planet TMP under a unit-scale proxy root.
        /// [TITAN-ORBIT] After PlanetVisualBody, roots no longer multiply this by planetSize.
        /// </summary>
        public const float TextWorldScale = 0.55f;

        /// <summary>Clamp floor for planet population labels (small planets).</summary>
        public const float PlanetLabelWorldScaleMin = 0.45f;

        /// <summary>Clamp ceiling for planet population labels (huge homes).</summary>
        public const float PlanetLabelWorldScaleMax = 0.95f;

        /// <summary>Legacy moon label local scale when parented under a scaled planet.</summary>
        public const float MoonLabelWorldScaleLegacyLocal = 0.022f;

        public const float MoonLabelWorldScaleMin = 0.16f;
        public const float MoonLabelWorldScaleMax = 0.4f;

        /// <summary>
        /// Readable planet label world scale matching the old inherited-planet-scale look.
        /// </summary>
        public static float GetReadablePlanetLabelWorldScale(float planetSize)
        {
            return Mathf.Clamp(
                Mathf.Max(0.25f, planetSize) * TextWorldScaleLegacyLocal,
                PlanetLabelWorldScaleMin,
                PlanetLabelWorldScaleMax);
        }

        /// <summary>
        /// Readable moon gem/shield label world scale under a unit-scale planet root.
        /// </summary>
        public static float GetReadableMoonLabelWorldScale(float planetSize)
        {
            return Mathf.Clamp(
                Mathf.Max(0.25f, planetSize) * MoonLabelWorldScaleLegacyLocal,
                MoonLabelWorldScaleMin,
                MoonLabelWorldScaleMax);
        }

        /// <summary>
        /// One-shot hull / body clearance from mesh AABBs. Call on ship spawn / chassis swap only —
        /// floating counts must read the cached result, not remesh every frame.
        /// </summary>
        public static bool TryMeasureBodyClearance(Transform root, out float liftFromPivot, out float xzRadius)
        {
            liftFromPivot = 0f;
            xzRadius = 0f;
            if (root == null)
                return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (!TryEncapsulateBodyBounds(renderers, out Bounds bounds))
                return false;

            liftFromPivot = Mathf.Max(0f, bounds.max.y - root.position.y);
            xzRadius = Mathf.Max(bounds.extents.x, bounds.extents.z);
            return liftFromPivot > 0.0001f || xzRadius > 0.0001f;
        }

        /// <summary>
        /// World AABB of mesh / skinned-mesh body renderers (skips labels, particles, nameplates).
        /// </summary>
        public static bool TryEncapsulateBodyBounds(Renderer[] renderers, out Bounds bounds)
        {
            bounds = default;
            if (renderers == null)
                return false;

            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsBodyMeshRenderer(renderer))
                    continue;

                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return any;
        }

        public static bool IsBodyMeshRenderer(Renderer renderer)
        {
            if (ShouldSkipRenderer(renderer) || !renderer.enabled)
                return false;
            return renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
        }

        public static bool ShouldSkipRenderer(Renderer renderer)
        {
            // --- ShouldSkipRenderer ---
            if (renderer == null)
                return true;

            if (renderer is ParticleSystemRenderer)
                return true;

            if (IsWorldStatsLabelRenderer(renderer.transform))
                return true;

            string name = renderer.gameObject.name;
            if (name.Contains("PopulationText") ||
                name.Contains("PlanetStatsLabel") ||
                name.Contains("PopulationRow") ||
                name.Contains("FamilyTitle") ||
                name.Contains("GemsLabel") ||
                name.Contains("GemRow") ||
                name.Contains("ShieldRow") ||
                name.Contains("GemMoon") ||
                name.Contains("GemMoonStats") ||
                name.Contains("MoonOrbitZone") ||
                name.Contains("GemMoonMatrixShield") ||
                name.Contains("PlanetRings") ||
                name.Contains("PlanetOrbit") ||
                name.Contains("PlanetaryDefense") ||
                name.Contains("PadZone") ||
                name.Contains("InfoPlate") ||
                name.Contains("DefenseSlot") ||
                name.Contains("ShipNameplate") ||
                name.Contains("FullVersionBadge") ||
                name.Contains("PlayerBadge") ||
                name.Contains("HealthBar") ||
                name.Contains("GemsBar") ||
                name.Contains("PeopleBar") ||
                name.Contains("RoleRow"))
                return true;

            return false;
        }

        static bool IsWorldStatsLabelRenderer(Transform rendererTransform)
        {
            // --- IsWorldStatsLabelRenderer ---
            Transform t = rendererTransform;
            while (t != null)
            {
                string name = t.name;
                if (name.Contains("PlanetStatsLabel") ||
                    name.Contains("GemsLabel") ||
                    name.Contains("PopulationText") ||
                    name.Contains("ShipNameplate"))
                    return true;
                t = t.parent;
            }

            return false;
        }

        static bool IsUnderMoonVisual(Transform rendererTransform, Transform planetRoot)
        {
            // --- IsUnderMoonVisual ---
            if (rendererTransform == null || planetRoot == null)
                return false;

            Transform moonRoot = planetRoot.Find("GemMoonVisual");
            return moonRoot != null && rendererTransform.IsChildOf(moonRoot);
        }

        /// <summary>Top of planet body geometry in planet-root local space (excludes orbiting moon).</summary>
        public static float GetPlanetSurfaceYLocal(Transform planetRoot)
        {
            // --- Compute value ---
            if (planetRoot == null)
                return 0.5f;

            // Prefer scaled body half-height — reliable under unit-scale roots even when
            // SgtPlanet renderers are not ready on the first frames after Instantiates.
            if (PlanetVisualBody.TryGet(planetRoot, out Transform body))
            {
                float fromBody = Mathf.Max(0.25f, body.localScale.x) * 0.5f;
                float maxY = fromBody;
                bool foundMesh = false;
                var renderers = body.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    if (ShouldSkipRenderer(renderer))
                        continue;

                    Bounds bounds = renderer.bounds;
                    Vector3 topWorld = bounds.center + Vector3.up * bounds.extents.y;
                    Vector3 topLocal = planetRoot.InverseTransformPoint(topWorld);
                    if (topLocal.y > maxY)
                    {
                        maxY = topLocal.y;
                        foundMesh = true;
                    }
                }

                return Mathf.Max(0.01f, foundMesh ? maxY : fromBody);
            }

            float legacyMaxY = 0f;
            bool found = false;
            var all = planetRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var renderer = all[i];
                if (ShouldSkipRenderer(renderer) || IsUnderMoonVisual(renderer.transform, planetRoot))
                    continue;

                Bounds bounds = renderer.bounds;
                Vector3 topWorld = bounds.center + Vector3.up * bounds.extents.y;
                Vector3 topLocal = planetRoot.InverseTransformPoint(topWorld);
                if (topLocal.y > legacyMaxY)
                {
                    legacyMaxY = topLocal.y;
                    found = true;
                }
            }

            if (found)
                return Mathf.Max(0.01f, legacyMaxY);

            return 0.5f;
        }

        /// <summary>Top of moon body geometry in moon-root local space.</summary>
        public static float GetMoonSurfaceYLocal(Transform moonRoot, float fallbackMoonRadius = 0.25f)
        {
            // --- Compute value ---
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
            // --- Compute value ---
            if (surfaceY <= 0.001f)
                surfaceY = fallbackSurfaceRadius;
            return surfaceY + MoonPaddingAboveSurfaceLocal;
        }

        public static float GetPlanetLabelAnchorYLocal(float surfaceY, float fallbackSurfaceRadius = 0.5f)
        {
            // --- Compute value ---
            if (surfaceY <= 0.001f)
                surfaceY = fallbackSurfaceRadius;
            return surfaceY + PlanetPaddingAboveSurfaceLocal + surfaceY * PlanetTerrainClearanceOverRadius;
        }

        public static void ApplySnugPlanetLabel(TextMeshPro label, Transform planetRoot)
        {
            // --- Apply changes ---
            if (label == null || planetRoot == null)
                return;

            ApplySnugPlanetLabel(label.transform, planetRoot);
            label.verticalAlignment = VerticalAlignmentOptions.Bottom;
            label.enabled = true;
            label.ForceMeshUpdate();
        }

        public static void ApplySnugPlanetLabel(Transform labelRoot, Transform planetRoot)
        {
            // --- Apply changes ---
            if (labelRoot == null || planetRoot == null)
                return;

            float surfaceY = GetPlanetSurfaceYLocal(planetRoot);
            float anchorY = GetPlanetLabelAnchorYLocal(surfaceY);
            // No tight upper clamp — homeworlds can be Size 15+ with unit-scale roots.
            labelRoot.localPosition = new Vector3(0f, Mathf.Max(0.01f, anchorY), 0f);
        }

        public static void ApplySnugMoonLabel(TextMeshPro label, Transform moonRoot, float fallbackMoonRadius = 0.25f)
        {
            // --- Apply changes ---
            if (label == null || moonRoot == null)
                return;

            ApplySnugMoonLabel(label.transform, moonRoot, fallbackMoonRadius);
            label.verticalAlignment = VerticalAlignmentOptions.Bottom;
            label.ForceMeshUpdate();
        }

        public static void ApplySnugMoonLabel(Transform labelRoot, Transform moonRoot, float fallbackMoonRadius = 0.25f)
        {
            // --- Apply changes ---
            if (labelRoot == null || moonRoot == null)
                return;

            float anchorY = GetMoonLabelAnchorYLocal(GetMoonSurfaceYLocal(moonRoot, fallbackMoonRadius), fallbackMoonRadius);
            labelRoot.localPosition = new Vector3(0f, anchorY, 0f);
        }
    }
}
