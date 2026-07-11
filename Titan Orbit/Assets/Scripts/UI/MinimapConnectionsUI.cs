using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    // --- Type members ---
    /// <summary>
    /// UGUI RawImage placeholder for minimap territory connection triangles. OnPopulateMesh is
    /// intentionally empty until planet adjacency is exposed from ECS for overlay drawing.
    /// Parent <see cref="MinimapController"/> toggles visibility. Client presentation only —
    /// does not affect sim or NetCode state.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MinimapConnectionsUI : RawImage
    {
        // [UNITY] Shared 1×1 white texture for invisible mesh placeholder.
        static Texture2D _whiteTex;

        /// <summary>
        /// [UNITY] Awake — disable raycasts and assign dummy texture so layout does not warn.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(1, 1);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
            }

            texture = _whiteTex;
            color = Color.white;
        }

        /// <summary>
        /// [UNITY] Suppress default quad — connections will be drawn when ECS graph data exists.
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
        }
    }
}
