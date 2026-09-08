#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Writes family jet prefabs into the single
    /// <see cref="ThrusterVfxBank"/> asset and removes leftover per-family settings.
    /// Menu: <b>Titan Orbit → Assign Family Thruster VFX</b>.
    /// </summary>
    public static class ShipThrusterVfxSettingsSetup
    {
        const string JetFlameRoot =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Interactive/JetFlame";
        const string LegacyDefaultAssetPath = "Assets/Resources/ShipThrusterVfxSettings.asset";

        static readonly string[] ColorOrder = { "Blue", "Green", "Purple", "Red", "Yellow" };

        struct FamilyFlameAssign
        {
            public string FamilyId;
            public string SignaturePrefabPath;
            public string StyleFolder;
            public string ColorPrefabFormat;
        }

        static readonly FamilyFlameAssign[] Assignments =
        {
            new FamilyFlameAssign
            {
                FamilyId = "AstroEagle",
                SignaturePrefabPath = JetFlameRoot + "/V2/ModularJetFlame2.prefab",
                StyleFolder = "V2",
                ColorPrefabFormat = "{0}JetFlame2"
            },
            new FamilyFlameAssign
            {
                FamilyId = "CosmicShark",
                SignaturePrefabPath = JetFlameRoot + "/V3/ModularJetFlame3.prefab",
                StyleFolder = "V3",
                ColorPrefabFormat = "{0}JetFlame3"
            },
            new FamilyFlameAssign
            {
                FamilyId = "ForceBadger",
                SignaturePrefabPath = JetFlameRoot + "/V1/RedJetFlame.prefab",
                StyleFolder = "V1",
                ColorPrefabFormat = "{0}JetFlame"
            },
            new FamilyFlameAssign
            {
                FamilyId = "GalaxyRaptor",
                SignaturePrefabPath = JetFlameRoot + "/V3/PurpleJetFlame3.prefab",
                StyleFolder = "V3",
                ColorPrefabFormat = "{0}JetFlame3"
            },
            new FamilyFlameAssign
            {
                FamilyId = "HyperFalcon",
                SignaturePrefabPath = JetFlameRoot + "/V2/YellowJetFlame2.prefab",
                StyleFolder = "V2",
                ColorPrefabFormat = "{0}JetFlame2"
            },
            new FamilyFlameAssign
            {
                FamilyId = "LightFox",
                SignaturePrefabPath = JetFlameRoot + "/Soft/JetFlameSoftYellow.prefab",
                StyleFolder = "Soft",
                ColorPrefabFormat = "JetFlameSoft{0}"
            },
            new FamilyFlameAssign
            {
                FamilyId = "MeteorMantis",
                SignaturePrefabPath = JetFlameRoot + "/Soft/JetFlameSoftGreen.prefab",
                StyleFolder = "Soft",
                ColorPrefabFormat = "JetFlameSoft{0}"
            },
            new FamilyFlameAssign
            {
                FamilyId = "NightAye",
                SignaturePrefabPath = JetFlameRoot + "/V1/PurpleJetFlame.prefab",
                StyleFolder = "V1",
                ColorPrefabFormat = "{0}JetFlame"
            },
            new FamilyFlameAssign
            {
                FamilyId = "ProtonLegacy",
                SignaturePrefabPath = JetFlameRoot + "/V2/RedJetFlame2.prefab",
                StyleFolder = "V2",
                ColorPrefabFormat = "{0}JetFlame2"
            },
            new FamilyFlameAssign
            {
                FamilyId = "SpaceExcalibur",
                SignaturePrefabPath = JetFlameRoot + "/V1/ModularJetFlame.prefab",
                StyleFolder = "V1",
                ColorPrefabFormat = "{0}JetFlame"
            },
            new FamilyFlameAssign
            {
                FamilyId = "StarForce",
                SignaturePrefabPath = JetFlameRoot + "/V3/BlueJetFlame3.prefab",
                StyleFolder = "V3",
                ColorPrefabFormat = "{0}JetFlame3"
            },
            new FamilyFlameAssign
            {
                FamilyId = "StriderOx",
                SignaturePrefabPath = JetFlameRoot + "/V1/GreenJetFlame.prefab",
                StyleFolder = "V1",
                ColorPrefabFormat = "{0}JetFlame"
            },
        };

        /// <summary>
        /// [EDITOR] Refresh <c>Resources/ThrusterVfxBank</c> and delete leftover per-family assets.
        /// </summary>
        [MenuItem("Titan Orbit/Assign Family Thruster VFX")]
        public static void AssignFamilyThrusterVfx()
        {
            EnsureThrusterVfxBank();
            DeleteLegacySettingsAssets();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Titan Orbit] Assign Family Thruster VFX: updated Resources/ThrusterVfxBank.");
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

            if (bank.entries == null)
                bank.entries = new List<ThrusterVfxBank.Entry>();
            bank.entries.Clear();

            for (int i = 0; i < Assignments.Length; i++)
            {
                FamilyFlameAssign row = Assignments[i];
                GameObject signature = AssetDatabase.LoadAssetAtPath<GameObject>(row.SignaturePrefabPath);
                if (signature == null)
                {
                    Debug.LogWarning(
                        "[Titan Orbit] Assign Family Thruster VFX: missing prefab " + row.SignaturePrefabPath);
                    continue;
                }

                var entry = new ThrusterVfxBank.Entry
                {
                    familyId = row.FamilyId,
                    displayName = row.FamilyId,
                    prefab = signature,
                    colorPrefabs = new List<ThrusterVfxBank.ColorPrefab>()
                };

                for (int c = 0; c < ColorOrder.Length; c++)
                {
                    string color = ColorOrder[c];
                    string fileName = string.Format(row.ColorPrefabFormat, color);
                    string path = JetFlameRoot + "/" + row.StyleFolder + "/" + fileName + ".prefab";
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null)
                        continue;

                    entry.colorPrefabs.Add(new ThrusterVfxBank.ColorPrefab
                    {
                        colorName = color,
                        prefab = prefab
                    });
                }

                bank.entries.Add(entry);
            }

            EditorUtility.SetDirty(bank);
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
