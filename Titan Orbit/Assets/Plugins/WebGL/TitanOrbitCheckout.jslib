mergeInto(LibraryManager.library, {
  // Same-tab navigation. window.open after an async checkout call is blocked.
  TitanOrbitNavigateBrowser: function (urlPtr) {
    try {
      var url = UTF8ToString(urlPtr);
      if (url)
        window.location.assign(url);
    } catch (e) {}
  },

  // index.html stores the Stripe return, then removes it from the address bar.
  TitanOrbitTakeCheckoutReturn: function () {
    try {
      var value = sessionStorage.getItem("titanOrbitCheckoutReturn") || "";
      if (value)
        sessionStorage.removeItem("titanOrbitCheckoutReturn");
      var size = lengthBytesUTF8(value) + 1;
      var buffer = _malloc(size);
      stringToUTF8(value, buffer, size);
      return buffer;
    } catch (e) {
      return 0;
    }
  },

  TitanOrbitFreeCheckoutReturn: function (ptr) {
    if (ptr) _free(ptr);
  },
});
