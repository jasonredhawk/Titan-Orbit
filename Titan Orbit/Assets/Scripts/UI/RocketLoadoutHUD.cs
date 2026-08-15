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
    /// Left-middle in-flight list of equipped rocket packs. One compact tile per slot: level,
    /// damage, remaining shots, and a caret for the pack that will fire. UP / DOWN and tile
    /// clicks change the selection. Hidden on the main menu, Join Team, and Orbit Menu.
    /// <para>
    /// [TITAN-ORBIT] Reads the local ship's ghosted <see cref="EquippedEquipmentElement"/>
    /// buffer and <see cref="ShipLoadoutState.NextRocketFireTime"/>. Skips all ship queries
    /// while <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> (Join Team Crash!!!).
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
        GameObject _mainMenuPanel;
        readonly List<TextMeshProUGUI> _levelLabels = new List<TextMeshProUGUI>(MaxRows);
        readonly List<TextMeshProUGUI> _detailLabels = new List<TextMeshProUGUI>(MaxRows);
        readonly List<Button> _rowButtons = new List<Button>(MaxRows);
        readonly List<Image> _rowBackgrounds = new List<Image>(MaxRows);
        readonly List<Image> _rowCarets = new List<Image>(MaxRows);
        readonly List<GameObject> _rowRoots = new List<GameObject>(MaxRows);

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
        /// Refreshes the list from the local ship. No ECS gathers during join Instantiates.
        /// </summary>
        void LateUpdate()
        {
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                SetVisible(false);
                return;
            }

            // --- Menu / team pick ---
            // [TITAN-ORBIT] Same suppress as speedometer. Infinite-rocket debug must not
            // paint this overlay on Main Menu or Join Team.
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                IsMainMenuShowing() ||
                !EcsGameBridge.HasLocalPlayerShip())
            {
                SetVisible(false);
                return;
            }

            if (MoonOrbitClientState.IsOrbitMenuVisible)
            {
                SetVisible(false);
                return;
            }

            if (!TryReadLoadout(out var slots, out double nextFire, out bool infinite, out int shipLevel))
            {
                SetVisible(false);
                return;
            }

            int count = slots != null ? slots.Count : 0;
            PollCycleKeys(count);
            RocketSlotSelection.Clamp(count);

            SetVisible(true);
            Paint(slots, nextFire, infinite, shipLevel);
        }

        /// <summary>UP / DOWN change which pack will fire. Ignored while a turret is possessed.</summary>
        static void PollCycleKeys(int count)
        {
            if (count <= 0)
                return;
            if (PlanetaryDefenseTurretClientState.IsControlling)
                return;

            var k = Keyboard.current;
            if (k == null)
                return;
            if (k.upArrowKey.wasPressedThisFrame)
                RocketSlotSelection.Cycle(-1, count);
            if (k.downArrowKey.wasPressedThisFrame)
                RocketSlotSelection.Cycle(1, count);
        }

        /// <summary>Reads rocket slots + cooldown from the client-world local ship.</summary>
        static bool TryReadLoadout(
            out List<(int level, int charges)> slots,
            out double nextFire,
            out bool infinite,
            out int shipLevel)
        {
            slots = null;
            nextFire = 0d;
            shipLevel = 1;
            infinite = TitanOrbitDebugFlags.InfiniteRockets;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) ||
                ship == Entity.Null)
                return false;

            var em = world.EntityManager;
            if (em.HasComponent<ShipLoadoutState>(ship))
                nextFire = em.GetComponentData<ShipLoadoutState>(ship).NextRocketFireTime;
            if (em.HasComponent<ShipState>(ship))
                shipLevel = Mathf.Max(1, em.GetComponentData<ShipState>(ship).ShipLevel);

            slots = new List<(int, int)>(4);
            if (em.HasBuffer<EquippedEquipmentElement>(ship))
            {
                var buffer = em.GetBuffer<EquippedEquipmentElement>(ship);
                for (int i = 0; i < buffer.Length; i++)
                {
                    var entry = buffer[i];
                    if (!StoreItemData.IsRocket((StoreItemType)entry.ItemType))
                        continue;
                    if (entry.RemainingCharges <= 0)
                        continue;
                    // Always show the stamped pack level — infinite does not hide it.
                    slots.Add((Mathf.Max(1, entry.ItemLevel), entry.RemainingCharges));
                }
            }

            if (infinite && slots.Count == 0)
                slots.Add((shipLevel, 0));

            return slots.Count > 0;
        }

        /// <summary>Writes caption, cooldown, and one highlighted row per rocket pack.</summary>
        void Paint(List<(int level, int charges)> slots, double nextFire, bool infinite, int shipLevel)
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

            int row = 0;
            if (slots != null)
            {
                for (int i = 0; i < slots.Count && row < _rowRoots.Count; i++, row++)
                {
                    bool isSel = i == selected;
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

            float tiles = row <= 0 ? 0f : row * TileHeight + (row - 1) * TileGap;
            float height = HeaderHeight + tiles + PanelPad;
            if (_panel != null)
                _panel.sizeDelta = new Vector2(PanelWidth, height);
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
            RocketSlotSelection.Select(hudIndex, Mathf.Max(1, hudIndex + 1));
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
        }

        /// <summary>
        /// Dark track + shrinking fill under the seconds label. Fraction is remaining / total
        /// so the bar counts down while the rocket reloads.
        /// </summary>
        void BuildCooldownBar(RectTransform parent)
        {
            var trackGo = new GameObject("CooldownTrack", typeof(RectTransform), typeof(Image));
            trackGo.transform.SetParent(parent, false);
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
