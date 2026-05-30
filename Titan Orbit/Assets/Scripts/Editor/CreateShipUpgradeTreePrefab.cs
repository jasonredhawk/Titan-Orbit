using TitanOrbit.Data;
using TitanOrbit.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Creates editable Ship Upgrade Tree prefabs (tree root + node template).
    /// Menu: Titan Orbit / UI / Create Ship Upgrade Tree Prefab
    /// </summary>
    public static class CreateShipUpgradeTreePrefab
    {
        private const string PrefabDir = "Assets/Prefabs/UI";
        private const string NodePrefabPath = PrefabDir + "/ShipUpgradeTreeNode.prefab";
        private const string TreePrefabPath = PrefabDir + "/ShipUpgradeTree.prefab";
        private const string ShipsTabPrefabPath = PrefabDir + "/ShipsTabContent.prefab";
        private const string DefaultPreviewFamilyPath = "Assets/Prefabs/Ships/AstroEagle/AstroEagleShipFamily.asset";

        [MenuItem("Titan Orbit/UI/Create Ship Upgrade Tree Prefab")]
        public static void CreatePrefabs()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder(PrefabDir))
                AssetDatabase.CreateFolder("Assets/Prefabs", "UI");

            var nodeTemplate = BuildNodeTemplate();
            var nodePrefab = SavePrefab(nodeTemplate, NodePrefabPath).GetComponent<ShipUpgradeTreeNodeUI>();
            Object.DestroyImmediate(nodeTemplate);

            var previewFamily = FindDefaultPreviewFamily();
            var treeRoot = BuildTreeRoot(nodePrefab);
            var treeUi = treeRoot.GetComponent<ShipUpgradeTreeUI>();
            AssignPreviewFamily(treeUi, previewFamily);
            SavePrefab(treeRoot, TreePrefabPath);
            Object.DestroyImmediate(treeRoot);

            var treeAsset = AssetDatabase.LoadAssetAtPath<ShipUpgradeTreeUI>(TreePrefabPath);
            GameObject shipsTabRoot = null;
            if (treeAsset != null)
            {
                shipsTabRoot = BuildShipsTabContent(treeAsset);
                SavePrefab(shipsTabRoot, ShipsTabPrefabPath);
                Object.DestroyImmediate(shipsTabRoot);

                string resourcesDir = "Assets/Resources";
                if (!AssetDatabase.IsValidFolder(resourcesDir))
                    AssetDatabase.CreateFolder("Assets", "Resources");
                string resourcePath = resourcesDir + "/ShipUpgradeTree.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(resourcePath) != null)
                    AssetDatabase.DeleteAsset(resourcePath);
                AssetDatabase.CopyAsset(TreePrefabPath, resourcePath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            string familyNote = previewFamily != null
                ? $"\nPreview family: {previewFamily.name}"
                : "\nNo ShipFamilyDefinition found — tree uses branch placeholders.";
            EditorUtility.DisplayDialog(
                "Ship Upgrade Tree",
                "Created (empty nodes canvas — preview via inspector Refresh):\n" +
                TreePrefabPath + "\n" + NodePrefabPath + "\n" + ShipsTabPrefabPath +
                familyNote +
                "\n\nOpen ShipUpgradeTree prefab, assign Preview Family to refresh data. " +
                "Assign ShipUpgradeTree.prefab to OrbitStationUI → Ship Upgrade Tree Prefab.",
                "OK");
        }

        private static ShipFamilyDefinition FindDefaultPreviewFamily()
        {
            var preferred = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(DefaultPreviewFamilyPath);
            if (preferred != null && preferred.upgradeTree != null && preferred.upgradeTree.Count > 0)
                return preferred;

            foreach (string guid in AssetDatabase.FindAssets("t:ShipFamilyDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def?.upgradeTree != null && def.upgradeTree.Count > 0)
                    return def;
            }

            return null;
        }

        private static void AssignPreviewFamily(ShipUpgradeTreeUI treeUi, ShipFamilyDefinition family)
        {
            if (treeUi == null) return;
            var so = new SerializedObject(treeUi);
            so.FindProperty("previewFamily").objectReferenceValue = family;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject BuildShipsTabContent(ShipUpgradeTreeUI treePrefabAsset)
        {
            var root = new GameObject("ShipsTabContent");
            var rootRt = root.AddComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0f, 1f);
            rootRt.anchorMax = new Vector2(1f, 1f);
            rootRt.pivot = new Vector2(0.5f, 1f);
            rootRt.sizeDelta = new Vector2(0f, 620f);

            var rootLe = root.AddComponent<LayoutElement>();
            rootLe.preferredHeight = 620f;
            rootLe.minHeight = 520f;
            rootLe.flexibleWidth = 1f;

            var vlg = root.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4f;
            vlg.padding = new RectOffset(12, 12, 8, 12);
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var title = CreateTmp("SectionHeaderTitle", root.transform, 22, FontStyles.Bold, TextAlignmentOptions.Left);
            title.text = "Ship upgrade tree";
            title.color = new Color(0.94f, 0.96f, 1f, 1f);
            title.enableWordWrapping = false;
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 34f;
            titleLe.flexibleHeight = 0f;

            var subtitle = CreateTmp("SectionHeaderSub", root.transform, 13, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            subtitle.text = "Each tier splits into branches — ship preview and price on the right.";
            subtitle.color = new Color(0.78f, 0.86f, 0.96f, 0.98f);
            subtitle.enableWordWrapping = true;
            var subLe = subtitle.gameObject.AddComponent<LayoutElement>();
            subLe.preferredHeight = 44f;
            subLe.flexibleHeight = 0f;

            var treeGo = (GameObject)PrefabUtility.InstantiatePrefab(treePrefabAsset.gameObject, root.transform);
            treeGo.name = "ShipUpgradeTree";
            var treeLe = treeGo.GetComponent<LayoutElement>();
            if (treeLe == null)
                treeLe = treeGo.AddComponent<LayoutElement>();
            treeLe.flexibleWidth = 1f;
            treeLe.flexibleHeight = 1f;
            treeLe.minHeight = 480f;
            treeLe.preferredHeight = 520f;

            return root;
        }

        private static GameObject SavePrefab(GameObject root, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
                PrefabUtility.SaveAsPrefabAssetAndConnect(root, path, InteractionMode.AutomatedAction);
            else
                PrefabUtility.SaveAsPrefabAsset(root, path);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private static GameObject BuildTreeRoot(ShipUpgradeTreeNodeUI nodePrefab)
        {
            var root = new GameObject("ShipUpgradeTree");
            var rootRt = root.AddComponent<RectTransform>();
            rootRt.sizeDelta = new Vector2(900f, 520f);
            var rootLe = root.AddComponent<LayoutElement>();
            rootLe.minHeight = 420f;
            rootLe.preferredHeight = 520f;
            rootLe.flexibleWidth = 1f;

            var vlg = root.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.padding = new RectOffset(0, 0, 0, 8);
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var hint = CreateTmp("Hint", root.transform, 13, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            hint.text = "Upgrade tree hint.";
            hint.enableWordWrapping = true;
            hint.color = new Color(0.78f, 0.88f, 0.98f, 0.96f);
            var hintLe = hint.gameObject.AddComponent<LayoutElement>();
            hintLe.preferredHeight = 26f;
            hintLe.minHeight = 22f;
            hintLe.flexibleHeight = 0f;

            var legendGo = BuildLegend(root.transform);

            var centerRow = new GameObject("CenterRow");
            centerRow.transform.SetParent(root.transform, false);
            var centerRt = centerRow.AddComponent<RectTransform>();
            var centerLe = centerRow.AddComponent<LayoutElement>();
            centerLe.flexibleHeight = 1f;
            centerLe.minHeight = 360f;
            centerLe.preferredHeight = 420f;
            var centerHlg = centerRow.AddComponent<HorizontalLayoutGroup>();
            centerHlg.childAlignment = TextAnchor.MiddleCenter;
            centerHlg.childControlWidth = true;
            centerHlg.childControlHeight = true;
            centerHlg.childForceExpandWidth = false;
            centerHlg.childForceExpandHeight = true;
            centerHlg.padding = new RectOffset(4, 4, 0, 0);

            var nodesCanvas = new GameObject("NodesCanvas");
            nodesCanvas.transform.SetParent(centerRow.transform, false);
            var canvasRt = nodesCanvas.AddComponent<RectTransform>();
            canvasRt.anchorMin = new Vector2(0.5f, 0.5f);
            canvasRt.anchorMax = new Vector2(0.5f, 0.5f);
            canvasRt.pivot = new Vector2(0.5f, 0.5f);
            canvasRt.anchoredPosition = Vector2.zero;
            canvasRt.sizeDelta = new Vector2(880f, 400f);
            var canvasLe = nodesCanvas.AddComponent<LayoutElement>();
            canvasLe.flexibleWidth = 0f;
            canvasLe.minWidth = 200f;
            nodesCanvas.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            nodesCanvas.AddComponent<ScrollRectForwarder>();

            var treeUi = root.AddComponent<ShipUpgradeTreeUI>();
            var so = new SerializedObject(treeUi);
            so.FindProperty("hintText").objectReferenceValue = hint;
            so.FindProperty("legend").objectReferenceValue = legendGo.GetComponent<ShipUpgradeTreeLegendUI>();
            so.FindProperty("centerRow").objectReferenceValue = centerRt;
            so.FindProperty("nodesCanvas").objectReferenceValue = canvasRt;
            so.FindProperty("nodePrefab").objectReferenceValue = nodePrefab;
            so.ApplyModifiedPropertiesWithoutUndo();

            return root;
        }

        private static GameObject BuildLegend(Transform parent)
        {
            var legendGo = new GameObject("Legend");
            legendGo.transform.SetParent(parent, false);
            var legendLe = legendGo.AddComponent<LayoutElement>();
            legendLe.preferredHeight = 56f;
            legendLe.minHeight = 52f;
            legendLe.flexibleHeight = 0f;

            var rootVlg = legendGo.AddComponent<VerticalLayoutGroup>();
            rootVlg.spacing = 4f;
            rootVlg.childAlignment = TextAnchor.UpperLeft;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            rootVlg.childForceExpandHeight = false;

            var row0 = new GameObject("Row0");
            row0.transform.SetParent(legendGo.transform, false);
            var row0Hlg = row0.AddComponent<HorizontalLayoutGroup>();
            row0Hlg.spacing = 10f;
            row0Hlg.childAlignment = TextAnchor.MiddleLeft;
            row0Hlg.childControlWidth = true;
            row0Hlg.childControlHeight = true;
            row0Hlg.childForceExpandWidth = true;
            row0Hlg.childForceExpandHeight = true;
            var row0Le = row0.AddComponent<LayoutElement>();
            row0Le.flexibleWidth = 1f;
            row0Le.preferredHeight = 26f;
            row0Le.minHeight = 22f;

            var row1 = new GameObject("Row1");
            row1.transform.SetParent(legendGo.transform, false);
            var row1Hlg = row1.AddComponent<HorizontalLayoutGroup>();
            row1Hlg.spacing = 10f;
            row1Hlg.childAlignment = TextAnchor.MiddleCenter;
            row1Hlg.childControlWidth = true;
            row1Hlg.childControlHeight = true;
            row1Hlg.childForceExpandWidth = true;
            row1Hlg.childForceExpandHeight = true;
            var row1Le = row1.AddComponent<LayoutElement>();
            row1Le.flexibleWidth = 1f;
            row1Le.preferredHeight = 26f;
            row1Le.minHeight = 22f;

            var pairs = new ShipUpgradeTreeLegendPairUI[ShipAbilityCategoryColors.PowerBreakdownPairCount];
            for (int i = 0; i < pairs.Length; i++)
            {
                var row = i < 3 ? row0.transform : row1.transform;
                pairs[i] = BuildLegendPair(row, i);
            }

            var legendUi = legendGo.AddComponent<ShipUpgradeTreeLegendUI>();
            var so = new SerializedObject(legendUi);
            so.FindProperty("pairs").arraySize = pairs.Length;
            for (int i = 0; i < pairs.Length; i++)
                so.FindProperty("pairs").GetArrayElementAtIndex(i).objectReferenceValue = pairs[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            return legendGo;
        }

        private static ShipUpgradeTreeLegendPairUI BuildLegendPair(Transform parent, int pairIndex)
        {
            var go = new GameObject($"Pair_{pairIndex}");
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minWidth = 64f;

            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 1f;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var title = CreateTmp("CategoryTitle", go.transform, 12, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            title.enableWordWrapping = false;
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 14f;
            titleLe.flexibleHeight = 0f;

            var row = new GameObject("SwatchRow");
            row.transform.SetParent(go.transform, false);
            var rowHlg = row.AddComponent<HorizontalLayoutGroup>();
            rowHlg.spacing = 4f;
            rowHlg.childAlignment = TextAnchor.MiddleLeft;
            rowHlg.childControlWidth = false;
            rowHlg.childControlHeight = true;
            var rowLe = row.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 12f;
            rowLe.flexibleHeight = 0f;

            var swatchA = CreateSwatch(row.transform);
            var swatchB = CreateSwatch(row.transform);

            var statLine = CreateTmp("StatLine", go.transform, 10, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            statLine.enableWordWrapping = true;
            statLine.overflowMode = TextOverflowModes.Ellipsis;
            statLine.color = new Color(0.78f, 0.84f, 0.92f, 1f);
            var statLe = statLine.gameObject.AddComponent<LayoutElement>();
            statLe.flexibleWidth = 1f;
            statLe.preferredHeight = 14f;
            statLe.minHeight = 12f;

            var pairUi = go.AddComponent<ShipUpgradeTreeLegendPairUI>();
            var so = new SerializedObject(pairUi);
            so.FindProperty("categoryTitle").objectReferenceValue = title;
            so.FindProperty("toneASwatch").objectReferenceValue = swatchA;
            so.FindProperty("toneBSwatch").objectReferenceValue = swatchB;
            so.FindProperty("statLine").objectReferenceValue = statLine;
            so.ApplyModifiedPropertiesWithoutUndo();
            pairUi.Configure(pairIndex);
            return pairUi;
        }

        private static Image CreateSwatch(Transform parent)
        {
            var go = new GameObject("Swatch");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 10f;
            le.preferredHeight = 10f;
            le.minWidth = 10f;
            le.minHeight = 10f;
            return img;
        }

        private static GameObject BuildNodeTemplate()
        {
            const float nodeWidth = 120f;
            const float nodeHeight = 100f;

            var root = new GameObject("ShipUpgradeTreeNode");
            var rt = root.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(nodeWidth, nodeHeight);
            var rootLe = root.AddComponent<LayoutElement>();
            rootLe.ignoreLayout = true;
            rootLe.flexibleWidth = 0f;
            rootLe.flexibleHeight = 0f;
            rootLe.minWidth = nodeWidth;
            rootLe.preferredWidth = nodeWidth;
            rootLe.minHeight = nodeHeight;
            rootLe.preferredHeight = nodeHeight;
            var bg = root.AddComponent<Image>();
            bg.color = new Color(0.1f, 0.14f, 0.22f, 0.98f);
            var btn = root.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.transition = Selectable.Transition.None;

            var vlg = root.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(4, 6, 4, 4);
            vlg.spacing = 4f;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var contentRow = new GameObject("ContentRow");
            contentRow.transform.SetParent(root.transform, false);
            var contentLe = contentRow.AddComponent<LayoutElement>();
            contentLe.flexibleHeight = 1f;
            contentLe.flexibleWidth = 1f;
            contentLe.minHeight = 72f;
            var hlg = contentRow.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.spacing = 5f;
            hlg.childAlignment = TextAnchor.UpperLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;

            var left = new GameObject("LeftColumn");
            left.transform.SetParent(contentRow.transform, false);
            var leftVlg = left.AddComponent<VerticalLayoutGroup>();
            leftVlg.spacing = 2f;
            leftVlg.childAlignment = TextAnchor.UpperLeft;
            leftVlg.childControlWidth = true;
            leftVlg.childControlHeight = true;
            leftVlg.childForceExpandWidth = true;
            leftVlg.childForceExpandHeight = false;
            var leftLe = left.AddComponent<LayoutElement>();
            leftLe.flexibleWidth = 1f;
            leftLe.minWidth = 40f;

            var level = CreateTmp("Level", left.transform, 13, FontStyles.Bold, TextAlignmentOptions.TopLeft);
            level.color = new Color(0.55f, 0.78f, 1f, 1f);
            level.enableWordWrapping = false;
            var levelLe = level.gameObject.AddComponent<LayoutElement>();
            levelLe.preferredHeight = 14f;
            levelLe.flexibleHeight = 0f;

            var nameGo = new GameObject("ShipName");
            nameGo.transform.SetParent(left.transform, false);
            nameGo.AddComponent<RectMask2D>();
            var name = nameGo.AddComponent<TextMeshProUGUI>();
            name.fontSize = 11;
            name.lineSpacing = 0f;
            name.enableWordWrapping = true;
            name.overflowMode = TextOverflowModes.Ellipsis;
            name.alignment = TextAlignmentOptions.TopLeft;
            name.color = new Color(0.95f, 0.97f, 1f, 1f);
            name.raycastTarget = false;
            var nameLe = nameGo.AddComponent<LayoutElement>();
            nameLe.preferredHeight = 26f;
            nameLe.minHeight = 22f;
            nameLe.flexibleHeight = 0f;

            var price = CreateTmp("Price", left.transform, 11, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            price.color = new Color(0.55f, 0.88f, 0.72f, 1f);
            price.enableWordWrapping = false;
            var priceLe = price.gameObject.AddComponent<LayoutElement>();
            priceLe.preferredHeight = 14f;
            priceLe.flexibleHeight = 0f;

            var previewCol = new GameObject("PreviewColumn");
            previewCol.transform.SetParent(contentRow.transform, false);
            var previewLe = previewCol.AddComponent<LayoutElement>();
            previewLe.preferredWidth = 56f;
            previewLe.minWidth = 56f;
            previewLe.flexibleWidth = 0f;
            previewLe.flexibleHeight = 1f;
            var previewVlg = previewCol.AddComponent<VerticalLayoutGroup>();
            previewVlg.spacing = 0f;
            previewVlg.padding = new RectOffset(0, 0, 0, 0);
            previewVlg.childAlignment = TextAnchor.UpperRight;
            previewVlg.childControlWidth = true;
            previewVlg.childControlHeight = true;
            previewVlg.childForceExpandWidth = true;
            previewVlg.childForceExpandHeight = false;

            var previewHolder = new GameObject("Preview");
            previewHolder.transform.SetParent(previewCol.transform, false);
            var previewImg = previewHolder.AddComponent<Image>();
            previewImg.preserveAspect = true;
            previewImg.raycastTarget = false;
            var previewImgLe = previewHolder.AddComponent<LayoutElement>();
            previewImgLe.preferredHeight = 56f;
            previewImgLe.preferredWidth = 56f;
            previewImgLe.flexibleHeight = 1f;
            previewImgLe.minHeight = 48f;

            var powerBarGo = BuildPowerBar(root.transform);

            var nodeUi = root.AddComponent<ShipUpgradeTreeNodeUI>();
            var so = new SerializedObject(nodeUi);
            so.FindProperty("layoutWidth").floatValue = nodeWidth;
            so.FindProperty("layoutHeight").floatValue = nodeHeight;
            so.FindProperty("button").objectReferenceValue = btn;
            so.FindProperty("levelText").objectReferenceValue = level;
            so.FindProperty("shipNameText").objectReferenceValue = name;
            so.FindProperty("priceText").objectReferenceValue = price;
            so.FindProperty("previewImage").objectReferenceValue = previewImg;
            so.FindProperty("powerBar").objectReferenceValue = powerBarGo.GetComponent<ShipUpgradeTreePowerBarUI>();
            so.ApplyModifiedPropertiesWithoutUndo();

            return root;
        }

        private static GameObject BuildPowerBar(Transform parent)
        {
            var barRow = new GameObject("PowerBar");
            barRow.transform.SetParent(parent, false);
            var barLe = barRow.AddComponent<LayoutElement>();
            barLe.preferredHeight = 10f;
            barLe.minHeight = 10f;
            barLe.flexibleHeight = 0f;
            barLe.flexibleWidth = 1f;
            barLe.minWidth = 48f;

            var barHlg = barRow.AddComponent<HorizontalLayoutGroup>();
            barHlg.spacing = 4f;
            barHlg.childAlignment = TextAnchor.MiddleLeft;
            barHlg.childControlWidth = true;
            barHlg.childControlHeight = true;
            barHlg.childForceExpandWidth = false;
            barHlg.childForceExpandHeight = true;

            var segments = new Image[ShipAbilityCategoryColors.PowerBreakdownStatCount];
            for (int pair = 0; pair < ShipAbilityCategoryColors.PowerBreakdownPairCount; pair++)
            {
                var pairGo = new GameObject("Pair_" + pair);
                pairGo.transform.SetParent(barRow.transform, false);
                var pairHlg = pairGo.AddComponent<HorizontalLayoutGroup>();
                pairHlg.spacing = 0f;
                pairHlg.childAlignment = TextAnchor.MiddleLeft;
                pairHlg.childControlWidth = true;
                pairHlg.childControlHeight = true;
                pairHlg.childForceExpandWidth = false;
                pairHlg.childForceExpandHeight = true;
                var pairLe = pairGo.AddComponent<LayoutElement>();
                pairLe.flexibleWidth = 0f;

                for (int tone = 0; tone < 2; tone++)
                {
                    int idx = pair * 2 + tone;
                    var segGo = new GameObject("Seg_" + idx);
                    segGo.transform.SetParent(pairGo.transform, false);
                    var segImg = segGo.AddComponent<Image>();
                    segImg.color = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(idx);
                    segImg.raycastTarget = false;
                    var segLe = segGo.AddComponent<LayoutElement>();
                    segLe.flexibleWidth = 0f;
                    segLe.minWidth = 0f;
                    segLe.preferredHeight = 10f;
                    segments[idx] = segImg;
                }
            }

            var powerBar = barRow.AddComponent<ShipUpgradeTreePowerBarUI>();
            var so = new SerializedObject(powerBar);
            so.FindProperty("segments").arraySize = segments.Length;
            for (int i = 0; i < segments.Length; i++)
                so.FindProperty("segments").GetArrayElementAtIndex(i).objectReferenceValue = segments[i];
            so.FindProperty("barHeight").floatValue = 10f;
            so.FindProperty("pairGap").floatValue = 4f;
            so.ApplyModifiedPropertiesWithoutUndo();
            return barRow;
        }

        private static TextMeshProUGUI CreateTmp(
            string name,
            Transform parent,
            int fontSize,
            FontStyles style,
            TextAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = align;
            tmp.enableAutoSizing = false;
            tmp.richText = false;
            tmp.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }
    }
}
