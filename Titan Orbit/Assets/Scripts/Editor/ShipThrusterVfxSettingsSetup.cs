#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Writes the four JetFlame types and their team-color prefabs into
    /// <see cref="ThrusterVfxBank"/>. Menu: <b>Titan Orbit → Assign Thruster VFX</b>.
    /// </summary>
    public static class ShipThrusterVfxSettingsSetup
    {
        const string JetFlameRoot =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Interactive/JetFlame";
        const string LegacyDefaultAssetPath = "Assets/Resources/ShipThrusterVfxSettings.asset";

        struct TeamColorAssign
        {
            public TeamId Team;
            public string ColorName;
            public string FileColor;
        }

        /// <summary>Team D is orange. Those prefabs are named Yellow.</summary>
        static readonly TeamColorAssign[] TeamColors =
        {
            new TeamColorAssign { Team = TeamId.TeamA, ColorName = "Red", FileColor = "Red" },
            new TeamColorAssign { Team = TeamId.TeamB, ColorName = "Blue", FileColor = "Blue" },
            new TeamColorAssign { Team = TeamId.TeamC, ColorName = "Green", FileColor = "Green" },
            new TeamColorAssign { Team = TeamId.TeamD, ColorName = "Orange", FileColor = "Yellow" },
            new TeamColorAssign { Team = TeamId.TeamE, ColorName = "Purple", FileColor = "Purple" },
        };

        struct StyleAssign
        {
            public string Name;
            public string Folder;
            public string ColorPrefabFormat;
            public string FallbackFileName;
        }

        static readonly StyleAssign[] Styles =
        {
            new StyleAssign
            {
                Name = "V1",
                Folder = "V1",
                ColorPrefabFormat = "{0}JetFlame",
                FallbackFileName = "ModularJetFlame"
            },
            new StyleAssign
            {
                Name = "V2",
                Folder = "V2",
                ColorPrefabFormat = "{0}JetFlame2",
                FallbackFileName = "ModularJetFlame2"
            },
            new StyleAssign
            {
                Name = "V3",
                Folder = "V3",
                ColorPrefabFormat = "{0}JetFlame3",
                FallbackFileName = "ModularJetFlame3"
            },
            new StyleAssign
            {
                Name = "Soft",
                Folder = "Soft",
                ColorPrefabFormat = "JetFlameSoft{0}",
                FallbackFileName = "JetFlameSoftWhite"
            },
        };

        /// <summary>
        /// [EDITOR] Refresh <c>Resources/ThrusterVfxBank</c> from the JetFlame folders.
        /// </summary>
        [MenuItem("Titan Orbit/Assign Thruster VFX")]
        public static void AssignThrusterVfx()
        {
            EnsureThrusterVfxBank();
            DeleteLegacySettingsAssets();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Titan Orbit] Assign Thruster VFX: updated Resources/ThrusterVfxBank (V1, V2, V3, Soft × team colors).");
        }

        static void EnsureThrusterVfxBank()
        {
            var bank = AssetDatabase.LoadAssetAtPath<ThrusterVfxBank>(ThrusterVfxBank.ResourcesAssetPath);
            if (bank == null)
            {
                string folder = Path.GetDirectoryName(ThrusterVfxBank.ResourcesAssetPath);
                if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                    Directory.CreateDirectory(folder);

                bank = ScriptableObject.CreateInstance<ThrusterVfxBank>();
                AssetDatabase.CreateAsset(bank, ThrusterVfxBank.ResourcesAssetPath);
            }

            if (bank.thrusters == null)
                bank.thrusters = new List<ThrusterVfxBank.ThrusterType>();
            bank.thrusters.Clear();

            for (int i = 0; i < Styles.Length; i++)
            {
                StyleAssign style = Styles[i];
                var row = new ThrusterVfxBank.ThrusterType
                {
                    name = style.Name,
                    teamColors = new List<ThrusterVfxBank.TeamColorFlame>(),
                    fallbackPrefab = LoadPrefab(style.Folder, style.FallbackFileName)
                };

                if (row.fallbackPrefab == null)
                {
                    Debug.LogWarning(
                        "[Titan Orbit] Assign Thruster VFX: missing fallback "
                        + style.Folder + "/" + style.FallbackFileName);
                }

                for (int c = 0; c < TeamColors.Length; c++)
                {
                    TeamColorAssign color = TeamColors[c];
                    string fileName = string.Format(style.ColorPrefabFormat, color.FileColor);
                    GameObject prefab = LoadPrefab(style.Folder, fileName);
                    if (prefab == null)
                    {
                        Debug.LogWarning(
                            "[Titan Orbit] Assign Thruster VFX: missing "
                            + style.Name + " " + color.ColorName + " at " + style.Folder + "/" + fileName);
                        continue;
                    }

                    row.teamColors.Add(new ThrusterVfxBank.TeamColorFlame
                    {
                        team = color.Team,
                        colorName = color.ColorName,
                        prefab = prefab
                    });
                }

                bank.thrusters.Add(row);
                CopyStylePrefabToResources(
                    JetFlameRoot + "/" + style.Folder + "/" + style.FallbackFileName + ".prefab",
                    "Assets/Resources/" + style.FallbackFileName + ".prefab");
            }

            CopyColoredStylePrefabsToResources();
            EditorUtility.SetDirty(bank);
        }

        static GameObject LoadPrefab(string folder, string fileName)
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(
                JetFlameRoot + "/" + folder + "/" + fileName + ".prefab");
        }

        static void CopyStylePrefabToResources(string sourcePath, string destPath)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath) == null)
                return;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(destPath) != null)
                return;
            AssetDatabase.CopyAsset(sourcePath, destPath);
        }

        static void CopyColoredStylePrefabsToResources()
        {
            for (int s = 0; s < Styles.Length; s++)
            {
                StyleAssign style = Styles[s];
                for (int c = 0; c < TeamColors.Length; c++)
                {
                    string fileName = string.Format(style.ColorPrefabFormat, TeamColors[c].FileColor);
                    CopyStylePrefabToResources(
                        JetFlameRoot + "/" + style.Folder + "/" + fileName + ".prefab",
                        "Assets/Resources/" + fileName + ".prefab");
                }
            }
        }

        static void DeleteLegacySettingsAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(LegacyDefaultAssetPath) != null)
                AssetDatabase.DeleteAsset(LegacyDefaultAssetPath);

            string[] leftover = AssetDatabase.FindAssets(
                "ThrusterVfxSettings t:ScriptableObject",
                new[] { "Assets/Prefabs/Ships" });
            for (int i = 0; i < leftover.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(leftover[i]);
                if (string.IsNullOrEmpty(path) || !path.EndsWith("/ThrusterVfxSettings.asset"))
                    continue;
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
#endif
