using System;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// LevelPlay (Unity Ads Mediation) rewarded-video backend for Android and iOS.
    /// Compiles as a stub until <c>com.unity.services.levelplay</c> is in the project
    /// (asmdef version define <c>TITAN_ORBIT_LEVELPLAY</c>).
    /// <para>
    /// [TITAN-ORBIT] Unity Ads was removed from this repo because the package pulled
    /// Android AARs into unrelated resolves. LevelPlay stays behind
    /// <c>#if UNITY_ANDROID || UNITY_IOS</c> and is never initialized on WebGL or
    /// the dedicated Linux server.
    /// </para>
    /// </summary>
    public static class TitanOrbitLevelPlayBackend
    {
        /// <summary>True after a successful <see cref="TryInitialize"/>.</summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// Starts LevelPlay and preloads one rewarded unit.
        /// No-op when the package is missing or keys are empty.
        /// </summary>
        public static void TryInitialize(
            string androidAppKey,
            string iosAppKey,
            string androidRewardedAdUnitId,
            string iosRewardedAdUnitId)
        {
#if (UNITY_ANDROID || UNITY_IOS) && TITAN_ORBIT_LEVELPLAY && !UNITY_EDITOR
            string appKey;
            string adUnitId;
#if UNITY_IOS
            appKey = iosAppKey;
            adUnitId = iosRewardedAdUnitId;
#else
            appKey = androidAppKey;
            adUnitId = androidRewardedAdUnitId;
#endif
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(adUnitId))
            {
                Debug.Log("[TitanOrbitLevelPlayBackend] Skipping init (missing app key or rewarded ad unit).");
                return;
            }

            LevelPlayBackendImpl.TryInitialize(appKey, adUnitId);
#else
            _ = androidAppKey;
            _ = iosAppKey;
            _ = androidRewardedAdUnitId;
            _ = iosRewardedAdUnitId;
            Debug.Log("[TitanOrbitLevelPlayBackend] LevelPlay package not present or not a mobile player — stub init.");
#endif
        }

        /// <summary>
        /// Shows a loaded rewarded ad. Returns false when nothing is ready
        /// (caller maps that to Unavailable).
        /// </summary>
        public static bool TryShow(Action<TitanOrbitRewardedAdResult> onFinished)
        {
#if (UNITY_ANDROID || UNITY_IOS) && TITAN_ORBIT_LEVELPLAY && !UNITY_EDITOR
            return LevelPlayBackendImpl.TryShow(onFinished);
#else
            onFinished?.Invoke(TitanOrbitRewardedAdResult.Unavailable);
            return false;
#endif
        }

#if (UNITY_ANDROID || UNITY_IOS) && TITAN_ORBIT_LEVELPLAY && !UNITY_EDITOR
        /// <summary>
        /// Isolated so the rest of Services compiles without Unity.Services.LevelPlay types.
        /// </summary>
        static class LevelPlayBackendImpl
        {
            static LevelPlayRewardedAd s_rewarded;
            static Action<TitanOrbitRewardedAdResult> s_pending;
            static string s_adUnitId;

            public static void TryInitialize(string appKey, string adUnitId)
            {
                s_adUnitId = adUnitId;
                LevelPlay.OnInitSuccess += _ =>
                {
                    IsInitialized = true;
                    s_rewarded = new LevelPlayRewardedAd(s_adUnitId);
                    s_rewarded.OnAdRewarded += (_, _) => Finish(TitanOrbitRewardedAdResult.Completed);
                    s_rewarded.OnAdDisplayFailed += _ => Finish(TitanOrbitRewardedAdResult.Failed);
                    s_rewarded.OnAdClosed += _ =>
                    {
                        // Closed without OnAdRewarded — treat as fail if still waiting.
                        if (s_pending != null)
                            Finish(TitanOrbitRewardedAdResult.Failed);
                    };
                    s_rewarded.LoadAd();
                };
                LevelPlay.OnInitFailed += error =>
                {
                    Debug.LogWarning("[TitanOrbitLevelPlayBackend] Init failed: " + error);
                    IsInitialized = false;
                };
                LevelPlay.Init(appKey);
            }

            public static bool TryShow(Action<TitanOrbitRewardedAdResult> onFinished)
            {
                if (s_rewarded == null || !s_rewarded.IsAdReady())
                {
                    s_rewarded?.LoadAd();
                    return false;
                }

                s_pending = onFinished;
                s_rewarded.ShowAd();
                return true;
            }

            static void Finish(TitanOrbitRewardedAdResult result)
            {
                Action<TitanOrbitRewardedAdResult> cb = s_pending;
                s_pending = null;
                s_rewarded?.LoadAd();
                cb?.Invoke(result);
            }
        }
#endif
    }
}
