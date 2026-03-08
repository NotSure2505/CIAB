using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Carrot in a Box — Leaderboard Manager
/// Fetches CRS leaderboard from server and populates UI.
///
/// Scene Setup:
///   - Attach to any persistent GameObject (e.g. GameManager)
///   - Assign leaderboardContent (ScrollRect content Transform)
///   - Assign rowPrefab (see LeaderboardRow prefab setup below)
///   - Assign playerCRSText (TMP showing current player's rank)
///   - Call RefreshLeaderboard() from your lobby UI open event
/// </summary>
public class LeaderboardManager : MonoBehaviour
{
    public static LeaderboardManager Instance { get; private set; }

    [Header("Server")]
    [SerializeField] private string serverBaseUrl = "https://sea-turtle-app-k7m7l.ondigitalocean.app";

    [Header("UI References")]
    [SerializeField] private Transform     leaderboardContent;   // ScrollRect → Viewport → Content
    [SerializeField] private GameObject    rowPrefab;            // LeaderboardRow prefab
    [SerializeField] private TMP_Text      playerCRSText;        // Shows "You: 🐇 Digger · CRS 312"
    [SerializeField] private TMP_Text      statusText;           // "Loading..." / "Updated just now"
    [SerializeField] private float         autoRefreshSeconds = 30f;

    private float _refreshTimer;
    private bool  _isFetching;

    // ── CRS helpers (mirrors server logic) ────────────────────────────────────
    private const int CRS_K = 10;

    public static int CalcCRS(int wins, int gamesPlayed)
    {
        if (gamesPlayed == 0) return 0;
        return Mathf.RoundToInt((float)wins / (gamesPlayed + CRS_K) * 1000f);
    }

    public static CarrotRankInfo GetRank(int crs)
    {
        if (crs >= 900) return new CarrotRankInfo("Carrot Legend",  "👑",     6, new Color(1f, 0.84f, 0f));
        if (crs >= 750) return new CarrotRankInfo("Carrot Master",  "🥕🥕🥕", 5, new Color(0.91f, 0.36f, 0.13f));
        if (crs >= 600) return new CarrotRankInfo("Carrot Hoarder", "🥕🥕",   4, new Color(0.91f, 0.36f, 0.13f));
        if (crs >= 450) return new CarrotRankInfo("Carrot Finder",  "🥕",     3, new Color(0.91f, 0.36f, 0.13f));
        if (crs >= 250) return new CarrotRankInfo("Digger",         "🐇",     2, new Color(0.68f, 0.85f, 0.48f));
        if (crs >= 100) return new CarrotRankInfo("Sprout",         "🌿",     1, new Color(0.48f, 0.72f, 0.34f));
        return           new CarrotRankInfo("Seedling",             "🌱",     0, new Color(0.6f, 0.8f, 0.4f));
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        RefreshPlayerCRS();
        RefreshLeaderboard();
    }

    void Update()
    {
        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer <= 0f && !_isFetching)
        {
            _refreshTimer = autoRefreshSeconds;
            RefreshLeaderboard();
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void RefreshLeaderboard()
    {
        StartCoroutine(FetchLeaderboard());
    }

    public void RefreshPlayerCRS()
    {
        string token = PlayerPrefs.GetString("ciab_token", "");
        if (!string.IsNullOrEmpty(token))
            StartCoroutine(FetchPlayerCRS(token));
    }

    // ── Fetch Leaderboard ──────────────────────────────────────────────────────
    private IEnumerator FetchLeaderboard()
    {
        _isFetching = true;
        if (statusText) statusText.text = "Refreshing...";

        using var req = UnityWebRequest.Get($"{serverBaseUrl}/leaderboard");
        yield return req.SendWebRequest();

        _isFetching = false;

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning("[Leaderboard] Fetch failed: " + req.error);
            if (statusText) statusText.text = "Could not load leaderboard.";
            yield break;
        }

        var response = JsonUtility.FromJson<LeaderboardResponse>(req.downloadHandler.text);
        PopulateRows(response.leaderboard);

        if (statusText) statusText.text = $"Updated {DateTime.Now:h:mm tt}";
    }

