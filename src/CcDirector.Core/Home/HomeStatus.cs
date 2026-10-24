namespace CcDirector.Core.Home;

/// <summary>Severity of a single readiness row on the home page.</summary>
public enum HomeCheckLevel
{
    Ok,
    Warn,
    Bad,

    /// <summary>
    /// Work that has not finished yet, and has not failed. Setup still running is NOT an error: on a
    /// brand-new machine the installer tells the user the tools finish installing the first time the
    /// app opens, and reporting that expected state as a red failure alarms them about the product
    /// working as designed. Rendered as calm progress, never as a problem to fix.
    /// </summary>
    Busy,
}

/// <summary>Where a row's "fix it" affordance should take the user, if anywhere.</summary>
public enum HomeCheckAction
{
    None,
    OpenTools,
    OpenSettings,

    /// <summary>
    /// Repair the cc-* tools in place (rebuild the shared Python venv) rather than just navigating.
    /// The Home tools row uses this so its "Fix it" button actually fixes the problem in one click.
    /// </summary>
    RepairTools,

    /// <summary>Open Settings on the GitHub tab, where git and gh are installed and gh is signed in (issue #4426).</summary>
    SetUpGitHub,
}

/// <summary>One row in the home page's system-status / readiness card.</summary>
public sealed record HomeCheck(
    string Title,
    HomeCheckLevel Level,
    string Detail,
    HomeCheckAction Action);

/// <summary>
/// One agent CLI's detection result, fed into the readiness "Agent CLIs" row. Director is
/// CLI-agnostic: any one of the supported CLIs (Claude Code, Pi, Codex, Gemini, OpenCode)
/// satisfies the requirement, so the row reports the set rather than a single binary.
/// </summary>
public sealed record AgentCliFact(string DisplayName, bool Found, string? Version);

/// <summary>
/// The computed readiness of a Director, shown on the full-screen home page when no
/// session is running. Pure data: <see cref="HomeStatusBuilder.Build"/> turns raw
/// service facts (gathered off the UI thread) into the rows the view renders, so the
/// decision logic is unit-testable without Avalonia.
/// </summary>
public sealed record HomeStatus(
    IReadOnlyList<HomeCheck> Checks,
    bool AllReady,
    int ReadyCount,
    int TotalCount);

/// <summary>
/// Builds the home page status rows from raw facts. Two intentional omissions:
/// the gateway is NOT a row (a local-only Director is a legitimate configuration, so it is
/// its own card and only its error states count against readiness, decided by the caller);
/// and there is no OpenAI-key or "Director running" row (the key is a voice-only feature,
/// not a setup gap, and a running Director is a tautology when you can see this page).
/// </summary>
public static class HomeStatusBuilder
{
    /// <summary>
    /// The row titles the user reads. Plain product words, not internal vocabulary - these are among
    /// the first things a new customer ever sees. Every lookup of a row goes through these constants
    /// so a rename is one edit, never a scatter of string literals.
    /// </summary>
    public const string AgentRowTitle = "Coding agent";

    public const string ToolsRowTitle = "DevThrottle tools";

    /// <summary>
    /// Whether the command line a spawned session reaches can actually drive this Director.
    ///
    /// This is a DIFFERENT question from the tools row, which is why it is a different row. The tools
    /// row asks whether the tools this install placed are present and working, and it can legitimately
    /// answer "8 of 8 passing" while every session on the machine is dead in the water - because the
    /// cc-devthrottle PATH resolves belongs to another install entirely. Reporting one as the other is
    /// how this page came to say "All systems go" directly above a red "Sessions cannot reach this
    /// Director" banner on the very same screen.
    /// </summary>
    public const string SessionsRowTitle = "Sessions";

    /// <summary>Whether this machine can work with GitHub: git, the GitHub command line, and its sign-in (issue #4426).</summary>
    public const string GitHubRowTitle = "GitHub";

