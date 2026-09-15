using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// One-item IAP overlay for Orbit Unlocked: no ads, full hangar cosmetics, and
    /// an automatic +1 gear slot every match. Runtime-built dark HUD chrome — no
    /// prefab. Opened from the Main Menu stack button and the Customize Ship CTA.
    /// <para>
    /// Buy / Restore talk to <see cref="TitanOrbitIapManager"/> on the services host.
    /// Editor Play Mode can also grant via TitanOrbit → Economy without a store.
    /// </para>
    /// Client presentation only. Sorting sits above Customize Ship (540) and the
    /// badge grid (560) so the buy sheet is always clickable.
    /// </summary>
    public sealed class OrbitUnlockedPurchaseScreen : MonoBehaviour
    {
        /// <summary>Child name under the hosting canvas.</summary>
        public const string OverlayObjectName = "OrbitUnlockedPurchaseScreen";

        const int OverlaySortingOrder = 580;

        static readonly Color PanelFill = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 0.95f);
        static readonly Color AccentCyan = new Color(0.42f, 0.78f, 0.98f, 0.95f);
        static readonly Color AccentGold = new Color(0.95f, 0.78f, 0.22f, 0.95f);
        static readonly Color FrameTint = new Color(0.22f, 0.36f, 0.52f, 0.55f);
        static readonly Color BuyFill = new Color(0.22f, 0.36f, 0.18f, 0.96f);
        static readonly Color RestoreFill = new Color(0.16f, 0.28f, 0.40f, 0.95f);
        static readonly Color RevokeFill = new Color(0.42f, 0.14f, 0.16f, 0.96f);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        const bool AllowTestRevoke = true;
#else
        const bool AllowTestRevoke = false;
