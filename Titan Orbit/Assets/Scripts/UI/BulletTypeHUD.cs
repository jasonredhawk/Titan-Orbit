using System.Globalization;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Left-side in-flight list of fire types the local ship can shoot. Each tile names the
    /// ship family (ASTRO EAGLE) and the <see cref="BulletVfxBank"/> category (Laserbolt).
    /// Production shows the hull default plus purchased foreign weapons. GameManager
    /// cycle-all (Test) lists every non-reserved catalog bank so testers can click types
    /// they have not bought. B still walks the same list; a tile click jumps to that bank.
    /// <para>
    /// [TITAN-ORBIT] Writes nothing to ECS. Clicks latch <see cref="BulletBankSelection"/>;
    /// <see cref="ShipCycleBulletSystem"/> applies the ghosted
    /// <see cref="ShipLoadoutState.RuntimeBulletIndex"/> on the predicted tick.
    /// Hidden on the main menu, Join Team, Orbit Menu, MEGA hulls, and while the local
    /// ship is dead. Holds last paint during
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> so MEGA plow gem
    /// Instantiates do not blink the panel off.
    /// </para>
    /// Dark space-gamer chrome — same void glass as <see cref="RocketLoadoutHUD"/>.
    /// </summary>
    [DefaultExecutionOrder(66190)]
    public class BulletTypeHUD : MonoBehaviour
    {
        /// <summary>Hard cap. Catalog has 17 categories; store Rockets are skipped.</summary>
        const int MaxRows = 16;

        /// <summary>Button width in overlay pixels. Matches the rocket / mine tiles.</summary>
        const float TileWidth = 108f;

        /// <summary>Comfortable height when only a few owned types are showing.</summary>
        const float TileHeightNormal = 52f;

        /// <summary>Shorter height when cycle-all lists the whole catalog.</summary>
        const float TileHeightCompact = 28f;

        /// <summary>Gap between stacked fire-type buttons.</summary>
        const float TileGap = 3f;

        /// <summary>Inset from the dark panel edge to the first tile.</summary>
        const float PanelPad = 8f;

        /// <summary>Panel width = tile + left/right pad.</summary>
        const float PanelWidth = TileWidth + PanelPad * 2f;

        /// <summary>
        /// Max glass height on a 1080 reference so cycle-all does not run off the top.
        /// Extra rows scroll inside this viewport.
        /// </summary>
        const float MaxPanelHeight = 380f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.92f);
        static readonly Color CaptionSelected = new Color(0.95f, 0.98f, 1f, 1f);
        static readonly Color CaptionDim = new Color(0.62f, 0.78f, 0.95f, 0.55f);
        static readonly Color CaptionUnowned = new Color(0.55f, 0.62f, 0.72f, 0.45f);
        static readonly Color BodyColor = new Color(0.94f, 0.97f, 1f, 1f);
        static readonly Color BodyDim = new Color(0.88f, 0.92f, 0.98f, 0.5f);
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color HealColor = new Color(0.49f, 1f, 0.70f, 1f);
        static readonly Color TestColor = new Color(0.95f, 0.72f, 0.28f, 0.85f);
        static readonly Color RowIdle = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color RowSelected = new Color(0.04f, 0.10f, 0.20f, 0.94f);
        static readonly Color CaretColor = new Color(0.45f, 0.95f, 1f, 1f);
        static readonly Color LabelOutline = new Color(0.02f, 0.04f, 0.08f, 0.95f);

        Canvas _canvas;
        RectTransform _panel;
        RectTransform _content;
        ScrollRect _scroll;
        GameObject _mainMenuPanel;
        PlanetShipFamilyConfig _familyConfig;
        BulletVfxBank _bank;

        /// <summary>Bank indices painted this frame so clicks map a row to a category.</summary>
        readonly int[] _paintedBanks = new int[MaxRows];

        /// <summary>How many tiles Paint filled (and how many clicks are valid).</summary>
        int _paintedCount;

        /// <summary>Last GameManager cycle-all value so a live Inspector flip rebuilds the list.</summary>
        bool _lastCycleAll;

        readonly BankTile[] _tiles = new BankTile[MaxRows];
        readonly VisibleBankRow[] _rowScratch = new VisibleBankRow[MaxRows];

        /// <summary>One fire-type button. Family name + bank category live on the tile.</summary>
        sealed class BankTile
        {
            /// <summary>Root GameObject. Hidden when this slot has no bank.</summary>
            public GameObject Root;

            /// <summary>Anchored to the scroll content so Paint can stack rows by Y.</summary>
            public RectTransform Rect;

            /// <summary>[UNITY] Click latches this bank for the predicted cycle system.</summary>
            public Button Button;

            /// <summary>Idle vs selected fill behind the labels.</summary>
            public Image Background;

            /// <summary>Left cyan rail. Enabled only on the active fire type.</summary>
            public Image Caret;

            /// <summary>Cyan edge. Enabled only on the active fire type.</summary>
            public Outline Outline;

            /// <summary>Player-facing family word: ASTRO EAGLE, COSMIC SHARK, …</summary>
            public TextMeshProUGUI FamilyLabel;

            /// <summary>B on the active tile; HEAL while heal mode locks B; TEST on catalog-only rows.</summary>
            public TextMeshProUGUI HintLabel;

            /// <summary>Bank category, e.g. Laserbolt or Plasma.</summary>
            public TextMeshProUGUI DetailLabel;
        }

        /// <summary>[UNITY] Creates the HUD once after the first scene load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<BulletTypeHUD>() != null)
                return;

            var go = new GameObject(nameof(BulletTypeHUD));
            DontDestroyOnLoad(go);
            go.AddComponent<BulletTypeHUD>();
        }

        /// <summary>Builds the left-side overlay canvas and empty fire-type rows.</summary>
        void Awake()
        {
            _familyConfig = PlanetShipFamilyConfig.LoadDefault();
            _bank = BulletVfxBank.LoadDefault();
            _lastCycleAll = TitanOrbitDebugFlags.CycleAllBulletBanks;
            BuildUi();
            SetVisible(false);
        }

        /// <summary>
        /// Refreshes the list from the local ship. No ECS gathers during join Instantiates;
        /// combat gem bursts keep the last paint instead of blinking the panel off.
        /// </summary>
        void LateUpdate()
        {
            // --- Menu / team pick / Orbit Menu ---
            // [TITAN-ORBIT] Same suppress as rockets. Cycle-all debug must not paint
            // this overlay on Main Menu or Join Team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                IsMainMenuShowing() ||
                MoonOrbitClientState.IsOrbitMenuVisible ||
                HUDController.LocalPlayerDeathHidesHud ||
                HUDController.MinimapExpandedObscuresHud)
            {
                SetVisible(false);
                return;
            }

            // MEGA volleys each fire a baked catalog bank — B and this list do not apply.
            if (EcsGameBridge.TryGetLocalMegaShipState(out MegaShipState mega) && mega.IsMega)
            {
                SetVisible(false);
                return;
            }

            // --- Instantiates gate: hold last paint, do not hide ---
            // [TITAN-ORBIT] ShouldSkipShipEntityQueries is also true mid-combat when MEGA plow
            // Instantiates gem ghosts. SetVisible(false) here blinked the left-side list.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (_panel != null &&
                    _panel.gameObject.activeSelf &&
                    (EcsGameBridge.HasLocalPlayerShip() || ShipDisplayPose.HasLocalPose))
                    return;

                SetVisible(false);
                return;
            }

            if (!EcsGameBridge.HasLocalPlayerShip())
            {
                SetVisible(false);
                return;
            }

            if (!TryReadRows(out int rowCount, out int selectedBank, out bool healLocked))
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            Paint(rowCount, selectedBank, healLocked);
        }

        /// <summary>
        /// Reads owned / catalog rows and the live fire bank from the client-world local ship.
        /// </summary>
        /// <param name="rowCount">How many <see cref="_rowScratch"/> slots are valid.</param>
        /// <param name="selectedBank">Ghosted fire index (heal bank when heal mode is firing).</param>
        /// <param name="healLocked">True when Production heal mode ignores clicks (same as B).</param>
        /// <returns>True when at least one tile should paint.</returns>
        bool TryReadRows(out int rowCount, out int selectedBank, out bool healLocked)
        {
            rowCount = 0;
            selectedBank = 0;
            healLocked = false;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) ||
                ship == Entity.Null)
                return false;

            var em = world.EntityManager;
            rowCount = BulletBankOwnership.CollectVisibleBankRows(em, ship, _rowScratch);
            if (rowCount <= 0)
                return false;

            if (em.HasComponent<ShipLoadoutState>(ship))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(ship);
                selectedBank = BulletBankFireResolve.ResolveFireBankIndex(in loadout);
                healLocked = loadout.HealingBulletsActive && !TitanOrbitDebugFlags.CycleAllBulletBanks;
            }

            return true;
        }

        /// <summary>
        /// Stacks tiles for the current visible set. Compact height when cycle-all lists
        /// the catalog so the glass stays on-screen; extra rows scroll.
        /// </summary>
        void Paint(int rowCount, int selectedBank, bool healLocked)
        {
            bool cycleAll = TitanOrbitDebugFlags.CycleAllBulletBanks;
            if (cycleAll != _lastCycleAll)
                _lastCycleAll = cycleAll;

            float tileHeight = rowCount > 5 ? TileHeightCompact : TileHeightNormal;
            _paintedCount = 0;

            for (int i = 0; i < _tiles.Length; i++)
            {
                if (i >= rowCount)
                {
                    HideTile(_tiles[i]);
                    continue;
                }

                VisibleBankRow row = _rowScratch[i];
                bool isSelected = row.BankIndex == selectedBank;
                PaintTile(_tiles[i], i, row, tileHeight, isSelected, healLocked, cycleAll);
                _paintedBanks[i] = row.BankIndex;
                _paintedCount++;
            }

            // --- Panel / scroll size ---
            // Content grows with every tile. The glass viewport caps at MaxPanelHeight
            // so a 16-row Test list scrolls instead of covering the speedometer.
            float tilesHeight = rowCount <= 0
                ? 0f
                : rowCount * tileHeight + (rowCount - 1) * TileGap;
            float contentHeight = tilesHeight + PanelPad * 2f;
            float panelHeight = Mathf.Min(MaxPanelHeight, Mathf.Max(tileHeight + PanelPad * 2f, contentHeight));

            if (_content != null)
                _content.sizeDelta = new Vector2(TileWidth, contentHeight);

            if (_panel != null)
                _panel.sizeDelta = new Vector2(PanelWidth, panelHeight);

            if (_scroll != null)
                _scroll.enabled = contentHeight > panelHeight + 0.5f;
        }

        /// <summary>
        /// Fills one fire-type button. Family word on top, bank category under it, B / HEAL / TEST hint.
        /// </summary>
        void PaintTile(
            BankTile tile,
            int row,
            VisibleBankRow data,
            float tileHeight,
            bool isSelected,
            bool healLocked,
            bool cycleAll)
        {
            if (tile == null || tile.Root == null)
                return;

            tile.Root.SetActive(true);
            tile.Rect.sizeDelta = new Vector2(TileWidth, tileHeight);
            tile.Rect.anchoredPosition = new Vector2(0f, -PanelPad - row * (tileHeight + TileGap));

            string family = ResolveFamilyCaption(data.BankIndex);
            string category = ResolveCategoryName(data.BankIndex);
            bool unowned = cycleAll && !data.IsOwned;

            if (string.IsNullOrEmpty(family))
            {
                tile.FamilyLabel.text = category.ToUpperInvariant();
                tile.DetailLabel.text = unowned ? "CATALOG" : category;
            }
            else
            {
                tile.FamilyLabel.text = family.ToUpperInvariant();
                tile.DetailLabel.text = category;
            }

            if (isSelected)
            {
                tile.FamilyLabel.color = CaptionSelected;
                tile.DetailLabel.color = BodyColor;
            }
            else if (unowned)
            {
                tile.FamilyLabel.color = CaptionUnowned;
                tile.DetailLabel.color = BodyDim;
            }
            else
            {
                tile.FamilyLabel.color = CaptionDim;
                tile.DetailLabel.color = BodyDim;
            }

            PaintHint(tile.HintLabel, isSelected, healLocked, unowned);

            tile.Background.color = isSelected ? RowSelected : RowIdle;
            if (tile.Caret != null)
                tile.Caret.enabled = isSelected;
            if (tile.Outline != null)
                tile.Outline.enabled = isSelected;
            if (tile.Button != null)
                tile.Button.interactable = !healLocked;
        }

        /// <summary>B on the live type; HEAL when Production heal locks the list; TEST on catalog-only rows.</summary>
        static void PaintHint(TextMeshProUGUI label, bool isSelected, bool healLocked, bool unowned)
        {
            if (label == null)
                return;

            if (healLocked && isSelected)
            {
                label.text = "HEAL";
                label.color = HealColor;
                return;
            }

            if (isSelected)
            {
                label.text = "B";
                label.color = ReadyColor;
                return;
            }

            if (unowned)
            {
                label.text = "TEST";
                label.color = TestColor;
                return;
            }

            label.text = string.Empty;
        }

        /// <summary>Family label whose default gun is this bank, or empty when none authored it.</summary>
        string ResolveFamilyCaption(int bankIndex)
        {
            if (_familyConfig == null)
                _familyConfig = PlanetShipFamilyConfig.LoadDefault();
            if (_familyConfig == null)
                return string.Empty;
            return _familyConfig.GetFamilyDisplayNameForDefaultBank(bankIndex);
        }

        /// <summary>Category name from the VFX bank (Laserbolt, Plasma, …).</summary>
        string ResolveCategoryName(int bankIndex)
        {
            if (_bank == null)
                _bank = BulletVfxBank.LoadDefault();
            if (_bank == null)
                return "Bank " + bankIndex.ToString(CultureInfo.InvariantCulture);
            string name = _bank.GetCategoryName(bankIndex);
            return string.IsNullOrEmpty(name)
                ? "Bank " + bankIndex.ToString(CultureInfo.InvariantCulture)
                : name;
        }

        /// <summary>Turns a spare row off so unused MaxRows slots do not show empty chrome.</summary>
        static void HideTile(BankTile tile)
        {
            if (tile == null || tile.Root == null)
                return;

            tile.Root.SetActive(false);
            if (tile.Outline != null)
                tile.Outline.enabled = false;
            if (tile.Caret != null)
                tile.Caret.enabled = false;
        }

        /// <summary>True while the scene Main Menu panel is up (Play / Join Game).</summary>
        bool IsMainMenuShowing()
        {
            if (_mainMenuPanel == null)
                _mainMenuPanel = GameObject.Find("MainMenuPanel");
            return _mainMenuPanel != null && _mainMenuPanel.activeInHierarchy;
        }

        /// <summary>
        /// Shows or hides the fire-type panel only. Never disables the Canvas — Orbit Menu
        /// must not share a disabled overlay.
        /// </summary>
        void SetVisible(bool visible)
        {
            if (_canvas != null)
                _canvas.enabled = true;
            if (_panel != null)
                _panel.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Click a row to fire that bank. Production heal mode ignores clicks (same as B).
        /// The server still validates ownership / cycle-all before writing the ghost field.
        /// </summary>
        void OnRowClicked(int hudIndex)
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;
            if (PlanetaryDefenseTurretClientState.IsControlling)
                return;
            if (hudIndex < 0 || hudIndex >= _paintedCount)
                return;

            int bankIndex = _paintedBanks[hudIndex];
            if (bankIndex < 0)
                return;

            // Production heal locks B — do not send a set that the cycle system will ignore.
            if (!TitanOrbitDebugFlags.CycleAllBulletBanks &&
                EcsGameBridge.TryGetLocalShipLoadout(out ShipLoadoutState loadout) &&
                loadout.HealingBulletsActive)
                return;

            BulletBankSelection.Request(bankIndex);
        }

        /// <summary>
        /// Builds dark-glass canvas, a scroll viewport, and a pool of tappable fire-type buttons.
        /// Parked above the rocket list so UP/DOWN stay on rockets / mines.
        /// </summary>
        void BuildUi()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            gameObject.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.GetComponent<RectTransform>();
            // Bottom-left pivot sits just above the rocket column (mid-left at y = 0).
            _panel.anchorMin = new Vector2(0f, 0.5f);
            _panel.anchorMax = new Vector2(0f, 0.5f);
            _panel.pivot = new Vector2(0f, 0f);
            _panel.anchoredPosition = new Vector2(14f, 92f);
            _panel.sizeDelta = new Vector2(PanelWidth, TileHeightNormal + PanelPad * 2f);
            var bg = panelGo.GetComponent<Image>();
            bg.color = FillColor;
            bg.raycastTarget = true;

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentGo.transform.SetParent(_panel, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(3f, 0f);
            accentRt.anchoredPosition = Vector2.zero;
            accentGo.GetComponent<Image>().color = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(0);
            accentGo.GetComponent<Image>().raycastTarget = false;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportGo.transform.SetParent(_panel, false);
            var viewportRt = viewportGo.GetComponent<RectTransform>();
            viewportRt.anchorMin = Vector2.zero;
            viewportRt.anchorMax = Vector2.one;
            viewportRt.offsetMin = Vector2.zero;
            viewportRt.offsetMax = Vector2.zero;
            var viewportImg = viewportGo.GetComponent<Image>();
            viewportImg.color = Color.clear;
            viewportImg.raycastTarget = true;
            viewportGo.GetComponent<Mask>().showMaskGraphic = false;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            _content = contentGo.GetComponent<RectTransform>();
            _content.anchorMin = new Vector2(0.5f, 1f);
            _content.anchorMax = new Vector2(0.5f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = new Vector2(TileWidth, TileHeightNormal + PanelPad * 2f);

            _scroll = panelGo.AddComponent<ScrollRect>();
            _scroll.viewport = viewportRt;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 24f;

            for (int i = 0; i < MaxRows; i++)
                _tiles[i] = BuildTile(_content, i);
        }

        /// <summary>
        /// Builds one fire-type button: family word, B/HEAL/TEST hint, bank category.
        /// </summary>
        /// <param name="parent">Scroll content that holds the stacked list.</param>
        /// <param name="index">Pool index and click identity (0..MaxRows-1).</param>
        BankTile BuildTile(RectTransform parent, int index)
        {
            int captured = index;
            var tile = new BankTile();

            var rowGo = new GameObject($"Tile{index}", typeof(RectTransform), typeof(Image), typeof(Button));
            rowGo.transform.SetParent(parent, false);
            var rt = rowGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -PanelPad - index * (TileHeightNormal + TileGap));
            rt.sizeDelta = new Vector2(TileWidth, TileHeightNormal);
            var img = rowGo.GetComponent<Image>();
            img.color = RowIdle;
            var btn = rowGo.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => OnRowClicked(captured));

            tile.Root = rowGo;
            tile.Rect = rt;
            tile.Button = btn;
            tile.Background = img;
            tile.Outline = AddFocusOutline(rowGo);

            var caretGo = new GameObject("Caret", typeof(RectTransform), typeof(Image));
            caretGo.transform.SetParent(rt, false);
            var caretRt = caretGo.GetComponent<RectTransform>();
            caretRt.anchorMin = new Vector2(0f, 0f);
            caretRt.anchorMax = new Vector2(0f, 1f);
            caretRt.pivot = new Vector2(0f, 0.5f);
            caretRt.sizeDelta = new Vector2(4f, 0f);
            caretRt.anchoredPosition = Vector2.zero;
            var caretImg = caretGo.GetComponent<Image>();
            caretImg.color = CaretColor;
            caretImg.raycastTarget = false;
            caretImg.enabled = false;
            tile.Caret = caretImg;

            var family = CreateLabel(rt, "Family", "ASTRO EAGLE", 11f, CaptionSelected, TextAlignmentOptions.Left);
            var familyRt = family.rectTransform;
            familyRt.anchorMin = new Vector2(0f, 0.48f);
            familyRt.anchorMax = new Vector2(0.68f, 1f);
            familyRt.offsetMin = new Vector2(10f, 0f);
            familyRt.offsetMax = new Vector2(-2f, -2f);
            tile.FamilyLabel = family;

            var hint = CreateLabel(rt, "Hint", "B", 11f, ReadyColor, TextAlignmentOptions.Right);
            var hintRt = hint.rectTransform;
            hintRt.anchorMin = new Vector2(0.58f, 0.48f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.offsetMin = new Vector2(0f, 0f);
            hintRt.offsetMax = new Vector2(-6f, -2f);
            tile.HintLabel = hint;

            var detail = CreateLabel(rt, "Detail", "Laserbolt", 12f, BodyColor, TextAlignmentOptions.Left);
            var detailRt = detail.rectTransform;
            detailRt.anchorMin = new Vector2(0f, 0f);
            detailRt.anchorMax = new Vector2(1f, 0.52f);
            detailRt.offsetMin = new Vector2(10f, 2f);
            detailRt.offsetMax = new Vector2(-6f, 0f);
            detail.enableWordWrapping = false;
            tile.DetailLabel = detail;

            rowGo.SetActive(false);
            return tile;
        }

        /// <summary>
        /// Cyan edge around the active tile so B / click have a clear caret beyond the fill tint.
        /// </summary>
        static Outline AddFocusOutline(GameObject rowGo)
        {
            var outline = rowGo.AddComponent<Outline>();
            outline.effectColor = CaretColor;
            outline.effectDistance = new Vector2(2f, -2f);
            outline.useGraphicAlpha = false;
            outline.enabled = false;
            return outline;
        }

        /// <summary>Creates a TMP label under <paramref name="parent"/>.</summary>
        static TextMeshProUGUI CreateLabel(
            Transform parent,
            string name,
            string text,
            float size,
            Color color,
            TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(-16f, 18f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.outlineWidth = 0.22f;
            tmp.outlineColor = LabelOutline;
            return tmp;
        }
    }
}
