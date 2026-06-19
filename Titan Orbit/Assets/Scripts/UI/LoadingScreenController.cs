using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Camera;
using TitanOrbit.Generation;
using TitanOrbit.Networking;
using Unity.Netcode;
using System.Collections;
using System.Globalization;

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
        [Tooltip("Minimum camera height (Y) during loading. Actual height is the larger of this and a value computed from ToroidalMap size, field of view, and aspect so the full map fits on screen.")]
        [SerializeField] private float loadingCameraHeight = 320f;
        [Tooltip("Extra margin applied when fitting the toroidal map bounds into the loading camera view (1 = tight fit).")]
        [SerializeField] private float loadingCameraFitMargin = 1.08f;

        [Header("Transitions")]
        [SerializeField] private MainMenu mainMenu;
        [Tooltip("Minimum time to show loading screen (seconds) before team menu appears.")]
        [SerializeField] private float minLoadingDisplayTime = 0.35f;
        [Tooltip("After the world reports ready, wait up to this long for Netcode (listening + client approved when joining) before giving up.")]
        [SerializeField] private float maxWaitNetcodeSeconds = 45f;
        [Tooltip("When joining an existing match, stop waiting for replication after this many seconds (avoids soft-lock if counts mismatch). Only used when RequireReplicationBeforeTeamSelect is enabled.")]
        [SerializeField] private float maxJoinWorldSyncSeconds = 8f;
        [Tooltip("When enabled, joining clients wait for a fraction of live networked map objects before team selection. When disabled, team selection opens as soon as the layout replay finishes (recommended).")]
        [SerializeField] private bool requireReplicationBeforeTeamSelect = false;
        [Tooltip("Joining clients: fraction of planets/asteroids replicated before we show team selection (only if RequireReplicationBeforeTeamSelect).")]
        [SerializeField, Range(0.35f, 0.98f)] private float joinReplicationEnoughFraction = 0.62f;
        [Tooltip("Joining clients: loading bar fraction (0–1) reached when layout replay finishes; replication fills from here to 100% so the bar does not sit at 100% during sync.")]
        [SerializeField, Range(0.55f, 0.95f)] private float joinProgressEndAfterReplay = 0.78f;

        [Header("Loading map build animation")]
        [Tooltip("When the loading panel has a root Image, set its alpha so the 3D world stays visible behind the UI.")]
        [SerializeField, Range(0.05f, 1f)] private float loadingBackdropImageAlpha = 0.28f;

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
        private bool joinWorldInvalid;

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

            var mapGen = MapGenerator.Active != null ? MapGenerator.Active : FindFirstObjectByType<MapGenerator>();
            if (mapGen != null && mapGen.IsClientJoinBuildActive)
                mapGen.SuppressUnrevealedMapRenderers();
            var nm = NetworkGameManager.ResolveNetworkManagerForGameplay();
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
                float replayEnd = Mathf.Clamp01(joinProgressEndAfterReplay);
                if (!mapGen.LoadingComplete)
                {
                    // While the server is still generating, scale progress into the first part of the bar only
                    // so we can continue smoothly into replay (avoids hitting 100% before local preview).
                    progress = Mathf.Clamp01(mapGen.LoadingProgress) * replayEnd;
                }
                else if (!joinPlaybackComplete)
                {
                    // Blueprint replay: animate from ~0 through replayEnd as preview prefabs spawn.
                    progress = Mathf.Lerp(0.02f, replayEnd, Mathf.Clamp01(joinPlaybackProgress));
                }
                else if (requireReplicationBeforeTeamSelect)
                {
                    // Optional: fill replayEnd → 1.0 from replication progress while waiting for live objects.
                    float rep = mapGen.GetClientWorldReplicationProgress();
                    float thresh = Mathf.Max(0.05f, joinReplicationEnoughFraction);
                    float repT = Mathf.Clamp01(rep / thresh);
                    progress = Mathf.Lerp(replayEnd, 1f, repT);
                }
                else
                    progress = 1f;

                float elapsedSync = Time.realtimeSinceStartup - loadingStartTime;
                bool replicatedEnough = mapGen.GetClientWorldReplicationProgress() >= joinReplicationEnoughFraction;
                bool syncTimedOut = mapGen.LoadingComplete && elapsedSync >= maxJoinWorldSyncSeconds;
                bool replicationGatePassed = !requireReplicationBeforeTeamSelect || replicatedEnough || syncTimedOut;
                complete = mapGen.LoadingComplete && joinPlaybackComplete && replicationGatePassed;
            }
            else
            {
                if (mapGen.LoadingComplete && !joinPlaybackComplete)
                    progress = Mathf.Lerp(0.02f, 1f, Mathf.Clamp01(joinPlaybackProgress));
                else
                    progress = mapGen.LoadingProgress;
                complete = mapGen.LoadingComplete && joinPlaybackComplete;
            }

            if (progressBar != null)
                progressBar.value = progress;

            if (progressText != null)
                progressText.text = $"{Mathf.RoundToInt(progress * 100)}%";

            if (statusText != null)
            {
                if (complete)
                    statusText.text = netcodeListening ? "Ready!" : "Connecting multiplayer session...";
                else if (mapGen != null && mapGen.LoadingComplete && !joinPlaybackComplete)
                {
                    if (mapGen.BlueprintEntryCount > 0)
                    {
                        mapGen.GetJoinReplayPhaseEndProgress(out float endHomes, out float endNeutrals);
                        float jp = joinPlaybackProgress;
                        if (jp >= 1f - 1e-4f)
                            statusText.text = pureClient ? "Waiting for live objects..." : "Materializing world...";
                        else if (jp < endHomes)
                            statusText.text = "Building home bases...";
                        else if (jp < endNeutrals)
                            statusText.text = "Placing planets...";
                        else
                            statusText.text = "Scattering asteroids...";
                    }
                    else
                        statusText.text = "Receiving map layout...";
                }
                else if (pureClient && mapGen != null && mapGen.LoadingComplete && joinPlaybackComplete && requireReplicationBeforeTeamSelect)
                    statusText.text = "Syncing world...";
                else if (pureClient && mapGen != null && mapGen.LoadingComplete && joinPlaybackComplete)
                    statusText.text = "Ready!";
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

            if (joinWorldInvalid ||
                (pureClient && mapGen != null && mapGen.LoadingComplete && joinPlaybackComplete &&
                 mapGen.BlueprintEntryCount <= 0 && elapsed >= minLoadingDisplayTime + 5f))
            {
                if (!teamMenuShownAfterLoad)
                    FailStaleJoinAndReturnToBrowser();
                return;
            }

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

            cam.orthographic = false;
            cam.fieldOfView = 45f;
            float y = ComputeLoadingCameraYForToroidalMap();
            cam.transform.position = new Vector3(0f, y, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>
        /// Perspective top-down camera at (0,Y,0) with euler (90,0,0): vertical FOV covers world Z, horizontal covers world X.
        /// </summary>
        private float ComputeLoadingCameraYForToroidalMap()
        {
            float w = Mathf.Max(1f, ToroidalMap.GetMapWidth());
            float h = Mathf.Max(1f, ToroidalMap.GetMapHeight());
            float margin = Mathf.Max(1f, loadingCameraFitMargin);
            float halfW = w * 0.5f * margin;
            float halfH = h * 0.5f * margin;
            if (cam == null)
                return loadingCameraHeight;
            float vRad = Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad);
            if (vRad < 1e-5f)
                return loadingCameraHeight;
            float hRad = vRad * Mathf.Max(0.01f, cam.aspect);
            float distForZ = halfH / vRad;
            float distForX = halfW / hRad;
            float fit = Mathf.Max(distForX, distForZ);
            return Mathf.Max(loadingCameraHeight, fit);
        }

        /// <summary>Show the loading screen and set up zoomed-out camera view.</summary>
        public void ShowLoading()
        {
            teamMenuShownAfterLoad = false;
            joinWorldInvalid = false;
            var nm = NetworkGameManager.ResolveNetworkManagerForGameplay();
            bool wantsLayoutPlayback = nm != null && nm.IsClient;
            joinPlaybackProgress = 0f;
            joinPlaybackComplete = !wantsLayoutPlayback;
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
                ApplyLoadingScreenBackdropAlpha();
            }

            OverrideCameraForLoading(true);

            if (wantsLayoutPlayback)
                joinLayoutPlaybackRoutine = StartCoroutine(CoJoinClientLayoutPlayback(nm));
        }

        private void ApplyLoadingScreenBackdropAlpha()
        {
            if (loadingPanel == null) return;
            var img = loadingPanel.GetComponent<Image>();
            if (img == null) return;
            Color c = img.color;
            c.a = Mathf.Clamp01(loadingBackdropImageAlpha);
            img.color = c;
        }

        private IEnumerator CoJoinClientLayoutPlayback(NetworkManager nm)
        {
            if (nm == null || !nm.IsClient)
            {
                joinPlaybackComplete = true;
                joinLayoutPlaybackRoutine = null;
                yield break;
            }

            joinPreviewRoot = new GameObject("JoinLoadingPreview");
            joinPreviewRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            joinPlaybackProgress = 0.02f;

            const float metaTimeout = 90f;
            float waitStart = Time.realtimeSinceStartup;

            MapGenerator mapGen = MapGenerator.Active;
            while (mapGen == null && Time.realtimeSinceStartup - waitStart < metaTimeout)
            {
                mapGen = MapGenerator.Active ?? FindFirstObjectByType<MapGenerator>();
                yield return null;
            }

            if (mapGen == null)
            {
                Debug.LogWarning("[LoadingScreenController] MapGenerator not found; skipping join build animation.");
                joinPlaybackProgress = 1f;
                joinPlaybackComplete = true;
                if (joinPreviewRoot != null)
                {
                    Destroy(joinPreviewRoot);
                    joinPreviewRoot = null;
                }
                joinLayoutPlaybackRoutine = null;
                yield break;
            }

            while (!mapGen.IsSpawned && Time.realtimeSinceStartup - waitStart < metaTimeout)
                yield return null;

            mapGen.BeginClientJoinBuild();

            while (mapGen != null && (!mapGen.LoadingComplete || mapGen.BlueprintEntryCount == 0))
            {
                if (joinPreviewRoot == null)
                {
                    joinLayoutPlaybackRoutine = null;
                    yield break;
                }

                if (Time.realtimeSinceStartup - waitStart > metaTimeout)
                {
                    Debug.LogWarning("[LoadingScreenController] Timed out waiting for map blueprint before build animation.");
                    break;
                }

                joinPlaybackProgress = Mathf.Clamp01(mapGen.LoadingProgress) * 0.92f;
                yield return null;
            }

            int n = mapGen != null ? mapGen.BlueprintEntryCount : 0;
            if (mapGen == null || joinPreviewRoot == null || n <= 0)
            {
                mapGen?.EndClientJoinBuild();

                joinPlaybackProgress = 1f;
                joinPlaybackComplete = true;
                joinWorldInvalid = n <= 0;
                if (joinPreviewRoot != null)
                {
                    Destroy(joinPreviewRoot);
                    joinPreviewRoot = null;
                }
                joinLayoutPlaybackRoutine = null;
                yield break;
            }

            yield return mapGen.CoPlayJoinLayout(
                joinPreviewRoot.transform,
                p => joinPlaybackProgress = Mathf.Clamp01(p));

            joinPlaybackProgress = 1f;

            mapGen?.EndClientJoinBuild();
            joinPlaybackComplete = true;
            if (joinPreviewRoot != null)
            {
                Destroy(joinPreviewRoot);
                joinPreviewRoot = null;
            }
            joinLayoutPlaybackRoutine = null;
        }

        private void FailStaleJoinAndReturnToBrowser()
        {
            teamMenuShownAfterLoad = true;
            joinWorldInvalid = true;
            Debug.LogError(
                "[LoadingScreenController] Match has no map data (stale or dead server). Returning to lobby browser.");

            var mapGenRestore = MapGenerator.Active != null ? MapGenerator.Active : FindFirstObjectByType<MapGenerator>();
            mapGenRestore?.EndClientJoinBuild();

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
                loadingPanel.SetActive(false);

            OverrideCameraForLoading(false);
            NetworkGameManager.Instance?.AbortStaleClientSession("stale_empty_world");
            NetworkGameManager.OnTeamChoiceFailed?.Invoke(
                "This match is no longer active (empty world). Refresh the game list and join again, or use Quick Join.");

            if (mainMenu != null)
                mainMenu.ShowLobbyScreen();
        }

        /// <summary>Hide loading screen and show team menu. Camera stays zoomed out until player picks a team.</summary>
        public void HideLoadingAndShowTeamMenu()
        {
            var mapGenRestore = MapGenerator.Active != null ? MapGenerator.Active : FindFirstObjectByType<MapGenerator>();
            mapGenRestore?.EndClientJoinBuild();

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
                var nm = NetworkGameManager.ResolveNetworkManagerForGameplay();
                if (nm != null && nm.SpawnManager != null)
                {
                    var localPlayer = nm.SpawnManager.GetLocalPlayerObject();
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
                {
                    cameraController.enabled = false;
                    cameraController.SetSpaceBackgroundHiddenForLoadingState(true);
                }

                if (cam != null)
                {
                    cam.orthographic = false;
                    cam.fieldOfView = 45f;
                    float y = ComputeLoadingCameraYForToroidalMap();
                    cam.transform.position = new Vector3(0, y, 0);
                    cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                }
            }
            else
            {
                cameraOverridden = false;
                if (cameraController != null)
                {
                    cameraController.enabled = true;
                    cameraController.SetSpaceBackgroundHiddenForLoadingState(false);
                }
            }
        }

        private void OnDestroy()
        {
            var mapGenRestore = MapGenerator.Active != null ? MapGenerator.Active : FindFirstObjectByType<MapGenerator>();
            mapGenRestore?.EndClientJoinBuild();

            if (teamSelectTransitionRoutine != null)
                StopCoroutine(teamSelectTransitionRoutine);
            if (cameraOverridden && cameraController != null)
            {
                cameraController.enabled = true;
                cameraController.SetSpaceBackgroundHiddenForLoadingState(false);
            }
        }
    }
}
