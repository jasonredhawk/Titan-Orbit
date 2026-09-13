// AppLixirBridge.jslib
// Official AppLixir Unity WebGL bridge (apiKey passed from C# so we do not commit secrets).
// Source pattern: https://github.com/applixirinc/applixir-integration
// Place this file in: Assets/Plugins/WebGL/AppLixirBridge.jslib

mergeInto(LibraryManager.library, {

  // Call this from C# to initialize and play a rewarded ad.
  // callbackObjectName = Unity GameObject that receives SendMessage
  // callbackMethodName = C# method that accepts the status.type string
  // apiKey = publisher key from the AppLixir dashboard
  PlayRewardedAd: function (callbackObjectNamePtr, callbackMethodNamePtr, apiKeyPtr) {
    var callbackObjectName = UTF8ToString(callbackObjectNamePtr);
    var callbackMethodName = UTF8ToString(callbackMethodNamePtr);
    var apiKey = UTF8ToString(apiKeyPtr);

    var container = document.getElementById("applixir-ad-container");
    if (!container) {
      container = document.createElement("div");
      container.id = "applixir-ad-container";
      container.style.cssText = "position:fixed;top:0;left:0;width:100%;height:100%;z-index:9999;";
      document.body.appendChild(container);
    }

    var options = {
      apiKey: apiKey,
      injectionElementId: "applixir-ad-container",

      adStatusCallbackFn: function (status) {
        // status is an OBJECT { type, ad?, error? }. Unity SendMessage only
        // accepts a string, so forward status.type (e.g. "complete").
        var type = status && status.type ? status.type : status;
        SendMessage(callbackObjectName, callbackMethodName, type);
      },

      adErrorCallbackFn: function (error) {
        SendMessage(callbackObjectName, callbackMethodName, "error");
      }
    };

    if (typeof initializeAndOpenPlayer === "function") {
      initializeAndOpenPlayer(options);
    } else {
      SendMessage(callbackObjectName, callbackMethodName, "sdk-not-loaded");
    }
  }

});
