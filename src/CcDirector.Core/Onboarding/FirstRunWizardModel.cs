using CcDirector.Core.Utilities;

namespace CcDirector.Core.Onboarding;

/// <summary>
/// The first-run wizard screens in their canonical order (issue #2100 mockup). A given
/// release presents a subsequence of these: the bookends (<see cref="Welcome"/> and
/// <see cref="Done"/>) always ship, the middle steps slot in as their own issues land. Absent
/// steps are simply not part of the present-step list a <see cref="FirstRunWizardModel"/> is built
/// with, so the wizard tolerates a configurable step list without re-ordering logic.
///
/// EVERY STEP HERE MUST BE ABOUT THIS MACHINE. The wizard runs once per director, per machine, so a
/// question whose answer belongs to the ACCOUNT gets asked once per machine and the copies never
/// reconcile. That is why there is no daily-report step (issue #996): the report is one person, one
/// email, delivered through the gateway, so asking about it on machine two was asking the same
/// person the same question again and writing the answer somewhere machine one could not see. The
/// gateway step stays, because enrolling THIS machine to a gateway genuinely is about this machine.
/// </summary>
public enum WizardStep
{
    Welcome,
    Agents,
    Tools,
    Code,

    /// <summary>git, the GitHub command line and its sign-in (issue #4426). About this machine: gh signs in per machine.</summary>
    GitHub,

    Screenshots,
    Browsers,
    Gateway,
    Done,
}

/// <summary>
/// The rendered state of one progress dot relative to the current step: an already-visited step,
/// the current step, or an upcoming step. The client renders the finished colours the Gateway/model
/// hands it (client-is-dumb: the model decides "what a dot means", the view only paints it).
/// </summary>
public enum WizardDotState
{
    Past,
    Current,
    Upcoming,
}

/// <summary>
/// UI-free state machine for the first-run setup wizard (issue #2101, epic #2100). Owns the ordered
/// present-step list, the current position, back/forward navigation, the progress-dot states, and
/// the skip rules - so the Avalonia dialog is a thin shell over decisions made and tested here,
/// exactly as <see cref="OnboardingModel"/> / <see cref="ToolDetectionWizardModel"/> are.
///
/// Gating and the completion marker are shared with the retired two-dialog onboarding path
/// (<see cref="OnboardingModel.IsOnboardingComplete"/> / <see cref="OnboardingModel.MarkComplete"/>,
/// the <c>onboarding.completed</c> key in config.json). Sharing the marker gives migration for free:
/// a machine that already finished the OLD onboarding carries the marker and never sees the new
/// wizard.
/// </summary>
public sealed class FirstRunWizardModel
{
    /// <summary>
    /// Every wizard step in the one true order. The present-step list a model is constructed with is
    /// always re-ordered to this sequence, so step order is guaranteed regardless of which interim or
    /// absent steps a given release includes.
    /// </summary>
    /// <remarks>
    /// The gateway comes FIRST, before anything about this machine. Connecting is who you are, and
    /// everything after it is configuration of one particular computer - so identity leads, and the
    /// machine questions follow. It also puts the one step that carries real value for the user (their
    /// agents on their phone, voice, the morning report) where it will actually be seen rather than
    /// five screens in, after the attention has gone.
    ///
    /// This does NOT make the gateway a gate. "Not now" stays exactly as prominent as it was: the
    /// Director installs and runs with no account, and a first screen that reads as sign-up-to-continue
    /// would change what the product is.
    ///
    /// Browsers comes LAST of the real steps, straight after Screenshots (issue #1012). The two belong
    /// beside each other - both are about how an agent gets context from you rather than about the agent
    /// itself - and it is the one step that can ask for a browser sign-in, which is interactive and
    /// cannot be hurried. Putting it last means the wizard has already delivered everything else before
    /// it asks for the minute that costs the most.
    ///
    /// GitHub comes straight after Code (issue #4426): once the person has said where their repositories are, the next
    /// thing those repositories need is a way to push. A trial user set up a repository and was never asked to sign in.
    /// </remarks>
    public static readonly IReadOnlyList<WizardStep> CanonicalOrder = new[]
    {
        WizardStep.Welcome,
        WizardStep.Gateway,
        WizardStep.Agents,
        WizardStep.Tools,
        WizardStep.Code,
        WizardStep.GitHub,
        WizardStep.Screenshots,
        WizardStep.Browsers,
        WizardStep.Done,
    };

