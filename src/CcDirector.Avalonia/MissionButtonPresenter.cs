using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// THE MISSION BUTTON, without the controls (the Missions First-Class mission, phase 2). The Director draws it twice -
/// on the session tab bar beside the Cockpit button, and in the toolbar corner beside the toolbar Cockpit button - and
/// both show the session the window has selected, so one presenter feeds both. The window copies these values onto the
/// two buttons and calls <see cref="BuildOpenUrl"/> on a click; everything the button decides lives here, so it is
/// tested without a screen.
///
/// IT RENDERS THE GATEWAY'S VERDICT AND NOTHING ELSE (CLAUDE.md rule 7). The label, the tooltip and the address come
/// from <see cref="Session.GatewayMissionButton"/>, the Gateway's <see cref="MissionButtonDisplay"/>, verbatim. The
/// colour key maps to one of three looks in MainWindow.axaml and the icon key to one of the icons drawn before the word,
/// and to nothing else; this class never reads the verdict to decide what a state means. A colour key or an icon key
/// this build does not know is a broken contract: it is logged, and no button is drawn rather than a guessed look. A Gateway that sends no verdict (one older than the mission) means no
/// button, which is the merge plan's promise.
/// </summary>
public sealed class MissionButtonPresenter
{
    /// <summary>The look for a Mission with a document: indigo, filled. A style class in MainWindow.axaml.</summary>
    public const string LookFilled = "missionFilled";

    /// <summary>The look for a Mission with no document yet: the same indigo as an outline only.</summary>
    public const string LookOutline = "missionOutline";

    /// <summary>The look for a session with no Mission: the warning yellow.</summary>
    public const string LookWarning = "missionWarning";

    /// <summary>Every look, so the window can clear the old one before setting the new one.</summary>
    public static readonly IReadOnlyList<string> AllLooks = new[] { LookFilled, LookOutline, LookWarning };

