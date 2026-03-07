using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Camera;
using TitanOrbit.Generation;
using TitanOrbit.Networking;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Shows a loading screen with zoomed-out world view and progress bar while the map is being constructed.
    /// After loading completes, hides itself and shows the team menu.
    /// </summary>
    public class LoadingScreenController : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject loadingPanel;
        [SerializeField] private Slider progressBar;
        [SerializeField] private TextMeshProUGUI progressText;
        [SerializeField] private TextMeshProUGUI statusText;

        [Header("Camera")]
        [SerializeField] private CameraController cameraController;
        [Tooltip("Orthographic size for zoomed-out map view during loading (sees whole map).")]
        [SerializeField] private float loadingOrthoSize = 180f;
        [Tooltip("Camera height (Y) during loading.")]
        [SerializeField] private float loadingCameraHeight = 200f;

        [Header("Transitions")]
        [SerializeField] private MainMenu mainMenu;
        [Tooltip("Minimum time to show loading screen (seconds) before team menu appears.")]
        [SerializeField] private float minLoadingDisplayTime = 1f;

        private UnityEngine.Camera cam;
        private bool wasShowing;
        private float loadingStartTime;
        private bool cameraOverridden;

        private void Awake()
        {
            if (loadingPanel != null)
                loadingPanel.SetActive(false);
            else
                CreateLoadingUI();

            if (cameraController == null)
                cameraController = FindFirstObjectByType<CameraController>();

            if (cam == null && cameraController != null)
                cam = cameraController.GetComponent<UnityEngine.Camera>();
            if (cam == null)
                cam = UnityEngine.Camera.main;
        }

        private void OnEnable()
        {
            NetworkGameManager.OnTeamChosen += OnTeamChosen;
        }

        private void OnDisable()
        {
            NetworkGameManager.OnTeamChosen -= OnTeamChosen;
        }

        private void OnTeamChosen(Core.TeamManager.Team team)
        {
            ReleaseCameraToShip();
        }

        private void CreateLoadingUI()
        {
            var canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            loadingPanel = new GameObject("LoadingPanel");
            loadingPanel.transform.SetParent(canvas.transform, false);

            var rect = loadingPanel.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;

            var img = loadingPanel.AddComponent<Image>();
            img.color = new Color(0.02f, 0.03f, 0.08f, 0.5f);

            var titleObj = new GameObject("LoadingTitle");
            titleObj.transform.SetParent(loadingPanel.transform, false);
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 0.7f);
            titleRect.anchorMax = new Vector2(0.5f, 0.7f);
            titleRect.sizeDelta = new Vector2(400, 60);
            var titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "BUILDING WORLD";
            titleText.fontSize = 42;
            titleText.alignment = TMPro.TextAlignmentOptions.Center;

            var statusObj = new GameObject("StatusText");
            statusObj.transform.SetParent(loadingPanel.transform, false);
            var statusRect = statusObj.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0.5f, 0.5f);
            statusRect.anchorMax = new Vector2(0.5f, 0.5f);
            statusRect.sizeDelta = new Vector2(400, 40);
            statusText = statusObj.AddComponent<TextMeshProUGUI>();
            statusText.text = "Initializing...";
            statusText.fontSize = 24;
            statusText.alignment = TMPro.TextAlignmentOptions.Center;

            var sliderObj = new GameObject("ProgressBar");
            sliderObj.transform.SetParent(loadingPanel.transform, false);
            var sliderRect = sliderObj.AddComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0.2f, 0.35f);
            sliderRect.anchorMax = new Vector2(0.8f, 0.4f);
            sliderRect.offsetMin = sliderRect.offsetMax = Vector2.zero;

            var bgObj = new GameObject("Background");
            bgObj.transform.SetParent(sliderObj.transform, false);
            var bgRect = bgObj.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.AddComponent<Image>();
            bgImg.color = new Color(0.1f, 0.12f, 0.2f, 1f);

            var fillArea = new GameObject("Fill Area");
            fillArea.transform.SetParent(sliderObj.transform, false);
            var fillAreaRect = fillArea.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.offsetMin = new Vector2(5, 0);
            fillAreaRect.offsetMax = new Vector2(-5, 0);
            var fillObj = new GameObject("Fill");
            fillObj.transform.SetParent(fillArea.transform, false);
            var fillRect = fillObj.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = fillRect.offsetMax = Vector2.zero;
            var fillImg = fillObj.AddComponent<Image>();
            fillImg.color = new Color(0.2f, 0.5f, 0.9f, 1f);

            progressBar = sliderObj.AddComponent<Slider>();
            progressBar.minValue = 0f;
            progressBar.maxValue = 1f;
            progressBar.value = 0f;
            progressBar.fillRect = fillRect;

            var pctObj = new GameObject("ProgressText");
            pctObj.transform.SetParent(loadingPanel.transform, false);
            var pctRect = pctObj.AddComponent<RectTransform>();
            pctRect.anchorMin = new Vector2(0.5f, 0.28f);
            pctRect.anchorMax = new Vector2(0.5f, 0.28f);
            pctRect.sizeDelta = new Vector2(100, 36);
            progressText = pctObj.AddComponent<TextMeshProUGUI>();
            progressText.text = "0%";
            progressText.fontSize = 28;
            progressText.alignment = TMPro.TextAlignmentOptions.Center;

            loadingPanel.SetActive(false);
        }

        private void Update()
        {
            if (loadingPanel == null || !loadingPanel.activeSelf) return;

            var mapGen = FindFirstObjectByType<MapGenerator>();
            float progress = mapGen != null ? mapGen.LoadingProgress : 0f;
            bool complete = mapGen != null && mapGen.LoadingComplete;

            if (progressBar != null)
                progressBar.value = progress;

            if (progressText != null)
                progressText.text = $"{Mathf.RoundToInt(progress * 100)}%";

            if (statusText != null)
            {
                if (complete)
                    statusText.text = "Ready!";
                else if (progress < 0.1f)
                    statusText.text = "Building home bases...";
                else if (progress < 0.3f)
                    statusText.text = "Placing planets...";
                else
                    statusText.text = "Scattering asteroids...";
            }

            float elapsed = Time.realtimeSinceStartup - loadingStartTime;
            if (complete && elapsed >= minLoadingDisplayTime)
            {
                HideLoadingAndShowTeamMenu();
            }
        }

        /// <summary>Show the loading screen and set up zoomed-out camera view.</summary>
        public void ShowLoading()
        {
            if (loadingPanel != null)
            {
                loadingPanel.SetActive(true);
                wasShowing = true;
                loadingStartTime = Time.realtimeSinceStartup;
            }

            OverrideCameraForLoading(true);
        }

        /// <summary>Hide loading screen and show team menu. Camera stays zoomed out until player picks a team.</summary>
        public void HideLoadingAndShowTeamMenu()
        {
            if (loadingPanel != null)
                loadingPanel.SetActive(false);

            if (mainMenu != null)
            {
                mainMenu.ShowLobbyAndTeamSelection();
            }
        }

        /// <summary>Release camera to follow player ship. Called when player chooses a team.</summary>
        public void ReleaseCameraToShip()
        {
            OverrideCameraForLoading(false);
        }

        private void OverrideCameraForLoading(bool overrideOn)
        {
            if (cameraOverridden == overrideOn) return;

            if (overrideOn)
            {
                cameraOverridden = true;
                if (cameraController != null)
                    cameraController.enabled = false;

                if (cam != null)
                {
                    cam.orthographic = true;
                    cam.orthographicSize = loadingOrthoSize;
                    cam.transform.position = new Vector3(0, loadingCameraHeight, 0);
                    cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                }
            }
            else
            {
                cameraOverridden = false;
                if (cameraController != null)
                    cameraController.enabled = true;
            }
        }

        private void OnDestroy()
        {
            if (cameraOverridden && cameraController != null)
                cameraController.enabled = true;
        }
    }
}
