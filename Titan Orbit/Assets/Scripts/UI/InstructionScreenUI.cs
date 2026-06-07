using System;
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

        private const float CardWidth = 300f;
        private const float CardImageAspect = 4f / 3f;
        private const float ContentMaxWidth = 820f;

        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI subtitleText;
        [SerializeField] private RectTransform stepsContentRoot;
        [SerializeField] private ScrollRect stepsScrollRect;
        [SerializeField] private Button continueButton;
        [SerializeField] private Image[] stepImageSlots;
        [Tooltip("Optional screenshots that override the built-in placeholder art (Objective, Transport, Mining, Upgrades, Planet Ships).")]
        [SerializeField] private Sprite[] stepScreenshots;

        private Action onContinue;
        private bool uiBuilt;

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
            if (panelRoot != null)
                panelRoot.SetActive(true);

            if (stepsScrollRect != null)
                stepsScrollRect.horizontalNormalizedPosition = 0f;

            if (stepsContentRoot != null)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(stepsContentRoot);
            }
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
            scrollRectTransform.anchorMin = new Vector2(0.04f, 0.12f);
            scrollRectTransform.anchorMax = new Vector2(0.96f, 0.84f);
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
            layout.spacing = 20f;
            layout.padding = new RectOffset(8, 8, 4, 4);
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

            stepImageSlots = new Image[DefaultSteps.Length];

            for (int i = 0; i < DefaultSteps.Length; i++)
                CreateStepCard(stepsContentRoot, DefaultSteps[i], i);

            ApplyStepScreenshots();
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
                    sprite = Resources.Load<Sprite>(DefaultSteps[i].SpriteResourcePath);

                if (sprite == null)
                    continue;

                stepImageSlots[i].sprite = sprite;
                stepImageSlots[i].color = Color.white;
                stepImageSlots[i].preserveAspect = true;
                stepImageSlots[i].type = Image.Type.Simple;
            }
        }

        private void CreateStepCard(Transform parent, InstructionStep step, int index)
        {
            var card = new GameObject("Step_" + (index + 1));
            card.transform.SetParent(parent, false);
            card.AddComponent<RectTransform>();

            var cardLayout = card.AddComponent<LayoutElement>();
            cardLayout.preferredWidth = CardWidth;
            cardLayout.minWidth = CardWidth;
            cardLayout.flexibleHeight = 1f;

            var cardBg = card.AddComponent<Image>();
            cardBg.color = new Color(0.08f, 0.11f, 0.2f, 0.92f);

            var cardGroup = card.AddComponent<VerticalLayoutGroup>();
            cardGroup.spacing = 12f;
            cardGroup.padding = new RectOffset(14, 14, 14, 14);
            cardGroup.childAlignment = TextAnchor.UpperCenter;
            cardGroup.childControlWidth = true;
            cardGroup.childControlHeight = true;
            cardGroup.childForceExpandWidth = true;
            cardGroup.childForceExpandHeight = false;

            var title = CreateStepText(card.transform, "StepTitle", step.Title, 21, FontStyles.Bold);
            title.alignment = TextAlignmentOptions.Center;
            title.color = new Color(0.92f, 0.96f, 1f, 1f);
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 34f;
            titleLe.flexibleWidth = 1f;

            stepImageSlots[index] = CreateStepImage(card.transform, CardWidth - 28f);

            var body = CreateStepText(card.transform, "StepBody", step.Body, 16, FontStyles.Normal);
            body.alignment = TextAlignmentOptions.TopLeft;
            body.color = new Color(0.72f, 0.82f, 0.94f, 0.98f);
            body.enableWordWrapping = true;
            body.overflowMode = TextOverflowModes.Overflow;
            var bodyLe = body.gameObject.AddComponent<LayoutElement>();
            bodyLe.flexibleWidth = 1f;
            bodyLe.flexibleHeight = 1f;
            bodyLe.minHeight = 72f;
            var bodyFitter = body.gameObject.AddComponent<ContentSizeFitter>();
            bodyFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            bodyFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private static Image CreateStepImage(Transform parent, float width)
        {
            var frame = new GameObject("ImageFrame");
            frame.transform.SetParent(parent, false);
            frame.AddComponent<RectTransform>();

            float imageHeight = width / CardImageAspect;
            var frameLe = frame.AddComponent<LayoutElement>();
            frameLe.preferredWidth = width;
            frameLe.preferredHeight = imageHeight;
            frameLe.minHeight = imageHeight;

            var frameBg = frame.AddComponent<Image>();
            frameBg.color = new Color(0.04f, 0.06f, 0.1f, 1f);

            var imageGo = new GameObject("StepImage");
            imageGo.transform.SetParent(frame.transform, false);
            var imageRect = imageGo.AddComponent<RectTransform>();
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = new Vector2(4f, 4f);
            imageRect.offsetMax = new Vector2(-4f, -4f);

            var img = imageGo.AddComponent<Image>();
            img.color = new Color(0.55f, 0.62f, 0.74f, 1f);
            img.preserveAspect = true;
            img.type = Image.Type.Simple;

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
