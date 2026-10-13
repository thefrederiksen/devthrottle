using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching contract 8.3 and 8.4, the TARGET machine's side: signing an account in on this machine - a new
/// spare, or a renewal in place - when the Gateway asks (<c>license-sign-in</c>) or the daily renewal finds one with
/// no browser profile here.
///
/// The sign-in itself is the license command line's, and so is the choice of route: with a browser profile set for
/// the account on this machine the tool approves in it (the owner's setting here wins, contract 8.3) and no other
/// machine is asked. With none, the tool prints its authorize URL and waits; the URL goes up with
/// <c>RequestLicenseApproval</c>, which returns at once; the answer comes back as the <c>license-approval-result</c>
/// command, sealed to this machine; it is opened and typed into the waiting login. A login that hears nothing by its
/// deadline is ended with nothing written. While no Gateway can relay an approval the tool is not told to wait, so an
/// account with no profile here ends at once as <c>no-approver</c>.
///
/// One sign-in runs at a time on a Director. A sign-in never copies or moves a key: the tool writes it once, where it
/// belongs.
/// </summary>
public sealed class LicenseSignInCoordinator
{
    /// <summary>The sentence every sign-in is answered with while license switching is off on this Director.</summary>
    internal const string TurnedOffMessage = "License switching is turned off on this Director, so nothing was changed.";

    private readonly ILicenseCommandLine _tool;
    private readonly LicenseKeyHolderLock _holder;
    private readonly LicenseSignInResultStore _results;
    private readonly LicenseUsageReporter _reporter;
    private readonly LicenseApprovalExchange _approvals;
    private readonly Func<DateTime> _utcNow;
    private readonly bool _enabled;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private volatile ILicenseApprovalRelay? _relay;
    private volatile ILicenseApprovalSeal? _seal;

