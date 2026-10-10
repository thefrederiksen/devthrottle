using System.Runtime.CompilerServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// The one way the desktop shows an error to the user (issue #3675, step 4a): showing it and reporting it are
/// ONE call, so an error can never reach a screen without reaching the Gateway's error store.
///
/// Before this, about half of the Director's message boxes, dialogs and error lines in panels reported nothing:
/// "Failed to load reviews" was shown and never written anywhere the nightly reader could see it. The rule is
/// now that error text is put on a screen only through <see cref="Report"/> (or, for a message box, through the
/// desktop's <c>ShownErrorBox</c>, which calls it), and <c>ShownErrorSourceScanTests</c> fails the build on any
/// display site that does not.
///
/// WHAT IT WRITES. One FileLog line in the house form, <c>[ClassName] MethodName FAILED: could not
/// {action}: {what the user saw}</c>, with the exception and its stack on the lines after it when there is one.
/// That is an error line, so the <see cref="ErrorReporter"/> picks it up like any other - scrubbed, capped,
/// rate-limited, kept on disk before sign-in. The class and method come from the compiler, so the row names the
/// code that showed the error. The line is written inside an <see cref="ErrorContext"/> that says the user saw it
/// and names the surface and the action, so the report carries user_visible, surface and action as FIELDS. That
/// scope is the innermost one and wraps this one line only: a field that describes one row never leaks onto the
/// rows logged around it, and the correlation and session id of any scope the caller has open still apply.
///
/// WHAT IT NEVER WRITES. Prompt, transcript, terminal or model text. Where a site shows such text, it passes
/// <c>reported</c> - what failed, in our own words - and the line carries that instead of the shown text.
///
/// It does no input or output beyond <see cref="FileLog.Write"/>, which only queues, so it is safe on the user
/// interface thread and on any other.
/// </summary>
public static class ShownError
{
    /// <summary>
    /// Report an error the user is about to see, and hand back the text to show.
    /// </summary>
    /// <param name="surface">Which screen shows it, in plain words: "settings", "new session dialog".</param>
    /// <param name="action">What the user was trying to do, in plain words, as it reads after "could not":
    /// "load the turn reviews", "install the tools".</param>
    /// <param name="shown">The text the user sees. Returned unchanged.</param>
    /// <param name="exception">The exception behind it, when there is one; its type and stack go in the report.</param>
    /// <param name="reported">What to report INSTEAD of <paramref name="shown"/>, for a site whose shown text carries
    /// words that must not leave the machine (a prompt, a transcript, terminal or model output).</param>
    /// <param name="fatal">True when the process ends because of it (a start-up that cannot go on): the line says
    /// FATAL rather than FAILED, so the report's kind is "fatal".</param>
    /// <param name="callerFile">Supplied by the compiler: the file that showed the error.</param>
    /// <param name="callerMember">Supplied by the compiler: the method that showed the error.</param>
    /// <returns><paramref name="shown"/>, so a site reads <c>StatusText.Text = ShownError.Report(...)</c>.</returns>
    public static string Report(
        string surface,
        string action,
        string shown,
        Exception? exception = null,
        string? reported = null,
        bool fatal = false,
        [CallerFilePath] string callerFile = "",
        [CallerMemberName] string callerMember = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(shown);

        using (ErrorContext.Begin(userVisible: true, surface: surface, action: action))
            FileLog.Write(LineFor(surface, action, shown, exception, reported, fatal, callerFile, callerMember));
        return shown;
    }

    /// <summary>The line <see cref="Report"/> writes. Separate so a test can read it without a running log.</summary>
    internal static string LineFor(string surface, string action, string shown, Exception? exception,
        string? reported, bool fatal, string callerFile, string callerMember)
    {
        var source = ClassNameOf(callerFile);
        var member = string.IsNullOrWhiteSpace(callerMember) ? "Unknown" : callerMember;
        var what = OneLine(reported ?? shown);
        var marker = fatal ? "FATAL" : "FAILED";
        var head = $"[{source}] {member} {marker}: could not {OneLine(action)}: {what} (surface: {OneLine(surface)})";
        return exception is null ? head : $"{head}\n{exception}";
    }

    /// <summary>"C:\...\TurnReviewDialog.axaml.cs" gives "TurnReviewDialog": the class a code-behind file holds.</summary>
    internal static string ClassNameOf(string callerFile)
    {
        if (string.IsNullOrWhiteSpace(callerFile)) return "ShownError";
        // The compiler's path uses the separator of the machine that BUILT it, which need not be this one's.
        var name = callerFile[(callerFile.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    // The report's message is the first line of the logged line, and the lines after it are read as the stack -
    // so text the user saw across several lines is joined onto one here, or its tail would land in the stack.
    private static string OneLine(string text)
        => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