    // ── Fetch Single Player CRS ────────────────────────────────────────────────
    private IEnumerator FetchPlayerCRS(string token)
    {
        using var req = new UnityWebRequest($"{serverBaseUrl}/player/crs", "GET");
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Authorization", $"Bearer {token}");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success) yield break;

        var data     = JsonUtility.FromJson<LeaderboardEntry>(req.downloadHandler.text);
        var rankInfo = GetRank(data.crs);

        if (playerCRSText)
        {
            playerCRSText.text = $"You · {rankInfo.emoji} <b>{rankInfo.name}</b> · CRS {data.crs}";
            playerCRSText.color = rankInfo.color;
        }
    }

    // ── Populate Rows ──────────────────────────────────────────────────────────
    private void PopulateRows(LeaderboardEntry[] entries)
    {
        if (leaderboardContent == null || rowPrefab == null)
        {
            Debug.LogWarning("[Leaderboard] Missing leaderboardContent or rowPrefab reference.");
            return;
        }

        // Clear existing rows
        foreach (Transform child in leaderboardContent)
            Destroy(child.gameObject);

        if (entries == null || entries.Length == 0)
        {
            var empty = Instantiate(rowPrefab, leaderboardContent);
            var t = empty.GetComponentInChildren<TMP_Text>();
            if (t) t.text = "No players yet — be the first!";
            return;
        }

        string localName = PlayerPrefs.GetString("ciab_displayName", "");

        for (int i = 0; i < entries.Length; i++)
        {
            var entry    = entries[i];
            var row      = Instantiate(rowPrefab, leaderboardContent);
            var rankInfo = GetRank(entry.crs);
            bool isYou   = entry.displayName == localName;

            // The prefab should have child TMP objects with these names:
            SetText(row, "RankNumber",  $"#{i + 1}");
            SetText(row, "RankEmoji",   rankInfo.emoji);
            SetText(row, "PlayerName",  isYou ? $"{entry.displayName} (You)" : entry.displayName);
            SetText(row, "RankName",    rankInfo.name);
            SetText(row, "CRSScore",    $"{entry.crs}");
            SetText(row, "WinLoss",     $"{entry.wins}W / {entry.losses}L");
            SetText(row, "WinRate",     $"{Mathf.RoundToInt(entry.winRate * 100)}%");

            // Highlight your own row
            if (isYou)
            {
                var bg = row.GetComponent<Image>();
                if (bg) bg.color = new Color(0.91f, 0.36f, 0.13f, 0.15f);
            }

            // Color rank name by tier
            var rankNameText = row.transform.Find("RankName")?.GetComponent<TMP_Text>();
            if (rankNameText) rankNameText.color = rankInfo.color;
        }
    }

    private void SetText(GameObject row, string childName, string value)
    {
        var child = row.transform.Find(childName);
        if (child == null) return;
        var tmp = child.GetComponent<TMP_Text>();
        if (tmp) tmp.text = value;
    }
}

// ── Data Models ───────────────────────────────────────────────────────────────

[Serializable]
public class CarrotRankInfo
{
    public string name;
    public string emoji;
    public int    tier;
    public Color  color;

    public CarrotRankInfo(string name, string emoji, int tier, Color color)
    {
        this.name  = name;
        this.emoji = emoji;
        this.tier  = tier;
        this.color = color;
    }
}

[Serializable]
public class LeaderboardEntry
{
    public string displayName;
    public int    wins;
    public int    gamesPlayed;
    public int    losses;
    public float  winRate;
    public int    crs;
    public string rank;
    public string rankEmoji;
    public int    tier;
}

[Serializable]
public class LeaderboardResponse
{
    public LeaderboardEntry[] leaderboard;
}
