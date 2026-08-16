#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Renders each MEGA hull with the same 3/4 hero camera as ship-family theatrical
    /// previews, writes one PNG per team under
    /// <c>Assets/Prefabs/MEGA_Ships/MenuPreviews/TeamA|TeamB|…/</c>, and assigns
    /// <see cref="MegaShipCatalogEntry.teamMenuPreviewSprites"/> plus
    /// <see cref="MegaShipCatalogEntry.menuPreviewSprite"/> (TeamA / first fallback).
    /// Team tint reuses family <see cref="ShipFamilyTeamMaterialSet"/> materials so
    /// thumbs match in-game MEGA proxies.
    /// </summary>
    public static class MegaShipMenuPreviewGenerator
    {
        const int PreviewLayer = 30;
        const int RenderSize = 512;
        const string OutputFolder = "Assets/Prefabs/MEGA_Ships/MenuPreviews";

        struct TheatricalPreviewFraming
        {
            public Vector3 lookTarget;
            public Vector3 cameraPosition;
            public Quaternion keyLightRotation;
            public float maxExtent;
        }

        struct PreviewVariant
        {
            public string name;
            public TeamManager.Team team;
            public Material[] materials;
        }

        /// <summary>Generates 5-team theatrical PNGs + Sprite refs for every catalog hull with a prefab.</summary>
        public static void GenerateTheatricalForCatalog(MegaShipCatalog catalog)
        {
            if (catalog == null || catalog.entries == null || catalog.entries.Count == 0)
            {
                EditorUtility.DisplayDialog("MEGA Theatrical Previews", "Catalog has no hull entries.", "OK");
                return;
            }

            EnsureAssetFolder(OutputFolder);
            PreviewVariant[] variants = BuildPreviewVariants(catalog);

            int done = 0;
            int skipped = 0;
            try
            {
                for (int i = 0; i < catalog.entries.Count; i++)
                {
                    MegaShipCatalogEntry entry = catalog.entries[i];
                    string label = entry != null && !string.IsNullOrEmpty(entry.displayName)
                        ? entry.displayName
                        : $"hull {i}";
                    EditorUtility.DisplayProgressBar(
                        "MEGA Theatrical Menu Previews",
                        $"{i + 1}/{catalog.entries.Count}  {label}",
                        (i + 0.5f) / catalog.entries.Count);

                    if (entry == null || entry.prefab == null)
                    {
                        skipped++;
                        continue;
                    }

                    if (entry.teamMenuPreviewSprites == null)
                        entry.teamMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();
                    else
                        entry.teamMenuPreviewSprites.Clear();

                    string chassisId = MegaShipCatalog.FormatChassisId(entry.catalogIndex);
                    string fileBase = SanitizeFileName(chassisId);
                    Sprite firstSprite = null;

                    for (int v = 0; v < variants.Length; v++)
                    {
                        PreviewVariant variant = variants[v];
                        string variantFolder = $"{OutputFolder}/{SanitizeFileName(variant.name)}";
                        EnsureAssetFolder(variantFolder);
                        string pngPath = $"{variantFolder}/{fileBase}.png";
                        if (!RenderShipToPng(entry.prefab, catalog, pngPath, variant.materials))
                        {
                            Debug.LogWarning(
                                $"[MegaShipCatalog] Theatrical preview skipped (no mesh): {chassisId} [{variant.name}] ({entry.prefab.name})");
                            skipped++;
                            continue;
                        }

                        AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                        ConfigureSpriteImporter(pngPath);

                        Sprite sprite = LoadSprite(pngPath);
                        if (sprite == null)
                        {
                            Debug.LogWarning($"[MegaShipCatalog] Theatrical preview imported but no Sprite at {pngPath}");
                            skipped++;
                            continue;
                        }

                        entry.teamMenuPreviewSprites.Add(new ShipFamilyTeamMenuPreview
                        {
                            variantName = variant.name,
                            team = variant.team,
                            sprite = sprite,
                        });
                        if (firstSprite == null)
                            firstSprite = sprite;
                        done++;
                    }

                    if (firstSprite != null)
                        entry.menuPreviewSprite = firstSprite;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            MegaShipCatalog.InvalidateCache();
            EditorUtility.DisplayDialog(
                "MEGA Theatrical Menu Previews",
                $"Generated {done} image(s) across {variants.Length} team variant(s). Skipped {skipped} (no prefab or no mesh).\nOutput: {OutputFolder}/TeamA|…",
                "OK");
        }

        static bool RenderShipToPng(
            GameObject prefab,
            MegaShipCatalog catalog,
            string assetPathFull,
            Material[] overrideMaterials)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            GameObject instance = !string.IsNullOrEmpty(prefabPath)
                ? PrefabUtility.InstantiatePrefab(prefab) as GameObject
                : UnityEngine.Object.Instantiate(prefab);
            if (instance == null)
                return false;

            var root = new GameObject("MegaMenuPreviewRoot");
            root.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetParent(root.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.Euler(0f, -28f, 0f);
            instance.transform.localScale = Vector3.one;

            PrepareRenderers(instance);
            if (overrideMaterials != null && overrideMaterials.Length > 0)
                ApplyMaterialOverride(instance, overrideMaterials);
            ApplyLayerRecursive(instance, PreviewLayer);

            Bounds wb = CalculateEnabledRendererBounds(instance);
            if (wb.size.sqrMagnitude < 1e-6f)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return false;
            }

            TheatricalPreviewFraming framing = BuildTheatricalFraming(wb, catalog);

            var camGo = new GameObject("MegaMenuPreviewCam");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            // Opaque black even if an existing catalog asset still has alpha 0.
            cam.backgroundColor = Color.black;
            cam.cullingMask = 1 << PreviewLayer;
            cam.orthographic = false;
            cam.fieldOfView = Mathf.Clamp(catalog.menuPreviewTheatricalFieldOfView, 20f, 55f);
            camGo.transform.position = framing.cameraPosition;
            camGo.transform.LookAt(framing.lookTarget, Vector3.up);

            float dist = Vector3.Distance(framing.cameraPosition, framing.lookTarget);
            cam.nearClipPlane = Mathf.Clamp(dist * 0.02f, 0.1f, 20f);
            cam.farClipPlane = Mathf.Max(200f, dist + framing.maxExtent * 6f);

            var lightGo = new GameObject("MegaMenuPreviewLight");
            lightGo.hideFlags = HideFlags.HideAndDontSave;
            lightGo.transform.SetParent(root.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.cullingMask = ~0;
            light.transform.rotation = framing.keyLightRotation;

            Color oldAmb = RenderSettings.ambientLight;
            RenderSettings.ambientLight = new Color(0.32f, 0.35f, 0.42f);

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

        static TheatricalPreviewFraming BuildTheatricalFraming(Bounds wb, MegaShipCatalog catalog)
        {
            Vector3 c = wb.center;
            float padding = Mathf.Max(1f, catalog.menuPreviewBoundsPadding);
            float maxExt = Mathf.Max(wb.extents.x, wb.extents.y, wb.extents.z);
            float standoff = maxExt * padding * 3.6f;

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

            Vector3 lightOffset = new Vector3(horiz * 0.62f, height * 1.75f, horiz * 0.82f);
            Vector3 lightPosition = lookTarget + lightOffset;
            Quaternion keyLightRotation = Quaternion.LookRotation((lookTarget - lightPosition).normalized, Vector3.up);

            return new TheatricalPreviewFraming
            {
                lookTarget = lookTarget,
                cameraPosition = cameraPosition,
                keyLightRotation = keyLightRotation,
                maxExtent = maxExt
            };
        }

        /// <summary>
        /// Builds TeamA–TeamE variants. Prefers catalog <see cref="MegaShipCatalog.teamMaterials"/>,
        /// then fills gaps from regular <see cref="ShipFamilyDefinition.teamMaterials"/> so MEGA
        /// thumbs use the same in-game team palettes.
        /// </summary>
        static PreviewVariant[] BuildPreviewVariants(MegaShipCatalog catalog)
        {
            var byTeam = new Dictionary<TeamManager.Team, PreviewVariant>();
            CollectMaterialSets(catalog != null ? catalog.teamMaterials : null, byTeam);

            if (byTeam.Count < 5)
            {
                string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition");
                for (int i = 0; i < guids.Length && byTeam.Count < 5; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                    if (def == null)
                        continue;
                    CollectMaterialSets(def.teamMaterials, byTeam);
                }
            }

            var ordered = new[]
            {
                TeamManager.Team.TeamA,
                TeamManager.Team.TeamB,
                TeamManager.Team.TeamC,
                TeamManager.Team.TeamD,
                TeamManager.Team.TeamE,
            };
            var result = new PreviewVariant[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
            {
                TeamManager.Team team = ordered[i];
                if (byTeam.TryGetValue(team, out PreviewVariant found))
                    result[i] = found;
                else
                    result[i] = new PreviewVariant
                    {
                        name = team.ToString(),
                        team = team,
                        materials = null,
                    };
            }

            return result;
        }

        /// <summary>Adds each authored team material set that is not already in <paramref name="byTeam"/>.</summary>
        static void CollectMaterialSets(
            List<ShipFamilyTeamMaterialSet> sets,
            Dictionary<TeamManager.Team, PreviewVariant> byTeam)
        {
            if (sets == null || byTeam == null)
                return;

            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (set == null || set.materials == null || set.materials.Count == 0)
                    continue;

                TeamManager.Team team = TeamManager.FromTeamId(set.team);
                if (team == TeamManager.Team.None || byTeam.ContainsKey(team))
                    continue;

                byTeam[team] = new PreviewVariant
                {
                    name = team.ToString(),
                    team = team,
                    materials = set.materials.ToArray(),
                };
            }
        }

        /// <summary>
        /// Swaps renderer sharedMaterials the same way <see cref="TitanOrbit.Game.ShipVisualApplier.ApplyTeamMaterials"/>
        /// tints live MEGA proxies.
        /// </summary>
        static void ApplyMaterialOverride(GameObject root, Material[] overrideMaterials)
        {
            if (root == null || overrideMaterials == null || overrideMaterials.Length == 0)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || r is ParticleSystemRenderer)
                    continue;

                Material[] current = r.sharedMaterials;
                if (current == null || current.Length == 0)
                    continue;

                var replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                {
                    Material chosen = overrideMaterials[s % overrideMaterials.Length];
                    replaced[s] = chosen != null ? chosen : current[s];
                }

                r.sharedMaterials = replaced;
            }
        }

        static void PrepareRenderers(GameObject subject)
        {
            LODGroup[] lods = subject.GetComponentsInChildren<LODGroup>(true);
            for (int i = 0; i < lods.Length; i++)
            {
                if (lods[i] != null)
                    lods[i].ForceLOD(0);
            }

            Renderer[] rends = subject.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null)
                    continue;
                r.enabled = !(r is ParticleSystemRenderer);
            }
        }

        static Bounds CalculateEnabledRendererBounds(GameObject root)
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

        static Sprite LoadSprite(string pngPath)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
            if (sprite != null)
                return sprite;

            UnityEngine.Object[] objs = AssetDatabase.LoadAllAssetsAtPath(pngPath);
            for (int i = 0; i < objs.Length; i++)
            {
                if (objs[i] is Sprite s)
                    return s;
            }

            return null;
        }

        static void ConfigureSpriteImporter(string assetPath)
        {
            var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (ti == null)
                return;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Bilinear;
            ti.maxTextureSize = 512;
            ti.SaveAndReimport();
        }

        static void ApplyLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                ApplyLayerRecursive(t.GetChild(i).gameObject, layer);
        }

        static void EnsureAssetFolder(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath))
                return;
            int idx = assetFolderPath.LastIndexOf('/');
            if (idx <= 0)
                return;
            string parent = assetFolderPath.Substring(0, idx);
            string leaf = assetFolderPath.Substring(idx + 1);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureAssetFolder(parent);
            if (!AssetDatabase.IsValidFolder(assetFolderPath))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        static string AssetPathToSystemPath(string assetPath)
        {
            string rel = assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? assetPath.Substring("Assets/".Length)
                : assetPath;
            return Path.Combine(Application.dataPath, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "mega";
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = false;
                for (int i = 0; i < invalid.Length; i++)
                {
                    if (c == invalid[i])
                    {
                        bad = true;
                        break;
                    }
                }

                sb.Append(bad ? '_' : c);
            }

            return sb.Length > 0 ? sb.ToString() : "mega";
        }
    }
}
#endif
