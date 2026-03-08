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
    [SerializeField] private TMP_Text   roleText;           // "You are the PEEKER" / "GUESSER"
    [SerializeField] private TMP_Text   opponentNameText;
    [SerializeField] private TMP_Text   roleDescriptionText;

    [Header("Peek Phase")]
    [SerializeField] private GameObject peekPanel;          // only visible to peeker
    [SerializeField] private TMP_Text   peekRevealText;     // "🥕 CARROT!" or "📦 EMPTY"
    [SerializeField] private Animator   boxOpenAnimator;    // plays box-open animation

    [Header("Deliberation Phase")]
    [SerializeField] private TMP_Text   deliberateTimerText;
    [SerializeField] private TMP_Text   deliberateHintText;  // tip for peeker/guesser
    [SerializeField] private Slider     timerSlider;
    [SerializeField] private float      deliberationSeconds = 30f;

    [Header("Decision Phase (Guesser only)")]
    [SerializeField] private GameObject decisionPanel;
    [SerializeField] private Button     btnSwap;
    [SerializeField] private Button     btnKeep;
    [SerializeField] private TMP_Text   decisionPromptText;
    [SerializeField] private TMP_Text   guesserWaitingText; // shown to peeker while guesser decides

    [Header("Reveal Phase")]
    [SerializeField] private GameObject myBoxReveal;
    [SerializeField] private GameObject opponentBoxReveal;
    [SerializeField] private TMP_Text   myBoxContentsText;
    [SerializeField] private TMP_Text   opponentBoxContentsText;
    [SerializeField] private Animator   myBoxAnimator;
    [SerializeField] private Animator   opponentBoxAnimator;

    [Header("Result Phase")]
    [SerializeField] private TMP_Text   resultHeadlineText; // "YOU WIN!" / "YOU LOSE"
    [SerializeField] private TMP_Text   resultDetailText;
    [SerializeField] private Button     btnPlayAgain;
    [SerializeField] private Button     btnBackToLobby;
    [SerializeField] private TMP_Text   newCRSText;

    [Header("Shared HUD")]
    [SerializeField] private TMP_Text   myNameText;
    [SerializeField] private TMP_Text   oppNameText;
    [SerializeField] private TMP_Text   phaseLabel;         // phase banner
    [SerializeField] private GameObject myBoxSprite;
    [SerializeField] private GameObject opponentBoxSprite;

    // ── State ──────────────────────────────────────────────────────────────────
    public enum GamePhase { None, Setup, Peek, Deliberate, Decision, Reveal, Result }
    public GamePhase CurrentPhase { get; private set; } = GamePhase.None;

    private PlayerRole _myRole;
    private bool       _hasCarrot;        // what's in MY box (only meaningful after peek for peeker)
    private bool       _swapped;          // did the guesser swap?
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
        if (btnSwap)      btnSwap.onClick.AddListener(() => OnGuesserDecision(true));
        if (btnKeep)      btnKeep.onClick.AddListener(() => OnGuesserDecision(false));
        if (btnPlayAgain) btnPlayAgain.onClick.AddListener(OnPlayAgain);
        if (btnBackToLobby) btnBackToLobby.onClick.AddListener(OnBackToLobby);

        ShowPhasePanel(null);

        // Subscribe to matchmaking
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

        // Start VOIP — peeker is the WebRTC initiator
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

        if (myNameText)       myNameText.text    = ItchAuthManager.Instance?.DisplayName ?? "You";
        if (oppNameText)      oppNameText.text    = _opponentName;
        if (opponentNameText) opponentNameText.text = _opponentName;

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

        // Auto-advance to peek phase after a short countdown
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

            // Peeker sees contents for 3 seconds, then deliberation starts
            StartCoroutine(DelayThen(4f, EnterDeliberationPhase));
        }
        else
        {
            // Guesser waits — server will fire the transition
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

        EnterDecisionPhase();
    }

    // ── PHASE: DECISION ───────────────────────────────────────────────────────
    private void EnterDecisionPhase()
    {
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
            // Peeker waits
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

        // Recalculate: if swapped, my contents flip
        if (swap) _hasCarrot = !_hasCarrot;

        Debug.Log($"[Game] Guesser decision: {(swap ? "SWAP" : "KEEP")}");

        // Notify server
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
        _hasCarrot = data.myCarrot;
        EnterRevealPhase(data.myCarrot, data.opponentCarrot, data.swapped);
    }

    private void EnterRevealPhase(bool myCarrot, bool opponentCarrot, bool swapped)
    {
        CurrentPhase = GamePhase.Reveal;
        ShowPhasePanel(panelReveal);
        SetPhaseLabel("REVEAL!");

        StartCoroutine(DoRevealSequence(myCarrot, opponentCarrot, swapped));
    }

    private IEnumerator DoRevealSequence(bool myCarrot, bool opponentCarrot, bool swapped)
    {
        // Dramatic pause
        yield return new WaitForSeconds(0.8f);

        // Open my box
        if (myBoxAnimator) myBoxAnimator.SetTrigger("Open");
        yield return new WaitForSeconds(0.5f);
        if (myBoxContentsText)
            myBoxContentsText.text = myCarrot ? "🥕" : "📦";

        yield return new WaitForSeconds(0.8f);

        // Open opponent's box
        if (opponentBoxAnimator) opponentBoxAnimator.SetTrigger("Open");
        yield return new WaitForSeconds(0.5f);
        if (opponentBoxContentsText)
            opponentBoxContentsText.text = opponentCarrot ? "🥕" : "📦";

        yield return new WaitForSeconds(1.2f);

        // Determine winner: the player with the carrot wins
        bool iWin = myCarrot;
        EnterResultPhase(iWin, myCarrot, opponentCarrot, swapped);
    }

    // ── PHASE: RESULT ─────────────────────────────────────────────────────────

    /// <summary>
    /// Called by server socket event "game_result".
    /// Server sends: { winner: "peeker"|"guesser", myCarrot: bool, opponentCarrot: bool, newCRS: int }
    /// </summary>
    public void OnServerResult(string json)
    {
        var data   = JsonUtility.FromJson<ResultData>(json);
        bool iWin  = data.winner == (_myRole == PlayerRole.Peeker ? "peeker" : "guesser");
        EnterResultPhase(iWin, data.myCarrot, data.opponentCarrot, _swapped);

        if (newCRSText)
            newCRSText.text = $"Your new CRS: {data.newCRS}";
    }

    private void EnterResultPhase(bool iWin, bool myCarrot, bool opponentCarrot, bool swapped)
    {
        CurrentPhase = GamePhase.Result;
        ShowPhasePanel(panelResult);

        string swapNote = swapped ? "(boxes were swapped)" : "(boxes were kept)";

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

        // Stop VOIP
        if (VoipManager.Instance != null)
            VoipManager.Instance.StopVoip();

        // Report result to server for CRS update
        SendGameEvent("game_result_ack", $"{{\"won\":{(iWin ? "true" : "false")}}}");
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
            case "game_peek_ready":      OnServerPeekReady(env.payload);      break;
            case "game_deliberate_start":OnServerDeliberateStart(env.payload); break;
            case "game_reveal":          OnServerReveal(env.payload);          break;
            case "game_result":          OnServerResult(env.payload);          break;
            case "voip_signal":
                if (VoipManager.Instance != null)
                    VoipManager.Instance.HandleVoipSignal(env.payload);
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
[Serializable] public class ResultData        { public string winner; public bool myCarrot; public bool opponentCarrot; public int newCRS; }
