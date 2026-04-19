using UnityEngine;
using UnityEngine.EventSystems;
using TitanOrbit.Input;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Reliable hold-to-fire on the shoot control. Prefer this over Button.onClick + EventTrigger (ordering conflicts).
    /// </summary>
    [DisallowMultipleComponent]
    public class MobileShootHoldRelay : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private MobileInputHandler handler;

        public void SetHandler(MobileInputHandler h) => handler = h;

        public void OnPointerDown(PointerEventData eventData) => handler?.SetShootHeld(true);

        public void OnPointerUp(PointerEventData eventData) => handler?.SetShootHeld(false);

        public void OnPointerExit(PointerEventData eventData) => handler?.SetShootHeld(false);
    }
}
