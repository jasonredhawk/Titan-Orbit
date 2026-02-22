using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using Shapes;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Top-left ship stats HUD: horizontal progress bars (Health, Energy, Gems, People)
    /// stacked with spacing. Same width as minimap. Current value at the right end of each bar.
    /// Requires ImmediateModeCanvas (e.g. TitanOrbitShapesCanvas) on the canvas.
    /// </summary>
    public class ShipStatsFpsStyleHUD : ImmediateModePanel
    {
        [Header("Layout (panel width = same as minimap)")]
        [SerializeField] private float margin = 12f;
        [SerializeField] private float barGap = 8f;
        [SerializeField] private float barHeight = 18f;

        [Header("Style")]
        [SerializeField] private float cornerRadius = 6f;
        [SerializeField] private float borderThickness = 2f;
        [Range(8f, 36f)] [SerializeField] private float valueFontSize = 18f;
        [Range(8f, 28f)] [SerializeField] private float labelFontSize = 14f;

        [Header("Colors")]
        [SerializeField] private Color healthColor = new Color(0.2f, 0.9f, 0.45f, 1f);
        [SerializeField] private Color energyColor = new Color(0.2f, 0.65f, 0.95f, 1f);
        [SerializeField] private Color gemsColor = new Color(0.95f, 0.25f, 0.2f, 1f);
        [SerializeField] private Color peopleColor = new Color(0.9f, 0.75f, 0.3f, 1f);
        [SerializeField] private Color emptyColor = new Color(0.12f, 0.12f, 0.18f, 0.9f);
        [SerializeField] private Color borderColor = new Color(0.35f, 0.4f, 0.5f, 0.9f);
        [SerializeField] private Color valueColor = new Color(1f, 1f, 1f, 0.95f);
        [SerializeField] private Color labelColor = new Color(0.85f, 0.85f, 0.9f, 0.95f);

        private Starship _playerShip;

        private Starship GetPlayerShip()
        {
            if (_playerShip != null && !_playerShip.IsDead) return _playerShip;
            _playerShip = null;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
                return null;
            NetworkObject localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            if (localPlayer == null) return null;
            var ship = localPlayer.GetComponent<Starship>();
            if (ship != null && !ship.IsDead) _playerShip = ship;
            return _playerShip;
        }

        public override void DrawPanelShapes(Rect rect, ImCanvasContext ctx)
        {
            Starship ship = GetPlayerShip();
            if (ship == null)
                return;

            float left = rect.xMin + margin;
            float right = rect.xMax - margin;
            float y = rect.yMax - margin - barHeight; // top-down: first bar at top

            // Reserve space for label (left) and value text (right)
            float labelWidth = 52f;
            float valueWidth = valueFontSize * 2.5f;
            float barLeft = left + labelWidth;
            float barMaxRight = right - valueWidth - 6f;
            float barWidth = barMaxRight - barLeft;

            DrawOneBar(left, barLeft, y, barWidth, barHeight, ship.CurrentHealth / (ship.MaxHealth > 0 ? ship.MaxHealth : 1f), healthColor, "Health", Mathf.FloorToInt(ship.CurrentHealth).ToString());
            y -= (barHeight + barGap);

            float energyCap = ship.EnergyCapacity;
            DrawOneBar(left, barLeft, y, barWidth, barHeight, energyCap > 0 ? ship.CurrentEnergy / energyCap : 0f, energyColor, "Energy", Mathf.FloorToInt(ship.CurrentEnergy).ToString());
            y -= (barHeight + barGap);

            float gemCap = ship.GemCapacity;
            DrawOneBar(left, barLeft, y, barWidth, barHeight, gemCap > 0 ? ship.CurrentGems / gemCap : 0f, gemsColor, "Gems", Mathf.FloorToInt(ship.CurrentGems).ToString());
            y -= (barHeight + barGap);

            float peopleCap = ship.PeopleCapacity;
            DrawOneBar(left, barLeft, y, barWidth, barHeight, peopleCap > 0 ? ship.CurrentPeople / peopleCap : 0f, peopleColor, "People", Mathf.FloorToInt(ship.CurrentPeople).ToString());
        }

        private void DrawOneBar(float labelX, float x, float y, float w, float h, float fill01, Color fillColor, string labelText, string valueText)
        {
            fill01 = Mathf.Clamp01(fill01);
            Rect barRect = new Rect(x, y, w, h);

            Draw.Rectangle(barRect, cornerRadius * 0.5f, emptyColor);
            if (fill01 > 0.001f)
            {
                Rect fillRect = new Rect(x, y, w * fill01, h);
                Draw.Rectangle(fillRect, cornerRadius * 0.5f, fillColor);
            }

            Draw.RectangleBorder(barRect, 1f, cornerRadius * 0.5f, borderColor);

            float baselineY = barRect.yMax - h * 0.35f;
            Draw.FontSize = labelFontSize;
            Draw.Text(new Vector2(labelX, baselineY), labelText, TextAlign.BaselineLeft, labelColor);
            Draw.FontSize = valueFontSize;
            Draw.Text(new Vector2(barRect.xMax + 6f, baselineY), valueText, TextAlign.BaselineLeft, valueColor);
        }
    }
}