    public static HomeStatus Build(
        IReadOnlyList<AgentCliFact> agentClis,
        int toolsBuilt,
        int toolsTotal,
        IReadOnlyList<string>? brokenTools = null,
        Tools.ToolHealthSummary? toolHealth = null,
        bool basePythonBroken = false,
        bool toolsSetupInProgress = false,
        Setup.FleetToolCheck? sessionReachability = null,
        Setup.GitHubSetupStatus? gitHub = null)
    {
        // Tool setup that is still running is progress, not a fault - it outranks every failure signal
        // below, because on first launch those signals ARE the unfinished setup. It goes red only once
        // setup has finished and the tools are still not working.
        HomeCheck toolsCheck;
        if (toolsSetupInProgress)
            toolsCheck = new HomeCheck(ToolsRowTitle, HomeCheckLevel.Busy,
                "Finishing setup - this completes on its own, you can start working",
                HomeCheckAction.None);
        // The shared base Python being hollow (present but unable to import its standard library) takes down
        // EVERY Python cc-* tool at once (issue #995). It is a distinct, repairable runtime failure, so it
        // short-circuits the per-tool breakdown with a clear message and a one-click repair - the repair
        // re-provisions the base Python.
        else if (basePythonBroken)
            toolsCheck = new HomeCheck(ToolsRowTitle, HomeCheckLevel.Bad,
                "The shared runtime the tools need cannot start; one-click repair reinstalls it",
                HomeCheckAction.RepairTools);
        // When tool tests have run (toolHealth supplied) the tools row reflects pass/fail/not-built;
        // before that it falls back to the cheap build-status check so the home renders immediately.
        else if (toolHealth is { } h)
            toolsCheck = BuildToolsFromHealth(h);
        else
            toolsCheck = BuildTools(toolsBuilt, toolsTotal, brokenTools ?? Array.Empty<string>());

        var checks = new List<HomeCheck>
        {
            BuildAgentClis(agentClis),
            toolsCheck,
        };

        // Only when there IS a verdict. An unjudged machine must not be handed a green row it did not
        // earn, and must not be handed a red one either - it gets no row at all until the check has run.
        if (BuildSessions(sessionReachability) is { } sessionsCheck)
            checks.Add(sessionsCheck);

        // The same rule: no row until the probe has answered, so a machine is never shown signed out before it was asked.
        if (gitHub is not null)
            checks.Add(BuildGitHub(gitHub));

        var readyCount = checks.Count(c => c.Level == HomeCheckLevel.Ok);
        var allReady = readyCount == checks.Count;
        return new HomeStatus(checks, allReady, readyCount, checks.Count);
    }

