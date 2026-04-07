using System;
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

        private struct PreviewVariant
        {
            public string name;
            public TeamManager.Team team;
            public Material[] materials;
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

        /// <summary>Generates PNGs + Sprite refs for every tier with a prefab. Safe to re-run; overwrites PNGs.</summary>
        public static void GenerateForFamily(ShipFamilyDefinition def)
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
                    tier.teamMenuPreviewSprites = new System.Collections.Generic.List<ShipFamilyMenuPreviewSprite>();
                else
                    tier.teamMenuPreviewSprites.Clear();

                for (int v = 0; v < variants.Length; v++)
                {
                    PreviewVariant variant = variants[v];
                    string variantFolder = $"{outFolder}/{SanitizeFileName(variant.name)}";
                    EnsureAssetFolder(variantFolder);
                    string pngPath = $"{variantFolder}/{fileBase}.png";

                    if (!RenderTopDownToPng(tier.prefab, def, pngPath, variant.materials))
                    {
                        Debug.LogWarning($"Menu preview: skip (no renderers?) {chassis} [{variant.name}]");
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
                        tier.teamMenuPreviewSprites.Add(new ShipFamilyMenuPreviewSprite
                        {
                            variantName = variant.name,
                            team = variant.team,
                            sprite = sprite
                        });
                        if (v == 0 || tier.menuPreviewSprite == null)
                            tier.menuPreviewSprite = sprite; // Backward compatibility for existing UI.
                        done++;
                    }
                    else
                        Debug.LogWarning($"Menu preview: imported but no Sprite at {pngPath}");
                }
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Menu Previews", $"Generated {done} image(s) across {variants.Length} variant(s). Skipped {skipped} (no prefab or no mesh). Output: {outFolder}", "OK");
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

        private static bool RenderTopDownToPng(GameObject prefab, ShipFamilyDefinition def, string assetPathFull, Material[] overrideMaterials)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            GameObject instance = !string.IsNullOrEmpty(prefabPath)
                ? PrefabUtility.InstantiatePrefab(prefab) as GameObject
                : UnityEngine.Object.Instantiate(prefab);

            if (instance == null) return false;

            var root = new GameObject("MenuPreviewRoot");
            root.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetParent(root.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;

            if (overrideMaterials != null && overrideMaterials.Length > 0)
                ApplyMaterialOverride(instance, overrideMaterials);

            ApplyLayerRecursive(instance, PreviewLayer);

            Bounds wb = CalculateWorldBounds(instance);
            if (wb.size.sqrMagnitude < 1e-6f)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            Vector3 c = wb.center;
            float ext = Mathf.Max(wb.extents.x, wb.extents.z, wb.extents.y * 0.25f);
            float ortho = Mathf.Max(ext * Mathf.Max(1f, def.menuPreviewBoundsPadding), 0.35f);

            var camGo = new GameObject("MenuPreviewCam");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = def.menuPreviewBackgroundColor;
            cam.cullingMask = 1 << PreviewLayer;
            cam.orthographic = true;
            cam.orthographicSize = ortho;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.transform.position = c + Vector3.up * Mathf.Max(12f, wb.size.y + 4f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            var lightGo = new GameObject("MenuPreviewLight");
            lightGo.hideFlags = HideFlags.HideAndDontSave;
            lightGo.transform.SetParent(root.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.05f;
            light.cullingMask = ~0;
            light.transform.rotation = Quaternion.Euler(58f, -32f, 0f);

            Color oldAmb = RenderSettings.ambientLight;
            RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.52f);

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
