using Shapes;
using UnityEngine;

namespace TitanOrbit.UI
{
    // --- Type members ---
    /// <summary>
    /// Shapes immediate-mode panel for minimap territory connection lines between allied planets.
    /// DrawPanelShapes is intentionally empty until planet adjacency data is exposed from ECS
    /// in a minimap-friendly format. Parent <see cref="MinimapController"/> enables/disables this panel.
    /// Client presentation only.
    /// </summary>
    public class MinimapConnectionsShapesPanel : ImmediateModePanel
    {
        /// <summary>
        /// [UNITY] Shapes draw callback — no-op until ECS planet graph overlay is implemented.
        /// </summary>
        public override void DrawPanelShapes(Rect rect, ImCanvasContext ctx)
        {
        }
    }
}
