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
}

/// <summary>One row in the home page's system-status / readiness card.</summary>
public sealed record HomeCheck(
    string Title,
    HomeCheckLevel Level,
    string Detail,
    HomeCheckAction Action);

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
/// Builds the home page status rows from raw facts. The gateway is intentionally NOT a
/// row here: a local-only Director with no gateway is a legitimate configuration, not a
/// setup gap, so the gateway is surfaced as its own card and only its error states count
/// against readiness (decided by the caller).
/// </summary>
public static class HomeStatusBuilder
{
    public static HomeStatus Build(
        bool claudeFound,
        string? claudeVersion,
        bool keyPresent,
        string keyUnavailableMessage,
        bool keyUsesGateway,
        int toolsBuilt,
        int toolsTotal,
        string directorVersion)
    {
        var checks = new List<HomeCheck>
        {
            BuildClaude(claudeFound, claudeVersion),
            BuildKey(keyPresent, keyUnavailableMessage, keyUsesGateway),
            BuildTools(toolsBuilt, toolsTotal),
            new HomeCheck("Director", HomeCheckLevel.Ok, $"{directorVersion} - running", HomeCheckAction.None),
        };

        var readyCount = checks.Count(c => c.Level == HomeCheckLevel.Ok);
        var allReady = readyCount == checks.Count;
        return new HomeStatus(checks, allReady, readyCount, checks.Count);
    }

    private static HomeCheck BuildClaude(bool found, string? version)
    {
        if (!found)
            return new HomeCheck("claude CLI", HomeCheckLevel.Bad,
                "Not found - set the path in Settings > Tools", HomeCheckAction.OpenSettings);

        var detail = string.IsNullOrWhiteSpace(version) ? "on PATH" : $"{version} - on PATH";
        return new HomeCheck("claude CLI", HomeCheckLevel.Ok, detail, HomeCheckAction.None);
    }

    private static HomeCheck BuildKey(bool present, string unavailableMessage, bool usesGateway)
    {
        if (!present)
        {
            var detail = string.IsNullOrWhiteSpace(unavailableMessage)
                ? "Not set - voice and dictation are disabled"
                : unavailableMessage;
            return new HomeCheck("OpenAI key", HomeCheckLevel.Bad, detail, HomeCheckAction.OpenSettings);
        }

        return new HomeCheck("OpenAI key", HomeCheckLevel.Ok,
            usesGateway ? "Set (Gateway vault)" : "Set", HomeCheckAction.None);
    }

    private static HomeCheck BuildTools(int built, int total)
    {
        var detail = $"{built} of {total} on PATH";
        if (total > 0 && built == total)
            return new HomeCheck("cc-* tools", HomeCheckLevel.Ok, detail, HomeCheckAction.None);
        if (built == 0)
            return new HomeCheck("cc-* tools", HomeCheckLevel.Bad, detail, HomeCheckAction.OpenTools);
        return new HomeCheck("cc-* tools", HomeCheckLevel.Warn, detail, HomeCheckAction.OpenTools);
    }
}
