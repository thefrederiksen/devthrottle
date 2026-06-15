namespace CcDirector.Core.Home;

/// <summary>Severity of a single readiness row on the home page.</summary>
public enum HomeCheckLevel
{
    Ok,
    Warn,
    Bad,
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
    public static HomeStatus Build(
        IReadOnlyList<AgentCliFact> agentClis,
        int toolsBuilt,
        int toolsTotal,
        IReadOnlyList<string>? missingTools = null)
    {
        var checks = new List<HomeCheck>
        {
            BuildAgentClis(agentClis),
            BuildTools(toolsBuilt, toolsTotal, missingTools ?? Array.Empty<string>()),
        };

        var readyCount = checks.Count(c => c.Level == HomeCheckLevel.Ok);
        var allReady = readyCount == checks.Count;
        return new HomeStatus(checks, allReady, readyCount, checks.Count);
    }

    /// <summary>
    /// Director is CLI-agnostic: ready when ANY supported agent CLI is installed. Red only
    /// when none of them are. The detail lists what was found (with versions where known).
    /// </summary>
    private static HomeCheck BuildAgentClis(IReadOnlyList<AgentCliFact> agentClis)
    {
        var installed = agentClis.Where(c => c.Found).ToList();
        if (installed.Count == 0)
            return new HomeCheck("Agent CLIs", HomeCheckLevel.Bad,
                "No agent CLI found - install Claude Code, Codex, Pi, Gemini, or OpenCode",
                HomeCheckAction.OpenSettings);

        var names = installed.Select(CliLabel);
        return new HomeCheck("Agent CLIs", HomeCheckLevel.Ok,
            $"{string.Join(", ", names)} - on PATH", HomeCheckAction.None);
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
    /// The cc-* tools row. Green when every tool's runnable exe is present. When some are missing it
    /// names them (so the warning is specific, not just "25 of 32") and offers a one-click in-place
    /// repair (<see cref="HomeCheckAction.RepairTools"/>) instead of merely opening the Tools page.
    /// </summary>
    private static HomeCheck BuildTools(int built, int total, IReadOnlyList<string> missing)
    {
        if (total > 0 && built == total)
            return new HomeCheck("cc-* tools", HomeCheckLevel.Ok, $"{built} of {total} on PATH", HomeCheckAction.None);

        string detail;
        if (missing.Count > 0)
        {
            var shown = string.Join(", ", missing.Take(4));
            if (missing.Count > 4) shown += $", +{missing.Count - 4} more";
            detail = $"{total - built} of {total} not installed: {shown}";
        }
        else
        {
            detail = $"{built} of {total} on PATH";
        }

        var level = built == 0 ? HomeCheckLevel.Bad : HomeCheckLevel.Warn;
        return new HomeCheck("cc-* tools", level, detail, HomeCheckAction.RepairTools);
    }
}
