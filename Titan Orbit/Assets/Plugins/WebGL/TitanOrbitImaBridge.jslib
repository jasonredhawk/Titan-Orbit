// TitanOrbitImaBridge.jslib
// Unity WebGL → Google IMA HTML5 player in the FullscreenViewport template.
// The template defines window.TitanOrbitImaPlay(adTagUrl, onStatus).

mergeInto(LibraryManager.library, {
  TitanOrbitIma_PlayRewarded: function (callbackObjectNamePtr, callbackMethodNamePtr, adTagUrlPtr) {
    var callbackObjectName = UTF8ToString(callbackObjectNamePtr);
    var callbackMethodName = UTF8ToString(callbackMethodNamePtr);
    var adTagUrl = UTF8ToString(adTagUrlPtr);

    function send(status) {
      SendMessage(callbackObjectName, callbackMethodName, status);
    }

    if (typeof TitanOrbitImaPlay !== "function") {
      send("sdk-not-loaded");
      return;
    }

    TitanOrbitImaPlay(adTagUrl, send);
  }
});
