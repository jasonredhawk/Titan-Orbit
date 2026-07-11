using UnityEngine;
using UnityEngine.EventSystems;
using TitanOrbit.Input;

namespace TitanOrbit.UI
{
    // --- Type members ---
    /// <summary>
    /// Touch/click control that toggles the same space-brakes mode as Left Ctrl on desktop.
    /// </summary>
    public class MobileSpaceBrakesToggle : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            PlayerInputHandler p = Object.FindFirstObjectByType<PlayerInputHandler>();
            p?.ToggleSpaceBrakes();
        }
    }
}
