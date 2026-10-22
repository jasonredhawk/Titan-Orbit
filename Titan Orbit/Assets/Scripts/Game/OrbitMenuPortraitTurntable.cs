using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Drag on the Your Ship picture orbits the live camera around the hull.
    /// A short tap still clicks the card, because this image sits above the
    /// card button and would otherwise swallow that hull-swap click.
    /// </summary>
    public sealed class OrbitMenuPortraitTurntable : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>Extra yaw, in degrees, added on top of the theatrical angle and auto-orbit.</summary>
        public static float DragYawDegrees { get; private set; }

        /// <summary>True while the pointer is down on the picture, so auto-orbit waits.</summary>
        public static bool IsDragging { get; private set; }

        /// <summary>How many degrees the view yaws per pixel of horizontal drag.</summary>
        const float DegreesPerPixel = 0.4f;

        /// <summary>Movement below this is a tap, not a spin, and still activates the card.</summary>
        const float ClickSlopPixels = 8f;

        float _moved;

        void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            // The Solo / Moon / Stars strip used to be a sibling of this picture.
            // Drop it if a menu built before this change is still open.
            Transform parent = transform.parent;
            if (parent != null)
            {
                Transform bar = parent.Find("PortraitToggles");
                if (bar != null)
                    Destroy(bar.gameObject);
            }

            RawImage raw = GetComponent<RawImage>();
            if (raw != null)
                OrbitMenuShipPortrait.Bind(raw);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            IsDragging = true;
            _moved = 0f;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData == null)
                return;
            DragYawDegrees += eventData.delta.x * DegreesPerPixel;
            _moved += eventData.delta.magnitude;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            IsDragging = false;
            if (_moved > ClickSlopPixels)
                return;

            Button button = GetComponentInParent<Button>();
            if (button != null && button.IsInteractable())
                button.onClick.Invoke();
        }
    }
}
