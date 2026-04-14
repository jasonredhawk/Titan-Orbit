using UnityEngine;
using UnityEngine.InputSystem;

namespace TitanOrbit.Input
{
    /// <summary>
    /// Handles mobile-specific input (touch controls, virtual joystick)
    /// </summary>
    public class MobileInputHandler : MonoBehaviour
    {
        [Header("Mobile Input Settings")]
        [SerializeField] private RectTransform joystickBackground;
        [SerializeField] private RectTransform joystickHandle;
        [SerializeField] private RectTransform shootButton;
        [SerializeField] private float joystickRadius = 50f;
        [SerializeField, Range(0f, 0.5f)] private float joystickDeadZone = 0.08f;

        private Vector2 joystickInput = Vector2.zero;
        private bool shootButtonPressed = false;
        private int joystickFingerId = -1;
        private int shootFingerId = -1;
        private Canvas rootCanvas;

        public Vector2 JoystickInput => joystickInput;
        public bool ShootButtonPressed => shootButtonPressed;

        private void Awake()
        {
            rootCanvas = joystickBackground != null ? joystickBackground.GetComponentInParent<Canvas>() : null;
        }

        private void Update()
        {
            if (joystickBackground == null || joystickHandle == null || shootButton == null)
            {
                return;
            }

            Touchscreen touchscreen = Touchscreen.current;
            if (touchscreen == null)
            {
                if (joystickFingerId != -1) ReleaseJoystick();
                if (shootFingerId != -1)
                {
                    shootFingerId = -1;
                    shootButtonPressed = false;
                }
                return;
            }

            bool joystickFingerTrackedThisFrame = false;
            bool shootFingerTrackedThisFrame = false;
            bool hasPressedTouch = false;

            foreach (var touchControl in touchscreen.touches)
            {
                int fingerId = touchControl.touchId.ReadValue();
                if (fingerId <= 0) continue;

                Vector2 touchPosition = touchControl.position.ReadValue();
                bool isPressed = touchControl.press.isPressed;
                var phase = touchControl.phase.ReadValue();
                bool isTouchActive = isPressed
                    || phase == UnityEngine.InputSystem.TouchPhase.Began
                    || phase == UnityEngine.InputSystem.TouchPhase.Moved
                    || phase == UnityEngine.InputSystem.TouchPhase.Stationary;
                bool isTouchEnded = phase == UnityEngine.InputSystem.TouchPhase.Ended
                    || phase == UnityEngine.InputSystem.TouchPhase.Canceled;

                if (isTouchActive)
                    hasPressedTouch = true;

                if (fingerId == joystickFingerId)
                {
                    joystickFingerTrackedThisFrame = true;
                    if (!isTouchActive || isTouchEnded)
                    {
                        ReleaseJoystick();
                    }
                    else
                    {
                        UpdateJoystick(touchPosition);
                    }
                    continue;
                }

                if (fingerId == shootFingerId)
                {
                    shootFingerTrackedThisFrame = true;
                    shootButtonPressed = isTouchActive && !isTouchEnded && IsInsideRect(shootButton, touchPosition);
                    if (!shootButtonPressed)
                    {
                        shootFingerId = -1;
                    }
                    continue;
                }

                if (phase != UnityEngine.InputSystem.TouchPhase.Began)
                {
                    continue;
                }

                if (joystickFingerId == -1 && IsInsideRect(joystickBackground, touchPosition))
                {
                    joystickFingerId = fingerId;
                    joystickFingerTrackedThisFrame = true;
                    UpdateJoystick(touchPosition);
                    continue;
                }

                if (shootFingerId == -1 && IsInsideRect(shootButton, touchPosition))
                {
                    shootFingerId = fingerId;
                    shootFingerTrackedThisFrame = true;
                    shootButtonPressed = true;
                }
            }

            if (joystickFingerId != -1 && !joystickFingerTrackedThisFrame)
            {
                ReleaseJoystick();
            }

            if (shootFingerId != -1 && !shootFingerTrackedThisFrame)
            {
                shootFingerId = -1;
                shootButtonPressed = false;
            }

            if (!hasPressedTouch && joystickFingerId == -1 && shootFingerId == -1)
            {
                shootButtonPressed = false;
            }
        }

        public void Configure(RectTransform joystickBg, RectTransform joystickKnob, RectTransform shootButtonRect, float radius)
        {
            joystickBackground = joystickBg;
            joystickHandle = joystickKnob;
            shootButton = shootButtonRect;
            if (radius > 0f) joystickRadius = radius;
            rootCanvas = joystickBackground != null ? joystickBackground.GetComponentInParent<Canvas>() : null;
            ReleaseJoystick();
            shootButtonPressed = false;
        }

        private bool IsInsideRect(RectTransform rectTransform, Vector2 screenPosition)
        {
            if (rectTransform == null) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPosition, GetEventCamera());
        }

        private UnityEngine.Camera GetEventCamera()
        {
            if (rootCanvas == null) return null;
            return rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
        }

        private float GetEffectiveJoystickRadius()
        {
            if (joystickRadius > 0f) return joystickRadius;
            if (joystickBackground == null) return 50f;
            return Mathf.Max(1f, Mathf.Min(joystickBackground.rect.width, joystickBackground.rect.height) * 0.5f);
        }

        private void ReleaseJoystick()
        {
            joystickFingerId = -1;
            joystickInput = Vector2.zero;
            if (joystickHandle != null)
            {
                joystickHandle.anchoredPosition = Vector2.zero;
            }
        }

        private void UpdateJoystick(Vector2 screenPosition)
        {
            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                joystickBackground,
                screenPosition,
                GetEventCamera(),
                out localPoint
            );

            float radius = GetEffectiveJoystickRadius();
            Vector2 clamped = Vector2.ClampMagnitude(localPoint, radius);
            Vector2 normalized = clamped / radius;
            joystickInput = normalized.magnitude < joystickDeadZone ? Vector2.zero : normalized;

            joystickHandle.anchoredPosition = clamped;
        }

        public void OnShootButtonPressed()
        {
            shootButtonPressed = true;
        }

        public void OnShootButtonReleased()
        {
            shootButtonPressed = false;
            shootFingerId = -1;
        }
    }
}
