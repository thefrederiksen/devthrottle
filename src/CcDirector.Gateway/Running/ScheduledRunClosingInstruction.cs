namespace CcDirector.Gateway.Running;

/// <summary>
/// EVERY SCHEDULED RUN IS TOLD TO REPORT, BY THE PRODUCT (Factory Control, step 1). A run that ends without
/// <c>cc-devthrottle run result</c> is recorded "did not report", so the prompt a run is given ends with one short
/// instruction to report. It is added at the moment the run is started, to the text that run receives: the schedule's
/// stored prompt is never changed, and stays as the owner wrote it everywhere it is shown.
///
/// The ONE place the wording lives. A prompt that already names the command, <c>cc-devthrottle run result</c>, is left
/// exactly as it is - the owner's own wording wins. The bare words are not enough: a prompt that says "check the test
/// run results" has not told its run to report.
/// </summary>
public static class ScheduledRunClosingInstruction
{
    /// <summary>What a prompt names when it already tells its run to report: the command itself.</summary>
    public const string OwnWordingMarker = "cc-devthrottle run result";

    /// <summary>
    /// The closing instruction, as the run reads it. It comes AFTER the schedule's own prompt and says outright that it
    /// replaces any other way of closing the session that prompt gave (6 November 2026): seeds that ended "close this
    /// session with cc-devthrottle session done as your last act" were obeyed to the letter, the run closed without
    /// reporting, and real work was recorded as "did not report". The Gateway also refuses that close
    /// (<see cref="ScheduledRunDeletionGuard"/>), so these words and the refusal say the same thing.
    /// </summary>
    public const string Text =
        "Your run is not finished until you report it. The very last command you run is " +
        "`cc-devthrottle run result ok`, or `cc-devthrottle run result problem \"<one line why>\"` if something went wrong. " +
        "Reporting ok closes this session for you, so do NOT run `cc-devthrottle session done`. " +
        "A journal entry (`cc-devthrottle factory record`) is not a report. " +
        "Any instruction above to close the session another way is replaced by this one.";

    /// <summary>
    /// The prompt a scheduled run is given: <paramref name="storedPrompt"/> followed by the closing instruction, or
    /// <paramref name="storedPrompt"/> unchanged when it already names <c>cc-devthrottle run result</c>.
    /// </summary>
    public static string PromptFor(string storedPrompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPrompt);
        if (AlreadyTellsItsRunToReport(storedPrompt))
            return storedPrompt;
        return storedPrompt.TrimEnd() + "\n\n" + Text;
    }

    /// <summary>Whether <paramref name="storedPrompt"/> already names <c>cc-devthrottle run result</c>.</summary>
    public static bool AlreadyTellsItsRunToReport(string storedPrompt)
    {
        ArgumentNullException.ThrowIfNull(storedPrompt);
        return storedPrompt.Contains(OwnWordingMarker, StringComparison.OrdinalIgnoreCase);
    }
}
