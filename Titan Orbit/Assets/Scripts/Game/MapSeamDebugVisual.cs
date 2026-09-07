using Shapes;
using TitanOrbit;
using TitanOrbit.Generation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Temporary wrap-test overlay: draws the canonical map rectangle in world space
    /// (the four seams at ±half width / ±half height). Turn off with
    /// <see cref="TitanOrbitDebugFlags.ShowMapSeamLines"/>. Client presentation only.
    /// </summary>
    [ExecuteAlways]
    public class MapSeamDebugVisual : ImmediateModeShapeDrawer
    {
        /// <summary>Height above the play plane so the line wins the depth test vs territory fill.</summary>
        const float LineY = 0.35f;

        /// <summary>Screen-pixel thickness so the seam stays readable at any camera height.</summary>
        const float LineThicknessPixels = 2.75f;

        /// <summary>Cyan — pops against dark space and team territory tints.</summary>
        static readonly Color SeamColor = new Color(0.15f, 0.95f, 1f, 0.92f);

        /// <summary>
        /// Ensures a drawer exists when the client visualizer starts. Safe to call every enable.
        /// </summary>
        public static void EnsureExists()
        {
            var go = GameObject.Find("MapSeamDebugVisual");
            if (go == null)
                go = new GameObject("MapSeamDebugVisual");

            if (go.GetComponent<MapSeamDebugVisual>() == null)
                go.AddComponent<MapSeamDebugVisual>();
        }

        /// <summary>
        /// [UNITY] Shapes callback — one billboard rectangle on the wrap planes.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            if (cam == null || !TitanOrbitDebugFlags.ShowMapSeamLines)
                return;
            if (!ToroidalMap.TryGetMapSize(out float mapW, out float mapH))
                return;

            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;
            Vector3 sw = new Vector3(-halfW, LineY, -halfH);
            Vector3 se = new Vector3(halfW, LineY, -halfH);
            Vector3 ne = new Vector3(halfW, LineY, halfH);
            Vector3 nw = new Vector3(-halfW, LineY, halfH);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.BlendMode = ShapesBlendMode.Transparent;
                Draw.ThicknessSpace = ThicknessSpace.Pixels;
                Draw.LineGeometry = LineGeometry.Billboard;
                Draw.Line(sw, se, LineThicknessPixels, LineEndCap.None, SeamColor);
                Draw.Line(se, ne, LineThicknessPixels, LineEndCap.None, SeamColor);
                Draw.Line(ne, nw, LineThicknessPixels, LineEndCap.None, SeamColor);
                Draw.Line(nw, sw, LineThicknessPixels, LineEndCap.None, SeamColor);
            }
        }
    }
}
