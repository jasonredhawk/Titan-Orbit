using UnityEngine;
using UnityEngine.EventSystems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Invisible hit target on a speedometer band. Forwards pointer enter/exit to
    /// <see cref="ShipSpeedometerHUD"/> so section rollovers only appear while hovered.
    /// [UNITY] Requires an EventSystem + GraphicRaycaster on the HUD canvas.
    /// </summary>
    public class ShipSpeedometerHoverZone : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Which HUD section this pad represents.</summary>
        public SpeedometerStatSection Section;

        /// <summary>Owner that owns the floating tooltip panel.</summary>
        public ShipSpeedometerHUD Owner;

        /// <summary>[UNITY] Pointer entered this pad — show that section's breakdown.</summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            if (Owner != null)
                Owner.ShowStatTooltip(Section);
        }

        /// <summary>[UNITY] Pointer left this pad — hide if this section is still the active one.</summary>
        public void OnPointerExit(PointerEventData eventData)
        {
            if (Owner != null)
                Owner.HideStatTooltip(Section);
        }
    }
}
