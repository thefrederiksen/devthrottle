using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching contract 8.3: the way a target Director asks the Gateway to have a sign-in approved on another
/// machine. <see cref="CanRequest"/> is false while the tunnel is down or the Gateway has no
/// <c>RequestLicenseApproval</c> - then nothing is asked, and a sign-in with no browser profile here stays
/// <c>no-approver</c>.
/// </summary>
public interface ILicenseApprovalRelay
{
    bool CanRequest { get; }

    /// <summary>Sends the request. It returns as soon as the Gateway has taken it; the answer arrives later as the
    /// <c>license-approval-result</c> command. Throws <see cref="LicenseApprovalRelayException"/> when it could not be sent.</summary>
    Task RequestAsync(LicenseApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>The approval request could not be handed to the Gateway.</summary>
public sealed class LicenseApprovalRelayException : Exception
{
    public LicenseApprovalRelayException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// License Switching contract 8.3: sealing the answer to an approved sign-in for the one machine that waits for it,
/// and opening it there. The sealing itself lives ONCE, in <c>cc-secrets seal</c> and <c>cc-secrets open</c>; this is
/// the seam the Director reaches it through, so every test runs against a fake.
/// </summary>
public interface ILicenseApprovalSeal
{
    /// <summary>The approval sealed to <paramref name="recipient"/> (the waiting Director's public half) for this
    /// sign-in, or null when it could not be sealed. Never logs the approval.</summary>
    Task<string?> SealAsync(string approval, string recipient, string signInId, CancellationToken cancellationToken);

    /// <summary>The approval inside <paramref name="sealedApproval"/>, or null when it does not open on this machine for
    /// this sign-in. Never logs what it opens.</summary>
    Task<string?> OpenAsync(string sealedApproval, string signInId, CancellationToken cancellationToken);
}

/// <summary>
/// License Switching contract 8.3: the answers this Director is waiting for, by SignInId. The request goes up as a hub
/// call that returns at once; the answer comes down later as the <c>license-approval-result</c> command, and this is
/// where the two meet. An answer nobody is waiting for - it arrived after the sign-in gave up, or names an id this
/// Director never asked about - is not delivered, and the caller logs it by id and drops it.
/// </summary>
public sealed class LicenseApprovalExchange
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LicenseApprovalResult>> _waiting = new(StringComparer.Ordinal);

    /// <summary>Starts waiting for the answer to <paramref name="signInId"/>. Dispose it to stop waiting.</summary>
    public Expectation Expect(string signInId)
    {
        if (signInId is null || !LicenseContract.SignInIdShape.IsMatch(signInId))
            throw new ArgumentException("a SignInId must be 32 lower-case hexadecimal characters", nameof(signInId));
        var source = new TaskCompletionSource<LicenseApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiting.TryAdd(signInId, source))
            throw new InvalidOperationException($"this Director is already waiting for the approval of sign-in {signInId}");
        return new Expectation(this, signInId, source);
    }

    /// <summary>Hands <paramref name="result"/> to the sign-in waiting for it. False when none is.</summary>
    public bool Deliver(LicenseApprovalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SignInId is null || !_waiting.TryRemove(result.SignInId, out var source))
            return false;
        FileLog.Write($"[LicenseApprovalExchange] Deliver: signInId={result.SignInId}, outcome={result.Outcome}, reason={result.ReasonCode ?? "-"}");
        return source.TrySetResult(result);
    }

    /// <summary>One sign-in's wait for its approval.</summary>
    public sealed class Expectation : IDisposable
    {
        private readonly LicenseApprovalExchange _owner;
        private readonly string _signInId;
        private readonly TaskCompletionSource<LicenseApprovalResult> _source;

        internal Expectation(LicenseApprovalExchange owner, string signInId, TaskCompletionSource<LicenseApprovalResult> source)
        {
            _owner = owner;
            _signInId = signInId;
            _source = source;
        }

        public Task<LicenseApprovalResult> Answer => _source.Task;

        public void Dispose() => _owner._waiting.TryRemove(new KeyValuePair<string, TaskCompletionSource<LicenseApprovalResult>>(_signInId, _source));
    }
}

/// <summary>
/// The sentence that goes with a renewal or sign-in failure. Written here, never taken from a tool's or a page's own
/// words: the contract (rule 6.1, section 8.1) lets no detail quote a process's output.
/// </summary>
public static class LicenseRenewalSentences
{
    public static string? For(string? error) => error switch
    {
        null => null,
        LicenseRenewalErrors.LoginFailed => "The sign-in could not be started or did not finish on this machine.",
        LicenseRenewalErrors.ApprovalFailed => "The approval in the browser did not complete.",
        LicenseRenewalErrors.WrongAccount => "The browser profile that approves this account is signed in to claude.ai as another account.",
        LicenseRenewalErrors.BrowserNotSignedIn => "The browser profile that approves this account is not signed in to claude.ai; sign it in by hand.",
        LicenseRenewalErrors.NeedsFreshSignIn => "The browser profile that approves this account is signed in to claude.ai, but the approval page asks for a fresh sign-in; sign it in again by hand.",
        LicenseRenewalErrors.NoApprover => "No browser profile is set to approve this account, on this machine or another.",
        LicenseRenewalErrors.ApproverOffline => "The machine that approves this account has no Director connected.",
        LicenseRenewalErrors.Expired => "No approval came back from the other machine in time.",
        LicenseRenewalErrors.StoreRefused => "The key store would not take the new sign-in just then; it is tried again at the next run.",
        LicenseSwitchReasonCodes.NotKeyHolder => "This Director is not the one that holds this machine's sign-ins.",
        _ => "The sign-in failed for a reason this Director does not recognise.",
    };
}
