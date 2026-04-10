using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Custom inspector for ShipFamilyDefinition.
    /// Adds a button to scan a prefab folder, detect components by name (FamilyId_ComponentSuffix),
    /// and auto-populate entries with heuristic stats (part type inferred from keywords in the suffix, e.g. Weapon1, weapon(1)).
    /// </summary>
    [CustomEditor(typeof(ShipFamilyDefinition))]
    public class ShipFamilyDefinitionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var def = target as ShipFamilyDefinition;

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Create New Card Deck", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Same as Titan Orbit → Cards → Build Scaled Astro Eagle Deck: writes CardData assets, builds the scaled deck for this Family Id, and assigns Upgrade Card Deck.",
                new GUIStyle(EditorStyles.miniLabel) { wordWrap = true });
            using (new EditorGUI.DisabledScope(def == null || string.IsNullOrWhiteSpace(def.familyId)))
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.55f, 0.82f, 1f, 1f);
                if (GUILayout.Button("Create New Card Deck", GUILayout.Height(34)))
                {
                    if (def != null)
                        CardDeckScaledAssetGenerator.BuildScaledDeckForFamily(def, interactiveDialogs: true);
                }
                GUI.backgroundColor = prev;
            }
            if (def != null && string.IsNullOrWhiteSpace(def.familyId))
                EditorGUILayout.HelpBox("Set Family Id on this asset to enable Create New Card Deck.", MessageType.Warning);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            bool serializedChanged = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            if (serializedChanged && def != null)
                ShipFamilyStatsPreviewLiveRefresh.OnShipFamilyDefinitionSerializedChanged(def);

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

                if (GUILayout.Button("Generate/Sync Balance Profile From Folder"))
                {
                    GenerateOrSyncBalanceProfileFromFolder(def);
                }

                if (GUILayout.Button("Export Canonical Component Inventory (CSV)"))
                {
                    ExportCanonicalComponentInventory(def);
                }

                if (GUILayout.Button("Build Upgrade Tree From Folder"))
                {
                    BuildUpgradeTreeFromFolder(def);
                }

                if (GUILayout.Button("Resort Upgrade Tree & Recalculate Power Scores"))
                {
                    ResortUpgradeTreeAndRecalculateStats(def);
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
                "Resort Upgrade Tree: recomputes power scores from the current component table and prefab scales, then reorders tiers like Build From Folder (power + orbit layout). Keeps each tier's prefab, chassisId, display name, and menu sprites — use this after stat tweaks instead of rebuilding.",
                MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Menu Preview Images: writes PNGs to MenuPreviews/<variant>/ next to this asset, imports them as Sprites, and assigns each tier's teamMenuPreviewSprites (plus legacy menuPreviewSprite). Variants come from ShipFamilyDefinition Team Materials. Re-run anytime after prefab/material changes.",
                MessageType.None);

            EditorGUILayout.Space(10);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Upgrade Card Deck", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Optional: empty deck asset next to this file for hand-authored cards, or prefab-scanned pool (different folder than Create New Card Deck above).",
                new GUIStyle(EditorStyles.miniLabel) { wordWrap = true });

            if (GUILayout.Button("Create empty Card Deck Definition & assign", GUILayout.MinHeight(26)))
                CreateEmptyCardDeckAndAssign(def);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(def.familyId)))
            {
                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.78f, 0.98f, 0.82f, 1f);
                if (GUILayout.Button("Generate card pool — from upgrade tree prefabs", GUILayout.MinHeight(26)))
                    CardDeckFromPrefabStatsGenerator.BuildPrefabDerivedDeckForFamily(def, interactiveDialogs: true);
                GUI.backgroundColor = prevBg;
            }

            EditorGUILayout.EndVertical();
        }

        private static void CreateEmptyCardDeckAndAssign(ShipFamilyDefinition def)
        {
            if (def == null)
                return;

            string familyAssetPath = AssetDatabase.GetAssetPath(def);
            if (string.IsNullOrEmpty(familyAssetPath))
            {
                EditorUtility.DisplayDialog("Upgrade Card Deck", "Save the Ship Family Definition asset to disk first.", "OK");
                return;
            }

            string dir = Path.GetDirectoryName(familyAssetPath)?.Replace('\\', '/') ?? "Assets";
            string baseName = Path.GetFileNameWithoutExtension(familyAssetPath);
            string deckPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{baseName}_CardDeck.asset");

            var deck = ScriptableObject.CreateInstance<CardDeckDefinition>();
            deck.deckId = string.IsNullOrWhiteSpace(def.familyId) ? baseName + "Deck" : def.familyId.Trim() + "Deck";
            deck.cards = new List<CardData>();

            AssetDatabase.CreateAsset(deck, deckPath);
            Undo.RecordObject(def, "Create Card Deck Definition");
            def.upgradeCardDeck = deck;
            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(deck);
            EditorUtility.DisplayDialog("Upgrade Card Deck", $"Created {deckPath} and assigned Upgrade Card Deck.", "OK");
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

            // canonical componentId -> canonical metadata
            var componentMap = new Dictionary<string, CanonicalComponentScanData>(StringComparer.OrdinalIgnoreCase);

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var transforms = prefab.GetComponentsInChildren<Transform>(true);
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    if (t == prefab.transform) continue; // Exclude prefab root (ship object), scan only child components.
                    string name = t.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string rest = name.Substring(familyId.Length + 1); // everything after "Family_"
                    if (string.IsNullOrWhiteSpace(rest))
                        continue;

                    string canonicalId = ShipFamilyComponentBalanceProfile.NormalizeComponentId(rest);
                    if (string.IsNullOrWhiteSpace(canonicalId))
                        continue;

                    string type = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(canonicalId);
                    int version = ExtractFirstVersionNumberFromComponentRest(canonicalId);
                    if (!componentMap.TryGetValue(canonicalId, out CanonicalComponentScanData data))
                    {
                        data = new CanonicalComponentScanData(canonicalId, type, version);
                        componentMap[canonicalId] = data;
                    }
                    data.aliases.Add(rest);
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
                string type = kvp.Value.partType;
                int version = kvp.Value.version;

                var entry = new ShipFamilyComponentEntry
                {
                    componentId = componentId,
                    displayName = $"{type} {version}".Trim()
                };

                if (def.componentBalanceProfile != null &&
                    def.componentBalanceProfile.TryGetStats(componentId, type, out ShipComponentAbilityStats profileStats))
                {
                    entry.stats = profileStats;
                }
                else
                {
                    entry.stats = SuggestStatsForComponent(componentId, type, version);
                }
                def.components.Add(entry);
            }

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>First integer in the suffix (e.g. Wing_3_L → 3, Weapon1 → 1); 1 if none.</summary>
        private static int ExtractFirstVersionNumberFromComponentRest(string rest)
        {
            if (string.IsNullOrEmpty(rest)) return 1;
            Match m = Regex.Match(rest, @"\d+");
            if (!m.Success) return 1;
            return int.TryParse(m.Value, out int v) ? Mathf.Max(1, v) : 1;
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
                ShipFamilyPowerScoreBreakdown breakdown = ShipFamilyPowerScoreBreakdown.FromSummedShipStats(stats);
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
                chunk.Sort((a, b) => CompareOdEmcSpectrumByBreakdown(a.breakdown, b.breakdown));
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

        /// <summary>
        /// Reorders existing upgrade-tree entries using the same rules as <see cref="BuildUpgradeTreeFromFolder"/> (global power sort, then O–D–E–M–C within triangular tiers).
        /// Refreshes <see cref="ShipFamilyChassisTierEntry.powerScore"/> and <see cref="ShipFamilyChassisTierEntry.powerScoreBreakdown"/> from prefabs and <paramref name="def"/>'s component stats.
        /// Preserves prefab references, chassisId, names, and menu sprites so designers need not rebuild after balance edits.
        /// </summary>
        private static void ResortUpgradeTreeAndRecalculateStats(ShipFamilyDefinition def)
        {
            if (def == null)
                return;

            string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
            if (string.IsNullOrEmpty(familyId))
            {
                EditorUtility.DisplayDialog("Missing Family Id",
                    "Please set 'familyId' on the ShipFamilyDefinition before resorting the upgrade tree.", "OK");
                return;
            }

            if (def.upgradeTree == null || def.upgradeTree.Count == 0)
            {
                EditorUtility.DisplayDialog("No Upgrade Tree", "Upgrade tree is empty. Build the upgrade tree from a folder first.", "OK");
                return;
            }

            var withPrefab = new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
            var noPrefab = new List<ShipFamilyChassisTierEntry>();

            foreach (var tier in def.upgradeTree)
            {
                if (tier == null)
                    continue;
                if (tier.prefab == null)
                {
                    noPrefab.Add(tier);
                    continue;
                }

                ShipComponentAbilityStats stats = SumStatsForPrefab(tier.prefab, def, familyId);
                ShipFamilyPowerScoreBreakdown breakdown = ShipFamilyPowerScoreBreakdown.FromSummedShipStats(stats);
                float power = breakdown.Total;
                withPrefab.Add((tier, power, breakdown));
            }

            if (withPrefab.Count == 0)
            {
                EditorUtility.DisplayDialog("No Prefabs",
                    "No upgrade-tree entries have a prefab assigned; nothing to resort.", "OK");
                return;
            }

            withPrefab.Sort((a, b) => a.power.CompareTo(b.power));
            var orderedForTree = new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
            int listIdx = 0;
            int chunkSize = 1;
            while (listIdx < withPrefab.Count)
            {
                var chunk = new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
                for (int c = 0; c < chunkSize && listIdx < withPrefab.Count; c++)
                    chunk.Add(withPrefab[listIdx++]);
                chunk.Sort((a, b) => CompareOdEmcSpectrumByBreakdown(a.breakdown, b.breakdown));
                orderedForTree.AddRange(chunk);
                chunkSize++;
            }

            Undo.RecordObject(def, "Resort Ship Family Upgrade Tree");

            var newTree = new List<ShipFamilyChassisTierEntry>(orderedForTree.Count + noPrefab.Count);
            int currentLevel = 1;
            int shipsAtCurrentLevel = 1;
            int assignedAtThisLevel = 0;

            for (int i = 0; i < orderedForTree.Count; i++)
            {
                var (entry, power, breakdown) = orderedForTree[i];
                if (assignedAtThisLevel >= shipsAtCurrentLevel)
                {
                    currentLevel++;
                    shipsAtCurrentLevel++;
                    assignedAtThisLevel = 0;
                }

                entry.powerScore = power;
                entry.powerScoreBreakdown = breakdown;
                entry.minHomePlanetLevel = currentLevel;
                newTree.Add(entry);
                assignedAtThisLevel++;
            }

            for (int i = 0; i < noPrefab.Count; i++)
                newTree.Add(noPrefab[i]);

            def.upgradeTree = newTree;

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (noPrefab.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    "Resort Upgrade Tree",
                    $"Resorted {orderedForTree.Count} tier(s) with prefabs. {noPrefab.Count} entr(y/ies) with no prefab were left at the end of the list unchanged.",
                    "OK");
            }
        }

        /// <summary>Sum component stats for prefab. Non-weapons: scale by average(x,y,z). Weapons: fire power by average(x,y), fire rate by 1/z; bullet speed not scaled by part size.</summary>
        private static ShipComponentAbilityStats SumStatsForPrefab(GameObject prefab, ShipFamilyDefinition def, string familyId)
        {
            return ShipFamilyUpgradeTreeStatScanner.SumStatsUnderRoot(prefab, def, familyId);
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
        private static int CompareOdEmcSpectrumByBreakdown(ShipFamilyPowerScoreBreakdown a, ShipFamilyPowerScoreBreakdown b)
        {
            float pa = OdEmcAxisPosition(a);
            float pb = OdEmcAxisPosition(b);
            int cmp = pa.CompareTo(pb);
            if (cmp != 0) return cmp;
            cmp = b.offense.CompareTo(a.offense);
            if (cmp != 0) return cmp;
            return a.capacity.CompareTo(b.capacity);
        }

        private static ShipComponentAbilityStats SuggestStatsForComponent(string componentId, string type, int version)
        {
            // Rebalanced heuristics:
            // - Weapons own offense + energy.
            // - Engines and thrusters both contribute move speed + acceleration (matches runtime: max speed from best part, thrust from sum).
            //   Thrusters also add turn speed; engines do not.
            // - Wings drive gem capacity; cockpits drive people + base ramming.
            float v = Mathf.Max(1, version);
            var stats = new ShipComponentAbilityStats();

            switch (type)
            {
                case "Cockpit":
                    stats.healthCap = 10f * v;
                    stats.healthCapPerLevel = 2f * v;
                    stats.healthRegen = 0.35f * v;
                    stats.healthRegenPerLevel = 0.08f * v;
                    stats.maxPeople = 8f * v;
                    stats.maxPeoplePerLevel = 1.6f * v;
                    stats.rammingPower = 2f * v;
                    stats.rammingPowerPerLevel = 0.5f * v;
                    break;

                case "Wing":
                    stats.maxGems = 8f * v;
                    stats.maxGemsPerLevel = 1.6f * v;
                    stats.turnSpeed = 2.5f * v;
                    stats.turnSpeedPerLevel = 0.75f * v;
                    stats.healthCap = 4f * v;
                    stats.healthCapPerLevel = 1.1f * v;
                    stats.healthRegen = 0.12f * v;
                    stats.healthRegenPerLevel = 0.03f * v;
                    break;

                case "Engine":
                    stats.moveSpeed = 5f * v;
                    stats.moveSpeedPerLevel = 0.8f * v;
                    stats.accelerationCap = 4f * v;
                    stats.accelerationCapPerLevel = 0.9f * v;
                    stats.healthCap = 2f * v;
                    stats.healthCapPerLevel = 0.5f * v;
                    break;

                case "Thruster":
                    stats.moveSpeed = 5f * v;
                    stats.moveSpeedPerLevel = 0.8f * v;
                    stats.accelerationCap = 4f * v;
                    stats.accelerationCapPerLevel = 0.9f * v;
                    stats.turnSpeed = 2f * v;
                    stats.turnSpeedPerLevel = 0.6f * v;
                    stats.healthCap = 2f * v;
                    stats.healthCapPerLevel = 0.5f * v;
                    break;

                case "Fin":
                    stats.turnSpeed = 3f * v;
                    stats.turnSpeedPerLevel = 0.8f * v;
                    stats.healthCap = 1.5f * v;
                    stats.healthCapPerLevel = 0.35f * v;
                    break;

                case "Weapon":
                    stats.firePower = 3f * v;
                    stats.firePowerPerLevel = 1f * v;
                    stats.bulletSpeed = 8f * v;
                    stats.bulletSpeedPerLevel = 2f * v;
                    stats.fireRate = 1.2f * v;
                    stats.fireRatePerLevel = 0.2f * v;
                    stats.energyCap = 6f * v;
                    stats.energyCapPerLevel = 1.5f * v;
                    stats.energyRegen = 1.5f * v;
                    stats.energyRegenPerLevel = 0.35f * v;
                    stats.healthCap = 1f * v;
                    break;

                case "Part":
                case "Hull":
                    stats.healthCap = 7f * v;
                    stats.healthCapPerLevel = 1.6f * v;
                    stats.healthRegen = 0.25f * v;
                    stats.healthRegenPerLevel = 0.06f * v;
                    stats.maxGems = 2f * v;
                    stats.maxGemsPerLevel = 0.4f * v;
                    stats.maxPeople = 1f * v;
                    stats.maxPeoplePerLevel = 0.2f * v;
                    break;

                default:
                    stats.healthCap = 2f * v;
                    stats.healthCapPerLevel = 0.5f * v;
                    break;
            }

            return stats;
        }

        private static void GenerateOrSyncBalanceProfileFromFolder(ShipFamilyDefinition def)
        {
            if (def == null) return;
            string folder = EditorUtility.OpenFolderPanel("Select Prefab Folder", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder)) return;
            if (!folder.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Invalid Folder", "Folder must be inside the project's Assets folder.", "OK");
                return;
            }
            string familyId = string.IsNullOrWhiteSpace(def.familyId) ? string.Empty : def.familyId.Trim();
            if (string.IsNullOrEmpty(familyId))
            {
                EditorUtility.DisplayDialog("Missing Family Id", "Please set familyId before syncing balance profile.", "OK");
                return;
            }

            string relativeFolder = "Assets" + folder.Substring(Application.dataPath.Length);
            var scan = ScanCanonicalComponents(relativeFolder, familyId);
            if (scan.Count == 0)
            {
                EditorUtility.DisplayDialog("No Components", "No matching family components found in selected folder.", "OK");
                return;
            }

            Undo.RecordObject(def, "Sync Ship Component Balance Profile");
            if (def.componentBalanceProfile == null)
            {
                string defPath = AssetDatabase.GetAssetPath(def);
                string dir = string.IsNullOrEmpty(defPath) ? "Assets" : Path.GetDirectoryName(defPath)?.Replace('\\', '/') ?? "Assets";
                string profilePath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{def.name}_ComponentBalanceProfile.asset");
                var p = ScriptableObject.CreateInstance<ShipFamilyComponentBalanceProfile>();
                p.profileId = string.IsNullOrWhiteSpace(def.familyId) ? def.name : def.familyId.Trim();
                AssetDatabase.CreateAsset(p, profilePath);
                def.componentBalanceProfile = p;
            }

            var profile = def.componentBalanceProfile;
            Undo.RecordObject(profile, "Sync Ship Component Balance Profile");
            profile.componentRules ??= new List<ShipFamilyComponentBalanceRule>();

            for (int i = 0; i < scan.Count; i++)
            {
                var d = scan[i];
                ShipFamilyComponentBalanceRule existing = null;
                for (int r = 0; r < profile.componentRules.Count; r++)
                {
                    var rule = profile.componentRules[r];
                    if (rule == null) continue;
                    if (string.Equals(ShipFamilyComponentBalanceProfile.NormalizeComponentId(rule.componentId), d.canonicalId, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = rule;
                        break;
                    }
                }

                if (existing == null)
                {
                    existing = new ShipFamilyComponentBalanceRule
                    {
                        componentId = d.canonicalId,
                        partType = d.partType,
                        stats = SuggestStatsForComponent(d.canonicalId, d.partType, d.version),
                        aliases = new List<string>()
                    };
                    profile.componentRules.Add(existing);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(existing.partType))
                        existing.partType = d.partType;
                    if (string.IsNullOrWhiteSpace(existing.componentId))
                        existing.componentId = d.canonicalId;
                }

                existing.aliases ??= new List<string>();
                foreach (string alias in d.aliases)
                {
                    if (string.IsNullOrWhiteSpace(alias)) continue;
                    bool exists = false;
                    for (int z = 0; z < existing.aliases.Count; z++)
                    {
                        if (string.Equals(existing.aliases[z], alias, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists) existing.aliases.Add(alias);
                }
            }

            EnsureDefaultPartTypeRules(profile);
            EditorUtility.SetDirty(profile);
            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(profile);
        }

        private static void ExportCanonicalComponentInventory(ShipFamilyDefinition def)
        {
            if (def == null) return;
            string folder = EditorUtility.OpenFolderPanel("Select Prefab Folder", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder)) return;
            if (!folder.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Invalid Folder", "Folder must be inside the project's Assets folder.", "OK");
                return;
            }
            string familyId = string.IsNullOrWhiteSpace(def.familyId) ? string.Empty : def.familyId.Trim();
            if (string.IsNullOrEmpty(familyId))
            {
                EditorUtility.DisplayDialog("Missing Family Id", "Please set familyId before exporting inventory.", "OK");
                return;
            }

            string relativeFolder = "Assets" + folder.Substring(Application.dataPath.Length);
            var scan = ScanCanonicalComponents(relativeFolder, familyId);
            if (scan.Count == 0)
            {
                EditorUtility.DisplayDialog("No Components", "No matching family components found in selected folder.", "OK");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(def);
            string dir = string.IsNullOrEmpty(assetPath) ? "Assets" : Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "Assets";
            string file = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{def.familyId}_ComponentInventory.csv");
            var sb = new StringBuilder();
            sb.AppendLine("CanonicalId,PartType,Version,Aliases");
            for (int i = 0; i < scan.Count; i++)
            {
                var d = scan[i];
                string aliases = string.Join("|", d.aliases);
                sb.AppendLine($"{EscapeCsv(d.canonicalId)},{EscapeCsv(d.partType)},{d.version},{EscapeCsv(aliases)}");
            }
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            AssetDatabase.ImportAsset(file);
            AssetDatabase.Refresh();
            var obj = AssetDatabase.LoadAssetAtPath<TextAsset>(file);
            if (obj != null) EditorGUIUtility.PingObject(obj);
            EditorUtility.DisplayDialog("Inventory Exported", $"Wrote canonical component inventory:\n{file}", "OK");
        }

        private static string EscapeCsv(string s)
        {
            if (s == null) return "\"\"";
            string t = s.Replace("\"", "\"\"");
            return $"\"{t}\"";
        }

        private static List<CanonicalComponentScanData> ScanCanonicalComponents(string relativeFolder, string familyId)
        {
            string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { relativeFolder });
            var map = new Dictionary<string, CanonicalComponentScanData>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var transforms = prefab.GetComponentsInChildren<Transform>(true);
                foreach (var t in transforms)
                {
                    if (t == null || string.IsNullOrEmpty(t.name)) continue;
                    if (t == prefab.transform) continue; // Exclude prefab root (ship object), scan only child components.
                    if (!t.name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = t.name.Substring(familyId.Length + 1);
                    string canonicalId = ShipFamilyComponentBalanceProfile.NormalizeComponentId(rest);
                    if (string.IsNullOrWhiteSpace(canonicalId)) continue;
                    string type = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(canonicalId);
                    int version = ExtractFirstVersionNumberFromComponentRest(canonicalId);
                    if (!map.TryGetValue(canonicalId, out CanonicalComponentScanData data))
                    {
                        data = new CanonicalComponentScanData(canonicalId, type, version);
                        map[canonicalId] = data;
                    }
                    data.aliases.Add(rest);
                }
            }
            var list = new List<CanonicalComponentScanData>(map.Values);
            list.Sort((a, b) => string.Compare(a.canonicalId, b.canonicalId, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void EnsureDefaultPartTypeRules(ShipFamilyComponentBalanceProfile profile)
        {
            if (profile == null) return;
            profile.partTypeRules ??= new List<ShipFamilyPartTypeBalanceRule>();
            string[] partTypes = { "Cockpit", "Wing", "Engine", "Thruster", "Fin", "Weapon", "Part", "Hull", "Utility", "Other" };
            for (int i = 0; i < partTypes.Length; i++)
            {
                string type = partTypes[i];
                bool exists = false;
                for (int j = 0; j < profile.partTypeRules.Count; j++)
                {
                    var r = profile.partTypeRules[j];
                    if (r == null || string.IsNullOrWhiteSpace(r.partType)) continue;
                    if (string.Equals(r.partType.Trim(), type, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    profile.partTypeRules.Add(new ShipFamilyPartTypeBalanceRule
                    {
                        partType = type,
                        stats = SuggestStatsForComponent(type, type, 1)
                    });
                }
            }
        }

        private sealed class CanonicalComponentScanData
        {
            public string canonicalId;
            public string partType;
            public int version;
            public HashSet<string> aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public CanonicalComponentScanData(string canonicalId, string partType, int version)
            {
                this.canonicalId = canonicalId;
                this.partType = partType;
                this.version = version;
            }
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

