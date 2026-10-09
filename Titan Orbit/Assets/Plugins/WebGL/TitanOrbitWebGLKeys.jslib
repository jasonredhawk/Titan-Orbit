// TitanOrbitWebGLKeys.jslib
// Chrome treats Alt as a menu accelerator. The FullscreenViewport template
// installs the same capture listener at page load. This copy runs if that
// script is missing (cached index.html, or a different WebGL template).

mergeInto(LibraryManager.library, {
  TitanOrbit_InstallAltCapture: function () {
    if (window.__titanOrbitAltCapture) return;
    window.__titanOrbitAltCapture = true;
    if (!window.__titanOrbitAltEdge) window.__titanOrbitAltEdge = 0;

    function isAltKey(e) {
      return e.key === "Alt" || e.code === "AltLeft" || e.code === "AltRight";
    }

    function onAltKey(e) {
      if (!isAltKey(e)) return;
      if (e.cancelable) e.preventDefault();
      if (e.type === "keydown" && !e.repeat)
        window.__titanOrbitAltEdge = 1;
    }

    window.addEventListener("keydown", onAltKey, true);
    window.addEventListener("keyup", onAltKey, true);

    var altLockHeld = false;
    var altLockGaveUp = false;
    function lockAltKeys() {
      if (altLockHeld) return;
      if (altLockGaveUp && !document.fullscreenElement) return;
      var kb = navigator.keyboard;
      if (!kb || !kb.lock) return;
      kb.lock(["AltLeft", "AltRight"]).then(function () {
        altLockHeld = true;
      }).catch(function () {
        if (!document.fullscreenElement) altLockGaveUp = true;
      });
    }
    window.addEventListener("pointerdown", lockAltKeys, true);
    document.addEventListener("fullscreenchange", function () {
      if (!document.fullscreenElement) return;
      altLockGaveUp = false;
      lockAltKeys();
    });
  },

  TitanOrbit_ConsumeAltPressed: function () {
    var edge = window.__titanOrbitAltEdge ? 1 : 0;
    window.__titanOrbitAltEdge = 0;
    return edge;
  }
});
