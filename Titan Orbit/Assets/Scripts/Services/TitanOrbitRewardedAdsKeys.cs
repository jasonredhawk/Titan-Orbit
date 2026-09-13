namespace TitanOrbit.Services
{
    /// <summary>
    /// Publisher keys for rewarded ads. The services host is often created at runtime
    /// (<see cref="TitanOrbitServicesRuntimeBootstrap"/>), so Inspector fields on
    /// <see cref="TitanOrbitRewardedAds"/> stay empty unless you place that component in a scene.
    /// Paste dashboard values here (or on the scene component — non-empty Inspector wins).
    /// <para>
    /// [TITAN-ORBIT] Do not commit live secrets if this repo is public. AppLixir keys are
    /// client-visible in a WebGL build anyway (the JS player needs them).
    /// </para>
    /// </summary>
    public static class TitanOrbitRewardedAdsKeys
    {
        /// <summary>AppLixir publisher API key from client.applixir.com.</summary>
        public const string AppLixirApiKey = "";

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
