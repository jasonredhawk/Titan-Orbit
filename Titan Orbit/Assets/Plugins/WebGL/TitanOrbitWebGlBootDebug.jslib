mergeInto(LibraryManager.library, {
  /**
   * Debug-mode ingest for WebGL boot crashes.
   * Posts to the local Cursor debug log server. No-ops if unreachable.
   */
  TitanOrbitDebug_Log: function (hypothesisIdPtr, locationPtr, messagePtr, dataJsonPtr) {
    try {
      var hypothesisId = UTF8ToString(hypothesisIdPtr);
      var location = UTF8ToString(locationPtr);
      var message = UTF8ToString(messagePtr);
      var dataJson = UTF8ToString(dataJsonPtr);
      var data = {};
      try { data = dataJson ? JSON.parse(dataJson) : {}; } catch (e) { data = { raw: dataJson }; }
      var payload = {
        sessionId: '1a7cd0',
        runId: 'webgl-boot',
        hypothesisId: hypothesisId,
        location: location,
        message: message,
        data: data,
        timestamp: Date.now()
      };
      console.log('[WebGLBoot][' + hypothesisId + '] ' + location + ' | ' + message + ' | ' + dataJson);
      // Hosted titanorbit.io cannot reach this ingest; localhost Fast Iterate can.
      // Swallow failures so a dead ingest never dumps a rAF stack during join.
      try {
        fetch('http://127.0.0.1:7774/ingest/30ccdc0d-4064-42d7-ab07-612840f5e6a2', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Debug-Session-Id': '1a7cd0' },
          body: JSON.stringify(payload)
        }).catch(function () {});
      } catch (e2) {}
      return;
    } catch (e) {}
  }
});
