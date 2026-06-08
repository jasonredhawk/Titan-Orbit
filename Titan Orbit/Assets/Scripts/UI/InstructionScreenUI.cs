using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Quick-read how-to-play screen shown after joining a match and before the map build animation.
    /// Steps scroll horizontally; each card shows title, illustration, and description.
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

        private struct StepCardLayout
        {
            public LayoutElement CardWidth;
            public LayoutElement ImageFrame;
        }

        private static readonly InstructionStep[] DefaultSteps =
        {
            new InstructionStep(
                "Capture All Planets",
                "Your team wins by controlling every planet on the map. Grow your empire by moving population between worlds.",
                "InstructionScreens/instruction_objective"),
            new InstructionStep(
                "Transport People",
                "Fly to a friendly planet, pick up people, then deliver them to neutral or enemy planets to capture and hold territory.",
                "InstructionScreens/instruction_transport"),
            new InstructionStep(
                "Mine Asteroids for Gems",
                "Pilot your ship into asteroid fields and mine them. Gems are the currency you need for upgrades.",
                "InstructionScreens/instruction_mining"),
            new InstructionStep(
                "Upgrade Ships & Planets",
                "Spend gems to upgrade your ship and level up planets. Stronger ships and higher-level planets give you an edge.",
                "InstructionScreens/instruction_upgrades"),
            new InstructionStep(
                "Unique Planet Ships",
                "Each planet sells its own ship types. Visit different worlds to discover and purchase new ships for your fleet.",
                "InstructionScreens/instruction_planet_ships"),
        };

        private const float CardViewportWidthFraction = 0.9f;
        private const float MinCardWidth = 480f;
        private const float MaxCardWidth = 760f;
        private const float CardImageAspect = 4f / 3f;
        private const float ContentMaxWidth = 920f;

        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI subtitleText;
        [SerializeField] private RectTransform stepsContentRoot;
        [SerializeField] private ScrollRect stepsScrollRect;
        [SerializeField] private Button continueButton;
        [SerializeField] private Image[] stepImageSlots;
        [Tooltip("Optional screenshots that override the built-in placeholder art (Objective, Transport, Mining, Upgrades, Planet Ships).")]
        [SerializeField] private Sprite[] stepScreenshots;

        private readonly List<StepCardLayout> stepCardLayouts = new List<StepCardLayout>();
        private readonly Dictionary<string, Sprite> spriteCache = new Dictionary<string, Sprite>();
        private Action onContinue;
        private bool uiBuilt;
        private Coroutine refreshLayoutRoutine;

        public bool IsVisible => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
            EnsureUiBuilt();
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
            EnsureUiBuilt();
            onContinue = onContinueCallback;

            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                transform.SetParent(canvas.transform, false);
                var hostRect = transform as RectTransform;
                if (hostRect == null)
                    hostRect = gameObject.AddComponent<RectTransform>();
                hostRect.anchorMin = Vector2.zero;
                hostRect.anchorMax = Vector2.one;
                hostRect.offsetMin = hostRect.offsetMax = Vector2.zero;
                transform.SetAsLastSibling();
            }

            PreloadInstructionSprites();

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }

            if (stepsScrollRect != null)
                stepsScrollRect.horizontalNormalizedPosition = 0f;

            if (refreshLayoutRoutine != null)
                StopCoroutine(refreshLayoutRoutine);
            refreshLayoutRoutine = StartCoroutine(CoRefreshLayoutAfterShow());
        }

        /// <summary>Assign screenshots from the MainMenu inspector (one per instruction step).</summary>
        public void SetStepScreenshots(Sprite[] sprites)
        {
            stepScreenshots = sprites;
            if (uiBuilt)
                ApplyStepScreenshots();
        }

        public void Hide()
        {
            if (refreshLayoutRoutine != null)
            {
                StopCoroutine(refreshLayoutRoutine);
                refreshLayoutRoutine = null;
            }

            if (panelRoot != null)
                panelRoot.SetActive(false);
            onContinue = null;
        }

        private void OnContinueClicked()
        {
            var callback = onContinue;
            Hide();
            callback?.Invoke();
        }

        private IEnumerator CoRefreshLayoutAfterShow()
        {
            yield return null;
            UpdateCardLayoutSizes();
            ApplyStepScreenshots();

            if (stepsContentRoot != null)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(stepsContentRoot);
            }

            refreshLayoutRoutine = null;
        }

        private void EnsureUiBuilt()
        {
            if (uiBuilt && panelRoot != null)
                return;

            Transform parent = transform;

            if (panelRoot == null)
            {
                panelRoot = new GameObject("InstructionScreenPanel");
                panelRoot.transform.SetParent(parent, false);

                var panelRect = panelRoot.AddComponent<RectTransform>();
                panelRect.anchorMin = Vector2.zero;
                panelRect.anchorMax = Vector2.one;
                panelRect.offsetMin = panelRect.offsetMax = Vector2.zero;

                var backdrop = panelRoot.AddComponent<Image>();
                backdrop.color = new Color(0.02f, 0.04f, 0.1f, 0.96f);
            }

            if (titleText == null)
            {
                titleText = CreateHeaderLabel(panelRoot.transform, "Title", "HOW TO PLAY", 36, FontStyles.Bold);
                var titleRect = titleText.rectTransform;
                titleRect.anchorMin = new Vector2(0.5f, 1f);
                titleRect.anchorMax = new Vector2(0.5f, 1f);
                titleRect.pivot = new Vector2(0.5f, 1f);
                titleRect.anchoredPosition = new Vector2(0f, -20f);
                titleRect.sizeDelta = new Vector2(ContentMaxWidth, 44f);
            }

            if (subtitleText == null)
            {
                subtitleText = CreateHeaderLabel(panelRoot.transform, "Subtitle",
                    "Swipe through the steps — tap Continue when you're ready to enter the match.",
                    18, FontStyles.Normal);
                subtitleText.color = new Color(0.65f, 0.78f, 0.92f, 0.95f);
                var subtitleRect = subtitleText.rectTransform;
                subtitleRect.anchorMin = new Vector2(0.5f, 1f);
                subtitleRect.anchorMax = new Vector2(0.5f, 1f);
                subtitleRect.pivot = new Vector2(0.5f, 1f);
                subtitleRect.anchoredPosition = new Vector2(0f, -62f);
                subtitleRect.sizeDelta = new Vector2(ContentMaxWidth, 28f);
            }

            if (stepsContentRoot == null)
                BuildScrollArea(panelRoot.transform);

            if (continueButton == null)
                continueButton = CreateContinueButton(panelRoot.transform);

            PopulateSteps();
            uiBuilt = true;
        }

        private void BuildScrollArea(Transform panel)
        {
            var scrollRoot = new GameObject("StepsScroll");
            scrollRoot.transform.SetParent(panel, false);
            var scrollRectTransform = scrollRoot.AddComponent<RectTransform>();
            scrollRectTransform.anchorMin = new Vector2(0.03f, 0.12f);
            scrollRectTransform.anchorMax = new Vector2(0.97f, 0.84f);
            scrollRectTransform.offsetMin = Vector2.zero;
            scrollRectTransform.offsetMax = Vector2.zero;

            stepsScrollRect = scrollRoot.AddComponent<ScrollRect>();
            stepsScrollRect.horizontal = true;
            stepsScrollRect.vertical = false;
            stepsScrollRect.movementType = ScrollRect.MovementType.Elastic;
            stepsScrollRect.scrollSensitivity = 24f;

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollRoot.transform, false);
            var viewportRect = viewport.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = viewportRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();
            var viewportImg = viewport.AddComponent<Image>();
            viewportImg.color = new Color(0f, 0f, 0f, 0.01f);

            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            stepsContentRoot = content.AddComponent<RectTransform>();
            stepsContentRoot.anchorMin = new Vector2(0f, 0f);
            stepsContentRoot.anchorMax = new Vector2(0f, 1f);
            stepsContentRoot.pivot = new Vector2(0f, 0.5f);
            stepsContentRoot.anchoredPosition = Vector2.zero;
            stepsContentRoot.sizeDelta = new Vector2(0f, 0f);

            var layout = content.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 24f;
            layout.padding = new RectOffset(12, 12, 6, 6);
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            stepsScrollRect.viewport = viewportRect;
            stepsScrollRect.content = stepsContentRoot;
        }

        private void PopulateSteps()
        {
            if (stepsContentRoot == null)
                return;

            for (int i = stepsContentRoot.childCount - 1; i >= 0; i--)
                Destroy(stepsContentRoot.GetChild(i).gameObject);

            stepCardLayouts.Clear();
            stepImageSlots = new Image[DefaultSteps.Length];

            for (int i = 0; i < DefaultSteps.Length; i++)
                CreateStepCard(stepsContentRoot, DefaultSteps[i], i);

            UpdateCardLayoutSizes();
            ApplyStepScreenshots();
        }

        private void UpdateCardLayoutSizes()
        {
            float cardWidth = ComputeCardWidth();
            float imageWidth = Mathf.Max(200f, cardWidth - 32f);
            float imageHeight = imageWidth / CardImageAspect;

            foreach (var layout in stepCardLayouts)
            {
                if (layout.CardWidth != null)
                {
                    layout.CardWidth.preferredWidth = cardWidth;
                    layout.CardWidth.minWidth = cardWidth;
                }

                if (layout.ImageFrame != null)
                {
                    layout.ImageFrame.preferredWidth = imageWidth;
                    layout.ImageFrame.minWidth = imageWidth;
                    layout.ImageFrame.preferredHeight = imageHeight;
                    layout.ImageFrame.minHeight = imageHeight;
                }
            }
        }

        private float ComputeCardWidth()
        {
            float viewportWidth = 960f;
            if (stepsScrollRect != null && stepsScrollRect.viewport != null)
            {
                Canvas.ForceUpdateCanvases();
                viewportWidth = stepsScrollRect.viewport.rect.width;
            }

            if (viewportWidth < 80f)
                viewportWidth = Screen.width;

            return Mathf.Clamp(viewportWidth * CardViewportWidthFraction, MinCardWidth, MaxCardWidth);
        }

        private void ApplyStepScreenshots()
        {
            if (stepImageSlots == null)
                return;

            for (int i = 0; i < stepImageSlots.Length; i++)
            {
                if (stepImageSlots[i] == null)
                    continue;

                Sprite sprite = null;
                if (stepScreenshots != null && i < stepScreenshots.Length)
                    sprite = stepScreenshots[i];

                if (sprite == null && i < DefaultSteps.Length)
                    sprite = LoadInstructionSprite(DefaultSteps[i].SpriteResourcePath);

                if (sprite == null)
                {
                    stepImageSlots[i].sprite = null;
                    stepImageSlots[i].color = new Color(0.2f, 0.24f, 0.34f, 1f);
                    continue;
                }

                stepImageSlots[i].sprite = sprite;
                stepImageSlots[i].color = Color.white;
                stepImageSlots[i].preserveAspect = true;
                stepImageSlots[i].type = Image.Type.Simple;
                stepImageSlots[i].enabled = true;
            }
        }

        private void PreloadInstructionSprites()
        {
            for (int i = 0; i < DefaultSteps.Length; i++)
                LoadInstructionSprite(DefaultSteps[i].SpriteResourcePath);
        }

        private Sprite LoadInstructionSprite(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath))
                return null;

            if (spriteCache.TryGetValue(resourcePath, out Sprite cached) && cached != null)
                return cached;

            Sprite sprite = Resources.Load<Sprite>(resourcePath);
            if (sprite == null)
            {
                var texture = Resources.Load<Texture2D>(resourcePath);
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
            {
                Debug.LogWarning("[InstructionScreenUI] Could not load instruction art at Resources/" + resourcePath);
                return null;
            }

            spriteCache[resourcePath] = sprite;
            return sprite;
        }

        private void CreateStepCard(Transform parent, InstructionStep step, int index)
        {
            var card = new GameObject("Step_" + (index + 1));
            card.transform.SetParent(parent, false);
            card.AddComponent<RectTransform>();

            var cardWidthElement = card.AddComponent<LayoutElement>();
            cardWidthElement.preferredWidth = MinCardWidth;
            cardWidthElement.minWidth = MinCardWidth;
            cardWidthElement.flexibleHeight = 1f;

            var cardBg = card.AddComponent<Image>();
            cardBg.color = new Color(0.08f, 0.11f, 0.2f, 0.92f);

            var cardGroup = card.AddComponent<VerticalLayoutGroup>();
            cardGroup.spacing = 14f;
            cardGroup.padding = new RectOffset(16, 16, 16, 16);
            cardGroup.childAlignment = TextAnchor.UpperCenter;
            cardGroup.childControlWidth = true;
            cardGroup.childControlHeight = true;
            cardGroup.childForceExpandWidth = true;
            cardGroup.childForceExpandHeight = false;

            var title = CreateStepText(card.transform, "StepTitle", step.Title, 24, FontStyles.Bold);
            title.alignment = TextAlignmentOptions.Center;
            title.color = new Color(0.92f, 0.96f, 1f, 1f);
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 36f;
            titleLe.flexibleWidth = 1f;

            stepImageSlots[index] = CreateStepImage(card.transform, out LayoutElement imageFrameElement);

            var body = CreateStepText(card.transform, "StepBody", step.Body, 18, FontStyles.Normal);
            body.alignment = TextAlignmentOptions.TopLeft;
            body.color = new Color(0.72f, 0.82f, 0.94f, 0.98f);
            body.enableWordWrapping = true;
            body.overflowMode = TextOverflowModes.Overflow;
            var bodyLe = body.gameObject.AddComponent<LayoutElement>();
            bodyLe.flexibleWidth = 1f;
            bodyLe.flexibleHeight = 1f;
            bodyLe.minHeight = 80f;
            var bodyFitter = body.gameObject.AddComponent<ContentSizeFitter>();
            bodyFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            bodyFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            stepCardLayouts.Add(new StepCardLayout
            {
                CardWidth = cardWidthElement,
                ImageFrame = imageFrameElement,
            });
        }

        private static Image CreateStepImage(Transform parent, out LayoutElement frameElement)
        {
            var frame = new GameObject("ImageFrame");
            frame.transform.SetParent(parent, false);
            frame.AddComponent<RectTransform>();

            frameElement = frame.AddComponent<LayoutElement>();
            frameElement.preferredWidth = MinCardWidth - 32f;
            frameElement.minWidth = MinCardWidth - 32f;
            frameElement.preferredHeight = (MinCardWidth - 32f) / CardImageAspect;
            frameElement.minHeight = frameElement.preferredHeight;

            var frameBg = frame.AddComponent<Image>();
            frameBg.color = new Color(0.04f, 0.06f, 0.1f, 1f);
            frameBg.raycastTarget = false;

            var imageGo = new GameObject("StepImage");
            imageGo.transform.SetParent(frame.transform, false);
            var imageRect = imageGo.AddComponent<RectTransform>();
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = new Vector2(4f, 4f);
            imageRect.offsetMax = new Vector2(-4f, -4f);

            var img = imageGo.AddComponent<Image>();
            img.color = new Color(0.2f, 0.24f, 0.34f, 1f);
            img.preserveAspect = true;
            img.type = Image.Type.Simple;
            img.raycastTarget = false;

            return img;
        }

        private static Button CreateContinueButton(Transform panel)
        {
            var go = new GameObject("ContinueButton");
            go.transform.SetParent(panel, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 24f);
            rect.sizeDelta = new Vector2(320f, 52f);

            var img = go.AddComponent<Image>();
            img.color = new Color(0.22f, 0.52f, 0.88f, 0.95f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var label = CreateHeaderLabel(go.transform, "Label", "Continue", 24, FontStyles.Bold);
            label.alignment = TextAlignmentOptions.Center;
            var labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            return btn;
        }

        private static TextMeshProUGUI CreateHeaderLabel(Transform parent, string name, string text, int fontSize, FontStyles fontStyle)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = fontStyle;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = true;
            tmp.color = new Color(0.85f, 0.92f, 1f, 1f);
            return tmp;
        }

        private static TextMeshProUGUI CreateStepText(Transform parent, string name, string text, int fontSize, FontStyles fontStyle)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = fontStyle;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = true;
            tmp.richText = true;
            tmp.color = new Color(0.85f, 0.92f, 1f, 1f);
            return tmp;
        }
    }
}
