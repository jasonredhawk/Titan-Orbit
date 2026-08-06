mergeInto(LibraryManager.library, {
  /**
   * [TITAN-ORBIT] Debug-mode ingest for WebGL boot crashes.
   * Posts NDJSON-compatible payload to the local Cursor debug log server.
   * No-ops if the server is unreachable (production / remote testers).
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
        sessionId: '9ed833',
        hypothesisId: hypothesisId,
        location: location,
        message: message,
        data: data,
        timestamp: Date.now(),
        runId: 'webgl-boot'
      };
      // #region agent log
      fetch('http://127.0.0.1:7811/ingest/30ccdc0d-4064-42d7-ab07-612840f5e6a2', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Debug-Session-Id': '9ed833'
        },
        body: JSON.stringify(payload)
      }).catch(function () {});
      // #endregion
    } catch (e) {}
  }
});