    private readonly List<WizardStep> _steps;
    private int _index;

    // Until an agent scan says otherwise, assume at least one agent exists so the Agents step does
    // not block skipping before it has been evaluated. The shell calls SetAgentsFound after its scan.
    private bool _agentsFound = true;
    private bool _agentsDeferred;

    /// <summary>
    /// Build a wizard over the given present steps. The list is normalised to
    /// <see cref="CanonicalOrder"/> (re-ordered and de-duplicated), and the mandatory bookends are
    /// enforced: <see cref="WizardStep.Welcome"/> must be first and <see cref="WizardStep.Done"/>
    /// must be last. Throws on an empty list or a missing bookend rather than silently repairing it.
    /// </summary>
    public FirstRunWizardModel(IEnumerable<WizardStep> presentSteps)
    {
        FileLog.Write("[FirstRunWizardModel] Constructor");
        if (presentSteps is null) throw new ArgumentNullException(nameof(presentSteps));

        var present = new HashSet<WizardStep>(presentSteps);
        _steps = CanonicalOrder.Where(present.Contains).ToList();

        if (_steps.Count == 0)
            throw new ArgumentException("The wizard needs at least the Welcome and Done steps.", nameof(presentSteps));
        if (_steps[0] != WizardStep.Welcome)
            throw new ArgumentException("Welcome must be the first present step.", nameof(presentSteps));
        if (_steps[^1] != WizardStep.Done)
            throw new ArgumentException("Done must be the last present step.", nameof(presentSteps));

        FileLog.Write($"[FirstRunWizardModel] Constructor: steps=[{string.Join(",", _steps)}]");
    }

    /// <summary>Build a wizard over every canonical step (the full mockup flow).</summary>
    public static FirstRunWizardModel CreateFull() => new(CanonicalOrder);

    /// <summary>The present steps, in canonical order. Its length is the number of progress dots.</summary>
    public IReadOnlyList<WizardStep> Steps => _steps;

    /// <summary>The number of present steps (and therefore progress dots).</summary>
    public int Count => _steps.Count;

    /// <summary>The zero-based index of the current step within <see cref="Steps"/>.</summary>
    public int Index => _index;

    /// <summary>The step the wizard is currently on.</summary>
    public WizardStep Current => _steps[_index];

    /// <summary>True when the wizard is on its first present step (Welcome).</summary>
    public bool IsFirst => _index == 0;

    /// <summary>True when the wizard is on its last present step (Done).</summary>
    public bool IsLast => _index == _steps.Count - 1;

    /// <summary>True when the given step is part of this wizard's present-step list.</summary>
    public bool Contains(WizardStep step) => _steps.Contains(step);

    /// <summary>
    /// Advance to the next present step. Returns false (and does not move) when already on the last
    /// step, so the caller can treat "next past Done" as finish.
    /// </summary>
    public bool MoveNext()
    {
        if (IsLast) return false;
        _index++;
        FileLog.Write($"[FirstRunWizardModel] MoveNext -> {Current}");
        return true;
    }

    /// <summary>
    /// Step back to the previous present step. Returns false (and does not move) when already on the
    /// first step.
    /// </summary>
    public bool MoveBack()
    {
        if (IsFirst) return false;
        _index--;
        FileLog.Write($"[FirstRunWizardModel] MoveBack -> {Current}");
        return true;
    }

