using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Quick-read how-to-play screen shown after joining a match and before the map build animation.
    /// Image slots are placeholders until real screenshots are assigned in the inspector.
    /// </summary>
    public class InstructionScreenUI : MonoBehaviour
    {
        private struct InstructionStep
        {
            public string Title;
            public string Body;
            public string PlaceholderLabel;

            public InstructionStep(string title, string body, string placeholderLabel)
            {
                Title = title;
                Body = body;
                PlaceholderLabel = placeholderLabel;
            }
        }

        private static readonly InstructionStep[] DefaultSteps =
        {
            new InstructionStep(
                "Capture All Planets",
                "Your team wins by controlling every planet on the map. Grow your empire by moving population between worlds.",
                "Objective"),
            new InstructionStep(
                "Transport People",
                "Fly to a friendly planet, pick up people, then deliver them to neutral or enemy planets to capture and hold territory.",
                "Transport"),
            new InstructionStep(
                "Mine Asteroids for Gems",
                "Pilot your ship into asteroid fields and mine them. Gems are the currency you need for upgrades.",
                "Mining"),
            new InstructionStep(
                "Upgrade Ships & Planets",
                "Spend gems to upgrade your ship and level up planets. Stronger ships and higher-level planets give you an edge.",
                "Upgrades"),
            new InstructionStep(
                "Unique Planet Ships",
                "Each planet sells its own ship types. Visit different worlds to discover and purchase new ships for your fleet.",
                "Planet Ships"),
        };

        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI subtitleText;
        [SerializeField] private RectTransform stepsContentRoot;
        [SerializeField] private Button continueButton;
        [SerializeField] private Image[] stepImageSlots;
        [Tooltip("Optional screenshots aligned with each instruction step (Objective, Transport, Mining, Upgrades, Planet Ships).")]
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

        private const float ContentMaxWidth = 820f;

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
                    "Quick guide — tap Continue when you're ready to enter the match.",
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

            var scroll = scrollRoot.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;

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
            stepsContentRoot.anchorMin = new Vector2(0f, 1f);
            stepsContentRoot.anchorMax = new Vector2(1f, 1f);
            stepsContentRoot.pivot = new Vector2(0.5f, 1f);
            stepsContentRoot.anchoredPosition = Vector2.zero;
            stepsContentRoot.sizeDelta = new Vector2(0f, 0f);

            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 16f;
            layout.padding = new RectOffset(4, 4, 4, 12);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewportRect;
            scroll.content = stepsContentRoot;
        }

        private void PopulateSteps()
        {
            if (stepsContentRoot == null)
                return;

            for (int i = stepsContentRoot.childCount - 1; i >= 0; i--)
                Destroy(stepsContentRoot.GetChild(i).gameObject);

            stepImageSlots = new Image[DefaultSteps.Length];

            for (int i = 0; i < DefaultSteps.Length; i++)
                CreateStepRow(stepsContentRoot, DefaultSteps[i], i);

            ApplyStepScreenshots();
        }

        private void ApplyStepScreenshots()
        {
            if (stepImageSlots == null || stepScreenshots == null)
                return;

            int count = Mathf.Min(stepImageSlots.Length, stepScreenshots.Length);
            for (int i = 0; i < count; i++)
            {
                if (stepImageSlots[i] == null || stepScreenshots[i] == null)
                    continue;

                stepImageSlots[i].sprite = stepScreenshots[i];
                stepImageSlots[i].color = Color.white;
                stepImageSlots[i].preserveAspect = true;

                var placeholderLabel = stepImageSlots[i].transform.Find("PlaceholderLabel");
                if (placeholderLabel != null)
                    placeholderLabel.gameObject.SetActive(false);
            }
        }

        private void CreateStepRow(Transform parent, InstructionStep step, int index)
        {
            var row = new GameObject("Step_" + (index + 1));
            row.transform.SetParent(parent, false);

            row.AddComponent<RectTransform>();

            var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 18f;
            rowLayout.padding = new RectOffset(16, 16, 14, 14);
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;

            var rowBg = row.AddComponent<Image>();
            rowBg.color = new Color(0.08f, 0.11f, 0.2f, 0.88f);

            var rowFitter = row.AddComponent<ContentSizeFitter>();
            rowFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            rowFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var rowWidth = row.AddComponent<LayoutElement>();
            rowWidth.minHeight = 110f;
            rowWidth.flexibleWidth = 1f;

            stepImageSlots[index] = CreateImagePlaceholder(row.transform, step.PlaceholderLabel);

            var textCol = new GameObject("TextColumn");
            textCol.transform.SetParent(row.transform, false);
            textCol.AddComponent<RectTransform>();

            var textColLayout = textCol.AddComponent<LayoutElement>();
            textColLayout.flexibleWidth = 1f;
            textColLayout.minWidth = 200f;

            var textGroup = textCol.AddComponent<VerticalLayoutGroup>();
            textGroup.spacing = 8f;
            textGroup.padding = new RectOffset(0, 0, 0, 0);
            textGroup.childAlignment = TextAnchor.UpperLeft;
            textGroup.childControlWidth = true;
            textGroup.childControlHeight = true;
            textGroup.childForceExpandWidth = true;
            textGroup.childForceExpandHeight = false;

            var textColFitter = textCol.AddComponent<ContentSizeFitter>();
            textColFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            textColFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var title = CreateStepText(textCol.transform, "StepTitle", step.Title, 22, FontStyles.Bold);
            title.color = new Color(0.92f, 0.96f, 1f, 1f);
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 30f;
            titleLe.flexibleWidth = 1f;

            var body = CreateStepText(textCol.transform, "StepBody", step.Body, 17, FontStyles.Normal);
            body.color = new Color(0.72f, 0.82f, 0.94f, 0.98f);
            body.enableWordWrapping = true;
            body.overflowMode = TextOverflowModes.Overflow;
            var bodyLe = body.gameObject.AddComponent<LayoutElement>();
            bodyLe.flexibleWidth = 1f;
            var bodyFitter = body.gameObject.AddComponent<ContentSizeFitter>();
            bodyFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            bodyFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private static Image CreateImagePlaceholder(Transform parent, string label)
        {
            var go = new GameObject("ImagePlaceholder");
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 168f;
            le.preferredHeight = 96f;
            le.minWidth = 140f;
            le.minHeight = 84f;

            var img = go.AddComponent<Image>();
            img.color = new Color(0.14f, 0.17f, 0.26f, 1f);

            var border = new GameObject("Border");
            border.transform.SetParent(go.transform, false);
            var borderRect = border.AddComponent<RectTransform>();
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = new Vector2(2f, 2f);
            borderRect.offsetMax = new Vector2(-2f, -2f);
            var borderImg = border.AddComponent<Image>();
            borderImg.color = new Color(0.22f, 0.28f, 0.4f, 0.55f);

            var labelGo = new GameObject("PlaceholderLabel");
            labelGo.transform.SetParent(go.transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(6f, 6f);
            labelRect.offsetMax = new Vector2(-6f, -6f);
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "[Screenshot:\n" + label + "]";
            tmp.fontSize = 13;
            tmp.fontStyle = FontStyles.Italic;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = true;
            tmp.color = new Color(0.55f, 0.62f, 0.74f, 0.95f);

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
