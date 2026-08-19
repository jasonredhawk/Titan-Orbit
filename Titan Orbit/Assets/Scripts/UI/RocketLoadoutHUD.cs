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
    /// Left-middle in-flight list of equipped rocket and mine packs. Rocket tiles: level,
    /// damage, remaining shots, and a caret for the pack that will fire (UP / DOWN / click).
    /// Mine tiles sit under a MINES header; click or UP/DOWN onto that row selects the pack.
    /// The focused section owns ALT (rockets fire, mines place). E also places a mine.
    /// Hidden on the main menu, Join Team, Orbit Menu, and while the local ship is dead.
    /// <para>
    /// [TITAN-ORBIT] Reads the local ship's ghosted <see cref="EquippedEquipmentElement"/>
    /// buffer and <see cref="ShipLoadoutState.NextRocketFireTime"/>. Skips ship gathers
    /// while <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> (Join Team Crash!!!)
    /// but keeps the last paint visible — MEGA plow gem Instantiates used to hide this list
    /// for a frame every rock.
    /// </para>
    /// Dark space-gamer chrome — same void glass as <see cref="ShipStatTooltipChrome"/>.
    /// </summary>
    [DefaultExecutionOrder(66200)]
    public class RocketLoadoutHUD : MonoBehaviour
    {
        const int MaxRows = 8;
        const float TileWidth = 88f;
        const float TileHeight = 44f;
        const float TileGap = 4f;
        const float PanelPad = 8f;
        const float PanelWidth = TileWidth + PanelPad * 2f;
        const float HeaderHeight = 56f;
        const float MineHeaderHeight = 52f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.92f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color LevelColor = new Color(0.55f, 0.95f, 1f, 1f);
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color WaitColor = new Color(0.95f, 0.72f, 0.28f, 1f);
        static readonly Color RowIdle = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color RowSelected = new Color(0.22f, 0.55f, 0.85f, 0.28f);
        static readonly Color CaretColor = new Color(0.45f, 0.95f, 1f, 1f);

        Canvas _canvas;
        RectTransform _panel;
        TextMeshProUGUI _caption;
        TextMeshProUGUI _cooldown;
        RectTransform _cooldownFill;
        Image _cooldownFillImage;
        GameObject _rocketCooldownTrack;
        GameObject _mainMenuPanel;
        readonly List<TextMeshProUGUI> _levelLabels = new List<TextMeshProUGUI>(MaxRows);
        readonly List<TextMeshProUGUI> _detailLabels = new List<TextMeshProUGUI>(MaxRows);
        readonly List<Button> _rowButtons = new List<Button>(MaxRows);
        readonly List<Image> _rowBackgrounds = new List<Image>(MaxRows);
        readonly List<Image> _rowCarets = new List<Image>(MaxRows);
        readonly List<GameObject> _rowRoots = new List<GameObject>(MaxRows);

        TextMeshProUGUI _mineCaption;
        TextMeshProUGUI _mineCooldown;
        RectTransform _mineCooldownFill;
        Image _mineCooldownFillImage;
        GameObject _mineHeaderRoot;
        readonly List<TextMeshProUGUI> _mineLevelLabels = new List<TextMeshProUGUI>(MaxRows);
        readonly List<TextMeshProUGUI> _mineDetailLabels = new List<TextMeshProUGUI>(MaxRows);
        readonly List<Button> _mineRowButtons = new List<Button>(MaxRows);
        readonly List<Image> _mineRowBackgrounds = new List<Image>(MaxRows);
        readonly List<Image> _mineRowCarets = new List<Image>(MaxRows);
        readonly List<GameObject> _mineRowRoots = new List<GameObject>(MaxRows);

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

        /// <summary>Builds the left-middle overlay canvas and row labels.</summary>
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
                HUDController.LocalPlayerDeathHidesHud)
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
        /// UP / DOWN walk rocket packs then mine packs as one list. The focused section
        /// owns ALT (rockets fire, mines place). E still places a mine. Ignored while a
        /// turret is possessed.
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

        /// <summary>Reads rocket + mine slots and cooldowns from the client-world local ship.</summary>
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

            if (infinite && slots.Count == 0)
                slots.Add((shipLevel, 0));
            if (infiniteMines && mineSlots.Count == 0)
                mineSlots.Add((shipLevel, 0));

            return slots.Count > 0 || mineSlots.Count > 0;
        }

        /// <summary>Writes rocket + mine captions, cooldowns, and highlighted pack rows.</summary>
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

            float remain = nextFire > now ? (float)(nextFire - now) : 0f;
            bool ready = remain <= 0.05f;
            int selected = RocketSlotSelection.Clamp(slots != null ? slots.Count : 0);

            if (_caption != null)
            {
                int selLevel = slots != null && selected < slots.Count
                    ? slots[selected].level
                    : Mathf.Max(1, shipLevel);
                _caption.text = infinite
                    ? $"RKT INF  Lv {selLevel}"
                    : $"ROCKETS  Lv {selLevel}";
            }

            int shotLevel = slots != null && selected < slots.Count
                ? slots[selected].level
                : Mathf.Max(1, shipLevel);
            float totalCd = Mathf.Max(0.1f, RocketCatalog.Get(shotLevel).fireCooldown);
            float fraction = ready ? 1f : Mathf.Clamp01(remain / totalCd);

            if (_cooldown != null)
            {
                _cooldown.text = ready ? "READY  ALT" : $"{remain:0.0}s  ALT";
                _cooldown.color = ready ? ReadyColor : WaitColor;
            }

            PaintCooldownBar(fraction, ready);

            bool showRockets = slots != null && slots.Count > 0;
            if (_caption != null)
                _caption.gameObject.SetActive(showRockets);
            if (_cooldown != null)
                _cooldown.gameObject.SetActive(showRockets);
            if (_rocketCooldownTrack != null)
                _rocketCooldownTrack.SetActive(showRockets);

            int row = 0;
            if (showRockets)
            {
                for (int i = 0; i < slots.Count && row < _rowRoots.Count; i++, row++)
                {
                    bool isSel = !MineSlotSelection.HudFocused && i == selected;
                    _rowRoots[row].SetActive(true);
                    _levelLabels[row].text = $"Lv {slots[i].level}";
                    _detailLabels[row].text = FormatDetails(slots[i].level, slots[i].charges, infinite);
                    _rowBackgrounds[row].color = isSel ? RowSelected : RowIdle;
                    if (_rowCarets[row] != null)
                        _rowCarets[row].enabled = isSel;
                    if (row < _rowButtons.Count && _rowButtons[row] != null)
                        _rowButtons[row].interactable = true;
                }
            }

            for (int i = row; i < _rowRoots.Count; i++)
                _rowRoots[i].SetActive(false);

            float rocketHeader = showRockets ? HeaderHeight : 0f;
            float rocketTiles = row <= 0 ? 0f : row * TileHeight + (row - 1) * TileGap;

            int mineRow = PaintMines(
                mineSlots, nextMine, infiniteMines, shipLevel, now,
                rocketHeader + rocketTiles);

            float mineHeader = mineRow > 0 || (mineSlots != null && mineSlots.Count > 0)
                ? MineHeaderHeight
                : 0f;
            float mineTiles = mineRow <= 0 ? 0f : mineRow * TileHeight + (mineRow - 1) * TileGap;
            float height = rocketHeader + rocketTiles + mineHeader + mineTiles + PanelPad;
            if (_panel != null)
                _panel.sizeDelta = new Vector2(PanelWidth, Mathf.Max(HeaderHeight, height));
        }

        /// <summary>
        /// Paints the MINES header, E cooldown, and pack tiles under the rocket list.
        /// <paramref name="topOffset"/> is the Y drop from the panel top after rocket chrome.
        /// </summary>
        int PaintMines(
            List<(int level, int charges)> mineSlots,
            double nextMine,
            bool infiniteMines,
            int shipLevel,
            double now,
            float topOffset)
        {
            bool show = mineSlots != null && mineSlots.Count > 0;
            if (_mineHeaderRoot != null)
                _mineHeaderRoot.SetActive(show);
            if (!show)
            {
                for (int i = 0; i < _mineRowRoots.Count; i++)
                    _mineRowRoots[i].SetActive(false);
                return 0;
            }

            if (_mineHeaderRoot != null)
            {
                var headerRt = _mineHeaderRoot.GetComponent<RectTransform>();
                headerRt.anchoredPosition = new Vector2(0f, -topOffset);
            }

            float remain = nextMine > now ? (float)(nextMine - now) : 0f;
            bool ready = remain <= 0.05f;
            int selected = MineSlotSelection.Clamp(mineSlots.Count);
            int selLevel = selected < mineSlots.Count
                ? mineSlots[selected].level
                : Mathf.Max(1, shipLevel);

            if (_mineCaption != null)
            {
                _mineCaption.text = infiniteMines
                    ? $"MINE INF  Lv {selLevel}"
                    : $"MINES  Lv {selLevel}";
            }

            float totalCd = Mathf.Max(0.1f, MineCatalog.Get(selLevel).deployCooldown);
            float fraction = ready ? 1f : Mathf.Clamp01(remain / totalCd);
            if (_mineCooldown != null)
            {
                string keyHint = MineSlotSelection.HudFocused ? "ALT" : "E";
                _mineCooldown.text = ready ? $"READY  {keyHint}" : $"{remain:0.0}s  {keyHint}";
                _mineCooldown.color = ready ? ReadyColor : WaitColor;
            }

            if (_mineCooldownFill != null)
            {
                _mineCooldownFill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
                if (_mineCooldownFillImage != null)
                    _mineCooldownFillImage.color = ready ? ReadyColor : WaitColor;
            }

            int row = 0;
            for (int i = 0; i < mineSlots.Count && row < _mineRowRoots.Count; i++, row++)
            {
                bool isSel = MineSlotSelection.HudFocused && i == selected;
                float y = -topOffset - MineHeaderHeight - i * (TileHeight + TileGap);
                var rt = _mineRowRoots[row].GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(PanelPad, y);
                _mineRowRoots[row].SetActive(true);
                _mineLevelLabels[row].text = $"Lv {mineSlots[i].level}";
                _mineDetailLabels[row].text = FormatMineDetails(
                    mineSlots[i].level, mineSlots[i].charges, infiniteMines);
                _mineRowBackgrounds[row].color = isSel ? RowSelected : RowIdle;
                if (_mineRowCarets[row] != null)
                    _mineRowCarets[row].enabled = isSel;
            }

            for (int i = row; i < _mineRowRoots.Count; i++)
                _mineRowRoots[i].SetActive(false);

            return row;
        }

        /// <summary>True while the scene Main Menu panel is up (Play / Join Game).</summary>
        bool IsMainMenuShowing()
        {
            if (_mainMenuPanel == null)
                _mainMenuPanel = GameObject.Find("MainMenuPanel");
            return _mainMenuPanel != null && _mainMenuPanel.activeInHierarchy;
        }

        /// <summary>
        /// Shows or hides the rocket panel only. Never disables the Canvas — Orbit Menu
        /// must not share a disabled overlay.
        /// </summary>
        void SetVisible(bool visible)
        {
            if (_canvas != null)
                _canvas.enabled = true;
            if (_panel != null)
                _panel.gameObject.SetActive(visible);
        }

        /// <summary>Click a row to select that pack (ALT still fires).</summary>
        static void OnRowSelected(int hudIndex)
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;
            MineSlotSelection.SetHudFocused(false);
            RocketSlotSelection.Select(hudIndex, Mathf.Max(1, hudIndex + 1));
        }

        /// <summary>Click a mine row to select that pack (E still places).</summary>
        static void OnMineRowSelected(int hudIndex)
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;
            MineSlotSelection.SetHudFocused(true);
            MineSlotSelection.Select(hudIndex, Mathf.Max(1, hudIndex + 1));
        }

        /// <summary>Builds dark-glass canvas, caption, cooldown, and tappable rows.</summary>
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
            _panel.sizeDelta = new Vector2(PanelWidth, HeaderHeight + TileHeight + PanelPad);
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

            _caption = CreateLabel(_panel, "Caption", "ROCKETS", 12f, CaptionColor, new Vector2(0f, -5f), TextAlignmentOptions.Top);
            var captionRt = _caption.rectTransform;
            captionRt.offsetMin = new Vector2(6f, captionRt.offsetMin.y);
            captionRt.offsetMax = new Vector2(-6f, captionRt.offsetMax.y);
            _cooldown = CreateLabel(_panel, "Cooldown", "READY", 11f, ReadyColor, new Vector2(0f, -20f), TextAlignmentOptions.Top);
            var cooldownRt = _cooldown.rectTransform;
            cooldownRt.offsetMin = new Vector2(6f, cooldownRt.offsetMin.y);
            cooldownRt.offsetMax = new Vector2(-6f, cooldownRt.offsetMax.y);
            BuildCooldownBar(_panel);

            for (int i = 0; i < MaxRows; i++)
            {
                int captured = i;
                var rowGo = new GameObject($"Tile{i}", typeof(RectTransform), typeof(Image), typeof(Button));
                rowGo.transform.SetParent(_panel, false);
                var rt = rowGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(PanelPad, -HeaderHeight - i * (TileHeight + TileGap));
                rt.sizeDelta = new Vector2(TileWidth, TileHeight);
                var img = rowGo.GetComponent<Image>();
                img.color = RowIdle;
                var btn = rowGo.GetComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => OnRowSelected(captured));
                _rowButtons.Add(btn);
                _rowBackgrounds.Add(img);
                _rowRoots.Add(rowGo);

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
                _rowCarets.Add(caretImg);

                var level = CreateLabel(rt, "Level", "Lv 1", 15f, LevelColor, Vector2.zero, TextAlignmentOptions.Center);
                var levelRt = level.rectTransform;
                levelRt.anchorMin = new Vector2(0f, 0.48f);
                levelRt.anchorMax = new Vector2(1f, 1f);
                levelRt.offsetMin = new Vector2(8f, 0f);
                levelRt.offsetMax = new Vector2(-6f, -2f);
                _levelLabels.Add(level);

                var detail = CreateLabel(rt, "Detail", "40  ×2", 11f, BodyColor, Vector2.zero, TextAlignmentOptions.Center);
                var detailRt = detail.rectTransform;
                detailRt.anchorMin = new Vector2(0f, 0f);
                detailRt.anchorMax = new Vector2(1f, 0.52f);
                detailRt.offsetMin = new Vector2(6f, 2f);
                detailRt.offsetMax = new Vector2(-6f, 0f);
                detail.enableWordWrapping = false;
                _detailLabels.Add(detail);
                rowGo.SetActive(false);
            }

            BuildMineSection(_panel);
        }

        /// <summary>MINES caption, E cooldown bar, and tappable pack tiles under the rockets.</summary>
        void BuildMineSection(RectTransform parent)
        {
            _mineHeaderRoot = new GameObject("MineHeader", typeof(RectTransform));
            _mineHeaderRoot.transform.SetParent(parent, false);
            var headerRt = _mineHeaderRoot.GetComponent<RectTransform>();
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.anchoredPosition = Vector2.zero;
            headerRt.sizeDelta = new Vector2(0f, MineHeaderHeight);

            _mineCaption = CreateLabel(
                headerRt, "MineCaption", "MINES", 12f, CaptionColor,
                new Vector2(0f, -2f), TextAlignmentOptions.Top);
            var mineCaptionRt = _mineCaption.rectTransform;
            mineCaptionRt.offsetMin = new Vector2(6f, mineCaptionRt.offsetMin.y);
            mineCaptionRt.offsetMax = new Vector2(-6f, mineCaptionRt.offsetMax.y);

            _mineCooldown = CreateLabel(
                headerRt, "MineCooldown", "READY  E", 11f, ReadyColor,
                new Vector2(0f, -18f), TextAlignmentOptions.Top);
            var mineCdRt = _mineCooldown.rectTransform;
            mineCdRt.offsetMin = new Vector2(6f, mineCdRt.offsetMin.y);
            mineCdRt.offsetMax = new Vector2(-6f, mineCdRt.offsetMax.y);

            var trackGo = new GameObject("MineCooldownTrack", typeof(RectTransform), typeof(Image));
            trackGo.transform.SetParent(headerRt, false);
            var trackRt = trackGo.GetComponent<RectTransform>();
            trackRt.anchorMin = new Vector2(0f, 1f);
            trackRt.anchorMax = new Vector2(1f, 1f);
            trackRt.pivot = new Vector2(0.5f, 1f);
            trackRt.anchoredPosition = new Vector2(0f, -36f);
            trackRt.sizeDelta = new Vector2(-16f, 6f);
            var trackImg = trackGo.GetComponent<Image>();
            trackImg.color = new Color(0.04f, 0.06f, 0.1f, 0.95f);
            trackImg.raycastTarget = false;

            var fillGo = new GameObject("MineCooldownFill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(trackRt, false);
            _mineCooldownFill = fillGo.GetComponent<RectTransform>();
            _mineCooldownFill.anchorMin = Vector2.zero;
            _mineCooldownFill.anchorMax = Vector2.one;
            _mineCooldownFill.offsetMin = Vector2.zero;
            _mineCooldownFill.offsetMax = Vector2.zero;
            _mineCooldownFillImage = fillGo.GetComponent<Image>();
            _mineCooldownFillImage.color = ReadyColor;
            _mineCooldownFillImage.raycastTarget = false;

            for (int i = 0; i < MaxRows; i++)
            {
                int captured = i;
                var rowGo = new GameObject($"MineTile{i}", typeof(RectTransform), typeof(Image), typeof(Button));
                rowGo.transform.SetParent(parent, false);
                var rt = rowGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(PanelPad, -HeaderHeight - i * (TileHeight + TileGap));
                rt.sizeDelta = new Vector2(TileWidth, TileHeight);
                var img = rowGo.GetComponent<Image>();
                img.color = RowIdle;
                var btn = rowGo.GetComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => OnMineRowSelected(captured));
                _mineRowButtons.Add(btn);
                _mineRowBackgrounds.Add(img);
                _mineRowRoots.Add(rowGo);

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
                _mineRowCarets.Add(caretImg);

                var level = CreateLabel(rt, "Level", "Lv 1", 15f, LevelColor, Vector2.zero, TextAlignmentOptions.Center);
                var levelRt = level.rectTransform;
                levelRt.anchorMin = new Vector2(0f, 0.48f);
                levelRt.anchorMax = new Vector2(1f, 1f);
                levelRt.offsetMin = new Vector2(8f, 0f);
                levelRt.offsetMax = new Vector2(-6f, -2f);
                _mineLevelLabels.Add(level);

                var detail = CreateLabel(rt, "Detail", "35  ×4", 11f, BodyColor, Vector2.zero, TextAlignmentOptions.Center);
                var detailRt = detail.rectTransform;
                detailRt.anchorMin = new Vector2(0f, 0f);
                detailRt.anchorMax = new Vector2(1f, 0.52f);
                detailRt.offsetMin = new Vector2(6f, 2f);
                detailRt.offsetMax = new Vector2(-6f, 0f);
                detail.enableWordWrapping = false;
                _mineDetailLabels.Add(detail);
                rowGo.SetActive(false);
            }

            _mineHeaderRoot.SetActive(false);
        }

        /// <summary>
        /// Dark track + shrinking fill under the seconds label. Fraction is remaining / total
        /// so the bar counts down while the rocket reloads.
        /// </summary>
        void BuildCooldownBar(RectTransform parent)
        {
            var trackGo = new GameObject("CooldownTrack", typeof(RectTransform), typeof(Image));
            trackGo.transform.SetParent(parent, false);
            _rocketCooldownTrack = trackGo;
            var trackRt = trackGo.GetComponent<RectTransform>();
            trackRt.anchorMin = new Vector2(0f, 1f);
            trackRt.anchorMax = new Vector2(1f, 1f);
            trackRt.pivot = new Vector2(0.5f, 1f);
            trackRt.anchoredPosition = new Vector2(0f, -38f);
            trackRt.sizeDelta = new Vector2(-16f, 6f);
            var trackImg = trackGo.GetComponent<Image>();
            trackImg.color = new Color(0.04f, 0.06f, 0.1f, 0.95f);
            trackImg.raycastTarget = false;

            var fillGo = new GameObject("CooldownFill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(trackRt, false);
            _cooldownFill = fillGo.GetComponent<RectTransform>();
            _cooldownFill.anchorMin = Vector2.zero;
            _cooldownFill.anchorMax = Vector2.one;
            _cooldownFill.offsetMin = Vector2.zero;
            _cooldownFill.offsetMax = Vector2.zero;
            _cooldownFillImage = fillGo.GetComponent<Image>();
            _cooldownFillImage.color = ReadyColor;
            _cooldownFillImage.raycastTarget = false;
        }

        /// <summary>Shrinks the fill from the right as remaining cooldown drops.</summary>
        void PaintCooldownBar(float remainingFraction, bool ready)
        {
            if (_cooldownFill == null)
                return;

            float t = Mathf.Clamp01(remainingFraction);
            _cooldownFill.anchorMax = new Vector2(t, 1f);
            if (_cooldownFillImage != null)
                _cooldownFillImage.color = ready ? ReadyColor : WaitColor;
        }

        /// <summary>Damage + charges (or INF) stacked inside a square tile.</summary>
        static string FormatDetails(int level, int charges, bool infinite)
        {
            float damage = RocketShotMath.ResolveDamage(Mathf.Max(1, level));
            string dmg = damage.ToString("0", CultureInfo.InvariantCulture);
            if (infinite)
                return $"{dmg}  INF";
            return $"{dmg}  ×{Mathf.Max(0, charges)}";
        }

        /// <summary>Mine damage + charges (or INF) stacked inside a square tile.</summary>
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
            return tmp;
        }
    }
}
