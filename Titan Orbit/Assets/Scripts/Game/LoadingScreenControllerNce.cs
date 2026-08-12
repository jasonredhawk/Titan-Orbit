using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen join overlay shown while the map builds on the client.
    /// Content sits in a tight vertical cluster in the middle of the screen (not pinned to
    /// the top/bottom edges): HOW TO PLAY → five instruction cards → BUILDING GALAXY →
    /// two compact progress bars (counts + % drawn inside each bar).
    /// <para>
    /// Bar 1 — <see cref="EcsGameBridge.TryGetNetworkJoinLoadProgress"/>: seed-hydrate ECS
    /// bodies + short InGame ghost catch-up.
    /// Bar 2 — <see cref="EcsGameBridge.TryGetProxyJoinLoadProgress"/>: hybrid planet/asteroid
    /// GameObject Instantiates vs server meta N. Join Team stays hidden until both complete.
    /// </para>
    /// <para>
    /// The instruction strip matches the five-step guide from <c>InstructionScreenUI</c>.
    /// Art loads from <c>Resources/InstructionScreens/</c>. Does not gather asteroid entities
    /// (Crash!!! on Windows).
    /// </para>
    /// </summary>
    public class LoadingScreenControllerNce : MonoBehaviour
    {
        // --- Progress bar chrome ---
        // [TITAN-ORBIT] Taller track so count + % fit inside; no separate status/percent rows.
        const float BarPadding = 2f;
        const float ProgressBarWidth = 560f;
        const float ProgressBarHeight = 28f;
        const float BarLabelHeight = 18f;
        const float BarStackGap = 8f;
        const float BarLabelToTrackGap = 3f;

        // --- Centered content cluster ---
        // [TITAN-ORBIT] Pack labels + cards + bar toward screen center so large monitors
        // do not leave a huge empty gap between a top title and a bottom progress bar.
        // Wide strip so each instruction image is physically larger (width × height), not
        // just a taller empty frame around the same sprite.
        const float ContentMaxWidth = 1770f; // ~1.5× original 1180 — bigger cards end-to-end
        const float ContentWidthScreenFraction = 0.94f;
        const float ContentSidePadding = 24f;
        const float SectionGap = 14f;
        const float HowToHeight = 36f;
        const float TitleHeight = 40f;

        // --- Instruction strip (matches InstructionScreenUI topics) ---
        const int StepCount = 5;
        const float ColumnGap = 10f;
        const float CardInnerPadding = 8f;
        // [TITAN-ORBIT] Instruction PNGs are 1536×1024 (3:2 → height/width = 2/3).
        // Using 4:3 (0.75) made the frame taller than the art → black bars top/bottom.
        const float ImageAspect = 2f / 3f;
        /// <summary>Cap card height so the cluster stays compact on tall monitors.</summary>
        const float MaxCardHeight = 560f;

        /// <summary>One how-to-play step: short title, body copy, and Resources sprite path.</summary>
        struct InstructionStep
        {
            public string Title;
            public string Body;
            public string SpriteResourcePath;

            public InstructionStep(string title, string body, string spriteResourcePath)
            {
                Title = title;
                Body = body;
                SpriteResourcePath = spriteResourcePath;
            }
        }

        /// <summary>Runtime refs for one vertical card in the horizontal strip.</summary>
        sealed class StepColumn
        {
            public RectTransform ColumnRoot;
            public RectTransform AccentBar;
            public TextMeshProUGUI Title;
            public RectTransform ImageFrame;
            public Image Illustration;
            public TextMeshProUGUI Body;
        }

        /// <summary>
        /// Five quick steps — objective, transport, mining, upgrades, planet ships.
        /// Same copy / art paths as <c>TitanOrbit.UI.InstructionScreenUI</c>.
        /// </summary>
        static readonly InstructionStep[] Steps =
        {
            new InstructionStep(
                "Capture All Planets",
                "Win by controlling every planet. Move population between worlds to grow your empire.",
                "InstructionScreens/instruction_objective"),
            new InstructionStep(
                "Transport People",
                "Pick up people at friendly planets and deliver them to capture neutral or enemy worlds.",
                "InstructionScreens/instruction_transport"),
            new InstructionStep(
                "Mine Asteroids",
                "Fly into asteroid fields and mine them. Gems are currency for upgrades.",
                "InstructionScreens/instruction_mining"),
            new InstructionStep(
                "Upgrade",
                "Spend gems to upgrade your ship and level up planets for a stronger fleet.",
                "InstructionScreens/instruction_upgrades"),
            new InstructionStep(
                "Planet Ships",
                "Each planet sells unique ships. Visit new worlds to expand your fleet.",
                "InstructionScreens/instruction_planet_ships"),
        };

        /// <summary>Accent stripe colors per card (left → right).</summary>
        static readonly Color[] AccentColors =
        {
            new Color(0.28f, 0.62f, 0.98f, 1f),
            new Color(0.34f, 0.78f, 0.62f, 1f),
            new Color(0.95f, 0.72f, 0.28f, 1f),
            new Color(0.78f, 0.42f, 0.95f, 1f),
            new Color(0.98f, 0.48f, 0.38f, 1f),
        };

        /// <summary>
        /// One labeled progress bar: short title above, fill track with in-bar overlay text
        /// (count + percent). Compact — no separate status/percent rows under the track.
        /// </summary>
        sealed class ProgressBarUi
        {
            public TextMeshProUGUI Label;
            public RectTransform Track;
            public Image Fill;
            /// <summary>Centered overlay on the track: e.g. "120 / 678   45%".</summary>
            public TextMeshProUGUI Overlay;
        }

        RectTransform _panelRoot;
        RectTransform _contentRoot;
        ProgressBarUi _networkBar;
        ProgressBarUi _proxyBar;
        TextMeshProUGUI _howToText;
        TextMeshProUGUI _titleText;
        RectTransform _instructionsRow;
        readonly List<StepColumn> _columns = new List<StepColumn>();
        readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        bool _uiBuilt;
        static Sprite _whiteSprite;

        /// <summary>1×1 white sprite used for solid UI fills (bar track / fill).</summary>
        static Sprite WhiteSprite
        {
            get
            {
                if (_whiteSprite == null)
                {
                    _whiteSprite = Sprite.Create(
                        Texture2D.whiteTexture,
                        new Rect(0f, 0f, 4f, 4f),
                        new Vector2(0.5f, 0.5f),
                        100f);
                }

                return _whiteSprite;
            }
        }

        /// <summary>True when the loading panel GameObject is active.</summary>
        public bool IsVisible => _panelRoot != null && _panelRoot.gameObject.activeSelf;

        /// <summary>[UNITY] Build UI once, start hidden.</summary>
        void Awake()
        {
            BuildUi();
            Hide();
        }

        /// <summary>
        /// Seconds without proxy-ready before appending a stuck hint to the status line.
        /// </summary>
        const float StuckHintAfterSeconds = 8f;

        /// <summary>Realtime when the bar last looked healthy (or when shown).</summary>
        float _stuckWatchSince = -1f;

        /// <summary>
        /// Per-frame: fill both bars from network/hydrate + hybrid GO proxy progress.
        /// Count and percent are drawn inside each taller track (compact layout).
        /// </summary>
        void Update()
        {
            if (!IsVisible)
                return;

            // --- Bar 1: ECS seed hydrate + InGame catch-up ---
            float networkProgress = 0f;
            EcsGameBridge.TryGetNetworkJoinLoadProgress(out networkProgress);
            string netCounts =
                EcsGameBridge.TryGetNetworkJoinLoadStepCounts(out int netDone, out int netTotal) &&
                netTotal > 0
                    ? netDone + " / " + netTotal
                    : string.Empty;
            ApplyBar(_networkBar, networkProgress, netCounts, stuckHint: null);

            // --- Bar 2: hybrid planet/asteroid GameObject Instantiates ---
            float proxyProgress = 0f;
            EcsGameBridge.TryGetProxyJoinLoadProgress(out proxyProgress);
            string goCounts =
                EcsGameBridge.TryGetProxyJoinLoadStepCounts(out int goDone, out int goTotal) &&
                goTotal > 0
                    ? goDone + " / " + goTotal
                    : string.Empty;

            // --- Stuck hint after a few seconds (Settling vs Instantiates vs drain) ---
            // [TITAN-ORBIT] 314/315 with Settling ON looked like a hang with no explanation.
            string mapHint = null;
            bool proxyReady = EcsGameBridge.IsMapProxyCountReady(out _, out _, out _);
            if (proxyReady || proxyProgress >= 0.99f)
            {
                _stuckWatchSince = -1f;
            }
            else
            {
                if (_stuckWatchSince < 0f)
                    _stuckWatchSince = Time.realtimeSinceStartup;

                if (Time.realtimeSinceStartup - _stuckWatchSince >= StuckHintAfterSeconds)
                    mapHint = EcsGameBridge.GetMapLoadStuckHint();
            }

            ApplyBar(_proxyBar, proxyProgress, goCounts, mapHint);
        }

        /// <summary>
        /// [UNITY] Canvas / resolution change — reflow the centered cluster and equal-width cards.
        /// </summary>
        void OnRectTransformDimensionsChange()
        {
            if (_uiBuilt && IsVisible)
                LayoutContent();
        }

        /// <summary>Shows the overlay, resets fill, and lays out the centered content cluster.</summary>
        public void Show()
        {
            BuildUi();
            ApplyBar(_networkBar, 0f, counts: string.Empty, stuckHint: null);
            ApplyBar(_proxyBar, 0f, counts: string.Empty, stuckHint: null);
            _stuckWatchSince = Time.realtimeSinceStartup;
            if (_panelRoot != null)
            {
                _panelRoot.SetAsLastSibling();
                _panelRoot.gameObject.SetActive(true);
            }

            // --- Instruction art + layout ---
            // Cards need an active frame so rect sizes are valid before we measure columns.
            PreloadInstructionSprites();
            ApplyInstructionSprites();
            LayoutContent();
        }

        /// <summary>Hides the overlay without destroying it.</summary>
        public void Hide()
        {
            if (_panelRoot != null)
                _panelRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Updates fill amount and the in-bar overlay text (count + percent, optional stuck hint).
        /// </summary>
        /// <param name="bar">World or Map bar UI.</param>
        /// <param name="progress">0–1 fill.</param>
        /// <param name="counts">e.g. "120 / 678", or empty while waiting for meta.</param>
        /// <param name="stuckHint">Optional short hint appended when load looks stuck.</param>
        static void ApplyBar(ProgressBarUi bar, float progress, string counts, string stuckHint)
        {
            if (bar == null)
                return;

            progress = Mathf.Clamp01(progress);

            if (bar.Fill != null)
                bar.Fill.fillAmount = progress;

            if (bar.Overlay == null)
                return;

            // --- In-bar text: "120 / 678   45%" ---
            string percent = Mathf.RoundToInt(progress * 100f) + "%";
            string overlay;
            if (!string.IsNullOrEmpty(counts))
                overlay = counts + "   " + percent;
            else
                overlay = percent;

            if (!string.IsNullOrEmpty(stuckHint))
                overlay += "  —  " + stuckHint;

            bar.Overlay.text = overlay;
        }

        /// <summary>
        /// [UNITY] Builds the full-screen panel once under the parent Canvas:
        /// full-screen backdrop + centered content cluster
        /// (HOW TO PLAY → cards → BUILDING GALAXY → sync bar → visuals bar).
        /// </summary>
        void BuildUi()
        {
            if (_uiBuilt)
                return;

            var canvasRect = GetComponentInParent<Canvas>()?.GetComponent<RectTransform>();
            if (canvasRect == null)
                return;

            // --- Destroy stale panel from a previous Play Mode without Domain Reload ---
            var existingPanel = canvasRect.Find("LoadingPanel");
            if (existingPanel != null)
                Destroy(existingPanel.gameObject);

            _panelRoot = CreateRect("LoadingPanel", canvasRect);
            StretchFill(_panelRoot);

            // [TITAN-ORBIT] Slightly transparent so players can watch asteroids/planets Instantiates
            // during join. Join Game is dismissed before this shows — so the lobby list does not
            // bleed through (see JoinGameBrowserController.DismissForLoading).
            var backdrop = _panelRoot.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0.02f, 0.04f, 0.1f, 0.97f);
            backdrop.raycastTarget = true;

            // --- Centered content cluster (sized/positioned in LayoutContent) ---
            _contentRoot = CreateRect("Content", _panelRoot);
            _contentRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _contentRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _contentRoot.pivot = new Vector2(0.5f, 0.5f);
            _contentRoot.anchoredPosition = Vector2.zero;
            _contentRoot.sizeDelta = new Vector2(ContentMaxWidth, 600f);

            // HOW TO PLAY — top of the cluster
            _howToText = CreateText(_contentRoot, "HowToPlay", "HOW TO PLAY", 28, FontStyles.Bold);
            _howToText.alignment = TextAlignmentOptions.Center;
            _howToText.color = new Color(0.85f, 0.92f, 1f, 1f);

            // Five instruction cards
            _instructionsRow = CreateRect("InstructionsRow", _contentRoot);
            _columns.Clear();
            for (int i = 0; i < StepCount; i++)
                _columns.Add(CreateInstructionColumn(_instructionsRow, Steps[i], i));

            // BUILDING GALAXY — under the cards
            _titleText = CreateText(_contentRoot, "Title", "BUILDING GALAXY", 30, FontStyles.Bold);
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(0.85f, 0.92f, 1f, 1f);

            // --- Two bars: world sync (ECS) then map visuals (GameObjects) ---
            _networkBar = CreateProgressBar(
                _contentRoot,
                "NetworkBar",
                "World",
                new Color(0.28f, 0.62f, 0.98f, 1f));
            _proxyBar = CreateProgressBar(
                _contentRoot,
                "ProxyBar",
                "Map",
                new Color(0.34f, 0.78f, 0.62f, 1f));

            _uiBuilt = true;
        }

        /// <summary>
        /// Builds one compact progress stack: short label above a taller track with in-bar overlay.
        /// </summary>
        ProgressBarUi CreateProgressBar(
            RectTransform parent,
            string rootName,
            string label,
            Color fillColor)
        {
            var labelTmp = CreateText(parent, rootName + "Label", label, 14, FontStyles.Bold);
            labelTmp.alignment = TextAlignmentOptions.Center;
            labelTmp.color = new Color(0.78f, 0.88f, 1f, 0.95f);

            var track = CreateRect(rootName + "Track", parent);
            var trackImage = track.gameObject.AddComponent<Image>();
            trackImage.sprite = WhiteSprite;
            trackImage.color = new Color(0.08f, 0.12f, 0.2f, 0.95f);
            trackImage.raycastTarget = false;

            var fillRect = CreateRect("Fill", track);
            StretchFill(fillRect, BarPadding, BarPadding, BarPadding, BarPadding);
            var fill = fillRect.gameObject.AddComponent<Image>();
            fill.sprite = WhiteSprite;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            fill.color = fillColor;
            fill.raycastTarget = false;

            // --- Overlay text sits on top of fill (sibling after Fill) ---
            // Dark outline keeps "120 / 678   45%" readable on both empty track and filled color.
            var overlay = CreateText(track, "Overlay", "0%", 15, FontStyles.Bold);
            StretchFill(overlay.rectTransform, 8f, 8f, 0f, 0f);
            overlay.alignment = TextAlignmentOptions.Center;
            overlay.verticalAlignment = VerticalAlignmentOptions.Middle;
            overlay.color = new Color(0.96f, 0.98f, 1f, 1f);
            overlay.raycastTarget = false;
            overlay.outlineWidth = 0.2f;
            overlay.outlineColor = new Color32(8, 12, 22, 220);

            return new ProgressBarUi
            {
                Label = labelTmp,
                Track = track,
                Fill = fill,
                Overlay = overlay,
            };
        }

        /// <summary>
        /// Builds one vertical card: accent → title → image frame → body.
        /// Positioned later by <see cref="LayoutContent"/>.
        /// </summary>
        StepColumn CreateInstructionColumn(RectTransform row, InstructionStep step, int index)
        {
            var columnRoot = CreateRect("Column_" + (index + 1), row);
            var cardBg = columnRoot.gameObject.AddComponent<Image>();
            cardBg.color = new Color(0.07f, 0.1f, 0.18f, 0.94f);
            cardBg.raycastTarget = false;

            var accentBar = CreateRect("Accent", columnRoot);
            var accentImage = accentBar.gameObject.AddComponent<Image>();
            accentImage.color = AccentColors[index % AccentColors.Length];
            accentImage.raycastTarget = false;

            var title = CreateText(columnRoot, "Title", step.Title, 18, FontStyles.Bold);
            title.alignment = TextAlignmentOptions.Center;
            title.color = new Color(0.94f, 0.97f, 1f, 1f);

            var imageFrame = CreateRect("ImageFrame", columnRoot);
            // Transparent — never show a dark letterbox behind preserveAspect.
            var frameBg = imageFrame.gameObject.AddComponent<Image>();
            frameBg.color = new Color(0f, 0f, 0f, 0f);
            frameBg.raycastTarget = false;

            // Illustration fills a frame sized to the sprite's real aspect (see LayoutContent).
            var illustrationGo = CreateRect("Illustration", imageFrame);
            StretchFill(illustrationGo, 0f, 0f, 0f, 0f);
            var illustration = illustrationGo.gameObject.AddComponent<Image>();
            illustration.preserveAspect = true;
            illustration.type = Image.Type.Simple;
            illustration.color = new Color(0.18f, 0.22f, 0.32f, 1f);
            illustration.raycastTarget = false;

            var body = CreateText(columnRoot, "Body", step.Body, 14, FontStyles.Normal);
            body.alignment = TextAlignmentOptions.Top;
            body.color = new Color(0.7f, 0.8f, 0.92f, 0.98f);
            body.enableWordWrapping = true;
            body.overflowMode = TextOverflowModes.Ellipsis;
            body.lineSpacing = -4f;

            return new StepColumn
            {
                ColumnRoot = columnRoot,
                AccentBar = accentBar,
                Title = title,
                ImageFrame = imageFrame,
                Illustration = illustration,
                Body = body,
            };
        }

        /// <summary>
        /// Packs the content cluster toward screen center and lays out equal-width cards.
        /// Order: HOW TO PLAY → cards → BUILDING GALAXY → sync bar → visuals bar.
        /// </summary>
        void LayoutContent()
        {
            if (_contentRoot == null || _panelRoot == null || _columns.Count == 0)
                return;

            Canvas.ForceUpdateCanvases();

            // --- Content width from panel, capped ---
            float panelWidth = _panelRoot.rect.width;
            float panelHeight = _panelRoot.rect.height;
            if (panelWidth < 10f || panelHeight < 10f)
                return;

            float contentWidth = Mathf.Min(
                ContentMaxWidth,
                panelWidth * ContentWidthScreenFraction,
                panelWidth - ContentSidePadding * 2f);
            contentWidth = Mathf.Max(contentWidth, 480f);

            float totalGaps = ColumnGap * (StepCount - 1);
            float columnWidth = Mathf.Max((contentWidth - totalGaps) / StepCount, 40f);

            // --- Measure cards first (drives cluster height) ---
            // Use a generous row budget so image height is not crushed; we then clamp card height.
            float measureBudget = MaxCardHeight;
            var metrics = new ColumnLayoutMetrics[_columns.Count];
            float maxCardHeight = 0f;
            for (int i = 0; i < _columns.Count; i++)
            {
                metrics[i] = ComputeColumnMetrics(_columns[i], columnWidth, measureBudget);
                maxCardHeight = Mathf.Max(maxCardHeight, metrics[i].TotalHeight);
            }

            float cardHeight = Mathf.Min(maxCardHeight, MaxCardHeight);

            // Compact stack: short label + taller in-bar text track.
            float oneBarStackHeight = BarLabelHeight + BarLabelToTrackGap + ProgressBarHeight;

            // --- Total cluster height (tight stack) ---
            float clusterHeight =
                HowToHeight
                + SectionGap
                + cardHeight
                + SectionGap
                + TitleHeight
                + 6f
                + oneBarStackHeight
                + BarStackGap
                + oneBarStackHeight;

            // Cap to ~78% of screen so it never feels edge-to-edge on short windows.
            float maxCluster = panelHeight * 0.78f;
            if (clusterHeight > maxCluster && cardHeight > 200f)
            {
                float shrink = clusterHeight - maxCluster;
                cardHeight = Mathf.Max(200f, cardHeight - shrink);
                clusterHeight =
                    HowToHeight
                    + SectionGap
                    + cardHeight
                    + SectionGap
                    + TitleHeight
                    + 6f
                    + oneBarStackHeight
                    + BarStackGap
                    + oneBarStackHeight;
            }

            _contentRoot.sizeDelta = new Vector2(contentWidth, clusterHeight);
            _contentRoot.anchoredPosition = Vector2.zero;

            // --- Stack from top of content (y decreases downward; pivot is center) ---
            float yFromTop = 0f;

            PlaceTopBand(_howToText.rectTransform, contentWidth, HowToHeight, ref yFromTop);
            yFromTop += SectionGap;

            // Instructions row
            _instructionsRow.anchorMin = new Vector2(0.5f, 1f);
            _instructionsRow.anchorMax = new Vector2(0.5f, 1f);
            _instructionsRow.pivot = new Vector2(0.5f, 1f);
            _instructionsRow.anchoredPosition = new Vector2(0f, -yFromTop);
            _instructionsRow.sizeDelta = new Vector2(contentWidth, cardHeight);

            for (int i = 0; i < _columns.Count; i++)
            {
                StepColumn col = _columns[i];
                float x = i * (columnWidth + ColumnGap);

                col.ColumnRoot.anchorMin = new Vector2(0f, 1f);
                col.ColumnRoot.anchorMax = new Vector2(0f, 1f);
                col.ColumnRoot.pivot = new Vector2(0f, 1f);
                col.ColumnRoot.anchoredPosition = new Vector2(x, 0f);
                col.ColumnRoot.sizeDelta = new Vector2(columnWidth, cardHeight);

                // Re-measure with the final card height budget so images fit.
                metrics[i] = ComputeColumnMetrics(col, columnWidth, cardHeight);
                ApplyColumnLayout(col, columnWidth, metrics[i]);
            }

            yFromTop += cardHeight + SectionGap;

            PlaceTopBand(_titleText.rectTransform, contentWidth, TitleHeight, ref yFromTop);
            yFromTop += 6f;

            PlaceProgressBarStack(_networkBar, contentWidth, ref yFromTop);
            yFromTop += BarStackGap;
            PlaceProgressBarStack(_proxyBar, contentWidth, ref yFromTop);
        }

        /// <summary>
        /// Places label + track (overlay is a child of the track) and advances
        /// <paramref name="yFromTop"/>.
        /// </summary>
        void PlaceProgressBarStack(ProgressBarUi bar, float contentWidth, ref float yFromTop)
        {
            if (bar == null)
                return;

            PlaceTopBand(bar.Label.rectTransform, contentWidth, BarLabelHeight, ref yFromTop);
            yFromTop += BarLabelToTrackGap;

            bar.Track.anchorMin = new Vector2(0.5f, 1f);
            bar.Track.anchorMax = new Vector2(0.5f, 1f);
            bar.Track.pivot = new Vector2(0.5f, 1f);
            bar.Track.anchoredPosition = new Vector2(0f, -yFromTop);
            bar.Track.sizeDelta = new Vector2(ProgressBarWidth, ProgressBarHeight);
            yFromTop += ProgressBarHeight;
        }

        /// <summary>
        /// Places a full-width band at the current top offset inside the content cluster,
        /// then advances <paramref name="yFromTop"/> by <paramref name="height"/>.
        /// </summary>
        static void PlaceTopBand(RectTransform rect, float contentWidth, float height, ref float yFromTop)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -yFromTop);
            rect.sizeDelta = new Vector2(contentWidth, height);
            yFromTop += height;
        }

        /// <summary>Preferred sizes for one column at the given width.</summary>
        struct ColumnLayoutMetrics
        {
            public float TitleFontSize;
            public float TitleHeight;
            public float ImageHeight;
            public float BodyFontSize;
            public float BodyHeight;
            public float TotalHeight;
        }

        /// <summary>Computes title / image / body heights for a column at <paramref name="columnWidth"/>.</summary>
        static ColumnLayoutMetrics ComputeColumnMetrics(StepColumn col, float columnWidth, float rowHeight)
        {
            const float accentHeight = 4f;
            const float titleGap = 8f;
            const float imageGap = 10f;

            float innerWidth = columnWidth - CardInnerPadding * 2f;
            float titleFontSize = Mathf.Clamp(columnWidth * 0.085f, 14f, 20f);
            float bodyFontSize = Mathf.Clamp(columnWidth * 0.068f, 12f, 16f);

            col.Title.fontSize = titleFontSize;
            col.Body.fontSize = bodyFontSize;

            float titleHeight = col.Title.GetPreferredValues(col.Title.text, innerWidth, 0f).y;
            float bodyHeight = col.Body.GetPreferredValues(col.Body.text, innerWidth, 0f).y;

            // Prefer the loaded sprite's aspect so the frame matches the art exactly.
            float aspect = ImageAspect;
            if (col.Illustration != null && col.Illustration.sprite != null)
            {
                Rect spriteRect = col.Illustration.sprite.rect;
                if (spriteRect.width > 1f)
                    aspect = spriteRect.height / spriteRect.width;
            }

            float imageHeight = innerWidth * aspect;
            // Image is the hero of each card — leave enough room for title + body under it.
            float maxImageHeight = rowHeight * 0.62f;
            imageHeight = Mathf.Min(imageHeight, maxImageHeight);

            float chrome =
                accentHeight
                + CardInnerPadding
                + titleHeight
                + titleGap
                + imageGap
                + bodyHeight
                + CardInnerPadding;

            // If the preferred stack is taller than the card budget, shrink the image first.
            if (chrome + imageHeight > rowHeight && rowHeight > chrome + 40f)
                imageHeight = rowHeight - chrome;

            float totalHeight = chrome + imageHeight;

            return new ColumnLayoutMetrics
            {
                TitleFontSize = titleFontSize,
                TitleHeight = titleHeight,
                ImageHeight = imageHeight,
                BodyFontSize = bodyFontSize,
                BodyHeight = bodyHeight,
                TotalHeight = totalHeight,
            };
        }

        /// <summary>Applies measured metrics to accent / title / image / body RectTransforms.</summary>
        static void ApplyColumnLayout(StepColumn col, float columnWidth, ColumnLayoutMetrics metrics)
        {
            const float accentHeight = 4f;
            const float titleGap = 8f;
            const float imageGap = 10f;

            float titleTop = CardInnerPadding;
            float imageTop = titleTop + metrics.TitleHeight + titleGap;
            float bodyTop = imageTop + metrics.ImageHeight + imageGap;

            // Accent stripe along the top edge of the card
            col.AccentBar.anchorMin = new Vector2(0f, 1f);
            col.AccentBar.anchorMax = new Vector2(1f, 1f);
            col.AccentBar.pivot = new Vector2(0.5f, 1f);
            col.AccentBar.anchoredPosition = Vector2.zero;
            col.AccentBar.sizeDelta = new Vector2(0f, accentHeight);

            col.Title.fontSize = metrics.TitleFontSize;
            col.Title.rectTransform.anchorMin = new Vector2(0f, 1f);
            col.Title.rectTransform.anchorMax = new Vector2(1f, 1f);
            col.Title.rectTransform.pivot = new Vector2(0.5f, 1f);
            col.Title.rectTransform.anchoredPosition = new Vector2(0f, -(titleTop + accentHeight));
            col.Title.rectTransform.sizeDelta = new Vector2(-CardInnerPadding * 2f, metrics.TitleHeight);

            col.ImageFrame.anchorMin = new Vector2(0f, 1f);
            col.ImageFrame.anchorMax = new Vector2(1f, 1f);
            col.ImageFrame.pivot = new Vector2(0.5f, 1f);
            col.ImageFrame.anchoredPosition = new Vector2(0f, -(imageTop + accentHeight));
            col.ImageFrame.sizeDelta = new Vector2(-CardInnerPadding * 2f, metrics.ImageHeight);

            col.Body.fontSize = metrics.BodyFontSize;
            col.Body.rectTransform.anchorMin = new Vector2(0f, 1f);
            col.Body.rectTransform.anchorMax = new Vector2(1f, 1f);
            col.Body.rectTransform.pivot = new Vector2(0.5f, 1f);
            col.Body.rectTransform.anchoredPosition = new Vector2(0f, -(bodyTop + accentHeight));
            col.Body.rectTransform.sizeDelta = new Vector2(-CardInnerPadding * 2f, metrics.BodyHeight);
        }

        /// <summary>Warm the sprite cache for all five instruction Resources paths.</summary>
        void PreloadInstructionSprites()
        {
            for (int i = 0; i < Steps.Length; i++)
                LoadSprite(Steps[i].SpriteResourcePath);
        }

        /// <summary>Assigns sprites (or keeps slate placeholder tint if art is missing).</summary>
        void ApplyInstructionSprites()
        {
            for (int i = 0; i < _columns.Count; i++)
            {
                Image image = _columns[i].Illustration;
                if (image == null)
                    continue;

                Sprite sprite = LoadSprite(Steps[i].SpriteResourcePath);
                image.sprite = sprite;
                image.color = sprite != null ? Color.white : new Color(0.18f, 0.22f, 0.32f, 1f);
            }
        }

        /// <summary>
        /// Loads instruction art from Resources as a Texture2D, trims baked letterbox bars
        /// when the texture is readable, then builds a Sprite that fills the UI frame.
        /// </summary>
        Sprite LoadSprite(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath))
                return null;

            if (_spriteCache.TryGetValue(resourcePath, out Sprite cached) && cached != null)
                return cached;

            Sprite sprite = null;
            Texture2D texture = Resources.Load<Texture2D>(resourcePath);
            if (texture != null)
            {
                // --- Crop baked black bars (e.g. instruction_upgrades) ---
                // [UNITY] Sprite.Create rect uses bottom-left origin in texture pixels.
                Rect rect = GetTrimmedContentRect(texture);
                sprite = Sprite.Create(
                    texture,
                    rect,
                    new Vector2(0.5f, 0.5f),
                    100f);
            }
            else
            {
                // Fallback if imported as Sprite only.
                sprite = Resources.Load<Sprite>(resourcePath);
            }

            if (sprite == null)
                Debug.LogWarning("[LoadingScreen] Missing instruction art at Resources/" + resourcePath);

            _spriteCache[resourcePath] = sprite;
            return sprite;
        }

        /// <summary>
        /// Returns the largest content rect after stripping near-black letterbox rows/columns.
        /// Falls back to the full texture when Read/Write is off or trim finds nothing.
        /// [UNITY] GetPixels32 is bottom-left origin — y=0 is the bottom row of the image.
        /// </summary>
        static Rect GetTrimmedContentRect(Texture2D texture)
        {
            int width = texture.width;
            int height = texture.height;
            var full = new Rect(0f, 0f, width, height);

            if (!texture.isReadable)
                return full;

            Color32[] pixels;
            try
            {
                pixels = texture.GetPixels32();
            }
            catch
            {
                return full;
            }

            const byte darkThreshold = 18;

            // --- Visual top = high y; visual bottom = low y ---
            int yMax = height - 1;
            int yMin = 0;

            for (; yMax >= 0; yMax--)
            {
                if (!RowIsDark(pixels, width, height, yMax, darkThreshold))
                    break;
            }

            for (; yMin <= yMax; yMin++)
            {
                if (!RowIsDark(pixels, width, height, yMin, darkThreshold))
                    break;
            }

            int left = 0;
            int right = width - 1;
            for (; left < width; left++)
            {
                if (!ColumnIsDark(pixels, width, height, left, yMin, yMax, darkThreshold))
                    break;
            }

            for (; right > left; right--)
            {
                if (!ColumnIsDark(pixels, width, height, right, yMin, yMax, darkThreshold))
                    break;
            }

            int contentHeight = yMax - yMin + 1;
            int contentWidth = right - left + 1;
            if (contentWidth < width / 4 || contentHeight < height / 4)
                return full;

            return new Rect(left, yMin, contentWidth, contentHeight);
        }

        /// <summary>True when every pixel in texture row <paramref name="y"/> is near-black.</summary>
        static bool RowIsDark(Color32[] pixels, int width, int height, int y, byte threshold)
        {
            if (y < 0 || y >= height)
                return true;

            int row = y * width;
            // Sample every 8th pixel for speed — letterbox bars are solid.
            for (int x = 0; x < width; x += 8)
            {
                Color32 c = pixels[row + x];
                if (c.r > threshold || c.g > threshold || c.b > threshold)
                    return false;
            }

            return true;
        }

        /// <summary>True when every sampled pixel in column <paramref name="x"/> (between yMin..yMax) is near-black.</summary>
        static bool ColumnIsDark(
            Color32[] pixels,
            int width,
            int height,
            int x,
            int yMin,
            int yMax,
            byte threshold)
        {
            if (x < 0 || x >= width)
                return true;

            for (int y = yMin; y <= yMax; y += 8)
            {
                Color32 c = pixels[y * width + x];
                if (c.r > threshold || c.g > threshold || c.b > threshold)
                    return false;
            }

            return true;
        }

        /// <summary>Creates an empty RectTransform child.</summary>
        static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        /// <summary>Creates a TMP label under <paramref name="parent"/>.</summary>
        static TextMeshProUGUI CreateText(Transform parent, string name, string text, float size, FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>Stretches a rect to its parent with optional padding.</summary>
        static void StretchFill(RectTransform rect, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }
    }
}
