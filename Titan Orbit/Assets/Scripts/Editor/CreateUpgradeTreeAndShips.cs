using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Creates ShipData assets and UpgradeTree for: L1(1)→L2(2)→L3(4)→L4(6)→L5(8)→L6(9)→L7(4 MEGA).
    /// Branch index 0 = fighter focus, last = miner focus, middle = blend.
    /// Run: Titan Orbit > Create Upgrade Tree And Ships
    /// </summary>
    public static class CreateUpgradeTreeAndShips
    {
        private const string SHIPS_DATA_FOLDER = "Assets/Data/Ships";
        private const string WEAPON_CONFIGS_FOLDER = "Assets/Data/WeaponConfigs";
        private const string PREFABS_SHIPS_FOLDER = "Assets/Prefabs/Ships";
        private const string UPGRADE_TREE_PATH = "Assets/Data/UpgradeTree.asset";
        private const string STARSPARROW_MODULES_FOLDER = "Assets/StarSparrow/Prefabs/Modules";
        private const string STARSPARROW_MODULAR_EXAMPLES_FOLDER = "Assets/StarSparrow/Prefabs/Modular Examples";
        private const string STARSPARROW_EXAMPLES_FOLDER = "Assets/StarSparrow/Prefabs/Examples";
        private const string STARSPARROW_MATERIALS_FOLDER = "Assets/StarSparrow/Materials";
        private const string STARSPARROW_URP_MATERIALS_FOLDER = "Assets/StarSparrow/Materials/GeneratedURP";
        private const string STARSPARROW_PREFABS_FOLDER = "Assets/StarSparrow/Prefabs";
        private const string HIREZ_ROOT_FOLDER = "Assets/HiRezSpaceshipsCreatorFree";
        private const string HIREZ_EXAMPLES_FOLDER = "Assets/HiRezSpaceshipsCreatorFree/Prefabs/Examples";
        private const string HIREZ_MATERIALS_FOLDER = "Assets/HiRezSpaceshipsCreatorFree/Materials";
        private const string HIREZ_URP_MATERIALS_FOLDER = "Assets/HiRezSpaceshipsCreatorFree/Materials/GeneratedURP";
        private const string STARTER_SHIP_PREFAB_PATH = "Assets/Prefabs/Ships/Starship_Lv1_0.prefab";

        private static readonly int[] CountPerLevel = { 2, 4, 6, 8, 9, 4 }; // levels 2-7
        private static readonly string[] StarSparrowColorVariants =
        {
            "Red", "Blue", "Green", "Purple", "Grey", "White", "Yellow", "Orange", "Cyan", "Black"
        };
        private static readonly Dictionary<string, GameObject> ModulePrefabCache = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, List<TemplatePart>> TemplatePartsCache = new Dictionary<string, List<TemplatePart>>();
        private static readonly Dictionary<string, Material> HiRezConvertedBySourcePath = new Dictionary<string, Material>();
        private static readonly Dictionary<string, Material> StarConvertedBySourcePath = new Dictionary<string, Material>();

        private class TemplatePart
        {
            public string moduleKey;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
        }

        [MenuItem("Titan Orbit/Create Upgrade Tree And Ships")]
        public static void CreateAll()
        {
            EnsureFolders();
            EnsureStarterShipPrefabAsset();
            CreateOrLoadLevel1Starter();
            List<List<ShipData>> shipDataByLevel = CreateAllShipDataAssets();
            UpgradeTree tree = CreateOrLoadUpgradeTree(shipDataByLevel);
            CreateShipPrefabs(shipDataByLevel);
            AssignStarterShipDataToDefaultPrefab();
            AssignUpgradeTreeInScene(tree);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Upgrade tree created: L1(1)→L2(2)→L3(4)→L4(6)→L5(8)→L6(9)→L7(4 MEGA). Level 7 requires home planet 6 + full gems.");
        }

        [MenuItem("Titan Orbit/Rebuild Ship Prefabs (Unique Designs)")]
        public static void RebuildShipPrefabs()
        {
            EnsureFolders();
            EnsureStarterShipPrefabAsset();
            // Strictly lightweight rebuild: only assign existing example prefabs to ShipData.
            // No prefab cloning/saving, no material conversion, no visual reconstruction.
            var shipDataByLevel = new List<List<ShipData>>();
            for (int li = 0; li < CountPerLevel.Length; li++)
            {
                int level = li + 2;
                int count = CountPerLevel[li];
                var list = new List<ShipData>();
                for (int bi = 0; bi < count; bi++)
                {
                    var data = AssetDatabase.LoadAssetAtPath<ShipData>($"{SHIPS_DATA_FOLDER}/ShipData_Level{level}_{bi}.asset");
                    if (data != null) list.Add(data);
                }
                if (list.Count > 0) shipDataByLevel.Add(list);
            }
            if (shipDataByLevel.Count == 0)
            {
                Debug.LogWarning("No ShipData assets found. Run 'Create Upgrade Tree And Ships' first.");
                return;
            }
            for (int li = 0; li < shipDataByLevel.Count; li++)
            {
                int count = shipDataByLevel[li].Count;
                foreach (var data in shipDataByLevel[li])
                {
                    float blend = count <= 1 ? 0.5f : (float)data.branchIndex / (count - 1);
                    data.shipColor = GetUniqueShipColor(data.shipLevel, data.branchIndex, blend);
                    EditorUtility.SetDirty(data);
                }
            }
            CreateShipPrefabs(shipDataByLevel);
            AssignStarterShipDataToDefaultPrefab();
            AssetDatabase.SaveAssets();
            Debug.Log($"Rebuild complete (reference-only): {shipDataByLevel.Sum(l => l.Count)} ShipData assets mapped to existing example prefabs.");
        }

        [MenuItem("Titan Orbit/Fix StarSparrow Materials (URP + Prefabs)")]
        public static void FixStarSparrowMaterialsAndPrefabs()
        {
            ConvertStarSparrowMaterialsAndPrefabsInternal(forceRebuildConvertedMaterials: true, logSummary: true);
        }

        [MenuItem("Titan Orbit/Fix HiRez Materials (URP + Prefabs)")]
        public static void FixHiRezMaterialsAndPrefabs()
        {
            ConvertHiRezMaterialsAndPrefabsInternal(forceRebuildConvertedMaterials: true, logSummary: true);
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Data"))
                AssetDatabase.CreateFolder("Assets", "Data");
            if (!AssetDatabase.IsValidFolder(SHIPS_DATA_FOLDER))
                AssetDatabase.CreateFolder("Assets/Data", "Ships");
            if (!AssetDatabase.IsValidFolder(WEAPON_CONFIGS_FOLDER))
                AssetDatabase.CreateFolder("Assets/Data", "WeaponConfigs");
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                return;
            if (!AssetDatabase.IsValidFolder(PREFABS_SHIPS_FOLDER))
                AssetDatabase.CreateFolder("Assets/Prefabs", "Ships");
        }

        private static void CreateOrLoadLevel1Starter()
        {
            string path = $"{SHIPS_DATA_FOLDER}/ShipData_Level1_0_Starter.asset";
            if (AssetDatabase.LoadAssetAtPath<ShipData>(path) != null) return;

            var data = ScriptableObject.CreateInstance<ShipData>();
            data.shipLevel = 1;
            data.branchIndex = 0;
            data.focusType = ShipFocusType.Fighter;
            data.shipName = "Starter";
            SetBaseStats(data, 1, 0f);
            data.weaponConfig = GetOrCreateWeaponConfig(1, 0);
            data.shipPrefab = GetStarterShipPrefabAsset();
            AssetDatabase.CreateAsset(data, path);
        }

        private static List<List<ShipData>> CreateAllShipDataAssets()
        {
            var result = new List<List<ShipData>>();
            for (int li = 0; li < CountPerLevel.Length; li++)
            {
                int level = li + 2;
                int count = CountPerLevel[li];
                var list = new List<ShipData>();
                for (int bi = 0; bi < count; bi++)
                {
                    float blend = count <= 1 ? 0.5f : (float)bi / (count - 1); // 0=fighter, 1=miner
                    var data = ScriptableObject.CreateInstance<ShipData>();
                    data.shipLevel = level;
                    data.branchIndex = bi;
                    data.focusType = blend < 0.5f ? ShipFocusType.Fighter : ShipFocusType.Miner;
                    data.shipName = level == 7 ? $"MEGA {bi + 1}" : $"{level}.{bi + 1}";
                    SetBaseStats(data, level, blend);
                    data.weaponConfig = GetOrCreateWeaponConfig(level, bi);
                    string assetPath = $"{SHIPS_DATA_FOLDER}/ShipData_Level{level}_{bi}.asset";
                    AssetDatabase.CreateAsset(data, assetPath);
                    list.Add(data);
                }
                result.Add(list);
            }
            return result;
        }

        /// <summary>Speed curve: L1 fastest, L2/L3 slower, L4–L6 faster again, L7 mega ships slow. Scaled up for physics-based movement.</summary>
        private static float GetMovementSpeedForLevel(int level)
        {
            switch (level)
            {
                case 1: return 9.5f;   // fastest (starter)
                case 2: return 8.2f;
                case 3: return 7.2f;   // slowest of mid-levels
                case 4: return 7.8f;   // start getting faster again
                case 5: return 8.2f;
                case 6: return 8.8f;
                case 7: return 5.5f;   // mega ships slow
                default: return 8f;
            }
        }

        private static void SetBaseStats(ShipData data, int level, float fighterToMinerBlend)
        {
            // Fighter: lighter mass, smaller size. Transport/Miner: heavier, larger, especially as level increases.
            float baseMassFighter = Mathf.Lerp(0.7f, 1.2f, (level - 1) / 6f);   // 0.7 @ L1 → 1.2 @ L7
            float baseMassMiner = Mathf.Lerp(1.8f, 4.5f, (level - 1) / 6f);    // 1.8 @ L1 → 4.5 @ L7
            data.baseMass = Mathf.Lerp(baseMassFighter, baseMassMiner, fighterToMinerBlend);
            if (level == 7) data.baseMass *= 1.15f;

            float visualScaleFighter = 0.82f + (level - 1) * 0.03f;  // smaller: 0.82 @ L1 → ~1.0 @ L7
            float visualScaleMiner = 1f + (level - 1) * 0.08f;        // larger: 1.0 @ L1 → ~1.5 @ L7
            data.visualScale = Mathf.Lerp(visualScaleFighter, visualScaleMiner, fighterToMinerBlend);
            if (level == 7) data.visualScale *= 1.12f;

            float mine = Mathf.Lerp(10f, 22f, fighterToMinerBlend) + (level - 1) * 3f;
            float health = Mathf.Lerp(130f, 95f, fighterToMinerBlend) + (level - 1) * 20f;
            float cap = Mathf.Lerp(110f, 180f, fighterToMinerBlend) + (level - 1) * 35f;
            if (level == 7) { mine *= 1.5f; health *= 2f; cap *= 1.8f; }
            data.baseMovementSpeed = GetMovementSpeedForLevel(level);
            data.baseMaxHealth = health;
            data.baseHealthRegenRate = 1.2f;
            data.baseRotationSpeed = 180f;
            data.baseGemCapacity = cap;
            data.basePeopleCapacity = 10f + (level - 1) * 2f;
            data.baseEnergyCapacity = 50f + (level - 1) * 8f;
            data.baseEnergyRegenRate = 5f;
            data.baseMiningRate = mine;
            data.miningMultiplier = Mathf.Lerp(1f, 1.3f, fighterToMinerBlend);
            data.shipColor = GetUniqueShipColor(level, data.branchIndex, fighterToMinerBlend);
        }

        /// <summary>Generates a unique accent color per ship (level + branch) for visual identity.</summary>
        private static Color GetUniqueShipColor(int level, int branchIndex, float fighterToMinerBlend)
        {
            int seed = level * 100 + branchIndex;
            float h = ((seed * 137) % 360) / 360f;
            float s = 0.5f + 0.3f * fighterToMinerBlend;
            float v = 0.75f + 0.2f * (1f - Mathf.Abs(fighterToMinerBlend - 0.5f) * 2f);
            return Color.HSVToRGB(h, s, v);
        }

        /// <summary>Load existing or create preset WeaponConfig for this ship (level, branch).</summary>
        private static WeaponConfig GetOrCreateWeaponConfig(int level, int branchIndex)
        {
            string assetPath = $"{WEAPON_CONFIGS_FOLDER}/WeaponConfig_Level{level}_{branchIndex}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<WeaponConfig>(assetPath);
            if (existing != null) return existing;

            var config = ScriptableObject.CreateInstance<WeaponConfig>();
            config.displayName = level == 1 ? "Starter" : (level == 7 ? $"MEGA {branchIndex + 1}" : $"{level}.{branchIndex + 1}");
            config.cannons = BuildPresetCannons(level, branchIndex);
            AssetDatabase.CreateAsset(config, assetPath);
            return config;
        }

        private static List<CannonConfig> BuildPresetCannons(int level, int branchIndex)
        {
            var list = new List<CannonConfig>();
            float levelScale = 0.9f + level * 0.08f;
            if (level == 7) levelScale *= 1.1f;

            if (level == 1)
            {
                list.Add(new CannonConfig
                {
                    fireRate = 2.5f,
                    energyCostPerShot = 2f,
                    damagePerBullet = 8f,
                    directionAngle = 0f,
                    spreadType = CannonSpreadType.Straight,
                    bulletScale = 0.6f,
                    bulletSpeed = 20f
                });
                return list;
            }

            if (level == 2)
            {
                if (branchIndex == 0)
                {
                    list.Add(new CannonConfig
                    {
                        fireRate = 2.2f,
                        energyCostPerShot = 2.5f,
                        damagePerBullet = 9f,
                        directionAngle = 0f,
                        spreadType = CannonSpreadType.FixedSpread,
                        spreadAngleMin = -12f,
                        spreadAngleMax = 12f,
                        spreadProjectileCount = 3,
                        bulletScale = 0.65f,
                        bulletSpeed = 22f
                    });
                }
                else
                {
                    list.Add(new CannonConfig { fireRate = 2.5f, energyCostPerShot = 2.2f, damagePerBullet = 8f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 0.6f, localOffsetX = -0.15f, bulletSpeed = 21f });
                    list.Add(new CannonConfig { fireRate = 2.5f, energyCostPerShot = 2.2f, damagePerBullet = 8f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 0.6f, localOffsetX = 0.15f, bulletSpeed = 21f });
                }
                return list;
            }

            if (level == 3)
            {
                if (branchIndex == 0)
                {
                    list.Add(new CannonConfig { fireRate = 3f, energyCostPerShot = 2f, damagePerBullet = 7f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 0.55f, localOffsetX = -0.2f, bulletSpeed = 23f });
                    list.Add(new CannonConfig { fireRate = 3f, energyCostPerShot = 2f, damagePerBullet = 7f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 0.55f, localOffsetX = 0.2f, bulletSpeed = 23f });
                    list.Add(new CannonConfig { fireRate = 1.2f, energyCostPerShot = 12f, damagePerBullet = 22f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 1.4f, bulletSpeed = 20f });
                }
                else if (branchIndex == 1)
                {
                    list.Add(new CannonConfig
                    {
                        fireRate = 6f,
                        energyCostPerShot = 1.5f,
                        damagePerBullet = 4f,
                        directionAngle = 0f,
                        spreadType = CannonSpreadType.RandomSpread,
                        spreadAngleMin = -15f,
                        spreadAngleMax = 15f,
                        bulletScale = 0.5f,
                        bulletSpeed = 24f
                    });
                }
                else if (branchIndex == 2)
                {
                    list.Add(new CannonConfig { fireRate = 2.8f, energyCostPerShot = 2.5f, damagePerBullet = 10f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 0.7f, localOffsetX = -0.18f, bulletSpeed = 22f });
                    list.Add(new CannonConfig { fireRate = 2.8f, energyCostPerShot = 2.5f, damagePerBullet = 10f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 0.7f, localOffsetX = 0.18f, bulletSpeed = 22f });
                }
                else
                {
                    list.Add(new CannonConfig { fireRate = 0.7f, energyCostPerShot = 25f, damagePerBullet = 35f, directionAngle = 0f, spreadType = CannonSpreadType.Straight, bulletScale = 1.8f, bulletSpeed = 18f });
                }
                return list;
            }

            if (level == 4)
            {
                int bc = branchIndex % 4;
                if (bc == 0) { list.Add(new CannonConfig { fireRate = 2.5f, energyCostPerShot = 3f, damagePerBullet = 12f, spreadType = CannonSpreadType.FixedSpread, spreadAngleMin = -8f, spreadAngleMax = 8f, spreadProjectileCount = 3, bulletScale = 0.75f, bulletSpeed = 23f }); }
                else if (bc == 1) { list.Add(new CannonConfig { fireRate = 2.2f, energyCostPerShot = 2.8f, damagePerBullet = 10f, localOffsetX = -0.2f, bulletScale = 0.7f, bulletSpeed = 22f }); list.Add(new CannonConfig { fireRate = 2.2f, energyCostPerShot = 2.8f, damagePerBullet = 10f, localOffsetX = 0.2f, bulletScale = 0.7f, bulletSpeed = 22f }); }
                else if (bc == 2) { list.Add(new CannonConfig { fireRate = 5f, energyCostPerShot = 2f, damagePerBullet = 5f, spreadType = CannonSpreadType.RandomSpread, spreadAngleMin = -10f, spreadAngleMax = 10f, bulletScale = 0.55f, bulletSpeed = 25f }); }
                else { list.Add(new CannonConfig { fireRate = 1f, energyCostPerShot = 15f, damagePerBullet = 28f, bulletScale = 1.5f, bulletSpeed = 19f }); }
                return list;
            }

            if (level == 5)
            {
                int bc = branchIndex % 5;
                if (bc <= 1) { list.Add(new CannonConfig { fireRate = 2.8f, energyCostPerShot = 3f, damagePerBullet = 13f, localOffsetX = -0.22f, bulletScale = 0.8f, bulletSpeed = 23f }); list.Add(new CannonConfig { fireRate = 2.8f, energyCostPerShot = 3f, damagePerBullet = 13f, localOffsetX = 0.22f, bulletScale = 0.8f, bulletSpeed = 23f }); }
                else if (bc == 2) { list.Add(new CannonConfig { fireRate = 2f, energyCostPerShot = 4f, damagePerBullet = 14f, spreadType = CannonSpreadType.FixedSpread, spreadAngleMin = -10f, spreadAngleMax = 10f, spreadProjectileCount = 3, bulletScale = 0.8f, bulletSpeed = 22f }); }
                else if (bc == 3) { list.Add(new CannonConfig { fireRate = 6f, energyCostPerShot = 2f, damagePerBullet = 6f, spreadType = CannonSpreadType.RandomSpread, spreadAngleMin = -12f, spreadAngleMax = 12f, bulletScale = 0.6f, bulletSpeed = 24f }); }
                else { list.Add(new CannonConfig { fireRate = 1.2f, energyCostPerShot = 18f, damagePerBullet = 32f, bulletScale = 1.6f, bulletSpeed = 20f }); }
                return list;
            }

            if (level == 6)
            {
                int bc = branchIndex % 6;
                if (bc == 0 || bc == 1) { list.Add(new CannonConfig { fireRate = 3f, energyCostPerShot = 3.2f, damagePerBullet = 14f, localOffsetX = -0.25f, bulletScale = 0.85f, bulletSpeed = 24f }); list.Add(new CannonConfig { fireRate = 3f, energyCostPerShot = 3.2f, damagePerBullet = 14f, localOffsetX = 0.25f, bulletScale = 0.85f, bulletSpeed = 24f }); }
                else if (bc == 2) { list.Add(new CannonConfig { fireRate = 2.2f, energyCostPerShot = 4f, damagePerBullet = 15f, spreadType = CannonSpreadType.FixedSpread, spreadAngleMin = -10f, spreadAngleMax = 10f, spreadProjectileCount = 4, bulletScale = 0.8f, bulletSpeed = 23f }); }
                else if (bc == 3) { list.Add(new CannonConfig { fireRate = 6.5f, energyCostPerShot = 2.2f, damagePerBullet = 6f, spreadType = CannonSpreadType.RandomSpread, spreadAngleMin = -14f, spreadAngleMax = 14f, bulletScale = 0.6f, bulletSpeed = 25f }); }
                else if (bc == 4) { list.Add(new CannonConfig { fireRate = 1.5f, energyCostPerShot = 20f, damagePerBullet = 38f, bulletScale = 1.7f, bulletSpeed = 21f }); }
                else { list.Add(new CannonConfig { fireRate = 2.5f, energyCostPerShot = 3.5f, damagePerBullet = 12f, localOffsetX = -0.3f, bulletScale = 0.75f, bulletSpeed = 23f }); list.Add(new CannonConfig { fireRate = 2.5f, energyCostPerShot = 3.5f, damagePerBullet = 12f, localOffsetX = 0.3f, bulletScale = 0.75f, bulletSpeed = 23f }); }
                return list;
            }

            if (level == 7)
            {
                int bc = branchIndex % 4;
                if (bc == 0) { list.Add(new CannonConfig { fireRate = 3.5f, energyCostPerShot = 4f, damagePerBullet = 16f, localOffsetX = -0.35f, bulletScale = 1f, bulletSpeed = 25f }); list.Add(new CannonConfig { fireRate = 3.5f, energyCostPerShot = 4f, damagePerBullet = 16f, localOffsetX = 0.35f, bulletScale = 1f, bulletSpeed = 25f }); list.Add(new CannonConfig { fireRate = 1.5f, energyCostPerShot = 14f, damagePerBullet = 28f, bulletScale = 1.3f, bulletSpeed = 22f }); }
                else if (bc == 1) { list.Add(new CannonConfig { fireRate = 2.5f, energyCostPerShot = 5f, damagePerBullet = 18f, spreadType = CannonSpreadType.FixedSpread, spreadAngleMin = -15f, spreadAngleMax = 15f, spreadProjectileCount = 4, bulletScale = 0.9f, bulletSpeed = 24f }); }
                else if (bc == 2) { list.Add(new CannonConfig { fireRate = 7f, energyCostPerShot = 2.5f, damagePerBullet = 8f, spreadType = CannonSpreadType.RandomSpread, spreadAngleMin = -18f, spreadAngleMax = 18f, bulletScale = 0.7f, bulletSpeed = 26f }); }
                else { list.Add(new CannonConfig { fireRate = 1f, energyCostPerShot = 35f, damagePerBullet = 55f, bulletScale = 2f, bulletSpeed = 20f }); }
                return list;
            }

            list.Add(new CannonConfig { fireRate = 2.5f, energyCostPerShot = 3f, damagePerBullet = 10f, bulletScale = 0.7f, bulletSpeed = 22f });
            return list;
        }

        private static UpgradeTree CreateOrLoadUpgradeTree(List<List<ShipData>> shipDataByLevel)
        {
            var tree = AssetDatabase.LoadAssetAtPath<UpgradeTree>(UPGRADE_TREE_PATH);
            if (tree == null) tree = ScriptableObject.CreateInstance<UpgradeTree>();
            SerializedObject so = new SerializedObject(tree);

            string[] levelProps = { "level2Ships", "level3Ships", "level4Ships", "level5Ships", "level6Ships", "level7Ships" };
            for (int li = 0; li < CountPerLevel.Length; li++)
            {
                var listProp = so.FindProperty(levelProps[li]);
                listProp.ClearArray();
                int count = CountPerLevel[li];
                var fromIndices = GetCanUpgradeFromBranchIndices(li, count);
                for (int j = 0; j < count; j++)
                {
                    listProp.InsertArrayElementAtIndex(j);
                    var elem = listProp.GetArrayElementAtIndex(j);
                    var shipData = shipDataByLevel[li][j];
                    elem.FindPropertyRelative("shipData").objectReferenceValue = shipData;
                    elem.FindPropertyRelative("shipName").stringValue = shipData.shipName;
                    elem.FindPropertyRelative("focusType").enumValueIndex = (int)shipData.focusType;
                    var canFrom = elem.FindPropertyRelative("canUpgradeFromBranchIndices");
                    canFrom.ClearArray();
                    foreach (int idx in fromIndices[j])
                    {
                        canFrom.InsertArrayElementAtIndex(canFrom.arraySize);
                        canFrom.GetArrayElementAtIndex(canFrom.arraySize - 1).intValue = idx;
                    }
                }
            }

            // Ensure level 2 cost = 100 so full starter ship (cap 100) can upgrade
            var costProp = so.FindProperty("gemCostsPerLevel");
            if (costProp != null && costProp.isArray)
            {
                float[] costs = { 0f, 100f, 100f, 250f, 500f, 1000f, 2000f, 15000f }; // indices 1-7 = levels 1-7
                costProp.ClearArray();
                for (int i = 0; i < costs.Length; i++)
                {
                    costProp.InsertArrayElementAtIndex(i);
                    costProp.GetArrayElementAtIndex(i).floatValue = costs[i];
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            if (!AssetDatabase.Contains(tree)) AssetDatabase.CreateAsset(tree, UPGRADE_TREE_PATH);
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            AssetDatabase.CopyAsset(UPGRADE_TREE_PATH, "Assets/Resources/UpgradeTree.asset");
            return tree;
        }

        /// <summary>For each ship at this level, which previous-level branch indices can upgrade to it. Level index li: 0=L2, 1=L3, ... 5=L7.</summary>
        private static List<int>[] GetCanUpgradeFromBranchIndices(int li, int count)
        {
            int prevCount = li == 0 ? 1 : CountPerLevel[li - 1];
            var result = new List<int>[count];
            for (int j = 0; j < count; j++) result[j] = new List<int>();

            if (li == 0)
            {
                result[0].Add(0); result[1].Add(0);
                return result;
            }
            if (li == 1)
            {
                result[0].Add(0); result[1].Add(0); result[2].Add(1); result[3].Add(1);
                return result;
            }
            if (li == 2)
            {
                result[0].Add(0);
                result[1].Add(0); result[1].Add(1);
                result[2].Add(1); result[2].Add(2);
                result[3].Add(2); result[3].Add(3);
                result[4].Add(3); result[5].Add(3);
                return result;
            }
            if (li == 3)
            {
                for (int j = 0; j < 6; j++) { result[j].Add(j); if (j < 5) result[j].Add(j + 1); }
                result[6].Add(5); result[7].Add(5);
                return result;
            }
            if (li == 4)
            {
                for (int j = 0; j < 9; j++)
                {
                    if (j > 0) result[j].Add(j - 1);
                    if (j < 8) result[j].Add(j);
                }
                return result;
            }
            if (li == 5)
            {
                result[0].Add(0); result[0].Add(3); result[0].Add(4); result[0].Add(8);
                result[1].Add(0); result[1].Add(1); result[1].Add(4); result[1].Add(5); result[1].Add(8);
                result[2].Add(1); result[2].Add(2); result[2].Add(5); result[2].Add(6);
                result[3].Add(2); result[3].Add(3); result[3].Add(6); result[3].Add(7);
                return result;
            }
            return result;
        }

        private static void CreateShipPrefabs(List<List<ShipData>> shipDataByLevel)
        {
            var examples = GetCombinedExampleShipPrefabs();
            if (examples == null || examples.Count == 0)
            {
                Debug.LogWarning("No example prefabs found in HiRez/StarSparrow example folders.");
                return;
            }

            int assigned = 0;
            int globalIndex = 0;
            for (int li = 0; li < shipDataByLevel.Count; li++)
            {
                foreach (var data in shipDataByLevel[li])
                {
                    GameObject example = examples[globalIndex % examples.Count];
                    globalIndex++;
                    if (data == null || example == null) continue;

                    // Direct mapping to authored example prefabs (reused cyclically).
                    var so = new SerializedObject(data);
                    so.FindProperty("shipPrefab").objectReferenceValue = example;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(data);
                    assigned++;
                }
            }
            Debug.Log($"Assigned {assigned} ShipData assets to example prefabs (reused from {examples.Count} sources).");
        }

        private static void AssignStarterShipDataToDefaultPrefab()
        {
            GameObject first = GetStarterShipPrefabAsset();
            if (first == null)
            {
                var examples = GetCombinedExampleShipPrefabs();
                if (examples == null || examples.Count == 0) return;
                first = FindPreferredStarterExamplePrefab(examples);
            }

            string[] starterPaths =
            {
                $"{SHIPS_DATA_FOLDER}/ShipData_Level1_0_Starter.asset",
                $"{SHIPS_DATA_FOLDER}/ShipData_Level1_Starter.asset"
            };
            foreach (string p in starterPaths)
            {
                var starter = AssetDatabase.LoadAssetAtPath<ShipData>(p);
                if (starter == null) continue;
                var so = new SerializedObject(starter);
                so.FindProperty("shipPrefab").objectReferenceValue = first;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(starter);
            }
        }

        private static GameObject GetStarterShipPrefabAsset()
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(STARTER_SHIP_PREFAB_PATH);
        }

        private static GameObject EnsureStarterShipPrefabAsset()
        {
            EnsureAssetFolder(PREFABS_SHIPS_FOLDER);
            var examples = GetCombinedExampleShipPrefabs();
            if (examples == null || examples.Count == 0) return GetStarterShipPrefabAsset();
            GameObject source = FindPreferredStarterExamplePrefab(examples);
            if (source == null) return GetStarterShipPrefabAsset();

            // Build from prefab contents to produce a real prefab asset root, not a variant-style PrefabInstance reference.
            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath)) return GetStarterShipPrefabAsset();

            GameObject sourceRoot = PrefabUtility.LoadPrefabContents(sourcePath);
            try
            {
                sourceRoot.name = "Starship_Lv1_0";
                PrefabUtility.SaveAsPrefabAsset(sourceRoot, STARTER_SHIP_PREFAB_PATH);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(sourceRoot);
            }

            GameObject starterPrefab = GetStarterShipPrefabAsset();
            RegisterNetworkPrefab(starterPrefab);
            return starterPrefab;
        }

        private static void RegisterNetworkPrefab(GameObject prefab)
        {
            if (prefab == null) return;
            var defaultList = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/DefaultNetworkPrefabs.asset");
            if (defaultList == null) return;

            var so = new SerializedObject(defaultList);
            var listProp = so.FindProperty("List");
            if (listProp == null) return;

            // Remove stale/invalid entries (including broken sub-object prefab refs) before adding starter.
            for (int i = listProp.arraySize - 1; i >= 0; i--)
            {
                var prefabRefProp = listProp.GetArrayElementAtIndex(i).FindPropertyRelative("Prefab");
                var prefabObj = prefabRefProp != null ? prefabRefProp.objectReferenceValue as GameObject : null;
                if (prefabObj == null)
                {
                    listProp.DeleteArrayElementAtIndex(i);
                    continue;
                }

                if (!PrefabUtility.IsPartOfPrefabAsset(prefabObj) || !AssetDatabase.IsMainAsset(prefabObj))
                {
                    listProp.DeleteArrayElementAtIndex(i);
                }
            }

            for (int i = 0; i < listProp.arraySize; i++)
            {
                var existing = listProp.GetArrayElementAtIndex(i).FindPropertyRelative("Prefab");
                if (existing != null && existing.objectReferenceValue == prefab)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    return;
                }
            }

            listProp.arraySize++;
            listProp.GetArrayElementAtIndex(listProp.arraySize - 1).FindPropertyRelative("Prefab").objectReferenceValue = prefab;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(defaultList);
        }

        /// <summary>Builds visuals by copying one of the authored example prefabs (HiRez/StarSparrow), then scales to game proportions.</summary>
        private static void BuildProceduralShipVisual(GameObject shipRoot, ShipData data, float fighterToMinerBlend, int branchCount)
        {
            int level = data.shipLevel;
            int branchIndex = data.branchIndex;
            int seed = level * 149 + branchIndex * 41;
            var root = shipRoot.transform;
            shipRoot.transform.localScale = Vector3.one;

            var rootMf = shipRoot.GetComponent<MeshFilter>();
            var rootMr = shipRoot.GetComponent<MeshRenderer>();
            if (rootMf == null) rootMf = shipRoot.AddComponent<MeshFilter>();
            if (rootMr == null) rootMr = shipRoot.AddComponent<MeshRenderer>();

            Transform firePoint = FindChildRecursive(root, "FirePoint");
            if (firePoint == null)
            {
                var fp = new GameObject("FirePoint");
                fp.transform.SetParent(root);
                fp.transform.localPosition = new Vector3(0f, 0f, 0.5f);
                fp.transform.localRotation = Quaternion.identity;
                fp.transform.localScale = Vector3.one;
                firePoint = fp.transform;
            }

            ClearVisualChildren(root, firePoint);
            var examples = GetCombinedExampleShipPrefabs();
            if (examples.Count == 0)
            {
                Debug.LogWarning("No example ship prefabs found in HiRez/StarSparrow examples folders.");
                return;
            }
            GameObject source = examples[Mathf.Abs(seed) % examples.Count];
            if (!ApplyVisualFromExamplePrefab(root, source, firePoint))
            {
                Debug.LogWarning($"Failed to apply example visual from {source.name}");
                return;
            }
            RemapExampleMaterialsToUrp(root);
            float cargoBias = Mathf.Clamp01(Mathf.InverseLerp(100f, 760f, data.baseGemCapacity));

            // Normalize size to level-1 proportions (no oversized ships), then modestly scale per level.
            NormalizeShipScaleToStarter(root, data, cargoBias);
            StripChildColliders(root, firePoint);

            Bounds visBounds = GetLocalRendererBounds(root);
            float noseZ = visBounds.size.sqrMagnitude > 0.0001f ? visBounds.max.z : 0.55f;
            firePoint.localPosition = new Vector3(0f, 0f, noseZ + 0.12f);
            firePoint.localRotation = Quaternion.identity;
            firePoint.localScale = Vector3.one;

            var ship = shipRoot.GetComponent<TitanOrbit.Entities.Starship>();
            if (ship != null)
            {
                var shipSo = new SerializedObject(ship);
                shipSo.FindProperty("firePoint").objectReferenceValue = firePoint;
                shipSo.ApplyModifiedPropertiesWithoutUndo();
            }

            var boxCol = shipRoot.GetComponent<BoxCollider>();
            if (boxCol != null)
            {
                FitColliderToVisuals(root, boxCol);
            }

            var teamColor = shipRoot.GetComponent<TitanOrbit.Entities.ShipTeamColor>();
            if (teamColor != null)
            {
                var so = new SerializedObject(teamColor);
                so.FindProperty("accentRenderers").ClearArray();
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static bool ApplyVisualFromExamplePrefab(Transform destinationRoot, GameObject sourcePrefab, Transform keepFirePoint)
        {
            if (destinationRoot == null || sourcePrefab == null) return false;
            GameObject sourceInstance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
            if (sourceInstance == null) return false;
            try
            {
                var srcRoot = sourceInstance.transform;
                var srcMf = srcRoot.GetComponent<MeshFilter>();
                var srcMr = srcRoot.GetComponent<MeshRenderer>();

                var dstMf = destinationRoot.GetComponent<MeshFilter>();
                var dstMr = destinationRoot.GetComponent<MeshRenderer>();
                if (dstMf == null) dstMf = destinationRoot.gameObject.AddComponent<MeshFilter>();
                if (dstMr == null) dstMr = destinationRoot.gameObject.AddComponent<MeshRenderer>();

                if (srcMf != null && dstMf != null) dstMf.sharedMesh = srcMf.sharedMesh;
                if (srcMr != null && dstMr != null)
                {
                    dstMr.sharedMaterials = srcMr.sharedMaterials;
                    dstMr.enabled = srcMr.enabled;
                }

                while (srcRoot.childCount > 0)
                {
                    Transform child = srcRoot.GetChild(0);
                    child.SetParent(destinationRoot, true);
                }

                // Remove non-visual heavy components from adopted visual hierarchy.
                RemoveHeavyComponents(destinationRoot, keepFirePoint);
                return true;
            }
            finally
            {
                Object.DestroyImmediate(sourceInstance);
            }
        }

        private static void RemoveHeavyComponents(Transform root, Transform keepFirePoint)
        {
            var colliders = root.GetComponentsInChildren<Collider>(true);
            foreach (var c in colliders)
            {
                if (c == null) continue;
                if (c.transform == root) continue; // keep root collider
                if (keepFirePoint != null && (c.transform == keepFirePoint || c.transform.IsChildOf(keepFirePoint))) continue;
                Object.DestroyImmediate(c);
            }
            var rigidbodies = root.GetComponentsInChildren<Rigidbody>(true);
            foreach (var rb in rigidbodies)
            {
                if (rb == null || rb.transform == root) continue;
                Object.DestroyImmediate(rb);
            }
            var scripts = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var mb in scripts)
            {
                if (mb == null) continue;
                if (mb.transform == root) continue;
                Object.DestroyImmediate(mb);
            }
        }

        private static List<GameObject> modularExamplePrefabsCache;
        private static List<GameObject> combinedExamplePrefabsCache;

        private static List<GameObject> GetModularExamplePrefabs()
        {
            if (modularExamplePrefabsCache != null && modularExamplePrefabsCache.Count > 0)
                return modularExamplePrefabsCache;

            modularExamplePrefabsCache = new List<GameObject>();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { STARSPARROW_MODULAR_EXAMPLES_FOLDER });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) modularExamplePrefabsCache.Add(prefab);
            }
            modularExamplePrefabsCache = modularExamplePrefabsCache
                .OrderBy(p => GetTrailingNumber(p.name))
                .ThenBy(p => p.name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            return modularExamplePrefabsCache;
        }

        private static List<GameObject> GetCombinedExampleShipPrefabs()
        {
            if (combinedExamplePrefabsCache != null && combinedExamplePrefabsCache.Count > 0)
                return combinedExamplePrefabsCache;

            combinedExamplePrefabsCache = new List<GameObject>();
            AddExamplePrefabsFromFolder(combinedExamplePrefabsCache, HIREZ_EXAMPLES_FOLDER);
            AddExamplePrefabsFromFolder(combinedExamplePrefabsCache, STARSPARROW_EXAMPLES_FOLDER);

            combinedExamplePrefabsCache = combinedExamplePrefabsCache
                .OrderBy(p => IsHiRezPath(AssetDatabase.GetAssetPath(p)) ? 0 : 1)
                .ThenBy(p => GetTrailingNumber(p.name))
                .ThenBy(p => p.name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            return combinedExamplePrefabsCache;
        }

        private static GameObject FindPreferredStarterExamplePrefab(List<GameObject> examples)
        {
            if (examples == null || examples.Count == 0) return null;

            // Keep starter visuals aligned with the modern StarSparrow fleet.
            for (int i = 0; i < examples.Count; i++)
            {
                var prefab = examples[i];
                if (prefab == null) continue;
                string path = AssetDatabase.GetAssetPath(prefab);
                if (!string.IsNullOrEmpty(path) && path.StartsWith(STARSPARROW_EXAMPLES_FOLDER))
                    return prefab;
            }

            return examples[0];
        }

        private static void AddExamplePrefabsFromFolder(List<GameObject> list, string folder)
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) list.Add(prefab);
            }
        }

        private static bool IsHiRezPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(HIREZ_ROOT_FOLDER);
        }

        private static void EnsureModulePrefabCache()
        {
            if (ModulePrefabCache.Count > 0) return;
            string[] keys = { "Core", "Weapon", "Wing", "Engine", "Thruster", "Tail", "Fin", "Plasma" };
            foreach (string key in keys)
            {
                string path = $"{STARSPARROW_MODULES_FOLDER}/StarSparrow_{key}.prefab";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) ModulePrefabCache[key] = prefab;
            }
        }

        private static GameObject GetModulePrefab(string moduleKey)
        {
            if (string.IsNullOrEmpty(moduleKey)) return null;
            ModulePrefabCache.TryGetValue(moduleKey, out GameObject prefab);
            return prefab;
        }

        private static List<TemplatePart> GetTemplateParts(string templatePath)
        {
            if (string.IsNullOrEmpty(templatePath)) return new List<TemplatePart>();
            if (TemplatePartsCache.TryGetValue(templatePath, out List<TemplatePart> cached))
                return cached;

            var results = new List<TemplatePart>();
            var root = PrefabUtility.LoadPrefabContents(templatePath);
            try
            {
                for (int i = 0; i < root.transform.childCount; i++)
                {
                    Transform child = root.transform.GetChild(i);
                    string moduleKey = DetectModuleKey(child.name);
                    if (string.IsNullOrEmpty(moduleKey)) continue;
                    results.Add(new TemplatePart
                    {
                        moduleKey = moduleKey,
                        localPosition = child.localPosition,
                        localRotation = child.localRotation,
                        localScale = child.localScale
                    });
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            TemplatePartsCache[templatePath] = results;
            return results;
        }

        private static string DetectModuleKey(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return null;
            string n = objectName.ToLowerInvariant();
            if (n.Contains("core")) return "Core";
            if (n.Contains("weapon")) return "Weapon";
            if (n.Contains("wing")) return "Wing";
            if (n.Contains("engine")) return "Engine";
            if (n.Contains("thruster")) return "Thruster";
            if (n.Contains("tail")) return "Tail";
            if (n.Contains("fin")) return "Fin";
            if (n.Contains("plasma")) return "Plasma";
            return null;
        }

        private static int IncrementModuleCounter(int[] counters, string moduleKey)
        {
            int idx = 0;
            switch (moduleKey)
            {
                case "Core": idx = 0; break;
                case "Weapon": idx = 1; break;
                case "Wing": idx = 2; break;
                case "Engine": idx = 3; break;
                case "Thruster": idx = 4; break;
                case "Tail": idx = 5; break;
                case "Fin": idx = 6; break;
                case "Plasma": idx = 7; break;
                default: return 1;
            }
            counters[idx]++;
            return counters[idx];
        }

        private static int GetTrailingNumber(string name)
        {
            if (string.IsNullOrEmpty(name)) return int.MaxValue;
            int idx = name.Length - 1;
            while (idx >= 0 && char.IsDigit(name[idx])) idx--;
            string digits = name.Substring(idx + 1);
            if (int.TryParse(digits, out int n)) return n;
            return int.MaxValue;
        }

        private static void ApplyWeaponLayoutFromStats(Transform root, ShipData data, GameObject weaponPrefab, Material moduleMaterial)
        {
            var cannons = data.weaponConfig != null ? data.weaponConfig.cannons : null;
            var barrelSpecs = new List<(float x, float z, float angle, float scale)>();
            if (cannons == null || cannons.Count == 0)
                cannons = new List<CannonConfig> { new CannonConfig() };

            foreach (var cannon in cannons)
            {
                int barrels = cannon.spreadType == CannonSpreadType.FixedSpread
                    ? Mathf.Clamp(cannon.spreadProjectileCount, 1, 6)
                    : 1;
                float baseScale = Mathf.Clamp(0.72f + cannon.bulletScale * 0.26f, 0.68f, 1.5f);
                float heavyScale = Mathf.Clamp(0.86f + cannon.damagePerBullet / 65f, 0.86f, 1.4f);
                float moduleScale = baseScale * heavyScale;
                for (int bi = 0; bi < barrels; bi++)
                {
                    float spreadT = barrels <= 1 ? 0.5f : (float)bi / (barrels - 1);
                    float spreadX = Mathf.Lerp(-0.1f, 0.1f, spreadT) * Mathf.Clamp(barrels, 1, 5);
                    float barrelX = cannon.localOffsetX * 0.6f + spreadX;
                    float barrelZ = cannon.localOffsetZ * 0.35f;
                    float angle = cannon.directionAngle;
                    if (barrels > 1)
                        angle += Mathf.Lerp(cannon.spreadAngleMin, cannon.spreadAngleMax, spreadT);
                    barrelSpecs.Add((barrelX, barrelZ, angle, moduleScale));
                }
            }

            barrelSpecs = barrelSpecs.Take(10).ToList();
            var existingWeapons = GetChildrenByNameContains(root, "Weapon");
            if (existingWeapons.Count == 0 && weaponPrefab != null)
            {
                InstantiateModule(weaponPrefab, root, "Weapon_Anchor", new Vector3(0f, 0f, 0.45f), Quaternion.identity, Vector3.one, moduleMaterial);
                existingWeapons = GetChildrenByNameContains(root, "Weapon");
            }
            if (existingWeapons.Count == 0) return;

            existingWeapons = existingWeapons.OrderByDescending(t => t.localPosition.z).ToList();
            while (existingWeapons.Count < barrelSpecs.Count && weaponPrefab != null)
            {
                Transform anchor = existingWeapons[existingWeapons.Count - 1];
                float side = existingWeapons.Count % 2 == 0 ? 0.1f : -0.1f;
                var newW = InstantiateModule(weaponPrefab, root, $"Weapon_{existingWeapons.Count + 1}",
                    anchor.localPosition + new Vector3(side, 0f, 0.02f), anchor.localRotation, anchor.localScale, moduleMaterial);
                if (newW != null) existingWeapons.Add(newW);
                else break;
            }

            for (int i = 0; i < existingWeapons.Count; i++)
            {
                if (i >= barrelSpecs.Count)
                {
                    Object.DestroyImmediate(existingWeapons[i].gameObject);
                    continue;
                }

                var spec = barrelSpecs[i];
                Transform anchor = existingWeapons[i];
                Vector3 basePos = anchor.localPosition;
                anchor.localPosition = new Vector3(
                    basePos.x + spec.x,
                    basePos.y,
                    basePos.z + spec.z);
                anchor.localRotation = anchor.localRotation * Quaternion.Euler(0f, spec.angle, 0f);
                anchor.localScale = anchor.localScale * spec.scale;
            }
        }

        private static void ApplyCargoPodsFromStats(Transform root, ShipData data, GameObject plasmaPrefab, Material moduleMaterial, float cargoBias)
        {
            if (plasmaPrefab == null) return;
            int desiredPairs = Mathf.Clamp(Mathf.RoundToInt(cargoBias * 3f) + (data.shipLevel >= 6 ? 1 : 0), 0, 4);
            var existing = GetChildrenByNameContains(root, "Plasma");
            int desiredTotal = desiredPairs * 2;

            while (existing.Count < desiredTotal)
            {
                var added = InstantiateModule(plasmaPrefab, root, $"Plasma_{existing.Count + 1}", Vector3.zero, Quaternion.identity, Vector3.one * 0.9f, moduleMaterial);
                if (added == null) break;
                existing.Add(added);
            }
            for (int i = existing.Count - 1; i >= desiredTotal; i--)
                Object.DestroyImmediate(existing[i].gameObject);

            if (desiredTotal == 0) return;

            Bounds b = GetLocalRendererBounds(root);
            float sideX = Mathf.Max(0.25f, b.extents.x * 1.05f);
            float y = b.center.y - Mathf.Min(0.05f, b.extents.y * 0.2f);
            for (int pair = 0; pair < desiredPairs; pair++)
            {
                float t = desiredPairs <= 1 ? 0.5f : (float)pair / (desiredPairs - 1);
                float z = Mathf.Lerp(b.min.z + b.size.z * 0.15f, b.max.z - b.size.z * 0.2f, t);
                float scale = Mathf.Lerp(0.8f, 1.12f, cargoBias);
                int leftIdx = pair * 2;
                int rightIdx = leftIdx + 1;
                if (leftIdx < existing.Count)
                {
                    existing[leftIdx].localPosition = new Vector3(-sideX, y, z);
                    existing[leftIdx].localRotation = Quaternion.Euler(0f, 6f, 0f);
                    existing[leftIdx].localScale = Vector3.one * scale;
                }
                if (rightIdx < existing.Count)
                {
                    existing[rightIdx].localPosition = new Vector3(sideX, y, z);
                    existing[rightIdx].localRotation = Quaternion.Euler(0f, -6f, 0f);
                    existing[rightIdx].localScale = Vector3.one * scale;
                }
            }
        }

        private static List<Transform> GetChildrenByNameContains(Transform root, string token)
        {
            string lower = token.ToLowerInvariant();
            var results = new List<Transform>();
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c.name.ToLowerInvariant().Contains(lower))
                    results.Add(c);
            }
            return results;
        }

        private static void NormalizeShipScaleToStarter(Transform root, ShipData data, float cargoBias)
        {
            Bounds b = GetLocalRendererBounds(root);
            float currentLength = Mathf.Max(0.001f, b.size.z);
            int shipLevel = data != null ? data.shipLevel : 1;
            float visualScale = data != null ? data.visualScale : 1f;
            float levelNorm = Mathf.Clamp01((shipLevel - 1f) / 6f);
            // Starter ship collider is ~1.1 length; keep new ships proportionate to that baseline.
            float targetLength = Mathf.Lerp(0.95f, 1.2f, levelNorm) * Mathf.Lerp(0.95f, 1.06f, cargoBias);
            float scale = targetLength / currentLength;
            // Keep visualScale influence but heavily damped to avoid oversized results.
            float visualNorm = Mathf.Clamp01(Mathf.InverseLerp(0.8f, 1.5f, visualScale));
            scale *= Mathf.Lerp(0.95f, 1.03f, visualNorm);
            root.localScale = Vector3.one * Mathf.Clamp(scale, 0.05f, 2.5f);
        }

        private static void StripChildColliders(Transform root, Transform keepChild)
        {
            var colliders = root.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                if (col.transform == root) continue; // keep root collider
                if (keepChild != null && (col.transform == keepChild || col.transform.IsChildOf(keepChild))) continue;
                Object.DestroyImmediate(col);
            }
        }

        private static Bounds GetLocalRendererBounds(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Bounds? localBounds = null;
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                Bounds wb = r.bounds;
                Vector3 c = root.InverseTransformPoint(wb.center);
                Vector3 e = wb.extents;
                Bounds lb = new Bounds(c, e * 2f);
                if (!localBounds.HasValue) localBounds = lb;
                else
                {
                    Bounds b = localBounds.Value;
                    b.Encapsulate(lb.min);
                    b.Encapsulate(lb.max);
                    localBounds = b;
                }
            }
            return localBounds ?? new Bounds(Vector3.zero, Vector3.one);
        }

        private static Transform InstantiateModule(GameObject modulePrefab, Transform parent, string name, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Material overrideMaterial)
        {
            if (modulePrefab == null) return null;
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(modulePrefab);
            if (instance == null) return null;

            instance.name = name;
            Transform t = instance.transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            t.localRotation = localRotation;
            t.localScale = localScale;
            if (overrideMaterial != null)
                ApplyMaterialToHierarchy(t, overrideMaterial);
            return t;
        }

        private static void ApplyMaterialToHierarchy(Transform moduleRoot, Material material)
        {
            if (moduleRoot == null || material == null) return;

            var renderers = moduleRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                int slotCount = renderer.sharedMaterials != null && renderer.sharedMaterials.Length > 0
                    ? renderer.sharedMaterials.Length
                    : 1;
                Material[] mats = new Material[slotCount];
                for (int i = 0; i < slotCount; i++) mats[i] = material;
                renderer.sharedMaterials = mats;
            }
        }

        private static string GetShipColorVariant(int level, int branchIndex, float fighterToMinerBlend)
        {
            int hash = Mathf.Abs(level * 97 + branchIndex * 37 + Mathf.RoundToInt(fighterToMinerBlend * 100f) * 17);
            return StarSparrowColorVariants[hash % StarSparrowColorVariants.Length];
        }

        private static void RemapExampleMaterialsToUrp(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var shared = renderer.sharedMaterials;
                if (shared == null || shared.Length == 0) continue;
                Material[] replaced = null;
                for (int i = 0; i < shared.Length; i++)
                {
                    Material src = shared[i];
                    if (src == null) continue;
                    string srcPath = AssetDatabase.GetAssetPath(src);
                    if (string.IsNullOrEmpty(srcPath)) continue;
                    Material dst = null;
                    if (srcPath.StartsWith(STARSPARROW_MATERIALS_FOLDER))
                    {
                        dst = GetOrCreateConvertedStarSparrowMaterial(ExtractStarSparrowVariant(src.name), false);
                    }
                    else if (srcPath.StartsWith(HIREZ_MATERIALS_FOLDER) && !srcPath.StartsWith(HIREZ_URP_MATERIALS_FOLDER))
                    {
                        dst = GetOrCreateConvertedHiRezMaterial(src, false);
                    }
                    if (dst == null) continue;
                    if (replaced == null) replaced = (Material[])shared.Clone();
                    replaced[i] = dst;
                }
                if (replaced != null)
                    renderer.sharedMaterials = replaced;
            }
        }

        private static Material GetOrCreateConvertedStarSparrowMaterial(string variantName, bool forceRebuild = false)
        {
            if (string.IsNullOrWhiteSpace(variantName)) variantName = "Red";
            string sourcePath = $"{STARSPARROW_MATERIALS_FOLDER}/StarSparrow_{variantName}.mat";
            Material source = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (source == null)
                source = AssetDatabase.LoadAssetAtPath<Material>($"{STARSPARROW_MATERIALS_FOLDER}/StarSparrow_Red.mat");
            if (source == null) return null;

            EnsureAssetFolder(STARSPARROW_URP_MATERIALS_FOLDER);
            string convertedPath = $"{STARSPARROW_URP_MATERIALS_FOLDER}/StarSparrow_{variantName}_URP.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(convertedPath);
            if (forceRebuild && existing != null)
            {
                AssetDatabase.DeleteAsset(convertedPath);
                existing = null;
            }
            if (existing != null) return existing;

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            Shader fallback = Shader.Find("Standard");
            Shader shader = urpLit != null ? urpLit : fallback;
            if (shader == null)
            {
                Debug.LogWarning("No compatible Lit shader found to convert StarSparrow materials.");
                return source;
            }

            Material converted = new Material(shader)
            {
                name = $"StarSparrow_{variantName}_URP"
            };

            // Preserve detailed StarSparrow texture set.
            Texture mainTex = source.GetTexture("_MainTex");
            Texture normalTex = source.GetTexture("_BumpMap");
            Texture metallicTex = source.GetTexture("_MetallicGlossMap");
            Texture emissionTex = source.GetTexture("_EmissionMap");

            Color baseColor = source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            Color emissionColor = source.HasProperty("_EmissionColor") ? source.GetColor("_EmissionColor") : Color.black;
            float smoothness = source.HasProperty("_Glossiness") ? source.GetFloat("_Glossiness") : 0.5f;

            if (converted.HasProperty("_BaseMap")) converted.SetTexture("_BaseMap", mainTex);
            if (converted.HasProperty("_MainTex")) converted.SetTexture("_MainTex", mainTex);
            if (converted.HasProperty("_BaseColor")) converted.SetColor("_BaseColor", baseColor);
            if (converted.HasProperty("_Color")) converted.SetColor("_Color", baseColor);
            if (converted.HasProperty("_BumpMap")) converted.SetTexture("_BumpMap", normalTex);
            if (converted.HasProperty("_MetallicGlossMap")) converted.SetTexture("_MetallicGlossMap", metallicTex);
            if (converted.HasProperty("_Smoothness")) converted.SetFloat("_Smoothness", smoothness);
            if (converted.HasProperty("_EmissionMap")) converted.SetTexture("_EmissionMap", emissionTex);
            if (converted.HasProperty("_EmissionColor")) converted.SetColor("_EmissionColor", emissionColor);

            if (normalTex != null) converted.EnableKeyword("_NORMALMAP");
            if (metallicTex != null) converted.EnableKeyword("_METALLICSPECGLOSSMAP");
            if (emissionTex != null && emissionColor.maxColorComponent > 0.0001f)
            {
                converted.EnableKeyword("_EMISSION");
                converted.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            AssetDatabase.CreateAsset(converted, convertedPath);
            EditorUtility.SetDirty(converted);
            return converted;
        }

        private static Material GetOrCreateConvertedHiRezMaterial(Material source, bool forceRebuild)
        {
            if (source == null) return null;
            string srcPath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(srcPath) || !srcPath.StartsWith(HIREZ_MATERIALS_FOLDER)) return null;
            if (HiRezConvertedBySourcePath.TryGetValue(srcPath, out Material cached) && cached != null && !forceRebuild)
                return cached;

            string relative = srcPath.Substring(HIREZ_MATERIALS_FOLDER.Length).TrimStart('/');
            string dstPath = $"{HIREZ_URP_MATERIALS_FOLDER}/{relative}";
            string dstDir = dstPath.Substring(0, dstPath.LastIndexOf('/'));
            EnsureAssetFolder(dstDir);

            Material existing = AssetDatabase.LoadAssetAtPath<Material>(dstPath);
            if (forceRebuild && existing != null)
            {
                AssetDatabase.DeleteAsset(dstPath);
                existing = null;
            }
            if (existing != null)
            {
                HiRezConvertedBySourcePath[srcPath] = existing;
                return existing;
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit == null) return source;
            Material converted = new Material(urpLit) { name = source.name + "_URP" };

            Texture mainTex = source.GetTexture("_MainTex");
            Texture normalTex = source.GetTexture("_BumpMap");
            Texture metallicTex = source.GetTexture("_MetallicGlossMap");
            Texture emissionTex = source.GetTexture("_EmissionMap");
            Color baseColor = source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            Color emissionColor = source.HasProperty("_EmissionColor") ? source.GetColor("_EmissionColor") : Color.black;
            float smoothness = source.HasProperty("_Glossiness") ? source.GetFloat("_Glossiness") : 0.5f;
            float mode = source.HasProperty("_Mode") ? source.GetFloat("_Mode") : 0f; // Standard shader mode

            if (converted.HasProperty("_BaseMap")) converted.SetTexture("_BaseMap", mainTex);
            if (converted.HasProperty("_MainTex")) converted.SetTexture("_MainTex", mainTex);
            if (converted.HasProperty("_BaseColor")) converted.SetColor("_BaseColor", baseColor);
            if (converted.HasProperty("_Color")) converted.SetColor("_Color", baseColor);
            if (converted.HasProperty("_BumpMap")) converted.SetTexture("_BumpMap", normalTex);
            if (converted.HasProperty("_MetallicGlossMap")) converted.SetTexture("_MetallicGlossMap", metallicTex);
            if (converted.HasProperty("_Smoothness")) converted.SetFloat("_Smoothness", smoothness);
            if (converted.HasProperty("_EmissionMap")) converted.SetTexture("_EmissionMap", emissionTex);
            if (converted.HasProperty("_EmissionColor")) converted.SetColor("_EmissionColor", emissionColor);

            if (normalTex != null) converted.EnableKeyword("_NORMALMAP");
            if (emissionTex != null || emissionColor.maxColorComponent > 0.0001f) converted.EnableKeyword("_EMISSION");
            if (mode >= 2f)
            {
                // Standard Fade/Transparent -> URP transparent surface
                converted.SetFloat("_Surface", 1f);
                converted.SetFloat("_Blend", 0f);
                converted.SetFloat("_ZWrite", 0f);
                converted.renderQueue = 3000;
            }

            AssetDatabase.CreateAsset(converted, dstPath);
            EditorUtility.SetDirty(converted);
            HiRezConvertedBySourcePath[srcPath] = converted;
            return converted;
        }

        private static void ConvertHiRezMaterialsAndPrefabsInternal(bool forceRebuildConvertedMaterials, bool logSummary)
        {
            EnsureAssetFolder(HIREZ_URP_MATERIALS_FOLDER);
            HiRezConvertedBySourcePath.Clear();

            int convertedCount = 0;
            string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { HIREZ_MATERIALS_FOLDER });
            foreach (string guid in matGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith(HIREZ_URP_MATERIALS_FOLDER)) continue;
                Material src = AssetDatabase.LoadAssetAtPath<Material>(path);
                Material dst = GetOrCreateConvertedHiRezMaterial(src, forceRebuildConvertedMaterials);
                if (dst != null) convertedCount++;
            }

            int updatedPrefabs = 0;
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { HIREZ_ROOT_FOLDER });
            foreach (string guid in prefabGuids)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                bool changed = false;
                try
                {
                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    foreach (var renderer in renderers)
                    {
                        var shared = renderer.sharedMaterials;
                        if (shared == null || shared.Length == 0) continue;
                        Material[] replaced = null;
                        for (int i = 0; i < shared.Length; i++)
                        {
                            Material src = shared[i];
                            if (src == null) continue;
                            string srcPath = AssetDatabase.GetAssetPath(src);
                            if (string.IsNullOrEmpty(srcPath) || !srcPath.StartsWith(HIREZ_MATERIALS_FOLDER) || srcPath.StartsWith(HIREZ_URP_MATERIALS_FOLDER))
                                continue;
                            Material dst = GetOrCreateConvertedHiRezMaterial(src, false);
                            if (dst == null) continue;
                            if (replaced == null) replaced = (Material[])shared.Clone();
                            replaced[i] = dst;
                            changed = true;
                        }
                        if (replaced != null) renderer.sharedMaterials = replaced;
                    }
                    if (changed)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                        updatedPrefabs++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (logSummary)
                Debug.Log($"HiRez URP conversion complete. Materials converted: {convertedCount}, prefabs updated: {updatedPrefabs}");
        }

        private static void UpdateBaseStarshipWithFirstExamplePrefab()
        {
            var examples = GetCombinedExampleShipPrefabs();
            if (examples == null || examples.Count == 0) return;
            var starterExample = FindPreferredStarterExamplePrefab(examples);
            if (starterExample == null) return;
            string starshipPath = "Assets/Prefabs/Starship.prefab";
            var root = PrefabUtility.LoadPrefabContents(starshipPath);
            try
            {
                Transform firePoint = FindChildRecursive(root.transform, "FirePoint");
                if (firePoint == null)
                {
                    var fp = new GameObject("FirePoint");
                    fp.transform.SetParent(root.transform, false);
                    fp.transform.localPosition = new Vector3(0f, 0f, 0.55f);
                    firePoint = fp.transform;
                }
                ClearVisualChildren(root.transform, firePoint);
                ApplyVisualFromExamplePrefab(root.transform, starterExample, firePoint);
                RemapExampleMaterialsToUrp(root.transform);
                ScaleVisualChildren(root.transform, firePoint, 0.175f);
                StripChildColliders(root.transform, firePoint);
                Bounds b = GetLocalRendererBounds(root.transform);
                firePoint.localPosition = new Vector3(0f, 0f, b.max.z + 0.12f);
                var box = root.GetComponent<BoxCollider>();
                if (box != null) FitColliderToVisuals(root.transform, box);
                PrefabUtility.SaveAsPrefabAsset(root, starshipPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ScaleVisualChildren(Transform root, Transform keepFirePoint, float scaleMultiplier)
        {
            if (root == null) return;
            float s = Mathf.Max(0.005f, scaleMultiplier);
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (keepFirePoint != null && (child == keepFirePoint || child.IsChildOf(keepFirePoint))) continue;
                child.localPosition *= s;
                child.localScale *= s;
            }
        }

        private static void ConvertStarSparrowMaterialsAndPrefabsInternal(bool forceRebuildConvertedMaterials, bool logSummary)
        {
            EnsureAssetFolder(STARSPARROW_URP_MATERIALS_FOLDER);

            var variantToConverted = new Dictionary<string, Material>();
            foreach (string variant in StarSparrowColorVariants)
            {
                Material converted = GetOrCreateConvertedStarSparrowMaterial(variant, forceRebuildConvertedMaterials);
                if (converted != null)
                    variantToConverted[variant] = converted;
            }
            if (!variantToConverted.ContainsKey("Red"))
            {
                Material redFallback = GetOrCreateConvertedStarSparrowMaterial("Red", forceRebuildConvertedMaterials);
                if (redFallback != null) variantToConverted["Red"] = redFallback;
            }

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { STARSPARROW_PREFABS_FOLDER });
            int updatedPrefabCount = 0;
            for (int i = 0; i < prefabGuids.Length; i++)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                bool changed = false;
                try
                {
                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null) continue;
                        Material[] shared = renderer.sharedMaterials;
                        if (shared == null || shared.Length == 0) continue;
                        Material[] replaced = null;
                        for (int m = 0; m < shared.Length; m++)
                        {
                            Material mat = shared[m];
                            if (mat == null) continue;
                            string matPath = AssetDatabase.GetAssetPath(mat);
                            if (string.IsNullOrEmpty(matPath) || !matPath.StartsWith(STARSPARROW_MATERIALS_FOLDER)) continue;
                            if (matPath.StartsWith(STARSPARROW_URP_MATERIALS_FOLDER)) continue;

                            string variant = ExtractStarSparrowVariant(mat.name);
                            if (!variantToConverted.TryGetValue(variant, out Material replacement) || replacement == null)
                            {
                                variantToConverted.TryGetValue("Red", out replacement);
                            }
                            if (replacement == null) continue;
                            if (replaced == null) replaced = (Material[])shared.Clone();
                            replaced[m] = replacement;
                            changed = true;
                        }
                        if (replaced != null)
                            renderer.sharedMaterials = replaced;
                    }

                    if (changed)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                        updatedPrefabCount++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (logSummary)
            {
                Debug.Log($"Fixed StarSparrow materials: {variantToConverted.Count} converted variants, {updatedPrefabCount} prefabs updated.");
            }
        }

        private static string ExtractStarSparrowVariant(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return "Red";
            const string prefix = "StarSparrow_";
            if (!materialName.StartsWith(prefix)) return "Red";
            string variant = materialName.Substring(prefix.Length);
            int spaceIndex = variant.IndexOf(' ');
            if (spaceIndex > 0) variant = variant.Substring(0, spaceIndex);
            int underscoreIndex = variant.IndexOf('_');
            if (underscoreIndex > 0) variant = variant.Substring(0, underscoreIndex);
            if (string.IsNullOrEmpty(variant)) return "Red";
            for (int i = 0; i < StarSparrowColorVariants.Length; i++)
            {
                if (string.Equals(StarSparrowColorVariants[i], variant, System.StringComparison.OrdinalIgnoreCase))
                    return StarSparrowColorVariants[i];
            }
            return "Red";
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static void ClearVisualChildren(Transform root, Transform keepChild)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (keepChild != null && child == keepChild) continue;
                Object.DestroyImmediate(child.gameObject);
            }
        }

        private static void FitColliderToVisuals(Transform root, BoxCollider boxCol)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Bounds? localBounds = null;
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                Bounds wb = r.bounds;
                Vector3 c = root.InverseTransformPoint(wb.center);
                Vector3 e = wb.extents;
                Bounds lb = new Bounds(c, e * 2f);
                if (!localBounds.HasValue) localBounds = lb;
                else
                {
                    Bounds b = localBounds.Value;
                    b.Encapsulate(lb.min);
                    b.Encapsulate(lb.max);
                    localBounds = b;
                }
            }
            if (!localBounds.HasValue)
            {
                boxCol.center = Vector3.zero;
                boxCol.size = new Vector3(1f, 1f, 2f);
                return;
            }
            Bounds final = localBounds.Value;
            final.Expand(new Vector3(0.2f, 0.2f, 0.2f));
            boxCol.center = final.center;
            boxCol.size = final.size;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var found = FindChildRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void AssignUpgradeTreeInScene(UpgradeTree tree)
        {
            var upgradeSystem = Object.FindFirstObjectByType<TitanOrbit.Systems.UpgradeSystem>();
            if (upgradeSystem == null) return;
            var so = new SerializedObject(upgradeSystem);
            so.FindProperty("upgradeTree").objectReferenceValue = tree;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
