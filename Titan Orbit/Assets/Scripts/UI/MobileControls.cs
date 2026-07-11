using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using TitanOrbit.Input;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Mobile: wires <see cref="MobileInputHandler"/> (left-half steer, right-half fire) and optional canvas visibility.
    /// Touchable controls (e.g. air brakes) live on <see cref="mobileTouchCanvas"/>; assign <see cref="classicHudCanvas"/> to hide that HUD on phones if you use a duplicate desktop layout.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class MobileControls : MonoBehaviour
    {
        [Header("Mobile Controls")]
        [Tooltip("Full-stretch panel under this component (used as input + canvas root).")]
        [SerializeField] private GameObject mobileControlsPanel;
        [Tooltip("Canvas that holds mobile-only touch buttons (brakes). Hidden on desktop unless Force Mobile.")]
        [SerializeField] private Canvas mobileTouchCanvas;
        [Tooltip("Optional: gameplay HUD that should only appear on desktop (disabled when mobile HUD is shown).")]
        [SerializeField] private Canvas classicHudCanvas;
        [Tooltip("When classic HUD is assigned, disable it while the mobile touch canvas is active.")]
        [SerializeField] private bool disableClassicHudWhileMobileActive = true;

        [Header("Shoot exclusions")]
        [Tooltip("UI rects on the right that must not count as firing (e.g. brake buttons).")]
        [SerializeField] private RectTransform[] shootZoneExclusions;

        [Header("Settings")]
        [SerializeField] private bool forceMobileControls;
        [SerializeField] private float joystickRadius = 72f;

        private MobileInputHandler mobileInputHandler;
        private bool wired;

        private void Awake()
        {
            TryWireMobileControls();
        }

        private void OnEnable()
        {
            TryWireMobileControls();
        }

        private void OnDisable()
        {
            MobileInputHandler.SetForceTouchSteer(false);
        }

        private void Start()
        {
            // --- Unity lifecycle ---
            bool showMobileHud = Application.isMobilePlatform || forceMobileControls;

            GameObject mobileRoot = mobileTouchCanvas != null ? mobileTouchCanvas.gameObject : mobileControlsPanel;
            if (mobileRoot != null)
                mobileRoot.SetActive(showMobileHud);

            if (classicHudCanvas != null && disableClassicHudWhileMobileActive)
                classicHudCanvas.gameObject.SetActive(!showMobileHud);

            MobileInputHandler.SetForceTouchSteer(forceMobileControls && !Application.isMobilePlatform);

            TryWireMobileControls();
        }

        private void TryWireMobileControls()
        {
            // --- Attempt resolution ---
            if (wired) return;

            RectTransform panelRect = mobileControlsPanel != null
                ? mobileControlsPanel.GetComponent<RectTransform>()
                : GetComponent<RectTransform>();

            if (panelRect == null)
            {
                Debug.LogWarning("MobileControls: assign Mobile Controls Panel (full-stretch RectTransform).");
                return;
            }

            Canvas canvas = mobileTouchCanvas != null ? mobileTouchCanvas : GetComponentInParent<Canvas>();
            if (canvas != null && canvas.GetComponent<GraphicRaycaster>() == null)
                canvas.gameObject.AddComponent<GraphicRaycaster>();

            EnsureSteerVisualLayer(canvas.transform);

            EnsureEventSystemExists();

            mobileInputHandler = gameObject.GetComponent<MobileInputHandler>();
            if (mobileInputHandler == null)
                mobileInputHandler = gameObject.AddComponent<MobileInputHandler>();

            mobileInputHandler.Initialize(null, null, null, joystickRadius, shootZoneExclusions);
            mobileInputHandler.SetRootCanvasForUiTests(canvas);

            if (canvas != null && canvas.GetComponent<CanvasScaler>() == null)
            {
                var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
            }

            wired = true;
        }

        private static void EnsureSteerVisualLayer(Transform canvasTransform)
        {
            // --- Ensure setup ---
            if (canvasTransform == null)
                return;

            Transform existing = canvasTransform.Find("SteerVisual");
            if (existing != null)
            {
                if (existing.GetComponent<MobileSteerVisualUI>() == null)
                    existing.gameObject.AddComponent<MobileSteerVisualUI>();
                return;
            }

            GameObject go = new GameObject("SteerVisual");
            go.transform.SetParent(canvasTransform, false);
            go.transform.SetAsLastSibling();
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            go.AddComponent<MobileSteerVisualUI>();
        }

        private static void EnsureEventSystemExists()
        {
            // --- Ensure setup ---
            if (EventSystem.current != null)
                return;
            if (Object.FindFirstObjectByType<EventSystem>(FindObjectsInactive.Include) != null)
                return;

            GameObject es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }
    }
}
