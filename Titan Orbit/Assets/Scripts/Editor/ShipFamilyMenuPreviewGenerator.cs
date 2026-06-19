using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using TitanOrbit.Data;
using TitanOrbit.Core;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Renders each upgrade-tree prefab from above with an isolated camera, saves PNGs under
    /// <c>.../MenuPreviews/</c> next to the <see cref="ShipFamilyDefinition"/> asset, imports as Sprites,
    /// and assigns <see cref="ShipFamilyChassisTierEntry.menuPreviewSprite"/>.
    /// </summary>
    public static class ShipFamilyMenuPreviewGenerator
    {
        private const int PreviewLayer = 30;
        private const int RenderSize = 512;

        private enum PreviewCameraStyle
        {
            TopDown,
            Theatrical
        }

        private struct PreviewVariant
        {
            public string name;
            public TeamManager.Team team;
            public Material[] materials;
        }

        private struct TheatricalPreviewFraming
        {
            public Vector3 lookTarget;
            public Vector3 cameraPosition;
            public Quaternion keyLightRotation;
        }

        private static TheatricalPreviewFraming BuildTheatricalFraming(Bounds wb, ShipFamilyDefinition def)
        {
            Vector3 c = wb.center;
            float padding = Mathf.Max(1f, def.menuPreviewBoundsPadding);
            float maxExt = Mathf.Max(wb.extents.x, wb.extents.y, wb.extents.z);
            float standoff = maxExt * padding * 3.6f;

            // Ship nose points +Z; camera sits front-right and above, looking down at the hull.
            const float elevationDeg = 26f;
            const float azimuthDeg = 34f;
            float elevRad = elevationDeg * Mathf.Deg2Rad;
            float azRad = azimuthDeg * Mathf.Deg2Rad;
            float horiz = standoff * Mathf.Cos(elevRad);
            float height = standoff * Mathf.Sin(elevRad);
            Vector3 cameraOffset = new Vector3(
                horiz * Mathf.Sin(azRad),
                height,
                horiz * Mathf.Cos(azRad));

            Vector3 lookTarget = c + new Vector3(0f, wb.extents.y * 0.12f, wb.extents.z * 0.1f);
            Vector3 cameraPosition = lookTarget + cameraOffset;

            // Key light from upper front-right, slightly higher than camera, toward the visible front-left surfaces.
            Vector3 lightOffset = new Vector3(
                horiz * 0.62f,
                height * 1.75f,
                horiz * 0.82f);
            Vector3 lightPosition = lookTarget + lightOffset;
            Quaternion keyLightRotation = Quaternion.LookRotation((lookTarget - lightPosition).normalized, Vector3.up);

            return new TheatricalPreviewFraming
            {
                lookTarget = lookTarget,
                cameraPosition = cameraPosition,
                keyLightRotation = keyLightRotation
            };
        }

        private static bool RenderComponentToPng(
            GameObject prefab,
            ShipFamilyDefinition def,
            string familyId,
            string componentId,
            string assetPathFull,
            Material[] overrideMaterials,
            PreviewCameraStyle style)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            GameObject instance = !string.IsNullOrEmpty(prefabPath)
                ? PrefabUtility.InstantiatePrefab(prefab) as GameObject
                : UnityEngine.Object.Instantiate(prefab);

            if (instance == null)
                return false;

            var root = new GameObject("ComponentMenuPreviewRoot");
            root.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetParent(root.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;

            if (!IsolateSingleComponentForPreview(root, instance, def, familyId, componentId, out GameObject componentRoot))
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            return RenderSubjectToPng(componentRoot, root, def, assetPathFull, overrideMaterials, style, minOrthoSize: 0.25f);
        }

        private static bool RenderShipToPng(
            GameObject prefab,
            ShipFamilyDefinition def,
            string assetPathFull,
            Material[] overrideMaterials,
            PreviewCameraStyle style)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            GameObject instance = !string.IsNullOrEmpty(prefabPath)
                ? PrefabUtility.InstantiatePrefab(prefab) as GameObject
                : UnityEngine.Object.Instantiate(prefab);

            if (instance == null)
                return false;

            var root = new GameObject("MenuPreviewRoot");
            root.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetParent(root.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;

            return RenderSubjectToPng(instance, root, def, assetPathFull, overrideMaterials, style, minOrthoSize: 0.35f);
        }

        private static bool RenderSubjectToPng(
            GameObject subject,
            GameObject root,
            ShipFamilyDefinition def,
            string assetPathFull,
            Material[] overrideMaterials,
            PreviewCameraStyle style,
            float minOrthoSize)
        {
            if (style == PreviewCameraStyle.Theatrical)
                subject.transform.localRotation = Quaternion.Euler(0f, -28f, 0f);

            if (overrideMaterials != null && overrideMaterials.Length > 0)
                ApplyMaterialOverride(subject, overrideMaterials);

            ApplyLayerRecursive(subject, PreviewLayer);

            Bounds wb = style == PreviewCameraStyle.Theatrical
                ? CalculateEnabledRendererBounds(subject)
                : CalculateWorldBounds(subject);
            if (wb.size.sqrMagnitude < 1e-6f)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            var camGo = new GameObject("MenuPreviewCam");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = def.menuPreviewBackgroundColor;
            cam.cullingMask = 1 << PreviewLayer;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;

            TheatricalPreviewFraming theatricalFraming = default;
            if (style == PreviewCameraStyle.Theatrical)
                theatricalFraming = BuildTheatricalFraming(wb, def);
            ConfigurePreviewCamera(cam, camGo.transform, wb, def, style, minOrthoSize, theatricalFraming);

            var lightGo = new GameObject("MenuPreviewLight");
            lightGo.hideFlags = HideFlags.HideAndDontSave;
            lightGo.transform.SetParent(root.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = style == PreviewCameraStyle.Theatrical ? 1.2f : 1.05f;
            light.cullingMask = ~0;
            if (style == PreviewCameraStyle.Theatrical)
                light.transform.rotation = theatricalFraming.keyLightRotation;
            else
                light.transform.rotation = Quaternion.Euler(58f, -32f, 0f);

            Color oldAmb = RenderSettings.ambientLight;
            RenderSettings.ambientLight = style == PreviewCameraStyle.Theatrical
                ? new Color(0.32f, 0.35f, 0.42f)
                : new Color(0.42f, 0.45f, 0.52f);

            var rt = RenderTexture.GetTemporary(RenderSize, RenderSize, 24, RenderTextureFormat.ARGB32);
            RenderTexture prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;

            RenderTexture.active = rt;
            var tex = new Texture2D(RenderSize, RenderSize, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, RenderSize, RenderSize), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            RenderSettings.ambientLight = oldAmb;

            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string sysPath = AssetPathToSystemPath(assetPathFull);
            string dir = Path.GetDirectoryName(sysPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(sysPath, png);

            UnityEngine.Object.DestroyImmediate(root);
            return true;
        }

        private static void ConfigurePreviewCamera(
            UnityEngine.Camera cam,
            Transform camTransform,
            Bounds wb,
            ShipFamilyDefinition def,
            PreviewCameraStyle style,
            float minOrthoSize,
            TheatricalPreviewFraming theatricalFraming)
        {
            Vector3 c = wb.center;
            float padding = Mathf.Max(1f, def.menuPreviewBoundsPadding);

            if (style == PreviewCameraStyle.TopDown)
            {
                float ext = Mathf.Max(wb.extents.x, wb.extents.z, wb.extents.y * 0.25f);
                float ortho = Mathf.Max(ext * padding, minOrthoSize);
                cam.orthographic = true;
                cam.orthographicSize = ortho;
                cam.fieldOfView = 60f;
                camTransform.position = c + Vector3.up * Mathf.Max(12f, wb.size.y + 4f);
                camTransform.rotation = Quaternion.Euler(90f, 0f, 0f);
                return;
            }

            cam.orthographic = false;
            cam.fieldOfView = Mathf.Clamp(def.menuPreviewTheatricalFieldOfView, 20f, 55f);
            camTransform.position = theatricalFraming.cameraPosition;
            camTransform.LookAt(theatricalFraming.lookTarget, Vector3.up);
        }

        [MenuItem("Titan Orbit/Generate Menu Previews For Selected Ship Family")]
        public static void MenuGenerateSelected()
        {
            foreach (UnityEngine.Object o in Selection.objects)
            {
                if (o is ShipFamilyDefinition def)
                {
                    GenerateForFamily(def);
                    return;
                }
            }
            EditorUtility.DisplayDialog("Ship Family", "Select a ShipFamilyDefinition asset in the Project window.", "OK");
        }

        [MenuItem("Titan Orbit/Generate Theatrical Menu Previews For Selected Ship Family")]
        public static void MenuGenerateTheatricalSelected()
        {
            foreach (UnityEngine.Object o in Selection.objects)
            {
                if (o is ShipFamilyDefinition def)
                {
                    GenerateTheatricalForFamily(def);
                    return;
                }
            }
            EditorUtility.DisplayDialog("Ship Family", "Select a ShipFamilyDefinition asset in the Project window.", "OK");
        }

        /// <summary>Generates PNGs + Sprite refs for every tier with a prefab. Safe to re-run; overwrites PNGs.</summary>
        public static void GenerateForFamily(ShipFamilyDefinition def)
        {
            GenerateMenuPreviewsForFamily(def, PreviewCameraStyle.TopDown);
        }

        /// <summary>Generates theatrical (3/4 hero) PNGs + Sprite refs for every tier with a prefab.</summary>
        public static void GenerateTheatricalForFamily(ShipFamilyDefinition def)
        {
            GenerateMenuPreviewsForFamily(def, PreviewCameraStyle.Theatrical);
        }

        private static void GenerateMenuPreviewsForFamily(ShipFamilyDefinition def, PreviewCameraStyle style)
        {
            if (def == null || def.upgradeTree == null || def.upgradeTree.Count == 0)
            {
                EditorUtility.DisplayDialog("Menu Previews", "Ship family has no upgrade tree entries.", "OK");
                return;
            }

            string defPath = AssetDatabase.GetAssetPath(def);
            if (string.IsNullOrEmpty(defPath))
            {
                EditorUtility.DisplayDialog("Menu Previews", "Could not resolve asset path.", "OK");
                return;
            }

            string defDir = Path.GetDirectoryName(defPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(defDir))
                return;

            string outFolder = $"{defDir}/MenuPreviews";
            EnsureAssetFolder(outFolder);

            PreviewVariant[] variants = BuildPreviewVariants(def);
            int done = 0;
            int skipped = 0;
            string styleLabel = style == PreviewCameraStyle.Theatrical ? "Theatrical menu" : "Menu";

            for (int i = 0; i < def.upgradeTree.Count; i++)
            {
                var tier = def.upgradeTree[i];
                if (tier == null || tier.prefab == null)
                {
                    skipped++;
                    continue;
                }

                string chassis = string.IsNullOrEmpty(tier.chassisId) ? $"tier_{i}" : tier.chassisId;
                string fileBase = SanitizeFileName(chassis);
                if (tier.teamMenuPreviewSprites == null)
                    tier.teamMenuPreviewSprites = new List<ShipFamilyMenuPreviewSprite>();
                else
                    tier.teamMenuPreviewSprites.Clear();
                List<ShipFamilyMenuPreviewSprite> teamSprites = tier.teamMenuPreviewSprites;

                for (int v = 0; v < variants.Length; v++)
                {
                    PreviewVariant variant = variants[v];
                    string variantFolder = $"{outFolder}/{SanitizeFileName(variant.name)}";
                    EnsureAssetFolder(variantFolder);
                    string pngPath = $"{variantFolder}/{fileBase}.png";

                    if (!RenderShipToPng(tier.prefab, def, pngPath, variant.materials, style))
                    {
                        Debug.LogWarning($"{styleLabel} preview: skip (no renderers?) {chassis} [{variant.name}]");
                        skipped++;
                        continue;
                    }

                    AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                    ConfigureSpriteImporter(pngPath);

                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
                    if (sprite == null)
                    {
                        var objs = AssetDatabase.LoadAllAssetsAtPath(pngPath);
                        foreach (var a in objs)
                        {
                            if (a is Sprite s)
                            {
                                sprite = s;
                                break;
                            }
                        }
                    }

                    if (sprite != null)
                    {
                        teamSprites.Add(new ShipFamilyMenuPreviewSprite
                        {
                            variantName = variant.name,
                            team = variant.team,
                            sprite = sprite
                        });
                        if (v == 0 || tier.menuPreviewSprite == null)
                            tier.menuPreviewSprite = sprite;
                        done++;
                    }
                    else
                        Debug.LogWarning($"{styleLabel} preview: imported but no Sprite at {pngPath}");
                }
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog(
                style == PreviewCameraStyle.Theatrical ? "Theatrical Menu Previews" : "Menu Previews",
                $"Generated {done} image(s) across {variants.Length} variant(s). Skipped {skipped} (no prefab or no mesh). Output: {outFolder}",
                "OK");
        }

        [MenuItem("Titan Orbit/Generate Component Menu Previews For Selected Ship Family")]
        public static void MenuGenerateComponentPreviewsSelected()
        {
            foreach (UnityEngine.Object o in Selection.objects)
            {
                if (o is ShipFamilyDefinition def)
                {
                    GenerateComponentPreviewsForFamily(def);
                    return;
                }
            }

            EditorUtility.DisplayDialog("Ship Family", "Select a ShipFamilyDefinition asset in the Project window.", "OK");
        }

        [MenuItem("Titan Orbit/Generate Theatrical Component Menu Previews For Selected Ship Family")]
        public static void MenuGenerateTheatricalComponentPreviewsSelected()
        {
            foreach (UnityEngine.Object o in Selection.objects)
            {
                if (o is ShipFamilyDefinition def)
                {
                    GenerateTheatricalComponentPreviewsForFamily(def);
                    return;
                }
            }

            EditorUtility.DisplayDialog("Ship Family", "Select a ShipFamilyDefinition asset in the Project window.", "OK");
        }

        /// <summary>
        /// Renders each listed component from an upgrade-tree prefab that contains that part, saves PNGs under
        /// <c>.../ComponentMenuPreviews/</c>, imports as Sprites, and assigns component menuPreviewSprite fields.
        /// </summary>
        public static void GenerateComponentPreviewsForFamily(ShipFamilyDefinition def)
        {
            GenerateComponentPreviewsForFamily(def, PreviewCameraStyle.TopDown);
        }

        /// <summary>
        /// Renders each component with a theatrical (3/4 hero) camera into <c>.../ComponentMenuPreviews/</c>, replacing assigned menu preview sprites.
        /// </summary>
        public static void GenerateTheatricalComponentPreviewsForFamily(ShipFamilyDefinition def)
        {
            GenerateComponentPreviewsForFamily(def, PreviewCameraStyle.Theatrical);
        }

        private static void GenerateComponentPreviewsForFamily(ShipFamilyDefinition def, PreviewCameraStyle style)
        {
            if (def == null || def.components == null || def.components.Count == 0)
            {
                EditorUtility.DisplayDialog("Component Menu Previews", "Ship family has no component entries.", "OK");
                return;
            }

            if (def.upgradeTree == null || def.upgradeTree.Count == 0)
            {
                EditorUtility.DisplayDialog("Component Menu Previews", "No upgrade-tree prefab found to render components from.", "OK");
                return;
            }

            string defPath = AssetDatabase.GetAssetPath(def);
            if (string.IsNullOrEmpty(defPath))
            {
                EditorUtility.DisplayDialog("Component Menu Previews", "Could not resolve asset path.", "OK");
                return;
            }

            string defDir = Path.GetDirectoryName(defPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(defDir))
                return;

            string familyId = string.IsNullOrWhiteSpace(def.familyId) ? "ShipFamily" : def.familyId.Trim();
            Dictionary<string, GameObject> componentSourcePrefabs = BuildComponentSourcePrefabMap(def, familyId);
            string outFolder = $"{defDir}/ComponentMenuPreviews";
            EnsureAssetFolder(outFolder);

            PreviewVariant[] variants = BuildPreviewVariants(def);
            int done = 0;
            int skippedMissingId = 0;
            int skippedNoPrefab = 0;
            int skippedNoMesh = 0;
            string styleLabel = style == PreviewCameraStyle.Theatrical ? "Theatrical component menu" : "Component menu";

            for (int i = 0; i < def.components.Count; i++)
            {
                ShipFamilyComponentEntry entry = def.components[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                {
                    skippedMissingId++;
                    continue;
                }

                string componentId = entry.componentId.Trim();
                if (!componentSourcePrefabs.TryGetValue(componentId, out GameObject sourcePrefab) || sourcePrefab == null)
                {
                    Debug.LogWarning($"{styleLabel} preview: skip (not found in any upgrade-tree prefab) {familyId}/{componentId}");
                    skippedNoPrefab += variants.Length;
                    continue;
                }

                string fileBase = SanitizeFileName(componentId);
                if (entry.teamMenuPreviewSprites == null)
                    entry.teamMenuPreviewSprites = new List<ShipFamilyMenuPreviewSprite>();
                else
                    entry.teamMenuPreviewSprites.Clear();
                List<ShipFamilyMenuPreviewSprite> teamSprites = entry.teamMenuPreviewSprites;

                for (int v = 0; v < variants.Length; v++)
                {
                    PreviewVariant variant = variants[v];
                    string variantFolder = $"{outFolder}/{SanitizeFileName(variant.name)}";
                    EnsureAssetFolder(variantFolder);
                    string pngPath = $"{variantFolder}/{fileBase}.png";

                    if (!RenderComponentToPng(sourcePrefab, def, familyId, componentId, pngPath, variant.materials, style))
                    {
                        Debug.LogWarning($"{styleLabel} preview: skip (no matching mesh) {familyId}/{componentId} [{variant.name}] from {sourcePrefab.name}");
                        skippedNoMesh++;
                        continue;
                    }

                    AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                    ConfigureSpriteImporter(pngPath);

                    Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
                    if (sprite == null)
                    {
                        var objs = AssetDatabase.LoadAllAssetsAtPath(pngPath);
                        foreach (var a in objs)
                        {
                            if (a is Sprite s)
                            {
                                sprite = s;
                                break;
                            }
                        }
                    }

                    if (sprite != null)
                    {
                        teamSprites.Add(new ShipFamilyMenuPreviewSprite
                        {
                            variantName = variant.name,
                            team = variant.team,
                            sprite = sprite
                        });
                        if (v == 0 || entry.menuPreviewSprite == null)
                            entry.menuPreviewSprite = sprite;
                        done++;
                    }
                    else
                    {
                        Debug.LogWarning($"{styleLabel} preview: imported but no Sprite at {pngPath}");
                    }
                }
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            int skippedTotal = skippedMissingId + skippedNoPrefab + skippedNoMesh;
            EditorUtility.DisplayDialog(
                style == PreviewCameraStyle.Theatrical ? "Theatrical Component Menu Previews" : "Component Menu Previews",
                $"Generated {done} image(s) across {variants.Length} variant(s). Skipped {skippedTotal} " +
                $"(missing id: {skippedMissingId}, no prefab: {skippedNoPrefab}, no mesh: {skippedNoMesh}). Output: {outFolder}",
                "OK");
        }

        /// <summary>
        /// Maps each component id to the highest-power upgrade-tree prefab that contains a matching transform.
        /// Components are spread across ship designs, so a single prefab is not enough.
        /// </summary>
        private static Dictionary<string, GameObject> BuildComponentSourcePrefabMap(ShipFamilyDefinition def, string familyId)
        {
            var map = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            var bestPower = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var prefabComponentsByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            if (def?.upgradeTree == null)
                return map;

            string prefix = familyId + "_";
            for (int i = 0; i < def.upgradeTree.Count; i++)
            {
                ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                if (tier?.prefab == null)
                    continue;

                string path = AssetDatabase.GetAssetPath(tier.prefab);
                if (string.IsNullOrEmpty(path))
                    continue;

                if (!prefabComponentsByPath.TryGetValue(path, out HashSet<string> componentIdsInPrefab))
                {
                    componentIdsInPrefab = CollectComponentIdsFromPrefabAsset(path, def, prefix);
                    prefabComponentsByPath[path] = componentIdsInPrefab;
                }

                foreach (string componentId in componentIdsInPrefab)
                {
                    if (!bestPower.TryGetValue(componentId, out float existingPower) || tier.powerScore >= existingPower)
                    {
                        bestPower[componentId] = tier.powerScore;
                        map[componentId] = tier.prefab;
                    }
                }
            }

            return map;
        }

        private static HashSet<string> CollectComponentIdsFromPrefabAsset(string assetPath, ShipFamilyDefinition def, string familyPrefix)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(assetPath) || def == null)
                return result;

            GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
            if (contents == null)
                return result;

            try
            {
                Transform[] transforms = contents.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform t = transforms[i];
                    if (t == null || t == contents.transform)
                        continue;

                    string name = t.name;
                    if (!name.StartsWith(familyPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string suffix = name.Substring(familyPrefix.Length);
                    if (def.TryGetComponentEntry(suffix, out ShipFamilyComponentEntry entry) &&
                        entry != null &&
                        !string.IsNullOrWhiteSpace(entry.componentId))
                    {
                        result.Add(entry.componentId.Trim());
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            return result;
        }

        /// <summary>
        /// Clones one matching component, resets it to local origin/identity on the preview root, and removes the ship hull.
        /// Uses Instantiate instead of reparenting prefab-instance transforms (Unity forbids SetParent on prefab parts).
        /// </summary>
        private static bool IsolateSingleComponentForPreview(
            GameObject previewRoot,
            GameObject shipInstance,
            ShipFamilyDefinition def,
            string familyId,
            string componentId,
            out GameObject componentRoot)
        {
            componentRoot = null;
            if (previewRoot == null || shipInstance == null || def == null || string.IsNullOrWhiteSpace(componentId))
                return false;

            var matched = new List<Transform>();
            CollectMatchingComponentTransforms(shipInstance.transform, def, familyId, componentId, matched);
            if (matched.Count == 0)
                return false;

            Transform anchor = SelectSingleComponentAnchor(matched);
            if (anchor == null)
                return false;

            Vector3 preservedScale = anchor.localScale;
            componentRoot = UnityEngine.Object.Instantiate(anchor.gameObject, previewRoot.transform);
            componentRoot.transform.localPosition = Vector3.zero;
            componentRoot.transform.localRotation = Quaternion.identity;
            componentRoot.transform.localScale = preservedScale;

            UnityEngine.Object.DestroyImmediate(shipInstance);

            Renderer[] renderers = componentRoot.GetComponentsInChildren<Renderer>(true);
            bool anyEnabled = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                    continue;
                if (r is ParticleSystemRenderer)
                {
                    r.enabled = false;
                    continue;
                }

                r.enabled = true;
                anyEnabled = true;
            }

            return anyEnabled;
        }

        private static Transform SelectSingleComponentAnchor(List<Transform> matched)
        {
            if (matched == null || matched.Count == 0)
                return null;
            if (matched.Count == 1)
                return matched[0];

            for (int i = 0; i < matched.Count; i++)
            {
                Transform t = matched[i];
                if (t == null)
                    continue;
                if (t.name.IndexOf("_Mirrored", StringComparison.OrdinalIgnoreCase) < 0)
                    return t;
            }

            return matched[0];
        }

        private static void CollectMatchingComponentTransforms(
            Transform root,
            ShipFamilyDefinition def,
            string familyId,
            string componentId,
            List<Transform> results)
        {
            if (root == null || results == null)
                return;

            if (TransformMatchesComponentId(def, root.name, familyId, componentId))
                results.Add(root);

            for (int i = 0; i < root.childCount; i++)
                CollectMatchingComponentTransforms(root.GetChild(i), def, familyId, componentId, results);
        }

        private static bool TransformMatchesComponentId(
            ShipFamilyDefinition def,
            string transformName,
            string familyId,
            string componentId)
        {
            if (def == null || string.IsNullOrWhiteSpace(transformName) || string.IsNullOrWhiteSpace(componentId))
                return false;

            string targetId = ShipFamilyDefinition.NormalizeComponentId(componentId.Trim());
            if (string.IsNullOrEmpty(targetId))
                return false;

            string suffix = transformName;
            if (!string.IsNullOrWhiteSpace(familyId))
            {
                string prefix = familyId.Trim() + "_";
                if (transformName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    suffix = transformName.Substring(prefix.Length);
                else
                    return false;
            }

            if (!def.TryGetComponentEntry(suffix, out ShipFamilyComponentEntry matchedEntry) || matchedEntry == null)
                return false;

            string matchedId = ShipFamilyDefinition.NormalizeComponentId(matchedEntry.componentId?.Trim());
            return !string.IsNullOrEmpty(matchedId) &&
                   string.Equals(matchedId, targetId, StringComparison.OrdinalIgnoreCase);
        }

        private static Bounds CalculateEnabledRendererBounds(GameObject root)
        {
            var rends = root.GetComponentsInChildren<Renderer>();
            Bounds? bounds = null;
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null || !r.enabled)
                    continue;
                if (!bounds.HasValue)
                    bounds = r.bounds;
                else
                {
                    Bounds b = bounds.Value;
                    b.Encapsulate(r.bounds);
                    bounds = b;
                }
            }

            return bounds ?? new Bounds(root.transform.position, Vector3.zero);
        }

        private static PreviewVariant[] BuildPreviewVariants(ShipFamilyDefinition def)
        {
            var list = new System.Collections.Generic.List<PreviewVariant>();
            if (def != null && def.teamMaterials != null)
            {
                for (int i = 0; i < def.teamMaterials.Count; i++)
                {
                    var set = def.teamMaterials[i];
                    if (set == null || set.materials == null || set.materials.Count == 0)
                        continue;
                    string label = !string.IsNullOrWhiteSpace(set.variantName)
                        ? set.variantName.Trim()
                        : set.team.ToString();
                    list.Add(new PreviewVariant
                    {
                        name = string.IsNullOrEmpty(label) ? $"Variant_{i + 1}" : label,
                        team = set.team,
                        materials = set.materials.ToArray()
                    });
                }
            }

            if (list.Count == 0)
            {
                list.Add(new PreviewVariant
                {
                    name = "Base",
                    team = TeamManager.Team.None,
                    materials = null
                });
            }

            return list.ToArray();
        }

        private static void ApplyMaterialOverride(GameObject root, Material[] overrideMaterials)
        {
            if (root == null || overrideMaterials == null || overrideMaterials.Length == 0)
                return;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                if (r is ParticleSystemRenderer) continue;
                Material[] current = r.sharedMaterials;
                if (current == null || current.Length == 0) continue;
                var replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                {
                    Material chosen = overrideMaterials[s % overrideMaterials.Length];
                    replaced[s] = chosen != null ? chosen : current[s];
                }
                r.sharedMaterials = replaced;
            }
        }

        private static void ConfigureSpriteImporter(string assetPath)
        {
            var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (ti == null) return;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Bilinear;
            ti.maxTextureSize = 512;
            ti.SaveAndReimport();
        }

        private static void ApplyLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                ApplyLayerRecursive(t.GetChild(i).gameObject, layer);
        }

        private static Bounds CalculateWorldBounds(GameObject root)
        {
            var rends = root.GetComponentsInChildren<Renderer>();
            if (rends == null || rends.Length == 0)
                return new Bounds(root.transform.position, Vector3.zero);

            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++)
                b.Encapsulate(rends[i].bounds);
            return b;
        }

        private static void EnsureAssetFolder(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath)) return;
            int idx = assetFolderPath.LastIndexOf('/');
            if (idx <= 0) return;
            string parent = assetFolderPath.Substring(0, idx);
            string leaf = assetFolderPath.Substring(idx + 1);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureAssetFolder(parent);
            if (!AssetDatabase.IsValidFolder(assetFolderPath))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        private static string AssetPathToSystemPath(string assetPath)
        {
            string rel = assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? assetPath.Substring("Assets/".Length)
                : assetPath;
            return Path.Combine(Application.dataPath, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "ship";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = false;
                foreach (char i in invalid)
                {
                    if (c == i) { bad = true; break; }
                }
                sb.Append(bad ? '_' : c);
            }
            return sb.Length > 0 ? sb.ToString() : "ship";
        }
    }
}
