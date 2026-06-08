using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TitanOrbit.UI
{
    /// <summary>
    /// How-to-play overlay: five equal columns side-by-side, each with title, image, and description stacked vertically.
    /// </summary>
    public class InstructionScreenUI : MonoBehaviour
    {
        private struct InstructionStep
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

        private sealed class StepColumn
        {
            public RectTransform ColumnRoot;
            public RectTransform AccentBar;
            public TextMeshProUGUI Title;
            public RectTransform ImageFrame;
            public Image Illustration;
            public TextMeshProUGUI Body;
        }

        private static readonly InstructionStep[] Steps =
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

        private static readonly Color[] AccentColors =
        {
            new Color(0.28f, 0.62f, 0.98f, 1f),
            new Color(0.34f, 0.78f, 0.62f, 1f),
            new Color(0.95f, 0.72f, 0.28f, 1f),
            new Color(0.78f, 0.42f, 0.95f, 1f),
            new Color(0.98f, 0.48f, 0.38f, 1f),
        };

        private const int StepCount = 5;
        private const float HeaderHeight = 88f;
        private const float FooterHeight = 76f;
        private const float RowSidePadding = 18f;
        private const float ColumnGap = 12f;
        private const float CardInnerPadding = 10f;
        private const float ImageAspect = 0.75f; // height / width (4:3)

        [SerializeField] private Sprite[] stepScreenshots;

        private readonly List<StepColumn> columns = new List<StepColumn>();
        private readonly Dictionary<string, Sprite> spriteCache = new Dictionary<string, Sprite>();

        private RectTransform panelRoot;
        private RectTransform columnsRow;
        private Button continueButton;
        private Action onContinue;
        private bool uiBuilt;
        private Coroutine layoutRoutine;

        public bool IsVisible => panelRoot != null && panelRoot.gameObject.activeSelf;

        private void Awake()
        {
            BuildUi();
            Hide();
        }

        private void OnEnable()
        {
            if (continueButton != null)
            {
                continueButton.onClick.RemoveListener(OnContinueClicked);
                continueButton.onClick.AddListener(OnContinueClicked);
            }
        }

        public void Show(Action onContinueCallback)
        {
            BuildUi();
            onContinue = onContinueCallback;
            transform.SetAsLastSibling();
            panelRoot.gameObject.SetActive(true);

            PreloadSprites();
            ApplySprites();

            if (layoutRoutine != null)
                StopCoroutine(layoutRoutine);
            layoutRoutine = StartCoroutine(CoLayoutAfterShow());
        }

        public void SetStepScreenshots(Sprite[] sprites)
        {
            stepScreenshots = sprites;
            if (uiBuilt)
                ApplySprites();
        }

        public void Hide()
        {
            if (layoutRoutine != null)
            {
                StopCoroutine(layoutRoutine);
                layoutRoutine = null;
            }

            if (panelRoot != null)
                panelRoot.gameObject.SetActive(false);
            onContinue = null;
        }

        private void OnContinueClicked()
        {
            var callback = onContinue;
            Hide();
            callback?.Invoke();
        }

        private IEnumerator CoLayoutAfterShow()
        {
            yield return null;
            LayoutColumns();
            ApplySprites();
            layoutRoutine = null;
        }

        private void OnRectTransformDimensionsChange()
        {
            if (uiBuilt && IsVisible)
                LayoutColumns();
        }

        private void BuildUi()
        {
            if (uiBuilt)
                return;

            var host = transform as RectTransform ?? gameObject.AddComponent<RectTransform>();
            StretchFill(host, 0f, 0f, 0f, 0f);

            panelRoot = CreateRect("Panel", host);
            StretchFill(panelRoot, 0f, 0f, 0f, 0f);
            var backdrop = panelRoot.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0.02f, 0.04f, 0.1f, 0.97f);

            // Header
            var header = CreateRect("Header", panelRoot);
            AnchorTopBand(header, HeaderHeight);

            var headerTitle = CreateText(header, "Title", "HOW TO PLAY", 32, FontStyles.Bold, TextAlignmentOptions.Center);
            StretchFill(headerTitle.rectTransform, 0f, 36f, 0f, 8f);

            var headerSubtitle = CreateText(header, "Subtitle",
                "Five quick steps — tap Continue when you're ready to join a team.",
                16, FontStyles.Normal, TextAlignmentOptions.Center);
            StretchFill(headerSubtitle.rectTransform, 0f, 8f, 0f, 36f);
            headerSubtitle.color = new Color(0.62f, 0.76f, 0.92f, 0.95f);

            // Five-column row
            columnsRow = CreateRect("ColumnsRow", panelRoot);
            StretchFill(columnsRow, RowSidePadding, FooterHeight, RowSidePadding, HeaderHeight);

            columns.Clear();
            for (int i = 0; i < StepCount; i++)
                columns.Add(CreateColumn(columnsRow, Steps[i], i));

            continueButton = CreateContinueButton(panelRoot);
            uiBuilt = true;
        }

        private StepColumn CreateColumn(RectTransform row, InstructionStep step, int index)
        {
            var columnRoot = CreateRect("Column_" + (index + 1), row);
            var cardBg = columnRoot.gameObject.AddComponent<Image>();
            cardBg.color = new Color(0.07f, 0.1f, 0.18f, 0.94f);
            cardBg.raycastTarget = false;

            var accentBar = CreateRect("Accent", columnRoot);
            var accentImage = accentBar.gameObject.AddComponent<Image>();
            accentImage.color = AccentColors[index % AccentColors.Length];
            accentImage.raycastTarget = false;

            var title = CreateText(columnRoot, "Title", step.Title, 22, FontStyles.Bold, TextAlignmentOptions.Center);
            title.color = new Color(0.94f, 0.97f, 1f, 1f);

            var imageFrame = CreateRect("ImageFrame", columnRoot);
            var frameBg = imageFrame.gameObject.AddComponent<Image>();
            frameBg.color = new Color(0.03f, 0.05f, 0.09f, 1f);
            frameBg.raycastTarget = false;

            var illustrationGo = CreateRect("Illustration", imageFrame);
            StretchFill(illustrationGo, 4f, 4f, 4f, 4f);
            var illustration = illustrationGo.gameObject.AddComponent<Image>();
            illustration.preserveAspect = true;
            illustration.type = Image.Type.Simple;
            illustration.color = new Color(0.18f, 0.22f, 0.32f, 1f);
            illustration.raycastTarget = false;

            var body = CreateText(columnRoot, "Body", step.Body, 17, FontStyles.Normal, TextAlignmentOptions.TopLeft);
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

        private void LayoutColumns()
        {
            if (columnsRow == null || columns.Count == 0)
                return;

            Canvas.ForceUpdateCanvases();

            float rowWidth = columnsRow.rect.width;
            float rowHeight = columnsRow.rect.height;
            if (rowWidth < 10f || rowHeight < 10f)
                return;

            float totalGaps = ColumnGap * (StepCount - 1);
            float columnWidth = (rowWidth - totalGaps) / StepCount;
            columnWidth = Mathf.Max(columnWidth, 40f);

            var metrics = new ColumnLayoutMetrics[columns.Count];
            float maxCardHeight = 0f;
            for (int i = 0; i < columns.Count; i++)
            {
                metrics[i] = ComputeColumnMetrics(columns[i], columnWidth, rowHeight);
                maxCardHeight = Mathf.Max(maxCardHeight, metrics[i].TotalHeight);
            }

            float uniformCardHeight = Mathf.Min(maxCardHeight, rowHeight);
            float verticalOffset = (rowHeight - uniformCardHeight) * 0.5f;

            for (int i = 0; i < columns.Count; i++)
            {
                StepColumn col = columns[i];
                float x = i * (columnWidth + ColumnGap);
                float cardHeight = uniformCardHeight;

                col.ColumnRoot.anchorMin = new Vector2(0f, 0f);
                col.ColumnRoot.anchorMax = new Vector2(0f, 0f);
                col.ColumnRoot.pivot = new Vector2(0f, 0f);
                col.ColumnRoot.anchoredPosition = new Vector2(x, verticalOffset);
                col.ColumnRoot.sizeDelta = new Vector2(columnWidth, cardHeight);

                ApplyColumnLayout(col, columnWidth, metrics[i]);
            }
        }

        private struct ColumnLayoutMetrics
        {
            public float TitleFontSize;
            public float TitleHeight;
            public float ImageHeight;
            public float BodyFontSize;
            public float BodyHeight;
            public float TotalHeight;
        }

        private static ColumnLayoutMetrics ComputeColumnMetrics(StepColumn col, float columnWidth, float rowHeight)
        {
            const float accentHeight = 4f;
            const float titleGap = 8f;
            const float imageGap = 10f;

            float innerWidth = columnWidth - CardInnerPadding * 2f;
            float titleFontSize = Mathf.Clamp(columnWidth * 0.085f, 17f, 24f);
            float bodyFontSize = Mathf.Clamp(columnWidth * 0.068f, 15f, 20f);

            col.Title.fontSize = titleFontSize;
            col.Body.fontSize = bodyFontSize;

            float titleHeight = col.Title.GetPreferredValues(col.Title.text, innerWidth, 0f).y;
            float bodyHeight = col.Body.GetPreferredValues(col.Body.text, innerWidth, 0f).y;

            float imageHeight = innerWidth * ImageAspect;
            float maxImageHeight = rowHeight * 0.45f;
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

        private static void ApplyColumnLayout(StepColumn col, float columnWidth, ColumnLayoutMetrics metrics)
        {
            const float accentHeight = 4f;
            const float titleGap = 8f;
            const float imageGap = 10f;

            float titleTop = CardInnerPadding;
            float imageTop = titleTop + metrics.TitleHeight + titleGap;
            float bodyTop = imageTop + metrics.ImageHeight + imageGap;

            // Accent stripe
            col.AccentBar.anchorMin = new Vector2(0f, 1f);
            col.AccentBar.anchorMax = new Vector2(1f, 1f);
            col.AccentBar.pivot = new Vector2(0.5f, 1f);
            col.AccentBar.anchoredPosition = Vector2.zero;
            col.AccentBar.sizeDelta = new Vector2(0f, accentHeight);

            // Title
            col.Title.fontSize = metrics.TitleFontSize;
            col.Title.rectTransform.anchorMin = new Vector2(0f, 1f);
            col.Title.rectTransform.anchorMax = new Vector2(1f, 1f);
            col.Title.rectTransform.pivot = new Vector2(0.5f, 1f);
            col.Title.rectTransform.anchoredPosition = new Vector2(0f, -(titleTop + accentHeight));
            col.Title.rectTransform.sizeDelta = new Vector2(-CardInnerPadding * 2f, metrics.TitleHeight);

            // Image
            col.ImageFrame.anchorMin = new Vector2(0f, 1f);
            col.ImageFrame.anchorMax = new Vector2(1f, 1f);
            col.ImageFrame.pivot = new Vector2(0.5f, 1f);
            col.ImageFrame.anchoredPosition = new Vector2(0f, -imageTop);
            col.ImageFrame.sizeDelta = new Vector2(-CardInnerPadding * 2f, metrics.ImageHeight);

            // Body sized to text, not stretched to card bottom
            col.Body.fontSize = metrics.BodyFontSize;
            col.Body.rectTransform.anchorMin = new Vector2(0f, 1f);
            col.Body.rectTransform.anchorMax = new Vector2(1f, 1f);
            col.Body.rectTransform.pivot = new Vector2(0.5f, 1f);
            col.Body.rectTransform.anchoredPosition = new Vector2(0f, -bodyTop);
            col.Body.rectTransform.sizeDelta = new Vector2(-CardInnerPadding * 2f, metrics.BodyHeight);
        }

        private void PreloadSprites()
        {
            for (int i = 0; i < Steps.Length; i++)
                LoadSprite(Steps[i].SpriteResourcePath);
        }

        private void ApplySprites()
        {
            for (int i = 0; i < columns.Count; i++)
            {
                Image image = columns[i].Illustration;
                if (image == null)
                    continue;

                Sprite sprite = null;
                if (stepScreenshots != null && i < stepScreenshots.Length)
                    sprite = stepScreenshots[i];
                if (sprite == null)
                    sprite = LoadSprite(Steps[i].SpriteResourcePath);

                image.sprite = sprite;
                image.color = sprite != null ? Color.white : new Color(0.18f, 0.22f, 0.32f, 1f);
            }
        }

        private Sprite LoadSprite(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath))
                return null;

            if (spriteCache.TryGetValue(resourcePath, out Sprite cached) && cached != null)
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
                Debug.LogWarning("[InstructionScreenUI] Missing art at Resources/" + resourcePath);

            spriteCache[resourcePath] = sprite;
            return sprite;
        }

        private static Button CreateContinueButton(RectTransform panel)
        {
            var buttonRect = CreateRect("ContinueButton", panel);
            buttonRect.anchorMin = new Vector2(0.5f, 0f);
            buttonRect.anchorMax = new Vector2(0.5f, 0f);
            buttonRect.pivot = new Vector2(0.5f, 0f);
            buttonRect.anchoredPosition = new Vector2(0f, 16f);
            buttonRect.sizeDelta = new Vector2(300f, 48f);

            var image = buttonRect.gameObject.AddComponent<Image>();
            image.color = new Color(0.22f, 0.52f, 0.88f, 0.95f);

            var button = buttonRect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            var label = CreateText(buttonRect, "Label", "Continue", 22, FontStyles.Bold, TextAlignmentOptions.Center);
            StretchFill(label.rectTransform, 0f, 0f, 0f, 0f);
            return button;
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static void StretchFill(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static void AnchorTopBand(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, height);
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, string text, int fontSize, FontStyles style, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = alignment;
            tmp.enableWordWrapping = true;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return tmp;
        }
    }
}

