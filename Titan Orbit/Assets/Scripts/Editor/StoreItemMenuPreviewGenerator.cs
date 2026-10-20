#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Game;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Theatrical 512px thumbs for orbit-store drones, rockets, and mines (TeamA–E).
    /// Writes PNGs under Assets/Prefabs/StoreItems/MenuPreviews and assigns
    /// <see cref="StoreItemPreviewCatalog"/>.
    /// </summary>
    public static class StoreItemMenuPreviewGenerator
    {
        const int PreviewLayer = 30;
        const int RenderSize = 512;
        const string OutputFolder = "Assets/Prefabs/StoreItems/MenuPreviews";
        const string CatalogPath = "Assets/Resources/StoreItemPreviewCatalog.asset";

        enum MaterialPolicy
        {
            /// <summary>Team hull mats on ship meshes; keep authored core / sphere materials.</summary>
            TeamHullKeepCore,
            /// <summary>Bank / authored colors already match the team — do not retint.</summary>
            KeepAuthored,
            /// <summary>Apply one team material to every mesh (Bomb_4 mines).</summary>
            SingleTeamMaterial,
        }

        [MenuItem("Titan Orbit/Store Items/Generate Theatrical Menu Previews")]
        public static void GenerateFromMenu()
        {
            int written = GenerateAll();
            EditorUtility.DisplayDialog(
                "Store Item Previews",
                written > 0
                    ? $"Wrote {written} theatrical thumbs into {OutputFolder}."
                    : "No previews written. Check drone/mine/rocket prefabs.",
                "OK");
        }

        public static int GenerateAll() => Generate(rocketsOnly: false);

        /// <summary>
        /// Rewrites only the rocket thumbs. Ship-hull skins do not match the missile UVs,
        /// so each team gets the authored rocket albedo with its stripe recolored.
        /// </summary>
        public static int GenerateRockets() => Generate(rocketsOnly: true);

        static int Generate(bool rocketsOnly)
        {
            EnsureFolder(OutputFolder);
            StoreItemPreviewCatalog catalog = LoadOrCreateCatalog();
            var variants = BuildTeamVariants();
            var subjects = new[]
            {
                // In-game swarms load Assets/Resources/*Drone.prefab. The copies under Assets/Prefabs are the old meshes.
                (StoreItemType.FighterDrone, "FighterDrone", LoadPrefab("Assets/Resources/FighterDrone.prefab"), MaterialPolicy.TeamHullKeepCore),
                (StoreItemType.ShieldDrone, "ShieldDrone", LoadPrefab("Assets/Resources/ShieldDrone.prefab"), MaterialPolicy.TeamHullKeepCore),
                (StoreItemType.MiningDrone, "MiningDrone", LoadPrefab("Assets/Resources/MiningDrone.prefab"), MaterialPolicy.TeamHullKeepCore),
                // Single team material: the Sci-Fi rocket skin, not AstroEagle hull mats.
                (StoreItemType.SmallRockets, "Rockets", null, MaterialPolicy.SingleTeamMaterial),
                (StoreItemType.SmallMines, "Mines", LoadMinePrefab(), MaterialPolicy.SingleTeamMaterial),
            };

            int written = 0;
            try
            {
                for (int s = 0; s < subjects.Length; s++)
                {
                    var (item, fileBase, prefab, policy) = subjects[s];
                    if (rocketsOnly && item != StoreItemType.SmallRockets)
                        continue;
                    EditorUtility.DisplayProgressBar("Store Item Previews", fileBase, (s + 0.5f) / subjects.Length);

                    var entry = catalog.GetOrCreateEntry(item);
                    if (entry.teamMenuPreviewSprites == null)
                        entry.teamMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();
                    entry.teamMenuPreviewSprites.Clear();
                    Sprite first = null;
                    bool isDrone = StoreItemData.IsDrone(item);

                    for (int v = 0; v < variants.Length; v++)
                    {
                        var variant = variants[v];
                        GameObject capturePrefab = prefab;
                        Material[] mats = variant.materials;
                        TeamId droneTeam = TeamId.None;
                        Material generatedSkin = null;
                        if (item == StoreItemType.SmallRockets)
                        {
                            capturePrefab = LoadRocketBankPrefab(variant.team);
                            generatedSkin = CreateRocketTeamSkin(variant.team);
                            mats = generatedSkin != null ? new[] { generatedSkin } : null;
                        }
                        else if (policy == MaterialPolicy.SingleTeamMaterial)
                        {
                            Material mineMat = LoadMineTeamMaterial(variant.team);
                            mats = mineMat != null ? new[] { mineMat } : null;
                        }
                        else if (isDrone)
                        {
                            droneTeam = TeamManager.ToTeamId(variant.team);
                            mats = null;
                        }

                        try
                        {
                            if (capturePrefab == null)
                                continue;

                            string folder = $"{OutputFolder}/{variant.name}";
                            EnsureFolder(folder);
                            string pngPath = $"{folder}/{fileBase}.png";
                            float standoffMul = item == StoreItemType.SmallMines ? 7.6f : 6.2f;
                            if (!RenderPrefabToPng(
                                    capturePrefab,
                                    pngPath,
                                    mats,
                                    isDrone ? MaterialPolicy.KeepAuthored : policy,
                                    standoffMul,
                                    droneTeam))
                                continue;
                            AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                            ConfigureSpriteImporter(pngPath);
                            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
                            if (sprite == null)
                                continue;
                            entry.teamMenuPreviewSprites.Add(new ShipFamilyTeamMenuPreview
                            {
                                variantName = variant.name,
                                team = variant.team,
                                sprite = sprite,
                            });
                            if (first == null)
                                first = sprite;
                            written++;
                        }
                        finally
                        {
                            DestroyGeneratedSkin(generatedSkin);
                        }
                    }

                    entry.menuPreviewSprite = first;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return written;
        }

        static StoreItemPreviewCatalog LoadOrCreateCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<StoreItemPreviewCatalog>(CatalogPath);
            if (catalog != null)
                return catalog;
            EnsureFolder("Assets/Resources");
            catalog = ScriptableObject.CreateInstance<StoreItemPreviewCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            return catalog;
        }

        static GameObject LoadPrefab(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);

        static GameObject LoadMinePrefab()
        {
            var catalog = Resources.Load<MineCatalog>("MineCatalog");
            if (catalog != null && catalog.visualPrefab != null)
                return catalog.visualPrefab;
            return AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Sci-fi Mines/Prefabs/Bomb_4.prefab");
        }

        static GameObject LoadRocketBankPrefab(TeamManager.Team team)
        {
            // Demo bank rows (RocketRedOBJ) are placeholder spheres. In-flight art is the
            // Sci-Fi Arsenal missile mesh the bank's projectileParticle uses.
            string color = RocketColorToken(team);
            GameObject missile = LoadPrefab(
                $"Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Missiles/Rockets/{color}RocketMissile.prefab");
            if (missile != null)
                return missile;

            int rocketIndex = BulletBankProfileUtility.FindRocketBankIndex();
            var bank = BulletVfxBank.LoadDefault();
            if (bank != null && rocketIndex >= 0)
            {
                GameObject visual = bank.GetProjectileVisualPrefab(rocketIndex, TeamManager.ToTeamId(team));
                if (visual != null)
                    return visual;
            }

            return LoadPrefab("Assets/Prefabs/RocketProjectile.prefab");
        }

        static string RocketColorToken(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return "Red";
                case TeamManager.Team.TeamB: return "Blue";
                case TeamManager.Team.TeamC: return "Green";
                case TeamManager.Team.TeamD: return "Yellow";
                case TeamManager.Team.TeamE: return "Purple";
                default: return "Blue";
            }
        }

        const string RocketMaterialPath =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Materials/Misc/Rocket.mat";

        static Material _rocketSource;
        static Color32[] _rocketAlbedo;
        static int _rocketAlbedoWidth;
        static int _rocketAlbedoHeight;

        /// <summary>
        /// Authored <c>Rocket.mat</c> with the yellow stripe painted in the team color.
        /// Hull atlases (GenericSpaceships / AstroEagle) do not match this mesh, so they
        /// read as a scrambled skin in the gear thumb.
        /// </summary>
        static Material CreateRocketTeamSkin(TeamManager.Team team)
        {
            EnsureRocketAlbedo();
            if (_rocketSource == null)
                return null;

            var skin = new Material(_rocketSource) { name = "RocketSkin_" + RocketColorToken(team) };
            if (_rocketAlbedo == null)
                return skin;

            Color stripe = TeamStripeColor(team);
            var tex = new Texture2D(_rocketAlbedoWidth, _rocketAlbedoHeight, TextureFormat.RGBA32, false, false)
            {
                name = skin.name + "_Albedo",
                wrapMode = TextureWrapMode.Clamp,
            };
            var painted = new Color32[_rocketAlbedo.Length];
            int stripePixels = 0;
            for (int i = 0; i < _rocketAlbedo.Length; i++)
            {
                Color32 src = _rocketAlbedo[i];
                if (!TryRecolorRocketStripe(src, stripe, out Color32 paintedPx))
                {
                    painted[i] = src;
                    continue;
                }

                painted[i] = paintedPx;
                stripePixels++;
            }

            if (stripePixels == 0)
                Debug.LogWarning($"[StoreItemPreview] Rocket stripe recolor matched no pixels for {team}.");

            tex.SetPixels32(painted);
            tex.Apply(true, false);
            skin.SetTexture("_BaseMap", tex);
            skin.SetTexture("_MainTex", tex);
            return skin;
        }

        static void EnsureRocketAlbedo()
        {
            if (_rocketSource != null)
                return;

            _rocketSource = AssetDatabase.LoadAssetAtPath<Material>(RocketMaterialPath);
            if (_rocketSource == null)
            {
                Debug.LogWarning("[StoreItemPreview] Missing Rocket.mat. Rocket thumbs keep the prefab material.");
                return;
            }

            Texture albedo = _rocketSource.HasProperty("_BaseMap")
                ? _rocketSource.GetTexture("_BaseMap")
                : _rocketSource.mainTexture;
            string texPath = albedo != null ? AssetDatabase.GetAssetPath(albedo) : null;
            if (string.IsNullOrEmpty(texPath))
                return;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                return;

            string full = Path.Combine(projectRoot, texPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                return;

            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!decoded.LoadImage(File.ReadAllBytes(full)))
            {
                Object.DestroyImmediate(decoded);
                return;
            }

            _rocketAlbedoWidth = decoded.width;
            _rocketAlbedoHeight = decoded.height;
            _rocketAlbedo = decoded.GetPixels32();
            Object.DestroyImmediate(decoded);
        }

        /// <summary>
        /// Canonical faction color for the rocket stripe. Team D is orange in the rest of the UI.
        /// </summary>
        static Color TeamStripeColor(TeamManager.Team team)
        {
            return TeamManager.ToTeamId(team).ToColor();
        }

        /// <summary>
        /// Yellow bands on <c>scifi_rocket.png</c> become <paramref name="stripe"/>. Gray metal stays.
        /// </summary>
        static bool TryRecolorRocketStripe(Color32 src, Color stripe, out Color32 painted)
        {
            painted = src;
            float r = src.r / 255f;
            float g = src.g / 255f;
            float b = src.b / 255f;
            float max = Mathf.Max(r, Mathf.Max(g, b));
            float min = Mathf.Min(r, Mathf.Min(g, b));
            float sat = max > 0.02f ? (max - min) / max : 0f;
            // The albedo's only saturated paint is the yellow nose cap and body band.
            bool isStripe = sat > 0.28f && r > 0.25f && g > 0.18f && b < r * 0.65f && g > b;
            if (!isStripe)
                return false;

            float brightness = Mathf.Clamp01(max);
            painted = new Color(stripe.r * brightness, stripe.g * brightness, stripe.b * brightness, src.a / 255f);
            return true;
        }

        static void DestroyGeneratedSkin(Material skin)
        {
            if (skin == null || EditorUtility.IsPersistent(skin))
                return;

            Texture tex = skin.HasProperty("_BaseMap") ? skin.GetTexture("_BaseMap") : null;
            Object.DestroyImmediate(skin);
            if (tex != null && !EditorUtility.IsPersistent(tex))
                Object.DestroyImmediate(tex);
        }

        static Material LoadMineTeamMaterial(TeamManager.Team team)
        {
            var mats = MineTeamMaterials.LoadDefault();
            return mats != null ? mats.GetMaterialForTeam(TeamManager.ToTeamId(team)) : null;
        }

        struct TeamVariant
        {
            public string name;
            public TeamManager.Team team;
            public Material[] materials;
        }

        static TeamVariant[] BuildTeamVariants()
        {
            var ordered = new[]
            {
                TeamManager.Team.TeamA, TeamManager.Team.TeamB, TeamManager.Team.TeamC,
                TeamManager.Team.TeamD, TeamManager.Team.TeamE,
            };
            var result = new TeamVariant[ordered.Length];
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition");
            List<ShipFamilyTeamMaterialSet> sets = null;
            ShipFamilyDefinition home = null;
            for (int i = 0; i < guids.Length; i++)
            {
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (def == null || def.teamMaterials == null || def.teamMaterials.Count == 0)
                    continue;
                if (string.Equals(def.familyId, "AstroEagle", System.StringComparison.OrdinalIgnoreCase))
                {
                    home = def;
                    break;
                }
                if (sets == null)
                    sets = def.teamMaterials;
            }

            if (home != null)
                sets = home.teamMaterials;

            for (int i = 0; i < ordered.Length; i++)
            {
                TeamManager.Team team = ordered[i];
                Material[] mats = null;
                if (sets != null)
                {
                    for (int s = 0; s < sets.Count; s++)
                    {
                        if (sets[s] != null && TeamManager.FromTeamId(sets[s].team) == team && sets[s].materials != null)
                        {
                            mats = sets[s].materials.ToArray();
                            break;
                        }
                    }
                }

                result[i] = new TeamVariant { name = team.ToString(), team = team, materials = mats };
            }

            return result;
        }

        static bool RenderPrefabToPng(
            GameObject prefab,
            string assetPath,
            Material[] materials,
            MaterialPolicy policy,
            float standoffMul = 6.2f,
            TeamId droneTeam = TeamId.None)
        {
            // PrefabUtility keeps FBX variants (Bomb_4) wired. Object.Instantiate can drop that mesh.
            var root = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (root == null)
                root = Object.Instantiate(prefab);
            root.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(root, PreviewLayer);
            PrepareCaptureRoot(root);
            if (droneTeam != TeamId.None)
                DroneTeamVisualApplier.Apply(root, droneTeam);
            else
                ApplyMaterials(root, materials, policy);
            root.name = "StoreItemPreviewCapture";

            if (!TryGetVisibleBounds(root, out Bounds wb))
            {
                Debug.LogWarning($"[StoreItemPreview] No visible mesh on {prefab.name}. Skipping {assetPath}.");
                Object.DestroyImmediate(root);
                return false;
            }

            var camGo = new GameObject("StoreItemPreviewCam");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 1 << PreviewLayer;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 120f;
            cam.fieldOfView = 32f;
            cam.allowHDR = false;
            cam.allowMSAA = false;

            float maxExt = Mathf.Max(wb.extents.x, wb.extents.y, wb.extents.z);
            // Extra standoff so the object sits in empty space instead of filling the thumb.
            float standoff = Mathf.Max(0.55f, maxExt * Mathf.Max(4.5f, standoffMul));
            float elev = 26f * Mathf.Deg2Rad;
            float az = 34f * Mathf.Deg2Rad;
            Vector3 look = wb.center + new Vector3(0f, wb.extents.y * 0.08f, 0f);
            Vector3 camPos = look + new Vector3(
                standoff * Mathf.Cos(elev) * Mathf.Sin(az),
                standoff * Mathf.Sin(elev),
                standoff * Mathf.Cos(elev) * Mathf.Cos(az));
            cam.transform.position = camPos;
            cam.transform.LookAt(look, Vector3.up);

            var lightGo = new GameObject("StoreItemPreviewKey");
            lightGo.hideFlags = HideFlags.HideAndDontSave;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.transform.rotation = Quaternion.LookRotation((look - (look + new Vector3(2f, 4f, 3f))).normalized, Vector3.up);
            light.cullingMask = 1 << PreviewLayer;

            Color oldAmb = RenderSettings.ambientLight;
            RenderSettings.ambientLight = new Color(0.32f, 0.35f, 0.42f);
            var rt = RenderTexture.GetTemporary(RenderSize, RenderSize, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            // First Camera.Render after a fresh shader variant can come back black.
            cam.Render();
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(RenderSize, RenderSize, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, RenderSize, RenderSize), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
            RenderSettings.ambientLight = oldAmb;

            string sys = Path.GetFullPath(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sys) ?? ".");
            File.WriteAllBytes(sys, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(lightGo);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(root);
            return true;
        }

        static void PrepareCaptureRoot(GameObject root)
        {
            var particles = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i] == null)
                    continue;
                particles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var particleRenderer = particles[i].GetComponent<ParticleSystemRenderer>();
                if (particleRenderer != null)
                    particleRenderer.enabled = false;
            }

            // Leave authored-off renderers off. The Resources drone prefabs disable the
            // placeholder sphere on the root; turning it back on covers the new ship mesh.
        }

        /// <summary>
        /// World bounds of renderers that will actually draw. Skips disabled placeholder
        /// spheres so the camera frames the ship (or the mine) instead of an empty shell.
        /// </summary>
        static bool TryGetVisibleBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (renderer is ParticleSystemRenderer)
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

            return any && bounds.size.sqrMagnitude > 1e-8f;
        }

        static void ApplyMaterials(GameObject root, Material[] materials, MaterialPolicy policy)
        {
            if (policy == MaterialPolicy.KeepAuthored)
                return;
            if (materials == null || materials.Length == 0)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                if (policy == MaterialPolicy.TeamHullKeepCore && ShouldKeepAuthoredCoreMaterial(renderer))
                    continue;

                var current = renderer.sharedMaterials;
                if (current == null || current.Length == 0)
                    continue;
                var replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                    replaced[s] = materials[s % materials.Length] ?? current[s];
                renderer.sharedMaterials = replaced;
            }
        }

        static bool ShouldKeepAuthoredCoreMaterial(Renderer renderer)
        {
            if (renderer == null)
                return false;

            string name = renderer.gameObject.name;
            if (NameLooksLikeCore(name))
                return true;

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null && filter.sharedMesh.name == "Sphere")
                return true;

            return false;
        }

        static bool NameLooksLikeCore(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.IndexOf("Sphere", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Core", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Orb", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Glow", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Energy", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Inner", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Nucleus", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Crystal", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void ConfigureSpriteImporter(string pngPath)
        {
            var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
            if (importer == null)
                return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;
            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/') ?? "Assets";
            string name = Path.GetFileName(assetFolder);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
#endif
