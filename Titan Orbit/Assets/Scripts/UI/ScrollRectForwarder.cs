using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Forwards drag and scroll events to the parent ScrollRect by directly applying
    /// movement to the scroll position. This works even when the pointer is over
    /// buttons or other raycast targets inside the content (e.g. orbit menu cards/ships).
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class ScrollRectForwarder : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
    {
        private ScrollRect _scrollRect;

        [Tooltip("Scroll sensitivity for mouse wheel (scroll delta is typically 3 or -3 per tick).")]
        [SerializeField] private float scrollSensitivity = 0.02f;

        [Tooltip("Drag sensitivity for pointer drag (pixels to normalized position).")]
        [SerializeField] private float dragSensitivity = 0.002f;

        private void Awake()
        {
            _scrollRect = GetComponentInParent<ScrollRect>();
        }

        private bool IsValid()
        {
            return _scrollRect != null && _scrollRect.enabled && _scrollRect.content != null && _scrollRect.viewport != null;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!IsValid()) return;
            _scrollRect.OnBeginDrag(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!IsValid() || !_scrollRect.vertical) return;

            RectTransform viewport = _scrollRect.viewport;
            RectTransform content = _scrollRect.content;
            float viewportHeight = viewport.rect.height;
            float contentHeight = content.rect.height;
            float scrollable = contentHeight - viewportHeight;
            if (scrollable <= 0f) return;

            // eventData.delta.y: positive when dragging pointer up. We want drag-up to scroll content up (see lower part).
            float step = eventData.delta.y / scrollable;
            float next = _scrollRect.verticalNormalizedPosition - step;
            _scrollRect.verticalNormalizedPosition = Mathf.Clamp01(next);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!IsValid()) return;
            _scrollRect.OnEndDrag(eventData);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!IsValid() || !_scrollRect.vertical) return;

            // eventData.scrollDelta.y: positive when scrolling wheel up. Scroll up = see content above = decrease normalized.
            float step = eventData.scrollDelta.y * scrollSensitivity;
            float next = _scrollRect.verticalNormalizedPosition - step;
            _scrollRect.verticalNormalizedPosition = Mathf.Clamp01(next);
        }
    }
}
