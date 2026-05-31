using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TitanOrbit.UI
{
    /// <summary>
    /// Prefab-driven ship upgrade tree (hint, nodes canvas). Assign on a GameObject under the ships tab;
    /// <see cref="OrbitStationUI"/> binds runtime state. Optional <see cref="previewFamily"/> fills the editor preview.
    /// </summary>
    public class ShipUpgradeTreeUI : MonoBehaviour
    {
        public const string PanelTitleText = "Ship Upgrade Tree";
        public const string PanelDefaultSubtitle = "Green: your ship. Blue: affordable upgrades. Cyan: free hull swap.";

        private const float CanvasInnerMargin = 8f;
        private const float MoonNodeHeight = 100f;
        private const float MoonMinNodeWidth = 74f;
        private const float MoonLevelColGap = 12f;
        private const float MoonBranchGapY = 8f;
        private const float LayoutWidthBucketPixels = 32f;
        private const float MoonChromeHeightHint = 28f;
        private const float VerticalNodeHeight = 188f;
        private const float VerticalLevelSpacing = VerticalNodeHeight + 44f;
        private const float VerticalColGap = 6f;
        private static readonly Color ConnectorDim = new Color(0.45f, 0.62f, 0.85f, 0.55f);
        private static readonly Color ConnectorPath = new Color(0.35f, 0.98f, 0.62f, 0.92f);
        private static readonly Vector3[] ConnectorCornerBuffer = new Vector3[4];

        [Header("Template references (edit on prefab)")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI hintText;
        [SerializeField] private RectTransform centerRow;
        [SerializeField] private RectTransform nodesCanvas;
        [SerializeField] private ShipUpgradeTreeNodeUI nodePrefab;
        [SerializeField] private Sprite nodeBackgroundSprite;

        [Header("Editor preview")]
        [SerializeField] private ShipFamilyDefinition previewFamily;

        public ShipFamilyDefinition PreviewFamily => previewFamily;

        private OrbitStationUI _station;
        private readonly List<ShipUpgradeTreeNodeUI> _nodes = new List<ShipUpgradeTreeNodeUI>();
        private readonly List<GameObject> _visuals = new List<GameObject>();
        private readonly List<int> _nextTargets = new List<int>(4);
        private string _structureKey = string.Empty;

        public TextMeshProUGUI Title => titleText;
        public TextMeshProUGUI Hint => hintText;
        public RectTransform CenterRow => centerRow;
        public RectTransform NodesCanvas => nodesCanvas;
        public IReadOnlyList<ShipUpgradeTreeNodeUI> Nodes => _nodes;

        private void Awake()
        {
            EnsurePanelHeader();
        }

        public void BindStation(OrbitStationUI station)
        {
            _station = station;
            EnsurePanelHeader();
        }

        /// <summary>Creates a title row on older prefabs that only had a dynamic hint line.</summary>
        public void EnsurePanelHeader()
        {
            if (titleText == null)
            {
                var existing = transform.Find("Title");
                if (existing != null)
                    titleText = existing.GetComponent<TextMeshProUGUI>();
            }

            if (titleText != null)
            {
                titleText.text = PanelTitleText;
                return;
            }

            var titleGo = new GameObject("Title", typeof(RectTransform));
            titleGo.transform.SetParent(transform, false);
            titleGo.transform.SetAsFirstSibling();

            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = PanelTitleText;
            titleText.fontSize = 22;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Left;
            titleText.color = new Color(0.94f, 0.96f, 1f, 1f);
            titleText.enableWordWrapping = false;
            titleText.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                titleText.font = TMP_Settings.defaultFontAsset;

            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 34f;
            titleLe.minHeight = 28f;
            titleLe.flexibleHeight = 0f;
        }

        public void RebuildIfNeeded(bool moonHorizontal, string structureKey)
        {
            int widthBucket = -1;
            if (moonHorizontal)
            {
                GetMoonContainerSize(out float containerW, out _);
                widthBucket = Mathf.RoundToInt(containerW / LayoutWidthBucketPixels);
            }

            string layoutKey = moonHorizontal ? $"{structureKey}_wb{widthBucket}" : structureKey;
            if (_structureKey == layoutKey && _nodes.Count > 0 && !HasOrphanNodesCanvasChildren())
            {
                RefreshVisualState();
                return;
            }

            _structureKey = layoutKey;
            Clear();
            if (_station == null || nodesCanvas == null || nodePrefab == null)
                return;

            if (moonHorizontal)
            {
                PrepareHorizontalContainerLayout();
                BuildHorizontal();
            }
            else
                BuildVertical();

            RefreshVisualState();
        }

        public void Clear()
        {
            ClearNodesCanvasChildren();
            _visuals.Clear();
            _nodes.Clear();
        }

        private bool HasOrphanNodesCanvasChildren()
        {
            if (nodesCanvas == null)
                return false;
            return nodesCanvas.childCount > _nodes.Count;
        }

        /// <summary>Removes all dynamic/baked children under the nodes canvas (fixes duplicate overlapping nodes).</summary>
        private void ClearNodesCanvasChildren()
        {
            if (nodesCanvas == null)
                return;

            for (int i = nodesCanvas.childCount - 1; i >= 0; i--)
            {
                var child = nodesCanvas.GetChild(i);
                if (child == null)
                    continue;
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    DestroyImmediate(child.gameObject);
                else
#endif
                    Destroy(child.gameObject);
            }
        }

        public void RefreshVisualState()
        {
            if (_station == null || _nodes.Count == 0)
                return;

            _station.RefreshShipUpgradeTreeNodeStates(_nodes, ComputeMaxDisplayPower());
        }

        /// <summary>Editor: populate all tier slots and connectors. Uses <paramref name="family"/> when assigned.</summary>
        public void EditorPreviewFromFamily(ShipFamilyDefinition family)
        {
            previewFamily = family;
            Clear();
            if (nodesCanvas == null || nodePrefab == null)
                return;

            float maxPower = 0.001f;
            if (family?.upgradeTree != null)
            {
                foreach (var tier in family.upgradeTree)
                {
                    if (tier == null) continue;
                    float t = tier.powerScoreBreakdown.GetDisplayTotalForUi();
                    if (t > maxPower) maxPower = t;
                }
            }

            const int maxLevel = 7;
            PrepareHorizontalContainerLayout();
            ComputeMoonHorizontalGeometry(out float nodeW, out float nodeH, out float canvasW, out float canvasH);
            ApplyHorizontalTreeCanvasLayout(canvasW, canvasH);
            float trackW = GetMoonPowerBarTrackWidth(nodeW);

            var byLevel = new Dictionary<int, List<ShipUpgradeTreeNodeUI>>();
            int tierIndex = 0;
            for (int level = 1; level <= maxLevel; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                var views = new List<ShipUpgradeTreeNodeUI>(count);
                float colX = 0f;
                float nodeY = 0f;
                for (int b = 0; b < count; b++)
                {
                    ShipFamilyChassisTierEntry tier = null;
                    if (family?.upgradeTree != null && tierIndex < family.upgradeTree.Count)
                        tier = family.upgradeTree[tierIndex];
                    if (family?.upgradeTree != null)
                        tierIndex++;

                    var node = InstantiateNodeForPreview();
                    node.BindSlot(level, b, null, nodeW, nodeH, trackW);
                    node.ConfigureLayout(true);
                    node.SetLevelLabel(level == 1 ? "Lv 1" : $"Lv {level}");
                    string shipName = tier != null
                        ? (string.IsNullOrEmpty(tier.upgradeTreeShipName) ? tier.chassisId : tier.upgradeTreeShipName)
                        : $"Branch {b + 1}";
                    node.SetShipName(shipName);
                    node.SetPrice(family != null ? "—" : "Preview");
                    node.SetPreview(tier != null ? tier.menuPreviewSprite : null);
                    if (tier != null)
                        node.ApplyPowerBreakdown(tier.powerScoreBreakdown, maxPower);
                    else
                        node.ApplyPowerBreakdown(default, maxPower);

                    GetMoonNodePosition(level, b, count, nodeW, nodeH, canvasW, canvasH, ComputeMaxColumnStackHeight(nodeH), out colX, out nodeY);
                    node.Rect.anchoredPosition = new Vector2(colX, nodeY);
                    views.Add(node);
                    _nodes.Add(node);
                    _visuals.Add(node.gameObject);
                }

                byLevel[level] = views;
            }

            ForceLayoutBeforeConnectors();
            EnforceUniformNodeSizes(nodeW, nodeH, trackW);
            DrawConnectors(byLevel, null, moonHorizontal: true);

            if (hintText != null)
            {
                hintText.text = family != null
                    ? $"Editor preview: {family.name}. Runtime uses live planet/ship state."
                    : "Assign Preview Family to fill names, previews, and power bars.";
            }
        }

        private ShipUpgradeTreeNodeUI InstantiateNodeForPreview()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && nodePrefab != null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(nodePrefab.gameObject, nodesCanvas);
                go.SetActive(true);
                return go.GetComponent<ShipUpgradeTreeNodeUI>();
            }
#endif
            var view = Instantiate(nodePrefab, nodesCanvas);
            view.gameObject.SetActive(true);
            return view;
        }

        private void BuildHorizontal()
        {
            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null || !_station.IsTreeDataAvailable())
            {
                SetUnavailable();
                return;
            }

            const int maxLevel = 7;
            ComputeMoonHorizontalGeometry(out float nodeW, out float nodeH, out float canvasW, out float canvasH);
            ApplyHorizontalTreeCanvasLayout(canvasW, canvasH);
            float trackW = GetMoonPowerBarTrackWidth(nodeW);

            float maxPower = ComputeMaxDisplayPower();
            _station.TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> pathEdges);
            var byLevel = new Dictionary<int, List<ShipUpgradeTreeNodeUI>>();

            for (int level = 1; level <= maxLevel; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                var views = new List<ShipUpgradeTreeNodeUI>(count);
                float colX = 0f;
                float nodeY = 0f;
                for (int b = 0; b < count; b++)
                {
                    ShipUpgradeNode upgradeNode = level == 1 ? null : tree.GetNodeForBranch(level, b);
                    var view = SpawnNode(level, b, upgradeNode, nodeW, nodeH, trackW);
                    view.ConfigureLayout(true);
                    GetMoonNodePosition(level, b, count, nodeW, nodeH, canvasW, canvasH, ComputeMaxColumnStackHeight(nodeH), out colX, out nodeY);
                    view.Rect.anchoredPosition = new Vector2(colX, nodeY);
                    views.Add(view);
                }

                byLevel[level] = views;
            }

            ForceLayoutBeforeConnectors();
            EnforceUniformNodeSizes(nodeW, nodeH, trackW);
            DrawConnectors(byLevel, pathEdges, moonHorizontal: true);
        }

        private void BuildVertical()
        {
            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null || !_station.IsTreeDataAvailable())
            {
                SetUnavailable();
                return;
            }

            nodesCanvas.anchorMin = new Vector2(0.5f, 0.5f);
            nodesCanvas.anchorMax = new Vector2(0.5f, 0.5f);
            nodesCanvas.pivot = new Vector2(0.5f, 0.5f);
            nodesCanvas.anchoredPosition = Vector2.zero;

            const int maxLevel = 7;
            float margin = CanvasInnerMargin;
            float innerW = Mathf.Max(120f, _station.GetShipTreeLayoutBasisWidthPublic() - 2f * margin);
            float nodeW = Mathf.Max(52f, innerW / 6f);
            float nodeH = VerticalNodeHeight;
            float contentH = Mathf.Max(160f, margin * 2f + (maxLevel - 1) * VerticalLevelSpacing + nodeH);
            nodesCanvas.sizeDelta = new Vector2(nodesCanvas.sizeDelta.x, contentH);
            ApplyCenterRowHeight(contentH);

            float maxPower = ComputeMaxDisplayPower();
            var byLevel = new Dictionary<int, List<ShipUpgradeTreeNodeUI>>();

            for (int level = 1; level <= maxLevel; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                var views = new List<ShipUpgradeTreeNodeUI>(count);
                float rowW = count * nodeW + (count - 1) * VerticalColGap;
                float startX = margin + (innerW - rowW) * 0.5f;
                float y = margin + (level - 1) * VerticalLevelSpacing;
                for (int b = 0; b < count; b++)
                {
                    ShipUpgradeNode upgradeNode = level == 1 ? null : tree.GetNodeForBranch(level, b);
                    var view = SpawnNode(level, b, upgradeNode, nodeW, nodeH, nodeW);
                    view.ConfigureLayout(false);
                    float x = startX + nodeW * 0.5f + b * (nodeW + VerticalColGap);
                    view.Rect.anchoredPosition = new Vector2(Mathf.Round(x), Mathf.Round(y));
                    views.Add(view);
                }

                byLevel[level] = views;
            }

            ForceLayoutBeforeConnectors();
            DrawConnectors(byLevel, null, moonHorizontal: false);
        }

        private static void GetMoonNodePosition(
            int level,
            int branch,
            int branchCount,
            float nodeW,
            float nodeH,
            float canvasW,
            float canvasH,
            float maxColStackH,
            out float x,
            out float y)
        {
            float halfW = canvasW * 0.5f;
            float halfH = canvasH * 0.5f;
            float margin = CanvasInnerMargin;
            float stackH = branchCount * nodeH + (branchCount - 1) * MoonBranchGapY;
            float stackTop = halfH - margin - (maxColStackH - stackH) * 0.5f;
            x = Mathf.Round(-halfW + margin + nodeW * 0.5f + (level - 1) * (nodeW + MoonLevelColGap));
            y = Mathf.Round(stackTop - nodeH * 0.5f - (branchCount - 1 - branch) * (nodeH + MoonBranchGapY));
        }

        private void ForceLayoutBeforeConnectors()
        {
            Canvas.ForceUpdateCanvases();
            if (nodesCanvas != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(nodesCanvas);
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i]?.Rect != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_nodes[i].Rect);
            }
        }

        private void EnforceUniformNodeSizes(float nodeW, float nodeH, float trackW)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                var node = _nodes[i];
                if (node == null)
                    continue;
                node.EnforceLayoutSize(nodeW, nodeH, trackW);
            }
        }

        private ShipUpgradeTreeNodeUI SpawnNode(int level, int branch, ShipUpgradeNode node, float w, float h, float trackW)
        {
            var view = Instantiate(nodePrefab, nodesCanvas);
            view.gameObject.SetActive(true);
            if (nodeBackgroundSprite != null)
            {
                var bg = view.GetComponent<Image>();
                if (bg != null)
                {
                    bg.sprite = nodeBackgroundSprite;
                    bg.type = Image.Type.Sliced;
                }
            }

            view.BindSlot(level, branch, node, w, h, trackW);
            view.EnsureStableButtonRendering();
            view.SetPriceClickHandler(() => _station.OnUpgradeTreeNodeClicked(level, branch));
            _station.PopulateTreeNode(view, ComputeMaxDisplayPower());
            _nodes.Add(view);
            _visuals.Add(view.gameObject);
            return view;
        }

        private void DrawConnectors(
            Dictionary<int, List<ShipUpgradeTreeNodeUI>> byLevel,
            HashSet<(int fL, int fB, int tL, int tB)> pathEdges,
            bool moonHorizontal)
        {
            for (int level = 2; level <= 7; level++)
            {
                if (!byLevel.TryGetValue(level, out var levelViews)) continue;
                if (!byLevel.TryGetValue(level - 1, out var prevViews)) continue;
                foreach (var prev in prevViews)
                {
                    foreach (var next in levelViews)
                    {
                        if (!UpgradeTree.IsValidUpgradeStep(level - 1, prev.BranchIndex, level, next.BranchIndex))
                            continue;
                        bool onPath = pathEdges != null && pathEdges.Contains((level - 1, prev.BranchIndex, level, next.BranchIndex));
                        Vector2 from = moonHorizontal
                            ? GetRectEdgeMidpoint(prev.Rect, rightEdge: true)
                            : GetRectEdgeMidpoint(prev.Rect, rightEdge: false, verticalOut: true);
                        Vector2 to = moonHorizontal
                            ? GetRectEdgeMidpoint(next.Rect, rightEdge: false)
                            : GetRectEdgeMidpoint(next.Rect, rightEdge: true, verticalIn: true);
                        DrawConnector(from, to, onPath ? ConnectorPath : ConnectorDim, onPath ? 3.5f : 2f);
                    }
                }
            }
        }

        private static float GetMoonPowerBarTrackWidth(float nodeW) =>
            Mathf.Max(48f, nodeW - 12f);

        private void GetMoonContainerSize(out float width, out float height)
        {
            var treeRt = transform as RectTransform;
            if (treeRt != null && treeRt.rect.width > 8f)
                width = treeRt.rect.width;
            else if (_station != null && Application.isPlaying)
                width = _station.GetShipTreeLayoutBasisWidthPublic();
            else if (centerRow != null && centerRow.rect.width > 8f)
                width = centerRow.rect.width;
            else
            {
                float refNodeW = nodePrefab != null ? nodePrefab.LayoutWidth : 120f;
                width = refNodeW * 7f + MoonLevelColGap * 6f + CanvasInnerMargin * 2f + 24f;
            }

            height = GetMoonRowAvailableHeight();
            width = Mathf.Max(160f, width);
        }

        private void PrepareHorizontalContainerLayout()
        {
            SyncTreeRootLayout();
            var treeRt = transform as RectTransform;
            if (treeRt == null)
                return;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(treeRt);
        }

        private void SyncTreeRootLayout()
        {
            var treeRt = transform as RectTransform;
            if (treeRt == null || centerRow == null)
                return;

            var treeVlg = treeRt.GetComponent<VerticalLayoutGroup>();
            if (treeVlg != null)
            {
                treeVlg.childControlHeight = true;
                treeVlg.childForceExpandHeight = true;
            }

            var rowLe = centerRow.GetComponent<LayoutElement>();
            if (rowLe != null)
            {
                rowLe.flexibleHeight = 1f;
                rowLe.flexibleWidth = 1f;
                rowLe.minHeight = 120f;
            }
        }

        /// <summary>Vertical space for the tree row from the parent panel — not from centerRow (which we resize).</summary>
        private float GetMoonRowAvailableHeight()
        {
            var treeRt = transform as RectTransform;
            if (treeRt == null || treeRt.rect.height < 16f)
                return 420f;

            float h = treeRt.rect.height;
            if (hintText != null)
                h -= MoonChromeHeightHint;
            return Mathf.Max(160f, h - 12f);
        }

        private static float ComputeMaxColumnStackHeight(float nodeH)
        {
            int maxStack = 1;
            for (int level = 1; level <= 7; level++)
                maxStack = Mathf.Max(maxStack, UpgradeTree.GetShipCountForLevel(level));
            return maxStack * nodeH + (maxStack - 1) * MoonBranchGapY;
        }

        /// <summary>
        /// Derives uniform node size from available row width. Prefab layout size is reference aspect only.
        /// </summary>
        private void ComputeMoonHorizontalGeometry(out float nodeW, out float nodeH, out float canvasW, out float canvasH)
        {
            const int maxLevel = 7;
            float margin = CanvasInnerMargin;
            GetMoonContainerSize(out float containerW, out float containerH);

            float rowPad = 0f;
            if (centerRow != null)
            {
                var hlg = centerRow.GetComponent<HorizontalLayoutGroup>();
                if (hlg != null)
                    rowPad = hlg.padding.left + hlg.padding.right;
            }

            float availableW = Mathf.Max(200f, containerW - rowPad);
            float innerW = availableW - margin * 2f;
            nodeW = (innerW - (maxLevel - 1) * MoonLevelColGap) / maxLevel;
            nodeW = Mathf.Round(Mathf.Max(MoonMinNodeWidth, nodeW));
            canvasW = availableW;

            nodeH = nodePrefab != null ? nodePrefab.LayoutHeight : MoonNodeHeight;
            nodeH = Mathf.Max(MoonNodeHeight, nodeH);

            float maxColStackH = ComputeMaxColumnStackHeight(nodeH);
            canvasH = margin * 2f + maxColStackH;

            float maxCanvasH = Mathf.Max(160f, containerH - 4f);
            if (canvasH > maxCanvasH)
            {
                float stackScale = (maxCanvasH - margin * 2f) / maxColStackH;
                nodeH = Mathf.Max(72f, Mathf.Round(nodeH * stackScale));
                maxColStackH = ComputeMaxColumnStackHeight(nodeH);
                canvasH = margin * 2f + maxColStackH;
            }
            else if (canvasH < maxCanvasH - 8f)
            {
                float targetStackH = maxCanvasH - margin * 2f;
                nodeH = Mathf.Max(72f, Mathf.Round(nodeH * (targetStackH / maxColStackH)));
                maxColStackH = ComputeMaxColumnStackHeight(nodeH);
                canvasH = margin * 2f + maxColStackH;
            }
        }

        private void ApplyHorizontalTreeCanvasLayout(float canvasW, float canvasH)
        {
            float maxRowH = GetMoonRowAvailableHeight();
            canvasH = Mathf.Min(canvasH, maxRowH);

            nodesCanvas.anchorMin = new Vector2(0.5f, 0.5f);
            nodesCanvas.anchorMax = new Vector2(0.5f, 0.5f);
            nodesCanvas.pivot = new Vector2(0.5f, 0.5f);
            nodesCanvas.sizeDelta = new Vector2(canvasW, canvasH);
            nodesCanvas.anchoredPosition = Vector2.zero;

            var canvasLe = nodesCanvas.GetComponent<LayoutElement>();
            if (canvasLe != null)
            {
                canvasLe.preferredWidth = canvasW;
                canvasLe.minWidth = canvasW;
                canvasLe.preferredHeight = canvasH;
                canvasLe.minHeight = canvasH;
                canvasLe.flexibleWidth = 0f;
            }

            if (centerRow != null)
            {
                centerRow.anchorMin = Vector2.zero;
                centerRow.anchorMax = Vector2.one;
                centerRow.offsetMin = Vector2.zero;
                centerRow.offsetMax = Vector2.zero;
                centerRow.pivot = new Vector2(0.5f, 0.5f);
                var hlg = centerRow.GetComponent<HorizontalLayoutGroup>();
                if (hlg != null)
                {
                    hlg.childAlignment = TextAnchor.MiddleCenter;
                    hlg.childForceExpandWidth = true;
                    hlg.childControlWidth = true;
                }
            }

            ApplyCenterRowHeight(maxRowH);
        }

        /// <summary>Edge midpoint in <paramref name="rt"/> parent local space (nodes canvas).</summary>
        private static Vector2 GetRectEdgeMidpoint(RectTransform rt, bool rightEdge, bool verticalOut = false, bool verticalIn = false)
        {
            rt.GetLocalCorners(ConnectorCornerBuffer);
            if (verticalOut)
                return new Vector2((ConnectorCornerBuffer[0].x + ConnectorCornerBuffer[3].x) * 0.5f, ConnectorCornerBuffer[0].y);
            if (verticalIn)
                return new Vector2((ConnectorCornerBuffer[1].x + ConnectorCornerBuffer[2].x) * 0.5f, ConnectorCornerBuffer[1].y);
            if (rightEdge)
                return new Vector2(ConnectorCornerBuffer[2].x, (ConnectorCornerBuffer[2].y + ConnectorCornerBuffer[3].y) * 0.5f);
            return new Vector2(ConnectorCornerBuffer[1].x, (ConnectorCornerBuffer[1].y + ConnectorCornerBuffer[0].y) * 0.5f);
        }

        private void DrawConnector(Vector2 from, Vector2 to, Color color, float thickness)
        {
            var go = new GameObject("ShipTreeConnector");
            go.transform.SetParent(nodesCanvas, false);
            var rect = go.AddComponent<RectTransform>();
            Vector2 delta = to - from;
            rect.sizeDelta = new Vector2(delta.magnitude, Mathf.Max(1.5f, thickness));
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = from;
            rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            go.transform.SetAsFirstSibling();
            _visuals.Add(go);
        }

        private void SetUnavailable()
        {
            if (hintText != null)
                hintText.text = "Upgrade tree unavailable.";
        }

        private void ApplyCenterRowHeight(float h)
        {
            if (centerRow == null) return;
            h = GetMoonRowAvailableHeight();
            centerRow.sizeDelta = new Vector2(0f, h);
            var rowLe = centerRow.GetComponent<LayoutElement>();
            if (rowLe != null)
            {
                rowLe.preferredHeight = h;
                rowLe.minHeight = 120f;
                rowLe.flexibleHeight = 1f;
            }
        }

        private float ComputeMaxDisplayPower()
        {
            float max = 0f;
            for (int level = 1; level <= 7; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                for (int b = 0; b < count; b++)
                {
                    float t = _station.GetPowerBreakdownForTreeNode(level, b).GetDisplayTotalForUi();
                    if (t > max) max = t;
                }
            }

            return Mathf.Max(max, 0.001f);
        }
    }
}