    /// <summary>The only mapping the desktop does: the Gateway's colour key to the style class that draws it.</summary>
    private static readonly IReadOnlyDictionary<string, string> Looks = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MissionButtonDisplay.ColorMission] = LookFilled,
        [MissionButtonDisplay.ColorMissionOutline] = LookOutline,
        [MissionButtonDisplay.ColorWarning] = LookWarning,
    };

    /// <summary>The only other mapping: the Gateway's icon key to the icon drawn before the word.</summary>
    private static readonly IReadOnlyDictionary<string, MissionButtonIcon> Icons = new Dictionary<string, MissionButtonIcon>(StringComparer.Ordinal)
    {
        [MissionButtonDisplay.IconDocument] = MissionButtonIcon.Document,
        [MissionButtonDisplay.IconNone] = MissionButtonIcon.None,
        [MissionButtonDisplay.IconWarning] = MissionButtonIcon.Warning,
    };

    private readonly Action<Action> _post;
    private Session? _session;

    /// <summary>The verdict the buttons show, copied from the session on the screen thread only (in <see cref="Attach"/>
    /// and in the posted redraw). Every getter reads this one copy, so a stamp arriving on another thread mid-redraw
    /// cannot make one redraw read two verdicts; the stamp is shown when its posted redraw runs.</summary>
    private MissionButtonDisplay? _shown;

    /// <param name="post">Runs work on the screen thread. The Gateway's stamp arrives on another thread, and every
    /// change to what the buttons show is made on the screen thread so the window never reads half of one.</param>
    public MissionButtonPresenter(Action<Action> post)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>Raised on the screen thread whenever anything the buttons show changes.</summary>
    public event Action? Changed;

    /// <summary>The session the buttons describe, or null when none is selected.</summary>
    public Session? Session => _session;

    /// <summary>The Gateway's verdict for the selected session, or null when there is none or this build cannot draw it
    /// (see <see cref="Problem"/>).</summary>
    public MissionButtonDisplay? Verdict => Problem is null ? _shown : null;

    /// <summary>Why the Gateway's verdict cannot be drawn: a colour key or an icon key this build does not know. Null
    /// otherwise.</summary>
    public string? Problem => ProblemWith(_shown);

    /// <summary>The line shown in place of the button when the Gateway's verdict cannot be drawn - the
    /// <see cref="Problem"/>, verbatim - so an unknown colour key does not look exactly like "the Gateway sent no
    /// verdict". Null when there is nothing to say, which is also when the line is hidden.</summary>
    public string? Note => Problem;

    /// <summary>True when there is a button to draw.</summary>
    public bool IsVisible => Verdict is not null;

    /// <summary>The words on the button, verbatim; empty when there is no button.</summary>
    public string Label => Verdict?.Label ?? "";

    /// <summary>The words on hover, verbatim; null when there is no button.</summary>
    public string? Tooltip => Verdict?.Tooltip;

    /// <summary>The style class that draws the Gateway's colour key; null when there is no button.</summary>
    public string? Look => Verdict is { } v ? LookFor(v.ColorKey) : null;

    /// <summary>The icon drawn before the word, from the Gateway's icon key; <see cref="MissionButtonIcon.None"/> when
    /// there is no button.</summary>
    public MissionButtonIcon Icon => Verdict is { } v && IconFor(v.IconKey) is { } icon ? icon : MissionButtonIcon.None;

    /// <summary>True when a click opens something: the Gateway gave an address.</summary>
    public bool CanOpen => Verdict?.DocumentAddress is not null;

    /// <summary>The style class for a colour key, or null for a key this build does not know.</summary>
    public static string? LookFor(string? colorKey) =>
        colorKey is not null && Looks.TryGetValue(colorKey, out var look) ? look : null;

    /// <summary>The icon for an icon key, or null for a key this build does not know.</summary>
    public static MissionButtonIcon? IconFor(string? iconKey) =>
        iconKey is not null && Icons.TryGetValue(iconKey, out var icon) ? icon : null;

    /// <summary>
    /// The address a click opens: the Gateway base this Director is connected to, then the Gateway's own address for the
    /// document (<c>/r/&lt;report id&gt;</c>), joined and nothing more. Null when the Gateway gave no address (a Mission
    /// with no document yet, or no Mission), and then a click opens nothing. An address that is not a path on the Gateway
    /// is a broken contract and throws, so the click says so instead of opening a guessed address.
    /// </summary>
    public static string? BuildOpenUrl(string gatewayBase, string? documentAddress)
    {
        if (string.IsNullOrWhiteSpace(gatewayBase))
            throw new ArgumentException("The Gateway base address is required.", nameof(gatewayBase));
        if (documentAddress is null) return null;
        if (!documentAddress.StartsWith('/') || documentAddress.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The Gateway sent a Mission document address this Director cannot open (\"{documentAddress}\"): it must be a path "
                + "on the Gateway, starting with one slash.");
        return gatewayBase.TrimEnd('/') + documentAddress;
    }

    /// <summary>
    /// Follow this session (null for none). Called by the window whenever the selected session changes, which is how the
    /// corner button follows the selection.
    /// </summary>
    public void Attach(Session? session)
    {
        if (ReferenceEquals(_session, session)) return;
        FileLog.Write($"[MissionButtonPresenter] Attach: session={session?.Id.ToString() ?? "(none)"}");
        if (_session is not null) _session.OnGatewayDisplayStateChanged -= OnDisplayStateChanged;
        _session = session;
        // Subscribe BEFORE taking the snapshot: a stamp landing in between then posts a redraw that re-reads, where the
        // other order would leave it in neither the snapshot nor a redraw until the verdict changed again.
        if (session is not null) session.OnGatewayDisplayStateChanged += OnDisplayStateChanged;
        _shown = session?.GatewayMissionButton;
        LogProblem();
        RaiseChanged();
    }

    private void OnDisplayStateChanged() => _post(ApplyDisplayState);

    private void ApplyDisplayState()
    {
        _shown = _session?.GatewayMissionButton;
        LogProblem();
        RaiseChanged();
    }

    private void LogProblem()
    {
        if (Problem is { } problem)
            FileLog.Write($"[MissionButtonPresenter] undrawable Mission button: session={_session?.Id.ToString() ?? "(none)"}: {problem}");
    }

    private static string? ProblemWith(MissionButtonDisplay? verdict)
    {
        if (verdict is null) return null;
        if (LookFor(verdict.ColorKey) is null)
            return $"The Gateway sent a Mission button colour this Director does not know (\"{verdict.ColorKey}\"), so no Mission "
                + "button is shown. Update the Director or the Gateway so they agree.";
        if (IconFor(verdict.IconKey) is null)
            return $"The Gateway sent a Mission button icon this Director does not know (\"{verdict.IconKey}\"), so no Mission "
                + "button is shown. Update the Director or the Gateway so they agree.";
        return null;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[MissionButtonPresenter] Changed handler FAILED: {ex.Message}"); }
    }
}

/// <summary>The icon drawn before the word on the Mission button, as the Gateway's icon key names it.</summary>
public enum MissionButtonIcon
{
    /// <summary>No icon: the word alone (a Mission with no document yet).</summary>
    None,

    /// <summary>A small document (a Mission with a document).</summary>
    Document,

    /// <summary>A small yellow circle with an exclamation mark (a session with no Mission).</summary>
    Warning,
}
