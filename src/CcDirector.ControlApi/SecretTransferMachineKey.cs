using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// The Secret Handoff mission, phase 4: this machine's PUBLIC key for receiving a secret, read once from
/// <c>cc-secrets machine-key --json</c> and carried on every Hello as <c>SecretTransferPublicKey</c>.
///
/// It is read ONCE, off the desktop's thread, when the Director starts, and the answer is kept. Hello does not wait
/// for it: until the read has finished the key is empty, and the Hello every ten seconds carries it from then on. A
/// slow or hung cc-secrets therefore never holds up the roster.
///
/// EMPTY IS AN ANSWER, NOT A GUESS. When cc-secrets is not installed, fails, or is too old to have the
/// <c>machine-key</c> command, the key is empty and the reason is logged once. The Gateway then never lists this
/// machine as able to receive and never sends this Director a transfer - which is the truth: it cannot take part.
/// </summary>
internal sealed class SecretTransferMachineKey
{
    /// <summary>How long the one read may take before it is abandoned and the key stays empty.</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(60);

    private readonly ICcSecretsRunner _runner;
    private readonly TimeSpan _readTimeout;
    private readonly object _gate = new();
    private Task<string>? _read;

    public SecretTransferMachineKey() : this(new CcSecretsRunner(), DefaultReadTimeout)
    {
    }

    /// <param name="runner">Test seam: the process that stands in for cc-secrets.</param>
    /// <param name="readTimeout">Test seam: the limit on the one read.</param>
    internal SecretTransferMachineKey(ICcSecretsRunner runner, TimeSpan readTimeout)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _readTimeout = readTimeout;
    }

    /// <summary>Starts the one read on the thread pool. Idempotent: a second call does nothing.</summary>
    public Task<string> Start()
    {
        lock (_gate)
        {
            if (_read is null)
            {
                FileLog.Write("[SecretTransferMachineKey] Start: reading this machine's public key from cc-secrets");
                _read = Task.Run(ReadOnceAsync);
            }
            return _read;
        }
    }

    /// <summary>The key once the read has finished, or empty while it is still running, when it has not been started,
    /// or when this machine cannot take part. Never blocks.</summary>
    public string Current
    {
        get
        {
            Task<string>? read;
            lock (_gate) read = _read;
            return read is { IsCompletedSuccessfully: true } ? read.Result : "";
        }
    }

    /// <summary>Runs <c>cc-secrets machine-key --json</c> and returns its publicKey, or empty with the reason logged.
    /// Never throws: every way the read can fail ends in the same truthful answer, that this machine cannot receive.</summary>
    private async Task<string> ReadOnceAsync()
    {
        try
        {
            ProcessRunner.Result result;
            using (var limit = new CancellationTokenSource(_readTimeout))
            {
                try
                {
                    result = await _runner.RunAsync(new[] { "machine-key", "--json" }, standardInput: null, limit.Token);
                }
                catch (OperationCanceledException)
                {
                    return Empty($"cc-secrets machine-key did not answer within {_readTimeout.TotalSeconds:F0} seconds");
                }
            }

            if (!result.Started)
                return Empty("cc-secrets is not installed on this machine or could not be started");
            if (result.ExitCode != 0)
                return Empty($"cc-secrets machine-key failed (exit {result.ExitCode}); a cc-secrets older than the Secret Handoff has no machine-key command");

            var key = ReadPublicKey(result.StandardOutput);
            if (key is null)
                return Empty("cc-secrets machine-key answered without a 32-byte base64 publicKey");

            FileLog.Write("[SecretTransferMachineKey] read this machine's public key; this Director can take part in a secret transfer");
            return key;
        }
        catch (Exception ex)
        {
            // The boundary of a fire-and-forget read: nothing above it would ever observe the fault.
            return Empty($"reading the key FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Empty(string reason)
    {
        FileLog.Write($"[SecretTransferMachineKey] no public key, so this Director cannot take part in a secret transfer: {reason}");
        return "";
    }

    /// <summary>The publicKey field of the one JSON object machine-key prints, when it is base64 of exactly 32 bytes
    /// (an X25519 public key); null for anything else.</summary>
    internal static string? ReadPublicKey(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return null;
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("publicKey", out var field) || field.ValueKind != JsonValueKind.String) return null;
            var text = field.GetString()?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            var bytes = new byte[64];
            return Convert.TryFromBase64String(text, bytes, out var written) && written == 32 ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
