using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Drag on the Your Ship picture orbits the live camera around the hull.
    /// A tap does not click the card under the picture. That card used to buy
    /// the docked moon's hull at the same upgrade-tree slot.
    /// </summary>
    public sealed class OrbitMenuPortraitTurntable : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>Extra yaw, in degrees, added on top of the theatrical angle and auto-orbit.</summary>
        public static float DragYawDegrees { get; private set; }

        /// <summary>True while the pointer is down on the picture, so auto-orbit waits.</summary>
        public static bool IsDragging { get; private set; }

        /// <summary>How many degrees the view yaws per pixel of horizontal drag.</summary>
        const float DegreesPerPixel = 0.4f;

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
            // Hold pauses auto-orbit. This object sits on the picture, above the
            // card, so the card button underneath must not receive the tap.
            IsDragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData == null)
                return;
            DragYawDegrees += eventData.delta.x * DegreesPerPixel;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            // Release only ends the drag. Do not Invoke a parent Button —
            // that bought the same ladder slot on another family's moon.
            IsDragging = false;
        }
    }
}
