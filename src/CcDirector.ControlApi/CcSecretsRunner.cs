using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// The Secret Handoff mission: runs the machine's <c>cc-secrets</c> with the given arguments and, when given, writes
/// a text to its standard input. A seam, so the transfer verbs and the machine key reader are tested against a fake
/// process and never against a real secret store.
///
/// The runner itself never logs what goes in or comes out. What a transfer carries is a sealed secret, so the only
/// lines about a run are written by the caller, and they name the verb, the command id, the exit code and the ok
/// flag - nothing else.
/// </summary>
internal interface ICcSecretsRunner
{
    /// <summary>
    /// Runs cc-secrets. <see cref="ProcessRunner.Result.Started"/> is false when the tool is not installed or could not
    /// start. On cancellation the process tree is killed and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<ProcessRunner.Result> RunAsync(IReadOnlyList<string> args, string? standardInput, CancellationToken cancellationToken);
}

/// <summary>The production <see cref="ICcSecretsRunner"/>: the installed cc-secrets, found the way every cc tool is.</summary>
internal sealed class CcSecretsRunner : ICcSecretsRunner
{
    /// <summary>The command, as it is installed in the machine's tool directory and on PATH.</summary>
    public const string ToolName = "cc-secrets";

    public Task<ProcessRunner.Result> RunAsync(IReadOnlyList<string> args, string? standardInput, CancellationToken cancellationToken)
    {
        // No explicit-path override: a variable that could point the Director at another program is not something the
        // tool that receives a sealed secret should honour. The machine's tool directory, then PATH.
        var exe = CcToolExecutable.Resolve(ToolName, explicitPathEnvVar: null);
        if (exe is null)
            return Task.FromResult(new ProcessRunner.Result(-1, "", "", Started: false));
        return ProcessRunner.RunAsync(exe, args, workingDirectory: null, standardInput, cancellationToken);
    }
}
