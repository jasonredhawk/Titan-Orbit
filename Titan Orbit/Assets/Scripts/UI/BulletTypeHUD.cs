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
    /// Compact left-side in-flight list of fire types the local ship can shoot. Each tile
    /// names the <see cref="BulletVfxBank"/> category (LASERBOLT) and the ship family
    /// that authored it (ASTRO EAGLE). Production shows the hull default plus purchased
    /// foreign weapons. GameManager cycle-all (Test) lists every non-reserved catalog
    /// bank so testers can click types they have not bought. B walks the same list; a
    /// tile click jumps to that bank.
    /// <para>
    /// Parks under <see cref="RocketLoadoutHUD"/> when that column is showing, or in the
    /// same mid-left slot when no rockets / mines are equipped. Grows downward.
    /// <see cref="SpaceBrakesHUD"/> docks under this strip. Writes nothing to ECS — clicks latch
    /// <see cref="BulletBankSelection"/>; <see cref="ShipCycleBulletSystem"/> applies the
    /// ghosted <see cref="ShipLoadoutState.RuntimeBulletIndex"/> on the predicted tick.
    /// Hidden on the main menu, Join Team, Orbit Menu, MEGA hulls, and while the local
    /// ship is dead. Holds last paint during
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> so MEGA plow gem
    /// Instantiates do not blink the panel off.
    /// </para>
    /// Dark space-gamer chrome — same void glass, caret, and outline as
    /// <see cref="RocketLoadoutHUD"/>.
    /// </summary>
    [DefaultExecutionOrder(66220)]
    public class BulletTypeHUD : MonoBehaviour
    {
        /// <summary>Hard cap. Catalog has 17 categories; store Rockets are skipped.</summary>
        const int MaxRows = 16;

        /// <summary>Button width in overlay pixels. Matches the rocket / mine tiles.</summary>
        const float TileWidth = 108f;

        /// <summary>
        /// Dense two-line tile. Smaller type than rockets so LASERBOLT / family names
        /// stay on one line instead of wrapping.
        /// </summary>
        const float TileHeight = 38f;

        /// <summary>Gap between stacked fire-type buttons.</summary>
        const float TileGap = 3f;

        /// <summary>Inset from the dark panel edge to the first tile.</summary>
        const float PanelPad = 6f;

        /// <summary>Thin ORDNANCE caption above the first tile.</summary>
        const float HeaderHeight = 14f;

        /// <summary>Panel width = tile + left/right pad.</summary>
        const float PanelWidth = TileWidth + PanelPad * 2f;

        /// <summary>Left inset shared with rockets and Space Brakes on the 1920×1080 overlay.</summary>
        const float OverlayLeft = 14f;

        /// <summary>Air between the rocket column bottom and this strip's top.</summary>
        const float DockGap = 8f;

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
        static readonly Color HeaderColor = new Color(0.55f, 0.72f, 0.88f, 0.7f);
        static readonly Color RuleColor = new Color(0.18f, 0.28f, 0.40f, 0.75f);
        static readonly Color KeycapFill = new Color(0.04f, 0.10f, 0.16f, 0.96f);
        static readonly Color KeycapIdle = new Color(0.04f, 0.08f, 0.12f, 0.55f);

        /// <summary>
        /// Live overlay instance. <see cref="SpaceBrakesHUD"/> docks under this strip
        /// after we LateUpdate.
        /// </summary>
        static BulletTypeHUD _instance;

        Canvas _canvas;
        RectTransform _panel;
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

        /// <summary>One fire-type button. Kind word + family live on the tile like ROCKET / MINE.</summary>
        sealed class BankTile
        {
            /// <summary>Root GameObject. Hidden when this slot has no bank.</summary>
            public GameObject Root;

            /// <summary>Anchored to the panel top so Paint can stack rows by Y.</summary>
            public RectTransform Rect;

            /// <summary>[UNITY] Click latches this bank for the predicted cycle system.</summary>
            public Button Button;

            /// <summary>Idle vs selected fill behind the labels.</summary>
            public Image Background;

            /// <summary>Left cyan rail. Enabled only on the live fire type.</summary>
            public Image Caret;

            /// <summary>Cyan edge. Enabled only on the live fire type.</summary>
            public Outline Outline;

            /// <summary>Bank category in rocket-style caps: LASERBOLT, PLASMA, …</summary>
            public TextMeshProUGUI KindLabel;

            /// <summary>B on the live tile; HEAL while heal mode locks B; TEST on catalog-only rows.</summary>
            public TextMeshProUGUI HintLabel;

            /// <summary>Dark keycap behind the hint so B reads as a bind, not body text.</summary>
            public Image HintChip;

            /// <summary>Family that authored this bank, e.g. ASTRO EAGLE.</summary>
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
            _instance = this;
            _familyConfig = PlanetShipFamilyConfig.LoadDefault();
            _bank = BulletVfxBank.LoadDefault();
            _lastCycleAll = TitanOrbitDebugFlags.CycleAllBulletBanks;
            BuildUi();
            SetVisible(false);
        }

        /// <summary>Drops the static dock pointer so brakes cannot sit under a destroyed panel.</summary>
        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>
        /// Bottom edge of the fire-type glass in the shared 1920×1080 overlay
        /// (left-center anchor space). <see cref="SpaceBrakesHUD"/> calls this after we
        /// LateUpdate so CTRL docks under this strip.
        /// </summary>
        /// <param name="y">Overlay Y of the panel bottom when visible, or 0 when hidden.</param>
        /// <param name="visible">True when at least one fire-type tile is showing.</param>
        /// <returns>True when this HUD exists and has a panel to measure.</returns>
        public static bool TryGetOverlayDockBottomY(out float y, out bool visible)
        {
            y = 0f;
            visible = false;
            if (_instance == null || _instance._panel == null)
                return false;

            visible = _instance._panel.gameObject.activeSelf;
            if (!visible)
                return true;

            y = RocketLoadoutHUD.OverlayPanelBottomY(_instance._panel);
            return true;
        }

        /// <summary>
        /// Refreshes the list from the local ship and docks under rockets. No ECS gathers
        /// during join Instantiates; combat gem bursts keep the last paint instead of
        /// blinking the panel off.
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

            if (!TryReadRows(out int rowCount, out int selectedBank, out bool healLocked, out string hullFamilyName))
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            Paint(rowCount, selectedBank, healLocked, hullFamilyName);
        }

        /// <summary>
        /// Reads owned / catalog rows and the live fire bank from the client-world local ship.
        /// </summary>
        /// <param name="rowCount">How many <see cref="_rowScratch"/> slots are valid.</param>
        /// <param name="selectedBank">Ghosted fire index (heal bank when heal mode is firing).</param>
        /// <param name="healLocked">True when Production heal mode ignores clicks (same as B).</param>
        /// <param name="hullFamilyName">Local ship family label for the hull-default tile.</param>
        /// <returns>True when at least one tile should paint.</returns>
        bool TryReadRows(out int rowCount, out int selectedBank, out bool healLocked, out string hullFamilyName)
        {
            rowCount = 0;
            selectedBank = 0;
            healLocked = false;
            hullFamilyName = string.Empty;

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

            // Hull tile uses THIS ship's family, not the first config row that shares the bank.
            if (em.HasComponent<ShipState>(ship))
            {
                if (_familyConfig == null)
                    _familyConfig = PlanetShipFamilyConfig.LoadDefault();
                if (_familyConfig != null)
                    hullFamilyName = _familyConfig.GetFamilyDisplayName(
                        em.GetComponentData<ShipState>(ship).ShipFamilyConfigIndex);
            }

            return true;
        }

        /// <summary>
        /// Stacks compact tiles for the current visible set, docks under rockets, and
        /// enables scroll only when Test cycle-all would cover Space Brakes.
        /// </summary>
        void Paint(int rowCount, int selectedBank, bool healLocked, string hullFamilyName)
        {
            bool cycleAll = TitanOrbitDebugFlags.CycleAllBulletBanks;
            if (cycleAll != _lastCycleAll)
                _lastCycleAll = cycleAll;

            _paintedCount = 0;

            // Heal / Test cycle-all can fire a bank that is not in this owned list.
            // Park the caret on the hull default so the strip still has a live row.
            int caretBank = selectedBank;
            bool caretInList = false;
            for (int i = 0; i < rowCount; i++)
            {
                if (_rowScratch[i].BankIndex == selectedBank)
                {
                    caretInList = true;
                    break;
                }
            }

            if (!caretInList && rowCount > 0)
                caretBank = _rowScratch[0].BankIndex;

            for (int i = 0; i < _tiles.Length; i++)
            {
                if (i >= rowCount)
                {
                    HideTile(_tiles[i]);
                    continue;
                }

                VisibleBankRow row = _rowScratch[i];
                bool isSelected = row.BankIndex == caretBank;
                PaintTile(_tiles[i], i, row, isSelected, healLocked, cycleAll, hullFamilyName);
                _paintedBanks[i] = row.BankIndex;
                _paintedCount++;
            }

            // --- Panel size ---
            // Owned list is short (hull + purchases). Scroll stays off unless the
            // loadout grows past the tile pool.
            float tilesHeight = rowCount <= 0
                ? 0f
                : rowCount * TileHeight + (rowCount - 1) * TileGap;
            float contentHeight = HeaderHeight + tilesHeight + PanelPad * 2f;
            ApplyDock(contentHeight);
        }

        /// <summary>
        /// Parks this strip under the rocket column, or in the mid-left slot when that
        /// column is hidden. Space Brakes docks under whatever height we settle on.
        /// </summary>
        /// <param name="contentHeight">Full stacked-tile height including panel pad.</param>
        void ApplyDock(float contentHeight)
        {
            if (_panel == null)
                return;

            float minHeight = HeaderHeight + TileHeight + PanelPad * 2f;
            bool rocketsVisible = false;
            float dockBottom = 0f;

            // Execution order 66220 runs after RocketLoadoutHUD (66200), so this
            // measurement already includes this frame's rocket / mine row count.
            if (RocketLoadoutHUD.TryGetOverlayDockBottomY(out dockBottom, out rocketsVisible) &&
                rocketsVisible)
            {
                // --- Under rockets ---
                // Top-left pivot: the strip hangs down from just below the rocket glass.
                _panel.pivot = new Vector2(0f, 1f);
                _panel.anchoredPosition = new Vector2(OverlayLeft, dockBottom - DockGap);
            }
            else
            {
                // --- Mid-left park ---
                // Same slot rockets use when they have packs. Pivot 0.5 grows equally
                // up and down from screen center.
                _panel.pivot = new Vector2(0f, 0.5f);
                _panel.anchoredPosition = new Vector2(OverlayLeft, 0f);
            }

            float panelHeight = Mathf.Max(minHeight, contentHeight);
            _panel.sizeDelta = new Vector2(PanelWidth, panelHeight);
        }

        /// <summary>
        /// Fills one fire-type button. Kind word on top (LASERBOLT), family under it,
        /// B / HEAL / TEST hint on the live row — same caret language as rockets.
        /// </summary>
        void PaintTile(
            BankTile tile,
            int row,
            VisibleBankRow data,
            bool isSelected,
            bool healLocked,
            bool cycleAll,
            string hullFamilyName)
        {
            if (tile == null || tile.Root == null)
                return;

            tile.Root.SetActive(true);
            tile.Rect.sizeDelta = new Vector2(TileWidth, TileHeight);
            tile.Rect.anchoredPosition = new Vector2(
                PanelPad,
                -PanelPad - HeaderHeight - row * (TileHeight + TileGap));

            string family = data.IsHullDefault && !string.IsNullOrEmpty(hullFamilyName)
                ? hullFamilyName
                : ResolveFamilyCaption(data.BankIndex);
            string category = ResolveCategoryName(data.BankIndex);
            bool unowned = cycleAll && !data.IsOwned;

            // Kind matches ROCKET / MINE: uppercase type word on the top row.
            tile.KindLabel.text = string.IsNullOrEmpty(category)
                ? "BANK " + data.BankIndex.ToString(CultureInfo.InvariantCulture)
                : category.ToUpperInvariant();

            if (string.IsNullOrEmpty(family))
                tile.DetailLabel.text = unowned ? "CATALOG" : string.Empty;
            else
                tile.DetailLabel.text = family.ToUpperInvariant();

            if (isSelected)
            {
                tile.KindLabel.color = CaptionSelected;
                tile.DetailLabel.color = BodyColor;
            }
            else if (unowned)
            {
                tile.KindLabel.color = CaptionUnowned;
                tile.DetailLabel.color = BodyDim;
            }
            else
            {
                tile.KindLabel.color = CaptionDim;
                tile.DetailLabel.color = BodyDim;
            }

            PaintHint(tile, isSelected, healLocked, unowned);

            tile.Background.color = isSelected ? RowSelected : RowIdle;
            if (tile.Caret != null)
                tile.Caret.enabled = isSelected;
            if (tile.Outline != null)
                tile.Outline.enabled = isSelected;
            if (tile.Button != null)
                tile.Button.interactable = !healLocked;
        }

        /// <summary>
        /// Hotkey hint only on the live type, like ALT on the focused rocket row.
        /// Unfocused owned rows stay quiet. Unowned Test rows keep a TEST tag.
        /// </summary>
        static void PaintHint(BankTile tile, bool isSelected, bool healLocked, bool unowned)
        {
            if (tile?.HintLabel == null)
                return;

            TextMeshProUGUI label = tile.HintLabel;
            bool showChip = false;
            Color chip = KeycapIdle;

            if (healLocked && isSelected)
            {
                label.text = "HEAL";
                label.color = HealColor;
                showChip = true;
                chip = KeycapFill;
            }
            else if (isSelected)
            {
                label.text = "B";
                label.color = ReadyColor;
                showChip = true;
                chip = KeycapFill;
            }
            else if (unowned)
            {
                label.text = "TEST";
                label.color = TestColor;
                showChip = true;
                chip = KeycapIdle;
            }
            else
            {
                label.text = string.Empty;
            }

            if (tile.HintChip != null)
            {
                tile.HintChip.enabled = showChip;
                tile.HintChip.color = chip;
                var chipRt = tile.HintChip.rectTransform;
                bool wide = label.text != null && label.text.Length > 1;
                chipRt.sizeDelta = new Vector2(wide ? 26f : 16f, 12f);
            }
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
        /// Builds dark-glass canvas and a pool of tappable fire-type buttons parented
        /// to the panel (same as rockets). A Mask + Color.clear viewport used to hide
        /// every tile. Starts in the mid-left rocket slot; LateUpdate docks under rockets.
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
            _panel.anchorMin = new Vector2(0f, 0.5f);
            _panel.anchorMax = new Vector2(0f, 0.5f);
            _panel.pivot = new Vector2(0f, 0.5f);
            _panel.anchoredPosition = new Vector2(OverlayLeft, 0f);
            _panel.sizeDelta = new Vector2(PanelWidth, TileHeight + PanelPad * 2f);
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

            var header = CreateLabel(_panel, "Header", "ORDNANCE", 7.5f, HeaderColor, TextAlignmentOptions.Left);
            var headerRt = header.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.anchoredPosition = new Vector2(0f, -2f);
            headerRt.sizeDelta = new Vector2(-16f, HeaderHeight);
            header.characterSpacing = 2.4f;

            var ruleGo = new GameObject("HeaderRule", typeof(RectTransform), typeof(Image));
            ruleGo.transform.SetParent(_panel, false);
            var ruleRt = ruleGo.GetComponent<RectTransform>();
            ruleRt.anchorMin = new Vector2(0f, 1f);
            ruleRt.anchorMax = new Vector2(1f, 1f);
            ruleRt.pivot = new Vector2(0.5f, 1f);
            ruleRt.anchoredPosition = new Vector2(0f, -HeaderHeight);
            ruleRt.sizeDelta = new Vector2(-16f, 1f);
            var ruleImg = ruleGo.GetComponent<Image>();
            ruleImg.color = RuleColor;
            ruleImg.raycastTarget = false;

            for (int i = 0; i < MaxRows; i++)
                _tiles[i] = BuildTile(_panel, i);
        }

        /// <summary>
        /// Builds one fire-type button: kind word, B/HEAL/TEST hint, family caption.
        /// </summary>
        /// <param name="parent">Dark panel that holds the stacked list.</param>
        /// <param name="index">Pool index and click identity (0..MaxRows-1).</param>
        BankTile BuildTile(RectTransform parent, int index)
        {
            int captured = index;
            var tile = new BankTile();

            var rowGo = new GameObject($"Tile{index}", typeof(RectTransform), typeof(Image), typeof(Button));
            rowGo.transform.SetParent(parent, false);
            var rt = rowGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(PanelPad, -PanelPad - HeaderHeight - index * (TileHeight + TileGap));
            rt.sizeDelta = new Vector2(TileWidth, TileHeight);
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
            caretRt.sizeDelta = new Vector2(3f, 0f);
            caretRt.anchoredPosition = Vector2.zero;
            var caretImg = caretGo.GetComponent<Image>();
            caretImg.color = CaretColor;
            caretImg.raycastTarget = false;
            caretImg.enabled = false;
            tile.Caret = caretImg;

            // Kind uses most of the row; the B keycap is a tight 18px chip on the right
            // so LASERBOLT no longer wraps into the bind.
            var kind = CreateLabel(rt, "Kind", "LASERBOLT", 9.5f, CaptionSelected, TextAlignmentOptions.Left);
            var kindRt = kind.rectTransform;
            kindRt.anchorMin = new Vector2(0f, 0.46f);
            kindRt.anchorMax = new Vector2(1f, 1f);
            kindRt.offsetMin = new Vector2(8f, 0f);
            kindRt.offsetMax = new Vector2(-22f, -1f);
            kind.characterSpacing = 0.4f;
            tile.KindLabel = kind;

            var chipGo = new GameObject("HintChip", typeof(RectTransform), typeof(Image));
            chipGo.transform.SetParent(rt, false);
            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(1f, 1f);
            chipRt.anchorMax = new Vector2(1f, 1f);
            chipRt.pivot = new Vector2(1f, 1f);
            chipRt.anchoredPosition = new Vector2(-3f, -3f);
            chipRt.sizeDelta = new Vector2(16f, 12f);
            var chipImg = chipGo.GetComponent<Image>();
            chipImg.color = KeycapFill;
            chipImg.raycastTarget = false;
            chipImg.enabled = false;
            tile.HintChip = chipImg;

            var hint = CreateLabel(chipRt, "Hint", "B", 8f, ReadyColor, TextAlignmentOptions.Center);
            Stretch(hint.rectTransform);
            hint.characterSpacing = 0f;
            tile.HintLabel = hint;

            var detail = CreateLabel(rt, "Detail", "ASTRO EAGLE", 7.5f, BodyColor, TextAlignmentOptions.Left);
            var detailRt = detail.rectTransform;
            detailRt.anchorMin = new Vector2(0f, 0f);
            detailRt.anchorMax = new Vector2(1f, 0.48f);
            detailRt.offsetMin = new Vector2(8f, 2f);
            detailRt.offsetMax = new Vector2(-4f, 0f);
            detail.characterSpacing = 0.8f;
            tile.DetailLabel = detail;

            rowGo.SetActive(false);
            return tile;
        }

        /// <summary>
        /// Cyan edge around the live tile so B / click have a clear caret beyond the fill tint.
        /// </summary>
        static Outline AddFocusOutline(GameObject rowGo)
        {
            var outline = rowGo.AddComponent<Outline>();
            outline.effectColor = CaretColor;
            outline.effectDistance = new Vector2(1f, -1f);
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
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.fontSizeMin = 6f;
            tmp.outlineWidth = 0.18f;
            tmp.outlineColor = LabelOutline;
            ApplyHudFont(tmp);
            return tmp;
        }

        /// <summary>Fills the parent rect so the B glyph sits in the keycap.</summary>
        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Prefers Shift Rajdhani so this strip matches tooltip / orbit chrome.</summary>
        static void ApplyHudFont(TextMeshProUGUI tmp)
        {
            if (tmp == null)
                return;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
        }
    }
}
