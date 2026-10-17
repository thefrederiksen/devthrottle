using CcDirector.ControlApi;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// THE TERMINAL VIEW'S "TAKE THE WHEEL" / "HAND BACK" BUTTON, without the controls (mission #4070, phase 4) - the
/// desktop twin of client-core's <c>TerminalWheelButton</c>. The main window draws its button and its reason line
/// from this and calls <see cref="PressAsync"/> on a click; everything the button decides lives here, so it is tested
/// with fakes and the window only copies the values onto controls.
///
/// IT RENDERS THE GATEWAY'S VERDICT AND NOTHING ELSE (CLAUDE.md rule 7): the button, its words, whether it can be
/// pressed and why not come from <see cref="Session.GatewayWheelAction"/>, which the Gateway stamps down. A terminal
/// session shows nothing because that verdict says there is no button - this class never reads the connection mode or
/// the wheel holder. Pressing it calls the GATEWAY's verb through <see cref="IGatewayWheel"/>, never the session's own
/// switch, so the Gateway's ruling and its refusal sentences reach the desktop exactly as they reach the browser.
/// </summary>
public sealed class TerminalWheelPresenter
{
    private readonly Func<IGatewayWheel?> _wheel;
    private readonly Action<Action> _post;
    private Session? _session;
    private GatewayWheelAction? _shownAction;

    /// <param name="wheel">Reads the Gateway seam at press time, so a Gateway client created after the window is
    /// still the one used. Null when no Gateway is configured.</param>
    /// <param name="post">Runs work on the screen thread. The Gateway's stamp arrives on another thread, and every
    /// change to what the button shows is made on the screen thread so the window never reads half of one.</param>
    public TerminalWheelPresenter(Func<IGatewayWheel?> wheel, Action<Action> post)
    {
        _wheel = wheel ?? throw new ArgumentNullException(nameof(wheel));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>Raised on the screen thread whenever anything the button shows changes.</summary>
    public event Action? Changed;

    /// <summary>The Gateway's button for the attached session, or null when there is none.</summary>
    public GatewayWheelAction? Action => _session?.GatewayWheelAction;

    /// <summary>True when there is a button to draw.</summary>
    public bool IsVisible => Action is not null;

    /// <summary>The button's words, verbatim; empty when there is no button.</summary>
    public string Label => Action?.Label ?? "";

    /// <summary>True while a press is on its way to the Gateway.</summary>
    public bool IsPending { get; private set; }

    /// <summary>True when the button can be pressed now: the Gateway enabled it and no press is on its way.</summary>
    public bool IsEnabled => Action is { Enabled: true } && !IsPending;

    /// <summary>The Gateway's reason the button cannot be pressed, verbatim; null when it can.</summary>
    public string? DisabledReason => Action is { Enabled: false } a ? a.DisabledReason : null;

    /// <summary>The Gateway's sentence for the last refused press, verbatim; null when there is none.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The line beside the button: a refusal wins over the disabled reason, because it is newer.</summary>
    public string? Note => Refusal ?? DisabledReason;

    /// <summary>
    /// Follow this session (null for none). Called by the window whenever the active session changes. A new session
    /// is a new button: any refusal and any pending press of the old one are forgotten.
    /// </summary>
    public void Attach(Session? session)
    {
        if (ReferenceEquals(_session, session)) return;
        FileLog.Write($"[TerminalWheelPresenter] Attach: session={session?.Id.ToString() ?? "(none)"}");
        if (_session is not null) _session.OnGatewayDisplayStateChanged -= OnDisplayStateChanged;
        _session = session;
        _shownAction = session?.GatewayWheelAction;
        IsPending = false;
        Refusal = null;
        if (session is not null) session.OnGatewayDisplayStateChanged += OnDisplayStateChanged;
        RaiseChanged();
    }

    /// <summary>
    /// The button's click. Goes pending before anything is awaited (CLAUDE.md rule 1), calls the Gateway's verb, and
    /// on a refusal keeps the Gateway's sentence in <see cref="Refusal"/>. The click is the entry point, so this is
    /// where a refusal is caught and turned into words on the screen. On success the Gateway has already been told the
    /// new holder (pull request #4094's contract), and its next display-state push turns the button into its other
    /// half through <see cref="Session.OnGatewayDisplayStateChanged"/>.
    /// </summary>
    public async Task PressAsync()
    {
        var session = _session;
        var action = Action;
        if (session is null || action is null || !IsEnabled)
        {
            FileLog.Write($"[TerminalWheelPresenter] PressAsync: ignored (session={session?.Id.ToString() ?? "(none)"}, "
                + $"action={action?.Verb.ToString() ?? "(none)"}, enabled={IsEnabled})");
            return;
        }

        var sid = session.Id.ToString();
        FileLog.Write($"[TerminalWheelPresenter] PressAsync: session={sid}, verb={action.Verb}");
        IsPending = true;
        Refusal = null;
        RaiseChanged();

        string? refusal = null;
        try
        {
            var wheel = _wheel() ?? throw new InvalidOperationException(
                "This Director is not connected to a Gateway, so the wheel cannot be taken or handed back from here.");
            if (action.Verb == WheelVerb.Take)
                await wheel.TakeWheelAsync(sid);
            else
                await wheel.HandBackWheelAsync(sid);
            FileLog.Write($"[TerminalWheelPresenter] PressAsync: session={sid}, verb={action.Verb} accepted");
        }
        catch (Exception ex)
        {
            refusal = ex.Message;
            FileLog.Write($"[TerminalWheelPresenter] PressAsync FAILED: session={sid}, verb={action.Verb}: {ex.Message}");
        }

        // The window moved to another session while the Gateway answered: this reply is not that session's.
        if (!ReferenceEquals(_session, session)) return;
        IsPending = false;
        Refusal = refusal;
        RaiseChanged();
    }

    private void OnDisplayStateChanged() => _post(ApplyDisplayState);

    private void ApplyDisplayState()
    {
        var now = _session?.GatewayWheelAction;
        // A new verdict is a new button: an old refusal no longer describes it.
        if (!Equals(now, _shownAction)) Refusal = null;
        _shownAction = now;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[TerminalWheelPresenter] Changed handler FAILED: {ex.Message}"); }
    }
}
