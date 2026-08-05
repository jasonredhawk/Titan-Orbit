using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen join overlay shown while the map builds on the client.
    /// Layout (top → bottom): title, status line, horizontal how-to-play instruction cards,
    /// then one progress bar driven by <see cref="EcsGameBridge.TryGetJoinLoadProgress"/>
    /// (planet/asteroid GameObject proxies vs server meta N).
    /// <para>
    /// The instruction strip is the same five-step guide that used to live on
    /// <c>InstructionScreenUI</c> — restored here so players can read while they wait.
    /// Art loads from <c>Resources/InstructionScreens/</c>. Does not gather asteroid entities
    /// (Crash!!! on Windows).
    /// </para>
    /// </summary>
    public class LoadingScreenControllerNce : MonoBehaviour
    {
        // --- Progress bar chrome ---
        const float BarPadding = 2f;

        // --- Instruction strip (matches InstructionScreenUI topics) ---
        const int StepCount = 5;
        const float InstructionSidePadding = 28f;
        const float InstructionBottomReserve = 110f; // room for status + bar + percent
        const float InstructionTopReserve = 100f;    // room for title
        const float ColumnGap = 14f;
        const float CardInnerPadding = 10f;
        const float ImageAspect = 0.75f; // height / width (4:3)

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

        RectTransform _panelRoot;
        RectTransform _barTrackRect;
        RectTransform _fillRect;
        Image _fillImage;
        TextMeshProUGUI _titleText;
        TextMeshProUGUI _statusText;
        TextMeshProUGUI _percentText;
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
        /// Per-frame: fill from <see cref="EcsGameBridge.TryGetJoinLoadProgress"/>
        /// (planet/asteroid GameObject proxies vs server meta N).
        /// </summary>
        void Update()
        {
            if (!IsVisible)
                return;

            // --- Map GO build progress ---
            // [TITAN-ORBIT] Bar covers hybrid Instantiates cost — complete only when proxies ≈ N.
            if (!EcsGameBridge.TryGetJoinLoadProgress(out float progress))
                return;

            if (_statusText != null)
            {
                string baseStatus;
                if (EcsGameBridge.TryGetMapLoadingStepCounts(out int done, out int total) && total > 0)
                    baseStatus = "Loading map... " + done + " / " + total;
                else
                    baseStatus = "Loading map...";

                // --- Stuck hint after a few seconds (Settling vs Instantiates vs drain) ---
                // [TITAN-ORBIT] 314/315 with Settling ON looked like a hang with no explanation.
                bool proxyReady = EcsGameBridge.IsMapProxyCountReady(out _, out _, out _);
                if (proxyReady || progress >= 0.99f)
                {
                    _stuckWatchSince = -1f;
                    _statusText.text = baseStatus;
                }
                else
                {
                    if (_stuckWatchSince < 0f)
                        _stuckWatchSince = Time.realtimeSinceStartup;

                    string hint = string.Empty;
                    if (Time.realtimeSinceStartup - _stuckWatchSince >= StuckHintAfterSeconds)
                        hint = EcsGameBridge.GetMapLoadStuckHint();

                    _statusText.text = string.IsNullOrEmpty(hint)
                        ? baseStatus
                        : baseStatus + " — " + hint;
                }
            }

            ApplyProgress(progress);
        }

        /// <summary>
        /// [UNITY] Canvas / resolution change — reflow the five instruction cards so they stay
        /// equal-width across the strip.
        /// </summary>
        void OnRectTransformDimensionsChange()
        {
            if (_uiBuilt && IsVisible)
                LayoutInstructionColumns();
        }

        /// <summary>Shows the overlay, resets fill, and lays out instruction cards.</summary>
        public void Show()
        {
            BuildUi();
            ApplyProgress(0f);
            _stuckWatchSince = Time.realtimeSinceStartup;
            if (_statusText != null)
                _statusText.text = "Loading map...";
            if (_panelRoot != null)
            {
                _panelRoot.SetAsLastSibling();
                _panelRoot.gameObject.SetActive(true);
            }

            // --- Instruction art + layout ---
            // Cards need an active frame so rect sizes are valid before we measure columns.
            PreloadInstructionSprites();
            ApplyInstructionSprites();
            LayoutInstructionColumns();
        }

        /// <summary>Hides the overlay without destroying it.</summary>
        public void Hide()
        {
            if (_panelRoot != null)
                _panelRoot.gameObject.SetActive(false);
        }

        /// <summary>Applies 0–1 fill amount and percent label.</summary>
        void ApplyProgress(float progress)
        {
            progress = Mathf.Clamp01(progress);

            if (_fillImage != null)
                _fillImage.fillAmount = progress;

            if (_percentText != null)
                _percentText.text = Mathf.RoundToInt(progress * 100f) + "%";
        }

        /// <summary>
        /// [UNITY] Builds the full-screen panel once under the parent Canvas:
        /// backdrop → title → how-to-play strip → status → progress bar → percent.
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

            // --- Title (top band) ---
            _titleText = CreateText(_panelRoot, "Title", "BUILDING GALAXY", 34, FontStyles.Bold);
            var titleRect = _titleText.rectTransform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -28f);
            titleRect.sizeDelta = new Vector2(-80f, 44f);
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(0.85f, 0.92f, 1f, 1f);

            var howToLabel = CreateText(_panelRoot, "HowToLabel", "HOW TO PLAY", 18, FontStyles.Bold);
            var howToRect = howToLabel.rectTransform;
            howToRect.anchorMin = new Vector2(0f, 1f);
            howToRect.anchorMax = new Vector2(1f, 1f);
            howToRect.pivot = new Vector2(0.5f, 1f);
            howToRect.anchoredPosition = new Vector2(0f, -72f);
            howToRect.sizeDelta = new Vector2(-80f, 24f);
            howToLabel.alignment = TextAlignmentOptions.Center;
            howToLabel.color = new Color(0.62f, 0.76f, 0.92f, 0.95f);

            // --- Horizontal instruction strip (middle of screen) ---
            // StretchFill(left, right, top, bottom) — leave bottom band for status + bar.
            _instructionsRow = CreateRect("InstructionsRow", _panelRoot);
            StretchFill(
                _instructionsRow,
                InstructionSidePadding,
                InstructionSidePadding,
                InstructionTopReserve,
                InstructionBottomReserve);

            _columns.Clear();
            for (int i = 0; i < StepCount; i++)
                _columns.Add(CreateInstructionColumn(_instructionsRow, Steps[i], i));

            // --- Status (above the bar) ---
            _statusText = CreateText(_panelRoot, "Status", "Loading map...", 18, FontStyles.Normal);
            var statusRect = _statusText.rectTransform;
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0f);
            statusRect.pivot = new Vector2(0.5f, 0f);
            statusRect.anchoredPosition = new Vector2(0f, 72f);
            statusRect.sizeDelta = new Vector2(-120f, 28f);
            _statusText.alignment = TextAlignmentOptions.Center;
            _statusText.color = new Color(0.62f, 0.76f, 0.92f, 0.95f);

            // --- Progress bar track ---
            var barBackground = CreateRect("ProgressBarBackground", _panelRoot);
            _barTrackRect = barBackground;
            barBackground.anchorMin = new Vector2(0.5f, 0f);
            barBackground.anchorMax = new Vector2(0.5f, 0f);
            barBackground.pivot = new Vector2(0.5f, 0f);
            barBackground.anchoredPosition = new Vector2(0f, 40f);
            barBackground.sizeDelta = new Vector2(520f, 18f);
            var barBgImage = barBackground.gameObject.AddComponent<Image>();
            barBgImage.sprite = WhiteSprite;
            barBgImage.color = new Color(0.08f, 0.12f, 0.2f, 0.95f);
            barBgImage.raycastTarget = false;

            _fillRect = CreateRect("Fill", barBackground);
            StretchFill(_fillRect, BarPadding, BarPadding, BarPadding, BarPadding);
            _fillImage = _fillRect.gameObject.AddComponent<Image>();
            _fillImage.sprite = WhiteSprite;
            _fillImage.type = Image.Type.Filled;
            _fillImage.fillMethod = Image.FillMethod.Horizontal;
            _fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            _fillImage.fillAmount = 0f;
            _fillImage.color = new Color(0.28f, 0.62f, 0.98f, 1f);
            _fillImage.raycastTarget = false;

            _percentText = CreateText(_panelRoot, "Percent", "0%", 16, FontStyles.Bold);
            var percentRect = _percentText.rectTransform;
            percentRect.anchorMin = new Vector2(0f, 0f);
            percentRect.anchorMax = new Vector2(1f, 0f);
            percentRect.pivot = new Vector2(0.5f, 0f);
            percentRect.anchoredPosition = new Vector2(0f, 12f);
            percentRect.sizeDelta = new Vector2(0f, 22f);
            _percentText.alignment = TextAlignmentOptions.Center;
            _percentText.color = new Color(0.75f, 0.85f, 1f, 0.95f);

            _uiBuilt = true;
        }

        /// <summary>
        /// Builds one vertical card: accent → title → image frame → body.
        /// Positioned later by <see cref="LayoutInstructionColumns"/>.
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
            var frameBg = imageFrame.gameObject.AddComponent<Image>();
            frameBg.color = new Color(0.03f, 0.05f, 0.09f, 1f);
            frameBg.raycastTarget = false;

            // Placeholder color until the Resources sprite loads (dark slate).
            var illustrationGo = CreateRect("Illustration", imageFrame);
            StretchFill(illustrationGo, 4f, 4f, 4f, 4f);
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
        /// Equal-width columns across <see cref="_instructionsRow"/>, stacked title / image / body.
        /// Safe to call when the panel is inactive (no-op if row has zero size).
        /// </summary>
        void LayoutInstructionColumns()
        {
            if (_instructionsRow == null || _columns.Count == 0)
                return;

            Canvas.ForceUpdateCanvases();

            float rowWidth = _instructionsRow.rect.width;
            float rowHeight = _instructionsRow.rect.height;
            if (rowWidth < 10f || rowHeight < 10f)
                return;

            float totalGaps = ColumnGap * (StepCount - 1);
            float columnWidth = Mathf.Max((rowWidth - totalGaps) / StepCount, 40f);

            // --- Measure each card, then use a uniform height so the strip looks even ---
            var metrics = new ColumnLayoutMetrics[_columns.Count];
            float maxCardHeight = 0f;
            for (int i = 0; i < _columns.Count; i++)
            {
                metrics[i] = ComputeColumnMetrics(_columns[i], columnWidth, rowHeight);
                maxCardHeight = Mathf.Max(maxCardHeight, metrics[i].TotalHeight);
            }

            float uniformCardHeight = Mathf.Min(maxCardHeight, rowHeight);
            float verticalOffset = (rowHeight - uniformCardHeight) * 0.5f;

            for (int i = 0; i < _columns.Count; i++)
            {
                StepColumn col = _columns[i];
                float x = i * (columnWidth + ColumnGap);

                col.ColumnRoot.anchorMin = new Vector2(0f, 0f);
                col.ColumnRoot.anchorMax = new Vector2(0f, 0f);
                col.ColumnRoot.pivot = new Vector2(0f, 0f);
                col.ColumnRoot.anchoredPosition = new Vector2(x, verticalOffset);
                col.ColumnRoot.sizeDelta = new Vector2(columnWidth, uniformCardHeight);

                ApplyColumnLayout(col, columnWidth, metrics[i]);
            }
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

            float imageHeight = innerWidth * ImageAspect;
            float maxImageHeight = rowHeight * 0.5f;
            imageHeight = Mathf.Min(imageHeight, maxImageHeight);

            float totalHeight = accentHeight
                + CardInnerPadding
                + titleHeight
                + titleGap
                + imageHeight
                + imageGap
                + bodyHeight
                + CardInnerPadding;

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
        /// Loads a sprite from Resources. Falls back to Texture2D + Sprite.Create when the
        /// asset is imported as a texture rather than a Sprite.
        /// </summary>
        Sprite LoadSprite(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath))
                return null;

            if (_spriteCache.TryGetValue(resourcePath, out Sprite cached) && cached != null)
                return cached;

            Sprite sprite = Resources.Load<Sprite>(resourcePath);
            if (sprite == null)
            {
                Texture2D texture = Resources.Load<Texture2D>(resourcePath);
                if (texture != null)
                {
                    sprite = Sprite.Create(
                        texture,
                        new Rect(0f, 0f, texture.width, texture.height),
                        new Vector2(0.5f, 0.5f),
                        100f);
                }
            }

            if (sprite == null)
                Debug.LogWarning("[LoadingScreen] Missing instruction art at Resources/" + resourcePath);

            _spriteCache[resourcePath] = sprite;
            return sprite;
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
