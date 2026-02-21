using UnityEngine;
using TitanOrbit.Entities;
using Shapes;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Shapes-based HUD that draws ship health, energy, and gems as prominent progress bars
    /// (like the Shapes IMPanelSample / ChargeBar style). Must be on a RectTransform under a canvas
    /// that has an ImmediateModeCanvas (e.g. TitanOrbitShapesCanvas).
    /// </summary>
    public class ShipStatsShapesHUD : ImmediateModePanel
    {
        [Header("Style")]
        [SerializeField] private Color healthColor = new Color(0.95f, 0.25f, 0.2f, 1f);
        [SerializeField] private Color healthEmptyColor = new Color(0.2f, 0.08f, 0.06f, 0.9f);
        [SerializeField] private Color energyColor = new Color(0.2f, 0.65f, 0.95f, 1f);
        [SerializeField] private Color energyEmptyColor = new Color(0.06f, 0.12f, 0.2f, 0.9f);
        [SerializeField] private Color gemsColor = new Color(0.2f, 0.9f, 0.45f, 1f);
        [SerializeField] private Color gemsEmptyColor = new Color(0.06f, 0.18f, 0.1f, 0.9f);
        [SerializeField] private Color borderColor = new Color(0.35f, 0.4f, 0.55f, 0.9f);
        [SerializeField] private float cornerRadius = 8f;
        [SerializeField] private float borderThickness = 4f;
        [SerializeField] private float barGap = 6f;
        [SerializeField] private float margin = 10f;
        [Range(8f, 48f)] [SerializeField] private float labelFontSize = 22f;

        private Starship _playerShip;
        private const int BAR_COUNT = 3;

        private Starship GetPlayerShip()
        {
            if (_playerShip != null && !_playerShip.IsDead) return _playerShip;
            _playerShip = null;
            foreach (var ship in FindObjectsOfType<Starship>())
            {
                if (ship.IsOwner) { _playerShip = ship; break; }
            }
            return _playerShip;
        }

        public override void DrawPanelShapes(Rect rect, ImCanvasContext ctx)
        {
            Starship ship = GetPlayerShip();
            if (ship == null)
                return;

            float totalGap = barGap * (BAR_COUNT - 1);
            float barHeight = (rect.height - margin * 2 - totalGap) / BAR_COUNT;
            if (barHeight <= 0) return;

            float y = rect.yMin + margin;
            float left = rect.xMin + margin;
            float right = rect.xMax - margin;
            float width = right - left;

            // Health bar
            DrawOneBar(new Rect(left, y, width, barHeight), ship.CurrentHealth / ship.MaxHealth,
                "HEALTH", $"{ship.CurrentHealth:F0}/{ship.MaxHealth:F0}", healthColor, healthEmptyColor);
            y += barHeight + barGap;

            // Energy bar
            float energyCap = ship.EnergyCapacity;
            DrawOneBar(new Rect(left, y, width, barHeight), energyCap > 0 ? ship.CurrentEnergy / energyCap : 0f,
                "ENERGY", $"{ship.CurrentEnergy:F0}/{energyCap:F0}", energyColor, energyEmptyColor);
            y += barHeight + barGap;

            // Gems bar
            float gemCap = ship.GemCapacity;
            DrawOneBar(new Rect(left, y, width, barHeight), gemCap > 0 ? ship.CurrentGems / gemCap : 0f,
                "GEMS", $"{ship.CurrentGems:F0}/{gemCap:F0}", gemsColor, gemsEmptyColor);

            // Panel border
            Draw.RectangleBorder(rect, borderThickness, cornerRadius, borderColor);
        }

        private void DrawOneBar(Rect barRect, float fill01, string label, string valueText, Color fillColor, Color emptyColor)
        {
            fill01 = Mathf.Clamp01(fill01);

            // Background (empty)
            Draw.Rectangle(barRect, cornerRadius * 0.5f, emptyColor);

            // Fill
            if (fill01 > 0.001f)
            {
                Rect fillRect = new Rect(barRect.x, barRect.y, barRect.width * fill01, barRect.height);
                Draw.Rectangle(fillRect, cornerRadius * 0.5f, fillColor);
            }

            // Inner border for bar
            Draw.RectangleBorder(barRect, 2f, cornerRadius * 0.5f, new Color(0, 0, 0, 0.3f));

            // Label (left) and value (right)
            Draw.FontSize = labelFontSize;
            Vector2 labelPos = new Vector2(barRect.xMin + 6f, barRect.yMax - barRect.height * 0.35f);
            Draw.Text(labelPos, label, TextAlign.BaselineLeft);
            Vector2 valuePos = new Vector2(barRect.xMax - 6f, barRect.yMax - barRect.height * 0.35f);
            Draw.Text(valuePos, valueText, TextAlign.BaselineRight);
        }
    }
}
