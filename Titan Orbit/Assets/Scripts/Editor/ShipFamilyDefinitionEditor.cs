using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
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
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Menu Preview Images: writes PNGs to a MenuPreviews folder next to this asset, imports them as Sprites, and assigns each tier's menuPreviewSprite. Re-run anytime after prefab changes.",
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

            // Collect prefab + power score
            var list = new List<(GameObject prefab, float power)>();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ShipComponentAbilityStats stats = SumStatsForPrefab(prefab, def, familyId);
                float power = ComputePowerScore(stats);
                list.Add((prefab, power));
            }

            if (list.Count == 0)
            {
                EditorUtility.DisplayDialog("No Family Components Detected",
                    $"No child transforms with names starting with '{familyId}_' were found in the prefabs under:\n{relativeFolder}",
                    "OK");
                return;
            }

            // Sort by power ascending so weaker ships unlock earlier
            list.Sort((a, b) => a.power.CompareTo(b.power));

            Undo.RecordObject(def, "Build Ship Family Upgrade Tree");

            if (def.upgradeTree == null)
                def.upgradeTree = new List<ShipFamilyChassisTierEntry>();
            else
                def.upgradeTree.Clear();

            int count = list.Count;
            int index = 0;
            int currentLevel = 1;
            int shipsAtCurrentLevel = 1;
            int assignedAtThisLevel = 0;

            for (int i = 0; i < count; i++)
            {
                var (prefab, power) = list[i];
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
                    prefab = prefab,
                    minHomePlanetLevel = currentLevel,
                    powerScore = power
                };
                def.upgradeTree.Add(entry);
                assignedAtThisLevel++;
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>Sum component stats for prefab. Non-weapons: scale by x*y*z. Weapons: fire power/bullet by x*y, fire rate by 1/z.</summary>
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

        private static float ComputePowerScore(ShipComponentAbilityStats s)
        {
            // Heuristic overall power metric: weighted sum of offense, defense, energy, mobility, capacity.
            float offense =
                s.firePower * 2.0f +
                s.firePowerPerLevel * 1.0f +
                s.bulletSpeed * 0.5f +
                s.bulletSpeedPerLevel * 0.25f +
                s.fireRate * 1.0f +
                s.fireRatePerLevel * 0.5f;

            float defense =
                s.healthCap * 0.03f +
                s.healthCapPerLevel * 0.5f +
                s.healthRegen * 1.0f +
                s.healthRegenPerLevel * 1.5f;

            float energy =
                s.energyCap * 0.01f +
                s.energyCapPerLevel * 0.25f +
                s.energyRegen * 0.8f +
                s.energyRegenPerLevel * 1.0f;

            float mobility =
                s.moveSpeed * 0.5f +
                s.moveSpeedPerLevel * 0.8f +
                s.turnSpeed * 0.6f +
                s.turnSpeedPerLevel * 0.9f;

            float capacity =
                s.maxGems * 0.01f +
                s.maxGemsPerLevel * 0.2f +
                s.maxPeople * 0.5f +
                s.maxPeoplePerLevel * 0.8f;

            return offense + defense + energy + mobility + capacity;
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
    }
}

