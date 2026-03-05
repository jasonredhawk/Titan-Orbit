using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Data;
using TitanOrbit.Core;
using TitanOrbit.Systems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Experimental card-and-slot grid UI: shows three coloured grids (Ship / Weapon / Cargo) in a Lite-Brite style.
    /// Grid sizes come from the player's current chassis (ShipChassisDefinition); falls back to defaults when no chassis is set.
    /// </summary>
    public class ShipCardGridUI : MonoBehaviour
    {
        [Header("Optional theme")]
        [SerializeField] private Sprite panelSprite;
        [SerializeField] private Sprite cellSprite;

        private GameObject panel;
        private Starship playerShip;

        private static readonly int DefaultShipW = 3, DefaultShipH = 7, DefaultWeaponW = 2, DefaultWeaponH = 3, DefaultCargoW = 3, DefaultCargoH = 4;

        private int currentShipW = DefaultShipW, currentShipH = DefaultShipH;
        private int currentWeaponW = DefaultWeaponW, currentWeaponH = DefaultWeaponH;
        private int currentCargoW = DefaultCargoW, currentCargoH = DefaultCargoH;
        private int lastChassisIndex = -2;

        private Image[,] shipCells;
        private Image[,] weaponCells;
        private Image[,] cargoCells;

        private Color shipColor = new Color(0.35f, 0.65f, 0.9f, 0.85f);
        private Color weaponColor = new Color(0.85f, 0.35f, 0.35f, 0.85f);
        private Color cargoColor = new Color(0.35f, 0.75f, 0.5f, 0.85f);

        public static ShipCardGridUI GetOrCreate()
        {
            var existing = Object.FindFirstObjectByType<ShipCardGridUI>();
            if (existing != null) return existing;

            Canvas canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                GameObject canvasObj = new GameObject("Canvas");
                canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            }

            GameObject uiObj = new GameObject("ShipCardGridUI");
            uiObj.transform.SetParent(canvas.transform, false);
            var menu = uiObj.AddComponent<ShipCardGridUI>();
            menu.Show();
            return menu;
        }

        private void Awake()
        {
            EnsurePanelExists();
            Hide();
        }

        private void Update()
        {
            if (playerShip == null)
            {
                foreach (var ship in Object.FindObjectsByType<Starship>(FindObjectsSortMode.None))
                {
                    if (ship.IsOwner) { playerShip = ship; break; }
                }
            }

            if (playerShip == null || playerShip.ShipTeam == TeamManager.Team.None)
            {
                Hide();
                return;
            }

            // Only show the card grid when in orbit (with orbit panel / store).
            if (!playerShip.IsInOrbit)
            {
                Hide();
                return;
            }

            int chassisIndex = playerShip.CurrentChassisIndex;
            if (chassisIndex != lastChassisIndex)
            {
                lastChassisIndex = chassisIndex;
                RefreshGridDimensionsFromChassis();
                if (panel != null)
                {
                    Object.Destroy(panel);
                    panel = null;
                    shipCells = null;
                    weaponCells = null;
                    cargoCells = null;
                }
            }

            Show();
            RefreshGrids();
        }

        private void RefreshGridDimensionsFromChassis()
        {
            ShipChassisDefinition chassis = CardShopSystem.Instance != null
                ? CardShopSystem.Instance.GetChassisByIndex(lastChassisIndex)
                : null;
            if (chassis != null)
            {
                currentShipW = Mathf.Max(1, chassis.shipGridWidth);
                currentShipH = Mathf.Max(1, chassis.shipGridHeight);
                currentWeaponW = Mathf.Max(1, chassis.weaponGridWidth);
                currentWeaponH = Mathf.Max(1, chassis.weaponGridHeight);
                currentCargoW = Mathf.Max(1, chassis.cargoGridWidth);
                currentCargoH = Mathf.Max(1, chassis.cargoGridHeight);
            }
            else
            {
                currentShipW = DefaultShipW;
                currentShipH = DefaultShipH;
                currentWeaponW = DefaultWeaponW;
                currentWeaponH = DefaultWeaponH;
                currentCargoW = DefaultCargoW;
                currentCargoH = DefaultCargoH;
            }
        }

        public void Show()
        {
            EnsurePanelExists();
            if (panel != null) panel.SetActive(true);
        }

        public void Hide()
        {
            if (panel != null) panel.SetActive(false);
        }

        private void EnsurePanelExists()
        {
            if (panel != null) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            panel = new GameObject("CardGridPanel");
            panel.transform.SetParent(canvas.transform, false);
            var rect = panel.AddComponent<RectTransform>();
            // Anchor to far left so center of screen stays clear.
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            float maxGridHeight = Mathf.Max(currentShipH, currentWeaponH, currentCargoH) * 24f + 40f;
            rect.sizeDelta = new Vector2(620f, Mathf.Max(220f, maxGridHeight));
            const float leftMargin = 12f;
            rect.anchoredPosition = new Vector2(rect.sizeDelta.x * 0.5f + leftMargin, 0f);
            var img = panel.AddComponent<Image>();
            img.color = new Color(0.06f, 0.06f, 0.12f, 0.9f);
            if (panelSprite != null)
            {
                img.sprite = panelSprite;
                img.type = panelSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
            }

            float gridSpacing = 8f;
            float cellSize = 16f;

            CreateGrid("ShipGrid", new Vector2(-200f, 20f), currentShipW, currentShipH, cellSize, gridSpacing, shipColor, out shipCells);
            CreateLabel("ShipLabel", "Ship", new Vector2(-200f, maxGridHeight - 30f));

            CreateGrid("WeaponGrid", new Vector2(0f, 20f), currentWeaponW, currentWeaponH, cellSize, gridSpacing, weaponColor, out weaponCells);
            CreateLabel("WeaponLabel", "Weapon", new Vector2(0f, maxGridHeight - 30f));

            CreateGrid("CargoGrid", new Vector2(200f, 20f), currentCargoW, currentCargoH, cellSize, gridSpacing, cargoColor, out cargoCells);
            CreateLabel("CargoLabel", "Cargo", new Vector2(200f, maxGridHeight - 30f));
        }

        private void CreateGrid(string name, Vector2 center, int width, int height, float cellSize, float spacing, Color color, out Image[,] cells)
        {
            cells = new Image[width, height];
            var root = new GameObject(name);
            root.transform.SetParent(panel.transform, false);
            var rect = root.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            float totalWidth = width * cellSize + (width - 1) * spacing;
            float totalHeight = height * cellSize + (height - 1) * spacing;
            rect.sizeDelta = new Vector2(totalWidth, totalHeight);
            rect.anchoredPosition = center;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var cellGo = new GameObject($"Cell_{x}_{y}");
                    cellGo.transform.SetParent(root.transform, false);
                    var cellRect = cellGo.AddComponent<RectTransform>();
                    cellRect.anchorMin = new Vector2(0f, 0f);
                    cellRect.anchorMax = new Vector2(0f, 0f);
                    cellRect.pivot = new Vector2(0.5f, 0.5f);
                    float px = -totalWidth * 0.5f + x * (cellSize + spacing) + cellSize * 0.5f;
                    float py = y * (cellSize + spacing) + cellSize * 0.5f;
                    cellRect.anchoredPosition = new Vector2(px, py);
                    cellRect.sizeDelta = new Vector2(cellSize, cellSize);
                    var img = cellGo.AddComponent<Image>();
                    img.color = color;
                    img.raycastTarget = false;
                    if (cellSprite != null)
                    {
                        img.sprite = cellSprite;
                        img.type = cellSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
                    }
                    cells[x, y] = img;
                }
            }
        }

        private void CreateLabel(string name, string text, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(panel.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(120f, 24f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 16;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.9f, 0.9f, 1f, 0.95f);
        }

        /// <summary>
        /// Simple visualisation: treat each equipped card as one filled cell in its SlotType grid,
        /// filling from bottom row upward and left-to-right. This does not yet respect card shapes.
        /// </summary>
        private void RefreshGrids()
        {
            if (playerShip == null) return;
            var cards = playerShip.EquippedCards;
            if (cards == null) return;

            Color emptyShip = new Color(shipColor.r, shipColor.g, shipColor.b, 0.12f);
            Color emptyWeapon = new Color(weaponColor.r, weaponColor.g, weaponColor.b, 0.12f);
            Color emptyCargo = new Color(cargoColor.r, cargoColor.g, cargoColor.b, 0.12f);

            ClearGrid(shipCells, currentShipW, currentShipH, emptyShip);
            ClearGrid(weaponCells, currentWeaponW, currentWeaponH, emptyWeapon);
            ClearGrid(cargoCells, currentCargoW, currentCargoH, emptyCargo);

            var shipCards = new List<CardData>();
            var weaponCards = new List<CardData>();
            var cargoCards = new List<CardData>();

            foreach (var card in cards)
            {
                if (card == null) continue;
                switch (card.slotType)
                {
                    case SlotType.Ship: shipCards.Add(card); break;
                    case SlotType.Weapon: weaponCards.Add(card); break;
                    case SlotType.Cargo: cargoCards.Add(card); break;
                }
            }

            AutoPackCardsOntoGrid(shipCells, currentShipW, currentShipH, shipCards, shipColor);
            AutoPackCardsOntoGrid(weaponCells, currentWeaponW, currentWeaponH, weaponCards, weaponColor);
            AutoPackCardsOntoGrid(cargoCells, currentCargoW, currentCargoH, cargoCards, cargoColor);
        }

        private void ClearGrid(Image[,] cells, int width, int height, Color emptyColor)
        {
            if (cells == null) return;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var img = cells[x, y];
                    if (img != null) img.color = emptyColor;
                }
            }
        }

        /// <summary>
        /// Simple auto-packing: for each card, try to place its Tetris-like footprint into the grid without overlap,
        /// scanning from bottom-left to top-right. This is a visual-only packing (no interaction yet).
        /// </summary>
        private void AutoPackCardsOntoGrid(Image[,] cells, int gridWidth, int gridHeight, List<CardData> cards, Color fullColor)
        {
            if (cells == null || cards == null) return;

            bool[,] occupied = new bool[gridWidth, gridHeight];

            foreach (var card in cards)
            {
                if (card == null) continue;
                int cw = Mathf.Max(1, card.gridWidth);
                int ch = Mathf.Max(1, card.gridHeight);

                // If the card footprint is larger than the grid, skip it.
                if (cw > gridWidth || ch > gridHeight)
                    continue;

                bool placed = false;
                for (int oy = 0; oy <= gridHeight - ch && !placed; oy++)
                {
                    for (int ox = 0; ox <= gridWidth - cw && !placed; ox++)
                    {
                        if (CanPlaceCardAt(card, ox, oy, cw, ch, gridWidth, gridHeight, occupied))
                        {
                            PlaceCardAt(card, ox, oy, cw, ch, occupied, cells, fullColor);
                            placed = true;
                        }
                    }
                }
            }
        }

        private bool CanPlaceCardAt(CardData card, int originX, int originY, int cardWidth, int cardHeight, int gridWidth, int gridHeight, bool[,] occupied)
        {
            ulong mask = card.shapeMask;

            for (int ly = 0; ly < cardHeight; ly++)
            {
                for (int lx = 0; lx < cardWidth; lx++)
                {
                    int rTop = cardHeight - 1 - ly; // Card mask is row-major from top-left
                    int bitIndex = rTop * cardWidth + lx;
                    bool filled = ((mask >> bitIndex) & 1UL) != 0UL;
                    if (!filled) continue;

                    int gx = originX + lx;
                    int gy = originY + ly;
                    if (gx < 0 || gx >= gridWidth || gy < 0 || gy >= gridHeight)
                        return false;
                    if (occupied[gx, gy])
                        return false;
                }
            }

            return true;
        }

        private void PlaceCardAt(CardData card, int originX, int originY, int cardWidth, int cardHeight, bool[,] occupied, Image[,] cells, Color fullColor)
        {
            ulong mask = card.shapeMask;

            for (int ly = 0; ly < cardHeight; ly++)
            {
                for (int lx = 0; lx < cardWidth; lx++)
                {
                    int rTop = cardHeight - 1 - ly;
                    int bitIndex = rTop * cardWidth + lx;
                    bool filled = ((mask >> bitIndex) & 1UL) != 0UL;
                    if (!filled) continue;

                    int gx = originX + lx;
                    int gy = originY + ly;
                    if (gx < 0 || gx >= cells.GetLength(0) || gy < 0 || gy >= cells.GetLength(1))
                        continue;

                    occupied[gx, gy] = true;
                    var img = cells[gx, gy];
                    if (img != null) img.color = fullColor;
                }
            }
        }
    }
}

