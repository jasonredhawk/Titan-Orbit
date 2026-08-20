using System.Collections.Generic;
using System.Globalization;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Left-middle in-flight list of equipped rocket and mine packs as one column of
    /// gear-slot buttons. Each button names the pack (ROCKET or MINE) and prints
    /// level, damage, and remaining shots. There is no separate ROCKETS / MINES
    /// header — the words live on the tiles so a mixed loadout reads as one list.
    /// UP / DOWN (and click) walk the combined list as one caret. ALT activates
    /// only the focused pack (fire rocket or place mine). Hidden on the main menu,
    /// Join Team, Orbit Menu, and while the local ship is dead.
    /// <para>
    /// [TITAN-ORBIT] Reads the local ship's ghosted <see cref="EquippedEquipmentElement"/>
    /// buffer plus <see cref="ShipLoadoutState.NextRocketFireTime"/> and
    /// <see cref="ShipLoadoutState.NextMinePlaceTime"/>. Skips ship gathers
    /// while <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> (Join Team Crash!!!)
    /// but keeps the last paint visible — MEGA plow gem Instantiates used to hide this list
    /// for a frame every rock.
    /// </para>
    /// Dark space-gamer chrome — same void glass as <see cref="ShipStatTooltipChrome"/>.
    /// </summary>
    [DefaultExecutionOrder(66200)]
    public class RocketLoadoutHUD : MonoBehaviour
    {
        /// <summary>Hard cap on visible packs. Matches a typical ship loadout plus headroom.</summary>
        const int MaxRows = 8;

        /// <summary>Button width in overlay pixels. Fits "ROCKET" plus an ALT / cooldown hint.</summary>
        const float TileWidth = 108f;

        /// <summary>Button height for kind + level + damage/charges stacked inside the tile.</summary>
        const float TileHeight = 62f;

        /// <summary>Gap between stacked gear-slot buttons.</summary>
        const float TileGap = 4f;

        /// <summary>Inset from the dark panel edge to the first tile.</summary>
        const float PanelPad = 8f;

        /// <summary>Panel width = tile + left/right pad. Height grows with pack count.</summary>
        const float PanelWidth = TileWidth + PanelPad * 2f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.92f);

        /// <summary>
        /// Focused kind word. Near-white so ROCKET / MINE stays readable on the dark
        /// selected fill. Mid cyan-on-cyan used to disappear into the old highlight.
        /// </summary>
        static readonly Color CaptionSelected = new Color(0.95f, 0.98f, 1f, 1f);

        static readonly Color CaptionDim = new Color(0.62f, 0.78f, 0.95f, 0.55f);
        static readonly Color BodyColor = new Color(0.94f, 0.97f, 1f, 1f);
        static readonly Color BodyDim = new Color(0.88f, 0.92f, 0.98f, 0.5f);
        static readonly Color LevelColor = new Color(0.75f, 0.97f, 1f, 1f);
        static readonly Color LevelDim = new Color(0.55f, 0.95f, 1f, 0.55f);
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color WaitColor = new Color(0.95f, 0.72f, 0.28f, 1f);
        static readonly Color WaitDim = new Color(0.95f, 0.72f, 0.28f, 0.45f);
        static readonly Color RowIdle = new Color(1f, 1f, 1f, 0.03f);

        /// <summary>
        /// Focused tile fill. Stays dark navy — cyan wash at ~50% alpha sat on top of
        /// cyan labels and hid ROCKET. Caret + outline carry the "this is active" cue.
        /// </summary>
        static readonly Color RowSelected = new Color(0.04f, 0.10f, 0.20f, 0.94f);

        static readonly Color CaretColor = new Color(0.45f, 0.95f, 1f, 1f);
        static readonly Color LabelOutline = new Color(0.02f, 0.04f, 0.08f, 0.95f);

        Canvas _canvas;
        RectTransform _panel;
        GameObject _mainMenuPanel;

        /// <summary>
        /// Rocket pack count from the last successful <see cref="Paint"/>. Click handlers
        /// use this to decide whether a row index is a rocket or a mine.
        /// </summary>
        int _paintedRocketCount;

        readonly List<PackTile> _tiles = new List<PackTile>(MaxRows);

        /// <summary>
        /// One gear-slot button. Kind, level, and damage live on the tile so rockets
        /// and mines can share a single column without section headers.
        /// </summary>
        sealed class PackTile
        {
            /// <summary>Root GameObject. Hidden when this slot has no pack.</summary>
            public GameObject Root;

            /// <summary>Anchored to the panel top so Paint can stack rows by Y.</summary>
            public RectTransform Rect;

            /// <summary>[UNITY] Click focuses this pack. ALT still fires / places.</summary>
            public Button Button;

            /// <summary>Idle vs selected fill behind the labels.</summary>
            public Image Background;

            /// <summary>Left cyan rail. Enabled only on the focused pack.</summary>
            public Image Caret;

            /// <summary>Cyan edge. Enabled only on the focused pack.</summary>
            public Outline Outline;

            /// <summary>Player-facing type word: ROCKET or MINE.</summary>
            public TextMeshProUGUI KindLabel;

            /// <summary>ALT when this pack is focused and ready; otherwise remaining seconds.</summary>
            public TextMeshProUGUI HintLabel;

            /// <summary>Pack purchase level, e.g. "Lv 1".</summary>
            public TextMeshProUGUI LevelLabel;

            /// <summary>Damage and remaining charges, e.g. "40  ×2".</summary>
            public TextMeshProUGUI DetailLabel;

            /// <summary>Shrinking cooldown fill. Visible on this tile while that weapon reloads.</summary>
            public RectTransform CooldownFill;

            /// <summary>Fill tint: green ready, amber waiting.</summary>
            public Image CooldownFillImage;

            /// <summary>Dark track under the fill. Hidden when this pack type is ready.</summary>
            public GameObject CooldownTrack;
        }

        /// <summary>[UNITY] Creates the HUD once after the first scene load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<RocketLoadoutHUD>() != null)
                return;

            var go = new GameObject(nameof(RocketLoadoutHUD));
            DontDestroyOnLoad(go);
            go.AddComponent<RocketLoadoutHUD>();
        }

        /// <summary>Builds the left-middle overlay canvas and empty gear-slot rows.</summary>
        void Awake()
        {
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
            // [TITAN-ORBIT] Same suppress as speedometer. Infinite-rocket debug must not
            // paint this overlay on Main Menu or Join Team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                IsMainMenuShowing() ||
                MoonOrbitClientState.IsOrbitMenuVisible ||
                HUDController.LocalPlayerDeathHidesHud ||
                HUDController.MinimapExpandedObscuresHud)
            {
                SetVisible(false);
                return;
            }

            // --- Instantiates gate: hold last paint, do not hide ---
            // [TITAN-ORBIT] ShouldSkipShipEntityQueries is also true mid-combat when MEGA plow
            // (or any asteroid kill) Instantiates gem ghosts. SetVisible(false) here blinked
            // the left-side list every collision. Queries stay skipped (Join Team Crash!!!).
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

            if (!TryReadLoadout(
                    out var slots, out double nextFire, out bool infinite,
                    out var mineSlots, out double nextMine, out bool infiniteMines,
                    out int shipLevel))
            {
                SetVisible(false);
                return;
            }

            int count = slots != null ? slots.Count : 0;
            int mineCount = mineSlots != null ? mineSlots.Count : 0;

            // --- Caret ownership ---
            // If only one weapon type is equipped, force focus onto that type so ALT
            // cannot sit on an empty rocket list or an empty mine list.
            if (count <= 0 && mineCount > 0)
                MineSlotSelection.SetHudFocused(true);
            else if (mineCount <= 0 && count > 0)
                MineSlotSelection.SetHudFocused(false);

            PollCycleKeys(count, mineCount);
            RocketSlotSelection.Clamp(count);
            MineSlotSelection.Clamp(mineCount);

            SetVisible(true);
            Paint(slots, nextFire, infinite, mineSlots, nextMine, infiniteMines, shipLevel);
        }

        /// <summary>
        /// UP / DOWN walk rocket packs then mine packs as one list. The focused row
        /// owns ALT (rockets fire, mines place). Ignored while a turret is possessed.
        /// </summary>
        static void PollCycleKeys(int rocketCount, int mineCount)
        {
            if (PlanetaryDefenseTurretClientState.IsControlling)
                return;

            var k = Keyboard.current;
            if (k == null)
                return;

            int delta = 0;
            if (k.upArrowKey.wasPressedThisFrame)
                delta = -1;
            if (k.downArrowKey.wasPressedThisFrame)
                delta = 1;
            if (delta == 0)
                return;

            int total = rocketCount + mineCount;
            if (total <= 0)
                return;

            if (rocketCount <= 0)
                MineSlotSelection.SetHudFocused(true);
            else if (mineCount <= 0)
                MineSlotSelection.SetHudFocused(false);

            // Combined index: rockets occupy 0..rocketCount-1, mines follow.
            int index = MineSlotSelection.HudFocused
                ? rocketCount + MineSlotSelection.Clamp(mineCount)
                : RocketSlotSelection.Clamp(rocketCount);
            index += delta;
            while (index < 0)
                index += total;
            index %= total;

            if (index < rocketCount)
            {
                MineSlotSelection.SetHudFocused(false);
                RocketSlotSelection.Select(index, rocketCount);
            }
            else
            {
                MineSlotSelection.SetHudFocused(true);
                MineSlotSelection.Select(index - rocketCount, mineCount);
            }
        }

        /// <summary>
        /// Reads rocket + mine slots and cooldowns from the client-world local ship.
        /// </summary>
        /// <param name="slots">Rocket packs with remaining charges (or a debug INF stub).</param>
        /// <param name="nextFire">ElapsedTime when the next rocket may fire.</param>
        /// <param name="infinite">True when Editor infinite-rocket debug is on.</param>
        /// <param name="mineSlots">Mine packs with remaining charges (or a debug INF stub).</param>
        /// <param name="nextMine">ElapsedTime when the next mine may drop.</param>
        /// <param name="infiniteMines">True when Editor infinite-mine debug is on.</param>
        /// <param name="shipLevel">Local ship chassis level, used for debug stub packs.</param>
        /// <returns>True when at least one rocket or mine pack should paint.</returns>
        static bool TryReadLoadout(
            out List<(int level, int charges)> slots,
            out double nextFire,
            out bool infinite,
            out List<(int level, int charges)> mineSlots,
            out double nextMine,
            out bool infiniteMines,
            out int shipLevel)
        {
            slots = null;
            mineSlots = null;
            nextFire = 0d;
            nextMine = 0d;
            shipLevel = 1;
            infinite = TitanOrbitDebugFlags.InfiniteRockets;
            infiniteMines = TitanOrbitDebugFlags.InfiniteMines;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) ||
                ship == Entity.Null)
                return false;

            var em = world.EntityManager;
            if (em.HasComponent<ShipLoadoutState>(ship))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(ship);
                nextFire = loadout.NextRocketFireTime;
                nextMine = loadout.NextMinePlaceTime;
            }
            if (em.HasComponent<ShipState>(ship))
                shipLevel = Mathf.Max(1, em.GetComponentData<ShipState>(ship).ShipLevel);

            slots = new List<(int, int)>(4);
            mineSlots = new List<(int, int)>(4);
            if (em.HasBuffer<EquippedEquipmentElement>(ship))
            {
                var buffer = em.GetBuffer<EquippedEquipmentElement>(ship);
                for (int i = 0; i < buffer.Length; i++)
                {
                    var entry = buffer[i];
                    if (entry.RemainingCharges <= 0)
                        continue;
                    var kind = (StoreItemType)entry.ItemType;
                    if (StoreItemData.IsRocket(kind))
                        slots.Add((Mathf.Max(1, entry.ItemLevel), entry.RemainingCharges));
                    else if (StoreItemData.IsMine(kind))
                        mineSlots.Add((Mathf.Max(1, entry.ItemLevel), entry.RemainingCharges));
                }
            }

            // Debug stubs so the HUD still appears when the ship has no store packs.
            if (infinite && slots.Count == 0)
                slots.Add((shipLevel, 0));
            if (infiniteMines && mineSlots.Count == 0)
                mineSlots.Add((shipLevel, 0));

            return slots.Count > 0 || mineSlots.Count > 0;
        }

        /// <summary>
        /// Writes one stacked column of gear-slot buttons. Rockets first, mines after —
        /// same order as UP / DOWN — with kind / level / damage on each tile.
        /// </summary>
        void Paint(
            List<(int level, int charges)> slots,
            double nextFire,
            bool infinite,
            List<(int level, int charges)> mineSlots,
            double nextMine,
            bool infiniteMines,
            int shipLevel)
        {
            double now = 0d;
            var world = EcsGameBridge.ClientWorld;
            if (world != null && world.IsCreated)
                now = world.Time.ElapsedTime;

            int rocketCount = slots != null ? slots.Count : 0;
            int mineCount = mineSlots != null ? mineSlots.Count : 0;
            _paintedRocketCount = rocketCount;

            int rocketSelected = RocketSlotSelection.Clamp(rocketCount);
            int mineSelected = MineSlotSelection.Clamp(mineCount);
            bool minesFocused = MineSlotSelection.HudFocused;

            // Shared cooldown per weapon type — every rocket tile uses the rocket timer,
            // every mine tile uses the mine timer.
            float rocketRemain = nextFire > now ? (float)(nextFire - now) : 0f;
            bool rocketReady = rocketRemain <= 0.05f;
            int rocketCdLevel = rocketCount > 0 && rocketSelected < rocketCount
                ? slots[rocketSelected].level
                : Mathf.Max(1, shipLevel);
            float rocketTotalCd = Mathf.Max(0.1f, RocketCatalog.Get(rocketCdLevel).fireCooldown);
            float rocketFraction = rocketReady ? 1f : Mathf.Clamp01(rocketRemain / rocketTotalCd);

            float mineRemain = nextMine > now ? (float)(nextMine - now) : 0f;
            bool mineReady = mineRemain <= 0.05f;
            int mineCdLevel = mineCount > 0 && mineSelected < mineCount
                ? mineSlots[mineSelected].level
                : Mathf.Max(1, shipLevel);
            float mineTotalCd = Mathf.Max(0.1f, MineCatalog.Get(mineCdLevel).deployCooldown);
            float mineFraction = mineReady ? 1f : Mathf.Clamp01(mineRemain / mineTotalCd);

            int row = 0;

            // --- Rocket tiles ---
            for (int i = 0; i < rocketCount && row < _tiles.Count; i++, row++)
            {
                bool isSel = !minesFocused && i == rocketSelected;
                PaintTile(
                    _tiles[row],
                    row,
                    isRocket: true,
                    slots[i].level,
                    slots[i].charges,
                    infinite,
                    isSel,
                    rocketReady,
                    rocketRemain,
                    rocketFraction);
            }

            // --- Mine tiles (same column, no second header) ---
            for (int i = 0; i < mineCount && row < _tiles.Count; i++, row++)
            {
                bool isSel = minesFocused && i == mineSelected;
                PaintTile(
                    _tiles[row],
                    row,
                    isRocket: false,
                    mineSlots[i].level,
                    mineSlots[i].charges,
                    infiniteMines,
                    isSel,
                    mineReady,
                    mineRemain,
                    mineFraction);
            }

            for (int i = row; i < _tiles.Count; i++)
                HideTile(_tiles[i]);

            float tilesHeight = row <= 0 ? 0f : row * TileHeight + (row - 1) * TileGap;
            float height = tilesHeight + PanelPad * 2f;
            if (_panel != null)
                _panel.sizeDelta = new Vector2(PanelWidth, Mathf.Max(TileHeight + PanelPad * 2f, height));
        }

        /// <summary>
        /// Fills one gear-slot button. Kind word, level, and damage stay on the tile
        /// so the player does not need a section header to know rocket vs mine.
        /// </summary>
        /// <param name="tile">Chrome built in <see cref="BuildUi"/>.</param>
        /// <param name="row">0-based stack index from the panel top.</param>
        /// <param name="isRocket">True paints ROCKET + rocket damage; false paints MINE.</param>
        /// <param name="level">Store purchase level stamped on the pack.</param>
        /// <param name="charges">Shots / mines left in this pack.</param>
        /// <param name="infinite">Editor debug: print INF instead of a charge count.</param>
        /// <param name="isSelected">True when this row owns the caret and ALT.</param>
        /// <param name="ready">True when this weapon type's shared cooldown is finished.</param>
        /// <param name="remain">Seconds until that cooldown finishes.</param>
        /// <param name="fraction">Remaining / total cooldown for the thin bar.</param>
        void PaintTile(
            PackTile tile,
            int row,
            bool isRocket,
            int level,
            int charges,
            bool infinite,
            bool isSelected,
            bool ready,
            float remain,
            float fraction)
        {
            if (tile == null || tile.Root == null)
                return;

            tile.Root.SetActive(true);
            tile.Rect.anchoredPosition = new Vector2(PanelPad, -PanelPad - row * (TileHeight + TileGap));

            tile.KindLabel.text = isRocket ? "ROCKET" : "MINE";
            tile.KindLabel.color = isSelected ? CaptionSelected : CaptionDim;
            tile.LevelLabel.text = $"Lv {level}";
            tile.LevelLabel.color = isSelected ? LevelColor : LevelDim;
            tile.DetailLabel.text = isRocket
                ? FormatRocketDetails(level, charges, infinite)
                : FormatMineDetails(level, charges, infinite);
            tile.DetailLabel.color = isSelected ? BodyColor : BodyDim;

            PaintTileHint(tile.HintLabel, ready, remain, isSelected);
            PaintTileCooldownBar(tile, fraction, ready);

            tile.Background.color = isSelected ? RowSelected : RowIdle;
            if (tile.Caret != null)
                tile.Caret.enabled = isSelected;
            if (tile.Outline != null)
                tile.Outline.enabled = isSelected;
            if (tile.Button != null)
                tile.Button.interactable = true;
        }

        /// <summary>Turns a spare row off so unused MaxRows slots do not show empty chrome.</summary>
        static void HideTile(PackTile tile)
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
        /// Shows or hides the loadout panel only. Never disables the Canvas — Orbit Menu
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
        /// Click a row to focus that pack. Rows below <see cref="_paintedRocketCount"/>
        /// are rockets; the rest are mines. ALT then fires or places.
        /// </summary>
        void OnRowClicked(int hudIndex)
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;

            if (hudIndex < _paintedRocketCount)
            {
                MineSlotSelection.SetHudFocused(false);
                RocketSlotSelection.Select(hudIndex, Mathf.Max(1, hudIndex + 1));
                return;
            }

            int mineIndex = hudIndex - _paintedRocketCount;
            MineSlotSelection.SetHudFocused(true);
            MineSlotSelection.Select(mineIndex, Mathf.Max(1, mineIndex + 1));
        }

        /// <summary>Builds dark-glass canvas and a pool of tappable gear-slot buttons.</summary>
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
            _panel.anchoredPosition = new Vector2(14f, 0f);
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
            accentGo.GetComponent<Image>().color = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(1);
            accentGo.GetComponent<Image>().raycastTarget = false;

            for (int i = 0; i < MaxRows; i++)
                _tiles.Add(BuildTile(_panel, i));
        }

        /// <summary>
        /// Builds one gear-slot button: kind word, ALT/cooldown hint, level, damage, thin bar.
        /// </summary>
        /// <param name="parent">Dark panel that holds the stacked list.</param>
        /// <param name="index">Pool index and click identity (0..MaxRows-1).</param>
        PackTile BuildTile(RectTransform parent, int index)
        {
            int captured = index;
            var tile = new PackTile();

            var rowGo = new GameObject($"Tile{index}", typeof(RectTransform), typeof(Image), typeof(Button));
            rowGo.transform.SetParent(parent, false);
            var rt = rowGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(PanelPad, -PanelPad - index * (TileHeight + TileGap));
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
            caretRt.sizeDelta = new Vector2(4f, 0f);
            caretRt.anchoredPosition = Vector2.zero;
            var caretImg = caretGo.GetComponent<Image>();
            caretImg.color = CaretColor;
            caretImg.raycastTarget = false;
            caretImg.enabled = false;
            tile.Caret = caretImg;

            // Kind on the left, ALT / seconds on the right — same top row so the
            // type word never leaves the button for a section header.
            var kind = CreateLabel(rt, "Kind", "ROCKET", 12f, CaptionSelected, Vector2.zero, TextAlignmentOptions.Left);
            var kindRt = kind.rectTransform;
            kindRt.anchorMin = new Vector2(0f, 0.68f);
            kindRt.anchorMax = new Vector2(0.62f, 1f);
            kindRt.offsetMin = new Vector2(10f, 0f);
            kindRt.offsetMax = new Vector2(-2f, -2f);
            tile.KindLabel = kind;

            var hint = CreateLabel(rt, "Hint", "ALT", 11f, ReadyColor, Vector2.zero, TextAlignmentOptions.Right);
            var hintRt = hint.rectTransform;
            hintRt.anchorMin = new Vector2(0.55f, 0.68f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.offsetMin = new Vector2(0f, 0f);
            hintRt.offsetMax = new Vector2(-6f, -2f);
            tile.HintLabel = hint;

            var level = CreateLabel(rt, "Level", "Lv 1", 14f, LevelColor, Vector2.zero, TextAlignmentOptions.Center);
            var levelRt = level.rectTransform;
            levelRt.anchorMin = new Vector2(0f, 0.38f);
            levelRt.anchorMax = new Vector2(1f, 0.70f);
            levelRt.offsetMin = new Vector2(8f, 0f);
            levelRt.offsetMax = new Vector2(-6f, 0f);
            tile.LevelLabel = level;

            var detail = CreateLabel(rt, "Detail", "40  ×2", 11f, BodyColor, Vector2.zero, TextAlignmentOptions.Center);
            var detailRt = detail.rectTransform;
            detailRt.anchorMin = new Vector2(0f, 0.10f);
            detailRt.anchorMax = new Vector2(1f, 0.40f);
            detailRt.offsetMin = new Vector2(6f, 0f);
            detailRt.offsetMax = new Vector2(-6f, 0f);
            detail.enableWordWrapping = false;
            tile.DetailLabel = detail;

            var trackGo = new GameObject("CooldownTrack", typeof(RectTransform), typeof(Image));
            trackGo.transform.SetParent(rt, false);
            var trackRt = trackGo.GetComponent<RectTransform>();
            trackRt.anchorMin = new Vector2(0f, 0f);
            trackRt.anchorMax = new Vector2(1f, 0f);
            trackRt.pivot = new Vector2(0.5f, 0f);
            trackRt.anchoredPosition = new Vector2(0f, 3f);
            trackRt.sizeDelta = new Vector2(-12f, 4f);
            var trackImg = trackGo.GetComponent<Image>();
            trackImg.color = new Color(0.04f, 0.06f, 0.1f, 0.95f);
            trackImg.raycastTarget = false;
            tile.CooldownTrack = trackGo;

            var fillGo = new GameObject("CooldownFill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(trackRt, false);
            tile.CooldownFill = fillGo.GetComponent<RectTransform>();
            tile.CooldownFill.anchorMin = Vector2.zero;
            tile.CooldownFill.anchorMax = Vector2.one;
            tile.CooldownFill.offsetMin = Vector2.zero;
            tile.CooldownFill.offsetMax = Vector2.zero;
            tile.CooldownFillImage = fillGo.GetComponent<Image>();
            tile.CooldownFillImage.color = ReadyColor;
            tile.CooldownFillImage.raycastTarget = false;

            rowGo.SetActive(false);
            return tile;
        }

        /// <summary>
        /// ALT only on the focused tile when that pack is ready. Unfocused tiles still
        /// show remaining cooldown so the player can see the other weapon reload,
        /// but they never advertise a second hotkey.
        /// </summary>
        static void PaintTileHint(TextMeshProUGUI label, bool ready, float remain, bool focused)
        {
            if (label == null)
                return;

            if (!ready)
            {
                label.text = $"{remain:0.0}s";
                label.color = focused ? WaitColor : WaitDim;
                return;
            }

            label.text = focused ? "ALT" : string.Empty;
            label.color = ReadyColor;
        }

        /// <summary>
        /// Thin bar on this tile. Hidden when ready so idle packs stay as kind / level / damage.
        /// </summary>
        static void PaintTileCooldownBar(PackTile tile, float remainingFraction, bool ready)
        {
            if (tile == null)
                return;

            if (tile.CooldownTrack != null)
                tile.CooldownTrack.SetActive(!ready);

            if (tile.CooldownFill == null)
                return;

            float t = Mathf.Clamp01(remainingFraction);
            tile.CooldownFill.anchorMax = new Vector2(t, 1f);
            if (tile.CooldownFillImage != null)
                tile.CooldownFillImage.color = ready ? ReadyColor : WaitColor;
        }

        /// <summary>
        /// Cyan edge around the focused tile so UP/DOWN / click have a clear caret
        /// beyond the fill tint. Disabled until that row owns ALT.
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

        /// <summary>Rocket damage + charges (or INF) stacked inside the gear-slot button.</summary>
        static string FormatRocketDetails(int level, int charges, bool infinite)
        {
            float damage = RocketShotMath.ResolveDamage(Mathf.Max(1, level));
            string dmg = damage.ToString("0", CultureInfo.InvariantCulture);
            if (infinite)
                return $"{dmg}  INF";
            return $"{dmg}  ×{Mathf.Max(0, charges)}";
        }

        /// <summary>Mine blast damage + charges (or INF) stacked inside the gear-slot button.</summary>
        static string FormatMineDetails(int level, int charges, bool infinite)
        {
            float damage = MineShotMath.ResolveDamage(Mathf.Max(1, level));
            string dmg = damage.ToString("0", CultureInfo.InvariantCulture);
            if (infinite)
                return $"{dmg}  INF";
            return $"{dmg}  ×{Mathf.Max(0, charges)}";
        }

        /// <summary>Creates a TMP label under <paramref name="parent"/>.</summary>
        static TextMeshProUGUI CreateLabel(
            Transform parent,
            string name,
            string text,
            float size,
            Color color,
            Vector2 anchoredPos,
            TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = new Vector2(-16f, 18f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            // Dark halo so ice-blue labels stay readable if a fill tint ever sits behind them.
            tmp.outlineWidth = 0.22f;
            tmp.outlineColor = LabelOutline;
            return tmp;
        }
    }
}
