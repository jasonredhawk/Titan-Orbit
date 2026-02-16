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

        [MenuItem("Titan Orbit/Rebuild Ship Prefabs (Unique Designs)")]
        public static void RebuildShipPrefabs()
        {
            EnsureFolders();
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
                        BuildProceduralShipVisual(instance, level, data.branchIndex, blend, count);
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

        /// <summary>Procedurally rebuilds the ship visual with unique hull shape, wings, engines, cockpit for each ship.</summary>
        private static void BuildProceduralShipVisual(GameObject shipRoot, int level, int branchIndex, float fighterToMinerBlend, int branchCount)
        {
            int seed = level * 50 + branchIndex;
            float R(int m) => ((seed * m) % 100) / 100f;

            float levelScale = 0.85f + (level - 1) * 0.15f;
            if (level == 7) levelScale *= 1.15f;
            shipRoot.transform.localScale = Vector3.one * levelScale;

            var hullMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/TitanOrbit_StarshipBody.mat");
            var accentMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/TitanOrbit_Starship.mat");
            var customHull = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Meshes/StarshipHull.asset");
            var cubeMesh = GetPrimitiveMesh(PrimitiveType.Cube);
            var sphereMesh = GetPrimitiveMesh(PrimitiveType.Sphere);
            var cylinderMesh = GetPrimitiveMesh(PrimitiveType.Cylinder);
            var capsuleMesh = GetPrimitiveMesh(PrimitiveType.Capsule);

            var root = shipRoot.transform;

            var rootMf = shipRoot.GetComponent<MeshFilter>();
            var rootMr = shipRoot.GetComponent<MeshRenderer>();
            if (rootMf != null) Object.DestroyImmediate(rootMf);
            if (rootMr != null) Object.DestroyImmediate(rootMr);

            RemoveVisualChildren(root, new[] { "Cockpit", "EngineL", "EngineR", "Engine1", "Engine2", "Engine3", "Engine4", "Wing1", "Wing2", "Wing3", "Wing4", "Hull" });

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

            int hullShape = (seed + 11) % 5;
            int engineCount = 1 + (seed + 7) % 4;
            int wingCount = ((seed + 13) % 3) * 2;
            bool cockpitForward = (seed % 2) == 0;
            bool cockpitElevated = ((seed >> 2) % 2) == 0;
            float hullLength = 0.6f + 0.4f * (level / 7f) + 0.15f * R(17);
            float hullWidth = Mathf.Lerp(0.35f, 0.55f, fighterToMinerBlend) + 0.1f * R(23);
            float hullHeight = Mathf.Lerp(0.18f, 0.28f, fighterToMinerBlend) + 0.06f * R(29);

            Mesh hullMesh;
            Vector3 hullScale;
            Quaternion hullRot = Quaternion.identity;
            switch (hullShape)
            {
                case 0: hullMesh = cubeMesh; hullScale = new Vector3(hullWidth, hullHeight, hullLength); break;
                case 1: hullMesh = cylinderMesh; hullScale = new Vector3(hullWidth, hullLength * 0.5f, hullWidth); hullRot = Quaternion.Euler(90f, 0f, 0f); break;
                case 2: hullMesh = capsuleMesh; hullScale = new Vector3(hullWidth, hullLength * 0.4f, hullWidth); hullRot = Quaternion.Euler(90f, 0f, 0f); break;
                case 3: hullMesh = sphereMesh; hullScale = new Vector3(hullWidth, hullHeight, hullLength) * 0.8f; break;
                default: hullMesh = customHull ?? cubeMesh; hullScale = new Vector3(hullWidth, hullHeight, hullLength); break;
            }

            CreateVisualPart(root, "Hull", hullMesh, hullMat, Vector3.zero, hullRot, hullScale, 10);

            float cockpitZ = cockpitForward ? hullLength * 0.35f : hullLength * 0.1f;
            float cockpitY = cockpitElevated ? hullHeight * 0.8f : hullHeight * 0.4f;
            float cockpitSz = Mathf.Lerp(0.2f, 0.14f, fighterToMinerBlend) + 0.04f * R(31);
            CreateVisualPart(root, "Cockpit", sphereMesh, accentMat,
                new Vector3(0f, cockpitY, cockpitZ), Quaternion.identity, Vector3.one * cockpitSz);

            float engineSize = Mathf.Lerp(0.08f, 0.14f, fighterToMinerBlend) + 0.03f * R(37);
            float engineZ = -hullLength * 0.5f - 0.1f;
            var enginePositions = GetEnginePositions(engineCount, seed, engineZ, fighterToMinerBlend);
            for (int i = 0; i < enginePositions.Count; i++)
            {
                CreateVisualPart(root, $"Engine{i + 1}", sphereMesh, accentMat, enginePositions[i], Quaternion.identity, Vector3.one * engineSize);
            }

            if (wingCount >= 2)
            {
                float wingSpan = Mathf.Lerp(0.15f, 0.28f, fighterToMinerBlend) + 0.05f * R(41);
                float wingLen = hullLength * 0.4f + 0.05f * R(43);
                float wingThick = 0.03f;
                var wingScale = wingCount == 2
                    ? new Vector3(wingSpan, wingThick, wingLen)
                    : new Vector3(wingSpan * 0.7f, wingThick, wingLen * 0.8f);
                float wingZ = -hullLength * 0.1f + 0.05f * R(47);
                float angleOffset = wingCount == 2 ? 90f : 45f;
                for (int i = 0; i < wingCount; i++)
                {
                    float angle = angleOffset + (360f / wingCount) * i;
                    var rot = Quaternion.Euler(0f, angle, 0f);
                    var pos = rot * new Vector3(wingSpan * 0.4f, hullHeight * 0.3f, wingZ);
                    CreateVisualPart(root, $"Wing{i + 1}", cubeMesh, accentMat, pos, rot, wingScale);
                }
            }

            firePoint.localPosition = new Vector3(0f, 0f, hullLength * 0.55f);

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
                boxCol.size = new Vector3(hullWidth * 2.2f, hullHeight * 2.5f, hullLength * 2.2f);
                boxCol.center = new Vector3(0f, hullHeight * 0.2f, 0f);
            }

            var teamColor = shipRoot.GetComponent<TitanOrbit.Entities.ShipTeamColor>();
            if (teamColor != null)
            {
                var so = new SerializedObject(teamColor);
                so.FindProperty("accentRenderers").ClearArray();
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static List<Vector3> GetEnginePositions(int count, int seed, float z, float blend)
        {
            var list = new List<Vector3>();
            float spread = Mathf.Lerp(0.08f, 0.2f, blend) + 0.04f * ((seed * 19) % 100) / 100f;
            switch (count)
            {
                case 1: list.Add(new Vector3(0f, 0f, z)); break;
                case 2: list.Add(new Vector3(-spread, 0f, z)); list.Add(new Vector3(spread, 0f, z)); break;
                case 3:
                    list.Add(new Vector3(0f, 0f, z));
                    list.Add(new Vector3(-spread, 0f, z - 0.05f));
                    list.Add(new Vector3(spread, 0f, z - 0.05f)); break;
                default:
                    list.Add(new Vector3(-spread * 0.7f, spread * 0.5f, z));
                    list.Add(new Vector3(spread * 0.7f, spread * 0.5f, z));
                    list.Add(new Vector3(-spread * 0.7f, -spread * 0.5f, z));
                    list.Add(new Vector3(spread * 0.7f, -spread * 0.5f, z)); break;
            }
            return list;
        }

        private static GameObject CreateVisualPart(Transform parent, string name, Mesh mesh, Material mat, Vector3 pos, Quaternion rot, Vector3 scale, int sortingOrder = 11)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            mr.receiveShadows = true;
            mr.sortingOrder = sortingOrder;
            return go;
        }

        private static Mesh GetPrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mf = go.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            Object.DestroyImmediate(go);
            return mesh;
        }

        private static void RemoveVisualChildren(Transform parent, string[] names)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (System.Array.Exists(names, n => child.name == n))
                    Object.DestroyImmediate(child.gameObject);
            }
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
