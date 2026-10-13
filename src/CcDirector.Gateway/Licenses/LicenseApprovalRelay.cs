using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE APPROVAL RELAY (License Switching, contract section 8.3). A machine that needs a sign-in approved and has no
/// browser profile of its own for the account asks through <c>DirectorHub.RequestLicenseApproval</c>. This class finds
/// the machine the OWNER set to approve that account, sends that machine's Director <c>license-approve</c>, and sends
/// what comes back to the asking Director as <c>license-approval-result</c>.
///
/// NOTHING WAITS ON THE HUB CALL. The hub runs one invocation at a time for each Director, so a call that waited the
/// minutes an approval takes would hold up that Director's usage reports and every session push behind it.
/// <see cref="Accept"/> checks only the request's identifier and returns; everything else happens on a task of its own,
/// and the answer travels back as a command.
///
/// THE ASKING DIRECTOR IS ALWAYS ANSWERED, and at once when nothing can be asked: <c>refused</c> with
/// <c>no-approver</c> when the owner set no approver for the account, with <c>approver-offline</c> when the approving
/// machine has never reported or its Director is not connected, with <c>expired</c> when the request arrived after its
/// own deadline. Only a request whose identifier is not an identifier is refused on the hub call itself, because an
/// answer to it could not be addressed.
///
/// THE GATEWAY RELAYS AND NEVER KEEPS. The address the sign-in is approved at, and the approval that comes back sealed
/// to the asking machine, exist here only in the local variables of one relay. Neither is ever written to a store, a
/// row of the Licenses log or a log line, and no exception's message is logged, because a message could quote one.
/// Every line this class writes names the sign-in's identifier, the account, the two machines and the outcome, and
/// nothing else. The Gateway cannot open the approval: it is sealed to a key only the asking machine holds, the one
/// that machine's Director said in its Hello.
///
/// ONE DEVTHROTTLE ACCOUNT NEVER REACHES ANOTHER'S. The approver is looked up in the asking Director's own tenant, the
/// approving machine is looked up among that tenant's machines, and both commands are sent inside that tenant's scope,
/// where the host resolves a Director's connection for that tenant alone (contract section 6 rule 4).
/// </summary>
public sealed class LicenseApprovalRelay
{
    /// <summary>How many approvals one DevThrottle account may have in flight. Each holds a command open for up to
    /// <see cref="LicenseContract.ApproveCommandTimeout"/>; one past this is refused at once rather than queued.</summary>
    public const int MaxInFlightPerTenant = 8;

    /// <summary>The longest address a sign-in may be approved at. A real one is a few hundred characters.</summary>
    public const int MaxAuthorizeUrlLength = 4096;

    /// <summary>The longest sealed approval passed on. A real one is a few hundred characters.</summary>
    public const int MaxSealedApprovalLength = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LicenseApproverStore _approvers;
    private readonly LicenseReadModel _reads;
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly LicenseCommandSender _send;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _lifetime;

    // The relays running now, by tenant and sign-in: the same request arriving again while its relay runs is dropped.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SignInId), Task> _inFlight = new();

    /// <param name="approvers">Who approves each account.</param>
    /// <param name="reads">The tenant's machines and the Director that last reported for each (contract section 2.4).</param>
    /// <param name="isConnected">Whether that tenant's Director has an active stream connection now.</param>
    /// <param name="send">Sends one command and waits for its answer; called inside the tenant's scope.</param>
    /// <param name="enterScope">Enters a tenant's scope for the work of one relay.</param>
    /// <param name="nowUtc">The Gateway's clock.</param>
    /// <param name="lifetime">Cancelled when the Gateway stops.</param>
    internal LicenseApprovalRelay(LicenseApproverStore approvers, LicenseReadModel reads, Func<TenantId, string, bool> isConnected,
        LicenseCommandSender send, Func<TenantId, IDisposable> enterScope, Func<DateTime> nowUtc, CancellationToken lifetime)
    {
        _approvers = approvers ?? throw new ArgumentNullException(nameof(approvers));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _lifetime = lifetime;
    }

