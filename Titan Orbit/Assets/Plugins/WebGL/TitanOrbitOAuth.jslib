mergeInto(LibraryManager.library, {
  TitanOrbitOAuth_ReplaceUrl: function (urlPtr) {
    try {
      var url = UTF8ToString(urlPtr);
      if (typeof history !== "undefined" && history.replaceState) {
        history.replaceState(null, document.title, url);
        return 1;
      }
    } catch (e) {}
    return 0;
  }
});
