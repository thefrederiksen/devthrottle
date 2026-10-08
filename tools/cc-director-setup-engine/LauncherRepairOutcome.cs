using System.Text.RegularExpressions;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Setup.Engine;

/// <summary>What one pass of the Director's launcher repair came to.</summary>
public enum LauncherRepairResult
{
    /// <summary>The launch agent was rebuilt and launchd reports the launcher running.</summary>
    Rebuilt,
    /// <summary>Nothing was rebuilt, on purpose: the launcher runs, was closed, or was switched off.</summary>
    LeftAlone,
    /// <summary>The pass could not look, or the rebuild did not end with a running launcher.</summary>
    Failed,
}

/// <summary>
/// One pass of <see cref="LauncherLaunchdRepair"/>, in a shape a report can be built from.
/// </summary>
/// <param name="Result">Rebuilt, left alone or failed.</param>
/// <param name="Verdict">The decision's verdict name (Running, ClosedOnPurpose, NoLaunchAgent, Disabled, Repair, ...),
/// or NoLauncherBinary, NotChecked (the pass could not look) or Exception (the pass threw).</param>
/// <param name="Reason">Why, in words.</param>
/// <param name="Pid">The process launchd reports for a rebuilt launcher; 0 otherwise.</param>
/// <param name="Line">The one line the Director logs, with every step and diagnostic the pass carries.</param>
public sealed record LauncherRepairOutcome(LauncherRepairResult Result, string Verdict, string Reason, int Pid, string Line)
{
    public override string ToString() => Line;
}

/// <summary>
/// The report one launcher repair pass sends to the Gateway (owner ruling of 7 October 2026: every outcome on a
/// signed-in user's machine reaches central error reporting, not only a failure). EXACTLY ONE per pass, whatever
/// the pass found, so a Mac that starts its Director often costs one row per start and never a flood.
///
/// The text names neither the person nor their home folder. <see cref="ErrorTextScrubber"/> turns a home folder in a
/// path into "~" and takes out anything shaped like a credential, on this side and again on the Gateway; on top of
/// that, the home folder as this process knows it becomes "~" wherever it appears, and the user name becomes
/// "&lt;user&gt;" wherever it stands as a word - launchctl's answers can carry it outside a path.
/// </summary>
public static class LauncherRepairReport
{
    /// <summary>The source every report carries.</summary>
    public const string Source = "LauncherLaunchdRepair";

    /// <summary>The kind every report carries, so the owner can ask for these and nothing else.</summary>
    public const string Kind = "launcher-repair";

    /// <summary>What replaces the user name in a report.</summary>
    public const string UserPlaceholder = "<user>";

    /// <summary>The text of one report: a message and the diagnostics behind it.</summary>
    public sealed record Text(string Message, string Detail);

    /// <summary>
    /// The report for <paramref name="outcome"/>, with the user name and home folder taken out. Pure.
    /// </summary>
    /// <param name="outcome">What the pass came to.</param>
    /// <param name="userName">The user this process runs as.</param>
    /// <param name="homeFolder">That user's home folder.</param>
    public static Text Compose(LauncherRepairOutcome outcome, string? userName, string? homeFolder)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var message = outcome.Result switch
        {
            LauncherRepairResult.Rebuilt =>
                $"launcher repair: rebuilt the launch agent and launchd reports the launcher running as process {outcome.Pid}. Found: {outcome.Reason}",
            LauncherRepairResult.LeftAlone =>
                $"launcher repair: left alone ({outcome.Verdict}): {outcome.Reason}",
            _ =>
                $"launcher repair: failed ({outcome.Verdict}): {outcome.Reason}",
        };
        // A left-alone pass has nothing behind its reason; a rebuild carries its steps and a failure carries every
        // diagnostic the pass gathered.
        var detail = outcome.Result == LauncherRepairResult.LeftAlone ? "" : outcome.Line;
        return new Text(
            ErrorTextScrubber.Clean(WithoutIdentity(message, userName, homeFolder), ErrorReportLimits.MaxMessage),
            ErrorTextScrubber.Clean(WithoutIdentity(detail, userName, homeFolder), ErrorReportLimits.MaxStack));
    }

    /// <summary>Send the one report for <paramref name="outcome"/> through <paramref name="reporter"/>.</summary>
    public static void Send(LauncherRepairOutcome outcome, ErrorReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        var text = Compose(outcome, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        reporter.ReportOutcome(Source, Kind, text.Message, text.Detail);
        EngineLog.Write($"[LauncherRepairReport] Send: {outcome.Result} ({outcome.Verdict}) queued for the Gateway");
    }

    /// <summary>The home folder as "~" and the user name, as a word, as <see cref="UserPlaceholder"/>. Pure.</summary>
    internal static string WithoutIdentity(string text, string? userName, string? homeFolder)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        var home = homeFolder?.TrimEnd('/', '\\');
        if (!string.IsNullOrEmpty(home) && home.Length > 1)
            s = s.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        s = ErrorTextScrubber.Scrub(s);
        var name = userName?.Trim();
        if (!string.IsNullOrEmpty(name))
            s = Regex.Replace(s, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", UserPlaceholder,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return s;
    }
}
