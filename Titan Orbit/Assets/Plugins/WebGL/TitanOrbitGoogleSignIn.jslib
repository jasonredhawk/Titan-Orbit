// TitanOrbitGoogleSignIn.jslib
// Official Sign in with Google button, aligned to the Unity sign-in card.
// The credential (Google ID token) is sent back with SendMessage.

mergeInto(LibraryManager.library, {
  TitanOrbitGoogle_SyncButton: function (clientIdPtr, objectNamePtr, methodNamePtr, x0, y0, x1, y1) {
    var clientId = UTF8ToString(clientIdPtr);
    var objectName = UTF8ToString(objectNamePtr);
    var methodName = UTF8ToString(methodNamePtr);
    if (!clientId || !objectName || !methodName)
      return;

    window.TitanOrbitGoogleCallbackObject = objectName;
    window.TitanOrbitGoogleCallbackMethod = methodName;

    var canvas = document.querySelector("#unity-canvas");
    if (!canvas || !canvas.width || !canvas.height)
      return;
    var rect = canvas.getBoundingClientRect();
    var scaleX = rect.width / canvas.width;
    var scaleY = rect.height / canvas.height;
    var left = rect.left + x0 * scaleX;
    var right = rect.left + x1 * scaleX;
    var top = rect.top + (canvas.height - y1) * scaleY;
    var width = Math.max(40, right - left);
    var height = Math.max(32, (y1 - y0) * scaleY);
    if (width < 8 || height < 8)
      return;

    function send(payload) {
      var target = window.TitanOrbitGoogleCallbackObject;
      var method = window.TitanOrbitGoogleCallbackMethod;
      if (target && method)
        SendMessage(target, method, payload);
    }

    function place() {
      if (!window.google || !google.accounts || !google.accounts.id) {
        if (!window.TitanOrbitGoogleLoadFailed)
          send("error:Google sign-in did not load. Try again.");
        return;
      }

      var el = document.getElementById("titan-orbit-google-btn");
      var created = !el;
      if (!el) {
        el = document.createElement("div");
        el.id = "titan-orbit-google-btn";
        el.style.position = "fixed";
        el.style.zIndex = "100002";
        el.style.overflow = "hidden";
        el.style.display = "flex";
        el.style.alignItems = "center";
        el.style.justifyContent = "center";
        document.body.appendChild(el);
      }
      el.style.left = Math.round(left) + "px";
      el.style.top = Math.round(top) + "px";
      el.style.width = Math.round(width) + "px";
      el.style.height = Math.round(height) + "px";

      if (created || window.TitanOrbitGoogleRenderedFor !== clientId) {
        el.innerHTML = "";
        google.accounts.id.initialize({
          client_id: clientId,
          callback: function (response) {
            if (response && response.credential)
              send(response.credential);
            else
              send("error:Google did not return a sign-in.");
          },
          auto_select: false,
          cancel_on_tap_outside: true,
          ux_mode: "popup",
          context: "signin"
        });
        google.accounts.id.renderButton(el, {
          type: "standard",
          theme: "filled_blue",
          size: "large",
          text: "signin_with",
          shape: "rectangular",
          width: Math.max(200, Math.min(400, Math.round(width))),
          logo_alignment: "left"
        });
        window.TitanOrbitGoogleRenderedFor = clientId;
      }
    }

    if (window.google && google.accounts && google.accounts.id) {
      place();
      return;
    }

    window.TitanOrbitGooglePendingPlace = place;
    if (window.TitanOrbitGsiLoading)
      return;

    window.TitanOrbitGsiLoading = true;
    var script = document.createElement("script");
    script.src = "https://accounts.google.com/gsi/client";
    script.async = true;
    script.onload = function () {
      window.TitanOrbitGsiLoading = false;
      if (typeof window.TitanOrbitGooglePendingPlace === "function")
        window.TitanOrbitGooglePendingPlace();
    };
    script.onerror = function () {
      window.TitanOrbitGsiLoading = false;
      window.TitanOrbitGoogleLoadFailed = true;
      send("error:Google sign-in did not load. Try again.");
    };
    document.head.appendChild(script);
  },

  TitanOrbitGoogle_Dismiss: function () {
    window.TitanOrbitGooglePendingPlace = null;
    window.TitanOrbitGoogleRenderedFor = "";
    var el = document.getElementById("titan-orbit-google-btn");
    if (el && el.parentNode)
      el.parentNode.removeChild(el);
  }
});