    /// <summary>
    /// The Sessions row, or null when the check has not reached a verdict yet.
    ///
    /// The detail is written for someone who has just been told by an agent that DevThrottle is down.
    /// It names the real cause, because the failure mode this row exists to end is a user believing the
    /// product or the network is broken when neither is.
    /// </summary>
    private static HomeCheck? BuildSessions(Setup.FleetToolCheck? check)
    {
        if (check is null) return null;

        return check.Verdict switch
        {
            Setup.FleetToolVerdict.Working =>
                new HomeCheck(SessionsRowTitle, HomeCheckLevel.Ok,
                    "the command line can reach the fleet through the Gateway", HomeCheckAction.None),

            Setup.FleetToolVerdict.NotFound =>
                new HomeCheck(SessionsRowTitle, HomeCheckLevel.Bad,
                    "cc-devthrottle is not on this machine's PATH, so sessions cannot drive DevThrottle",
                    HomeCheckAction.OpenTools),

            Setup.FleetToolVerdict.CannotReachGateway when check.IsDifferentInstall =>
                new HomeCheck(SessionsRowTitle, HomeCheckLevel.Bad,
                    "the command line on your PATH is from another install, so agents report "
                    + "\"cannot connect to DevThrottle\"", HomeCheckAction.OpenTools),

            Setup.FleetToolVerdict.CannotReachGateway =>
                new HomeCheck(SessionsRowTitle, HomeCheckLevel.Bad,
                    $"the command line cannot reach the fleet: {check.Detail}", HomeCheckAction.OpenTools),

            // The Gateway is connected and refusing the key. RED, and routed to SETTINGS rather than
            // to Tools: OpenTools would send someone to repair an install that has nothing wrong
            // with it, which is the same wrong-destination mistake #1045 fixed for the row above.
            // Settings is where the Gateway this Director is attached to is named, which is the one
            // thing the user can actually act on from here - a Director cannot deploy a Gateway, and
            // a row that offers nowhere to go reads as broken.
            //
            // The sentence names the Gateway because the failure the user has in front of them is an
            // agent saying DevThrottle is broken, on a machine where nothing is. It also says EVERY
            // session, because that is what turns this from "my session is odd" into something to act
            // on - one refused session looks like bad luck, all of them do not.
            //
            // It says the key was NOT ACCEPTED, and offers an out-of-date Gateway as the likely cause
            // rather than asserting it. The evidence underneath is a single false from
            // RegisterSessionKeyAsync, which returns the same value for a hub-side refusal, an
            // unconnected tunnel and a transport failure - so a row that stated "the Gateway is out of
            // date" would be a confident diagnosis drawn from evidence that cannot tell those apart.
            // That is the exact failure this whole change exists to stop, and it would be poor to
            // reintroduce it in the row meant to fix it.
            Setup.FleetToolVerdict.GatewayRefusedKey =>
                new HomeCheck(SessionsRowTitle, HomeCheckLevel.Bad,
                    "the Gateway did not accept this Director's session keys, so EVERY session's "
                    + "command line answers 401 - most often a Gateway older than this Director",
                    HomeCheckAction.OpenSettings),

            // No Gateway means no agent tooling - the accepted trade, not a fault in the install.
            // No row, matching what this page has always done for the standalone state: a local-only
            // machine must not carry a standing warning for a configuration it chose, and painting the
            // tools red for it would offer repairs to an install with nothing wrong.
            Setup.FleetToolVerdict.NoGateway => null,

            // Unchecked, or a verdict added later that this switch has not been taught. Saying nothing
            // is correct; inventing a green row would be the bug this whole row exists to prevent.
            _ => null,
        };
    }

    /// <summary>
    /// The GitHub row (issue #4426): green when gh is signed in, amber otherwise, with the step that is missing and a
    /// way to fix it. Amber, not red - DevThrottle runs without GitHub - but it is not "all systems go" either, because a
    /// trial user who could not push was never told GitHub had not been set up.
    /// </summary>
    private static HomeCheck BuildGitHub(Setup.GitHubSetupStatus gitHub) => gitHub.State switch
    {
        Setup.GitHubSetupState.SignedIn => new HomeCheck(GitHubRowTitle, HomeCheckLevel.Ok, gitHub.Detail, HomeCheckAction.None),
        Setup.GitHubSetupState.SignedOut => new HomeCheck(GitHubRowTitle, HomeCheckLevel.Warn,
            "not signed in - sign in once so sessions can push and open pull requests", HomeCheckAction.SetUpGitHub),
        _ => new HomeCheck(GitHubRowTitle, HomeCheckLevel.Warn, gitHub.Detail, HomeCheckAction.SetUpGitHub),
    };

    /// <summary>
    /// The cc-* tools row from actual test results. Green ONLY when every tool passes. Otherwise it
    /// warns and shows the true breakdown ("26 pass · 2 not built" / "24 pass · 1 fail · 4 not built") -
    /// any failing OR not-built tool surfaces here rather than hiding behind "all systems go". A failing
    /// built tool OR a broken (expected-but-missing) tool offers the one-click repair - a failure is
    /// repairable, not merely something to look at; a not-built-only state (optional tools) routes to the
    /// Tools page.
    /// </summary>
    private static HomeCheck BuildToolsFromHealth(Tools.ToolHealthSummary h)
    {
        if (h.Total == 0)
            return new HomeCheck(ToolsRowTitle, HomeCheckLevel.Ok, "no tools installed", HomeCheckAction.None);

        var parts = new List<string> { $"{h.Pass} pass" };
        if (h.Fail > 0) parts.Add($"{h.Fail} fail");
        if (h.NotBuilt > 0) parts.Add($"{h.NotBuilt} not built");
        var detail = string.Join(" · ", parts);

        if (!h.HasProblem)
            return new HomeCheck(ToolsRowTitle, HomeCheckLevel.Ok, detail, HomeCheckAction.None);

        // Name the failing tools WITH the reason each one gave. "1 fail - failing: cc-pdf" sends the reader
        // to a log that kept no record of why; "failing: cc-pdf (smoke check: timed out after 90s)" is a
        // fact they can act on, and it is free - the runner already knows it (issue #1045).
        if (h.Failures.Count > 0)
        {
            var shown = string.Join(", ", h.Failures.Take(2).Select(f => f.ToString()));
            if (h.Failures.Count > 2) shown += $", +{h.Failures.Count - 2} more";
            detail += $" - failing: {shown}";
        }

        var action = (h.Broken > 0 || h.Fail > 0) ? HomeCheckAction.RepairTools : HomeCheckAction.OpenTools;
        return new HomeCheck(ToolsRowTitle, HomeCheckLevel.Warn, detail, action);
    }

