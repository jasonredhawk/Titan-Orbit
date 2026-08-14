using System.Collections.Generic;
using System.Text;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="ShipFamilyDefinitionCatalog"/>:
    /// refresh the family list from Prefabs/Ships, then Recalculate + Resort every listed family.
    /// </summary>
    [CustomEditor(typeof(ShipFamilyDefinitionCatalog))]
    public class ShipFamilyDefinitionCatalogEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var catalog = target as ShipFamilyDefinitionCatalog;
            if (catalog == null)
                return;

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Batch Update All Families", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Refresh Family List finds every ShipFamilyDefinition under Assets/Prefabs/Ships " +
                "and assigns the shared Profile Set when a family field is empty.\n\n" +
                "Recalculate All runs the same pipeline as each family's " +
                "Recalculate Component Stats From Part Profiles, then " +
                "Resort Upgrade Tree & Recalculate Power Scores — one pass, one save.",
                MessageType.Info);

            if (GUILayout.Button("Refresh Family List From Project", GUILayout.Height(28)))
            {
                RefreshFamilyListFromProject(catalog);
            }

            int listed = CountListedFamilies(catalog);
            using (new EditorGUI.DisabledScope(listed == 0))
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.55f, 0.82f, 1f, 1f);
                if (GUILayout.Button(
                        $"Recalculate All Families From Profiles & Resort Upgrade Trees ({listed})",
                        GUILayout.Height(36)))
                {
                    RecalculateAndResortAll(catalog);
                }
                GUI.backgroundColor = prev;
            }

            EditorGUILayout.EndVertical();
        }

        static int CountListedFamilies(ShipFamilyDefinitionCatalog catalog)
        {
            if (catalog?.families == null)
                return 0;

            int count = 0;
            for (int i = 0; i < catalog.families.Count; i++)
            {
                if (catalog.families[i] != null)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Finds every <see cref="ShipFamilyDefinition"/> under Prefabs/Ships, fills the catalog list,
        /// and assigns the shared ProfileSet onto any family (and the catalog) that is missing one.
        /// </summary>
        public static void RefreshFamilyListFromProject(ShipFamilyDefinitionCatalog catalog)
        {
            if (catalog == null)
                return;

            string[] guids = AssetDatabase.FindAssets(
                "t:ShipFamilyDefinition",
                new[] { ShipFamilyPartCalcProfileSetEditorUtility.ShipsRootFolder });
            if (guids == null || guids.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "Refresh Family List",
                    "No ShipFamilyDefinition assets found under Assets/Prefabs/Ships.",
                    "OK");
                return;
            }

            var profileSet = catalog.partCalcProfileSet != null
                ? catalog.partCalcProfileSet
                : ShipFamilyPartCalcProfileSetEditorUtility.FindOrLoadShared();

            Undo.RecordObject(catalog, "Refresh Ship Family Definition Catalog");
            if (catalog.partCalcProfileSet == null && profileSet != null)
                catalog.partCalcProfileSet = profileSet;

            var seen = new HashSet<ShipFamilyDefinition>();
            var next = new List<ShipFamilyDefinition>(guids.Length);
            int assignedProfiles = 0;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def == null || !seen.Add(def))
                    continue;

                next.Add(def);
                if (def.partCalcProfileSet == null && profileSet != null)
                {
                    Undo.RecordObject(def, "Assign Shared Profile Set");
                    def.partCalcProfileSet = profileSet;
                    EditorUtility.SetDirty(def);
                    assignedProfiles++;
                }
            }

            next.Sort((a, b) =>
            {
                string aId = a.familyId != null ? a.familyId : a.name;
                string bId = b.familyId != null ? b.familyId : b.name;
                return string.CompareOrdinal(aId, bId);
            });

            catalog.families = next;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog(
                "Refresh Family List",
                $"Listed {next.Count} family asset(s).\n" +
                $"Assigned shared Profile Set on {assignedProfiles} family asset(s).",
                "OK");
        }

        /// <summary>
        /// Recalculate component stats from profiles, then resort upgrade trees, for every listed family.
        /// Refreshes Part Profiles once, then saves once.
        /// </summary>
        public static void RecalculateAndResortAll(ShipFamilyDefinitionCatalog catalog)
        {
            if (catalog?.families == null)
                return;

            var families = new List<ShipFamilyDefinition>();
            for (int i = 0; i < catalog.families.Count; i++)
            {
                if (catalog.families[i] != null)
                    families.Add(catalog.families[i]);
            }

            if (families.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Recalculate All Families",
                    "Catalog family list is empty. Use Refresh Family List From Project first.",
                    "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Recalculate All Families",
                    $"Recalculate component stats from part profiles, then resort upgrade trees, " +
                    $"for {families.Count} family asset(s)?\n\n" +
                    "This rewrites components[] and reorders unlocked upgrade-tree tiers.",
                    "Update All",
                    "Cancel"))
            {
                return;
            }

            var catalogSet = catalog.partCalcProfileSet != null
                ? catalog.partCalcProfileSet
                : ShipFamilyPartCalcProfileSetEditorUtility.FindOrLoadShared();
            if (catalogSet == null)
            {
                EditorUtility.DisplayDialog(
                    "Missing Profile Set",
                    "Assign a Part Calc Profile Set on this catalog, or create " +
                    "Resources/ShipFamilyPartCalcProfileSet first.",
                    "OK");
                return;
            }

            Undo.RecordObject(catalogSet, "Refresh Part Profiles For Catalog Recalculate");
            int profilesUpdated = ShipFamilyDefinitionEditor.RefreshAllPartProfiles(catalogSet);
            catalogSet.InvalidateLookups();
            EditorUtility.SetDirty(catalogSet);

            int succeeded = 0;
            int failed = 0;
            int componentsUpdated = 0;
            int cosmetics = 0;
            int resorted = 0;
            var failures = new StringBuilder();

            try
            {
                for (int i = 0; i < families.Count; i++)
                {
                    ShipFamilyDefinition def = families[i];
                    string label = !string.IsNullOrWhiteSpace(def.familyId) ? def.familyId : def.name;
                    EditorUtility.DisplayProgressBar(
                        "Recalculate All Families",
                        label,
                        (float)i / families.Count);

                    var profileSet = def.partCalcProfileSet != null ? def.partCalcProfileSet : catalogSet;
                    var recalc = ShipFamilyDefinitionEditor.RecalculateComponentsFromProfiles(
                        def,
                        profileSet,
                        refreshProfiles: false,
                        showDialog: false,
                        saveAssets: false);
                    if (!recalc.success)
                    {
                        failed++;
                        failures.AppendLine($"{label}: {recalc.error}");
                        continue;
                    }

                    var resort = ShipFamilyDefinitionEditor.ResortUpgradeTreeAndRecalculateStats(
                        def,
                        showDialog: false,
                        saveAssets: false);
                    if (!resort.success)
                    {
                        failed++;
                        failures.AppendLine($"{label}: {resort.error}");
                        continue;
                    }

                    succeeded++;
                    componentsUpdated += recalc.updated;
                    cosmetics += recalc.cosmetics;
                    resorted += resort.resortedUnlocked;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string failBlock = failed == 0
                ? string.Empty
                : $"\n\nFailed ({failed}):\n{failures}";
            EditorUtility.DisplayDialog(
                "Recalculate All Families",
                $"Part profiles refreshed: {profilesUpdated}.\n" +
                $"Updated {succeeded} family asset(s).\n" +
                $"Components with ability stats: {componentsUpdated}. Cosmetics zeroed: {cosmetics}.\n" +
                $"Unlocked tiers resorted: {resorted}.{failBlock}",
                "OK");

            Debug.Log(
                $"[ShipFamilyDefinitionCatalog] Recalculate+Resort updated {succeeded} family asset(s), " +
                $"failed {failed}. Profiles refreshed: {profilesUpdated}.");
        }
    }
}
