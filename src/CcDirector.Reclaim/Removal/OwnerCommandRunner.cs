using System.Globalization;
using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Removal;

/// <summary>What running an owner's own cleanup command did.</summary>
public sealed record OwnerCommandOutcome
{
    /// <summary>True when the command was started and finished.</summary>
    public required bool Ran { get; init; }

    /// <summary>The command's exit code, when it ran.</summary>
    public required int? ExitCode { get; init; }

    /// <summary>Why the command could not be run at all, or null when it ran.</summary>
    public required string? RefusalReason { get; init; }

    /// <summary>What the command wrote to its output, truncated, or the empty string.</summary>
    public required string Output { get; init; }
}

/// <summary>
/// The machinery that runs an owner's own cleanup command, for a rule whose proof is that the owner
/// of the data has one.
///
/// The command is the rule's own, printed by its recommendation and run verbatim here; this tool
/// never composes one and never runs somebody else's command behind the reader's back. It has no
/// timeout: a cleanup interrupted mid-write is worse than a cleanup that takes its time, and speed
/// is not a constraint of this mission. What is reported is what happened - the exit code and the
/// measured bytes before and after - never an estimate presented as a fact.
///
/// No test ever points this at a real package manager. The tests supply a harmless command that
/// writes one marker file inside the fixture tree.
/// </summary>
public sealed class OwnerCommandRunner
{
    /// <summary>
    /// Run one command and report exactly what happened.
    /// </summary>
    /// <param name="command">The command, as the rule prints it: a program and its arguments.</param>
    /// <param name="workingDirectory">Where the command runs.</param>
    public OwnerCommandOutcome Run(string command, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        FileLog.Write($"[OwnerCommandRunner] Run: command={command}, workingDirectory={workingDirectory}");

        var split = SplitProgramFromArguments(command);
        if (split is null)
        {
            FileLog.Write($"[OwnerCommandRunner] Run refused: the command {command} has no program in it");
            return new OwnerCommandOutcome
            {
                Ran = false,
                ExitCode = null,
                RefusalReason = $"the command {command} has no program in it, so there is nothing to run",
                Output = string.Empty
            };
        }

        var (program, arguments) = split.Value;
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = program,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("The operating system started no process.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FileLog.Write($"[OwnerCommandRunner] Run FAILED: the command {command} could not be started, code={ex.HResult}");
            return new OwnerCommandOutcome
            {
                Ran = false,
                ExitCode = null,
                RefusalReason = $"the command {command} could not be started: {ex.Message}",
                Output = string.Empty
            };
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var code = process.ExitCode;
            var said = Truncate((output + " " + error).Trim());

            FileLog.Write($"[OwnerCommandRunner] Run done: command={command}, exitCode={code}");
            return new OwnerCommandOutcome
            {
                Ran = true,
                ExitCode = code,
                RefusalReason = null,
                Output = said
            };
        }
    }

    /// <summary>
    /// Split a command line into its program and its arguments, honouring double quotes: a path with
    /// spaces is one program, not three words. Everything after the program is passed to it as it was
    /// spelled, quotes included.
    /// </summary>
    private static (string Program, string Arguments)? SplitProgramFromArguments(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing < 0) return (trimmed[1..], string.Empty);
            var program = trimmed[1..closing];
            var arguments = trimmed[(closing + 1)..].Trim();
            return (program, arguments);
        }

        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0) return (trimmed, string.Empty);
        return (trimmed[..firstSpace], trimmed[(firstSpace + 1)..].Trim());
    }

    private static string Truncate(string text) =>
        text.Length <= 200 ? text : text[..200] + " (truncated)";
}
