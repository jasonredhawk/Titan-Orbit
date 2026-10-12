namespace TitanOrbit.Services
{
    /// <summary>
    /// Publisher keys for rewarded ads. The services host is often created at runtime
    /// (<see cref="TitanOrbitServicesRuntimeBootstrap"/>), so Inspector fields on
    /// <see cref="TitanOrbitRewardedAds"/> stay empty unless you place that component in a scene.
    /// Paste dashboard values here (or on the scene component — non-empty Inspector wins).
    /// <para>
    /// [TITAN-ORBIT] WebGL uses the AdSense H5 Ad Placement API. The publisher id below
    /// must match the <c>adsbygoogle.js?client=</c> query on the WebGL template and the
    /// <c>pub-</c> id in <c>Assets/CloudflarePages/ads.txt</c>.
    /// </para>
    /// </summary>
    public static class TitanOrbitRewardedAdsKeys
    {
        /// <summary>
        /// AdSense publisher id for WebGL rewarded ads (H5 Ad Placement API).
        /// The page script in the WebGL template is what actually loads the SDK.
        /// </summary>
        public const string AdSenseClientId = "ca-pub-6601000494638397";

        /// <summary>LevelPlay Android app key.</summary>
        public const string LevelPlayAndroidAppKey = "";

        /// <summary>LevelPlay iOS app key.</summary>
        public const string LevelPlayIosAppKey = "";

        /// <summary>LevelPlay rewarded ad unit id (Android).</summary>
        public const string LevelPlayRewardedAdUnitIdAndroid = "";

        /// <summary>LevelPlay rewarded ad unit id (iOS).</summary>
        public const string LevelPlayRewardedAdUnitIdIos = "";
    }
}
