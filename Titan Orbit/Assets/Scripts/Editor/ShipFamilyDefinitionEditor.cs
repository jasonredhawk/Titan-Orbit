using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
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
        private ReorderableList _componentsList;
        private ReorderableList _upgradeTreeList;

        private void OnEnable()
        {
            SerializedProperty componentsProp = serializedObject.FindProperty("components");
            _componentsList = new ReorderableList(serializedObject, componentsProp, true, true, true, true)
            {
                drawHeaderCallback = DrawComponentsListHeader,
                drawElementCallback = DrawComponentsListElement,
                elementHeightCallback = GetComponentsListElementHeight
            };

            SerializedProperty upgradeTreeProp = serializedObject.FindProperty("upgradeTree");
            _upgradeTreeList = new ReorderableList(serializedObject, upgradeTreeProp, true, true, true, true)
            {
                drawHeaderCallback = DrawUpgradeTreeListHeader,
                drawElementCallback = DrawUpgradeTreeListElement,
                elementHeightCallback = GetUpgradeTreeListElementHeight
            };
        }

        private static void DrawComponentsListHeader(Rect rect)
        {
            EditorGUI.LabelField(rect, "Components");
        }

        private void DrawComponentsListElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            SerializedProperty element = _componentsList.serializedProperty.GetArrayElementAtIndex(index);
            var label = new GUIContent($"Element {index}");
            ShipFamilyComponentEntryInspectorUI.Draw(rect, element, label);
        }

        private float GetComponentsListElementHeight(int index)
        {
            SerializedProperty element = _componentsList.serializedProperty.GetArrayElementAtIndex(index);
            return ShipFamilyComponentEntryInspectorUI.GetHeight(element);
        }

        private static void DrawUpgradeTreeListHeader(Rect rect)
        {
            EditorGUI.LabelField(rect, "Upgrade Tree");
        }

        private void DrawUpgradeTreeListElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            SerializedProperty element = _upgradeTreeList.serializedProperty.GetArrayElementAtIndex(index);
            var label = new GUIContent($"Element {index}");
            ShipFamilyUpgradeTreeEntryInspectorUI.Draw(rect, element, label, target as ShipFamilyDefinition);
        }

        private float GetUpgradeTreeListElementHeight(int index)
        {
            SerializedProperty element = _upgradeTreeList.serializedProperty.GetArrayElementAtIndex(index);
            return ShipFamilyUpgradeTreeEntryInspectorUI.GetHeight(element, element.isExpanded);
        }

        private void DrawInspectorFieldsExceptCustomLists()
        {
            SerializedProperty prop = serializedObject.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.name == "m_Script")
                    continue;
                if (prop.name == "components")
                {
                    _componentsList.DoLayoutList();
                    continue;
                }
                if (prop.name == "upgradeTree")
                {
                    _upgradeTreeList.DoLayoutList();
                    continue;
                }
                EditorGUILayout.PropertyField(prop, true);
            }
        }

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
            DrawInspectorFieldsExceptCustomLists();
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
                "Scans all prefabs in the family folder for child names like 'GalaxyRaptor_Wing2' or 'AstroEagle_Engine_2'. " +
                "Each unique component id becomes an entry with a Stat Category (Offense, Health, Energy, Movement, Capacity). " +
                "Only stats for that category are shown and stored on each component.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(def.familyId)))
            {
                if (GUILayout.Button("Scan Folder And Auto-Populate Components"))
                {
                    ScanFolderAndPopulate(def);
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
                "Resort Upgrade Tree: recomputes power scores from prefabs and reorders unlocked tiers (power + orbit layout). Entries with Lock In Upgrade Tree enabled stay at their list index.",
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
            string startPath = Path.Combine(Application.dataPath, "Prefabs/Ships/" + def.familyId);
            string folder = EditorUtility.OpenFolderPanel("Select Prefab Folder", startPath, "");
            if (string.IsNullOrEmpty(folder))
                return;

            if (!folder.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Invalid Folder",
                    "Folder must be inside the project's Assets folder.", "OK");
                return;
            }

            string relativeFolder = "Assets" + folder.Substring(Application.dataPath.Length);

            string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
            if (string.IsNullOrEmpty(familyId))
            {
                EditorUtility.DisplayDialog("Missing Family Id",
                    "Please set 'familyId' on the ShipFamilyDefinition before scanning.", "OK");
                return;
            }

            var scan = ScanCanonicalComponents(relativeFolder, familyId);
            if (scan.Count == 0)
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

            foreach (var data in scan)
            {
                string componentId = data.canonicalId;
                string type = data.partType;
                int version = data.version;
                var categories = ShipFamilyComponentPartKey.InferDefaultStatCategories(componentId);

                var entry = new ShipFamilyComponentEntry
                {
                    componentId = componentId,
                    displayName = $"{type} {version}".Trim(),
                    statCategories = categories,
                    stats = SuggestStatsForComponent(componentId, type, version, categories)
                };
                def.components.Add(entry);
            }

            ShipPropulsionAggregation.BalanceWeaponEnergyForComponents(def.components);

            def.EnforceComponentStatCategories();
            def.InvalidateComponentStatsLookup();
            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Scan Complete",
                $"Found {scan.Count} unique component(s).\n" +
                "Each component has Stat Categories (e.g. cockpits: Offense + Health + Capacity; wings: Health + Capacity). Only stats for those categories are stored.",
                "OK");
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
        /// Reorders unlocked upgrade-tree entries using the same rules as <see cref="BuildUpgradeTreeFromFolder"/>.
        /// Locked entries stay at their current list index; power scores are still refreshed for all tiers with prefabs.
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

            int treeCount = def.upgradeTree.Count;
            var unlockedWithPrefab = new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
            var trailingNoPrefab = new List<ShipFamilyChassisTierEntry>();
            int lockedCount = 0;

            for (int i = 0; i < treeCount; i++)
            {
                ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                if (tier == null)
                    continue;

                if (tier.lockedInUpgradeTree)
                    lockedCount++;

                if (tier.prefab == null)
                {
                    if (!tier.lockedInUpgradeTree)
                        trailingNoPrefab.Add(tier);
                    continue;
                }

                ShipComponentAbilityStats stats = SumStatsForPrefab(tier.prefab, def, familyId);
                ShipFamilyPowerScoreBreakdown breakdown = ShipFamilyPowerScoreBreakdown.FromSummedShipStats(stats);
                float power = breakdown.Total;
                tier.powerScore = power;
                tier.powerScoreBreakdown = breakdown;

                if (!tier.lockedInUpgradeTree)
                    unlockedWithPrefab.Add((tier, power, breakdown));
            }

            if (unlockedWithPrefab.Count == 0 && lockedCount == 0)
            {
                EditorUtility.DisplayDialog("No Prefabs",
                    "No upgrade-tree entries have a prefab assigned; nothing to resort.", "OK");
                return;
            }

            List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)> orderedUnlocked =
                OrderUpgradeTreeEntriesByPower(unlockedWithPrefab);

            Undo.RecordObject(def, "Resort Ship Family Upgrade Tree");

            var newTree = new List<ShipFamilyChassisTierEntry>(treeCount + trailingNoPrefab.Count);
            int unlockedIdx = 0;
            int currentLevel = 1;
            int shipsAtCurrentLevel = 1;
            int assignedAtThisLevel = 0;

            for (int i = 0; i < treeCount; i++)
            {
                ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                if (tier == null)
                {
                    newTree.Add(null);
                    continue;
                }

                ShipFamilyChassisTierEntry entry;
                if (tier.lockedInUpgradeTree)
                {
                    entry = tier;
                }
                else if (tier.prefab != null)
                {
                    if (unlockedIdx >= orderedUnlocked.Count)
                        continue;
                    var (sortedEntry, power, breakdown) = orderedUnlocked[unlockedIdx++];
                    entry = sortedEntry;
                    entry.powerScore = power;
                    entry.powerScoreBreakdown = breakdown;
                }
                else
                {
                    continue;
                }

                if (assignedAtThisLevel >= shipsAtCurrentLevel)
                {
                    currentLevel++;
                    shipsAtCurrentLevel++;
                    assignedAtThisLevel = 0;
                }

                entry.minHomePlanetLevel = currentLevel;
                newTree.Add(entry);
                assignedAtThisLevel++;
            }

            for (int i = 0; i < trailingNoPrefab.Count; i++)
                newTree.Add(trailingNoPrefab[i]);

            def.upgradeTree = newTree;

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Resort Upgrade Tree",
                $"Resorted {orderedUnlocked.Count} unlocked tier(s) with prefabs. " +
                $"{lockedCount} locked tier(s) kept at their list index. " +
                $"{trailingNoPrefab.Count} unlocked entr(y/ies) with no prefab appended at the end.",
                "OK");
        }

        private static List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)> OrderUpgradeTreeEntriesByPower(
            List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)> withPrefab)
        {
            if (withPrefab == null || withPrefab.Count == 0)
                return new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();

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

            return orderedForTree;
        }

        /// <summary>Scale-adjusted stats for power scoring (loads prefab contents so nested part localScale is included).</summary>
        private static ShipComponentAbilityStats SumStatsForPrefab(GameObject prefab, ShipFamilyDefinition def, string familyId)
        {
            return ShipFamilyUpgradeTreeStatScanner.SumStatsForPrefabAsset(prefab, def, familyId);
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

        /// <summary>Per-level stat terms are ~25% of the base value (within the 20–30% design band).</summary>
        private static float PerLevelFromBase(float baseValue) =>
            baseValue * ShipPropulsionAggregation.PerLevelFractionOfBase;

        private static float PerLevelPeopleFromBase(float maxPeople) =>
            Mathf.Max(0, Mathf.RoundToInt(PerLevelFromBase(maxPeople)));

        private static ShipComponentAbilityStats SuggestStatsForComponent(
            string componentId,
            string type,
            int version,
            IReadOnlyList<ShipComponentStatCategory> categories)
        {
            var merged = new ShipComponentAbilityStats();
            if (categories == null || categories.Count == 0)
                categories = ShipFamilyComponentPartKey.InferDefaultStatCategories(componentId);

            for (int i = 0; i < categories.Count; i++)
            {
                ShipComponentAbilityStats part = SuggestStatsForCategory(componentId, type, version, categories[i]);
                merged = MergeSuggestedStats(merged, part);
            }

            return ShipComponentAbilityStats.KeepOnlyAuthoringFields(merged, categories, componentId);
        }

        private static ShipComponentAbilityStats MergeSuggestedStats(
            ShipComponentAbilityStats target,
            ShipComponentAbilityStats source)
        {
            if (source.firePower != 0f) target.firePower = source.firePower;
            if (source.firePowerPerLevel != 0f) target.firePowerPerLevel = source.firePowerPerLevel;
            if (source.bulletSpeed != 0f) target.bulletSpeed = source.bulletSpeed;
            if (source.bulletSpeedPerLevel != 0f) target.bulletSpeedPerLevel = source.bulletSpeedPerLevel;
            if (source.fireRate != 0f) target.fireRate = source.fireRate;
            if (source.fireRatePerLevel != 0f) target.fireRatePerLevel = source.fireRatePerLevel;
            if (source.rammingPower != 0f) target.rammingPower = source.rammingPower;
            if (source.rammingPowerPerLevel != 0f) target.rammingPowerPerLevel = source.rammingPowerPerLevel;
            if (source.healthCap != 0f) target.healthCap = source.healthCap;
            if (source.healthCapPerLevel != 0f) target.healthCapPerLevel = source.healthCapPerLevel;
            if (source.healthRegen != 0f) target.healthRegen = source.healthRegen;
            if (source.healthRegenPerLevel != 0f) target.healthRegenPerLevel = source.healthRegenPerLevel;
            if (source.energyCap != 0f) target.energyCap = source.energyCap;
            if (source.energyCapPerLevel != 0f) target.energyCapPerLevel = source.energyCapPerLevel;
            if (source.energyRegen != 0f) target.energyRegen = source.energyRegen;
            if (source.energyRegenPerLevel != 0f) target.energyRegenPerLevel = source.energyRegenPerLevel;
            if (source.moveSpeed != 0f) target.moveSpeed = source.moveSpeed;
            if (source.moveSpeedPerLevel != 0f) target.moveSpeedPerLevel = source.moveSpeedPerLevel;
            if (source.accelerationCap != 0f) target.accelerationCap = source.accelerationCap;
            if (source.accelerationCapPerLevel != 0f) target.accelerationCapPerLevel = source.accelerationCapPerLevel;
            if (source.turnSpeed != 0f) target.turnSpeed = source.turnSpeed;
            if (source.turnSpeedPerLevel != 0f) target.turnSpeedPerLevel = source.turnSpeedPerLevel;
            if (source.maxGems != 0f) target.maxGems = source.maxGems;
            if (source.maxGemsPerLevel != 0f) target.maxGemsPerLevel = source.maxGemsPerLevel;
            if (source.maxPeople != 0f) target.maxPeople = source.maxPeople;
            if (source.maxPeoplePerLevel != 0f) target.maxPeoplePerLevel = source.maxPeoplePerLevel;
            return target;
        }

        private static ShipComponentAbilityStats SuggestStatsForCategory(
            string componentId,
            string type,
            int version,
            ShipComponentStatCategory category)
        {
            float v = Mathf.Max(1, version);
            var stats = new ShipComponentAbilityStats();

            switch (category)
            {
                case ShipComponentStatCategory.Offense:
                    if (string.Equals(type, "Cockpit", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.rammingPower = ShipComponentRammingSuggestions.GetSuggestedRammingPower(version);
                        stats.rammingPowerPerLevel = ShipComponentRammingSuggestions.GetSuggestedRammingPowerPerLevel(version);
                    }
                    else
                    {
                        stats.firePower = 3f * v;
                        stats.bulletSpeed = 12f * v;
                        stats.fireRate = 1.2f * v;
                        stats.firePowerPerLevel = PerLevelFromBase(stats.firePower);
                        stats.bulletSpeedPerLevel = PerLevelFromBase(stats.bulletSpeed);
                        stats.fireRatePerLevel = PerLevelFromBase(stats.fireRate);
                    }
                    break;

                case ShipComponentStatCategory.Health:
                    stats.healthCap = ShipComponentHealthSuggestions.GetSuggestedHealthCap(version);
                    stats.healthRegen = ShipComponentHealthSuggestions.GetSuggestedHealthRegen(version);
                    stats.healthCapPerLevel = ShipComponentHealthSuggestions.GetSuggestedHealthCapPerLevel(version);
                    stats.healthRegenPerLevel = ShipComponentHealthSuggestions.GetSuggestedHealthRegenPerLevel(version);
                    break;

                case ShipComponentStatCategory.Energy:
                    stats.energyCap = 20f * v;
                    stats.energyRegen = 2.5f * v;
                    stats.energyCapPerLevel = PerLevelFromBase(stats.energyCap);
                    stats.energyRegenPerLevel = PerLevelFromBase(stats.energyRegen);
                    break;

                case ShipComponentStatCategory.Movement:
                    if (string.Equals(type, "Tail", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.turnSpeed = ShipComponentTurnSpeedSuggestions.GetSuggestedTailTurnSpeed(version);
                        stats.turnSpeedPerLevel = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(stats.turnSpeed);
                    }
                    else if (string.Equals(type, "Fin", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.turnSpeed = ShipComponentTurnSpeedSuggestions.GetSuggestedFinTurnSpeed(version);
                        stats.turnSpeedPerLevel = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(stats.turnSpeed);
                    }
                    else if (string.Equals(type, "Thruster", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.moveSpeed = ShipPropulsionAggregation.GetSuggestedPropulsionMoveSpeed(version);
                        stats.accelerationCap = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCap(version);
                        stats.moveSpeedPerLevel = ShipPropulsionAggregation.GetSuggestedPropulsionMoveSpeedPerLevel(version);
                        stats.accelerationCapPerLevel = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCapPerLevel(version);
                        stats.turnSpeed = ShipComponentTurnSpeedSuggestions.GetSuggestedThrusterTurnSpeed(version);
                        stats.turnSpeedPerLevel = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(stats.turnSpeed);
                    }
                    else
                    {
                        stats.moveSpeed = ShipPropulsionAggregation.GetSuggestedPropulsionMoveSpeed(version);
                        stats.accelerationCap = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCap(version);
                        stats.moveSpeedPerLevel = ShipPropulsionAggregation.GetSuggestedPropulsionMoveSpeedPerLevel(version);
                        stats.accelerationCapPerLevel = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCapPerLevel(version);
                    }
                    break;

                case ShipComponentStatCategory.Capacity:
                    stats.maxGems = 8f * v;
                    stats.maxPeople = 4f * v;
                    stats.maxGemsPerLevel = PerLevelFromBase(stats.maxGems);
                    stats.maxPeoplePerLevel = PerLevelPeopleFromBase(stats.maxPeople);
                    break;
            }

            return stats;
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
            sb.AppendLine("CanonicalId,PartType,Version,StatCategories,Aliases");
            for (int i = 0; i < scan.Count; i++)
            {
                var d = scan[i];
                string aliases = string.Join("|", d.aliases);
                var categories = ShipFamilyComponentPartKey.InferDefaultStatCategories(d.canonicalId);
                string categoryList = string.Join("|", categories);
                sb.AppendLine($"{EscapeCsv(d.canonicalId)},{EscapeCsv(d.partType)},{d.version},{EscapeCsv(categoryList)},{EscapeCsv(aliases)}");
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
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    continue;

                GameObject root = PrefabUtility.LoadPrefabContents(path);
                if (root == null)
                    continue;

                try
                {
                    CollectComponentsFromPrefabHierarchy(root, root.transform, familyId, map);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            var list = new List<CanonicalComponentScanData>(map.Values);
            list.Sort((a, b) => string.Compare(a.canonicalId, b.canonicalId, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void CollectComponentsFromPrefabHierarchy(
            GameObject prefabRoot,
            Transform rootTransform,
            string familyId,
            Dictionary<string, CanonicalComponentScanData> map)
        {
            var transforms = prefabRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (t == null || t == rootTransform) continue;
                string name = t.name;
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase)) continue;

                string rest = name.Substring(familyId.Length + 1);
                if (string.IsNullOrWhiteSpace(rest)) continue;

                string canonicalId = ShipFamilyDefinition.NormalizeComponentId(rest);
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

