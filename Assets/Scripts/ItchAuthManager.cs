using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Carrot in a Box — itch.io Auth Manager
/// Works with your DigitalOcean OAuth server (PKCE flow).
///
/// Setup:
///   1. Attach to your GameManager GameObject
///   2. Set Server Base URL in Inspector
///   3. Optionally wire up LoginButton in Inspector
/// </summary>
public class ItchAuthManager : MonoBehaviour
{
    public static ItchAuthManager Instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────────────
    [Header("Server")]
    [SerializeField] private string serverBaseUrl = "https://sea-turtle-app-k7m7l.ondigitalocean.app";

    [Header("UI (optional)")]
    [SerializeField] private Button loginButton;

    // ── Public Player Data ─────────────────────────────────────────────────────
    public bool   IsLoggedIn    { get; private set; }
    public string DisplayName   { get; private set; }
    public string ItchUsername  { get; private set; }
    public int    ItchId        { get; private set; }
    public int    Wins          { get; private set; }
    public int    GamesPlayed   { get; private set; }

    // ── Events ─────────────────────────────────────────────────────────────────
    public event Action<PlayerData> OnLoginSuccess;
    public event Action<string>     OnLoginFailed;
    public event Action             OnLogout;

    // ── Private ────────────────────────────────────────────────────────────────
    private string    _sessionToken;
    private Coroutine _pollCoroutine;

    // ── WebGL JS Bridge ────────────────────────────────────────────────────────
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void OpenAuthWindow(string url);

    [DllImport("__Internal")]
    private static extern void RegisterAuthCallback();
#endif

