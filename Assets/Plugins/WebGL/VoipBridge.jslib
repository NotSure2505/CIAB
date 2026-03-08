/**
 * Carrot in a Box — VoipBridge.jslib
 *
 * WebRTC peer-to-peer voice chat.
 * Signalling is relayed through the existing Socket.io connection
 * (MatchmakingSocket.jslib / window._ciabSocket).
 *
 * Flow:
 *   1. VoipInit(roomId, isInitiator) — called after match_found
 *   2. Initiator (peeker role) creates offer → sends via socket voip_signal
 *   3. Responder (guesser role) receives offer → sends answer
 *   4. ICE candidates exchanged → audio streams
 *   5. Voice Activity Detection pulses OnOpponentVoiceActivity to Unity
 */

mergeInto(LibraryManager.library, {

  // ── VoipInit ─────────────────────────────────────────────────────────────────
  VoipInit: async function(roomIdPtr, isInitiator) {
    var roomId      = UTF8ToString(roomIdPtr);
    var initiator   = !!isInitiator;

    console.log('[VOIP] Init — room:', roomId, '| initiator:', initiator);

    // Clean up any previous session
    if (window._ciabVoip) {
      try { window._ciabVoip.pc.close(); } catch(e) {}
      if (window._ciabVoip.vadTimer) clearInterval(window._ciabVoip.vadTimer);
      window._ciabVoip = null;
    }

    // ── Request microphone ────────────────────────────────────────────────────
    var stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
    } catch(err) {
      console.error('[VOIP] Mic permission denied:', err);
      SendMessage('GameManager', 'OnMicPermissionDenied', 'denied');
      return;
    }

    // ── Create RTCPeerConnection ──────────────────────────────────────────────
    var config = {
      iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' }
      ]
    };
    var pc = new RTCPeerConnection(config);

    window._ciabVoip = { pc: pc, stream: stream, muted: false, vadTimer: null };

    // Add local audio tracks
    stream.getTracks().forEach(function(track) {
      pc.addTrack(track, stream);
    });

    // ── Remote audio playback ─────────────────────────────────────────────────
    var remoteAudio = document.createElement('audio');
    remoteAudio.autoplay = true;
    remoteAudio.id = 'ciab-remote-audio';
    document.body.appendChild(remoteAudio);
    window._ciabVoip.remoteAudio = remoteAudio;

    pc.ontrack = function(event) {
      console.log('[VOIP] Remote track received');
      if (remoteAudio.srcObject !== event.streams[0]) {
        remoteAudio.srcObject = event.streams[0];
      }
      // Start Voice Activity Detection on remote stream
      _ciabStartVAD(event.streams[0]);
    };

    // ── ICE candidate handling ────────────────────────────────────────────────
    pc.onicecandidate = function(event) {
      if (event.candidate) {
        var signal = JSON.stringify({
          type:      'ice',
          roomId:    roomId,
          candidate: event.candidate
        });
        // Relay through Unity → MatchmakingSocket.jslib → Socket.io
        SendMessage('GameManager', 'SendVoipSignalRelay', signal);
      }
    };

    pc.onconnectionstatechange = function() {
      console.log('[VOIP] Connection state:', pc.connectionState);
      if (pc.connectionState === 'connected') {
        SendMessage('GameManager', 'OnVoipConnectedCallback', 'connected');
      } else if (pc.connectionState === 'disconnected' || pc.connectionState === 'failed') {
        SendMessage('GameManager', 'OnVoipDisconnectedCallback', 'disconnected');
      }
    };

    // Register socket listener for incoming voip signals
    if (window._ciabSocket) {
      window._ciabSocket.on('voip_signal', function(data) {
        _ciabHandleIncomingSignal(data);
      });
    }

    // ── Initiate offer (peeker goes first) ────────────────────────────────────
    if (initiator) {
      try {
        var offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        var signal = JSON.stringify({
          type:   'offer',
          roomId: roomId,
          sdp:    pc.localDescription
        });
        SendMessage('GameManager', 'SendVoipSignalRelay', signal);
        console.log('[VOIP] Offer sent');
      } catch(err) {
        console.error('[VOIP] Failed to create offer:', err);
      }
    }
  },

  // ── VoipHandleSignal ─────────────────────────────────────────────────────────
  // Called by Unity C# when a voip_signal arrives via Socket.io
  VoipHandleSignal: async function(signalJsonPtr) {
    var signalJson = UTF8ToString(signalJsonPtr);
    var data;
    try { data = JSON.parse(signalJson); } catch(e) { return; }
    await _ciabHandleIncomingSignal(data);
  },

  // ── VoipSetMuted ─────────────────────────────────────────────────────────────
  VoipSetMuted: function(muted) {
    if (!window._ciabVoip || !window._ciabVoip.stream) return;
    window._ciabVoip.muted = !!muted;
    window._ciabVoip.stream.getAudioTracks().forEach(function(track) {
      track.enabled = !muted;
    });
    console.log('[VOIP] Muted:', muted);
  },

  // ── VoipDisconnect ────────────────────────────────────────────────────────────
  VoipDisconnect: function() {
    if (window._ciabVoip) {
      try {
        window._ciabVoip.stream.getTracks().forEach(function(t) { t.stop(); });
        window._ciabVoip.pc.close();
      } catch(e) {}
      if (window._ciabVoip.vadTimer) clearInterval(window._ciabVoip.vadTimer);
      var audio = document.getElementById('ciab-remote-audio');
      if (audio) audio.remove();
      window._ciabVoip = null;
      console.log('[VOIP] Disconnected and cleaned up');
    }
    if (window._ciabSocket) {
      window._ciabSocket.off('voip_signal');
    }
  },

  // ── SocketSendVoipSignal ──────────────────────────────────────────────────────
  // Called by VoipManager.cs to relay WebRTC signals through Socket.io
  SocketSendVoipSignal: function(signalJsonPtr) {
    var signalJson = UTF8ToString(signalJsonPtr);
    if (window._ciabSocket && window._ciabSocket.connected) {
      var data = JSON.parse(signalJson);
      window._ciabSocket.emit('voip_signal', data);
    } else {
      console.warn('[VOIP] Socket not connected — cannot relay signal');
    }
  }

});

