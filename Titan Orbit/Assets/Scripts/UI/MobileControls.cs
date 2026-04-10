using UnityEngine;
using UnityEngine.EventSystems;
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
        [SerializeField] private RectTransform shootButton;
        [SerializeField] private CanvasScaler canvasScaler;

        [Header("Settings")]
        [SerializeField] private bool autoDetectMobile = true;
        [SerializeField] private bool forceMobileControls = false;
        [SerializeField] private float bottomPadding = 48f;
        [SerializeField] private float sidePadding = 48f;
        [SerializeField] private float joystickSize = 220f;
        [SerializeField] private float joystickHandleSize = 110f;
        [SerializeField] private float shootButtonSize = 190f;

        private MobileInputHandler mobileInputHandler;

        private void Start()
        {
            bool isMobile = forceMobileControls || (autoDetectMobile && Application.isMobilePlatform);

            EnsureEventSystemExists();
            EnsurePanelHierarchy();

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
            mobileInputHandler = FindFirstObjectByType<MobileInputHandler>();
            if (mobileInputHandler == null)
            {
                GameObject handlerObj = new GameObject("MobileInputHandler");
                mobileInputHandler = handlerObj.AddComponent<MobileInputHandler>();
            }

            // Setup canvas scaler for different screen sizes
            if (canvasScaler != null)
            {
                canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasScaler.referenceResolution = new Vector2(1920, 1080);
                canvasScaler.matchWidthOrHeight = 0.5f;
            }

            float joystickRadius = Mathf.Max(1f, joystickSize * 0.5f);
            mobileInputHandler.Configure(joystickBackground, joystickHandle, shootButton, joystickRadius);
        }

        private void EnsurePanelHierarchy()
        {
            if (mobileControlsPanel == null)
            {
                mobileControlsPanel = new GameObject("MobileControlsPanel");
                mobileControlsPanel.transform.SetParent(transform, false);
                RectTransform panelRect = mobileControlsPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = Vector2.zero;
                panelRect.anchorMax = Vector2.one;
                panelRect.offsetMin = Vector2.zero;
                panelRect.offsetMax = Vector2.zero;
            }

            if (joystickBackground == null)
            {
                joystickBackground = CreateCircle("JoystickBackground", mobileControlsPanel.transform, new Color(1f, 1f, 1f, 0.2f), joystickSize);
                joystickBackground.anchorMin = new Vector2(0f, 0f);
                joystickBackground.anchorMax = new Vector2(0f, 0f);
                joystickBackground.pivot = new Vector2(0f, 0f);
                joystickBackground.anchoredPosition = new Vector2(sidePadding, bottomPadding);
            }

            if (joystickHandle == null)
            {
                joystickHandle = CreateCircle("JoystickHandle", joystickBackground, new Color(1f, 1f, 1f, 0.45f), joystickHandleSize);
                joystickHandle.anchorMin = new Vector2(0.5f, 0.5f);
                joystickHandle.anchorMax = new Vector2(0.5f, 0.5f);
                joystickHandle.pivot = new Vector2(0.5f, 0.5f);
                joystickHandle.anchoredPosition = Vector2.zero;
            }

            if (shootButton == null)
            {
                shootButton = CreateCircle("ShootButton", mobileControlsPanel.transform, new Color(1f, 0.2f, 0.2f, 0.5f), shootButtonSize);
                shootButton.anchorMin = new Vector2(1f, 0f);
                shootButton.anchorMax = new Vector2(1f, 0f);
                shootButton.pivot = new Vector2(1f, 0f);
                shootButton.anchoredPosition = new Vector2(-sidePadding, bottomPadding);
            }
        }

        private static RectTransform CreateCircle(string name, Transform parent, Color color, float size)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(parent, false);
            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(size, size);
            Image image = obj.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            return rect;
        }

        private static void EnsureEventSystemExists()
        {
            if (EventSystem.current != null)
                return;

            GameObject eventSystemObj = new GameObject("EventSystem");
            eventSystemObj.AddComponent<EventSystem>();
            eventSystemObj.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }
    }
}
