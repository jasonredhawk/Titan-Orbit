using UnityEngine;
using Shapes;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Enables Shapes ImmediateModePanel drawing on the main game canvas.
    /// Add this to the same GameObject as the Canvas; then any child with an
    /// ImmediateModePanel (e.g. ShipStatsShapesHUD) will be drawn.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class TitanOrbitShapesCanvas : ImmediateModeCanvas
    {
        public override void DrawCanvasShapes(ImCanvasContext ctx)
        {
            DrawPanels();
        }
    }
}
