using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Custom inspector for ShipFamilyDefinition.
    /// Adds a button to scan a prefab folder, detect components by name (Family_Type_Version),
    /// and auto-populate entries with heuristic stats.
    /// </summary>
    [CustomEditor(typeof(ShipFamilyDefinition))]
    public class ShipFamilyDefinitionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var def = (ShipFamilyDefinition)target;
            if (def == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Bullet Prefab Index: index into CombatSystem's Bullet Prefab Bank (0 = first). The list of bullets lives only on CombatSystem; change the index here to pick which bullet this family uses.",
                MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Auto Populate From Prefab Folder", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scans all prefabs in a folder for child names like 'AstroEagle_Engine_2'. " +
                "Family = prefix (AstroEagle), Type = Engine, Version = 2. " +
                "Each unique 'Type[_Version]' becomes a component entry with suggested stats.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(def.familyId)))
            {
                if (GUILayout.Button("Scan Folder And Auto-Populate Components"))
                {
                    ScanFolderAndPopulate(def);
                }

                if (GUILayout.Button("Build Upgrade Tree From Folder"))
                {
                    BuildUpgradeTreeFromFolder(def);
                }

                if (GUILayout.Button("Add Ship Family Stats Preview To All Upgrade Tree Prefabs"))
                {
                    AddShipFamilyStatsPreviewToUpgradeTreePrefabs(def);
                }

                if (GUILayout.Button("Generate Menu Preview Images (Top-Down)"))
                {
                    ShipFamilyMenuPreviewGenerator.GenerateForFamily(def);
                }

                if (GUILayout.Button("Auto-Detect Team Materials From Upgrade Tree (5 Teams)"))
                {
                    AutoDetectTeamMaterialsFromUpgradeTree(def);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Menu Preview Images: writes PNGs to MenuPreviews/<variant>/ next to this asset, imports them as Sprites, and assigns each tier's teamMenuPreviewSprites (plus legacy menuPreviewSprite). Variants come from ShipFamilyDefinition Team Materials. Re-run anytime after prefab/material changes.",
                MessageType.None);
        }

        /// <summary>Adds ShipFamilyStatsPreview to each prefab in the upgrade tree if missing; assigns this definition as Ship Family.</summary>
        private static void AddShipFamilyStatsPreviewToUpgradeTreePrefabs(ShipFamilyDefinition def)
        {
            if (def == null || def.upgradeTree == null || def.upgradeTree.Count == 0)
            {
                EditorUtility.DisplayDialog("No Prefabs", "Upgrade tree is empty. Build the upgrade tree first.", "OK");
                return;
            }
            int added = 0;
            int updated = 0;
            foreach (var entry in def.upgradeTree)
            {
                if (entry?.prefab == null) continue;
                string path = AssetDatabase.GetAssetPath(entry.prefab);
                if (string.IsNullOrEmpty(path)) continue;
                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                if (contents == null) continue;
                try
                {
                    var preview = contents.GetComponent<ShipFamilyStatsPreview>();
                    if (preview == null)
                    {
                        preview = contents.AddComponent<ShipFamilyStatsPreview>();
                        added++;
                    }
                    else
                        updated++;
                    var so = new SerializedObject(preview);
                    so.FindProperty("shipFamily").objectReferenceValue = def;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Done", $"Added ShipFamilyStatsPreview to {added} prefab(s), updated Ship Family on {updated} existing. All upgrade tree prefabs now use this definition for summed stats.", "OK");
        }

        private static void ScanFolderAndPopulate(ShipFamilyDefinition def)
        {
            string folder = EditorUtility.OpenFolderPanel("Select Prefab Folder", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder))
                return;

            if (!folder.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Invalid Folder",
                    "Folder must be inside the project's Assets folder.", "OK");
                return;
            }

            string relativeFolder = "Assets" + folder.Substring(Application.dataPath.Length);

            string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { relativeFolder });
            if (guids == null || guids.Length == 0)
            {
                EditorUtility.DisplayDialog("No Prefabs Found",
                    $"No prefabs found under folder:\n{relativeFolder}", "OK");
                return;
            }

            string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
            if (string.IsNullOrEmpty(familyId))
            {
                EditorUtility.DisplayDialog("Missing Family Id",
                    "Please set 'familyId' on the ShipFamilyDefinition before scanning.", "OK");
                return;
            }

            // componentId -> (type, version)
            var componentMap = new Dictionary<string, (string type, int version)>(StringComparer.OrdinalIgnoreCase);

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var transforms = prefab.GetComponentsInChildren<Transform>(true);
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    string name = t.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string rest = name.Substring(familyId.Length + 1); // everything after "Family_"
                    if (string.IsNullOrWhiteSpace(rest))
                        continue;

                    // Example rest: "Engine_2", "Cockpit", "Wing_3_L"
                    string[] parts = rest.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    string type = parts[0];
                    int version = 1;

                    // Try to find an integer in the remaining segments to use as version
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (int.TryParse(parts[i], out int v))
                        {
                            version = Mathf.Max(1, v);
                            break;
                        }
                    }

                    // Use full "rest" as componentId so it matches the substring used in ShipFamilyStatsPreview
                    string componentId = rest;

                    if (!componentMap.ContainsKey(componentId))
                    {
                        componentMap[componentId] = (type, version);
                    }
                }
            }

            if (componentMap.Count == 0)
            {
                EditorUtility.DisplayDialog("No Components Detected",
                    $"No child transforms with names starting with '{familyId}_' were found in prefabs under:\n{relativeFolder}",
                    "OK");
                return;
            }

            Undo.RecordObject(def, "Auto-Populate Ship Family Components");

            if (def.components == null)
                def.components = new List<ShipFamilyComponentEntry>();
            else
                def.components.Clear();

            foreach (var kvp in componentMap)
            {
                string componentId = kvp.Key;
                string type = kvp.Value.type;
                int version = kvp.Value.version;

                var entry = new ShipFamilyComponentEntry
                {
                    componentId = componentId,
                    displayName = $"{type} {version}".Trim()
                };

                entry.stats = SuggestStatsForComponent(type, version);
                def.components.Add(entry);
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void BuildUpgradeTreeFromFolder(ShipFamilyDefinition def)
        {
            string startPath = Path.Combine(Application.dataPath, "Prefabs/Ships/" + def.familyId);
            string folder = EditorUtility.OpenFolderPanel("Select Family Prefab Folder", startPath, "");
            if (string.IsNullOrEmpty(folder))
                return;

            if (!folder.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Invalid Folder",
                    "Folder must be inside the project's Assets folder.", "OK");
                return;
            }

            string relativeFolder = "Assets" + folder.Substring(Application.dataPath.Length);
            string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { relativeFolder });
            if (guids == null || guids.Length == 0)
            {
                EditorUtility.DisplayDialog("No Prefabs Found",
                    $"No prefabs found under folder:\n{relativeFolder}", "OK");
                return;
            }

            string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
            if (string.IsNullOrEmpty(familyId))
            {
                EditorUtility.DisplayDialog("Missing Family Id",
                    "Please set 'familyId' on the ShipFamilyDefinition before building the upgrade tree.", "OK");
                return;
            }

            // Collect prefab + power score (+ breakdown for inspector)
            var list = new List<(GameObject prefab, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ShipComponentAbilityStats stats = SumStatsForPrefab(prefab, def, familyId);
                ShipFamilyPowerScoreBreakdown breakdown = ComputePowerScoreBreakdown(stats);
                float power = breakdown.Total;
                list.Add((prefab, power, breakdown));
            }

            if (list.Count == 0)
            {
                EditorUtility.DisplayDialog("No Family Components Detected",
                    $"No child transforms with names starting with '{familyId}_' were found in the prefabs under:\n{relativeFolder}",
                    "OK");
                return;
            }

            // Weaker ships unlock earlier (global order by power). Within each planet tier row, order left→right
            // on the O–D–E–M–C spectrum (offense-heavy left, capacity-heavy right) to match the orbit tree layout.
            list.Sort((a, b) => a.power.CompareTo(b.power));
            var orderedForTree = new List<(GameObject prefab, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
            int listIdx = 0;
            int chunkSize = 1;
            while (listIdx < list.Count)
            {
                var chunk = new List<(GameObject prefab, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
                for (int c = 0; c < chunkSize && listIdx < list.Count; c++)
                    chunk.Add(list[listIdx++]);
                chunk.Sort(CompareOdEmcSpectrumInTier);
                orderedForTree.AddRange(chunk);
                chunkSize++;
            }

            Undo.RecordObject(def, "Build Ship Family Upgrade Tree");

            if (def.upgradeTree == null)
                def.upgradeTree = new List<ShipFamilyChassisTierEntry>();
            else
                def.upgradeTree.Clear();

            int count = orderedForTree.Count;
            int index = 0;
            int currentLevel = 1;
            int shipsAtCurrentLevel = 1;
            int assignedAtThisLevel = 0;

            for (int i = 0; i < count; i++)
            {
                var (prefab, power, breakdown) = orderedForTree[i];
                if (prefab == null) continue;

                if (assignedAtThisLevel >= shipsAtCurrentLevel)
                {
                    currentLevel++;
                    shipsAtCurrentLevel++;
                    assignedAtThisLevel = 0;
                }

                index++;
                string chassisId = $"{familyId}_{index:00}";

                var entry = new ShipFamilyChassisTierEntry
                {
                    chassisId = chassisId,
                    upgradeTreeShipName = GetUpgradeTreeShipNameFromPrefabName(prefab.name),
                    prefab = prefab,
                    minHomePlanetLevel = currentLevel,
                    powerScore = power,
                    powerScoreBreakdown = breakdown
                };
                def.upgradeTree.Add(entry);
                assignedAtThisLevel++;
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>Sum component stats for prefab. Non-weapons: scale by average(x,y,z). Weapons: fire power by average(x,y), fire rate by 1/z; bullet speed not scaled by part size.</summary>
        private static ShipComponentAbilityStats SumStatsForPrefab(GameObject prefab, ShipFamilyDefinition def, string familyId)
        {
            var total = new ShipComponentAbilityStats();
            if (prefab == null || def == null) return total;

            var transforms = prefab.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (t == null) continue;
                string name = t.name;
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string componentId = name.Substring(familyId.Length + 1);
                if (string.IsNullOrWhiteSpace(componentId))
                    continue;

                if (def.TryGetStatsForComponent(componentId, out var stats))
                {
                    ShipComponentAbilityStats scaled = ShipComponentAbilityStats.ScaleStatsByTransform(stats, t, componentId);
                    total.AddInPlace(scaled);
                }
            }

            return total;
        }

        /// <summary>Second segment after splitting prefab root name on '_' (e.g. AstroEagle_Thumper → Thumper).</summary>
        private static string GetUpgradeTreeShipNameFromPrefabName(string prefabRootName)
        {
            if (string.IsNullOrEmpty(prefabRootName))
                return string.Empty;
            string[] parts = prefabRootName.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return string.Empty;
            return parts[1];
        }

        /// <summary>0 = offense-heavy, 4 = capacity-heavy; weighted mean of category indices O/D/E/M/C.</summary>
        private static float OdEmcAxisPosition(ShipFamilyPowerScoreBreakdown x)
        {
            float s = x.offense + x.defense + x.energy + x.mobility + x.capacity;
            if (s <= 0.0001f) return 2f;
            return (0f * x.offense + 1f * x.defense + 2f * x.energy + 3f * x.mobility + 4f * x.capacity) / s;
        }

        /// <summary>Left branch = lower axis (more O); right = higher (more C). Tie-break: offense desc, capacity asc.</summary>
        private static int CompareOdEmcSpectrumInTier(
            (GameObject prefab, float power, ShipFamilyPowerScoreBreakdown breakdown) a,
            (GameObject prefab, float power, ShipFamilyPowerScoreBreakdown breakdown) b)
        {
            float pa = OdEmcAxisPosition(a.breakdown);
            float pb = OdEmcAxisPosition(b.breakdown);
            int cmp = pa.CompareTo(pb);
            if (cmp != 0) return cmp;
            cmp = b.breakdown.offense.CompareTo(a.breakdown.offense);
            if (cmp != 0) return cmp;
            return a.breakdown.capacity.CompareTo(b.breakdown.capacity);
        }

        private static ShipFamilyPowerScoreBreakdown ComputePowerScoreBreakdown(ShipComponentAbilityStats s)
        {
            // Heuristic overall power metric: weighted sum of offense, defense, energy, mobility, capacity.
            return new ShipFamilyPowerScoreBreakdown
            {
                offense =
                    s.firePower * 2.0f +
                    s.firePowerPerLevel * 1.0f +
                    s.bulletSpeed * 0.5f +
                    s.bulletSpeedPerLevel * 0.25f +
                    s.fireRate * 1.0f +
                    s.fireRatePerLevel * 0.5f,
                defense =
                    s.healthCap * 0.03f +
                    s.healthCapPerLevel * 0.5f +
                    s.healthRegen * 1.0f +
                    s.healthRegenPerLevel * 1.5f,
                energy =
                    s.energyCap * 0.01f +
                    s.energyCapPerLevel * 0.25f +
                    s.energyRegen * 0.8f +
                    s.energyRegenPerLevel * 1.0f,
                mobility =
                    s.moveSpeed * 0.5f +
                    s.moveSpeedPerLevel * 0.8f +
                    s.turnSpeed * 0.6f +
                    s.turnSpeedPerLevel * 0.9f,
                capacity =
                    s.maxGems * 0.01f +
                    s.maxGemsPerLevel * 0.2f +
                    s.maxPeople * 0.5f +
                    s.maxPeoplePerLevel * 0.8f
            };
        }

        private static ShipComponentAbilityStats SuggestStatsForComponent(string type, int version)
        {
            // Simple heuristic: base values per type, scaled by version.
            float v = Mathf.Max(1, version);
            var stats = new ShipComponentAbilityStats();

            switch (type)
            {
                case "Cockpit":
                    // Core survivability + crew + energy
                    stats.healthCap = 15f * v;
                    stats.healthCapPerLevel = 3f * v;
                    stats.healthRegen = 5f * v;
                    stats.healthRegenPerLevel = 1f * v;

                    stats.energyCap = 5f * v;
                    stats.energyCapPerLevel = 1.5f * v;
                    stats.energyRegen = 3f * v;
                    stats.energyRegenPerLevel = 0.5f * v;

                    stats.maxGems = 5f * v;
                    stats.maxGemsPerLevel = 1f * v;
                    stats.maxPeople = 5f * v;
                    stats.maxPeoplePerLevel = 1f * v;
                    break;

                case "Wing":
                    // Lateral control + cargo + a bit of sustain
                    stats.maxGems = 10f * v;
                    stats.maxGemsPerLevel = 2f * v;

                    stats.turnSpeed = 4f * v;
                    stats.turnSpeedPerLevel = 1f * v;

                    stats.healthCap = 5f * v;
                    stats.healthCapPerLevel = 1.5f * v;
                    stats.healthRegen = 1.5f * v;
                    stats.healthRegenPerLevel = 0.5f * v;

                    stats.energyRegen = 1.5f * v;
                    stats.energyRegenPerLevel = 0.5f * v;
                    break;

                case "Engine":
                    // Forward thrust / speed
                    stats.moveSpeed = 8f * v;
                    stats.moveSpeedPerLevel = 2f * v;

                    stats.healthCap = 2f * v;
                    stats.healthCapPerLevel = 0.5f * v;
                    break;

                case "Thruster":
                    // Turning control
                    stats.turnSpeed = 8f * v;
                    stats.turnSpeedPerLevel = 2f * v;

                    stats.healthCap = 2f * v;
                    stats.healthCapPerLevel = 0.5f * v;
                    break;

                case "Fin":
                    // Fins: primarily fine-grained turning control, lighter than thrusters
                    stats.turnSpeed = 5f * v;
                    stats.turnSpeedPerLevel = 1.5f * v;

                    stats.healthCap = 1.5f * v;
                    stats.healthCapPerLevel = 0.4f * v;
                    break;

                case "Weapon":
                    // Pure offense
                    stats.firePower = 5f * v;
                    stats.firePowerPerLevel = 1.5f * v;

                    stats.bulletSpeed = 5f * v;
                    stats.bulletSpeedPerLevel = 1f * v;

                    stats.fireRate = 1.5f * v;          // shots per second
                    stats.fireRatePerLevel = 0.25f * v;
                    break;

                case "Part":
                case "Hull":
                    // General survivability + cargo
                    stats.healthCap = 10f * v;
                    stats.healthCapPerLevel = 2f * v;
                    stats.healthRegen = 2f * v;
                    stats.healthRegenPerLevel = 0.75f * v;

                    stats.maxGems = 5f * v;
                    stats.maxGemsPerLevel = 1.5f * v;
                    stats.maxPeople = 2f * v;
                    stats.maxPeoplePerLevel = 0.5f * v;
                    break;

                default:
                    // Fallback: small balanced bonus
                    stats.healthCap = 5f * v;
                    stats.healthCapPerLevel = 1f * v;
                    stats.energyCap = 3f * v;
                    stats.energyCapPerLevel = 0.5f * v;
                    stats.maxGems = 3f * v;
                    stats.maxGemsPerLevel = 0.5f * v;
                    break;
            }

            return stats;
        }

        private static void AutoDetectTeamMaterialsFromUpgradeTree(ShipFamilyDefinition def)
        {
            if (def == null || def.upgradeTree == null || def.upgradeTree.Count == 0)
            {
                EditorUtility.DisplayDialog("No Upgrade Tree", "Upgrade tree is empty. Build the upgrade tree first.", "OK");
                return;
            }

            // Team order and color keyword mapping are kept explicit and deterministic.
            var teamSpecs = new[]
            {
                new TeamMaterialSpec(TeamManager.Team.TeamA, "Red",    new[] { "red", "teama", "team_a", "team a" }),
                new TeamMaterialSpec(TeamManager.Team.TeamB, "Blue",   new[] { "blue", "teamb", "team_b", "team b" }),
                new TeamMaterialSpec(TeamManager.Team.TeamC, "Green",  new[] { "green", "teamc", "team_c", "team c" }),
                new TeamMaterialSpec(TeamManager.Team.TeamD, "Orange", new[] { "orange", "teamd", "team_d", "team d" }),
                new TeamMaterialSpec(TeamManager.Team.TeamE, "Purple", new[] { "purple", "violet", "teame", "team_e", "team e" }),
            };

            var detectedByTeam = new Dictionary<TeamManager.Team, Material>();
            var detectedLocationByTeam = new Dictionary<TeamManager.Team, string>();
            var seenMaterialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scannedPrefabs = 0;
            int scannedSlots = 0;

            foreach (var entry in def.upgradeTree)
            {
                if (entry?.prefab == null)
                    continue;

                string prefabPath = AssetDatabase.GetAssetPath(entry.prefab);
                if (string.IsNullOrEmpty(prefabPath))
                    continue;

                GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null)
                    continue;

                scannedPrefabs++;
                try
                {
                    Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                    foreach (Renderer renderer in renderers)
                    {
                        if (renderer == null) continue;
                        if (renderer is ParticleSystemRenderer) continue;

                        Material[] mats = renderer.sharedMaterials;
                        if (mats == null || mats.Length == 0) continue;

                        for (int slot = 0; slot < mats.Length; slot++)
                        {
                            Material mat = mats[slot];
                            if (mat == null) continue;
                            scannedSlots++;

                            string matPath = AssetDatabase.GetAssetPath(mat);
                            string uniqueKey = string.IsNullOrEmpty(matPath) ? mat.name : matPath;
                            if (seenMaterialPaths.Contains(uniqueKey))
                                continue;
                            seenMaterialPaths.Add(uniqueKey);

                            string token = BuildMaterialSearchToken(mat, matPath);
                            if (string.IsNullOrEmpty(token))
                                continue;

                            for (int i = 0; i < teamSpecs.Length; i++)
                            {
                                TeamMaterialSpec spec = teamSpecs[i];
                                if (detectedByTeam.ContainsKey(spec.team))
                                    continue;
                                if (!ContainsAnyKeyword(token, spec.keywords))
                                    continue;

                                detectedByTeam[spec.team] = mat;
                                detectedLocationByTeam[spec.team] = $"{entry.prefab.name}/{GetTransformPath(renderer.transform, root.transform)} [slot {slot}]";
                            }
                        }
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            Undo.RecordObject(def, "Auto-Detect Team Materials From Upgrade Tree");
            def.teamMaterials ??= new List<ShipFamilyTeamMaterialSet>();
            def.teamMaterials.Clear();

            for (int i = 0; i < teamSpecs.Length; i++)
            {
                TeamMaterialSpec spec = teamSpecs[i];
                var set = new ShipFamilyTeamMaterialSet
                {
                    team = spec.team,
                    variantName = spec.variantName,
                    materials = new List<Material>()
                };

                if (detectedByTeam.TryGetValue(spec.team, out Material found) && found != null)
                    set.materials.Add(found);

                def.teamMaterials.Add(set);
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var report = new StringBuilder();
            report.AppendLine($"Scanned {scannedPrefabs} upgrade-tree prefab(s), {scannedSlots} material slot(s).");
            report.AppendLine("Team material assignment:");
            for (int i = 0; i < teamSpecs.Length; i++)
            {
                TeamMaterialSpec spec = teamSpecs[i];
                if (detectedByTeam.TryGetValue(spec.team, out Material found) && found != null)
                {
                    string location = detectedLocationByTeam.TryGetValue(spec.team, out string loc) ? loc : "(location unknown)";
                    report.AppendLine($"- {spec.team} ({spec.variantName}): {found.name} @ {location}");
                }
                else
                {
                    report.AppendLine($"- {spec.team} ({spec.variantName}): no material name match found");
                }
            }

            EditorUtility.DisplayDialog("Auto-Detect Team Materials", report.ToString(), "OK");
        }

        private static string BuildMaterialSearchToken(Material material, string assetPath)
        {
            if (material == null)
                return string.Empty;

            string matName = material.name ?? string.Empty;
            string fileName = string.IsNullOrEmpty(assetPath) ? string.Empty : Path.GetFileNameWithoutExtension(assetPath);
            return (matName + " " + fileName).ToLowerInvariant();
        }

        private static bool ContainsAnyKeyword(string text, IReadOnlyList<string> keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null)
                return false;
            for (int i = 0; i < keywords.Count; i++)
            {
                string k = keywords[i];
                if (string.IsNullOrEmpty(k)) continue;
                if (text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string GetTransformPath(Transform current, Transform root)
        {
            if (current == null)
                return "(null)";
            if (root == null || current == root)
                return current.name;

            var names = new Stack<string>();
            Transform t = current;
            while (t != null && t != root)
            {
                names.Push(t.name);
                t = t.parent;
            }

            if (t == root)
                names.Push(root.name);

            return string.Join("/", names);
        }

        private readonly struct TeamMaterialSpec
        {
            public readonly TeamManager.Team team;
            public readonly string variantName;
            public readonly string[] keywords;

            public TeamMaterialSpec(TeamManager.Team team, string variantName, string[] keywords)
            {
                this.team = team;
                this.variantName = variantName;
                this.keywords = keywords;
            }
        }
    }
}

