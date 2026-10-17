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
/// <see cref="Accept"/> checks the request and returns; everything else happens on a task of its own, and the answer
/// travels back as a command.
///
/// A REQUEST THAT IS NOT TAKEN IS REFUSED ON THE HUB CALL, AND HOLDS NOTHING. <see cref="Accept"/> throws - and the
/// Director's call fails, which ends its sign-in - for an identifier that is not one, an account that is not an
/// email, an address that is missing or too long, and a request past <see cref="MaxInFlightPerTenant"/>. Nothing is
/// kept for a refused request: no entry, no task, no command, and no line that quotes it. That is what makes the
/// limit a limit - a Director that asks in a loop costs the Gateway nothing for each request past it.
///
/// A REQUEST THAT IS TAKEN IS ALWAYS ANSWERED, and at once when nothing can be asked: <c>refused</c> with
/// <c>no-approver</c> when the owner set no approver for the account, with <c>approver-offline</c> when the approving
/// machine has never reported or its Director is not connected, with <c>expired</c> when the request arrived at or
/// after its own deadline - and with <c>approval-failed</c> when the relay itself failed, so the asking Director is
/// never left to wait out its own deadline for something that already went wrong here.
///
/// THE GATEWAY RELAYS AND NEVER KEEPS. The address the sign-in is approved at, and the approval that comes back sealed
/// to the asking machine, exist here only in the local variables of one relay. Neither is ever written to a store, a
/// row of the Licenses log or a log line, and no exception's message is logged, because a message could quote one.
///
/// WHAT IT DOES KEEP is one row of the Licenses log for each relay that is answered (<see cref="LicenseApprovalLog"/>):
/// the machine that asked, the account, the machine set to approve it, and approved or refused with the reason word -
/// so a renewal no person started still shows who approved it. The row is written after the answer is sent, so a
/// store that cannot take it never delays or loses the answer.
/// Every line this class writes names the sign-in's identifier, the account, the two machines and the outcome, and
/// nothing else - and the account is written only once it has been checked to be an email. The Gateway cannot open the approval: it is sealed to a key only the asking machine holds, the one
/// that machine's Director said in its Hello.
///
/// ONE DEVTHROTTLE ACCOUNT NEVER REACHES ANOTHER'S. The approver is looked up in the asking Director's own tenant, the
/// approving machine is looked up among that tenant's machines, and both commands are sent inside that tenant's scope,
/// where the host resolves a Director's connection for that tenant alone (contract section 6 rule 4).
/// </summary>
public sealed class LicenseApprovalRelay
{
    /// <summary>How many approvals one DevThrottle account may have in flight. Each holds a command open for up to
    /// <see cref="LicenseContract.ApproveCommandTimeout"/>; one past this is refused on the hub call and holds nothing.</summary>
    public const int MaxInFlightPerTenant = 8;

    /// <summary>The longest account email taken: the width of the column an account is stored in.</summary>
    public const int MaxAccountEmailLength = 320;

    /// <summary>The longest address a sign-in may be approved at. A real one is a few hundred characters.</summary>
    public const int MaxAuthorizeUrlLength = 4096;

    /// <summary>The longest sealed approval passed on. A real one is a few hundred characters.</summary>
    public const int MaxSealedApprovalLength = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LicenseApproverStore _approvers;
    private readonly LicenseReadModel _reads;
    private readonly LicenseApprovalLog _log;
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly LicenseCommandSender _send;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _lifetime;

    // The relays running now, by tenant and sign-in, and how many each tenant has. Both are read and changed only
    // under _gate, so the count a request is refused by is exact and costs the same however many are running.
    private readonly object _gate = new();
    private readonly Dictionary<(TenantId Tenant, string SignInId), Task> _inFlight = new();
    private readonly Dictionary<TenantId, int> _inFlightOf = new();

    /// <param name="approvers">Who approves each account.</param>
    /// <param name="reads">The tenant's machines and the Director that last reported for each (contract section 2.4).</param>
    /// <param name="log">Where each answered relay is recorded in the Licenses log.</param>
    /// <param name="isConnected">Whether that tenant's Director has an active stream connection now.</param>
    /// <param name="send">Sends one command and waits for its answer; called inside the tenant's scope.</param>
    /// <param name="enterScope">Enters a tenant's scope for the work of one relay.</param>
    /// <param name="nowUtc">The Gateway's clock.</param>
    /// <param name="lifetime">Cancelled when the Gateway stops.</param>
    internal LicenseApprovalRelay(LicenseApproverStore approvers, LicenseReadModel reads, LicenseApprovalLog log,
        Func<TenantId, string, bool> isConnected, LicenseCommandSender send, Func<TenantId, IDisposable> enterScope,
        Func<DateTime> nowUtc, CancellationToken lifetime)
    {
        _approvers = approvers ?? throw new ArgumentNullException(nameof(approvers));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _lifetime = lifetime;
    }

