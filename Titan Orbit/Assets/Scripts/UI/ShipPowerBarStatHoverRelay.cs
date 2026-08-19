using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Invisible padded hit target over a colourful power bar. Maps the pointer to
    /// one of the ten tiny ODEMC slots and opens <see cref="ShipPowerBarStatTooltip"/>.
    /// Forwards click and drag to the parent <see cref="Button"/> / <see cref="ScrollRect"/>
    /// so hovering a slot does not steal chassis purchase or list scroll.
    /// <para>
    /// [UNITY] A child Image with <c>raycastTarget = true</c> wins the EventSystem hit
    /// over the card Button underneath. We therefore re-raise click/drag ourselves.
    /// </para>
    /// Presentation-only — no ECS writes. Paired with <see cref="ShipUpgradeTreePowerBarUI"/>.
    /// </summary>
    public class ShipPowerBarStatHoverRelay : MonoBehaviour,
        IPointerEnterHandler,
        IPointerMoveHandler,
        IPointerExitHandler,
        IPointerClickHandler,
        IInitializePotentialDragHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        /// <summary>Bar that owns the ten slot rects and the last painted breakdown.</summary>
        public ShipUpgradeTreePowerBarUI Owner;

        int _hoverSlot = -1;

        /// <summary>Pointer entered the padded bar — pick a slot and show that tip.</summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            ShowSlotUnderPointer(eventData);
        }

        /// <summary>Pointer slid across the bar — retarget when the slot changes.</summary>
        public void OnPointerMove(PointerEventData eventData)
        {
            ShowSlotUnderPointer(eventData);
        }

        /// <summary>Pointer left the padded bar — hide if we still own the shared tip.</summary>
        public void OnPointerExit(PointerEventData eventData)
        {
            _hoverSlot = -1;
            ShipPowerBarStatTooltip.Hide();
        }

        /// <summary>
        /// Treat a click on the colourful bar as a click on the tree card / gear tile.
        /// [UNITY] Button.OnPointerClick is public — we call it so purchase still works.
        /// </summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            Button button = GetComponentInParent<Button>();
            if (button != null && button.interactable)
                button.OnPointerClick(eventData);
        }

        /// <summary>Forwards potential-drag so a ScrollRect parent can still start a flick.</summary>
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.initializePotentialDrag);
        }

        /// <summary>Forwards begin-drag to a parent ScrollRect (tree / shop lists).</summary>
        public void OnBeginDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.beginDragHandler);
        }

        /// <summary>Forwards drag so scrolling does not stick when the pointer started on the bar.</summary>
        public void OnDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.dragHandler);
        }

        /// <summary>Forwards end-drag to the same parent that received begin-drag.</summary>
        public void OnEndDrag(PointerEventData eventData)
        {
            Forward(eventData, ExecuteEvents.endDragHandler);
        }

        void OnDisable()
        {
            _hoverSlot = -1;
            ShipPowerBarStatTooltip.Hide();
        }

        /// <summary>
        /// Resolves which of the ten slots contains the pointer (or is nearest in the pad)
        /// and opens the shared tip. Skips rebuild when the slot did not change.
        /// </summary>
        void ShowSlotUnderPointer(PointerEventData eventData)
        {
            if (Owner == null)
                return;

            int slot = Owner.PickSlotAtScreenPoint(eventData.position, eventData.enterEventCamera);
            if (slot < 0)
                return;
            if (slot == _hoverSlot && ShipPowerBarStatTooltip.ActiveSlot == slot)
                return;

            _hoverSlot = slot;
            Owner.ShowStatTooltip(slot);
        }

        /// <summary>
        /// Re-raises a pointer event on the first parent that implements
        /// <typeparamref name="T"/> (usually ScrollRect). Skips this GameObject
        /// so we do not recurse into ourselves.
        /// </summary>
        void Forward<T>(PointerEventData eventData, ExecuteEvents.EventFunction<T> functor)
            where T : IEventSystemHandler
        {
            if (transform.parent == null)
                return;
            ExecuteEvents.ExecuteHierarchy(transform.parent.gameObject, eventData, functor);
        }
    }
}
