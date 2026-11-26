// TitanOrbitImaBridge.jslib
// Unity WebGL → AdSense H5 rewarded placement in the FullscreenViewport template.
// The template defines window.TitanOrbitH5RewardedPlay(placementName, onStatus).

mergeInto(LibraryManager.library, {
  TitanOrbitIma_PlayRewarded: function (callbackObjectNamePtr, callbackMethodNamePtr, placementPtr) {
    var callbackObjectName = UTF8ToString(callbackObjectNamePtr);
    var callbackMethodName = UTF8ToString(callbackMethodNamePtr);
    var placement = UTF8ToString(placementPtr);

    function send(status) {
      SendMessage(callbackObjectName, callbackMethodName, status);
    }

    var play = (typeof window !== "undefined" && window.TitanOrbitH5RewardedPlay)
      || (typeof TitanOrbitH5RewardedPlay === "function" ? TitanOrbitH5RewardedPlay : null);
    if (typeof play !== "function") {
      send("sdk-not-loaded");
      return;
    }

    play(placement, send);
  },

  TitanOrbitIma_SetGestureHost: function (gameObjectNamePtr) {
    window.TitanOrbitRewardedHost = UTF8ToString(gameObjectNamePtr);
  }
});
