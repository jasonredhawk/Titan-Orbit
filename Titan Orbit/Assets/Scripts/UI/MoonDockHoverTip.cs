using UnityEngine;
using UnityEngine.EventSystems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Pointer hover tip for moon-dock GEAR tiles and ORDNANCE rails.
    /// Builds one shared <see cref="ShipStatTooltipChrome"/> card under the parent canvas.
    /// Presentation-only — no ECS writes.
    /// </summary>
    public class MoonDockHoverTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string Caption = "GEAR";
        public string Body;

        static ShipStatTooltipChrome.Handles s_Chrome;
        static Canvas s_Canvas;

        /// <summary>Shows the shared chrome next to this rect with the current caption/body.</summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            if (string.IsNullOrWhiteSpace(Body))
                return;
            EnsureChrome();
            if (s_Chrome.Root == null)
                return;

            if (s_Chrome.CaptionLabel != null)
                s_Chrome.CaptionLabel.text = string.IsNullOrWhiteSpace(Caption) ? "GEAR" : Caption;
            if (s_Chrome.BodyLabel != null)
                s_Chrome.BodyLabel.text = Body;

            var self = transform as RectTransform;
            var tip = s_Chrome.RootRect;
            if (self != null && tip != null && s_Canvas != null)
            {
                tip.SetParent(s_Canvas.transform, false);
                Vector3[] corners = new Vector3[4];
                self.GetWorldCorners(corners);
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(s_Canvas.worldCamera, corners[2]);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    s_Canvas.transform as RectTransform, screen, s_Canvas.worldCamera, out Vector2 local);
                tip.pivot = new Vector2(0f, 1f);
                tip.anchoredPosition = local + new Vector2(8f, 8f);
                tip.sizeDelta = new Vector2(320f, 220f);
            }

            s_Chrome.Root.SetActive(true);
        }

        /// <summary>Hides the shared chrome when the pointer leaves this tile.</summary>
        public void OnPointerExit(PointerEventData eventData)
        {
            if (s_Chrome.Root != null)
                s_Chrome.Root.SetActive(false);
        }

        void OnDisable()
        {
            if (s_Chrome.Root != null)
                s_Chrome.Root.SetActive(false);
        }

        static void EnsureChrome()
        {
            if (s_Chrome.Root != null)
                return;
            s_Canvas = Object.FindFirstObjectByType<Canvas>();
            if (s_Canvas == null)
                return;
            s_Chrome = ShipStatTooltipChrome.Build(
                "MoonDockHoverTip",
                s_Canvas.transform,
                "GEAR",
                320f,
                220f,
                1f);
            if (s_Chrome.Root != null)
                s_Chrome.Root.SetActive(false);
        }
    }
}
