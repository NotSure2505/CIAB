mergeInto(LibraryManager.library, {

  OpenAuthWindow: function(urlPtr) {
    var url = UTF8ToString(urlPtr);
    var popup = window.open(url, 'itchio_auth', 'width=620,height=720');
    if (!popup) {
      window.location.href = url;
    }
  },

  RegisterAuthCallback: function() {
    window.addEventListener('message', function(event) {
      try {
        var data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        if (data && data.type === 'AUTH_SUCCESS') {
          SendMessage('GameManager', 'HandleWebGLAuthMessage', JSON.stringify(data));
        }
      } catch(e) {
        console.warn('[CarrotBox] Could not parse auth message:', e);
      }
    }, false);
    console.log('[CarrotBox] Auth callback listener registered');
  },

  // ── Clipboard (WebGL safe) ────────────────────────────────────────────────
  CopyToClipboard: function(textPtr) {
    var text = UTF8ToString(textPtr);

    function fallbackCopy(t) {
      var el = document.createElement('textarea');
      el.value = t;
      el.style.position = 'fixed';
      el.style.opacity  = '0';
      document.body.appendChild(el);
      el.focus();
      el.select();
      try {
        document.execCommand('copy');
        SendMessage('GameManager', 'OnClipboardCopied', 'success');
      } catch(e) {
        console.warn('[CarrotBox] Copy failed:', e);
        SendMessage('GameManager', 'OnClipboardCopied', 'failed');
      }
      document.body.removeChild(el);
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(function() {
        console.log('[CarrotBox] Copied to clipboard');
        SendMessage('GameManager', 'OnClipboardCopied', 'success');
      }).catch(function() {
        fallbackCopy(text);
      });
    } else {
      fallbackCopy(text);
    }
  }

});
