using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Carrot in a Box — Matchmaking Manager
/// Handles real-time matchmaking via Socket.io through a WebGL jslib bridge.
///
/// Scene Setup:
///   - Attach to GameManager
///   - Wire up UI references in Inspector
///   - Ensure MatchmakingSocket.jslib is in Assets/Plugins/WebGL/
/// </summary>
public class MatchmakingManager : MonoBehaviour
{
    public static MatchmakingManager Instance { get; private set; }

    [Header("Server")]
    [SerializeField] private string serverBaseUrl = "https://sea-turtle-app-k7m7l.ondigitalocean.app";
    [SerializeField] private string itchGameUrl   = "https://notsure2505.itch.io/carrot-in-a-box";

    [Header("UI References")]
    [SerializeField] private TMP_Text   onlineCountText;
    [SerializeField] private Button     findMatchButton;
    [SerializeField] private Button     cancelQueueButton;
    [SerializeField] private Button     createInviteButton;
    [SerializeField] private TMP_Text   queueStatusText;
    [SerializeField] private TMP_Text   inviteLinkText;
    [SerializeField] private Button     copyInviteButton;
    [SerializeField] private GameObject matchmakingPanel;
    [SerializeField] private GameObject invitePanel;

    // ── Events ─────────────────────────────────────────────────────────────────
    public event Action<MatchData> OnMatchFound;
    public event Action<string>    OnOpponentDisconnected;
    public event Action<int>       OnOnlineCountUpdated;

    // ── State ──────────────────────────────────────────────────────────────────
    public bool      IsConnected   { get; private set; }
    public bool      IsInQueue     { get; private set; }
    public MatchData CurrentMatch  { get; private set; }

    private string    _currentInviteUrl;
    // BUG FIX: track pending actions to execute once socket connects
    private bool      _joinQueueOnConnect;
    private bool      _createInviteOnConnect;
    private Coroutine _pollCoroutine;

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        if (findMatchButton)    findMatchButton.onClick.AddListener(JoinQueue);
        if (cancelQueueButton)  cancelQueueButton.onClick.AddListener(LeaveQueue);
        if (createInviteButton) createInviteButton.onClick.AddListener(CreateInviteLink);
        if (copyInviteButton)   copyInviteButton.onClick.AddListener(CopyInviteLink);

        SetQueueUI(false);
        CheckForInviteCode();
    }

    // ── Show Lobby ─────────────────────────────────────────────────────────────
    public void ShowLobby()
    {
        if (matchmakingPanel) matchmakingPanel.SetActive(true);
        if (invitePanel)      invitePanel.SetActive(false);
        SetQueueUI(false);

        // BUG FIX: stop any existing poll coroutine before starting a new one
        // to prevent multiple concurrent polling coroutines accumulating on
        // repeated ShowLobby() calls (e.g. opponent disconnect → back to lobby).
        if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);
        _pollCoroutine = StartCoroutine(PollOnlineCount());
    }

    // ── Socket Connection ──────────────────────────────────────────────────────
    public void ConnectSocket()
    {
        string token = PlayerPrefs.GetString("ciab_token", "");
        if (string.IsNullOrEmpty(token))
        {
            Debug.LogWarning("[Matchmaking] No token — not connecting socket");
            return;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        SocketConnect(serverBaseUrl, token);
#else
        Debug.Log("[Matchmaking] Socket.io not available in Editor — using REST polling");
        IsConnected = true;
        // BUG FIX: in editor, socket "connects" synchronously so run pending actions now
        OnSocketConnected("connected");
#endif
    }

    public void DisconnectSocket()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        SocketDisconnect();
#endif
        IsConnected = false;
    }

    // ── Matchmaking Queue ──────────────────────────────────────────────────────
    public void JoinQueue()
    {
        if (!IsConnected)
        {
            // BUG FIX: was returning immediately after ConnectSocket() without ever
            // actually joining the queue. Now we set a flag and join once connected.
            _joinQueueOnConnect = true;
            ConnectSocket();
            SetQueueUI(true); // show "searching" UI immediately so button feels responsive
            return;
        }

        _joinQueueOnConnect = false;
        IsInQueue = true;
        SetQueueUI(true);

#if UNITY_WEBGL && !UNITY_EDITOR
        SocketJoinQueue();
#else
        StartCoroutine(SimulateQueueEditor());
#endif
        Debug.Log("[Matchmaking] Joined queue");
    }

    public void LeaveQueue()
    {
        _joinQueueOnConnect = false;
        IsInQueue = false;
        SetQueueUI(false);

#if UNITY_WEBGL && !UNITY_EDITOR
        SocketLeaveQueue();
#endif
        Debug.Log("[Matchmaking] Left queue");
    }

    // ── Invite Links ───────────────────────────────────────────────────────────
    public void CreateInviteLink()
    {
        if (!IsConnected)
        {
            // BUG FIX: same pattern as JoinQueue — queue the action for after connect
            _createInviteOnConnect = true;
            ConnectSocket();
            return;
        }

        _createInviteOnConnect = false;
        StartCoroutine(FetchInviteLink());
    }

    private IEnumerator FetchInviteLink()
    {
        if (createInviteButton) createInviteButton.interactable = false;

        string token = PlayerPrefs.GetString("ciab_token", "");
        using var req = new UnityWebRequest($"{serverBaseUrl}/invite/create", "POST");
        req.uploadHandler   = new UploadHandlerRaw(new byte[0]);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {token}");
        yield return req.SendWebRequest();

        if (createInviteButton) createInviteButton.interactable = true;

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning("[Matchmaking] Failed to create invite: " + req.error);
            yield break;
        }

        var data = JsonUtility.FromJson<InviteResponse>(req.downloadHandler.text);
        _currentInviteUrl = data.inviteUrl;

        if (inviteLinkText) inviteLinkText.text = data.inviteUrl;
        if (invitePanel)    invitePanel.SetActive(true);

        // Host joins their own invite room to wait
