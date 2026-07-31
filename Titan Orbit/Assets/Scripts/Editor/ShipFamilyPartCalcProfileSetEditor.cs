using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Shared helpers to locate / create the project-wide <see cref="ShipFamilyPartCalcProfileSet"/>
    /// under Resources, used by every ShipFamilyDefinition Scan button.
    /// </summary>
    public static class ShipFamilyPartCalcProfileSetEditorUtility
    {
        public const string ResourcesFolder = "Assets/Resources";
        public const string AssetPath = ResourcesFolder + "/ShipFamilyPartCalcProfileSet.asset";

        /// <summary>
        /// Parent folder that contains every ship-family subfolder
        /// (AstroEagle, CosmicShark, SpaceExcalibur, …).
        /// </summary>
        public const string ShipsRootFolder = "Assets/Prefabs/Ships";

        /// <summary>Loads Resources asset or finds any ProfileSet in the project.</summary>
        public static ShipFamilyPartCalcProfileSet FindOrLoadShared()
        {
            var fromResources = ShipFamilyPartCalcProfileSet.LoadShared();
            if (fromResources != null)
                return fromResources;

            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyPartCalcProfileSet");
            if (guids == null || guids.Length == 0)
                return null;
            return AssetDatabase.LoadAssetAtPath<ShipFamilyPartCalcProfileSet>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        /// <summary>Creates the Resources asset if missing, then pings it.</summary>
        public static ShipFamilyPartCalcProfileSet PingOrCreateShared()
        {
            var existing = FindOrLoadShared();
            if (existing != null)
            {
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                return existing;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");

            var created = ScriptableObject.CreateInstance<ShipFamilyPartCalcProfileSet>();
            created.ResetPartProfilesToCodeDefaults();
            AssetDatabase.CreateAsset(created, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = created;
            EditorGUIUtility.PingObject(created);
            return created;
        }
    }

    /// <summary>
    /// Custom inspector for <see cref="ShipFamilyPartCalcProfileSet"/>:
    /// Discover all ship-family folders under Prefabs/Ships, export a Cursor classification prompt,
    /// import suggestions, and manage part calc profiles.
    /// <para>
    /// Classification uses <b>Cursor</b> (export prompt → chat/agent → import JSON) — not ChatGPT HTTP.
    /// </para>
    /// </summary>
    [CustomEditor(typeof(ShipFamilyPartCalcProfileSet))]
    public class ShipFamilyPartCalcProfileSetEditor : UnityEditor.Editor
    {
        bool _reclassifyAll;
        Vector2 _suggestionScroll;
        List<AiSuggestionRow> _pendingSuggestions;
        string _lastCursorPromptPath = string.Empty;
        string _lastDiscoverSummary = string.Empty;

        sealed class AiSuggestionRow
        {
            public string discoveredName;
            public string partType;
            public bool contributesAbilityStats = true;
            public bool enablePropulsionVfx;
            public float propulsionVfxScale = 1f;
            public float confidence;
            public string rationale;
            public bool apply = true;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var set = (ShipFamilyPartCalcProfileSet)target;

            EditorGUILayout.HelpBox(
                "Two lists, different jobs:\n" +
                "• Name Mappings = inventory of unique prefab part names (sorted A→Z).\n" +
                "• Part Profiles = shared stats per group:\n" +
                "  Cockpit, Weapon Bullet, Weapon Cannon, Wing, Engine/Thrust, Tail, Hull.\n\n" +
                "Engine + thrusters share Engine/Thrust stats (VFX only on thruster mounts).\n" +
                "Fin merges into Tail. Guns → Weapon Bullet; cannons/missiles → Weapon Cannon.\n" +
                "Covers/plates stay in their group with contributesAbilityStats off and VFX off.\n\n" +
                "1) Discover All Ship Families\n" +
                "2) Classify with Cursor (optional)\n" +
                "3) Ensure Profiles / Reset Profiles → Scan Folder on each family.",
                MessageType.Info);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Ship families root", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(ShipFamilyPartCalcProfileSetEditorUtility.ShipsRootFolder);
            EditorGUILayout.HelpBox(
                "Every immediate subfolder (AstroEagle, CosmicShark, SpaceExcalibur, …) is scanned — " +
                "not only the currently selected family.",
                MessageType.None);
            EditorGUILayout.EndVertical();

            _reclassifyAll = EditorGUILayout.ToggleLeft(
                "Include already-classified names when exporting for Cursor",
                _reclassifyAll);

            if (GUILayout.Button("Discover All Ship Families & Export for Cursor", GUILayout.Height(34)))
                DiscoverAndExportForCursor(set, forceReclassify: _reclassifyAll);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Last Cursor Prompt"))
                    OpenLastCursorPrompt();
                if (GUILayout.Button("Import Cursor Suggestions JSON"))
                    ImportAiSuggestionsJson(set);
            }

            if (!string.IsNullOrEmpty(_lastDiscoverSummary))
                EditorGUILayout.HelpBox(_lastDiscoverSummary, MessageType.None);

            if (_pendingSuggestions != null && _pendingSuggestions.Count > 0)
                DrawSuggestionReview(set);

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Ensure Profiles For Mapped Part Types"))
            {
                Undo.RecordObject(set, "Ensure Part Profiles");
                int n = set.EnsureProfilesForMappedPartTypes();
                EditorUtility.SetDirty(set);
                EditorUtility.DisplayDialog("Profiles", $"Created {n} missing profile row(s).", "OK");
            }

            if (GUILayout.Button("Reset Part Calc Profiles To Defaults"))
            {
                if (EditorUtility.DisplayDialog(
                        "Reset Profiles",
                        "Rewrite partProfiles from code seeds? Name mappings and VFX flags are kept.",
                        "Reset",
                        "Cancel"))
                {
                    Undo.RecordObject(set, "Reset Part Profiles");
                    set.ResetPartProfilesToCodeDefaults();
                    EditorUtility.SetDirty(set);
                }
            }

            EditorGUILayout.Space(8);
            DrawDefaultInspector();
            if (GUI.changed)
                set.InvalidateLookups();
            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Scans every family folder under Prefabs/Ships, merges nameMappings, then writes a Cursor
        /// prompt (.md + .json) and tries to open it in Cursor.
        /// </summary>
        void DiscoverAndExportForCursor(ShipFamilyPartCalcProfileSet set, bool forceReclassify)
        {
            var discoverResult = DiscoverAllFamilyComponentNames(set);
            set.InvalidateLookups();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            var unmapped = CollectNamesForAi(set, forceReclassify);
            _lastDiscoverSummary =
                $"Scanned {discoverResult.familyFolderCount} family folder(s), {discoverResult.prefabCount} prefab(s). " +
                $"+{discoverResult.namesAdded} new name(s). Inventory size: {set.nameMappings?.Count ?? 0}. " +
                $"Names for Cursor: {unmapped.Count}.";

            if (unmapped.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Discover Complete",
                    _lastDiscoverSummary + "\n\nNo Unmapped names need Cursor classification " +
                    "(enable “Include already-classified…” to re-export everything).",
                    "OK");
                return;
            }

            string mdPath = ExportCursorClassificationPrompt(set, unmapped, discoverResult);
            _lastCursorPromptPath = mdPath;
            EditorGUIUtility.systemCopyBuffer = File.ReadAllText(AbsoluteFromAssetsPath(mdPath));

            bool openedCursor = TryOpenInCursor(mdPath);
            string abs = AbsoluteFromAssetsPath(mdPath);
            EditorUtility.RevealInFinder(abs);

            EditorUtility.DisplayDialog(
                "Ready for Cursor",
                _lastDiscoverSummary + "\n\n" +
                "Prompt copied to clipboard and written to:\n" + abs + "\n\n" +
                (openedCursor
                    ? "Opened in Cursor when possible.\n\n"
                    : "Could not auto-launch Cursor — paste the clipboard into a Cursor chat, or open the .md file.\n\n") +
                "Ask Cursor to classify the names and return ONLY a JSON array. " +
                "Save the reply (or write ShipFamilyPartCalc_Cursor_Suggestions.json next to the ProfileSet), " +
                "then click Import Cursor Suggestions JSON.",
                "OK");
        }

        void OpenLastCursorPrompt()
        {
            if (string.IsNullOrEmpty(_lastCursorPromptPath))
            {
                EditorUtility.DisplayDialog(
                    "No Prompt Yet",
                    "Run Discover All Ship Families & Export for Cursor first.",
                    "OK");
                return;
            }

            string abs = AbsoluteFromAssetsPath(_lastCursorPromptPath);
            if (!File.Exists(abs))
            {
                EditorUtility.DisplayDialog("Missing File", abs, "OK");
                return;
            }

            EditorGUIUtility.systemCopyBuffer = File.ReadAllText(abs);
            TryOpenInCursor(_lastCursorPromptPath);
            EditorUtility.RevealInFinder(abs);
        }

        /// <summary>
        /// Walks every immediate subfolder of Prefabs/Ships (and falls back to all ShipFamilyDefinition
        /// asset directories). Does not limit to AstroEagle.
        /// </summary>
        public static DiscoverResult DiscoverAllFamilyComponentNames(ShipFamilyPartCalcProfileSet set)
        {
            var result = new DiscoverResult();
            if (set == null)
                return result;

            int addedBefore = set.nameMappings != null ? set.nameMappings.Count : 0;
            Undo.RecordObject(set, "Discover Component Names");

            // Prefab roots are full ships (AstroEagle_Thumper). Collect those suffixes so we can
            // strip them from nameMappings — Discover used to treat roots as parts.
            var shipHullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // --- Primary: every child folder under Prefabs/Ships ---
            string shipsRoot = ShipFamilyPartCalcProfileSetEditorUtility.ShipsRootFolder;
            if (AssetDatabase.IsValidFolder(shipsRoot))
            {
                string[] familyFolders = AssetDatabase.GetSubFolders(shipsRoot);
                for (int i = 0; i < familyFolders.Length; i++)
                {
                    string folder = familyFolders[i].Replace('\\', '/');
                    string familyId = Path.GetFileName(folder);
                    if (string.IsNullOrEmpty(familyId))
                        continue;

                    result.familyFolderCount++;
                    result.prefabCount += DiscoverPrefabsInFolder(set, folder, familyId, shipHullNames);
                }
            }
            else
            {
                Debug.LogWarning(
                    $"[ShipFamilyPartCalc] Ships root folder missing: {shipsRoot}. " +
                    "Falling back to ShipFamilyDefinition asset directories.");
            }

            // --- Fallback / supplement: folders next to each ShipFamilyDefinition asset ---
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition");
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(assetPath);
                if (def == null || string.IsNullOrWhiteSpace(def.familyId))
                    continue;

                string familyId = def.familyId.Trim();
                string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                    continue;

                // Skip if already covered as Prefabs/Ships/{familyId}.
                string canonical = $"{shipsRoot}/{familyId}";
                if (string.Equals(folder, canonical, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.familyFolderCount++;
                result.prefabCount += DiscoverPrefabsInFolder(set, folder, familyId, shipHullNames);
            }

            int pruned = PruneShipHullNamesFromMappings(set, shipHullNames);
            if (pruned > 0)
                Debug.Log($"[ShipFamilyPartCalc] Removed {pruned} full-ship name(s) from component inventory.");

            // Migrate legacy types, sort Name Mappings A→Z, ensure core Part Profiles exist.
            set.EnsureProfilesForMappedPartTypes();

            int after = set.nameMappings != null ? set.nameMappings.Count : 0;
            result.namesAdded = Mathf.Max(0, after - addedBefore);
            return result;
        }

        /// <summary>Result counters from a Discover pass (for Inspector messaging).</summary>
        public struct DiscoverResult
        {
            public int familyFolderCount;
            public int prefabCount;
            public int namesAdded;
        }

        static int DiscoverPrefabsInFolder(
            ShipFamilyPartCalcProfileSet set,
            string folder,
            string familyId,
            HashSet<string> shipHullNames)
        {
            int count = 0;
            string[] prefabGuids = AssetDatabase.FindAssets("t:GameObject", new[] { folder });
            for (int g = 0; g < prefabGuids.Length; g++)
            {
                string path = AssetDatabase.GUIDToAssetPath(prefabGuids[g]);
                // Only prefabs (skip nested non-prefab GameObjects if any).
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                // Asset / root name is the full ship (FamilyId_ShipName) — not a chassis part.
                string hull = ShipFamilyPrefabComponentNameUtility.ExtractFamilySuffix(prefab.name, familyId);
                if (!string.IsNullOrEmpty(hull))
                    shipHullNames.Add(hull);

                DiscoverNamesOnPrefab(set, prefab, familyId);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Drops nameMappings that match a ship prefab root suffix (e.g. Thumper), not a part id.
        /// </summary>
        static int PruneShipHullNamesFromMappings(ShipFamilyPartCalcProfileSet set, HashSet<string> shipHullNames)
        {
            if (set?.nameMappings == null || shipHullNames == null || shipHullNames.Count == 0)
                return 0;

            int removed = 0;
            for (int i = set.nameMappings.Count - 1; i >= 0; i--)
            {
                var m = set.nameMappings[i];
                if (m == null || string.IsNullOrWhiteSpace(m.discoveredName))
                    continue;
                string key = ShipFamilyDefinition.NormalizeComponentId(m.discoveredName);
                if (!shipHullNames.Contains(key))
                    continue;
                set.nameMappings.RemoveAt(i);
                removed++;
            }

            if (removed > 0)
                set.InvalidateLookups();
            return removed;
        }

        static void DiscoverNamesOnPrefab(ShipFamilyPartCalcProfileSet set, GameObject prefab, string familyId)
        {
            GameObject root = prefab;
            bool unload = false;
            string path = AssetDatabase.GetAssetPath(prefab);
            if (!string.IsNullOrEmpty(path))
            {
                root = PrefabUtility.LoadPrefabContents(path);
                unload = root != null;
            }

            if (root == null)
                return;

            try
            {
                // Prefab root is the full ship (e.g. AstroEagle_Thumper) — never treat that as a part.
                Transform rootTransform = root.transform;
                string rootShipSuffix = ShipFamilyPrefabComponentNameUtility.ExtractFamilySuffix(
                    rootTransform.name, familyId);

                var transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform t = transforms[i];
                    if (t == null || t == rootTransform)
                        continue;

                    // Prefab asset name — never Unity "Name (1)" instance duplicates.
                    if (!ShipFamilyPrefabComponentNameUtility.TryResolveComponentRest(
                            t, familyId, out string rest))
                        continue;

                    // Skip duplicates of the hull/ship name if nested under the root with the same id.
                    if (!string.IsNullOrEmpty(rootShipSuffix)
                        && string.Equals(rest, rootShipSuffix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    set.MergeDiscoveredName(rest, familyId);
                }
            }
            finally
            {
                if (unload)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static List<string> CollectNamesForAi(ShipFamilyPartCalcProfileSet set, bool forceAll)
        {
            var names = new List<string>();
            if (set.nameMappings == null)
                return names;
            for (int i = 0; i < set.nameMappings.Count; i++)
            {
                var m = set.nameMappings[i];
                if (m == null || string.IsNullOrWhiteSpace(m.discoveredName))
                    continue;
                if (forceAll || string.Equals(m.partType, "Unmapped", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(m.partType))
                    names.Add(m.discoveredName);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Writes a Cursor-friendly .md prompt + companion .json names list next to the ProfileSet.
        /// </summary>
        static string ExportCursorClassificationPrompt(
            ShipFamilyPartCalcProfileSet set,
            List<string> names,
            DiscoverResult discoverResult)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "Assets/Resources";
            string mdPath = $"{dir}/ShipFamilyPartCalc_Cursor_Prompt.md";
            string jsonPath = $"{dir}/ShipFamilyPartCalc_Cursor_Names.json";
            string suggestionsPath = $"{dir}/ShipFamilyPartCalc_Cursor_Suggestions.json";

            var md = new StringBuilder();
            md.AppendLine("# Titan Orbit — classify ship component names");
            md.AppendLine();
            md.AppendLine("You are helping author `ShipFamilyPartCalcProfileSet` mappings for Unity.");
            md.AppendLine($"Discovered from `{ShipFamilyPartCalcProfileSetEditorUtility.ShipsRootFolder}` " +
                          $"({discoverResult.familyFolderCount} family folders, {discoverResult.prefabCount} prefabs).");
            md.AppendLine();
            md.AppendLine("## Task");
            md.AppendLine("Classify each `discoveredName` (prefab asset suffix). Return **ONLY** a JSON array (no markdown fences) of objects:");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine("{");
            md.AppendLine("  \"discoveredName\": \"Thrusters_Big\",");
            md.AppendLine("  \"partType\": \"Thruster\",");
            md.AppendLine("  \"contributesAbilityStats\": true,");
            md.AppendLine("  \"enablePropulsionVfx\": true,");
            md.AppendLine("  \"propulsionVfxScale\": 1.5,");
            md.AppendLine("  \"confidence\": 0.9,");
            md.AppendLine("  \"rationale\": \"Plural big thrusters — jets on\"");
            md.AppendLine("}");
            md.AppendLine("```");
            md.AppendLine();
            md.AppendLine("## Mental model");
            md.AppendLine("- `partType` = **broad group** (shared Part Profile stats + attribute mesh-scale bucket).");
            md.AppendLine("- Allowed partType values ONLY:");
            md.AppendLine("  `Cockpit`, `Weapon Bullet`, `Weapon Cannon`, `Wing`, `Engine/Thrust`, `Tail`, `Hull`, `Ignore`");
            md.AppendLine("- `Cockpit` and `Cockpit_Base` both → `Cockpit` so Max People scales together.");
            md.AppendLine("- Engine meshes + thrusters → `Engine/Thrust` (same stats). VFX only on thruster/exhaust mounts.");
            md.AppendLine("- Fin → `Tail`. Everything else that is not a gameplay part → `Hull`.");
            md.AppendLine("- Covers / plates / holders stay in the parent group with");
            md.AppendLine("  `contributesAbilityStats: false` and `enablePropulsionVfx: false`.");
            md.AppendLine();
            md.AppendLine("## Rules");
            md.AppendLine("- Gun / Machinegun / Barrel → `Weapon Bullet` (rapid, smaller); VFX off");
            md.AppendLine("- Cannon / Missile / Rocket → `Weapon Cannon` (slow, heavier); VFX off");
            md.AppendLine("- Engine_* → `Engine/Thrust`; stats true; VFX **off** (mesh only)");
            md.AppendLine("- Exhaust / Thrusters / Thrusters_Big / Tiny_Thrusters → `Engine/Thrust`; VFX on");
            md.AppendLine("- Thrusters_Big → VFX scale ≈ **1.5**; Tiny_Thrusters → ≈ **0.45**");
            md.AppendLine("- Fin / Tail → `Tail`; stats true; VFX off");
            md.AppendLine("- Thruster_Place / *Cover* / *Plate* / *Holder* → parent group;");
            md.AppendLine("  **contributesAbilityStats false**; **VFX off**");
            md.AppendLine("- Body / Armor / Core / Support / Arm / unknown filler → `Hull`");
            md.AppendLine("- Default propulsionVfxScale = 1 when VFX is on and size is normal");
            md.AppendLine();
            md.AppendLine("## Output");
            md.AppendLine($"Save your JSON array to `{suggestionsPath}` (create/overwrite), or paste it back in Unity via **Import Cursor Suggestions JSON**.");
            md.AppendLine();
            md.AppendLine("## Names to classify");
            for (int i = 0; i < names.Count; i++)
                md.AppendLine($"- `{names[i]}`");

            File.WriteAllText(AbsoluteFromAssetsPath(mdPath), md.ToString(), Encoding.UTF8);

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"shipsRoot\": \"" + ShipFamilyPartCalcProfileSetEditorUtility.ShipsRootFolder + "\",");
            json.AppendLine("  \"names\": [");
            for (int i = 0; i < names.Count; i++)
            {
                json.Append("    \"").Append(names[i].Replace("\"", "\\\"")).Append("\"");
                if (i < names.Count - 1) json.Append(",");
                json.AppendLine();
            }

            json.AppendLine("  ]");
            json.AppendLine("}");
            File.WriteAllText(AbsoluteFromAssetsPath(jsonPath), json.ToString(), Encoding.UTF8);

            AssetDatabase.ImportAsset(mdPath);
            AssetDatabase.ImportAsset(jsonPath);
            return mdPath;
        }

        static string AbsoluteFromAssetsPath(string assetsPath)
        {
            if (string.IsNullOrEmpty(assetsPath))
                return string.Empty;
            string relative = assetsPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? assetsPath.Substring("Assets/".Length)
                : assetsPath;
            return Path.GetFullPath(Path.Combine(Application.dataPath, relative));
        }

        /// <summary>
        /// Tries to open a project file in the Cursor app via the `cursor` CLI.
        /// Returns false if Cursor is not on PATH (user can still paste from clipboard).
        /// </summary>
        static bool TryOpenInCursor(string assetsPath)
        {
            string abs = AbsoluteFromAssetsPath(assetsPath);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
                return false;

            try
            {
                // `cursor <file>` opens the file in Cursor (same pattern as `code <file>` for VS Code).
                var psi = new ProcessStartInfo
                {
                    FileName = "cursor",
                    Arguments = "\"" + abs + "\"",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[ShipFamilyPartCalc] Could not launch Cursor CLI (`cursor`). " +
                    "Install Cursor shell command, or paste the clipboard into Cursor manually. " + ex.Message);
                return false;
            }
        }

        void ImportAiSuggestionsJson(ShipFamilyPartCalcProfileSet set)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "Assets/Resources";
            string defaultSuggestions = AbsoluteFromAssetsPath($"{dir}/ShipFamilyPartCalc_Cursor_Suggestions.json");
            string startDir = File.Exists(defaultSuggestions)
                ? Path.GetDirectoryName(defaultSuggestions)
                : Application.dataPath;

            string path = EditorUtility.OpenFilePanel(
                "Import Cursor Suggestions JSON",
                startDir ?? Application.dataPath,
                "json");
            if (string.IsNullOrEmpty(path))
            {
                // Convenience: if the default suggestions file exists, offer to load it.
                if (File.Exists(defaultSuggestions)
                    && EditorUtility.DisplayDialog(
                        "Import Cursor Suggestions",
                        "Use the default file?\n" + defaultSuggestions,
                        "Import",
                        "Cancel"))
                {
                    path = defaultSuggestions;
                }
                else
                {
                    return;
                }
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            _pendingSuggestions = ParseSuggestionsJson(json);
            EditorUtility.DisplayDialog(
                "Import",
                $"Loaded {_pendingSuggestions.Count} suggestion(s) from Cursor. Review below and Apply.",
                "OK");
        }

        void DrawSuggestionReview(ShipFamilyPartCalcProfileSet set)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Cursor Suggestions (review)", EditorStyles.boldLabel);
            _suggestionScroll = EditorGUILayout.BeginScrollView(_suggestionScroll, GUILayout.Height(180));
            for (int i = 0; i < _pendingSuggestions.Count; i++)
            {
                var row = _pendingSuggestions[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                row.apply = EditorGUILayout.ToggleLeft(row.discoveredName, row.apply);
                row.partType = EditorGUILayout.TextField("Part Type (group)", row.partType);
                row.contributesAbilityStats = EditorGUILayout.Toggle("Contributes Ability Stats", row.contributesAbilityStats);
                row.enablePropulsionVfx = EditorGUILayout.Toggle("Propulsion VFX", row.enablePropulsionVfx);
                row.propulsionVfxScale = EditorGUILayout.FloatField("VFX Scale", row.propulsionVfxScale);
                if (!string.IsNullOrEmpty(row.rationale))
                    EditorGUILayout.LabelField(row.rationale, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Apply Cursor Suggestions", GUILayout.Height(28)))
            {
                ApplySuggestions(set, _pendingSuggestions);
                _pendingSuggestions = null;
            }

            if (GUILayout.Button("Discard Suggestions"))
                _pendingSuggestions = null;
        }

        static void ApplySuggestions(ShipFamilyPartCalcProfileSet set, List<AiSuggestionRow> rows)
        {
            if (set == null || rows == null)
                return;
            Undo.RecordObject(set, "Apply Cursor Part Classifications");
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || !row.apply || string.IsNullOrWhiteSpace(row.discoveredName))
                    continue;
                var mapping = set.MergeDiscoveredName(row.discoveredName, null);
                if (mapping == null)
                    continue;
                string rawType = string.IsNullOrWhiteSpace(row.partType) ? "Unmapped" : row.partType.Trim();
                mapping.partType = string.Equals(rawType, "Ignore", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rawType, "Unmapped", StringComparison.OrdinalIgnoreCase)
                    ? rawType
                    : ShipFamilyPartTypes.Normalize(rawType, row.discoveredName);
                mapping.contributesAbilityStats = row.contributesAbilityStats;
                mapping.enablePropulsionVfx = row.enablePropulsionVfx;
                mapping.propulsionVfxScale = row.propulsionVfxScale > 0.0001f ? row.propulsionVfxScale : 1f;
                mapping.includeInPopulate = !string.Equals(mapping.partType, "Ignore", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(row.rationale))
                    mapping.notes = row.rationale;
            }

            set.EnsureProfilesForMappedPartTypes();
            set.InvalidateLookups();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
        }

        static List<AiSuggestionRow> ParseSuggestionsJson(string json)
        {
            var rows = new List<AiSuggestionRow>();
            if (string.IsNullOrWhiteSpace(json))
                return rows;

            json = json.Trim();
            if (json.StartsWith("```"))
            {
                int firstNl = json.IndexOf('\n');
                int lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNl >= 0 && lastFence > firstNl)
                    json = json.Substring(firstNl + 1, lastFence - firstNl - 1).Trim();
            }

            foreach (Match obj in Regex.Matches(json, "\\{[^{}]+\\}"))
            {
                string block = obj.Value;
                string name = ExtractJsonString(block, "discoveredName");
                if (string.IsNullOrEmpty(name))
                    continue;
                bool hasContributes = Regex.IsMatch(
                    block, "\"contributesAbilityStats\"\\s*:", RegexOptions.IgnoreCase);
                bool contributes = hasContributes
                    ? ExtractJsonBool(block, "contributesAbilityStats")
                    : !ShipFamilyPartCalcProfileSet.IsCosmeticPartName(name);

                rows.Add(new AiSuggestionRow
                {
                    discoveredName = name,
                    partType = ExtractJsonString(block, "partType") ?? "Unmapped",
                    contributesAbilityStats = contributes,
                    enablePropulsionVfx = ExtractJsonBool(block, "enablePropulsionVfx"),
                    propulsionVfxScale = ExtractJsonFloat(block, "propulsionVfxScale", 1f),
                    confidence = ExtractJsonFloat(block, "confidence", 0f),
                    rationale = ExtractJsonString(block, "rationale") ?? string.Empty,
                    apply = true,
                });
            }

            return rows;
        }

        static string ExtractJsonString(string block, string key)
        {
            Match m = Regex.Match(block, "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        static bool ExtractJsonBool(string block, string key)
        {
            Match m = Regex.Match(block, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            return m.Success && string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        static float ExtractJsonFloat(string block, string key, float fallback)
        {
            Match m = Regex.Match(block, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            if (m.Success && float.TryParse(m.Groups[1].Value, out float v))
                return v;
            return fallback;
        }
    }
}
