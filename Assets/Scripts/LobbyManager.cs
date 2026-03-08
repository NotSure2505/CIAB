using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Carrot in a Box — Lobby Manager
/// The HTML title screen handles login. By the time Unity loads,
/// the player is already authenticated. This script populates the
/// lobby UI and connects matchmaking.
/// </summary>
public class LobbyManager : MonoBehaviour
{
    [Header("Player Info")]
    [SerializeField] private TextMeshProUGUI playerNameText;  // "Welcome, NotSure2505!"
    [SerializeField] private TextMeshProUGUI playerRankText;  // "Seedling · CRS 42"

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    void Start()
    {
        // Listen for match events
        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnMatchFound           += HandleMatchFound;
            MatchmakingManager.Instance.OnOpponentDisconnected += HandleOpponentDisconnected;
        }

        // If already logged in (token restored from PlayerPrefs), populate now.
        // Otherwise wait for OnLoginSuccess fired by ItchAuthManager.
        if (ItchAuthManager.Instance != null)
        {
            ItchAuthManager.Instance.OnLoginSuccess += HandleLoginSuccess;

            if (ItchAuthManager.Instance.IsLoggedIn)
            {
                PopulateLobbyUI(
                    ItchAuthManager.Instance.DisplayName,
                    ItchAuthManager.Instance.Wins,
                    ItchAuthManager.Instance.GamesPlayed
                );
            }
        }
        else
        {
            Debug.LogWarning("[Lobby] ItchAuthManager not found on GameManager.");
        }
    }

    void OnDestroy()
    {
        if (ItchAuthManager.Instance != null)
            ItchAuthManager.Instance.OnLoginSuccess -= HandleLoginSuccess;

        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnMatchFound           -= HandleMatchFound;
            MatchmakingManager.Instance.OnOpponentDisconnected -= HandleOpponentDisconnected;
        }
    }

    // ── Login Success ──────────────────────────────────────────────────────────

    private void HandleLoginSuccess(PlayerData data)
    {
        PopulateLobbyUI(data.displayName, data.wins, data.gamesPlayed);
    }

    private void PopulateLobbyUI(string displayName, int wins, int gamesPlayed)
    {
        if (playerNameText)
            playerNameText.text = $"Welcome, {displayName}!";

        int crs  = LeaderboardManager.CalcCRS(wins, gamesPlayed);
        var rank = LeaderboardManager.GetRank(crs);
        if (playerRankText)
        {
            playerRankText.text  = $"{rank.name} · CRS {crs}";
            playerRankText.color = rank.color;
        }

        if (LeaderboardManager.Instance != null)
        {
            LeaderboardManager.Instance.RefreshLeaderboard();
            LeaderboardManager.Instance.RefreshPlayerCRS();
        }

        if (MatchmakingManager.Instance != null)
            MatchmakingManager.Instance.ShowLobby();
    }

    // ── Match Events ───────────────────────────────────────────────────────────

    private void HandleMatchFound(MatchData match)
    {
        Debug.Log($"[Lobby] Match found! Opponent: {match.opponentName}, Role: {match.role}");
        // TODO: Load game scene when ready
        // SceneManager.LoadScene("GameScene");
    }

    private void HandleOpponentDisconnected(string reason)
    {
        Debug.Log("[Lobby] Opponent disconnected — back to lobby");
        if (MatchmakingManager.Instance != null)
            MatchmakingManager.Instance.ShowLobby();
    }
}
