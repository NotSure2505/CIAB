using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Carrot in a Box — VOIP Manager
/// Handles WebRTC peer-to-peer voice chat via a jslib bridge.
/// Signalling is piggybacked on the existing Socket.io connection.
///
/// Scene Setup:
///   - Attach to GameManager alongside MatchmakingManager
///   - Wire up UI references in Inspector
///   - Ensure VoipBridge.jslib is in Assets/Plugins/WebGL/
/// </summary>
public class VoipManager : MonoBehaviour
{
    public static VoipManager Instance { get; private set; }

    [Header("UI References")]
    [SerializeField] private GameObject micIndicator;      // pulsing mic icon (local)
    [SerializeField] private GameObject opponentMicIcon;   // speaking indicator (remote)
    [SerializeField] private Button     muteButton;
    [SerializeField] private TMP_Text   muteButtonLabel;
    [SerializeField] private TMP_Text   voipStatusText;    // "Connected" / "Connecting..." / "No mic"

    // ── State ──────────────────────────────────────────────────────────────────
    public bool IsConnected   { get; private set; }
    public bool IsMuted       { get; private set; }
    public bool OpponentSpeaking { get; private set; }

    // ── Events ─────────────────────────────────────────────────────────────────
    public event Action         OnVoipConnected;
    public event Action         OnVoipDisconnected;
    public event Action<bool>   OnOpponentSpeakingChanged;  // true = speaking

    // ── jslib Imports ──────────────────────────────────────────────────────────
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void VoipInit(string roomId, bool isInitiator);
    [DllImport("__Internal")] private static extern void VoipSetMuted(bool muted);
    [DllImport("__Internal")] private static extern void VoipDisconnect();
    [DllImport("__Internal")] private static extern void VoipHandleSignal(string signalJson);
#endif

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        if (muteButton) muteButton.onClick.AddListener(ToggleMute);
        UpdateMuteUI();
        SetVoipStatus("Waiting for match...");
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Call after a match is found. isInitiator = true for the "peeker" role.
    /// </summary>
    public void StartVoip(string roomId, bool isInitiator)
    {
        SetVoipStatus("Connecting voice...");
        Debug.Log($"[VOIP] Starting — room: {roomId}, initiator: {isInitiator}");

#if UNITY_WEBGL && !UNITY_EDITOR
        VoipInit(roomId, isInitiator);
#else
        // Editor stub
        OnVoipConnectedCallback("connected");
#endif
    }

    public void StopVoip()
    {
        IsConnected = false;
        SetVoipStatus("Disconnected");
        if (micIndicator) micIndicator.SetActive(false);
        if (opponentMicIcon) opponentMicIcon.SetActive(false);

#if UNITY_WEBGL && !UNITY_EDITOR
        VoipDisconnect();
#endif
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        UpdateMuteUI();

#if UNITY_WEBGL && !UNITY_EDITOR
        VoipSetMuted(IsMuted);
#endif
        Debug.Log($"[VOIP] Muted: {IsMuted}");
    }

    /// <summary>
    /// Called by MatchmakingSocket.jslib when a WebRTC signal arrives via Socket.io.
    /// Forward it to VoipBridge.jslib to handle ICE / offer / answer.
    /// </summary>
    public void HandleVoipSignal(string signalJson)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        VoipHandleSignal(signalJson);
#endif
    }

    // ── Callbacks from jslib ───────────────────────────────────────────────────

    /// <summary>WebRTC peer connection established.</summary>
    public void OnVoipConnectedCallback(string msg)
    {
        IsConnected = true;
        SetVoipStatus("Voice connected");
        if (micIndicator) micIndicator.SetActive(true);
        OnVoipConnected?.Invoke();
        Debug.Log("[VOIP] Connected!");
    }

    /// <summary>WebRTC peer disconnected.</summary>
    public void OnVoipDisconnectedCallback(string msg)
    {
        IsConnected = false;
        SetVoipStatus("Voice disconnected");
        if (micIndicator) micIndicator.SetActive(false);
        if (opponentMicIcon) opponentMicIcon.SetActive(false);
        OnVoipDisconnected?.Invoke();
    }

    /// <summary>Microphone permission denied.</summary>
    public void OnMicPermissionDenied(string msg)
    {
        SetVoipStatus("⚠ Mic access denied");
        Debug.LogWarning("[VOIP] Microphone permission denied.");
    }

    /// <summary>
    /// Called by jslib when remote voice activity is detected.
    /// Payload: "1" (speaking) or "0" (silent)
    /// </summary>
    public void OnOpponentVoiceActivity(string payload)
    {
        bool speaking = payload == "1";
        if (speaking == OpponentSpeaking) return;
        OpponentSpeaking = speaking;
        if (opponentMicIcon) opponentMicIcon.SetActive(speaking);
        OnOpponentSpeakingChanged?.Invoke(speaking);
    }

    /// <summary>
    /// Called by jslib to relay a WebRTC signal through Unity back to
    /// MatchmakingSocket.jslib so it can be sent via Socket.io.
    /// </summary>
    public void SendVoipSignalRelay(string signalJson)
    {
        // Forward to the socket bridge
#if UNITY_WEBGL && !UNITY_EDITOR
        SocketSendVoipSignal(signalJson);
#endif
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private void SetVoipStatus(string msg)
    {
        if (voipStatusText) voipStatusText.text = msg;
    }

    private void UpdateMuteUI()
    {
        if (muteButtonLabel)
            muteButtonLabel.text = IsMuted ? "🔇 Unmute" : "🎤 Mute";
        if (micIndicator)
        {
            // Dim the mic indicator when muted
            var img = micIndicator.GetComponent<UnityEngine.UI.Image>();
            if (img) img.color = IsMuted
                ? new Color(1f, 0.3f, 0.3f, 0.6f)
                : new Color(1f, 1f, 1f, 1f);
        }
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void SocketSendVoipSignal(string signalJson);
#endif
}
