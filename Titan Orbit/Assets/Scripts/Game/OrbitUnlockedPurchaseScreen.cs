using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// One-item IAP card for Orbit Unlocked: no ads, full hangar cosmetics, and
    /// an automatic +1 gear slot every match. Runtime-built dark HUD chrome — no
    /// prefab. Lives on the Command Deck. <see cref="Open"/> still raises a modal
    /// when the in-match Customize Ship studio asks for it.
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

        /// <summary>True when this card is a Command Deck slot, not a modal sheet.</summary>
        bool _embedded;
#if UNITY_WEBGL && !UNITY_EDITOR
        bool _checkoutInFlight;
        int _priceRequest;
#endif

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

        /// <summary>
        /// Drops the buy sheet into a Command Deck slot. No dim backdrop and no Close.
        /// Reuses an existing screen if one was already built as a modal.
        /// </summary>
        public static void Embed(Transform slot)
        {
            if (slot == null)
                return;

            OrbitUnlockedPurchaseScreen screen = slot.GetComponentInChildren<OrbitUnlockedPurchaseScreen>(true);
            if (screen == null)
            {
                var found = UnityEngine.Object.FindObjectsByType<OrbitUnlockedPurchaseScreen>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] == null)
                        continue;
                    screen = found[i];
                    break;
                }
            }

            if (screen == null)
            {
                var go = new GameObject(
                    OverlayObjectName,
                    typeof(RectTransform),
                    typeof(OrbitUnlockedPurchaseScreen));
                go.layer = slot.gameObject.layer;
                go.transform.SetParent(slot, false);
                screen = go.GetComponent<OrbitUnlockedPurchaseScreen>();
            }
            else if (screen.transform.parent != slot)
            {
                screen.transform.SetParent(slot, false);
            }

            StripOwnCanvas(screen.gameObject);
            screen._embedded = true;
            StretchFull(screen.GetComponent<RectTransform>());
            screen.Show();
        }

        /// <summary>Builds chrome once, then paints owned vs buy state.</summary>
        public void Show()
        {
            EnsureChrome();
            Refresh();
            gameObject.SetActive(true);
            if (!_embedded)
                transform.SetAsLastSibling();
        }

        /// <summary>Hides the modal sheet. The deck card stays put.</summary>
        public void Close()
        {
            if (_embedded)
                return;
            gameObject.SetActive(false);
        }

        void OnEnable()
        {
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged += Refresh;
            UnityGameServicesBootstrap.AuthStateChanged += Refresh;
        }

        void OnDisable()
        {
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged -= Refresh;
            UnityGameServicesBootstrap.AuthStateChanged -= Refresh;
        }

        /// <summary>
        /// One-time overlay: dim backdrop, glass card, three benefit bullets,
        /// price, Buy / Restore / Close.
        /// </summary>
        void EnsureChrome()
        {
            if (_embedded)
            {
                if (transform.Find("EmbedCard") == null)
                {
                    ClearChildrenImmediate();
                    BuildEmbedCard();
                }

                return;
            }

            if (transform.Find("EmbedCard") != null)
                ClearChildrenImmediate();

            if (transform.Find("Panel") != null)
                return;

            var overlayCanvas = GetComponent<Canvas>();
            if (overlayCanvas != null)
            {
                overlayCanvas.overrideSorting = true;
                overlayCanvas.sortingOrder = OverlaySortingOrder;
            }

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
                "NO ADS — gear stays when you die",
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
        /// Repaints price / owned label. WebGL reads the Stripe Price. Mobile
        /// still uses the store catalog. Called on show and whenever ownership flips.
        /// </summary>
        public void Refresh()
        {
            EnsureChrome();

            bool owned = TitanOrbitEntitlements.IsOrbitUnlockedOwned;
            bool unityAccount = UnityGameServicesBootstrap.HasDurablePlayerAccount();
            var iap = Object.FindFirstObjectByType<TitanOrbitIapManager>();
            string productId = iap != null
                ? iap.OrbitUnlockedProductId
                : TitanOrbitEntitlements.OrbitUnlockedProductIdDefault;
#if UNITY_WEBGL && !UNITY_EDITOR
            string price = "";
#else
            string price = iap != null ? iap.GetLocalizedPriceString(productId) : "";
#endif

            if (_price != null)
            {
                if (!owned && !unityAccount)
                    _price.text = "";
                else
                    _price.text = owned ? "OWNED" : (string.IsNullOrEmpty(price) ? "" : price);
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (!owned && unityAccount)
                _ = LoadStripePriceAsync(++_priceRequest);
#endif

            if (_buyLabel != null)
            {
                if (owned && AllowTestRevoke)
                    _buyLabel.text = "REMOVE (TEST)";
                else if (!owned && !unityAccount)
                    _buyLabel.text = "SIGN IN";
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
                else if (!unityAccount)
                    _buyButton.interactable = true;
                else
#if UNITY_WEBGL && !UNITY_EDITOR
                    _buyButton.interactable = !_checkoutInFlight;
#elif UNITY_EDITOR
                    _buyButton.interactable = true;
#else
                    _buyButton.interactable = iap != null && iap.CanInitiatePurchase(productId);
#endif
            }

            if (_status == null)
                return;

            if (!owned && !unityAccount)
            {
                _status.text = "Sign in to buy Orbit Unlocked. The purchase stays on that account.";
                return;
            }

            if (owned)
            {
                _status.text = AllowTestRevoke
                    ? "Owned. REMOVE (TEST) clears the local entitlement — also TitanOrbit → Economy."
                    : "Ads are off. Hangar cosmetics and the extra gear slot are yours.";
                return;
            }

            if (iap == null || !iap.IsStoreReady)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                _status.text = "UNLOCK opens secure checkout for this account.";
#elif UNITY_EDITOR
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

            if (!UnityGameServicesBootstrap.HasDurablePlayerAccount())
            {
                UsernamePasswordSignInScreen.Open(ResolveOverlayHost());
                if (_status != null)
                    _status.text = "Sign in with Google, or a username and password. The purchase stays on that account.";
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (_checkoutInFlight)
                return;
            _checkoutInFlight = true;
            if (_status != null)
                _status.text = "Opening checkout…";
            _ = BeginWebCheckoutAsync();
            return;
#endif

            var iap = Object.FindFirstObjectByType<TitanOrbitIapManager>();
#if UNITY_EDITOR
            // [EDITOR] Fake store often has no catalog. Grant onto the signed-in Unity player.
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

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>WebGL only. Replaces the store placeholder with the Stripe Price.</summary>
        async System.Threading.Tasks.Task LoadStripePriceAsync(int requestId)
        {
            string label = await TitanOrbit.Services.TitanOrbitWebCheckout.FetchDisplayPriceAsync();
            if (requestId != _priceRequest || _price == null)
                return;
            if (TitanOrbitEntitlements.IsOrbitUnlockedOwned || string.IsNullOrEmpty(label))
                return;
            _price.text = label;
        }

        /// <summary>WebGL only. Opens Stripe Checkout for the signed-in account.</summary>
        async System.Threading.Tasks.Task BeginWebCheckoutAsync()
        {
            // --- Web checkout ---
            string error = await TitanOrbit.Services.TitanOrbitWebCheckout.BeginAsync();
            _checkoutInFlight = false;
            Refresh();
            if (_status == null)
                return;
            if (!string.IsNullOrEmpty(error))
                _status.text = error;
        }
#endif

        /// <summary>Apple restore on iOS; Cloud Save re-read on WebGL.</summary>
        void OnRestoreClicked()
        {
            if (!UnityGameServicesBootstrap.HasDurablePlayerAccount())
            {
                if (_status != null)
                    _status.text = "Sign in before restoring Orbit Unlocked.";
                UsernamePasswordSignInScreen.Open(ResolveOverlayHost());
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (_status != null)
                _status.text = "Checking this account…";
            TitanOrbit.Services.TitanOrbitAccountEntitlementSync.RefreshForCurrentPlayerAsync();
            return;
#endif

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

        /// <summary>
        /// Compact deck card: title, three benefits, price, status, Unlock and Restore.
        /// Stretches the slot so the column height drives the card.
        /// </summary>
        void BuildEmbedCard()
        {
            var card = CreateUi("EmbedCard", transform, typeof(Image), typeof(Outline));
            StretchFull(card.GetComponent<RectTransform>());
            card.GetComponent<Image>().color = PanelFill;
            var outline = card.GetComponent<Outline>();
            outline.effectColor = new Color(AccentGold.r, AccentGold.g, AccentGold.b, 0.65f);
            outline.effectDistance = new Vector2(1.25f, -1.25f);

            var rail = CreateUi("TopRail", card.transform, typeof(Image));
            var railRt = rail.GetComponent<RectTransform>();
            railRt.anchorMin = new Vector2(0f, 1f);
            railRt.anchorMax = new Vector2(1f, 1f);
            railRt.pivot = new Vector2(0.5f, 1f);
            railRt.sizeDelta = new Vector2(0f, 3f);
            railRt.anchoredPosition = Vector2.zero;
            rail.GetComponent<Image>().color = AccentGold;
            rail.GetComponent<Image>().raycastTarget = false;

            var title = CreateTmp(card.transform, "Title", "ORBIT UNLOCKED", 18f, FontStyles.Bold);
            PlaceTop(title.rectTransform, 10f, 24f);
            title.alignment = TextAlignmentOptions.MidlineLeft;
            title.color = AccentGold;
            title.characterSpacing = 1.6f;

            var blurb = CreateTmp(
                card.transform,
                "Blurb",
                "No ads — keep your gear\nFull hangar — hulls, badges, jets\n+1 gear slot every match",
                13f,
                FontStyles.Bold);
            PlaceTop(blurb.rectTransform, 36f, 54f);
            blurb.alignment = TextAlignmentOptions.TopLeft;
            blurb.color = BodyColor;
            blurb.enableWordWrapping = true;

            _price = CreateTmp(card.transform, "Price", "", 16f, FontStyles.Bold);
            PlaceTop(_price.rectTransform, 94f, 22f);
            _price.alignment = TextAlignmentOptions.MidlineLeft;
            _price.color = AccentCyan;

            _status = CreateTmp(card.transform, "Status", "", 12f, FontStyles.Normal);
            PlaceTop(_status.rectTransform, 116f, 32f);
            _status.alignment = TextAlignmentOptions.TopLeft;
            _status.color = CaptionColor;
            _status.enableWordWrapping = true;

            _buyButton = CreateActionButton(
                card.transform,
                "Buy",
                "UNLOCK",
                new Vector2(-108f, 10f),
                BuyFill,
                OnBuyClicked);
            _buyLabel = _buyButton.GetComponentInChildren<TextMeshProUGUI>();

            CreateActionButton(
                card.transform,
                "Restore",
                "RESTORE",
                new Vector2(108f, 10f),
                RestoreFill,
                OnRestoreClicked);
        }

        /// <summary>Root canvas so the sign-in sheet is not clipped inside the deck slot.</summary>
        Transform ResolveOverlayHost()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                return transform.parent != null ? transform.parent : transform;
            var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            return root.transform;
        }

        /// <summary>Drops a leftover modal canvas so the card draws with the menu.</summary>
        static void StripOwnCanvas(GameObject go)
        {
            if (go == null)
                return;
            var ray = go.GetComponent<GraphicRaycaster>();
            if (ray != null)
                DestroyImmediate(ray);
            var scaler = go.GetComponent<CanvasScaler>();
            if (scaler != null)
                DestroyImmediate(scaler);
            var canvas = go.GetComponent<Canvas>();
            if (canvas != null)
                DestroyImmediate(canvas);
        }

        /// <summary>Same-frame wipe so a modal sheet cannot sit under the deck card.</summary>
        void ClearChildrenImmediate()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                DestroyImmediate(transform.GetChild(i).gameObject);
            _status = null;
            _price = null;
            _buyLabel = null;
            _buyButton = null;
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
