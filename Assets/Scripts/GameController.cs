using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Carrot in a Box — Game Controller
///
/// Round flow:
///   1. SETUP      — Roles assigned by server (peeker / guesser). Carrot placed randomly.
///   2. PEEK       — Peeker privately sees their box contents (carrot or empty).
///   3. DELIBERATE — Both players talk via VOIP. Peeker tries to convince guesser
///                   to swap (or not). Timer counts down.
///   4. DECISION   — Guesser decides: SWAP boxes or KEEP their box.
///   5. REVEAL     — Both boxes open. Winner determined.
///   6. RESULT     — Scores updated, play-again offered.
///
/// The server controls authoritative state; this script drives the client UI.
/// All game events arrive via Socket.io → MatchmakingSocket.jslib → here.
/// </summary>
public class GameController : MonoBehaviour
{
    public static GameController Instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────────────
    [Header("Phase Panels")]
    [SerializeField] private GameObject panelSetup;
    [SerializeField] private GameObject panelPeek;
    [SerializeField] private GameObject panelDeliberate;
    [SerializeField] private GameObject panelDecision;
    [SerializeField] private GameObject panelReveal;
    [SerializeField] private GameObject panelResult;

    [Header("Setup Phase")]
    [SerializeField] private TMP_Text   roleText;
    [SerializeField] private TMP_Text   opponentNameText;
    [SerializeField] private TMP_Text   roleDescriptionText;

    [Header("Peek Phase")]
    [SerializeField] private GameObject peekPanel;
    [SerializeField] private TMP_Text   peekRevealText;
    [SerializeField] private Animator   boxOpenAnimator;

    [Header("Deliberation Phase")]
    [SerializeField] private TMP_Text   deliberateTimerText;
    [SerializeField] private TMP_Text   deliberateHintText;
    [SerializeField] private Slider     timerSlider;
    [SerializeField] private float      deliberationSeconds = 30f;

    [Header("Decision Phase (Guesser only)")]
    [SerializeField] private GameObject decisionPanel;
    [SerializeField] private Button     btnSwap;
    [SerializeField] private Button     btnKeep;
    [SerializeField] private TMP_Text   decisionPromptText;
    [SerializeField] private TMP_Text   guesserWaitingText;

    [Header("Reveal Phase")]
    [SerializeField] private GameObject myBoxReveal;
    [SerializeField] private GameObject opponentBoxReveal;
    [SerializeField] private TMP_Text   myBoxContentsText;
    [SerializeField] private TMP_Text   opponentBoxContentsText;
    [SerializeField] private Animator   myBoxAnimator;
    [SerializeField] private Animator   opponentBoxAnimator;

    [Header("Result Phase")]
    [SerializeField] private TMP_Text   resultHeadlineText;
    [SerializeField] private TMP_Text   resultDetailText;
    [SerializeField] private Button     btnPlayAgain;
    [SerializeField] private Button     btnBackToLobby;
    [SerializeField] private TMP_Text   newCRSText;

    [Header("Shared HUD")]
    [SerializeField] private TMP_Text   myNameText;
    [SerializeField] private TMP_Text   oppNameText;
    [SerializeField] private TMP_Text   phaseLabel;
    [SerializeField] private GameObject myBoxSprite;
    [SerializeField] private GameObject opponentBoxSprite;

    // ── State ──────────────────────────────────────────────────────────────────
    public enum GamePhase { None, Setup, Peek, Deliberate, Decision, Reveal, Result }
    public GamePhase CurrentPhase { get; private set; } = GamePhase.None;

    private PlayerRole _myRole;
    private bool       _hasCarrot;
    private bool       _swapped;
    private string     _opponentName;
    private string     _roomId;
    private Coroutine  _timerCoroutine;

    public enum PlayerRole { Peeker, Guesser }

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        if (btnSwap)        btnSwap.onClick.AddListener(() => OnGuesserDecision(true));
        if (btnKeep)        btnKeep.onClick.AddListener(() => OnGuesserDecision(false));
        if (btnPlayAgain)   btnPlayAgain.onClick.AddListener(OnPlayAgain);
        if (btnBackToLobby) btnBackToLobby.onClick.AddListener(OnBackToLobby);