    /// <param name="delay">Test seam: how the wait for an approval's deadline passes.</param>
    public LicenseSignInCoordinator(ILicenseCommandLine tool, LicenseKeyHolderLock holder, LicenseSignInResultStore results,
        LicenseUsageReporter reporter, LicenseApprovalExchange approvals, Func<DateTime> utcNow, bool enabled,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _enabled = enabled;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The tunnel to ask for an approval on. Replaced when the Director's Gateway settings change; null while there is none.</summary>
    public void AttachRelay(ILicenseApprovalRelay? relay)
    {
        FileLog.Write($"[LicenseSignInCoordinator] AttachRelay: attached={relay is not null}");
        _relay = relay;
    }

    /// <summary>How a sealed approval is opened on this machine. Null until the Director's tunnel host supplies it.</summary>
    public void AttachSeal(ILicenseApprovalSeal? seal)
    {
        FileLog.Write($"[LicenseSignInCoordinator] AttachSeal: attached={seal is not null}");
        _seal = seal;
    }

    /// <summary>Carries out <paramref name="request"/>, whose SignInId has already been checked for shape.</summary>
    public async Task<LicenseSignInResult> SignInAsync(LicenseSignInRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = Normalize(request.AccountEmail);
        FileLog.Write($"[LicenseSignInCoordinator] SignInAsync: signInId={request.SignInId}, account={email}, reason={request.Reason}");

        // Off: nothing touches the key store. "Not the key holder" is literally true of a Director that never takes
        // the lock. Not stored: the same SignInId must run on a Director that has it on.
        if (!_enabled)
            return Build(request, LicenseSignInOutcomes.Failed, LicenseSwitchReasonCodes.NotKeyHolder, TurnedOffMessage, null);

        if (_results.Find(request.SignInId) is { } stored)
            return Replay(stored);

        // Not stored either: the results folder is shared by every Director on the machine, and the one that holds
        // the key must run this SignInId when it is sent there.
        if (!_holder.IsHeld)
            return Build(request, LicenseSignInOutcomes.Failed, LicenseSwitchReasonCodes.NotKeyHolder,
                LicenseRenewalSentences.For(LicenseSwitchReasonCodes.NotKeyHolder), null);

        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            // The same SignInId, sent again while the first was still running, answers what the first one did.
            if (_results.Find(request.SignInId) is { } finishedMeanwhile)
                return Replay(finishedMeanwhile);

            LicenseSignInAnswer answer;
            if (_utcNow() > request.NotAfterUtc)
            {
                answer = LicenseSignInAnswer.Failed(LicenseRenewalErrors.Expired);
            }
            else
            {
                answer = await RunAsync(request.SignInId, email, request.NotAfterUtc, profileHere: null, cancellationToken);
                _reporter.RecordRenewal(email, answer.Succeeded ? null : _utcNow(), answer.ReasonCode);
                // Section 8.4: a "changed" report after it, so the Gateway sees the new sign-in or the failure.
                _reporter.RequestReport(LicenseReportTriggers.Changed);
            }

            var result = Build(request, answer.Outcome, answer.ReasonCode, Sentence(answer, email), answer);
            _results.Save(result);
            FileLog.Write($"[LicenseSignInCoordinator] signInId={request.SignInId} finished: outcome={result.Outcome}, reason={result.ReasonCode ?? "-"}");
            return result;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// The daily renewal's sign-in for an account that is due: the same sign-in by the same route, under a SignInId
    /// minted here. The caller records the outcome and reports it. <paramref name="hasBrowserProfileHere"/> is
    /// what the renewal check already said of the account, so the tool is not asked again.
    /// </summary>
    public async Task<LicenseSignInAnswer> RenewAsync(string accountEmail, bool hasBrowserProfileHere, CancellationToken cancellationToken)
    {
        var email = Normalize(accountEmail);
        var signInId = Guid.NewGuid().ToString("N");
        FileLog.Write($"[LicenseSignInCoordinator] RenewAsync: signInId={signInId}, account={email}");
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var answer = await RunAsync(signInId, email, _utcNow() + LicenseContract.SignInNotAfter, hasBrowserProfileHere, cancellationToken);
            FileLog.Write($"[LicenseSignInCoordinator] signInId={signInId} finished: outcome={answer.Outcome}, reason={answer.ReasonCode ?? "-"}");
            return answer;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>One sign-in, by whichever route the tool chooses.</summary>
    /// <param name="profileHere">Whether a browser profile is set for the account on this machine, when the caller
    /// already knows; null to ask the tool if it matters.</param>
    private async Task<LicenseSignInAnswer> RunAsync(string signInId, string email, DateTime notAfterUtc, bool? profileHere, CancellationToken cancellationToken)
    {
        var relay = _relay;
        var seal = _seal;
        // The tool is told to wait for another machine's approval only while one can be asked for and opened here.
        var canAsk = relay is { CanRequest: true } && seal is not null;
        if (!canAsk)
        {
            // Only a profile on this machine could approve. With none set the sign-in cannot happen, and the tool is
            // not started to find that out: a sign-in that fails by design would file an error report every day.
            profileHere ??= await _tool.HasBrowserProfileAsync(email, cancellationToken);
            if (profileHere is null)
            {
                FileLog.Write($"[LicenseSignInCoordinator] signInId={signInId} FAILED: the tool did not say whether a browser profile is set here for {email}; no sign-in was started");
                return LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed);
            }
            if (profileHere == false)
            {
                FileLog.Write($"[LicenseSignInCoordinator] signInId={signInId}: no browser profile is set here for {email} and no Gateway relays an approval just now ({LicenseRenewalErrors.NoApprover}); no sign-in was started");
                return LicenseSignInAnswer.Failed(LicenseRenewalErrors.NoApprover);
            }
        }

        var startedUtc = _utcNow();
        var start = await _tool.StartSignInAsync(email, canAsk, cancellationToken);
        if (start.Waiting is null)
            return start.Finished ?? LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed);

        // Disposing a login that was not completed abandons it: nothing is written, and a new spare's empty folder goes.
        await using var waiting = start.Waiting;
        if (relay is null || seal is null || !canAsk)
            return LicenseSignInAnswer.Failed(LicenseRenewalErrors.NoApprover);
        // The approval must be in hand early enough for the login to finish inside the Gateway's own wait for this
        // sign-in (contract 8.5), and never later than the request allows.
        var approveBy = startedUtc + LicenseContract.SignInApprovalWait;
        if (notAfterUtc < approveBy) approveBy = notAfterUtc;
        var wait = approveBy - _utcNow();
        if (wait <= TimeSpan.Zero)
            return LicenseSignInAnswer.Failed(LicenseRenewalErrors.Expired);
        using var expected = _approvals.Expect(signInId);

        try
        {
            await relay.RequestAsync(new LicenseApprovalRequest
            {
                SignInId = signInId,
                AccountEmail = email,
                AuthorizeUrl = waiting.AuthorizeUrl,
                NotAfterUtc = approveBy,
            }, cancellationToken);
        }
        catch (LicenseApprovalRelayException ex)
        {
            FileLog.Write($"[LicenseSignInCoordinator] signInId={signInId} FAILED: the approval request did not reach the Gateway: {ex.Message}");
            return LicenseSignInAnswer.Failed(LicenseRenewalErrors.ApprovalFailed);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timedOut = _delay(wait, deadline.Token);
        var first = await Task.WhenAny(expected.Answer, timedOut);
        if (first != expected.Answer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileLog.Write($"[LicenseSignInCoordinator] signInId={signInId}: no approval by the deadline; the login is ended with nothing written");
            return LicenseSignInAnswer.Failed(LicenseRenewalErrors.Expired);
        }
        deadline.Cancel();

        var approval = await expected.Answer;
        if (approval.Outcome != LicenseApprovalOutcomes.Approved)
        {
            // Only the contract's words travel on; anything else could be anything.
            var reason = approval.ReasonCode is { } code && LicenseRenewalErrors.All.Contains(code) ? code : LicenseRenewalErrors.ApprovalFailed;
            FileLog.Write($"[LicenseSignInCoordinator] signInId={signInId}: the approval was refused ({reason})");
            return LicenseSignInAnswer.Failed(reason);
        }

        var opened = string.IsNullOrWhiteSpace(approval.SealedApproval)
            ? null
            : await seal.OpenAsync(approval.SealedApproval, signInId, cancellationToken);
        if (string.IsNullOrWhiteSpace(opened))
        {
            FileLog.Write($"[LicenseSignInCoordinator] signInId={signInId} FAILED: the sealed approval did not open on this machine for this sign-in");
            return LicenseSignInAnswer.Failed(LicenseRenewalErrors.ApprovalFailed);
        }

        return await waiting.CompleteAsync(opened, cancellationToken);
    }

    private static string? Sentence(LicenseSignInAnswer answer, string email) => answer.Outcome switch
    {
        LicenseSignInOutcomes.Added => $"{email} was signed in and is held as a spare.",
        LicenseSignInOutcomes.Renewed => $"The sign-in for {email} was renewed.",
        _ => LicenseRenewalSentences.For(answer.ReasonCode ?? LicenseRenewalErrors.LoginFailed),
    };

    private LicenseSignInResult Build(LicenseSignInRequest request, string outcome, string? reasonCode, string? message, LicenseSignInAnswer? answer) => new()
    {
        SignInId = request.SignInId,
        Outcome = outcome,
        ReasonCode = outcome == LicenseSignInOutcomes.Failed ? reasonCode ?? LicenseRenewalErrors.LoginFailed : null,
        Message = message,
        SignedInAtUtc = answer?.SignedInAtUtc,
        SignInExpiresAtUtc = answer?.SignInExpiresAtUtc,
        FinishedAtUtc = _utcNow(),
        Replayed = false,
    };

    private static LicenseSignInResult Replay(LicenseSignInResult stored)
    {
        FileLog.Write($"[LicenseSignInCoordinator] signInId={stored.SignInId} already finished ({stored.Outcome}), replaying");
        return new LicenseSignInResult
        {
            SignInId = stored.SignInId,
            Outcome = stored.Outcome,
            ReasonCode = stored.ReasonCode,
            Message = stored.Message,
            SignedInAtUtc = stored.SignedInAtUtc,
            SignInExpiresAtUtc = stored.SignInExpiresAtUtc,
            FinishedAtUtc = stored.FinishedAtUtc,
            Replayed = true,
        };
    }

    private static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();
}

/// <summary>
/// License Switching contract 8.3, the APPROVING machine's side: the <c>license-approve</c> command. This machine has
/// the browser profile signed in to claude.ai as the account; it approves the other machine's sign-in there and hands
/// the answer back sealed to that machine, so neither the Gateway nor either Director's log can read it.
///
/// The license command line refuses, before any browser opens, a URL that names another account than the request does
/// (review of 3875, round 2): a consent page shows only who the browser is signed in as.
/// </summary>
public sealed class LicenseApprover
{
    private readonly ILicenseCommandLine _tool;
    private readonly Func<DateTime> _utcNow;
    private readonly bool _enabled;
    private volatile ILicenseApprovalSeal? _seal;

    public LicenseApprover(ILicenseCommandLine tool, Func<DateTime> utcNow, bool enabled)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _enabled = enabled;
    }

    /// <summary>How an approval is sealed on this machine. Null until the Director's tunnel host supplies it.</summary>
    public void AttachSeal(ILicenseApprovalSeal? seal)
    {
        FileLog.Write($"[LicenseApprover] AttachSeal: attached={seal is not null}");
        _seal = seal;
    }

    /// <summary>Carries out <paramref name="request"/>, whose SignInId has already been checked for shape. Every
    /// outcome, a refusal included, is an answer.</summary>
    public async Task<LicenseApprovalResult> ApproveAsync(LicenseApproveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = (request.AccountEmail ?? "").Trim().ToLowerInvariant();
        FileLog.Write($"[LicenseApprover] ApproveAsync: signInId={request.SignInId}, account={email}");

        if (!_enabled)
            return Refused(request, LicenseRenewalErrors.NoApprover, "License switching is turned off on this Director, so nothing was approved.");
        var seal = _seal;
        if (seal is null || string.IsNullOrWhiteSpace(request.SealToRecipient))
            return Refused(request, LicenseRenewalErrors.ApprovalFailed, "The approval could not be sealed for the waiting machine, so nothing was approved.");
        if (_utcNow() > request.NotAfterUtc)
            return Refused(request, LicenseRenewalErrors.Expired, "The request arrived after its deadline, so nothing was approved.");
        if (string.IsNullOrWhiteSpace(request.BrowserProfile))
            return Refused(request, LicenseRenewalErrors.NoApprover, LicenseRenewalSentences.For(LicenseRenewalErrors.NoApprover));

        var answer = await _tool.ApproveAsync(request.AuthorizeUrl, email, request.BrowserProfile, cancellationToken);
        if (!answer.Approved || string.IsNullOrWhiteSpace(answer.Approval))
        {
            var reason = answer.ReasonCode ?? LicenseRenewalErrors.ApprovalFailed;
            return Refused(request, reason, LicenseRenewalSentences.For(reason));
        }

        // Sealed before it leaves this process (contract 8.3). It is in no log line and no field but SealedApproval.
        var sealedApproval = await seal.SealAsync(answer.Approval, request.SealToRecipient, request.SignInId, cancellationToken);
        if (string.IsNullOrWhiteSpace(sealedApproval))
            return Refused(request, LicenseRenewalErrors.ApprovalFailed, "The approval could not be sealed for the waiting machine; the sign-in must be started again.");

        FileLog.Write($"[LicenseApprover] signInId={request.SignInId} approved for {email}");
        return new LicenseApprovalResult
        {
            SignInId = request.SignInId,
            Outcome = LicenseApprovalOutcomes.Approved,
            Message = $"Approved for {email}.",
            SealedApproval = sealedApproval,
        };
    }

    private static LicenseApprovalResult Refused(LicenseApproveRequest request, string reasonCode, string? message)
    {
        FileLog.Write($"[LicenseApprover] signInId={request.SignInId} refused: {reasonCode}");
        return new LicenseApprovalResult
        {
            SignInId = request.SignInId,
            Outcome = LicenseApprovalOutcomes.Refused,
            ReasonCode = reasonCode,
            Message = message,
        };
    }
}