#if UNITY_WEBGL && !UNITY_EDITOR
        SocketJoinInviteRoom(data.code);
#endif

        Debug.Log("[Matchmaking] Invite created: " + data.inviteUrl);
    }

    private void CopyInviteLink()
    {
        if (string.IsNullOrEmpty(_currentInviteUrl)) return;

        var txt   = copyInviteButton?.GetComponentInChildren<TMP_Text>();
        var flash = txt?.text ?? "Copied!";

#if UNITY_WEBGL && !UNITY_EDITOR
        CopyToClipboard(_currentInviteUrl);
        if (txt != null)
            StartCoroutine(FlashText(txt, "Copied!", flash, 2f));
#else
        GUIUtility.systemCopyBuffer = _currentInviteUrl;
        if (txt != null)
            StartCoroutine(FlashText(txt, "Copied!", flash, 2f));
#endif
    }

    // ── Check for Invite on Load ───────────────────────────────────────────────
    private void CheckForInviteCode()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        GetInviteCodeFromURL();
#endif
    }

    /// <summary>Called by jslib with the invite code from URL hash.</summary>
    public void HandleInviteCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return;
        Debug.Log("[Matchmaking] Joining via invite code: " + code);
        StartCoroutine(ValidateAndJoinInvite(code));
    }

    private IEnumerator ValidateAndJoinInvite(string code)
    {
        using var req = UnityWebRequest.Get($"{serverBaseUrl}/invite/{code}");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success) yield break;

        var data = JsonUtility.FromJson<InviteValidateResponse>(req.downloadHandler.text);
        if (!data.valid) { Debug.LogWarning("[Matchmaking] Invalid invite"); yield break; }

        if (queueStatusText)
            queueStatusText.text = $"Joining {data.hostName}'s game...";

        // Ensure connected before joining invite room
        if (!IsConnected)
        {
            ConnectSocket();
            // Wait for connection (up to 5 seconds)
            float waited = 0f;
            while (!IsConnected && waited < 5f)
            {
                waited += Time.deltaTime;
                yield return null;
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        SocketJoinInvite(code);
#endif
    }

    // ── Callbacks from jslib ───────────────────────────────────────────────────

    /// <summary>Called by jslib when socket connects.</summary>
    public void OnSocketConnected(string msg)
    {
        IsConnected = true;
        Debug.Log("[Matchmaking] Socket connected");

        // BUG FIX: execute any action that was deferred waiting for the socket
        if (_joinQueueOnConnect)
            JoinQueue();
        else if (_createInviteOnConnect)
            CreateInviteLink();
    }

    /// <summary>Called by jslib when online count updates.</summary>
    public void OnOnlineCountReceived(string countStr)
    {
        if (int.TryParse(countStr, out int count))
        {
            UpdateOnlineCountUI(count);
            OnOnlineCountUpdated?.Invoke(count);
        }
    }

    /// <summary>Called by jslib when a match is found.</summary>
    public void OnMatchFoundReceived(string json)
    {
        var match = JsonUtility.FromJson<MatchData>(json);
        CurrentMatch = match;
        IsInQueue    = false;

        Debug.Log($"[Matchmaking] Match found! Room: {match.roomId}, Role: {match.role}");

        SetQueueUI(false);
        if (queueStatusText) queueStatusText.text = $"Found: {match.opponentName}!";

        OnMatchFound?.Invoke(match);
    }

    /// <summary>Called by jslib when opponent disconnects.</summary>
    public void OnOpponentDisconnectedReceived(string msg)
    {
        Debug.Log("[Matchmaking] Opponent disconnected");
        CurrentMatch = null;
        OnOpponentDisconnected?.Invoke(msg);
    }

    /// <summary>
    /// Called by MatchmakingSocket.jslib via SendMessage('GameManager', 'OnCRSUpdate', json).
    /// Received after game_result_ack — server confirms updated win/loss and CRS.
    /// Updates cached player stats so the lobby shows the correct rank on return.
    /// </summary>
    public void OnCRSUpdate(string json)
    {
        try
        {
            var data = JsonUtility.FromJson<CRSUpdateData>(json);
            Debug.Log($"[Matchmaking] CRS update — Wins: {data.wins}, Games: {data.gamesPlayed}, CRS: {data.crs}");

            if (ItchAuthManager.Instance != null)
                ItchAuthManager.Instance.UpdateStats(data.wins, data.gamesPlayed);

            if (LeaderboardManager.Instance != null)
                LeaderboardManager.Instance.RefreshLeaderboard();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Matchmaking] Failed to parse CRS update: " + e.Message);
        }
    }

    // ── Online Count Polling (REST fallback) ───────────────────────────────────
    private IEnumerator PollOnlineCount()
    {
        while (true)
        {
            yield return new WaitForSeconds(10f);

            using var req = UnityWebRequest.Get($"{serverBaseUrl}/lobby/online");
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var data = JsonUtility.FromJson<OnlineCountResponse>(req.downloadHandler.text);
                UpdateOnlineCountUI(data.onlineCount);
            }
        }
    }

    private void UpdateOnlineCountUI(int count)
    {
        if (onlineCountText)
            onlineCountText.text = count == 1 ? "1 player online" : $"{count} players online";
    }

    // ── Editor Simulation ─────────────────────────────────────────────────────
    private IEnumerator SimulateQueueEditor()
    {
        if (queueStatusText) queueStatusText.text = "Searching... (Editor mode)";
        yield return new WaitForSeconds(3f);
        var fakeMatch = new MatchData
        {
            roomId       = "editor_test_room",
            opponentName = "TestOpponent",
            role         = "guesser",
            message      = "Editor simulation match"
        };
        OnMatchFoundReceived(JsonUtility.ToJson(fakeMatch));
    }

    // ── UI Helpers ─────────────────────────────────────────────────────────────
    private void SetQueueUI(bool searching)
    {
        if (findMatchButton)   findMatchButton.gameObject.SetActive(!searching);
        if (cancelQueueButton) cancelQueueButton.gameObject.SetActive(searching);
        if (queueStatusText)
            queueStatusText.text = searching ? "Searching for opponent..." : "";
    }

    private IEnumerator FlashText(TMP_Text tmp, string flash, string original, float duration)
    {
        tmp.text = flash;
        yield return new WaitForSeconds(duration);
        tmp.text = original;
    }

    // ── jslib Imports ──────────────────────────────────────────────────────────
#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SocketConnect(string serverUrl, string token);

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SocketDisconnect();

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SocketJoinQueue();

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SocketLeaveQueue();

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SocketJoinInvite(string code);

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SocketJoinInviteRoom(string code);

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void GetInviteCodeFromURL();

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void CopyToClipboard(string text);
#endif
}

// ── Data Models ───────────────────────────────────────────────────────────────

[Serializable]
public class MatchData
{
    public string roomId;
    public string opponentName;
    public string role;
    public string message;
}

[Serializable] public class OnlineCountResponse    { public int    onlineCount; }
[Serializable] public class InviteResponse         { public string code; public string roomId; public string inviteUrl; }
[Serializable] public class InviteValidateResponse { public bool   valid; public string roomId; public string hostName; }
[Serializable] public class CRSUpdateData          { public int    wins; public int gamesPlayed; public int crs; }
