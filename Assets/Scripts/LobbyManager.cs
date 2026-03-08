using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Carrot in a Box — Lobby Manager
///
/// Populates the lobby UI after login and hides/shows LobbyPanel
/// when a game starts or ends. MatchmakingPanel is a child of
/// LobbyPanel so it hides/shows automatically with it.
///
/// Inspector setup:
///   - playerNameText → LobbyPanel → PlayerNameText
///   - playerRankText → LobbyPanel → PlayerCRSText
///   - lobbyPanel        → LobbyPanel
/// </summary>
public class LobbyManager : MonoBehaviour
{
    [Header("Player Info")]
    [SerializeField] private TextMeshProUGUI playerNameText;   // LobbyPanel → PlayerNameText
    [SerializeField] private TextMeshProUGUI playerRankText;   // LobbyPanel → PlayerCRSText

    [Header("Panels")]
    [SerializeField] private GameObject lobbyPanel;               // LobbyPanel — hidden on match found

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    void Start()
    {
        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnMatchFound           += HandleMatchFound;
            MatchmakingManager.Instance.OnOpponentDisconnected += HandleOpponentDisconnected;
        }

        if (ItchAuthManager.Instance != null)
        {
            ItchAuthManager.Instance.OnLoginSuccess += HandleLoginSuccess;

            if (ItchAuthManager.Instance.IsLoggedIn)
            {
                PopulateLobbyPanel(
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

        if (lobbyPanel) lobbyPanel.SetActive(true);
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

    // ── Public: called by GameController when returning to lobby ──────────────
    public void ShowLobby()
    {
        if (lobbyPanel) lobbyPanel.SetActive(true);
        if (MatchmakingManager.Instance != null)
            MatchmakingManager.Instance.ShowLobby();
    }

    // ── Login Success ──────────────────────────────────────────────────────────
    private void HandleLoginSuccess(PlayerData data)
    {
        PopulateLobbyPanel(data.displayName, data.wins, data.gamesPlayed);
    }

    private void PopulateLobbyPanel(string displayName, int wins, int gamesPlayed)
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
        if (lobbyPanel) lobbyPanel.SetActive(false);
        // GameController.HandleMatchFound() fires automatically from the same event.
    }

    private void HandleOpponentDisconnected(string reason)
    {
        Debug.Log("[Lobby] Opponent disconnected — back to lobby");
        if (lobbyPanel) lobbyPanel.SetActive(true);
        if (MatchmakingManager.Instance != null)
            MatchmakingManager.Instance.ShowLobby();
    }
}