        ShowPhasePanel(null);

        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnMatchFound           += HandleMatchFound;
            MatchmakingManager.Instance.OnOpponentDisconnected += HandleOpponentDisconnected;
        }
    }

    void OnDestroy()
    {
        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnMatchFound           -= HandleMatchFound;
            MatchmakingManager.Instance.OnOpponentDisconnected -= HandleOpponentDisconnected;
        }
    }

    // ── Match Found → Setup ───────────────────────────────────────────────────
    private void HandleMatchFound(MatchData match)
    {
        _roomId       = match.roomId;
        _opponentName = match.opponentName;
        _myRole       = match.role == "peeker" ? PlayerRole.Peeker : PlayerRole.Guesser;

        Debug.Log($"[Game] Match found! Role: {_myRole}, Opponent: {_opponentName}");

        if (VoipManager.Instance != null)
            VoipManager.Instance.StartVoip(match.roomId, _myRole == PlayerRole.Peeker);

        EnterSetupPhase();
    }

    // ── PHASE: SETUP ──────────────────────────────────────────────────────────
    private void EnterSetupPhase()
    {
        CurrentPhase = GamePhase.Setup;
        ShowPhasePanel(panelSetup);
        SetPhaseLabel("GAME START");

        if (myNameText)       myNameText.text       = ItchAuthManager.Instance?.DisplayName ?? "You";
        if (oppNameText)      oppNameText.text       = _opponentName;
        if (opponentNameText) opponentNameText.text  = _opponentName;

        if (_myRole == PlayerRole.Peeker)
        {
            if (roleText)            roleText.text = "🔍 YOU ARE THE PEEKER";
            if (roleDescriptionText) roleDescriptionText.text =
                "You get to peek inside your box.\nConvince your opponent to swap — or not.";
        }
        else
        {
            if (roleText)            roleText.text = "❓ YOU ARE THE GUESSER";
            if (roleDescriptionText) roleDescriptionText.text =
                "You cannot see your box.\nListen carefully — then decide whether to swap.";
        }

        StartCoroutine(DelayThen(3f, EnterPeekPhase));
    }

    // ── PHASE: PEEK ───────────────────────────────────────────────────────────

    /// <summary>
    /// Called by Socket.io event "game_peek_ready" from server.
    /// Server sends: { hasCarrot: bool }
    /// </summary>
    public void OnServerPeekReady(string json)
    {
        var data = JsonUtility.FromJson<PeekReadyData>(json);
        _hasCarrot = data.hasCarrot;
        EnterPeekPhase();
    }

    private void EnterPeekPhase()
    {
        CurrentPhase = GamePhase.Peek;
        ShowPhasePanel(panelPeek);
        SetPhaseLabel("PEEK PHASE");

        if (_myRole == PlayerRole.Peeker)
        {
            if (peekPanel) peekPanel.SetActive(true);
            if (boxOpenAnimator) boxOpenAnimator.SetTrigger("Open");

            StartCoroutine(DelayThen(0.6f, () =>
            {
                if (peekRevealText)
                {
                    peekRevealText.text = _hasCarrot
                        ? "<color=#e85d20>🥕 CARROT!</color>"
                        : "<color=#aaaaaa>📦 EMPTY</color>";
                }
            }));

            StartCoroutine(DelayThen(4f, EnterDeliberationPhase));
        }
        else
        {
            if (peekPanel) peekPanel.SetActive(false);
            SetPhaseLabel("OPPONENT IS PEEKING...");
        }
    }

    // ── PHASE: DELIBERATE ─────────────────────────────────────────────────────

    /// <summary>Called by server socket event "game_deliberate_start".</summary>
    public void OnServerDeliberateStart(string json)
    {
        EnterDeliberationPhase();
    }

    private void EnterDeliberationPhase()
    {
        // Guard: don't re-enter if already in or past this phase
        if (CurrentPhase == GamePhase.Deliberate ||
            CurrentPhase == GamePhase.Decision   ||
            CurrentPhase == GamePhase.Reveal     ||
            CurrentPhase == GamePhase.Result) return;

        CurrentPhase = GamePhase.Deliberate;
        ShowPhasePanel(panelDeliberate);
        SetPhaseLabel("DELIBERATION");

        if (deliberateHintText)
        {
            deliberateHintText.text = _myRole == PlayerRole.Peeker
                ? "Talk to your opponent. Convince them — truthfully or not."
                : "Listen carefully. Do you trust them?";
        }

        if (_timerCoroutine != null) StopCoroutine(_timerCoroutine);
        _timerCoroutine = StartCoroutine(RunDeliberationTimer());
    }

    private IEnumerator RunDeliberationTimer()
    {
        float remaining = deliberationSeconds;
        if (timerSlider) timerSlider.maxValue = deliberationSeconds;

        while (remaining > 0f)
        {
            remaining -= Time.deltaTime;
            if (deliberateTimerText)
                deliberateTimerText.text = Mathf.CeilToInt(remaining).ToString();
            if (timerSlider)
                timerSlider.value = remaining;
            yield return null;
        }

        // Only the guesser's client needs to act; server drives authoritative decision phase
        // The server will fire game_decision_start for both clients anyway
    }

    // ── PHASE: DECISION ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by server socket event "game_decision_start".
    /// BUG FIX: this event was received but never handled — the switch statement
    /// in OnGameEvent was missing this case entirely, so the decision phase
    /// would never appear on either client.
    /// </summary>
    public void OnServerDecisionStart(string json)
    {
        EnterDecisionPhase();
    }

    private void EnterDecisionPhase()
    {
        if (_timerCoroutine != null)
        {
            StopCoroutine(_timerCoroutine);
            _timerCoroutine = null;
        }

        CurrentPhase = GamePhase.Decision;
        ShowPhasePanel(panelDecision);
        SetPhaseLabel("MAKE YOUR CHOICE");

        if (_myRole == PlayerRole.Guesser)
        {
            if (decisionPanel) decisionPanel.SetActive(true);
            if (guesserWaitingText) guesserWaitingText.gameObject.SetActive(false);
            if (decisionPromptText)
                decisionPromptText.text = "Do you want to SWAP boxes?";
        }
        else
        {
            if (decisionPanel) decisionPanel.SetActive(false);
            if (guesserWaitingText)
            {
                guesserWaitingText.gameObject.SetActive(true);
                guesserWaitingText.text = "Waiting for opponent's decision...";
            }
        }
    }

    private void OnGuesserDecision(bool swap)
    {
        _swapped = swap;
        if (decisionPanel) decisionPanel.SetActive(false);

        // BUG FIX: do NOT flip _hasCarrot locally here. The server sends the
        // authoritative final state in the game_reveal event. Flipping locally
        // before that arrives can cause the reveal to show wrong contents if
        // the server's timeout fires a different result than the client assumed.

        Debug.Log($"[Game] Guesser decision: {(swap ? "SWAP" : "KEEP")}");

        SendGameEvent("game_decision", $"{{\"swap\":{(swap ? "true" : "false")}}}");

        SetPhaseLabel("WAITING FOR REVEAL...");
    }

    // ── PHASE: REVEAL ─────────────────────────────────────────────────────────

    /// <summary>
    /// Called by server socket event "game_reveal".
    /// Server sends: { myCarrot: bool, opponentCarrot: bool, swapped: bool }
    /// </summary>
    public void OnServerReveal(string json)
    {
        var data = JsonUtility.FromJson<RevealData>(json);
        // Use server-authoritative values, not locally computed ones
        _hasCarrot = data.myCarrot;
        _swapped   = data.swapped;
        EnterRevealPhase(data.myCarrot, data.opponentCarrot, data.swapped);
    }

    private void EnterRevealPhase(bool myCarrot, bool opponentCarrot, bool swapped)
    {
        CurrentPhase = GamePhase.Reveal;
        ShowPhasePanel(panelReveal);
        SetPhaseLabel("REVEAL!");

        if (myBoxContentsText)
            myBoxContentsText.text = myCarrot
                ? "<color=#e85d20>🥕 CARROT!</color>"
                : "<color=#aaaaaa>📦 EMPTY</color>";

        if (opponentBoxContentsText)
            opponentBoxContentsText.text = opponentCarrot
                ? "<color=#e85d20>🥕 CARROT!</color>"
                : "<color=#aaaaaa>📦 EMPTY</color>";

        if (myBoxAnimator)       myBoxAnimator.SetTrigger("Open");
        if (opponentBoxAnimator) opponentBoxAnimator.SetTrigger("Open");
    }

    // ── PHASE: RESULT ─────────────────────────────────────────────────────────

    /// <summary>Called by server socket event "game_result".</summary>
    public void OnServerResult(string json)
    {
        var data = JsonUtility.FromJson<ResultData>(json);

        bool iWin     = data.myCarrot;
        string swapNote = _swapped ? "The boxes were swapped." : "The boxes were not swapped.";

        if (newCRSText)
            newCRSText.text = $"New CRS: {data.newCRS}";

        if (resultHeadlineText)
        {
            resultHeadlineText.text  = iWin ? "🥕 YOU WIN!" : "📦 YOU LOSE";
            resultHeadlineText.color = iWin
                ? new Color(0.91f, 0.36f, 0.13f)
                : new Color(0.6f, 0.6f, 0.6f);
        }

        if (resultDetailText)
        {
            string detail = _myRole == PlayerRole.Peeker
                ? (iWin
                    ? $"Your deception worked! {swapNote}"
                    : $"The guesser saw through you! {swapNote}")
                : (iWin
                    ? $"You made the right call! {swapNote}"
                    : $"You were deceived! {swapNote}");
            resultDetailText.text = detail;
        }

        if (VoipManager.Instance != null)
            VoipManager.Instance.StopVoip();

        SendGameEvent("game_result_ack", $"{{\"won\":{(iWin ? "true" : "false")}}}");

        ShowPhasePanel(panelResult);
        CurrentPhase = GamePhase.Result;
    }

    // ── Button Handlers ───────────────────────────────────────────────────────
    private void OnPlayAgain()
    {
        CurrentPhase = GamePhase.None;
        ShowPhasePanel(null);
        if (MatchmakingManager.Instance != null)
            MatchmakingManager.Instance.ShowLobby();
    }

    private void OnBackToLobby()
    {
        OnPlayAgain();
    }

    // ── Opponent Disconnected ─────────────────────────────────────────────────
    private void HandleOpponentDisconnected(string reason)
    {
        if (CurrentPhase == GamePhase.None || CurrentPhase == GamePhase.Result) return;

        if (_timerCoroutine != null) StopCoroutine(_timerCoroutine);

        if (VoipManager.Instance != null) VoipManager.Instance.StopVoip();

        if (resultHeadlineText) resultHeadlineText.text = "Opponent disconnected";
        if (resultDetailText)   resultDetailText.text   = "The game has ended early.";
        ShowPhasePanel(panelResult);
        CurrentPhase = GamePhase.Result;
    }

    // ── Socket Event Dispatch ─────────────────────────────────────────────────
    /// <summary>
    /// Entry point called by MatchmakingSocket.jslib via SendMessage('GameManager', 'OnGameEvent', json).
    /// </summary>
    public void OnGameEvent(string json)
    {
        GameEventEnvelope env;
        try { env = JsonUtility.FromJson<GameEventEnvelope>(json); }
        catch { Debug.LogWarning("[Game] Failed to parse game event: " + json); return; }

        switch (env.eventType)
        {
            case "game_peek_ready":       OnServerPeekReady(env.payload);      break;
            case "game_deliberate_start": OnServerDeliberateStart(env.payload); break;
            // BUG FIX: this case was completely missing — decision phase never triggered
            case "game_decision_start":   OnServerDecisionStart(env.payload);   break;
            case "game_reveal":           OnServerReveal(env.payload);          break;
            case "game_result":           OnServerResult(env.payload);          break;
            case "voip_signal":
                if (VoipManager.Instance != null)
                    VoipManager.Instance.HandleVoipSignal(env.payload);
                break;
            default:
                Debug.LogWarning($"[Game] Unhandled event type: {env.eventType}");
                break;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private void ShowPhasePanel(GameObject panel)
    {
        GameObject[] panels = {
            panelSetup, panelPeek, panelDeliberate,
            panelDecision, panelReveal, panelResult
        };
        foreach (var p in panels)
            if (p != null) p.SetActive(p == panel);
    }

    private void SetPhaseLabel(string text)
    {
        if (phaseLabel) phaseLabel.text = text;
    }

    private IEnumerator DelayThen(float seconds, Action callback)
    {
        yield return new WaitForSeconds(seconds);
        callback?.Invoke();
    }

    private void SendGameEvent(string eventType, string payloadJson)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        SocketSendGameEvent(eventType, payloadJson);
#else
        Debug.Log($"[Game] (Editor) SendGameEvent: {eventType} → {payloadJson}");
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SocketSendGameEvent(string eventType, string payloadJson);
#endif
}

// ── Data Models ───────────────────────────────────────────────────────────────
[Serializable] public class GameEventEnvelope { public string eventType; public string payload; }
[Serializable] public class PeekReadyData     { public bool hasCarrot; }
[Serializable] public class RevealData        { public bool myCarrot; public bool opponentCarrot; public bool swapped; }
[Serializable] public class ResultData        { public string winner; public bool myCarrot; public bool opponentCarrot; public bool swapped; public int newCRS; }
