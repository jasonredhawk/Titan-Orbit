#if UNITY_EDITOR
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
    /// Points every playable <see cref="ShipFamilyDefinition"/> and Titan visual line
    /// at Colorize materials under UltimateSpaceshipsCreator/Materials.
    /// Prefers root-folder mats over Standard / Extra / Legacy / Alien.
    /// </summary>
    public static class ShipColorizeTeamMaterialAssignEditor
    {
        const string MaterialsRoot = "Assets/UltimateSpaceshipsCreator/Materials";
        const string CatalogPath = "Assets/Resources/MegaShipCatalog.asset";
        const string FamiliesRoot = "Assets/Prefabs/Ships";

        static readonly (TeamId team, string variant, string[] colors)[] TeamSpecs =
        {
            (TeamId.TeamA, "Red", new[] { "Red" }),
            (TeamId.TeamB, "Blue", new[] { "Blue" }),
            (TeamId.TeamC, "Green", new[] { "Green" }),
            (TeamId.TeamD, "Orange", new[] { "Orange", "Yellow", "Gold", "Brown", "Ruby", "Lime" }),
            (TeamId.TeamE, "Purple", new[] { "Purple", "Violet" }),
        };

        [MenuItem("TitanOrbit/Ships/Assign Colorize Team Materials")]
        public static void AssignAll()
        {
            var report = new StringBuilder();
            int families = AssignPlayableFamilies(report);
            AssignMegaVisualFamilies(report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            MegaShipCatalog.InvalidateCache();
            EditorUtility.DisplayDialog(
                "Assign Colorize Team Materials",
                $"Updated {families} playable families and Titan visual lines.\n\n{report}",
                "OK");
        }

        static int AssignPlayableFamilies(StringBuilder report)
        {
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition", new[] { FamiliesRoot });
            int updated = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def == null || string.IsNullOrWhiteSpace(def.familyId))
                    continue;

                Undo.RecordObject(def, "Assign Colorize Team Materials");
                def.teamMaterials ??= new List<ShipFamilyTeamMaterialSet>();
                def.teamMaterials.Clear();

                report.AppendLine(def.familyId);
                for (int t = 0; t < TeamSpecs.Length; t++)
                {
                    var spec = TeamSpecs[t];
                    Material mat = FindBestFamilyMaterial(def.familyId, spec.colors);
                    def.teamMaterials.Add(new ShipFamilyTeamMaterialSet
                    {
                        team = spec.team,
                        variantName = spec.variant,
                        materials = mat != null
                            ? new List<Material> { mat }
                            : new List<Material>(),
                    });
                    report.AppendLine(mat != null
                        ? $"  {spec.team}: {mat.name}"
                        : $"  {spec.team}: MISSING");
                }

                EditorUtility.SetDirty(def);
                updated++;
            }

            return updated;
        }

        static void AssignMegaVisualFamilies(StringBuilder report)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(CatalogPath);
            if (catalog == null)
            {
                report.AppendLine("MegaShipCatalog missing.");
                return;
            }

            Undo.RecordObject(catalog, "Assign Titan Colorize Team Materials");
            catalog.visualFamilyTeamMaterials ??= new List<MegaShipVisualFamilyTeamMaterials>();
            catalog.visualFamilyTeamMaterials.Clear();

            AssignMegaFamily(catalog, MegaShipVisualFamily.CraizanStar, "CraizanStar", splitMainParts: false, report);
            AssignMegaFamily(catalog, MegaShipVisualFamily.GalacticOkamoto, "GalacticOkamoto", splitMainParts: false, report);
            AssignMegaFamily(catalog, MegaShipVisualFamily.GalacticLeopard, "GalacticLeopard", splitMainParts: true, report);

            EditorUtility.SetDirty(catalog);
        }

        static void AssignMegaFamily(
            MegaShipCatalog catalog,
            MegaShipVisualFamily family,
            string folderName,
            bool splitMainParts,
            StringBuilder report)
        {
            var row = new MegaShipVisualFamilyTeamMaterials
            {
                visualFamily = family,
                teamMaterials = new List<ShipFamilyTeamMaterialSet>(),
            };

            report.AppendLine(folderName + " (Titan)");
            for (int t = 0; t < TeamSpecs.Length; t++)
            {
                var spec = TeamSpecs[t];
                var mats = new List<Material>();
                if (splitMainParts)
                {
                    Material main = FindBestNamedMaterial(folderName, spec.colors, "Main");
                    Material parts = FindBestNamedMaterial(folderName, spec.colors, "Parts");
                    if (main != null)
                        mats.Add(main);
                    if (parts != null)
                        mats.Add(parts);
                }
                else
                {
                    Material mat = FindBestNamedMaterial(folderName, spec.colors, token: null);
                    if (mat != null)
                        mats.Add(mat);
                }

                row.teamMaterials.Add(new ShipFamilyTeamMaterialSet
                {
                    team = spec.team,
                    variantName = spec.variant,
                    materials = mats,
                });
                report.AppendLine(mats.Count > 0
                    ? $"  {spec.team}: {string.Join(", ", mats.ConvertAll(m => m.name))}"
                    : $"  {spec.team}: MISSING");
            }

            catalog.visualFamilyTeamMaterials.Add(row);
        }

        static Material FindBestFamilyMaterial(string familyId, string[] colors)
        {
            return FindBestNamedMaterial(familyId, colors, token: null);
        }

        static Material FindBestNamedMaterial(string familyFolder, string[] colors, string token)
        {
            string folder = $"{MaterialsRoot}/{familyFolder}";
            if (!AssetDatabase.IsValidFolder(folder))
                return null;

            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { folder });
            Material best = null;
            int bestScore = int.MinValue;
            for (int c = 0; c < colors.Length; c++)
            {
                string color = colors[c];
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    string file = Path.GetFileNameWithoutExtension(path);
                    if (!NameMatches(file, familyFolder, color, token))
                        continue;

                    int score = ScorePath(path) + ((colors.Length - c) * 20);
                    if (score <= bestScore)
                        continue;
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat == null)
                        continue;
                    best = mat;
                    bestScore = score;
                }

                if (best != null)
                    return best;
            }

            return best;
        }

        static bool NameMatches(string fileName, string family, string color, string token)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            string n = fileName.Replace(" ", string.Empty);
            if (token != null)
                return n.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0
                    && n.EndsWith("_" + color, System.StringComparison.OrdinalIgnoreCase);
            return n.Equals(family + "_" + color, System.StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("_" + color, System.StringComparison.OrdinalIgnoreCase);
        }

        static int ScorePath(string path)
        {
            string p = path.Replace('\\', '/').ToLowerInvariant();
            if (p.Contains("/alien/") || p.Contains("/legacy/"))
                return 0;
            if (!p.Contains("/standard/") && !p.Contains("/extra/"))
                return 50;
            if (p.Contains("/extra/"))
                return 20;
            if (p.Contains("/standard/"))
                return 10;
            return 5;
        }
    }
}
#endif
