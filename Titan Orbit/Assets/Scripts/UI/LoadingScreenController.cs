using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Camera;
using TitanOrbit.Generation;
using TitanOrbit.Networking;
using Unity.Netcode;
using System.Collections;

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
        [Tooltip("Fallback orthographic size when map size is not yet known (should match synced ToroidalMap after join).")]
        [SerializeField] private float loadingOrthoSize = 180f;
        [Tooltip("Camera height (Y) during loading.")]
        [SerializeField] private float loadingCameraHeight = 200f;

        [Header("Transitions")]
        [SerializeField] private MainMenu mainMenu;
        [Tooltip("Minimum time to show loading screen (seconds) before team menu appears.")]
        [SerializeField] private float minLoadingDisplayTime = 1f;
        [Tooltip("After the world reports ready, wait up to this long for Netcode (listening + client approved when joining) before giving up.")]
        [SerializeField] private float maxWaitNetcodeSeconds = 45f;
        [Tooltip("When joining an existing match, stop waiting for replication after this many seconds (avoids soft-lock if counts mismatch).")]
        [SerializeField] private float maxJoinWorldSyncSeconds = 18f;
        [Tooltip("Joining clients: fraction of planets/asteroids replicated before we show team selection. Lower = faster but you may see a brief pop-in.")]
        [SerializeField, Range(0.35f, 0.98f)] private float joinReplicationEnoughFraction = 0.62f;

        private UnityEngine.Camera cam;
        private bool wasShowing;
        private float loadingStartTime;
        private bool cameraOverridden;
        private Coroutine teamSelectTransitionRoutine;
        private bool teamMenuShownAfterLoad;

        private Coroutine joinLayoutPlaybackRoutine;
        private GameObject joinPreviewRoot;
        private float joinPlaybackProgress;
        private bool joinPlaybackComplete = true;

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
            StartTeamSelectCameraTransition();
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

            RefreshLoadingCameraFromToroidalMap();

            var mapGen = FindFirstObjectByType<MapGenerator>();
            var nm = NetworkManager.Singleton;
            bool pureClient = nm != null && nm.IsClient && !nm.IsServer;
            bool netcodeListening = NetworkGameManager.IsNetcodeTransportReadyForGameplay(nm);

            float progress;
            bool complete;

            if (mapGen == null)
            {
                progress = 0f;
                complete = false;
            }
            else if (pureClient)
            {
                if (!mapGen.LoadingComplete)
                {
                    progress = mapGen.LoadingProgress;
                }
                else if (!joinPlaybackComplete)
                {
                    progress = Mathf.Clamp01(Mathf.Max(0.06f, joinPlaybackProgress));
                }
                else
                {
                    float rep = mapGen.GetClientWorldReplicationProgress();
                    progress = Mathf.Clamp01(Mathf.Max(1f, rep));
                }

                float elapsedSync = Time.realtimeSinceStartup - loadingStartTime;
                bool replicatedEnough = mapGen.GetClientWorldReplicationProgress() >= joinReplicationEnoughFraction;
                bool syncTimedOut = mapGen.LoadingComplete && elapsedSync >= maxJoinWorldSyncSeconds;
                complete = mapGen.LoadingComplete && joinPlaybackComplete && (replicatedEnough || syncTimedOut);
            }
            else
            {
                progress = mapGen.LoadingProgress;
                complete = mapGen.LoadingComplete;
            }

            if (progressBar != null)
                progressBar.value = progress;

            if (progressText != null)
                progressText.text = $"{Mathf.RoundToInt(progress * 100)}%";

            if (statusText != null)
            {
                if (complete)
                    statusText.text = netcodeListening ? "Ready!" : "Connecting multiplayer session...";
                else if (pureClient && mapGen != null && mapGen.LoadingComplete && !joinPlaybackComplete)
                {
                    if (mapGen.HasClientJoinLayoutReady())
                    {
                        mapGen.GetJoinReplayPhaseEndProgress(out float endHomes, out float endNeutrals);
                        float jp = joinPlaybackProgress;
                        if (jp < endHomes)
                            statusText.text = "Building home bases...";
                        else if (jp < endNeutrals)
                            statusText.text = "Placing planets...";
                        else
                            statusText.text = "Scattering asteroids...";
                    }
                    else
                        statusText.text = "Receiving map layout...";
                }
                else if (pureClient && mapGen != null && mapGen.LoadingComplete && joinPlaybackComplete)
                    statusText.text = "Syncing world...";
                else if (progress < 0.1f)
                    statusText.text = "Building home bases...";
                else if (progress < 0.3f)
                    statusText.text = "Placing planets...";
                else
                    statusText.text = "Scattering asteroids...";
            }

            float elapsed = Time.realtimeSinceStartup - loadingStartTime;
            if (!complete || teamMenuShownAfterLoad || elapsed < minLoadingDisplayTime)
                return;

            if (!netcodeListening)
            {
                if (elapsed < minLoadingDisplayTime + maxWaitNetcodeSeconds)
                    return;
                Debug.LogError("[LoadingScreenController] Timed out waiting for Netcode (transport listening and client approved). The world reported ready but multiplayer session was not ready.");
                NetworkGameManager.OnTeamChoiceFailed?.Invoke("Multiplayer session did not start in time. Return to the main menu, create or join a match from the list, or join with a relay code.");
                teamMenuShownAfterLoad = true;
                HideLoadingAndShowTeamMenu();
                return;
            }

            teamMenuShownAfterLoad = true;
            HideLoadingAndShowTeamMenu();
        }

        /// <summary>Frames the whole toroidal map so joining players see the match layout as objects replicate.</summary>
        private void RefreshLoadingCameraFromToroidalMap()
        {
            if (cam == null || !cameraOverridden) return;

            float w = ToroidalMap.GetMapWidth();
            float h = ToroidalMap.GetMapHeight();
            float halfExtent = Mathf.Max(w, h) * 0.5f;
            float targetOrtho = Mathf.Clamp(halfExtent * 1.08f, 40f, 2500f);
            cam.orthographic = true;
            cam.orthographicSize = targetOrtho;
            cam.transform.position = new Vector3(0f, loadingCameraHeight, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>Show the loading screen and set up zoomed-out camera view.</summary>
        public void ShowLoading()
        {
            teamMenuShownAfterLoad = false;
            var nm = NetworkManager.Singleton;
            bool pureClient = nm != null && nm.IsClient && !nm.IsServer;
            joinPlaybackProgress = 0f;
            joinPlaybackComplete = !pureClient;
            if (joinLayoutPlaybackRoutine != null)
            {
                StopCoroutine(joinLayoutPlaybackRoutine);
                joinLayoutPlaybackRoutine = null;
            }
            if (joinPreviewRoot != null)
            {
                Destroy(joinPreviewRoot);
                joinPreviewRoot = null;
            }

            if (loadingPanel != null)
            {
                loadingPanel.SetActive(true);
                wasShowing = true;
                loadingStartTime = Time.realtimeSinceStartup;
            }

            OverrideCameraForLoading(true);

            if (pureClient)
            {
                var mapGen = FindFirstObjectByType<MapGenerator>();
                if (mapGen != null)
                    joinLayoutPlaybackRoutine = StartCoroutine(CoJoinClientLayoutPlayback(mapGen));
            }
        }

        private IEnumerator CoJoinClientLayoutPlayback(MapGenerator mapGen)
        {
            float waitMeta = 0f;
            const float metaTimeout = 90f;
            while (mapGen != null && !mapGen.HasClientJoinLayoutReady() && waitMeta < metaTimeout)
            {
                waitMeta += Time.unscaledDeltaTime;
                yield return null;
            }
            if (mapGen == null || !mapGen.HasClientJoinLayoutReady())
            {
                joinPlaybackComplete = true;
                joinLayoutPlaybackRoutine = null;
                yield break;
            }

            joinPreviewRoot = new GameObject("JoinLoadingPreview");
            joinPreviewRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            joinPlaybackProgress = 0.02f;
            yield return mapGen.StartCoroutine(mapGen.CoPlayJoinLayout(joinPreviewRoot.transform, p => joinPlaybackProgress = p));
            joinPlaybackComplete = true;
            joinPlaybackProgress = 1f;
            if (joinPreviewRoot != null)
            {
                Destroy(joinPreviewRoot);
                joinPreviewRoot = null;
            }
            joinLayoutPlaybackRoutine = null;
        }

        /// <summary>Hide loading screen and show team menu. Camera stays zoomed out until player picks a team.</summary>
        public void HideLoadingAndShowTeamMenu()
        {
            if (joinLayoutPlaybackRoutine != null)
            {
                StopCoroutine(joinLayoutPlaybackRoutine);
                joinLayoutPlaybackRoutine = null;
            }
            if (joinPreviewRoot != null)
            {
                Destroy(joinPreviewRoot);
                joinPreviewRoot = null;
            }
            joinPlaybackComplete = true;

            if (loadingPanel != null)
                loadingPanel.SetActive(false);

            if (mainMenu != null)
                mainMenu.ShowLobbyAndTeamSelection();
        }

        /// <summary>Release camera to follow player ship. Called when player chooses a team.</summary>
        public void ReleaseCameraToShip()
        {
            if (teamSelectTransitionRoutine != null)
            {
                StopCoroutine(teamSelectTransitionRoutine);
                teamSelectTransitionRoutine = null;
            }
            OverrideCameraForLoading(false);
        }

        private void StartTeamSelectCameraTransition()
        {
            if (teamSelectTransitionRoutine != null)
                StopCoroutine(teamSelectTransitionRoutine);
            teamSelectTransitionRoutine = StartCoroutine(CoSnapToLocalPlayerAndRelease());
        }

        private IEnumerator CoSnapToLocalPlayerAndRelease()
        {
            if (cam == null)
            {
                ReleaseCameraToShip();
                yield break;
            }

            Transform playerTransform = null;
            const float maxWaitSeconds = 2.5f;
            float waitStart = Time.realtimeSinceStartup;
            while (playerTransform == null && Time.realtimeSinceStartup - waitStart < maxWaitSeconds)
            {
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager != null)
                {
                    var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
                    if (localPlayer != null)
                        playerTransform = localPlayer.transform;
                }
                if (playerTransform == null)
                    yield return null;
            }

            if (playerTransform == null)
            {
                ReleaseCameraToShip();
                yield break;
            }

            if (cameraController != null)
                cameraController.SetTarget(playerTransform);

            teamSelectTransitionRoutine = null;
            ReleaseCameraToShip();
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
                    float w = ToroidalMap.GetMapWidth();
                    float h = ToroidalMap.GetMapHeight();
                    bool haveBounds = w > 1f && h > 1f;
                    float halfExtent = haveBounds ? Mathf.Max(w, h) * 0.5f : loadingOrthoSize;
                    float targetOrtho = haveBounds ? Mathf.Clamp(halfExtent * 1.08f, 40f, 2500f) : loadingOrthoSize;
                    cam.orthographic = true;
                    cam.orthographicSize = targetOrtho;
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
            if (teamSelectTransitionRoutine != null)
                StopCoroutine(teamSelectTransitionRoutine);
            if (cameraOverridden && cameraController != null)
                cameraController.enabled = true;
        }
    }
}
