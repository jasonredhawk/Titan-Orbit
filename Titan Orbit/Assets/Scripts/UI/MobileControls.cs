using UnityEngine;
using UnityEngine.UI;
using TitanOrbit.Input;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Mobile UI controls including virtual joystick and shoot button
    /// </summary>
    public class MobileControls : MonoBehaviour
    {
        [Header("Mobile Controls")]
        [SerializeField] private GameObject mobileControlsPanel;
        [SerializeField] private RectTransform joystickBackground;
        [SerializeField] private RectTransform joystickHandle;
        [SerializeField] private Button shootButton;
        [SerializeField] private CanvasScaler canvasScaler;

        [Header("Settings")]
        [SerializeField] private bool autoDetectMobile = true;
        [SerializeField] private bool forceMobileControls = false;

        private MobileInputHandler mobileInputHandler;

        private void Start()
        {
            bool isMobile = autoDetectMobile ? Application.isMobilePlatform : forceMobileControls;

            if (mobileControlsPanel != null)
            {
                mobileControlsPanel.SetActive(isMobile);
            }

            if (isMobile)
            {
                SetupMobileControls();
            }
        }

        private void SetupMobileControls()
        {
            // Get or create mobile input handler
            mobileInputHandler = FindObjectOfType<MobileInputHandler>();
            if (mobileInputHandler == null)
            {
                GameObject handlerObj = new GameObject("MobileInputHandler");
                mobileInputHandler = handlerObj.AddComponent<MobileInputHandler>();
            }

            // Setup joystick
            if (joystickBackground != null && joystickHandle != null)
            {
                // Joystick setup is handled by MobileInputHandler
            }

            // Setup shoot button
            if (shootButton != null)
            {
                shootButton.onClick.AddListener(OnShootButtonClicked);
            }

            // Setup canvas scaler for different screen sizes
            if (canvasScaler != null)
            {
                canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasScaler.referenceResolution = new Vector2(1920, 1080);
                canvasScaler.matchWidthOrHeight = 0.5f;
            }
        }

        private void OnShootButtonClicked()
        {
            if (mobileInputHandler != null)
            {
                mobileInputHandler.OnShootButtonPressed();
            }
        }

        private void Update()
        {
            // Handle touch-to-move alternative
            if (Application.isMobilePlatform && UnityEngine.Input.touchCount > 0)
            {
                Touch touch = UnityEngine.Input.GetTouch(0);
                
                // Check if touch is not on UI
                if (!IsPointerOverUI(touch.position))
                {
                // Handle touch movement
                UnityEngine.Camera mainCam = UnityEngine.Camera.main;
                if (mainCam != null)
                {
                    Vector3 worldPos = mainCam.ScreenToWorldPoint(new Vector3(touch.position.x, touch.position.y, mainCam.nearClipPlane));
                }
                    // Movement would be handled by input system
                }
            }
        }

        private bool IsPointerOverUI(Vector2 screenPosition)
        {
            // Simple check - in production, use EventSystem
            return false;
        }
    }
}
