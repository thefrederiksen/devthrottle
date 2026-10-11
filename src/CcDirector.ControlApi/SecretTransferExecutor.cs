using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The Secret Handoff mission, phase 4: the SECRET TRANSFER area of the Director's tunnel command surface. Two verbs,
/// neither addressed to a session:
///
///   - <c>secret-transfer-send</c> goes to the Director on the machine holding the secret. It runs
///     <c>cc-secrets transfer-send &lt;transferId&gt;</c>, which seals the entry to the receiving machine's key.
///   - <c>secret-transfer-receive</c> goes to the Director on the receiving machine. It runs
///     <c>cc-secrets transfer-receive &lt;transferId&gt;</c>, which opens the envelope and stores the entry.
///
/// THE PAYLOAD GOES ON STANDARD INPUT, NEVER IN THE ARGUMENTS OR THE ENVIRONMENT. Any process on the machine can read
/// another's argument list, and the receive payload carries the sealed envelope. Only the transfer id - not secret,
/// and checked to be letters, digits and hyphens before it goes anywhere near a command line - is an argument.
///
/// NOTHING THAT PASSES THROUGH IS EVER LOGGED OR RETURNED IN AN ERROR: not the payload, not cc-secrets' standard
/// output, not its standard error. The log lines name the verb, the command id, the exit code and the ok flag. On
/// success the body is cc-secrets' one JSON line, verbatim, because that line is the answer the Gateway asked for
/// (an envelope sealed to the receiver, or "stored NAME"). A failure says only which command failed and its exit
/// code, because cc-secrets' own error text could quote what it was handed.
///
/// A REFUSAL IS A SUCCESSFUL ANSWER. cc-secrets saying <c>{ok: false, reason}</c> (exit 2) is the tool doing its job,
/// so it travels as Success with that line as the body, the same as <c>{ok: true}</c>. Only an answer that is not one
/// JSON object with an <c>ok</c> field - a crash, a missing tool, a timeout - is a failure.
/// </summary>
internal sealed class SecretTransferExecutor : ISessionCommandArea
{
    /// <summary>The verb sent to the Director on the machine holding the secret.</summary>
    public const string SendVerb = "secret-transfer-send";

    /// <summary>The verb sent to the Director on the machine receiving the secret.</summary>
    public const string ReceiveVerb = "secret-transfer-receive";

    /// <summary>How long one cc-secrets run may take before the Director kills it and reports a failure.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>What a transfer id may be: it is a command-line argument, and the installed tool is started through a
    /// command shim on Windows, so anything a shell could read as an operator or an option is refused.</summary>
    private static readonly Regex TransferIdShape = new("^[A-Za-z0-9][A-Za-z0-9-]{0,127}$", RegexOptions.CultureInvariant);

    private readonly ICcSecretsRunner _runner;
    private readonly TimeSpan _callTimeout;

    public SecretTransferExecutor() : this(new CcSecretsRunner(), DefaultCallTimeout)
    {
    }

    /// <param name="runner">Test seam: the process that stands in for cc-secrets.</param>
    /// <param name="callTimeout">Test seam: the limit on one run.</param>
    internal SecretTransferExecutor(ICcSecretsRunner runner, TimeSpan callTimeout)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _callTimeout = callTimeout;
    }

    public IReadOnlyCollection<string> Verbs { get; } = new[] { SendVerb, ReceiveVerb };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return command.Verb switch
        {
            SendVerb => RunAsync(command, "transfer-send", cancellationToken),
            ReceiveVerb => RunAsync(command, "transfer-receive", cancellationToken),
            _ => Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"verb '{command.Verb}' is not handled by the secret transfer area")),
        };
    }

    /// <summary>
    /// Runs one cc-secrets transfer command with the payload on standard input and maps its answer. Never logs or
    /// returns anything that passed through the process except, on success, its one JSON line as the body.
    /// </summary>
    internal async Task<DirectorCommandResult> RunAsync(DirectorCommand command, string subcommand, CancellationToken cancellationToken)
    {
        FileLog.Write($"[SecretTransferExecutor] {command.Verb}: cmdId={command.CommandId}");

        var transferId = ReadTransferId(command.PayloadJson);
        if (transferId is null)
        {
            FileLog.Write($"[SecretTransferExecutor] {command.Verb}: cmdId={command.CommandId} refused: the payload carries no usable transferId");
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                "the payload must be a JSON object whose transferId is letters, digits and hyphens");
        }

        ProcessRunner.Result result;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(_callTimeout);
            try
            {
                result = await _runner.RunAsync(new[] { subcommand, transferId }, command.PayloadJson, limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                FileLog.Write($"[SecretTransferExecutor] {command.Verb}: cmdId={command.CommandId} FAILED: timed out after {Seconds(_callTimeout)}s, process killed");
                return DirectorCommandResult.Fail(DirectorCommandStatus.Error,
                    $"cc-secrets {subcommand} did not finish within {Seconds(_callTimeout)} seconds.");
            }
        }

        if (!result.Started)
        {
            FileLog.Write($"[SecretTransferExecutor] {command.Verb}: cmdId={command.CommandId} FAILED: cc-secrets could not be started (not installed, or not runnable)");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error,
                $"cc-secrets could not be started on this machine, so {subcommand} did not run.");
        }

        var ok = ReadOkFlag(result.StandardOutput);
        if (ok is null)
        {
            FileLog.Write($"[SecretTransferExecutor] {command.Verb}: cmdId={command.CommandId}, exit={result.ExitCode} FAILED: the answer was not one JSON object with an ok field");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error,
                $"cc-secrets {subcommand} failed (exit {result.ExitCode}).");
        }

        FileLog.Write($"[SecretTransferExecutor] {command.Verb}: cmdId={command.CommandId}, exit={result.ExitCode}, ok={(ok.Value ? "true" : "false")}");
        return DirectorCommandResult.Success(result.StandardOutput.Trim());
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The payload's transferId when it is a safe argument; null otherwise. Never says why in words that
    /// could carry the payload.</summary>
    internal static string? ReadTransferId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("transferId", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var text = id.GetString();
            return text is not null && TransferIdShape.IsMatch(text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The ok flag of cc-secrets' answer when standard output is exactly one JSON object with a boolean <c>ok</c>
    /// field; null for anything else (empty, a traceback, two objects, an object without the field).
    /// </summary>
    internal static bool? ReadOkFlag(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return null;
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("ok", out var ok)) return null;
            return ok.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
