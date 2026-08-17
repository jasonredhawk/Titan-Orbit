using System.Collections.Generic;
using TitanOrbit.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TitanOrbit.UI
{
    /// <summary>
    /// Prefab-driven ship upgrade tree panel (hint line, node canvas, connectors). Assigned on a GameObject
    /// under the orbit station ships tab; <see cref="OrbitStationUI"/> binds runtime state via <see cref="IOrbitStationHost"/>.
    /// Supports horizontal moon-dock layout and vertical fallback. Optional <see cref="previewFamily"/> fills editor preview.
    /// </summary>
    public class ShipUpgradeTreeUI : MonoBehaviour
    {
        public const string PanelTitleText = "Ship Upgrade Tree";
        public const string PanelDefaultSubtitle = "Green: your path. Cyan: available next hulls. Dim: other routes. Your ship is in the left panel.";

        private const float CanvasInnerMargin = 8f;
        private const float MoonNodeHeight = 100f;
        private const float MoonMinNodeWidth = 74f;
        private const float MoonMegaScale = 1.5f;
        private const float MoonLevelColGap = 20f;
        private const float MoonBranchGapY = 8f;
        private const float LayoutWidthBucketPixels = 32f;
        private const float MoonChromeHeightHint = 28f;
        private const float VerticalNodeHeight = 188f;
        private const float VerticalLevelSpacing = VerticalNodeHeight + 44f;
        private const float VerticalColGap = 6f;
        private static readonly Color ConnectorDim = new Color(0.28f, 0.42f, 0.62f, 0.40f);
        private static readonly Color ConnectorAvailable = new Color(0.32f, 0.92f, 1f, 0.94f);
        private static readonly Color ConnectorPath = new Color(0.35f, 0.98f, 0.62f, 0.95f);
        private static readonly Vector3[] ConnectorCornerBuffer = new Vector3[4];
        private readonly List<int> _connectorTargets = new List<int>(2);

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
        public ShipUpgradeTreeNodeUI NodePrefab => nodePrefab;
        public Sprite NodeBackgroundSprite => nodeBackgroundSprite;

        private IOrbitStationHost _station;
        private readonly List<ShipUpgradeTreeNodeUI> _nodes = new List<ShipUpgradeTreeNodeUI>();
        private readonly List<GameObject> _visuals = new List<GameObject>();
        private readonly List<int> _nextTargets = new List<int>(4);
        private string _structureKey = string.Empty;
        private ShipUpgradeTreeNodeUI _currentShipNode;

        public TextMeshProUGUI Title => titleText;
        public TextMeshProUGUI Hint => hintText;
        public RectTransform CenterRow => centerRow;
        public RectTransform NodesCanvas => nodesCanvas;
        public IReadOnlyList<ShipUpgradeTreeNodeUI> Nodes => _nodes;
        public ShipUpgradeTreeNodeUI CurrentShipDisplayNode => _currentShipNode;

        private void Awake()
        {
            EnsurePanelHeader();
        }

        /// <summary>Stores orbit station host and ensures title/hint header exists on older prefabs.</summary>
        public void BindStation(IOrbitStationHost station)
        {
            _station = station;
            EnsurePanelHeader();
        }

        /// <summary>Creates a title row on older prefabs that only had a dynamic hint line.</summary>
        public void EnsurePanelHeader()
        {
            // --- Ensure setup ---
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

        /// <summary>
        /// Rebuilds node layout when structure or container width changes; otherwise only refreshes colors/state.
        /// </summary>
        public void RebuildIfNeeded(bool moonHorizontal, string structureKey)
        {
            // --- Rebuild cache ---
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
            // --- Clear state ---
            DestroyCurrentShipDisplayNode();
            ClearNodesCanvasChildren();
            _visuals.Clear();
            _nodes.Clear();
        }

        private void DestroyCurrentShipDisplayNode()
        {
            // --- DestroyCurrentShipDisplayNode ---
            if (_currentShipNode == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(_currentShipNode.gameObject);
            else
#endif
                Destroy(_currentShipNode.gameObject);
            _currentShipNode = null;
        }

        private bool HasOrphanNodesCanvasChildren()
        {
            // --- HasOrphanNodesCanvasChildren ---
            if (nodesCanvas == null)
                return false;
            return nodesCanvas.childCount > _nodes.Count;
        }

        /// <summary>Removes all dynamic/baked children under the nodes canvas (fixes duplicate overlapping nodes).</summary>
        private void ClearNodesCanvasChildren()
        {
            // --- Clear state ---
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

        /// <summary>Updates node colors, prices, and power bars without destroying/recreating widgets.</summary>
        public void RefreshVisualState()
        {
            // --- RefreshVisualState ---
            if (_station == null)
                return;
            if (_nodes.Count == 0 && _currentShipNode == null)
                return;

            ShipPowerBarStatMaxes maxes = ComputePowerBarStatMaxes();
            _station.RefreshShipUpgradeTreeNodeStates(_nodes, maxes);
        }

        /// <summary>Editor: populate all tier slots and connectors. Uses <paramref name="family"/> when assigned.</summary>
        public void EditorPreviewFromFamily(ShipFamilyDefinition family)
        {
            // --- EditorPreviewFromFamily ---
            previewFamily = family;
            Clear();
            if (nodesCanvas == null || nodePrefab == null)
                return;

            ShipPowerBarStatMaxes maxes = ShipFamilyPowerBarNorm.GetGlobalMaxPerStat();

            const int maxLevel = 7;
            PrepareHorizontalContainerLayout();
            ComputeMoonHorizontalGeometry(out float nodeW, out float nodeH, out float canvasW, out float canvasH);
            ApplyHorizontalTreeCanvasLayout(canvasW, canvasH);
            float trackW = GetMoonPowerBarTrackWidth(nodeW);
            float megaW = Mathf.Round(nodeW * MoonMegaScale);
            float megaH = Mathf.Round(nodeH * MoonMegaScale);
            float megaTrackW = GetMoonPowerBarTrackWidth(megaW);

            var byLevel = new Dictionary<int, List<ShipUpgradeTreeNodeUI>>();
            int tierIndex = 0;
            for (int level = 1; level <= maxLevel; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                var views = new List<ShipUpgradeTreeNodeUI>(count);
                bool mega = level == 7;
                float useW = mega ? megaW : nodeW;
                float useH = mega ? megaH : nodeH;
                float useTrack = mega ? megaTrackW : trackW;
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
                    node.BindSlot(level, b, null, useW, useH, useTrack);
                    node.ConfigureLayout(true);
                    node.SetLevelLabel(level == 1 ? "Lv 1" : $"Lv {level}");
                    string shipName = tier != null
                        ? (string.IsNullOrEmpty(tier.upgradeTreeShipName) ? tier.chassisId : tier.upgradeTreeShipName)
                        : $"Branch {b + 1}";
                    node.SetShipName(shipName);
                    node.SetPrice(family != null ? "—" : "Preview");
                    node.SetPreview(tier != null ? tier.menuPreviewSprite : null);
                    if (tier != null)
                    {
                        ShipFamilyPowerScoreBreakdown breakdown = ShipFamilyPowerBarNorm.GetBreakdownAtShipLevel(
                            family, tier, level);
                        node.ApplyPowerBreakdown(breakdown, maxes);
                    }
                    else
                        node.ApplyPowerBreakdown(default, maxes);

                    GetMoonNodePosition(level, b, count, nodeW, nodeH, megaW, canvasW, canvasH,
                        ComputeMaxColumnStackHeight(nodeH), out colX, out nodeY);
                    if (mega)
                    {
                        GetMoonNodePosition(6, b * 2, 6, nodeW, nodeH, megaW, canvasW, canvasH,
                            ComputeMaxColumnStackHeight(nodeH), out _, out float y0);
                        GetMoonNodePosition(6, b * 2 + 1, 6, nodeW, nodeH, megaW, canvasW, canvasH,
                            ComputeMaxColumnStackHeight(nodeH), out _, out float y1);
                        nodeY = (y0 + y1) * 0.5f;
                    }
                    node.Rect.anchoredPosition = new Vector2(colX, nodeY);
                    views.Add(node);
                    _nodes.Add(node);
                    _visuals.Add(node.gameObject);
                }

                byLevel[level] = views;
            }

            ForceLayoutBeforeConnectors();
            EnforceUniformNodeSizesExceptMega(nodeW, nodeH, trackW, megaW, megaH, megaTrackW);
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
            // --- InstantiateNodeForPreview ---
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
            // --- Build data ---
            UpgradeTree tree = _station != null ? _station.UpgradeTree : null;
            if (tree == null || _station == null || !_station.IsTreeDataAvailable())
            {
                SetUnavailable();
                return;
            }

            const int maxLevel = 7;
            ComputeMoonHorizontalGeometry(out float nodeW, out float nodeH, out float canvasW, out float canvasH);
            ApplyHorizontalTreeCanvasLayout(canvasW, canvasH);
            float trackW = GetMoonPowerBarTrackWidth(nodeW);

            _station.TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> pathEdges);
            var byLevel = new Dictionary<int, List<ShipUpgradeTreeNodeUI>>();

            float megaW = Mathf.Round(nodeW * MoonMegaScale);
            float megaH = Mathf.Round(nodeH * MoonMegaScale);
            float megaTrackW = GetMoonPowerBarTrackWidth(megaW);

            for (int level = 1; level <= maxLevel; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                var views = new List<ShipUpgradeTreeNodeUI>(count);
                bool mega = level == 7;
                float useW = mega ? megaW : nodeW;
                float useH = mega ? megaH : nodeH;
                float useTrack = mega ? megaTrackW : trackW;
                float colX = 0f;
                float nodeY = 0f;
                for (int b = 0; b < count; b++)
                {
                    ShipUpgradeNode upgradeNode = level == 1 ? null : tree.GetNodeForBranch(level, b);
                    var view = SpawnNode(level, b, upgradeNode, useW, useH, useTrack);
                    view.ConfigureLayout(true);
                    GetMoonNodePosition(level, b, count, nodeW, nodeH, megaW, canvasW, canvasH,
                        ComputeMaxColumnStackHeight(nodeH), out colX, out nodeY);
                    if (mega)
                    {
                        // Align each mega to the midpoint of its L6 pair (1&2, 3&4, 5&6).
                        GetMoonNodePosition(6, b * 2, 6, nodeW, nodeH, megaW, canvasW, canvasH,
                            ComputeMaxColumnStackHeight(nodeH), out _, out float y0);
                        GetMoonNodePosition(6, b * 2 + 1, 6, nodeW, nodeH, megaW, canvasW, canvasH,
                            ComputeMaxColumnStackHeight(nodeH), out _, out float y1);
                        nodeY = (y0 + y1) * 0.5f;
                    }
                    view.Rect.anchoredPosition = new Vector2(colX, nodeY);
                    views.Add(view);
                }

                byLevel[level] = views;
            }

            ForceLayoutBeforeConnectors();
            EnforceUniformNodeSizesExceptMega(nodeW, nodeH, trackW, megaW, megaH, megaTrackW);
            DrawConnectors(byLevel, pathEdges, moonHorizontal: true);
        }

        private void BuildVertical()
        {
            // --- Build data ---
            UpgradeTree tree = _station != null ? _station.UpgradeTree : null;
            if (tree == null || _station == null || !_station.IsTreeDataAvailable())
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
            float megaW,
            float canvasW,
            float canvasH,
            float maxColStackH,
            out float x,
            out float y)
        {
            float halfW = canvasW * 0.5f;
            float halfH = canvasH * 0.5f;
            float margin = CanvasInnerMargin;
            float gapY = ComputeMoonBranchGapY(nodeH);
            float stackH = branchCount * nodeH + (branchCount - 1) * gapY;
            float stackTop = halfH - margin - (maxColStackH - stackH) * 0.5f;
            float stride = nodeW + MoonLevelColGap;
            if (level >= 7)
                x = Mathf.Round(-halfW + margin + 6f * stride + Mathf.Max(nodeW, megaW) * 0.5f);
            else
                x = Mathf.Round(-halfW + margin + nodeW * 0.5f + (level - 1) * stride);
            y = Mathf.Round(stackTop - nodeH * 0.5f - (branchCount - 1 - branch) * (nodeH + gapY));
        }

        private void ForceLayoutBeforeConnectors()
        {
            // --- ForceLayoutBeforeConnectors ---
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
            EnforceUniformNodeSizesExceptMega(nodeW, nodeH, trackW, nodeW, nodeH, trackW);
        }

        /// <summary>Keeps L1–L6 uniform; L7 MEGA nodes stay larger so the final hulls read as bosses.</summary>
        private void EnforceUniformNodeSizesExceptMega(
            float nodeW, float nodeH, float trackW,
            float megaW, float megaH, float megaTrackW)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                var node = _nodes[i];
                if (node == null)
                    continue;
                if (node.Level == 7)
                    node.EnforceLayoutSize(megaW, megaH, megaTrackW);
                else
                    node.EnforceLayoutSize(nodeW, nodeH, trackW);
            }
        }

        private ShipUpgradeTreeNodeUI SpawnNode(int level, int branch, ShipUpgradeNode node, float w, float h, float trackW)
        {
            // --- SpawnNode ---
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
            _station.PopulateTreeNode(view, ComputePowerBarStatMaxes());
            view.SetPriceClickHandler(() => _station.OnUpgradeTreeNodeClicked(view.Level, view.BranchIndex));
            _nodes.Add(view);
            _visuals.Add(view.gameObject);
            return view;
        }

        private void DrawConnectors(
            Dictionary<int, List<ShipUpgradeTreeNodeUI>> byLevel,
            HashSet<(int fL, int fB, int tL, int tB)> pathEdges,
            bool moonHorizontal)
        {
            int shipLevel = _station != null ? _station.ShipLevel : 0;
            int shipBranch = _station != null ? _station.BranchIndex : -1;

            for (int level = 2; level <= 7; level++)
            {
                if (!byLevel.TryGetValue(level, out var levelViews)) continue;
                if (!byLevel.TryGetValue(level - 1, out var prevViews)) continue;
                foreach (var prev in prevViews)
                {
                    if (prev == null) continue;
                    UpgradeTree.GetNextLevelBranchTargets(prev.Level, prev.BranchIndex, _connectorTargets);
                    bool fromCurrent = prev.Level == shipLevel && prev.BranchIndex == shipBranch;
                    for (int t = 0; t < _connectorTargets.Count; t++)
                    {
                        ShipUpgradeTreeNodeUI next = FindNodeByBranch(levelViews, _connectorTargets[t]);
                        if (next == null) continue;

                        bool onPath = pathEdges != null
                            && pathEdges.Contains((prev.Level, prev.BranchIndex, next.Level, next.BranchIndex));
                        Color color = onPath
                            ? ConnectorPath
                            : fromCurrent ? ConnectorAvailable : ConnectorDim;
                        float thickness = onPath ? 3.6f : fromCurrent ? 2.8f : 2f;

                        Vector2 from;
                        Vector2 to;
                        if (!moonHorizontal)
                        {
                            from = GetRectPointInCanvas(prev.Rect, 0.5f, 0f);
                            to = GetRectPointInCanvas(next.Rect, 0.5f, 1f);
                        }
                        else
                        {
                            from = GetRectPointInCanvas(prev.Rect, 1f, 0.5f);
                            to = GetRectPointInCanvas(next.Rect, 0f, 0.5f);
                        }

                        DrawConnector(from, to, color, thickness);
                    }
                }
            }
        }

        private static ShipUpgradeTreeNodeUI FindNodeByBranch(List<ShipUpgradeTreeNodeUI> views, int branch)
        {
            if (views == null) return null;
            for (int i = 0; i < views.Count; i++)
            {
                if (views[i] != null && views[i].BranchIndex == branch)
                    return views[i];
            }

            return null;
        }

        private static float GetMoonPowerBarTrackWidth(float nodeW) =>
            Mathf.Max(48f, nodeW - 12f);

        private void GetMoonContainerSize(out float width, out float height)
        {
            // --- Compute value ---
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
                width = refNodeW * (6f + MoonMegaScale) + MoonLevelColGap * 6f + CanvasInnerMargin * 2f + 24f;
            }

            height = GetMoonRowAvailableHeight();
            width = Mathf.Max(160f, width);
        }

        private void PrepareHorizontalContainerLayout()
        {
            // --- PrepareHorizontalContainerLayout ---
            SyncTreeRootLayout();
            var treeRt = transform as RectTransform;
            if (treeRt == null)
                return;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(treeRt);
        }

        private void SyncTreeRootLayout()
        {
            // --- SyncTreeRootLayout ---
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

        /// <summary>Vertical space for the tree row from the parent panel ΓÇö not from centerRow (which we resize).</summary>
        private float GetMoonRowAvailableHeight()
        {
            // --- Compute value ---
            var treeRt = transform as RectTransform;
            if (treeRt == null || treeRt.rect.height < 16f)
                return 420f;

            float h = treeRt.rect.height;
            if (hintText != null)
                h -= MoonChromeHeightHint;
            return Mathf.Max(160f, h - 12f);
        }

        /// <summary>
        /// Vertical gap so a 1.25× mega centered on an L6 pair does not overlap those cards.
        /// </summary>
        private static float ComputeMoonBranchGapY(float nodeH) =>
            Mathf.Max(MoonBranchGapY, Mathf.Round(nodeH * (MoonMegaScale - 1f) + 12f));

        private static float ComputeMaxColumnStackHeight(float nodeH)
        {
            // --- Compute value ---
            int maxStack = 1;
            for (int level = 1; level <= 7; level++)
                maxStack = Mathf.Max(maxStack, UpgradeTree.GetShipCountForLevel(level));
            float gapY = ComputeMoonBranchGapY(nodeH);
            return maxStack * nodeH + (maxStack - 1) * gapY;
        }

        /// <summary>
        /// Derives uniform node size from available row width. Prefab layout size is reference aspect only.
        /// </summary>
        private void ComputeMoonHorizontalGeometry(out float nodeW, out float nodeH, out float canvasW, out float canvasH)
        {
            // --- Compute value ---
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
            // Six regular columns plus a wider mega column, with a gap after each of the first six.
            nodeW = (innerW - 6f * MoonLevelColGap) / (6f + MoonMegaScale);
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
                // --- if ---
                float targetStackH = maxCanvasH - margin * 2f;
                nodeH = Mathf.Max(72f, Mathf.Round(nodeH * (targetStackH / maxColStackH)));
                maxColStackH = ComputeMaxColumnStackHeight(nodeH);
                canvasH = margin * 2f + maxColStackH;
            }
        }

        private void ApplyHorizontalTreeCanvasLayout(float canvasW, float canvasH)
        {
            // --- Apply changes ---
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

        /// <summary>
        /// Point on a node in <see cref="nodesCanvas"/> local space.
        /// <paramref name="nx"/> / <paramref name="ny"/> are 0–1 from bottom-left (1,1 = top-right).
        /// </summary>
        private Vector2 GetRectPointInCanvas(RectTransform rt, float nx, float ny)
        {
            if (rt == null || nodesCanvas == null)
                return Vector2.zero;

            rt.GetWorldCorners(ConnectorCornerBuffer);
            Vector3 bl = ConnectorCornerBuffer[0];
            Vector3 tl = ConnectorCornerBuffer[1];
            Vector3 tr = ConnectorCornerBuffer[2];
            Vector3 br = ConnectorCornerBuffer[3];
            Vector3 bottom = Vector3.Lerp(bl, br, nx);
            Vector3 top = Vector3.Lerp(tl, tr, nx);
            Vector3 world = Vector3.Lerp(bottom, top, ny);
            return nodesCanvas.InverseTransformPoint(world);
        }

        private void DrawConnector(Vector2 from, Vector2 to, Color color, float thickness)
        {
            // --- DrawConnector ---
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
            // --- Apply changes ---
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

        /// <summary>
        /// Ten global catalog maxes for equal-slot bars (all families, each chassis at its tree level).
        /// </summary>
        ShipPowerBarStatMaxes ComputePowerBarStatMaxes()
        {
            var config = TitanOrbit.ECS.ShipStatApplyLogic.Config;
            return config != null
                ? config.GetGlobalPowerBarStatMaxes()
                : ShipFamilyPowerBarNorm.GetGlobalMaxPerStat();
        }

        public ShipPowerBarStatMaxes GetPowerBarStatMaxes() => ComputePowerBarStatMaxes();
    }
}