    /// <summary>
    /// Take one request from a bound Director and return at once. The relay runs on its own task and always answers
    /// the asking Director with <c>license-approval-result</c>.
    /// </summary>
    /// <param name="tenant">The asking connection's bound tenant - never a value from the request.</param>
    /// <param name="targetDirectorId">The asking connection's bound Director.</param>
    /// <param name="sealToRecipient">The public half that Director said in its Hello for receiving a sealed value.</param>
    /// <exception cref="ArgumentException">The request is missing, or its identifier is not a sign-in identifier.</exception>
    public void Accept(TenantId tenant, string targetDirectorId, string? sealToRecipient, LicenseApprovalRequest? request)
    {
        if (request is null || request.SignInId is null || !LicenseContract.SignInIdShape.IsMatch(request.SignInId))
        {
            FileLog.Write($"[LicenseApprovalRelay] Accept: REFUSED, no request or an identifier of the wrong shape: target={targetDirectorId}");
            throw new ArgumentException("The request's signInId must be 32 lower-case hexadecimal characters.", nameof(request));
        }

        var key = (tenant, request.SignInId);
        var overLimit = _inFlight.Keys.Count(k => k.Tenant.Equals(tenant)) >= MaxInFlightPerTenant;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(key, done.Task))
        {
            FileLog.Write($"[LicenseApprovalRelay] Accept: signIn={request.SignInId} is already being relayed, the repeat was dropped: target={targetDirectorId}");
            return;
        }
        FileLog.Write($"[LicenseApprovalRelay] Accept: signIn={request.SignInId}, account={LicenseStore.NormalizeEmail(request.AccountEmail)}, target={targetDirectorId}");
        _ = Task.Run(async () =>
        {
            try
            {
                await RelayAsync(tenant, targetDirectorId, sealToRecipient, request, overLimit).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
                done.SetResult();
            }
        });
    }

    /// <summary>Completes when every relay accepted so far has answered. For tests, which wait on this and never on a clock.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values.ToArray());

    /// <summary>One relay, start to finish: decide the answer, then deliver it to the asking Director. The entry point
    /// of a background task, so nothing above it would see a failure: it never throws.</summary>
    internal async Task RelayAsync(TenantId tenant, string targetDirectorId, string? sealToRecipient, LicenseApprovalRequest request,
        bool overLimit)
    {
        var signInId = request.SignInId;
        var account = LicenseStore.NormalizeEmail(request.AccountEmail);
        using var scope = _enterScope(tenant);
        try
        {
            var decided = await DecideAsync(tenant, account, sealToRecipient, request, overLimit).ConfigureAwait(false);
            var delivered = await _send(targetDirectorId, LicenseContract.ApprovalResultVerb, decided.Answer,
                LicenseContract.ApprovalResultCommandTimeout, "the machine that asked", _lifetime).ConfigureAwait(false);
            FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account}, target={targetDirectorId}, "
                          + $"approver={decided.ApproverMachine ?? "(none)"}, outcome={decided.Answer.Outcome}, reason={decided.Answer.ReasonCode ?? "-"}, "
                          + $"delivered={(delivered is null ? "no - the Director that asked is not connected" : delivered.Status.ToString())}");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account}: stopped, the Gateway is stopping");
        }
        catch (Exception ex)
        {
            // The TYPE only: a message could quote a command's payload or its answer.
            FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account} FAILED: {ex.GetType().Name}");
        }
    }

    /// <param name="ApproverMachine">The machine set to approve, for the log; null when none is set.</param>
    private sealed record Decided(LicenseApprovalResult Answer, string? ApproverMachine);

    private async Task<Decided> DecideAsync(TenantId tenant, string account, string? sealToRecipient, LicenseApprovalRequest request,
        bool overLimit)
    {
        var signInId = request.SignInId;
        Decided Refused(string reason, string sentence, string? approverMachine = null)
            => new(Refuse(signInId, reason, sentence), approverMachine);

        if (!LicenseApproverStore.IsAccountEmail(account) || string.IsNullOrWhiteSpace(request.AuthorizeUrl)
            || request.AuthorizeUrl.Length > MaxAuthorizeUrlLength)
            return Refused(LicenseRenewalErrors.ApprovalFailed, "The request named no account, or carried no address to approve the sign-in at.");
        if (overLimit)
            return Refused(LicenseRenewalErrors.ApprovalFailed,
                $"The Gateway is already relaying {MaxInFlightPerTenant} approvals for this account. Try the sign-in again when one has finished.");
        if (!IsPublicHalf(sealToRecipient))
            return Refused(LicenseRenewalErrors.ApprovalFailed,
                "The Director that asked offered no key to seal an approval to, so no other machine can approve its sign-in.");

        var now = _nowUtc();
        var notAfter = Utc(request.NotAfterUtc);
        if (now > notAfter)
            return Refused(LicenseRenewalErrors.Expired, "The request arrived after the sign-in's own deadline.");

        var approver = _approvers.Find(tenant, account);
        if (approver is null)
            return Refused(LicenseRenewalErrors.NoApprover, $"No machine is set to approve sign-ins for {account}.");

        // The machines of THIS tenant only, each with the Director that sent its last accepted report (section 2.4).
        var machines = _reads.Read(tenant, now).Machines;
        var machine = machines.FirstOrDefault(m => string.Equals(m.MachineName, approver.MachineName, StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(m.OsUser, approver.OsUser, StringComparison.OrdinalIgnoreCase));
        if (machine is null)
            return Refused(LicenseRenewalErrors.ApproverOffline,
                $"{approver.MachineName} is set to approve sign-ins for {account}, and no Director on it has reported to this account.",
                approver.MachineName);
        if (!_isConnected(tenant, machine.DirectorId))
            return Refused(LicenseRenewalErrors.ApproverOffline,
                $"{machine.MachineName} is set to approve sign-ins for {account}, and its Director is not connected.", machine.MachineName);

        var approve = new LicenseApproveRequest
        {
            SignInId = signInId,
            AccountEmail = account,
            AuthorizeUrl = request.AuthorizeUrl,
            BrowserProfile = approver.BrowserProfile,
            SealToRecipient = sealToRecipient!.Trim(),
            NotAfterUtc = notAfter,
        };
        var result = await _send(machine.DirectorId, LicenseContract.ApproveVerb, approve, LicenseContract.ApproveCommandTimeout,
            machine.MachineName, _lifetime).ConfigureAwait(false);
        return new Decided(AnswerOf(result, signInId, machine.MachineName), machine.MachineName);
    }

    /// <summary>What the asking Director is told for each thing the send to the approving Director can return.</summary>
    private static LicenseApprovalResult AnswerOf(DirectorCommandResult? result, string signInId, string machineName) => result switch
    {
        null => Refuse(signInId, LicenseRenewalErrors.ApproverOffline, $"The Director on {machineName} is not connected."),
        { Status: DirectorCommandStatus.Timeout } => Refuse(signInId, LicenseRenewalErrors.ApprovalFailed,
            $"The Director on {machineName} did not answer within {LicenseContract.ApproveCommandTimeout.TotalSeconds:0} seconds."),
        { Status: DirectorCommandStatus.TunnelDropped } => Refuse(signInId, LicenseRenewalErrors.ApproverOffline,
            $"The connection to the Director on {machineName} dropped while it was approving."),
        { Status: DirectorCommandStatus.Ok } => ReadApproval(result, signInId)
            ?? Refuse(signInId, LicenseRenewalErrors.ApprovalFailed,
                $"The Director on {machineName} answered, but its answer could not be read as the result of this approval."),
        _ => Refuse(signInId, LicenseRenewalErrors.ApprovalFailed, $"The Director on {machineName} could not run the approval ({result.Status})."),
    };

    /// <summary>
    /// The approving Director's answer as it is passed on, or null when the body is not the result of THIS sign-in with
    /// an outcome this Gateway knows. An approval is passed on only with its sealed value; a refusal is passed on with
    /// no sealed value at all, and with a reason only from the contract's own words.
    /// </summary>
    private static LicenseApprovalResult? ReadApproval(DirectorCommandResult result, string signInId)
    {
        if (string.IsNullOrWhiteSpace(result.BodyJson))
            return null;
        LicenseApprovalResult? body;
        try
        {
            body = JsonSerializer.Deserialize<LicenseApprovalResult>(result.BodyJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (body is null || !string.Equals(body.SignInId, signInId, StringComparison.Ordinal))
            return null;
        var message = Sentence(body.Message);
        if (body.Outcome == LicenseApprovalOutcomes.Approved)
        {
            var sealedApproval = body.SealedApproval?.Trim();
            return string.IsNullOrEmpty(sealedApproval) || sealedApproval.Length > MaxSealedApprovalLength
                ? null
                : new LicenseApprovalResult { SignInId = signInId, Outcome = LicenseApprovalOutcomes.Approved, Message = message, SealedApproval = sealedApproval };
        }
        if (body.Outcome != LicenseApprovalOutcomes.Refused)
            return null;
        var reason = body.ReasonCode is { } word && LicenseRenewalErrors.All.Contains(word) ? word : LicenseRenewalErrors.ApprovalFailed;
        return Refuse(signInId, reason, message ?? "The approving machine refused the sign-in.");
    }

    private static LicenseApprovalResult Refuse(string signInId, string reasonCode, string sentence) => new()
    {
        SignInId = signInId,
        Outcome = LicenseApprovalOutcomes.Refused,
        ReasonCode = reasonCode,
        Message = sentence,
    };

    private static string? Sentence(string? message)
    {
        var text = message?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > LicenseStore.MaxMessageLength ? text[..LicenseStore.MaxMessageLength] : text;
    }

    /// <summary>Base64 of exactly 32 bytes: the public half a Director says in its Hello for receiving a sealed value.</summary>
    internal static bool IsPublicHalf(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 64)
            return false;
        var bytes = new byte[64];
        return Convert.TryFromBase64String(trimmed, bytes, out var written) && written == 32;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
