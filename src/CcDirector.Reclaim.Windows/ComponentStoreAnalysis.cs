using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// What came back from asking Windows how big its component store is.
/// </summary>
/// <param name="Answered">True when Windows answered with the two sizes. False means the rule does
/// not know, which is a different thing from knowing the store is empty.</param>
/// <param name="ActualSizeBytes">The size Windows itself reports for the component store, not the
/// size a directory walk would report: most of the store's files are hard links, and only Windows
/// knows which bytes belong to the store alone.</param>
/// <param name="BytesItsCommandCanClear">The bytes Windows itself says its cleanup command can
/// clear: the store's superseded versions and disabled features, the only part of the store the
/// cleanup command touches.</param>
/// <param name="ReasonNotAnswered">Why Windows did not answer, in one finished sentence, when it
/// did not. Null when it did.</param>
public sealed record ComponentStoreQuestion(
    bool Answered,
    long ActualSizeBytes,
    long BytesItsCommandCanClear,
    string? ReasonNotAnswered)
{
    /// <summary>Build the answer for a question Windows did not answer.</summary>
    /// <param name="reason">Why, in one finished sentence.</param>
    public static ComponentStoreQuestion Unanswered(string reason) => new(false, 0, 0, reason);
}

/// <summary>
/// Where the component store question is asked, so the rule can be tested.
///
/// The question is answered by running a command, and a test that ran the real command would depend
/// on a component store existing on whatever machine the test runs on. The analysis command is put
/// behind this for the same reason the installer records are: the machinery that runs a command and
/// reads its answer is proven with a harmless command a test supplies, never with the real one.
/// </summary>
public interface IComponentStoreAnalysis
{
    /// <summary>
    /// Ask Windows how big its component store is. It reads; it changes nothing.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The machine is not running Windows.</exception>
    ComponentStoreQuestion Ask();
}

/// <summary>
/// Asks Windows by running its own analysis command and reading its report.
///
/// The command is Dism.exe with /AnalyzeComponentStore, which is the ANALYSIS command: it measures
/// the store and removes nothing. It is not the cleanup command, which is never run by anything in
/// this tool - the rule prints the cleanup command for the owner, and what runs here only asks
/// Windows what it would say.
///
/// Asking needs an administrator, and this tool never raises itself to one. When it is not running
/// as an administrator the question is answered without starting the command at all, which is the
/// honest answer the mission asks for: the size cannot be determined, so the rule says it does not
/// know rather than guessing, and the rule reports BROKEN rather than nothing to remove.
///
/// The report is read as plain text and Windows localises it, so on a machine that speaks a
/// language this parser does not recognise the question comes back unanswered and the rule says it
/// does not know. Failing closed is the correct behaviour for a parser that cannot prove it read
/// the right lines.
/// </summary>
public sealed class DismComponentStoreAnalysis : IComponentStoreAnalysis
{
    /// <summary>Windows' own analysis command, which measures the store and removes nothing.</summary>
    public const string DefaultExecutable = "Dism.exe";

    /// <summary>The arguments that ask for the component store analysis report.</summary>
    public const string DefaultArguments = "/Online /Cleanup-Image /AnalyzeComponentStore";

    private readonly string _executable;
    private readonly string _arguments;
    private readonly bool _requiresAdministrator;
    private readonly Func<bool>? _runningAsAdministrator;
    private readonly TimeSpan _timeLimit;

    /// <summary>
    /// Build the question.
    /// </summary>
    /// <param name="executable">The command to run. The default is Windows' own.</param>
    /// <param name="arguments">The arguments that ask for the analysis report.</param>
    /// <param name="requiresAdministrator">
    /// True when the command must not be started unless this process is running as an
    /// administrator. The default is true because Windows' own analysis refuses to run otherwise.
    /// A test that supplies a harmless command passes false so the machinery can be proven.
    /// </param>
    /// <param name="runningAsAdministrator">
    /// How the running process is known to be an administrator, in place of asking the system, or
    /// null to ask the system. A test supplies its own answer; nothing else does.
    /// </param>
    /// <param name="timeLimit">How long the analysis may take before it counts as not answering.</param>
    public DismComponentStoreAnalysis(
        string executable = DefaultExecutable,
        string arguments = DefaultArguments,
        bool requiresAdministrator = true,
        Func<bool>? runningAsAdministrator = null,
        TimeSpan? timeLimit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);

        _executable = executable;
        _arguments = arguments;
        _requiresAdministrator = requiresAdministrator;
        _runningAsAdministrator = runningAsAdministrator;
        _timeLimit = timeLimit ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>Ask Windows how big its component store is. It reads; it changes nothing.</summary>
    public ComponentStoreQuestion Ask()
    {
        FileLog.Write($"[DismComponentStoreAnalysis] Ask: executable={_executable}, arguments={_arguments}");

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Windows component store analysis is a Windows command, and this machine is not running Windows.");
        }

