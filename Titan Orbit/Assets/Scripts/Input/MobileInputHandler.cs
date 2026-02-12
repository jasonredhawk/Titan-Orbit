using UnityEngine;
using UnityEngine.EventSystems;

namespace TitanOrbit.Input
{
    /// <summary>
    /// Handles mobile-specific input (touch controls, virtual joystick)
    /// </summary>
    public class MobileInputHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        [Header("Mobile Input Settings")]
        [SerializeField] private RectTransform joystickBackground;
        [SerializeField] private RectTransform joystickHandle;
        [SerializeField] private RectTransform shootButton;
        [SerializeField] private float joystickRadius = 50f;

        private Vector2 joystickInput = Vector2.zero;
        private bool isJoystickActive = false;
        private bool shootButtonPressed = false;

        public Vector2 JoystickInput => joystickInput;
        public bool ShootButtonPressed => shootButtonPressed;

        private void Start()
        {
            // Hide joystick if not on mobile
            if (!Application.isMobilePlatform)
            {
                if (joystickBackground != null) joystickBackground.gameObject.SetActive(false);
                if (shootButton != null) shootButton.gameObject.SetActive(false);
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (joystickBackground != null && RectTransformUtility.RectangleContainsScreenPoint(joystickBackground, eventData.position))
            {
                isJoystickActive = true;
                UpdateJoystick(eventData);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (isJoystickActive)
            {
                UpdateJoystick(eventData);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (isJoystickActive)
            {
                isJoystickActive = false;
                joystickInput = Vector2.zero;
                
                if (joystickHandle != null)
                {
                    joystickHandle.anchoredPosition = Vector2.zero;
                }
            }
        }

        private void UpdateJoystick(PointerEventData eventData)
        {
            if (joystickBackground == null || joystickHandle == null) return;

            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                joystickBackground, 
                eventData.position, 
                eventData.pressEventCamera, 
                out localPoint
            );

            joystickInput = Vector2.ClampMagnitude(localPoint, joystickRadius);
            joystickInput /= joystickRadius; // Normalize

            joystickHandle.anchoredPosition = joystickInput * joystickRadius;
        }

        public void OnShootButtonPressed()
        {
            shootButtonPressed = true;
        }

        public void OnShootButtonReleased()
        {
            shootButtonPressed = false;
        }
    }
}
