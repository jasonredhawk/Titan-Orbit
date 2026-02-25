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
        private const string STARSPARROW_MATERIALS_FOLDER = "Assets/StarSparrow/Materials";
        private const string STARSPARROW_URP_MATERIALS_FOLDER = "Assets/StarSparrow/Materials/GeneratedURP";
        private const string STARSPARROW_PREFABS_FOLDER = "Assets/StarSparrow/Prefabs";
        private const float GENERATED_SHIP_SCALE_MULTIPLIER = 0.38f;

        private static readonly int[] CountPerLevel = { 2, 4, 6, 8, 9, 4 }; // levels 2-7
        private static readonly string[] StarSparrowColorVariants =
        {
            "Red", "Blue", "Green", "Purple", "Grey", "White", "Yellow", "Orange", "Cyan", "Black"
        };

        [MenuItem("Titan Orbit/Create Upgrade Tree And Ships")]
        public static void CreateAll()
        {
            EnsureFolders();
            CreateOrLoadLevel1Starter();
            List<List<ShipData>> shipDataByLevel = CreateAllShipDataAssets();
            UpgradeTree tree = CreateOrLoadUpgradeTree(shipDataByLevel);
            CreateShipPrefabs(shipDataByLevel);
            AssignUpgradeTreeInScene(tree);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Upgrade tree created: L1(1)→L2(2)→L3(4)→L4(6)→L5(8)→L6(9)→L7(4 MEGA). Level 7 requires home planet 6 + full gems.");
        }

        [MenuItem("Titan Orbit/Rebuild Ship Prefabs (Unique Designs)")]
        public static void RebuildShipPrefabs()
        {
            EnsureFolders();
            ConvertStarSparrowMaterialsAndPrefabsInternal(forceRebuildConvertedMaterials: false, logSummary: false);
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
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Rebuilt {shipDataByLevel.Sum(l => l.Count)} ship prefabs with unique designs.");
        }

        [MenuItem("Titan Orbit/Fix StarSparrow Materials (URP + Prefabs)")]
        public static void FixStarSparrowMaterialsAndPrefabs()
        {
            ConvertStarSparrowMaterialsAndPrefabsInternal(forceRebuildConvertedMaterials: true, logSummary: true);
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
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Starship.prefab");
            if (basePrefab == null) { Debug.LogWarning("Starship.prefab not found."); return; }

            for (int li = 0; li < shipDataByLevel.Count; li++)
            {
                int level = li + 2;
                int count = shipDataByLevel[li].Count;
                foreach (var data in shipDataByLevel[li])
                {
                    string path = $"{PREFABS_SHIPS_FOLDER}/Starship_Lv{level}_{data.branchIndex}.prefab";
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                    var ship = instance.GetComponent<TitanOrbit.Entities.Starship>();
                    if (ship != null)
                    {
                        ship.SetShipData(data);
                        float blend = count <= 1 ? 0.5f : (float)data.branchIndex / (count - 1);
                        BuildProceduralShipVisual(instance, data, blend, count);
                        var saved = PrefabUtility.SaveAsPrefabAsset(instance, path);
                        if (saved != null)
                        {
                            var so = new SerializedObject(data);
                            so.FindProperty("shipPrefab").objectReferenceValue = saved;
                            so.ApplyModifiedPropertiesWithoutUndo();
                        }
                    }
                    Object.DestroyImmediate(instance);
                }
            }
        }

        /// <summary>
        /// Rebuilds ship visuals using StarSparrow modular parts.
        /// Silhouette and module counts are driven by ship role/stats:
        /// - Cannon count and bullet scale drive front weapon barrels
        /// - Gem capacity drives cargo pods and body bulk
        /// - Fighter/miner blend shifts wing-heavy vs heavy-hauler profiles
        /// </summary>
        private static void BuildProceduralShipVisual(GameObject shipRoot, ShipData data, float fighterToMinerBlend, int branchCount)
        {
            int level = data.shipLevel;
            int branchIndex = data.branchIndex;
            int seed = level * 101 + branchIndex * 17;
            float R(int m) => ((seed * m) % 100) / 100f;
            var root = shipRoot.transform;
            shipRoot.transform.localScale = Vector3.one * (data.visualScale * GENERATED_SHIP_SCALE_MULTIPLIER);
            Material moduleMaterial = GetOrCreateConvertedStarSparrowMaterial(GetShipColorVariant(level, branchIndex, fighterToMinerBlend));

            var rootMf = shipRoot.GetComponent<MeshFilter>();
            var rootMr = shipRoot.GetComponent<MeshRenderer>();
            if (rootMf != null) Object.DestroyImmediate(rootMf);
            if (rootMr != null) Object.DestroyImmediate(rootMr);

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

            var corePrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Core.prefab");
            var weaponPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Weapon.prefab");
            var wingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Wing.prefab");
            var enginePrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Engine.prefab");
            var thrusterPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Thruster.prefab");
            var tailPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Tail.prefab");
            var finPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Fin.prefab");
            var plasmaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{STARSPARROW_MODULES_FOLDER}/StarSparrow_Plasma.prefab");

            if (corePrefab == null || weaponPrefab == null || wingPrefab == null || enginePrefab == null)
            {
                Debug.LogWarning("StarSparrow module prefabs missing. Expected modules under Assets/StarSparrow/Prefabs/Modules.");
                return;
            }

            float cargoBias = Mathf.Clamp01(Mathf.InverseLerp(110f, 760f, data.baseGemCapacity));
            float healthBias = Mathf.Clamp01(Mathf.InverseLerp(120f, 340f, data.baseMaxHealth));
            float speedBias = Mathf.Clamp01(Mathf.InverseLerp(5.5f, 9.5f, data.baseMovementSpeed));
            float minerBias = Mathf.Clamp01((fighterToMinerBlend + (data.focusType == ShipFocusType.Miner ? 0.2f : 0f)) * 0.9f);

            // Larger cargos get longer, bulkier hulls while fighter lines stay sleeker.
            int coreSegments = Mathf.Clamp(1 + Mathf.FloorToInt(cargoBias * 2f) + (level >= 7 ? 1 : 0), 1, 4);
            float coreScaleX = Mathf.Lerp(0.72f, 1.35f, 0.35f * minerBias + 0.65f * cargoBias);
            float coreScaleY = Mathf.Lerp(0.62f, 1.1f, 0.4f * minerBias + 0.6f * healthBias);
            float coreScaleZ = Mathf.Lerp(0.8f, 1.35f, cargoBias) + 0.05f * R(19);
            float coreSpacing = 1.05f + 0.1f * R(23);
            float startZ = -0.25f * (coreSegments - 1) * coreSpacing;
            for (int i = 0; i < coreSegments; i++)
            {
                float t = coreSegments <= 1 ? 0f : (float)i / (coreSegments - 1);
                float taper = Mathf.Lerp(1.15f, 0.92f, t);
                InstantiateModule(
                    corePrefab,
                    root,
                    $"HullCore_{i + 1}",
                    new Vector3(0f, -0.08f * minerBias, startZ + i * coreSpacing),
                    Quaternion.identity,
                    new Vector3(coreScaleX * taper, coreScaleY * taper, coreScaleZ),
                    moduleMaterial);
            }

            if (tailPrefab != null)
            {
                InstantiateModule(
                    tailPrefab,
                    root,
                    "Tail",
                    new Vector3(0f, -0.12f, startZ - 0.95f),
                    Quaternion.identity,
                    Vector3.one * Mathf.Lerp(0.78f, 1.25f, minerBias),
                    moduleMaterial);
            }

            int wingPairs = Mathf.Clamp(1 + Mathf.RoundToInt((1f - minerBias) * 2f) + (level >= 5 ? 1 : 0), 1, 4);
            float wingSpread = Mathf.Lerp(0.72f, 1.5f, 1f - minerBias) + 0.15f * R(29);
            float wingBaseScale = Mathf.Lerp(0.72f, 1.25f, 1f - minerBias) * (1f + 0.04f * (level - 1));
            for (int i = 0; i < wingPairs; i++)
            {
                float t = wingPairs <= 1 ? 0f : (float)i / (wingPairs - 1);
                float z = Mathf.Lerp(startZ - 0.5f, startZ + coreSegments * coreSpacing - 0.25f, t);
                float side = wingSpread * Mathf.Lerp(0.7f, 1.05f, t);
                float y = Mathf.Lerp(0.04f, 0.22f, R(31 + i));
                float scale = wingBaseScale * Mathf.Lerp(0.9f, 1.06f, R(37 + i));
                InstantiateModule(wingPrefab, root, $"WingL_{i + 1}", new Vector3(-side, y, z), Quaternion.Euler(0f, -18f, 0f), new Vector3(scale, scale, scale), moduleMaterial);
                InstantiateModule(wingPrefab, root, $"WingR_{i + 1}", new Vector3(side, y, z), Quaternion.Euler(0f, 18f, 0f), new Vector3(scale, scale, scale), moduleMaterial);
            }

            int engineCount = Mathf.Clamp(2 + Mathf.RoundToInt((1f - speedBias) * 2.5f + minerBias * 1.5f), 2, 7);
            float rearZ = startZ - 1.45f;
            float engineSpread = Mathf.Lerp(0.26f, 0.95f, Mathf.InverseLerp(2f, 7f, engineCount));
            for (int i = 0; i < engineCount; i++)
            {
                float t = engineCount <= 1 ? 0.5f : (float)i / (engineCount - 1);
                float x = Mathf.Lerp(-engineSpread, engineSpread, t);
                float y = -0.16f + 0.12f * Mathf.Sin(t * Mathf.PI);
                float s = Mathf.Lerp(0.65f, 1.15f, 0.5f * minerBias + 0.5f * (1f - speedBias));
                InstantiateModule(enginePrefab, root, $"Engine_{i + 1}", new Vector3(x, y, rearZ), Quaternion.identity, Vector3.one * s, moduleMaterial);
                if (thrusterPrefab != null)
                {
                    InstantiateModule(thrusterPrefab, root, $"Thruster_{i + 1}", new Vector3(x, y - 0.04f, rearZ - 0.5f), Quaternion.identity, Vector3.one * (s * 0.78f), moduleMaterial);
                }
            }

            int cargoPairs = Mathf.Clamp(Mathf.RoundToInt(cargoBias * 4f) + (level >= 6 ? 1 : 0), 0, 6);
            if (cargoPairs > 0 && plasmaPrefab != null)
            {
                for (int i = 0; i < cargoPairs; i++)
                {
                    float t = cargoPairs <= 1 ? 0.5f : (float)i / (cargoPairs - 1);
                    float z = Mathf.Lerp(startZ - 0.25f, startZ + coreSegments * coreSpacing - 0.2f, t);
                    float x = Mathf.Lerp(0.65f, 1.25f, cargoBias) + 0.08f * R(43 + i);
                    float y = -0.05f + 0.1f * minerBias;
                    float s = Mathf.Lerp(0.68f, 1.35f, cargoBias);
                    InstantiateModule(plasmaPrefab, root, $"CargoPodL_{i + 1}", new Vector3(-x, y, z), Quaternion.Euler(0f, 8f, 0f), new Vector3(s, s * 0.85f, s), moduleMaterial);
                    InstantiateModule(plasmaPrefab, root, $"CargoPodR_{i + 1}", new Vector3(x, y, z), Quaternion.Euler(0f, -8f, 0f), new Vector3(s, s * 0.85f, s), moduleMaterial);
                }
            }

            var cannons = data.weaponConfig != null ? data.weaponConfig.cannons : null;
            int cannonCount = (cannons != null && cannons.Count > 0) ? cannons.Count : 1;
            float noseZ = startZ + coreSegments * coreSpacing + 0.35f;
            float maxWeaponZ = noseZ;
            for (int ci = 0; ci < cannonCount; ci++)
            {
                CannonConfig cannon = (cannons != null && ci < cannons.Count) ? cannons[ci] : new CannonConfig();
                int barrels = cannon.spreadType == CannonSpreadType.FixedSpread
                    ? Mathf.Clamp(cannon.spreadProjectileCount, 1, 6)
                    : 1;
                float baseX = cannon.localOffsetX * 4.2f;
                float baseZ = noseZ + cannon.localOffsetZ * 2.2f + ci * 0.04f;
                float baseScale = Mathf.Clamp(0.7f + cannon.bulletScale * 0.35f, 0.62f, 1.85f);
                float heavyScale = Mathf.Clamp(0.8f + cannon.damagePerBullet / 45f, 0.8f, 1.6f);
                float moduleScale = baseScale * heavyScale;

                for (int bi = 0; bi < barrels; bi++)
                {
                    float spreadT = barrels <= 1 ? 0.5f : (float)bi / (barrels - 1);
                    float spreadX = Mathf.Lerp(-0.22f, 0.22f, spreadT) * Mathf.Clamp(barrels, 1, 4);
                    float barrelX = baseX + spreadX;
                    float barrelZ = baseZ + bi * 0.03f;
                    float angle = cannon.directionAngle;
                    if (barrels > 1)
                        angle += Mathf.Lerp(cannon.spreadAngleMin, cannon.spreadAngleMax, spreadT);

                    InstantiateModule(
                        weaponPrefab,
                        root,
                        $"Weapon_{ci + 1}_{bi + 1}",
                        new Vector3(barrelX, -0.02f + 0.03f * R(53 + bi), barrelZ),
                        Quaternion.Euler(0f, angle, 0f),
                        new Vector3(moduleScale, moduleScale, Mathf.Lerp(moduleScale * 0.85f, moduleScale * 1.15f, R(59 + ci))),
                        moduleMaterial);
                    maxWeaponZ = Mathf.Max(maxWeaponZ, barrelZ);
                }

                // Heavy cannons get additional support housings to read as "big gun".
                if (moduleScale >= 1.28f && plasmaPrefab != null)
                {
                    InstantiateModule(
                        plasmaPrefab,
                        root,
                        $"WeaponSupport_{ci + 1}",
                        new Vector3(baseX, -0.1f, baseZ - 0.3f),
                        Quaternion.identity,
                        Vector3.one * Mathf.Clamp(moduleScale * 0.95f, 0.9f, 1.7f),
                        moduleMaterial);
                }
            }

            if (finPrefab != null)
            {
                int dorsalFins = Mathf.Clamp(Mathf.RoundToInt(minerBias * 3f) + (level >= 6 ? 1 : 0), 1, 4);
                for (int i = 0; i < dorsalFins; i++)
                {
                    float t = dorsalFins <= 1 ? 0.5f : (float)i / (dorsalFins - 1);
                    float z = Mathf.Lerp(startZ - 0.25f, startZ + coreSegments * coreSpacing - 0.2f, t);
                    float s = Mathf.Lerp(0.62f, 1.25f, 0.65f * minerBias + 0.35f * cargoBias);
                    InstantiateModule(finPrefab, root, $"FinTop_{i + 1}", new Vector3(0f, 0.35f + 0.08f * t, z), Quaternion.identity, Vector3.one * s, moduleMaterial);
                }
            }

            firePoint.localPosition = new Vector3(0f, 0f, maxWeaponZ + 0.35f);
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