    // ── Unity Lifecycle ────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

#if UNITY_WEBGL && !UNITY_EDITOR
        RegisterAuthCallback();
#endif
    }

    void Start()
    {
        if (loginButton != null)
            loginButton.onClick.AddListener(StartLogin);

        // Try to restore saved session
        string saved = PlayerPrefs.GetString("ciab_token", "");
        if (!string.IsNullOrEmpty(saved))
        {
            Debug.Log("[Auth] Found saved token, validating...");
            StartCoroutine(ValidateToken(saved));
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Call from your Login button or UI.</summary>
    public void StartLogin()
    {
        if (loginButton != null) loginButton.interactable = false;
        StartCoroutine(BeginOAuthFlow());
    }

    /// <summary>
    /// Called by WebGL jslib (ItchOAuth.jslib) when auth popup sends postMessage.
    /// Also called by HTML title screen via SendMessage('GameManager', 'HandleWebGLAuthMessage', json)
    /// </summary>
    public void HandleWebGLAuthMessage(string json)
    {
        Debug.Log("[Auth] WebGL message received: " + json);
        try
        {
            var msg = JsonUtility.FromJson<WebGLAuthMessage>(json);
            if (msg.type == "AUTH_SUCCESS" && !string.IsNullOrEmpty(msg.token))
            {
                if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);
                StartCoroutine(ValidateToken(msg.token));
            }
            else
            {
                HandleLoginFailed("Auth cancelled or missing token.");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[Auth] Failed to parse WebGL message: " + e.Message);
            HandleLoginFailed("Auth message parse error.");
        }
    }

    public void Logout()
    {
        StartCoroutine(DoLogout());
    }

    /// <summary>
    /// Called by MatchmakingManager.OnCRSUpdate to keep cached stats in sync
    /// so the lobby rank text is correct when the player returns from a game.
    /// </summary>
    public void UpdateStats(int wins, int gamesPlayed)
    {
        Wins        = wins;
        GamesPlayed = gamesPlayed;
        Debug.Log($"[Auth] Stats updated — {wins}W / {gamesPlayed}G");
    }

    // ── OAuth Flow ─────────────────────────────────────────────────────────────

    private IEnumerator BeginOAuthFlow()
    {
        Debug.Log("[Auth] Starting OAuth flow...");

        using var req = UnityWebRequest.Get($"{serverBaseUrl}/auth/login");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            HandleLoginFailed($"Cannot reach auth server: {req.error}");
            yield break;
        }

        var resp = JsonUtility.FromJson<AuthLoginResponse>(req.downloadHandler.text);
        if (string.IsNullOrEmpty(resp.authUrl))
        {
            HandleLoginFailed("Invalid response from auth server.");
            yield break;
        }

        Debug.Log("[Auth] Opening auth URL: " + resp.authUrl);

#if UNITY_WEBGL && !UNITY_EDITOR
        OpenAuthWindow(resp.authUrl);
#else
        Application.OpenURL(resp.authUrl);
        if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);
        _pollCoroutine = StartCoroutine(PollForToken());
#endif
    }

    /// <summary>Desktop only: polls server every 2s until token appears (2 min timeout).</summary>
    private IEnumerator PollForToken()
    {
        float elapsed = 0f;
        const float timeout  = 120f;
        const float interval = 2f;

        Debug.Log("[Auth] Polling for token...");

        while (elapsed < timeout)
        {
            yield return new WaitForSeconds(interval);
            elapsed += interval;

            using var req = UnityWebRequest.Get($"{serverBaseUrl}/auth/poll");
            req.SetRequestHeader("credentials", "include");
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var poll = JsonUtility.FromJson<AuthPollResponse>(req.downloadHandler.text);
                if (poll.ready && !string.IsNullOrEmpty(poll.token))
                {
                    Debug.Log("[Auth] Poll succeeded, validating token...");
                    yield return ValidateToken(poll.token);
                    yield break;
                }
            }
        }

        HandleLoginFailed("Login timed out. Please try again.");
    }

    private IEnumerator ValidateToken(string token)
    {
        Debug.Log("[Auth] Validating token...");

        string body = JsonUtility.ToJson(new ValidateRequest { token = token });
        using var req = new UnityWebRequest($"{serverBaseUrl}/auth/validate", "POST");
        req.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            HandleLoginFailed($"Validation failed: {req.error}");
            yield break;
        }

        var data = JsonUtility.FromJson<PlayerData>(req.downloadHandler.text);
        if (!data.success)
        {
            PlayerPrefs.DeleteKey("ciab_token");
            HandleLoginFailed(data.error ?? "Login failed.");
            yield break;
        }

        // Save token and update state
        _sessionToken = token;
        PlayerPrefs.SetString("ciab_token", token);
        PlayerPrefs.Save();

        IsLoggedIn   = true;
        ItchId       = data.itchId;
        ItchUsername = data.itchUsername;
        DisplayName  = data.displayName;
        Wins         = data.wins;
        GamesPlayed  = data.gamesPlayed;

        Debug.Log($"[Auth] Logged in as {DisplayName} (itch: {ItchUsername})");

        if (loginButton != null) loginButton.gameObject.SetActive(false);

        if (MatchmakingManager.Instance != null)
            MatchmakingManager.Instance.ShowLobby();

        OnLoginSuccess?.Invoke(data);
    }

    private void HandleLoginFailed(string error)
    {
        Debug.LogError("[Auth] Login failed: " + error);
        IsLoggedIn = false;
        if (loginButton != null) loginButton.interactable = true;
        OnLoginFailed?.Invoke(error);
    }

    private IEnumerator DoLogout()
    {
        using var req = UnityWebRequest.PostWwwForm($"{serverBaseUrl}/auth/logout", "");
        yield return req.SendWebRequest();

        _sessionToken = null;
        IsLoggedIn    = false;
        DisplayName   = null;
        ItchUsername  = null;
        PlayerPrefs.DeleteKey("ciab_token");
        PlayerPrefs.Save();

        if (loginButton != null)
        {
            loginButton.gameObject.SetActive(true);
            loginButton.interactable = true;
        }

        OnLogout?.Invoke();
        Debug.Log("[Auth] Logged out.");
    }
}

// ── Data Models ───────────────────────────────────────────────────────────────

[Serializable]
public class PlayerData
{
    public bool   success;
    public string error;
    public int    itchId;
    public string itchUsername;
    public string displayName;
    public int    wins;
    public int    gamesPlayed;
    public string WinRateString =>
        gamesPlayed > 0 ? $"{(float)wins / gamesPlayed * 100f:F1}%" : "No games yet";
}

[Serializable] public class AuthLoginResponse { public string authUrl; public string state; }
[Serializable] public class AuthPollResponse  { public bool ready; public string token; }
[Serializable] public class ValidateRequest   { public string token; }
[Serializable] public class WebGLAuthMessage  { public string type; public string token; }
