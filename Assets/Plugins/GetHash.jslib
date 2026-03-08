mergeInto(LibraryManager.library, {
  GetLocationHash: function () {
    var hash = window.location.hash || '';
    var bufferSize = lengthBytesUTF8(hash) + 1;
    var buffer = _malloc(bufferSize);
    stringToUTF8(hash, buffer, bufferSize);
    return buffer;
  }
});