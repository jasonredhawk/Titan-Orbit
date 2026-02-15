using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
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
        private const string PREFABS_SHIPS_FOLDER = "Assets/Prefabs/Ships";
        private const string UPGRADE_TREE_PATH = "Assets/Data/UpgradeTree.asset";

        private static readonly int[] CountPerLevel = { 2, 4, 6, 8, 9, 4 }; // levels 2-7

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

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Data"))
                AssetDatabase.CreateFolder("Assets", "Data");
            if (!AssetDatabase.IsValidFolder(SHIPS_DATA_FOLDER))
                AssetDatabase.CreateFolder("Assets/Data", "Ships");
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
                    string assetPath = $"{SHIPS_DATA_FOLDER}/ShipData_Level{level}_{bi}.asset";
                    AssetDatabase.CreateAsset(data, assetPath);
                    list.Add(data);
                }
                result.Add(list);
            }
            return result;
        }

        private static void SetBaseStats(ShipData data, int level, float fighterToMinerBlend)
        {
            float scale = 0.9f + level * 0.08f;
            float fire = Mathf.Lerp(14f, 8f, fighterToMinerBlend) + (level - 1) * 2f;
            float mine = Mathf.Lerp(10f, 22f, fighterToMinerBlend) + (level - 1) * 3f;
            float health = Mathf.Lerp(130f, 95f, fighterToMinerBlend) + (level - 1) * 20f;
            float cap = Mathf.Lerp(110f, 180f, fighterToMinerBlend) + (level - 1) * 35f;
            if (level == 7) { fire *= 1.8f; mine *= 1.5f; health *= 2f; cap *= 1.8f; scale *= 1.2f; }
            data.baseMovementSpeed = 10f * scale;
            data.baseFireRate = Mathf.Lerp(1.2f, 0.85f, fighterToMinerBlend);
            data.baseFirePower = fire;
            data.baseBulletSpeed = Mathf.Lerp(24f, 18f, fighterToMinerBlend);
            data.baseMaxHealth = health;
            data.baseHealthRegenRate = 1.2f;
            data.baseRotationSpeed = 180f;
            data.baseGemCapacity = cap;
            data.basePeopleCapacity = 10f + (level - 1) * 2f;
            data.baseEnergyCapacity = 50f + (level - 1) * 8f;
            data.baseEnergyRegenRate = 5f;
            data.baseMiningRate = mine;
            data.miningMultiplier = Mathf.Lerp(1f, 1.3f, fighterToMinerBlend);
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
                foreach (var data in shipDataByLevel[li])
                {
                    string path = $"{PREFABS_SHIPS_FOLDER}/Starship_Lv{level}_{data.branchIndex}.prefab";
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) continue;
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                    var ship = instance.GetComponent<TitanOrbit.Entities.Starship>();
                    if (ship != null)
                    {
                        ship.SetShipData(data);
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