// ── Internal helpers (not exported) ──────────────────────────────────────────

async function _ciabHandleIncomingSignal(data) {
  if (!window._ciabVoip) return;
  var pc = window._ciabVoip.pc;

  try {
    if (data.type === 'offer') {
      console.log('[VOIP] Received offer, creating answer...');
      await pc.setRemoteDescription(new RTCSessionDescription(data.sdp));
      var answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      var signal = JSON.stringify({
        type:   'answer',
        roomId: data.roomId,
        sdp:    pc.localDescription
      });
      SendMessage('GameManager', 'SendVoipSignalRelay', signal);
      console.log('[VOIP] Answer sent');

    } else if (data.type === 'answer') {
      console.log('[VOIP] Received answer');
      await pc.setRemoteDescription(new RTCSessionDescription(data.sdp));

    } else if (data.type === 'ice') {
      if (data.candidate) {
        await pc.addIceCandidate(new RTCIceCandidate(data.candidate));
      }
    }
  } catch(err) {
    console.error('[VOIP] Signal handling error:', err);
  }
}

function _ciabStartVAD(remoteStream) {
  // Voice Activity Detection using Web Audio API
  try {
    var AudioContext = window.AudioContext || window.webkitAudioContext;
    var ctx    = new AudioContext();
    var source = ctx.createMediaStreamSource(remoteStream);
    var analyser = ctx.createAnalyser();
    analyser.fftSize = 512;
    analyser.smoothingTimeConstant = 0.8;
    source.connect(analyser);

    var buf = new Uint8Array(analyser.frequencyBinCount);
    var speaking = false;

    if (window._ciabVoip) {
      window._ciabVoip.vadTimer = setInterval(function() {
        analyser.getByteFrequencyData(buf);
        var sum = 0;
        for (var i = 0; i < buf.length; i++) sum += buf[i];
        var avg = sum / buf.length;
        var nowSpeaking = avg > 12; // threshold — tune as needed
        if (nowSpeaking !== speaking) {
          speaking = nowSpeaking;
          SendMessage('GameManager', 'OnOpponentVoiceActivity', speaking ? '1' : '0');
        }
      }, 100);
    }
  } catch(err) {
    console.warn('[VOIP] VAD init failed:', err);
  }
}