    /// <summary>Jump to a specific present step. Throws when the step is not present.</summary>
    public void GoTo(WizardStep step)
    {
        var i = _steps.IndexOf(step);
        if (i < 0) throw new ArgumentException($"Step {step} is not present in this wizard.", nameof(step));
        _index = i;
        FileLog.Write($"[FirstRunWizardModel] GoTo -> {Current}");
    }

    /// <summary>
    /// Record whether the agent scan found at least one usable agent. This drives the single hard
    /// skip rule: the Agents step is unskippable when zero agents are found, because the product is
    /// unusable without one. Wired even while the Agents step is interim (issue #2101 acceptance).
    /// </summary>
    public void SetAgentsFound(bool found)
    {
        _agentsFound = found;
        FileLog.Write($"[FirstRunWizardModel] SetAgentsFound: {found}");
    }

    /// <summary>Whether the last agent scan reported at least one agent (defaults true pre-scan).</summary>
    public bool AgentsFound => _agentsFound;

    /// <summary>
    /// Record that the user chose "I'll do this later" on the zero-agents state. Deferral is the
    /// honest alternative to a skip the rule forbids: the wizard may proceed, but the missing agent
    /// stays a carried to-do - the Done screen leads with "install an agent" instead of "start my
    /// first agent". Finding an agent later (a re-scan after an install) clears the deferral.
    /// </summary>
    public void DeferAgents()
    {
        _agentsDeferred = true;
        FileLog.Write("[FirstRunWizardModel] DeferAgents");
    }

    /// <summary>True while the user has parked the zero-agents state for later and no agent has been found since.</summary>
    public bool AgentsDeferred => _agentsDeferred && !_agentsFound;

    /// <summary>
    /// Whether the given step may be individually skipped. Every step is skippable EXCEPT the Agents
    /// step when zero agents were found. This is a pure rule the shell renders (show/hide the skip
    /// affordance) - never re-derived in the view.
    /// </summary>
    public bool CanSkipStep(WizardStep step) => step switch
    {
        WizardStep.Agents => _agentsFound,
        _ => true,
    };

    /// <summary>Whether the current step may be individually skipped.</summary>
    public bool CanSkipCurrent => CanSkipStep(Current);

    /// <summary>
    /// True for the one step that carries the quiet whole-wizard skip link (Welcome). A whole-wizard
    /// skip drops straight to the board and writes the completion marker.
    /// </summary>
    public static bool IsWholeWizardSkip(WizardStep step) => step == WizardStep.Welcome;

    /// <summary>
    /// The dot state for the progress dot at <paramref name="position"/> (0-based over
    /// <see cref="Steps"/>): visited steps are Past, the current step is Current, later steps are
    /// Upcoming. Throws when the position is outside the present-step range.
    /// </summary>
    public WizardDotState DotStateAt(int position)
    {
        if (position < 0 || position >= _steps.Count)
            throw new ArgumentOutOfRangeException(nameof(position));
        if (position < _index) return WizardDotState.Past;
        if (position == _index) return WizardDotState.Current;
        return WizardDotState.Upcoming;
    }

    // ---- gating + completion marker (shared with the retired two-dialog onboarding path) ----------

    /// <summary>
    /// True when the wizard should auto-open on launch: the completion marker is absent. A machine
    /// that finished the OLD onboarding (its <c>onboarding.completed</c> marker present) returns
    /// false here, so it never sees the new wizard - the migration guarantee.
    /// </summary>
    public static bool ShouldShow()
    {
        var complete = OnboardingModel.IsOnboardingComplete();
        FileLog.Write($"[FirstRunWizardModel] ShouldShow: complete={complete}, result={!complete}");
        return !complete;
    }

    /// <summary>
    /// Write the completion marker so the wizard never nags twice - called however the user leaves it
    /// (finished on Done, whole-wizard skip on Welcome, or closing the window). Idempotent: the same
    /// merge-patch as the retired path, so re-running from Settings is harmless.
    /// </summary>
    public static void MarkComplete()
    {
        FileLog.Write("[FirstRunWizardModel] MarkComplete");
        OnboardingModel.MarkComplete();
    }
}
