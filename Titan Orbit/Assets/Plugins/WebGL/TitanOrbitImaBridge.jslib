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

    if (typeof TitanOrbitH5RewardedPlay !== "function") {
      send("sdk-not-loaded");
      return;
    }

    TitanOrbitH5RewardedPlay(placement, send);
  }
});