    /// <summary>
    /// Director is CLI-agnostic: ready when ANY supported agent CLI is installed. Red only
    /// when none of them are. The detail lists what was found (with versions where known).
    /// </summary>
    private static HomeCheck BuildAgentClis(IReadOnlyList<AgentCliFact> agentClis)
    {
        var installed = agentClis.Where(c => c.Found).ToList();
        if (installed.Count == 0)
            return new HomeCheck(AgentRowTitle, HomeCheckLevel.Bad,
                "No coding agent found - install Claude Code, Codex, Pi, Gemini, or OpenCode",
                HomeCheckAction.OpenSettings);

        // "ready", not "on PATH". An agent counts when it is launchable, and being on PATH is only one
        // of the ways it can be - the wizard records an absolute path for an agent it installed off
        // PATH, and that agent is just as usable. Naming a mechanism the row does not actually know
        // would be a small lie in the same family as the one this row was fixed for (issue #1047).
        var names = installed.Select(CliLabel);
        return new HomeCheck(AgentRowTitle, HomeCheckLevel.Ok,
            $"{string.Join(", ", names)} - ready", HomeCheckAction.None);
    }

    /// <summary>
    /// "Claude Code 2.1.177" from a CLI fact. Some CLIs (e.g. Claude) report their version as
    /// "2.1.177 (Claude Code)"; we drop a trailing parenthetical so the product name is not
    /// printed twice.
    /// </summary>
    private static string CliLabel(AgentCliFact cli)
    {
        if (string.IsNullOrWhiteSpace(cli.Version)) return cli.DisplayName;

        var version = cli.Version.Trim();
        var paren = version.IndexOf('(');
        if (paren > 0) version = version[..paren].Trim();

        return version.Length == 0 ? cli.DisplayName : $"{cli.DisplayName} {version}";
    }

    /// <summary>
    /// The cc-* tools row. Reports only tools this install is EXPECTED to provide (it placed a shim or
    /// built them): <paramref name="total"/> is that expected count, <paramref name="built"/> is how many
    /// actually run, and <paramref name="broken"/> names the expected-but-not-runnable ones. Tools that
    /// were never installed here (the extras tier, other bundles, manifest drift) are excluded by the
    /// caller, so a healthy machine is GREEN instead of nagging "25 of 32". When something is genuinely
    /// broken it names the tools and offers a one-click repair (<see cref="HomeCheckAction.RepairTools"/>).
    /// </summary>
    private static HomeCheck BuildTools(int built, int total, IReadOnlyList<string> broken)
    {
        if (total == 0)
            return new HomeCheck(ToolsRowTitle, HomeCheckLevel.Ok, "no tools installed", HomeCheckAction.None);
        if (built == total)
            return new HomeCheck(ToolsRowTitle, HomeCheckLevel.Ok, $"{total} installed, all working", HomeCheckAction.None);

        string detail;
        if (broken.Count > 0)
        {
            var shown = string.Join(", ", broken.Take(4));
            if (broken.Count > 4) shown += $", +{broken.Count - 4} more";
            detail = $"{total - built} of {total} need repair: {shown}";
        }
        else
        {
            detail = $"{built} of {total} working";
        }

        var level = built == 0 ? HomeCheckLevel.Bad : HomeCheckLevel.Warn;
        return new HomeCheck(ToolsRowTitle, level, detail, HomeCheckAction.RepairTools);
    }
}