        if (_requiresAdministrator && !RunningAsAdministrator())
        {
            FileLog.Write("[DismComponentStoreAnalysis] Ask done: not answered, the command needs an administrator");
            return ComponentStoreQuestion.Unanswered(
                "asking Windows how big the component store is needs an administrator, and this tool never " +
                "raises itself to one: run cc-cleanup-storage from a command prompt opened with Run as " +
                "administrator, or run Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore yourself");
        }

        Output output;
        try
        {
            output = RunTheCommand();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            FileLog.Write($"[DismComponentStoreAnalysis] Ask done: not answered, the command did not start: {ex.Message}");
            return ComponentStoreQuestion.Unanswered(
                $"the analysis command could not be started, so the size of the component store is not known: {ex.Message}");
        }

        if (!output.FinishedInTime)
        {
            FileLog.Write("[DismComponentStoreAnalysis] Ask done: not answered, the command ran out of time");
            return ComponentStoreQuestion.Unanswered(
                $"the analysis command did not finish within {_timeLimit.TotalMinutes.ToString(CultureInfo.InvariantCulture)} " +
                "minutes, so the size of the component store is not known");
        }

        if (output.ExitCode != 0)
        {
            FileLog.Write($"[DismComponentStoreAnalysis] Ask done: not answered, exit code {output.ExitCode}");
            return ComponentStoreQuestion.Unanswered(
                $"the analysis command ended with exit code {output.ExitCode.ToString(CultureInfo.InvariantCulture)} " +
                "rather than a report, so the size of the component store is not known");
        }

        var report = ParseReport(output.Text);
        if (report is null)
        {
            FileLog.Write("[DismComponentStoreAnalysis] Ask done: not answered, the report was not recognised");
            return ComponentStoreQuestion.Unanswered(
                "the analysis command answered, but its report did not contain the two sizes in a form this tool " +
                "recognises, so the size of the component store is not known rather than guessed");
        }

        FileLog.Write(
            $"[DismComponentStoreAnalysis] Ask done: actualSize={report.ActualSizeBytes}, canClear={report.BytesItsCommandCanClear}");
        return report;
    }

    /// <summary>
    /// Read the two sizes out of an analysis report. Null when the report does not carry both of
    /// them in a form this parser recognises, because a number this parser is not sure of is a
    /// guess and the rule says it does not know instead.
    /// </summary>
    /// <param name="text">The report, exactly as the command printed it.</param>
    internal static ComponentStoreQuestion? ParseReport(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        long? actualSize = null;
        long? canClear = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var split = line.Split(':', 2);
            if (split.Length != 2) continue;

            var label = split[0].Trim();
            if (label.Equals("Actual Size of Component Store", StringComparison.OrdinalIgnoreCase))
                actualSize = SizeInBytes(split[1].Trim());
            else if (label.Equals("Backups and Disabled Features", StringComparison.OrdinalIgnoreCase))
                canClear = SizeInBytes(split[1].Trim());
        }

        if (actualSize is null || canClear is null) return null;

        return new ComponentStoreQuestion(true, actualSize.Value, canClear.Value, null);
    }

    private static long? SizeInBytes(string text)
    {
        var pieces = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length != 2) return null;

        if (!double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;

        var multiplier = pieces[1].ToLowerInvariant() switch
        {
            "b" or "bytes" => 1L,
            "kb" => 1_024L,
            "mb" => 1_024L * 1_024,
            "gb" => 1_024L * 1_024 * 1_024,
            "tb" => 1_024L * 1_024 * 1_024 * 1_024,
            _ => -1L
        };
        if (multiplier < 0) return null;

        return (long)Math.Round(number * multiplier, MidpointRounding.AwayFromZero);
    }

    private bool RunningAsAdministrator()
    {
        if (_runningAsAdministrator is not null) return _runningAsAdministrator();

        // A machine that is not running Windows cannot be running as a Windows administrator, and
        // Ask has already refused non-Windows machines before this line can be reached.
        if (!OperatingSystem.IsWindows()) return false;

        return RunningAsAdministratorOnWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool RunningAsAdministratorOnWindows() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    [SupportedOSPlatform("windows")]
    private Output RunTheCommand()
    {
        var start = new ProcessStartInfo(_executable, _arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"The analysis command {_executable} could not be started.");

        // Both streams are read to their end before waiting, because a command that fills a pipe
        // nobody is reading waits forever, and a wait that hangs is not an answer.
        var text = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();

        var finished = process.WaitForExit((int)_timeLimit.TotalMilliseconds);
        if (!finished)
        {
            // This process is one this tool started itself, and it has run past the time it was
            // given. Everything this tool starts is an analysis that reads, so stopping it cannot
            // damage anything; leaving it running would leave this tool waiting on nothing.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It finished on its own in the moment between the wait and the stop.
            }

            return new Output(string.Empty, 0, FinishedInTime: false);
        }

        return new Output(text, process.ExitCode, FinishedInTime: true);
    }

    private sealed record Output(string Text, int ExitCode, bool FinishedInTime);
}
