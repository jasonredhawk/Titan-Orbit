using System;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Client-only rewarded-ad facade. Gameplay UI calls <see cref="Show"/>; this type
    /// picks AppLixir (WebGL), LevelPlay (Android/iOS), an Editor simulate, or "unavailable".
    /// Dedicated server never creates this component.
    /// <para>
    /// [TITAN-ORBIT] Rewards that change sim state (keep loadout, extra slot) still go
    /// through a NetCode RPC after this callback. We do not write ship ghosts here.
    /// Remove-ads IAP owners skip the video and receive <see cref="TitanOrbitRewardedAdResult.Completed"/>.
    /// </para>
    /// Placements: <see cref="PlacementKeepLoadout"/> and <see cref="PlacementBonusSlot"/>.
    /// </summary>
    public class TitanOrbitRewardedAds : MonoBehaviour
    {
        /// <summary>Death-screen "watch to keep cards + equipment" placement id.</summary>
        public const string PlacementKeepLoadout = "keep_loadout";

        /// <summary>Orbit Menu locked +1 loadout slot placement id.</summary>
        public const string PlacementBonusSlot = "bonus_slot";

        /// <summary>Singleton set in Awake so UI can call static <see cref="Show"/>.</summary>
        public static TitanOrbitRewardedAds Instance { get; private set; }

        /// <summary>True while a video (or Editor simulate) is in flight. Buttons should disable.</summary>
        public static bool IsShowing { get; private set; }

        [Header("AppLixir (WebGL)")]
        [Tooltip("Publisher API key from client.applixir.com. Empty = WebGL ads report Unavailable.")]
        [SerializeField] string applixirApiKey;

        [Header("LevelPlay (Android / iOS)")]
        [Tooltip("LevelPlay Android app key. Used only in Android player builds.")]
        [SerializeField] string levelPlayAndroidAppKey;

        [Tooltip("LevelPlay iOS app key. Used only in iOS player builds.")]
        [SerializeField] string levelPlayIosAppKey;

        [Tooltip("Rewarded ad unit id for Android (LevelPlay dashboard).")]
        [SerializeField] string levelPlayRewardedAdUnitIdAndroid;

        [Tooltip("Rewarded ad unit id for iOS (LevelPlay dashboard).")]
        [SerializeField] string levelPlayIosRewardedAdUnitId;

        [Header("Editor")]
        [Tooltip("Play Mode cannot show a real AppLixir/LevelPlay video. When true, Show() completes so death/slot flows can be tested.")]
        [SerializeField] bool editorSimulateCompleted = true;

        /// <summary>Pending UI callback. Invoked once on the main thread after the SDK (or simulate) finishes.</summary>
        Action<TitanOrbitRewardedAdResult> _pendingCallback;

        /// <summary>Placement string of the in-flight show (for logs only).</summary>
        string _pendingPlacement;

        /// <summary>
        /// [UNITY] Awake runs when the services host is created.
        /// We keep one instance and kick platform SDK init (no-op on Windows player / server).
        /// </summary>
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            ApplyKeyFallbacks();
            TryInitializeBackends();
        }

        /// <summary>
        /// Fills empty Inspector fields from <see cref="TitanOrbitRewardedAdsKeys"/> so a
        /// bootstrap-created host still has dashboard values.
        /// </summary>
        void ApplyKeyFallbacks()
        {
            if (string.IsNullOrWhiteSpace(applixirApiKey))
                applixirApiKey = TitanOrbitRewardedAdsKeys.AppLixirApiKey;
            if (string.IsNullOrWhiteSpace(levelPlayAndroidAppKey))
                levelPlayAndroidAppKey = TitanOrbitRewardedAdsKeys.LevelPlayAndroidAppKey;
            if (string.IsNullOrWhiteSpace(levelPlayIosAppKey))
                levelPlayIosAppKey = TitanOrbitRewardedAdsKeys.LevelPlayIosAppKey;
            if (string.IsNullOrWhiteSpace(levelPlayRewardedAdUnitIdAndroid))
                levelPlayRewardedAdUnitIdAndroid = TitanOrbitRewardedAdsKeys.LevelPlayRewardedAdUnitIdAndroid;
            if (string.IsNullOrWhiteSpace(levelPlayIosRewardedAdUnitId))
                levelPlayIosRewardedAdUnitId = TitanOrbitRewardedAdsKeys.LevelPlayRewardedAdUnitIdIos;
        }

        /// <summary>[UNITY] Domain-reload / scene teardown must not leave a stale static host.</summary>
        void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                IsShowing = false;
            }
        }

        /// <summary>
        /// Starts a rewarded video for <paramref name="placementId"/>.
        /// Callback always runs (success, fail, or unavailable) so UI can re-enable buttons.
        /// </summary>
        /// <param name="placementId">One of the placement constants on this type.</param>
        /// <param name="onComplete">Main-thread result. Grant gameplay rewards only on Completed.</param>
        public static void Show(string placementId, Action<TitanOrbitRewardedAdResult> onComplete)
        {
            if (Instance == null)
            {
                onComplete?.Invoke(TitanOrbitRewardedAdResult.Unavailable);
                return;
            }

            Instance.ShowInternal(placementId, onComplete);
        }

        /// <summary>
        /// True when this client can offer an ad button (or instant remove-ads grant).
        /// Windows player without remove-ads returns false so the extra slot stays hidden
        /// rather than showing a dead "WATCH AD" that can never complete.
        /// Editor always returns true so Play Mode can exercise the flow.
        /// </summary>
        public static bool CanOfferRewarded
        {
            get
            {
#if UNITY_EDITOR
                return true;
#elif UNITY_SERVER
                return false;
#elif UNITY_WEBGL
                return TitanOrbitEntitlements.IsRemoveAdsOwned
                    || (Instance != null && !string.IsNullOrWhiteSpace(Instance.applixirApiKey));
#elif UNITY_ANDROID || UNITY_IOS
                return TitanOrbitEntitlements.IsRemoveAdsOwned
                    || (Instance != null && Instance.HasLevelPlayKeys);
#else
                return TitanOrbitEntitlements.IsRemoveAdsOwned;
#endif
            }
        }

        /// <summary>Instance helper: LevelPlay keys exist for the current mobile platform.</summary>
        bool HasLevelPlayKeys
        {
            get
            {
#if UNITY_IOS
                return !string.IsNullOrWhiteSpace(levelPlayIosAppKey)
                    && !string.IsNullOrWhiteSpace(levelPlayIosRewardedAdUnitId);
#else
                return !string.IsNullOrWhiteSpace(levelPlayAndroidAppKey)
                    && !string.IsNullOrWhiteSpace(levelPlayRewardedAdUnitIdAndroid);
#endif
            }
        }

        /// <summary>
        /// Wires platform SDKs. Safe to call more than once. Skips when remove-ads is owned
        /// (no point downloading ad creatives).
        /// </summary>
        public void TryInitializeBackends()
        {
            // --- Guard: no ads on dedicated server ---
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#endif
            if (!TitanOrbitAdsGate.ShouldShowAds)
            {
                Debug.Log("[TitanOrbitRewardedAds] Skipping SDK init (remove-ads entitlement active).");
                return;
            }

#if UNITY_ANDROID || UNITY_IOS
            TitanOrbitLevelPlayBackend.TryInitialize(
                levelPlayAndroidAppKey,
                levelPlayIosAppKey,
                levelPlayRewardedAdUnitIdAndroid,
                levelPlayIosRewardedAdUnitId);
#endif
        }

        /// <summary>
        /// Resolves remove-ads, Editor simulate, then the platform backend.
        /// Only one show at a time — a second click while IsShowing fails immediately.
        /// </summary>
        void ShowInternal(string placementId, Action<TitanOrbitRewardedAdResult> onComplete)
        {
            // --- Validate ---
            if (onComplete == null)
                return;

            if (string.IsNullOrWhiteSpace(placementId))
                placementId = PlacementKeepLoadout;

            if (IsShowing)
            {
                Debug.LogWarning("[TitanOrbitRewardedAds] Show ignored — an ad is already in flight.");
                onComplete(TitanOrbitRewardedAdResult.Unavailable);
                return;
            }

            // --- Remove-ads IAP: instant grant, no video ---
            // [TITAN-ORBIT] Same buttons as paying players; the video is skipped.
            if (!TitanOrbitAdsGate.ShouldShowAds)
            {
                onComplete(TitanOrbitRewardedAdResult.Completed);
                return;
            }

#if UNITY_EDITOR
            // AppLixir and LevelPlay cannot play inside the Editor. Simulate so death/slot
            // UI can be click-tested in Play Mode / MPPM.
            if (editorSimulateCompleted)
            {
                Debug.Log("[TitanOrbitRewardedAds] Editor simulate Completed for " + placementId);
                onComplete(TitanOrbitRewardedAdResult.Completed);
                return;
            }

            onComplete(TitanOrbitRewardedAdResult.Failed);
            return;
#endif

#if UNITY_SERVER && !UNITY_EDITOR
            onComplete(TitanOrbitRewardedAdResult.Unavailable);
            return;
#endif

            IsShowing = true;
            _pendingCallback = onComplete;
            _pendingPlacement = placementId;

#if UNITY_WEBGL && !UNITY_EDITOR
            if (string.IsNullOrWhiteSpace(applixirApiKey))
            {
                Finish(TitanOrbitRewardedAdResult.Unavailable);
                return;
            }

            TitanOrbitAppLixirBackend.Play(gameObject.name, nameof(OnAppLixirStatus), applixirApiKey);
            return;
#elif UNITY_ANDROID || UNITY_IOS
            if (!TitanOrbitLevelPlayBackend.TryShow(OnLevelPlayFinished))
                Finish(TitanOrbitRewardedAdResult.Unavailable);
            return;
#else
            Finish(TitanOrbitRewardedAdResult.Unavailable);
#endif
        }

        /// <summary>
        /// [HYBRID] AppLixir jslib calls this via SendMessage with status.type
        /// ("complete", "skipped", "error", …). Grant only on complete.
        /// </summary>
        public void OnAppLixirStatus(string status)
        {
            // --- Map AppLixir status.type to our enum ---
            // Official bridge: https://github.com/applixirinc/applixir-integration
            TitanOrbitRewardedAdResult result = TitanOrbitRewardedAdResult.Failed;
            if (string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ad-watched", StringComparison.OrdinalIgnoreCase))
            {
                result = TitanOrbitRewardedAdResult.Completed;
            }
            else if (string.Equals(status, "sdk-not-loaded", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(status, "no-ad", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(status, "allAdsCompleted", StringComparison.OrdinalIgnoreCase))
            {
                // allAdsCompleted also fires when no ad was available — not a reward.
                result = string.Equals(status, "allAdsCompleted", StringComparison.OrdinalIgnoreCase)
                    ? TitanOrbitRewardedAdResult.Failed
                    : TitanOrbitRewardedAdResult.Unavailable;
            }

            Debug.Log("[TitanOrbitRewardedAds] AppLixir status=" + status + " placement=" + _pendingPlacement);
            Finish(result);
        }

        /// <summary>LevelPlay rewarded callback — already on the Unity main thread.</summary>
        void OnLevelPlayFinished(TitanOrbitRewardedAdResult result)
        {
            Finish(result);
        }

        /// <summary>Clears in-flight state and delivers the UI callback once.</summary>
        void Finish(TitanOrbitRewardedAdResult result)
        {
            IsShowing = false;
            Action<TitanOrbitRewardedAdResult> cb = _pendingCallback;
            _pendingCallback = null;
            _pendingPlacement = null;
            cb?.Invoke(result);
        }
    }
}
