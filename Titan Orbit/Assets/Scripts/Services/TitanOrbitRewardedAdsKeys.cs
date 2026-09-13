namespace TitanOrbit.Services
{
    /// <summary>
    /// Publisher keys / ad tags for rewarded ads. The services host is often created at runtime
    /// (<see cref="TitanOrbitServicesRuntimeBootstrap"/>), so Inspector fields on
    /// <see cref="TitanOrbitRewardedAds"/> stay empty unless you place that component in a scene.
    /// Paste dashboard values here (or on the scene component — non-empty Inspector wins).
    /// <para>
    /// [TITAN-ORBIT] WebGL uses Google IMA + a VAST tag (no 100k-user floor).
    /// The default URL is Google's official single-inline-linear sample so a Cloudflare
    /// build can show a test ad before you create a Google Ad Manager unit.
    /// Replace <see cref="VastAdTagUrl"/> with your own GAM / AdSense-for-Games tag for live fill.
    /// Tags are client-visible in a WebGL build.
    /// </para>
    /// </summary>
    public static class TitanOrbitRewardedAdsKeys
    {
        /// <summary>
        /// VAST ad tag played by Google IMA on WebGL.
        /// Default = Google IMA sample "Single Inline Linear" (non-skippable test ad).
        /// https://developers.google.com/interactive-media-ads/docs/sdks/html5/client-side/tags
        /// </summary>
        public const string VastAdTagUrl =
            "https://pubads.g.doubleclick.net/gampad/ads?iu=/21775744923/external/single_ad_samples&sz=640x480&cust_params=sample_ct%3Dlinear&ciu_szs=300x250%2C728x90&gdfp_req=1&output=vast&unviewed_position_start=1&env=vp&correlator=";

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