#endif

        TextMeshProUGUI _status;
        TextMeshProUGUI _price;
        TextMeshProUGUI _buyLabel;
        Button _buyButton;

        /// <summary>
        /// Finds or creates the overlay under <paramref name="canvasRoot"/> and shows it.
        /// Safe to call from Main Menu or Customize Ship.
        /// </summary>
        /// <param name="canvasRoot">Canvas transform that already hosts menu overlays.</param>
        public static void Open(Transform canvasRoot)
        {
            if (canvasRoot == null)
                return;

            Transform existing = canvasRoot.Find(OverlayObjectName);
            OrbitUnlockedPurchaseScreen screen;
            if (existing != null)
            {
                screen = existing.GetComponent<OrbitUnlockedPurchaseScreen>();
                if (screen == null)
                    screen = existing.gameObject.AddComponent<OrbitUnlockedPurchaseScreen>();
            }
            else
            {
                var go = new GameObject(
                    OverlayObjectName,
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(GraphicRaycaster),
                    typeof(OrbitUnlockedPurchaseScreen));
                go.layer = canvasRoot.gameObject.layer;
                go.transform.SetParent(canvasRoot, false);
                screen = go.GetComponent<OrbitUnlockedPurchaseScreen>();
            }

            screen.Show();
        }

        /// <summary>Builds chrome once, then paints owned vs buy state.</summary>
        public void Show()
        {
            EnsureChrome();
            Refresh();
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        /// <summary>Hides the overlay. Does not change entitlements.</summary>
        public void Close()
        {
            gameObject.SetActive(false);
        }

        void OnEnable()
        {
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged += Refresh;
        }

        void OnDisable()
        {
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged -= Refresh;
        }

        /// <summary>
        /// One-time overlay: dim backdrop, glass card, three benefit bullets,
        /// price, Buy / Restore / Close.
        /// </summary>
        void EnsureChrome()
        {
            if (transform.Find("Panel") != null)
                return;

            var overlayCanvas = GetComponent<Canvas>();
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = OverlaySortingOrder;

            var rootRt = GetComponent<RectTransform>();
            StretchFull(rootRt);

            var backdrop = CreateUi("Backdrop", transform, typeof(Image), typeof(Button));
            StretchFull(backdrop.GetComponent<RectTransform>());
            var backdropImage = backdrop.GetComponent<Image>();
            backdropImage.color = new Color(0.02f, 0.04f, 0.08f, 0.62f);
            backdropImage.raycastTarget = true;
            var backdropBtn = backdrop.GetComponent<Button>();
            backdropBtn.transition = Selectable.Transition.None;
            backdropBtn.onClick.AddListener(Close);

            var panel = CreateUi("Panel", transform, typeof(Image), typeof(Outline));
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(520f, 420f);
            panel.GetComponent<Image>().color = PanelFill;
            var outline = panel.GetComponent<Outline>();
            outline.effectColor = FrameTint;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var rail = CreateUi("TopRail", panel.transform, typeof(Image));
            var railRt = rail.GetComponent<RectTransform>();
            railRt.anchorMin = new Vector2(0f, 1f);
            railRt.anchorMax = new Vector2(1f, 1f);
            railRt.pivot = new Vector2(0.5f, 1f);
            railRt.sizeDelta = new Vector2(0f, 3f);
            railRt.anchoredPosition = Vector2.zero;
            rail.GetComponent<Image>().color = AccentGold;
            rail.GetComponent<Image>().raycastTarget = false;

            var title = CreateTmp(panel.transform, "Title", "ORBIT UNLOCKED", 22f, FontStyles.Bold);
            PlaceTop(title.rectTransform, 16f, 32f);
            title.alignment = TextAlignmentOptions.Center;
            title.color = AccentGold;
            title.characterSpacing = 2f;

            var blurb = CreateTmp(
                panel.transform,
                "Blurb",
                "One purchase. The hangar and the match both stay open.",
                13f,
                FontStyles.Normal);
            PlaceTop(blurb.rectTransform, 50f, 22f);
            blurb.alignment = TextAlignmentOptions.Center;
            blurb.color = CaptionColor;

            string[] bullets =
            {
                "NO ADS — keep-loadout without a video",
                "FULL HANGAR — hull presets, badges, jets",
                "+1 GEAR SLOT — every match, no tap"
            };
            for (int i = 0; i < bullets.Length; i++)
            {
                var line = CreateTmp(panel.transform, "Bullet" + i, bullets[i], 15f, FontStyles.Bold);
                PlaceTop(line.rectTransform, 88f + i * 28f, 24f);
                line.rectTransform.offsetMin = new Vector2(28f, line.rectTransform.offsetMin.y);
                line.rectTransform.offsetMax = new Vector2(-28f, line.rectTransform.offsetMax.y);
                line.alignment = TextAlignmentOptions.MidlineLeft;
                line.color = BodyColor;
            }

            _price = CreateTmp(panel.transform, "Price", "", 18f, FontStyles.Bold);
            PlaceTop(_price.rectTransform, 184f, 28f);
            _price.alignment = TextAlignmentOptions.Center;
            _price.color = AccentCyan;

            _status = CreateTmp(panel.transform, "Status", "", 13f, FontStyles.Normal);
            PlaceTop(_status.rectTransform, 214f, 36f);
            _status.alignment = TextAlignmentOptions.Center;
            _status.color = CaptionColor;
            _status.enableWordWrapping = true;

            _buyButton = CreateActionButton(
                panel.transform,
                "Buy",
                "UNLOCK",
                new Vector2(0f, 88f),
                BuyFill,
                OnBuyClicked);
            _buyLabel = _buyButton.GetComponentInChildren<TextMeshProUGUI>();

            CreateActionButton(
                panel.transform,
                "Restore",
                "RESTORE",
                new Vector2(-110f, 36f),
                RestoreFill,
                OnRestoreClicked);

            CreateActionButton(
                panel.transform,
                "Close",
                "CLOSE",
                new Vector2(110f, 36f),
                RestoreFill,
                Close);
        }

        /// <summary>
        /// Repaints price / owned label from the live IAP manager and entitlements.
        /// Called on show and whenever ownership flips.
        /// </summary>
        public void Refresh()
        {
            EnsureChrome();

            bool owned = TitanOrbitEntitlements.IsOrbitUnlockedOwned;
            var iap = Object.FindFirstObjectByType<TitanOrbitIapManager>();
            string productId = iap != null
                ? iap.OrbitUnlockedProductId
                : TitanOrbitEntitlements.OrbitUnlockedProductIdDefault;
            string price = iap != null ? iap.GetLocalizedPriceString(productId) : "";

            if (_price != null)
                _price.text = owned ? "OWNED" : (string.IsNullOrEmpty(price) ? "" : price);

            if (_buyLabel != null)
            {
                if (owned && AllowTestRevoke)
                    _buyLabel.text = "REMOVE (TEST)";
                else
                    _buyLabel.text = owned ? "OWNED" : "UNLOCK";
            }

            if (_buyButton != null)
            {
                var buyImage = _buyButton.GetComponent<Image>();
                if (buyImage != null)
                    buyImage.color = owned && AllowTestRevoke ? RevokeFill : BuyFill;

                if (owned)
                    _buyButton.interactable = AllowTestRevoke;
                else
#if UNITY_EDITOR
                    _buyButton.interactable = true;
#else
                    _buyButton.interactable = iap != null && iap.CanInitiatePurchase(productId);
#endif
            }

            if (_status == null)
                return;

            if (owned)
            {
                _status.text = AllowTestRevoke
                    ? "Owned. REMOVE (TEST) clears the local entitlement — also TitanOrbit → Economy."
                    : "Ads are off. Hangar cosmetics and the extra gear slot are yours.";
                return;
            }

            if (iap == null || !iap.IsStoreReady)
            {
#if UNITY_EDITOR
                _status.text = "Store not ready. Editor: TitanOrbit → Economy → Grant Orbit Unlocked.";
#else
                _status.text = "Store not ready. Try Restore after the catalog loads.";
#endif
                return;
            }

            _status.text = iap.GetUiOwnershipLabel(productId);
        }

        /// <summary>
        /// Starts the Unity IAP purchase, or in Editor / Development Player clears
        /// the local entitlement so hangar locks can be re-tested.
        /// </summary>
        void OnBuyClicked()
        {
            if (TitanOrbitEntitlements.IsOrbitUnlockedOwned)
            {
                if (!AllowTestRevoke)
                    return;

                // --- Test revoke ---
                // [EDITOR] PlayerPrefs only. A real store receipt can come back on Restore.
                TitanOrbitEntitlements.SetOrbitUnlockedOwned(false);
                Refresh();
                return;
            }

            var iap = Object.FindFirstObjectByType<TitanOrbitIapManager>();
#if UNITY_EDITOR
            // [EDITOR] Fake store often has no catalog. Grant locally so hangar locks can be tested.
            if (iap == null || !iap.CanInitiatePurchase(iap.OrbitUnlockedProductId))
            {
                TitanOrbitEntitlements.SetOrbitUnlockedOwned(true);
                Refresh();
                return;
            }
#endif
            if (iap == null)
            {
                if (_status != null)
                    _status.text = "Store host missing.";
                return;
            }

            iap.InitiateOrbitUnlockedPurchase();
            if (_status != null)
                _status.text = "Opening store…";
        }

        /// <summary>Apple restore on iOS; receipt re-read on other platforms.</summary>
        void OnRestoreClicked()
        {
            var iap = Object.FindFirstObjectByType<TitanOrbitIapManager>();
            if (iap == null)
            {
                if (_status != null)
                    _status.text = "Store host missing.";
                return;
            }

            if (_status != null)
                _status.text = "Restoring…";
            iap.RestorePurchases((ok, message) =>
            {
                Refresh();
                if (_status == null)
                    return;
                if (TitanOrbitEntitlements.IsOrbitUnlockedOwned)
                    _status.text = "Restored. Orbit Unlocked is owned.";
                else if (!ok)
                    _status.text = string.IsNullOrEmpty(message) ? "Restore failed." : message;
                else
                    _status.text = "No Orbit Unlocked receipt on this store account.";
            });
        }

        Button CreateActionButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchored,
            Color fill,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = CreateUi(name, parent, typeof(Image), typeof(Button));
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(200f, 40f);
            rt.anchoredPosition = anchored;
            go.GetComponent<Image>().color = fill;
            var button = go.GetComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            button.onClick.AddListener(onClick);
            var tmp = CreateTmp(go.transform, "Label", label, 15f, FontStyles.Bold);
            StretchFull(tmp.rectTransform);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = BodyColor;
            tmp.raycastTarget = false;
            return button;
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

        static TextMeshProUGUI CreateTmp(
            Transform parent,
            string name,
            string text,
            float fontSize,
            FontStyles style)
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

        static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        static void PlaceTop(RectTransform rt, float yFromTop, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-24f, height);
            rt.anchoredPosition = new Vector2(0f, -yFromTop);
        }
    }

    /// <summary>
    /// Keeps a Main Menu stack button labeled Unlock Orbit / Orbit Unlocked and
    /// opens <see cref="OrbitUnlockedPurchaseScreen"/> on click.
    /// </summary>
    public sealed class OrbitUnlockedMenuButton : MonoBehaviour
    {
        TextMeshProUGUI _label;

        /// <summary>Wires listeners. Safe to call every Main Menu refresh.</summary>
        public void Bind()
        {
            _label = GetComponentInChildren<TextMeshProUGUI>(true);
            var button = GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveListener(OnClicked);
                button.onClick.AddListener(OnClicked);
            }

            RefreshLabel();
        }

        void OnEnable()
        {
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged += RefreshLabel;
            RefreshLabel();
        }

        void OnDisable()
        {
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged -= RefreshLabel;
        }

        void OnClicked()
        {
            var canvas = GetComponentInParent<Canvas>();
            Transform root = canvas != null ? canvas.transform : transform.root;
            OrbitUnlockedPurchaseScreen.Open(root);
        }

        void RefreshLabel()
        {
            if (_label == null)
                _label = GetComponentInChildren<TextMeshProUGUI>(true);
            if (_label == null)
                return;
            _label.text = TitanOrbitEntitlements.IsOrbitUnlockedOwned
                ? "Orbit Unlocked"
                : "Unlock Orbit";
        }
    }
}