    /// <summary>
    /// Take one request from a bound Director and return at once. The relay runs on its own task and always answers
    /// the asking Director with <c>license-approval-result</c>. A request that is not taken is refused here, by
    /// throwing, and nothing is kept for it.
    /// </summary>
    /// <param name="tenant">The asking connection's bound tenant - never a value from the request.</param>
    /// <param name="targetDirectorId">The asking connection's bound Director.</param>
    /// <param name="sealToRecipient">The public half that Director said in its Hello for receiving a sealed value.</param>
    /// <exception cref="ArgumentException">The request is missing; its identifier is not a sign-in identifier; its
    /// account is not an email; or its address is missing or longer than <see cref="MaxAuthorizeUrlLength"/>.</exception>
    /// <exception cref="LicenseApprovalsBusyException">The account already has <see cref="MaxInFlightPerTenant"/>
    /// approvals in flight.</exception>
    public void Accept(TenantId tenant, string targetDirectorId, string? sealToRecipient, LicenseApprovalRequest? request)
    {
        // Nothing of the request is written to the log before it has been checked, and a refusal quotes none of it.
        if (request is null || request.SignInId is null || !LicenseContract.SignInIdShape.IsMatch(request.SignInId))
            throw NotTaken(targetDirectorId, "no request, or an identifier of the wrong shape",
                "The request's signInId must be 32 lower-case hexadecimal characters.");
        // The length is looked at before the text is copied: the hub takes a message of many megabytes.
        if (request.AccountEmail is not { Length: <= MaxAccountEmailLength + 64 } named
            || LicenseStore.NormalizeEmail(named) is not { } account || !LicenseApproverStore.IsAccountEmail(account))
            throw NotTaken(targetDirectorId, "the account is not an email",
                $"The request's accountEmail must be the email of a Claude account, at most {MaxAccountEmailLength} characters.");
        if (request.AuthorizeUrl is not { Length: > 0 and <= MaxAuthorizeUrlLength } address || string.IsNullOrWhiteSpace(address))
            throw NotTaken(targetDirectorId, "the address is missing or too long",
                $"The request's authorizeUrl must be the address to approve the sign-in at, at most {MaxAuthorizeUrlLength} characters.");

        var key = (tenant, request.SignInId);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_inFlight.ContainsKey(key))
            {
                FileLog.Write($"[LicenseApprovalRelay] Accept: signIn={request.SignInId} is already being relayed, the repeat was dropped: target={targetDirectorId}");
                return;
            }
            var running = _inFlightOf.GetValueOrDefault(tenant);
            if (running >= MaxInFlightPerTenant)
            {
                FileLog.Write($"[LicenseApprovalRelay] Accept: REFUSED, {running} approvals are already in flight for this account: signIn={request.SignInId}, target={targetDirectorId}");
                throw new LicenseApprovalsBusyException(
                    $"The Gateway is already relaying {MaxInFlightPerTenant} approvals for this account. Try the sign-in again when one has finished.");
            }
            _inFlight[key] = done.Task;
            _inFlightOf[tenant] = running + 1;
        }
        FileLog.Write($"[LicenseApprovalRelay] Accept: signIn={request.SignInId}, account={account}, target={targetDirectorId}");
        _ = Task.Run(async () =>
        {
            try
            {
                await RelayAsync(tenant, targetDirectorId, sealToRecipient, account, request).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight.Remove(key);
                    var left = _inFlightOf[tenant] - 1;
                    if (left == 0) _inFlightOf.Remove(tenant);
                    else _inFlightOf[tenant] = left;
                }
                done.SetResult();
            }
        });
    }

    private static ArgumentException NotTaken(string targetDirectorId, string why, string sentence)
    {
        FileLog.Write($"[LicenseApprovalRelay] Accept: REFUSED, {why}: target={targetDirectorId}");
        return new ArgumentException(sentence, "request");
    }

    /// <summary>How many relays this account has running now.</summary>
    internal int InFlightCount(TenantId tenant)
    {
        lock (_gate) return _inFlightOf.GetValueOrDefault(tenant);
    }

    /// <summary>How many relays are running now, over every account.</summary>
    internal int InFlightCount()
    {
        lock (_gate) return _inFlight.Count;
    }

    /// <summary>Completes when every relay accepted so far has answered. For tests, which wait on this and never on a clock.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate) return Task.WhenAll(_inFlight.Values.ToArray());
    }

    /// <summary>One relay, start to finish: decide the answer, then deliver it to the asking Director. The entry point
    /// of a background task, so nothing above it would see a failure: it never throws.</summary>
    /// <param name="account">The request's account, already checked to be an email.</param>
    internal async Task RelayAsync(TenantId tenant, string targetDirectorId, string? sealToRecipient, string account,
        LicenseApprovalRequest request)
    {
        var signInId = request.SignInId;
        IDisposable? scope = null;
        try
        {
            // Entered inside the try: a scope that cannot be entered is a failure this method logs, not one it lets go.
            scope = _enterScope(tenant);
            Decided decided;
            try
            {
                decided = await DecideAsync(tenant, account, sealToRecipient, request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                // The relay itself failed. The asking Director is still answered, now, so its sign-in ends with what
                // happened instead of waiting out its deadline. The TYPE only: a message could quote a payload.
                FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account} FAILED while relaying: {ex.GetType().Name}");
                decided = new Decided(Refuse(signInId, LicenseRenewalErrors.ApprovalFailed,
                    $"The Gateway failed while relaying this approval ({ex.GetType().Name})."), null);
            }
            var delivered = await _send(targetDirectorId, LicenseContract.ApprovalResultVerb, decided.Answer,
                LicenseContract.ApprovalResultCommandTimeout, "the machine that asked", _lifetime).ConfigureAwait(false);
            FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account}, target={targetDirectorId}, "
                          + $"approver={decided.ApproverMachine ?? "(none)"}, outcome={decided.Answer.Outcome}, reason={decided.Answer.ReasonCode ?? "-"}, "
                          + $"delivered={(delivered is null ? "no - the Director that asked is not connected" : delivered.Status.ToString())}");
            Record(tenant, targetDirectorId, signInId, account, decided);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account}: stopped, the Gateway is stopping");
        }
        catch (Exception ex)
        {
            // The scope could not be entered, or the answer could not be sent. The TYPE only: a message could quote a
            // command's payload or its answer.
            FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account} FAILED: {ex.GetType().Name}");
        }
        finally
        {
            scope?.Dispose();
        }
    }

    /// <summary>The Licenses log row for one answered relay, against the machine whose Director asked. A Director that
    /// has never reported for this account holds no sign-in the Gateway knows of, so it has no machine to record
    /// against: that is said in a line, and nothing is written.</summary>
    private void Record(TenantId tenant, string askingDirectorId, string signInId, string account, Decided decided)
    {
        var now = _nowUtc();
        var asking = _reads.Read(tenant, now).Machines.FirstOrDefault(m => string.Equals(m.DirectorId, askingDirectorId, StringComparison.Ordinal));
        if (asking is null)
        {
            FileLog.Write($"[LicenseApprovalRelay] signIn={signInId}, account={account}: not in the Licenses log, the Director that asked ({askingDirectorId}) has never reported for this account");
            return;
        }
        _log.Record(tenant, asking, signInId, account, decided.ApproverMachine, decided.Answer.Outcome, decided.Answer.ReasonCode, now);
    }

    /// <param name="ApproverMachine">The machine set to approve, for the log; null when none is set.</param>
    private sealed record Decided(LicenseApprovalResult Answer, string? ApproverMachine);

    private async Task<Decided> DecideAsync(TenantId tenant, string account, string? sealToRecipient, LicenseApprovalRequest request)
    {
        var signInId = request.SignInId;
        Decided Refused(string reason, string sentence, string? approverMachine = null)
            => new(Refuse(signInId, reason, sentence), approverMachine);

        if (!IsPublicHalf(sealToRecipient))
            return Refused(LicenseRenewalErrors.ApprovalFailed,
                "The Director that asked offered no key to seal an approval to, so no other machine can approve its sign-in.");

        var now = _nowUtc();
        var notAfter = Utc(request.NotAfterUtc);
        if (now >= notAfter)
            return Refused(LicenseRenewalErrors.Expired, "The request arrived at or after the sign-in's own deadline.");

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
        // The approving Director is waited for until the sooner of the Gateway's own wait and the sign-in's deadline:
        // an approval that comes back after the deadline completes nothing, so nothing is held open for it.
        var untilDeadline = notAfter - now;
        var endsAtDeadline = untilDeadline < LicenseContract.ApproveCommandTimeout;
        var wait = endsAtDeadline ? untilDeadline : LicenseContract.ApproveCommandTimeout;
        var result = await _send(machine.DirectorId, LicenseContract.ApproveVerb, approve, wait, machine.MachineName, _lifetime)
            .ConfigureAwait(false);
        return new Decided(AnswerOf(result, signInId, machine.MachineName, endsAtDeadline), machine.MachineName);
    }

    /// <summary>What the asking Director is told for each thing the send to the approving Director can return.</summary>
    /// <param name="endsAtDeadline">The wait given was the time left to the sign-in's own deadline.</param>
    private static LicenseApprovalResult AnswerOf(DirectorCommandResult? result, string signInId, string machineName, bool endsAtDeadline) => result switch
    {
        null => Refuse(signInId, LicenseRenewalErrors.ApproverOffline, $"The Director on {machineName} is not connected."),
        { Status: DirectorCommandStatus.Timeout } when endsAtDeadline => Refuse(signInId, LicenseRenewalErrors.Expired,
            $"No approval came back from the Director on {machineName} before the sign-in's own deadline."),
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

/// <summary>An approval was asked for while the account already has <see cref="LicenseApprovalRelay.MaxInFlightPerTenant"/>
/// in flight. The request was not taken and nothing is kept for it; its message quotes nothing of the request.</summary>
public sealed class LicenseApprovalsBusyException : InvalidOperationException
{
    public LicenseApprovalsBusyException(string message) : base(message)
    {
    }
}
