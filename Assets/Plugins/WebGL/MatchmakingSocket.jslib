/**
 * Carrot in a Box — MatchmakingSocket.jslib
 *
 * Handles all Socket.io communication between Unity (C#) and the server:
 *   - Authentication
 *   - Matchmaking queue & invite codes
 *   - Game event dispatch (→ GameController.OnGameEvent)
 *   - WebRTC VOIP signal relay (→ VoipManager.HandleVoipSignal)
 *   - Clipboard utility
 */

mergeInto(LibraryManager.library, {

  // ── SocketConnect ─────────────────────────────────────────────────────────────
  SocketConnect: function(serverUrlPtr, tokenPtr) {
    var serverUrl = UTF8ToString(serverUrlPtr);
    var token     = UTF8ToString(tokenPtr);

    if (window._ciabSocket) {
      window._ciabSocket.disconnect();
      window._ciabSocket = null;
    }

    function initSocket() {
      var socket = io(serverUrl, {
        transports:           ['websocket', 'polling'],
        reconnectionAttempts: 5,
        reconnectionDelay:    2000
      });

      window._ciabSocket = socket;

      // ── Connection ──────────────────────────────────────────────────────────
      socket.on('connect', function() {
        console.log('[CIAB Socket] Connected:', socket.id);
        socket.emit('authenticate', token);
        SendMessage('GameManager', 'OnSocketConnected', 'connected');
      });

      socket.on('authenticated', function(data) {
        console.log('[CIAB Socket] Authenticated as:', data.displayName);
      });

      socket.on('auth_error', function(err) {
        console.error('[CIAB Socket] Auth error:', err);
      });

      // ── Lobby / Matchmaking ─────────────────────────────────────────────────
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

      // ── Game Events → GameController ────────────────────────────────────────
      // Covers: game_peek_ready, game_deliberate_start, game_decision_start,
      //         game_reveal, game_result, voip_signal
      socket.on('game_event', function(data) {
        var json = JSON.stringify(data);
        SendMessage('GameManager', 'OnGameEvent', json);
      });

      // ── CRS Update → MatchmakingManager / LobbyManager ─────────────────────
      socket.on('crs_update', function(data) {
        SendMessage('GameManager', 'OnCRSUpdate', JSON.stringify(data));
      });

      // ── Connection Lifecycle ────────────────────────────────────────────────
      socket.on('disconnect', function(reason) {
        console.log('[CIAB Socket] Disconnected:', reason);
      });

      socket.on('error', function(err) {
        console.error('[CIAB Socket] Error:', err);
      });
    }

    // Load Socket.io client from server if not already present
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

  // ── SocketDisconnect ──────────────────────────────────────────────────────────
  SocketDisconnect: function() {
    if (window._ciabSocket) {
      window._ciabSocket.disconnect();
      window._ciabSocket = null;
      console.log('[CIAB Socket] Disconnected manually');
    }
  },

  // ── SocketJoinQueue ───────────────────────────────────────────────────────────
  SocketJoinQueue: function() {
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('join_queue');
      console.log('[CIAB Socket] Joining queue...');
    } else {
      console.warn('[CIAB Socket] Not connected — cannot join queue');
    }
  },

  // ── SocketLeaveQueue ──────────────────────────────────────────────────────────
  SocketLeaveQueue: function() {
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('leave_queue');
    }
  },

  // ── SocketJoinInvite ──────────────────────────────────────────────────────────
  SocketJoinInvite: function(codePtr) {
    var code = UTF8ToString(codePtr);
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('join_invite', code);
      console.log('[CIAB Socket] Joining invite:', code);
    } else {
      console.warn('[CIAB Socket] Not connected — cannot join invite');
    }
  },

  // ── SocketJoinInviteRoom ──────────────────────────────────────────────────────
  SocketJoinInviteRoom: function(codePtr) {
    var code = UTF8ToString(codePtr);
    if (window._ciabSocket && window._ciabSocket.connected) {
      window._ciabSocket.emit('host_invite_room', code);
      console.log('[CIAB Socket] Hosting invite room:', code);
    }
  },

  // ── GetInviteCodeFromURL ──────────────────────────────────────────────────────
  GetInviteCodeFromURL: function() {
    // Check URL hash e.g. #invite=a3f9c2
    var hash  = window.location.hash || '';
    var match = hash.match(/[#&]invite=([a-f0-9]{12})/);
    if (match) {
      console.log('[CIAB] Found invite code in URL hash:', match[1]);
      SendMessage('GameManager', 'HandleInviteCode', match[1]);
      return;
    }
    // Also check query string e.g. ?invite=a3f9c2
    var params      = new URLSearchParams(window.location.search);
    var inviteParam = params.get('invite');
    if (inviteParam) {
      console.log('[CIAB] Found invite code in query string:', inviteParam);
      SendMessage('GameManager', 'HandleInviteCode', inviteParam);
    }
  },

  // ── SocketSendGameEvent ───────────────────────────────────────────────────────
  // Called by GameController.cs to send game actions to the server.
  // e.g. game_decision { swap: true }, game_result_ack { won: true }
  SocketSendGameEvent: function(eventTypePtr, payloadJsonPtr) {
    var eventType  = UTF8ToString(eventTypePtr);
    var payloadStr = UTF8ToString(payloadJsonPtr);

    if (!window._ciabSocket || !window._ciabSocket.connected) {
      console.warn('[CIAB Socket] Not connected — cannot send game event:', eventType);
      return;
    }

    var payload;
    try   { payload = JSON.parse(payloadStr); }
    catch (e) { payload = {}; }

    window._ciabSocket.emit(eventType, payload);
    console.log('[CIAB Socket] Game event sent:', eventType, payload);
  },

  // ── SocketSendVoipSignal ──────────────────────────────────────────────────────
  // Called by VoipManager.cs to relay a WebRTC signal through Socket.io.
  // The server echoes it to the opponent as a game_event { eventType: 'voip_signal' }.
  SocketSendVoipSignal: function(signalJsonPtr) {
    var signalJson = UTF8ToString(signalJsonPtr);

    if (!window._ciabSocket || !window._ciabSocket.connected) {
      console.warn('[CIAB Socket] Not connected — cannot relay VOIP signal');
      return;
    }

    var data;
    try   { data = JSON.parse(signalJson); }
    catch (e) { console.error('[CIAB Socket] Bad VOIP signal JSON:', signalJson); return; }

    window._ciabSocket.emit('voip_signal', data);
  },

  // ── CopyToClipboard ───────────────────────────────────────────────────────────
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
        console.warn('[CIAB] Copy failed:', e);
        SendMessage('GameManager', 'OnClipboardCopied', 'failed');
      }
      document.body.removeChild(el);
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text)
        .then(function() {
          console.log('[CIAB] Copied to clipboard');
          SendMessage('GameManager', 'OnClipboardCopied', 'success');
        })
        .catch(function() { fallbackCopy(text); });
    } else {
      fallbackCopy(text);
    }
  }

});
