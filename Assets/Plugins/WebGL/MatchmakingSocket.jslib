mergeInto(LibraryManager.library, {

  SocketConnect: function(serverUrlPtr, tokenPtr) {
    var serverUrl = UTF8ToString(serverUrlPtr);
    var token     = UTF8ToString(tokenPtr);

    // Dynamically load Socket.io client from your server
    if (window._ciabSocket) {
      window._ciabSocket.disconnect();
      window._ciabSocket = null;
    }

    function initSocket() {
      var socket = io(serverUrl, {
        transports:         ['websocket', 'polling'],
        reconnectionAttempts: 5,
        reconnectionDelay:  2000
      });

      window._ciabSocket = socket;

      socket.on('connect', function() {
        console.log('[CIAB Socket] Connected:', socket.id);
        // Authenticate immediately on connect
        socket.emit('authenticate', token);
        SendMessage('GameManager', 'OnSocketConnected', 'connected');
      });

      socket.on('authenticated', function(data) {
        console.log('[CIAB Socket] Authenticated as:', data.displayName);
      });

      socket.on('auth_error', function(err) {
        console.error('[CIAB Socket] Auth error:', err);
      });

      socket.on('online_count', function(count) {
        SendMessage('GameManager', 'OnOnlineCountReceived', count.toString());
      });

      socket.on('queue_joined', function(data) {
        console.log('[CIAB Socket] Joined queue, position:', data.position);
      });

      socket.on('match_found', function(data) {
        console.log('[CIAB Socket] Match found:', data);
        SendMessage('GameManager', 'OnMatchFoundReceived', JSON.stringify(data));
      });

      socket.on('opponent_disconnected', function() {
        SendMessage('GameManager', 'OnOpponentDisconnectedReceived', 'disconnected');
      });

      socket.on('invite_error', function(err) {
        console.warn('[CIAB Socket] Invite error:', err);
      });

      socket.on('disconnect', function(reason) {
        console.log('[CIAB Socket] Disconnected:', reason);
      });

      socket.on('error', function(err) {
        console.error('[CIAB Socket] Error:', err);
      });
    }

    // Load Socket.io script if not already loaded
    if (typeof io === 'undefined') {
      var script    = document.createElement('script');
      script.src    = serverUrl + '/socket.io/socket.io.js';
      script.onload = function() {
        console.log('[CIAB] Socket.io loaded');
        initSocket();
      };
      script.onerror = function() {
        console.error('[CIAB] Failed to load Socket.io client');
      };
      document.head.appendChild(script);
    } else {
      initSocket();
    }
  },

  SocketDisconnect: function() {
    if (window._ciabSocket) {
      window._ciabSocket.disconnect();
      window._ciabSocket = null;
      console.log('[CIAB Socket] Disconnected manually');
    }
  },

  SocketJoinQueue: function() {
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('join_queue');
      console.log('[CIAB Socket] Joining queue...');
    } else {
      console.warn('[CIAB Socket] Not connected — cannot join queue');
    }
  },

  SocketLeaveQueue: function() {
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('leave_queue');
    }
  },

  SocketJoinInvite: function(codePtr) {
    var code = UTF8ToString(codePtr);
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('join_invite', code);
      console.log('[CIAB Socket] Joining invite:', code);
    } else {
      console.warn('[CIAB Socket] Not connected — cannot join invite');
    }
  },

  SocketJoinInviteRoom: function(codePtr) {
    var code = UTF8ToString(codePtr);
    // Host waits in the invite room for a guest to arrive
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('host_invite_room', code);
      console.log('[CIAB Socket] Hosting invite room:', code);
    }
  },

  GetInviteCodeFromURL: function() {
    // Read invite code from URL hash e.g. #invite=a3f9c2
    var hash = window.location.hash || '';
    var match = hash.match(/[#&]invite=([a-f0-9]{12})/);
    if (match) {
      var code = match[1];
      console.log('[CIAB] Found invite code in URL:', code);
      SendMessage('GameManager', 'HandleInviteCode', code);
    } else {
      // Also check query string e.g. ?invite=a3f9c2 (itch.io passes params this way)
      var params = new URLSearchParams(window.location.search);
      var inviteParam = params.get('invite');
      if (inviteParam) {
        console.log('[CIAB] Found invite code in query string:', inviteParam);
        SendMessage('GameManager', 'HandleInviteCode', inviteParam);
      }
    }
  }

});
