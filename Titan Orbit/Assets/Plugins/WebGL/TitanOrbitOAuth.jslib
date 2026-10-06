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
  },

  // localStorage is synchronous. PlayerPrefs on WebGL writes IndexedDB and can
  // still be empty when the OAuth tab reads it.
  TitanOrbitOAuth_SetItem: function (keyPtr, valuePtr) {
    try {
      var key = UTF8ToString(keyPtr);
      var value = UTF8ToString(valuePtr);
      if (!key) return 0;
      if (value) localStorage.setItem(key, value);
      else localStorage.removeItem(key);
      return 1;
    } catch (e) {
      return 0;
    }
  },

  TitanOrbitOAuth_GetItem: function (keyPtr) {
    try {
      var value = localStorage.getItem(UTF8ToString(keyPtr));
      if (value == null) value = "";
      var size = lengthBytesUTF8(value) + 1;
      var buffer = _malloc(size);
      stringToUTF8(value, buffer, size);
      return buffer;
    } catch (e) {
      return 0;
    }
  },

  TitanOrbitOAuth_Free: function (ptr) {
    if (ptr) _free(ptr);
  }
});
