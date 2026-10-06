using System.Threading.Tasks;
using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// In-game username and password form. WebGL uses this so Sign in never opens
    /// player-login.unity.com. Runtime-built. No prefab.
    /// </summary>
    public sealed class UsernamePasswordSignInScreen : MonoBehaviour
    {
        public const string OverlayObjectName = "UsernamePasswordSignInScreen";

        const int OverlaySortingOrder = 640;

        static readonly Color PanelFill = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CardFill = new Color(0.04f, 0.06f, 0.10f, 0.98f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 0.95f);
        static readonly Color HintColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color InputFill = new Color(0.02f, 0.03f, 0.05f, 0.92f);
        static readonly Color SignInFill = new Color(0.16f, 0.28f, 0.40f, 0.95f);
        static readonly Color CreateFill = new Color(0.22f, 0.36f, 0.18f, 0.96f);

        TMP_InputField _username;
        TMP_InputField _password;
        TextMeshProUGUI _status;
        Button _signInButton;
        Button _createButton;
        bool _busy;

        /// <summary>Finds or creates the form under <paramref name="canvasRoot"/> and shows it.</summary>
        public static void Open(Transform canvasRoot)
        {
            if (canvasRoot == null)
                return;

            Transform existing = canvasRoot.Find(OverlayObjectName);
            UsernamePasswordSignInScreen screen;
            if (existing != null)
            {
                screen = existing.GetComponent<UsernamePasswordSignInScreen>();
                if (screen == null)
                    screen = existing.gameObject.AddComponent<UsernamePasswordSignInScreen>();
            }
            else
            {
                var go = new GameObject(
                    OverlayObjectName,
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(GraphicRaycaster),
                    typeof(UsernamePasswordSignInScreen));
                go.layer = canvasRoot.gameObject.layer;
                go.transform.SetParent(canvasRoot, false);
                screen = go.GetComponent<UsernamePasswordSignInScreen>();
            }

            screen.Show();
        }

        public void Show()
        {
            EnsureChrome();
            if (_status != null)
                _status.text = "Username 3–20 characters. Password 8–30 with upper, lower, number, and symbol.";
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        public void Close()
        {
            gameObject.SetActive(false);
        }

        void EnsureChrome()
        {
            if (transform.Find("Card") != null)
                return;

            var overlayCanvas = GetComponent<Canvas>();
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = OverlaySortingOrder;
            var root = GetComponent<RectTransform>();
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var dim = CreateUi("Dim", transform, typeof(Image), typeof(Button));
            Stretch(dim.GetComponent<RectTransform>());
            dim.GetComponent<Image>().color = PanelFill;
            dim.GetComponent<Button>().onClick.AddListener(Close);

            var card = CreateUi("Card", transform, typeof(Image));
            var cardRt = card.GetComponent<RectTransform>();
            cardRt.anchorMin = new Vector2(0.5f, 0.5f);
            cardRt.anchorMax = new Vector2(0.5f, 0.5f);
            cardRt.pivot = new Vector2(0.5f, 0.5f);
            cardRt.sizeDelta = new Vector2(520f, 460f);
            card.GetComponent<Image>().color = CardFill;

            var title = CreateTmp(card.transform, "Title", "Sign in", 28f, FontStyles.Bold);
            Place(title.rectTransform, 24f, 40f);
            title.alignment = TextAlignmentOptions.Center;
            title.color = BodyColor;

            _username = CreateField(card.transform, "Username", "Username", false, 80f);
            _password = CreateField(card.transform, "Password", "Password", true, 156f);

            _status = CreateTmp(card.transform, "Status", "", 16f, FontStyles.Normal);
            Place(_status.rectTransform, 232f, 72f);
            _status.alignment = TextAlignmentOptions.Center;
            _status.color = HintColor;
            _status.enableWordWrapping = true;

            _signInButton = CreateButton(card.transform, "SignIn", "Sign in", SignInFill, 320f, () => _ = Submit(false));
            _createButton = CreateButton(card.transform, "Create", "Create account", CreateFill, 372f, () => _ = Submit(true));
            CreateButton(card.transform, "Cancel", "Cancel", new Color(0.20f, 0.22f, 0.26f, 0.95f), 424f, Close);
        }

        async Task Submit(bool createAccount)
        {
            if (_busy)
                return;
            _busy = true;
            SetButtonsInteractable(false);
            if (_status != null)
                _status.text = createAccount ? "Creating account…" : "Signing in…";

            try
            {
                var result = await UnityGameServicesBootstrap.SignInOrCreateUsernamePasswordAsync(
                    _username != null ? _username.text : "",
                    _password != null ? _password.text : "",
                    createAccount);
                if (result.ok)
                {
                    Close();
                    return;
                }

                if (_status != null)
                    _status.text = string.IsNullOrEmpty(result.message) ? "Sign-in failed. Try again." : result.message;
            }
            finally
            {
                _busy = false;
                SetButtonsInteractable(true);
            }
        }

        void SetButtonsInteractable(bool interactable)
        {
            if (_signInButton != null)
                _signInButton.interactable = interactable;
            if (_createButton != null)
                _createButton.interactable = interactable;
        }

        TMP_InputField CreateField(Transform parent, string name, string placeholder, bool password, float yFromTop)
        {
            var go = CreateUi(name, parent, typeof(Image), typeof(TMP_InputField));
            Place(go.GetComponent<RectTransform>(), yFromTop, 56f);
            go.GetComponent<Image>().color = InputFill;

            var text = CreateTmp(go.transform, "Text", "", 22f, FontStyles.Normal);
            StretchInset(text.rectTransform, 16f, 8f);
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.MidlineLeft;

            var hint = CreateTmp(go.transform, "Placeholder", placeholder, 22f, FontStyles.Italic);
            StretchInset(hint.rectTransform, 16f, 8f);
            hint.color = new Color(0.65f, 0.72f, 0.82f, 0.55f);
            hint.alignment = TextAlignmentOptions.MidlineLeft;

            var input = go.GetComponent<TMP_InputField>();
            input.textViewport = go.GetComponent<RectTransform>();
            input.textComponent = text;
            input.placeholder = hint;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = password
                ? TMP_InputField.ContentType.Password
                : TMP_InputField.ContentType.Standard;
            input.characterLimit = password ? 30 : 20;
            return input;
        }

        Button CreateButton(Transform parent, string name, string label, Color fill, float yFromTop, UnityEngine.Events.UnityAction onClick)
        {
            var go = CreateUi(name, parent, typeof(Image), typeof(Button));
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(280f, 40f);
            rt.anchoredPosition = new Vector2(0f, -yFromTop);
            go.GetComponent<Image>().color = fill;
            var button = go.GetComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            button.onClick.AddListener(onClick);
            var tmp = CreateTmp(go.transform, "Label", label, 18f, FontStyles.Bold);
            Stretch(tmp.rectTransform);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = BodyColor;
            return button;
        }

        static void Place(RectTransform rt, float yFromTop, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-48f, height);
            rt.anchoredPosition = new Vector2(0f, -yFromTop);
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void StretchInset(RectTransform rt, float x, float y)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(x, y);
            rt.offsetMax = new Vector2(-x, -y);
        }

        static GameObject CreateUi(string name, Transform parent, params System.Type[] extras)
        {
            var types = new System.Type[extras.Length + 1];
            types[0] = typeof(RectTransform);
            for (int i = 0; i < extras.Length; i++)
                types[i + 1] = extras[i];
            var go = new GameObject(name, types);
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            return go;
        }

        static TextMeshProUGUI CreateTmp(Transform parent, string name, string text, float fontSize, FontStyles style)
        {
            var go = CreateUi(name, parent, typeof(TextMeshProUGUI));
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return tmp;
        }
    }
}
