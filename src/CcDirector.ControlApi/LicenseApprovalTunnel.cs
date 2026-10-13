using CcDirector.Core.Licenses;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>The License Switching mission, contract 8.3: the way up for an approval request - this Director's Gateway
/// tunnel. It asks only while the tunnel is connected and the Gateway has <c>RequestLicenseApproval</c>, so against a
/// Gateway built before the relay nothing is asked (contract 6.5, inert alone).</summary>
internal sealed class GatewayLicenseApprovalRelay : ILicenseApprovalRelay
{
    private readonly GatewayStreamClient _client;

    public GatewayLicenseApprovalRelay(GatewayStreamClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool CanRequest => _client.CanRequestLicenseApproval;

    public Task RequestAsync(LicenseApprovalRequest request, CancellationToken cancellationToken) =>
        _client.RequestLicenseApprovalAsync(request, cancellationToken);
}

/// <summary>
/// The License Switching mission, contract 8.3: sealing and opening an approval with the machine's <c>cc-secrets</c>
/// (<c>seal</c> and <c>open</c>). The sealing code lives in that one tool; the Director holds no copy of it.
///
/// THE APPROVAL GOES ON STANDARD INPUT AND COMES BACK ON STANDARD OUTPUT, NEVER IN THE ARGUMENTS. Only the recipient's
/// public half, the purpose word and the SignInId are arguments, and each is checked for its exact shape first,
/// because the installed tool is started through a command shim on Windows. Nothing that passes through is logged:
/// the lines name the step, the SignInId and the exit code.
/// </summary>
internal sealed class CcSecretsLicenseApprovalSeal : ILicenseApprovalSeal
{
    /// <summary>How long one cc-secrets run may take before the Director kills it.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(60);

    private readonly ICcSecretsRunner _runner;
    private readonly TimeSpan _callTimeout;

    public CcSecretsLicenseApprovalSeal() : this(new CcSecretsRunner(), DefaultCallTimeout)
    {
    }

    /// <param name="runner">Test seam: the process that stands in for cc-secrets.</param>
    /// <param name="callTimeout">Test seam: the limit on one run.</param>
    internal CcSecretsLicenseApprovalSeal(ICcSecretsRunner runner, TimeSpan callTimeout)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _callTimeout = callTimeout;
    }

    public async Task<string?> SealAsync(string approval, string recipient, string signInId, CancellationToken cancellationToken)
    {
        if (!IsSignInId(signInId) || !IsPublicHalf(recipient) || string.IsNullOrWhiteSpace(approval))
        {
            FileLog.Write($"[CcSecretsLicenseApprovalSeal] SealAsync: signInId={SafeId(signInId)} refused: the id or the recipient does not have its shape, or there is nothing to seal");
            return null;
        }
        var printed = await RunAsync("seal", signInId,
            new[] { "seal", "--to", recipient.Trim(), "--purpose", LicenseContract.ApprovalSealPurpose, "--id", signInId },
            approval, cancellationToken);
        // What comes back is base64 text only the recipient can open; anything else is not an envelope.
        return printed is not null && IsBase64(printed) ? printed : null;
    }

    public async Task<string?> OpenAsync(string sealedApproval, string signInId, CancellationToken cancellationToken)
    {
        if (!IsSignInId(signInId) || string.IsNullOrWhiteSpace(sealedApproval) || !IsBase64(sealedApproval.Trim()))
        {
            FileLog.Write($"[CcSecretsLicenseApprovalSeal] OpenAsync: signInId={SafeId(signInId)} refused: the id does not have its shape, or what arrived is not a sealed value");
            return null;
        }
        return await RunAsync("open", signInId,
            new[] { "open", "--purpose", LicenseContract.ApprovalSealPurpose, "--id", signInId },
            sealedApproval.Trim(), cancellationToken);
    }

    /// <summary>One run. The trimmed standard output on exit 0, else null with the step and exit code logged.</summary>
    private async Task<string?> RunAsync(string step, string signInId, IReadOnlyList<string> args, string standardInput, CancellationToken cancellationToken)
    {
        ProcessRunner.Result result;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(_callTimeout);
            try
            {
                result = await _runner.RunAsync(args, standardInput + "\n", limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                FileLog.Write($"[CcSecretsLicenseApprovalSeal] {step}: signInId={signInId} FAILED: cc-secrets did not finish within {_callTimeout.TotalSeconds:0} seconds, process killed");
                return null;
            }
        }

        if (!result.Started)
        {
            FileLog.Write($"[CcSecretsLicenseApprovalSeal] {step}: signInId={signInId} FAILED: cc-secrets could not be started (not installed, or not runnable)");
            return null;
        }
        var printed = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || printed.Length == 0)
        {
            FileLog.Write($"[CcSecretsLicenseApprovalSeal] {step}: signInId={signInId} FAILED: exit={result.ExitCode}; a cc-secrets older than the seal and open commands has neither");
            return null;
        }
        FileLog.Write($"[CcSecretsLicenseApprovalSeal] {step}: signInId={signInId}, exit=0");
        return printed;
    }

    private static bool IsSignInId(string? signInId) => signInId is not null && LicenseContract.SignInIdShape.IsMatch(signInId);

    private static string SafeId(string? signInId) => IsSignInId(signInId) ? signInId! : "(not an id)";

    /// <summary>Base64 of exactly 32 bytes: an X25519 public key, as the Director publishes it in Hello.</summary>
    internal static bool IsPublicHalf(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        var bytes = new byte[64];
        return Convert.TryFromBase64String(trimmed, bytes, out var written) && written == 32;
    }

    private static bool IsBase64(string text)
    {
        if (text.Length == 0 || text.Length % 4 != 0) return false;
        var bytes = new byte[text.Length];
        return Convert.TryFromBase64String(text, bytes, out _);
    }
}
