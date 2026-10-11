using System.Diagnostics;
using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching contract 3.3, steps 2 to 12: carrying out one <c>license-switch</c> on this machine. Step 1 -
/// an unreadable payload or a SwitchId of the wrong shape - is the tunnel verb's, and has happened before this class
/// sees the request.
///
/// The exchange itself is the license command line's (<see cref="ILicenseCommandLine.SwitchAsync"/>): it moves the
/// active sign-in and its oauthAccount block to the spare's place and the spare's into the default config, as one
/// step that completes or is undone. This class decides WHETHER to run it, keeps one switch at a time, makes a
/// repeated SwitchId answer from the stored result instead of running again, and reads the result back.
///
/// It types nothing into any session (contract 6.3): it touches the key store through the tool, and nothing else.
/// </summary>
public sealed class LicenseSwitchCoordinator
{
    private readonly ILicenseCommandLine _tool;
    private readonly LicenseKeyHolderLock _holder;
    private readonly LicenseSwitchResultStore _results;
    private readonly LicenseUsageReporter _reporter;
    private readonly Func<DateTime> _utcNow;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private (string SwitchId, Task<LicenseSwitchResult> Run)? _running;

    /// <summary>The active account the last successful listing named; null before the first one.</summary>
    private volatile string? _lastReadActive;

    /// <summary>Reason codes for a switch refused before anything was exchanged. Their result says what is active NOW,
    /// so a replay reads it again rather than repeating a value that may since have changed.</summary>
    private static readonly HashSet<string> RefusedBeforeTheExchange = new(StringComparer.Ordinal)
    {
        LicenseSwitchReasonCodes.SwitchInProgress, LicenseSwitchReasonCodes.Expired, LicenseSwitchReasonCodes.ActiveAccountChanged,
        LicenseSwitchReasonCodes.SpareNotHeld, LicenseSwitchReasonCodes.SpareNotUsable,
    };

    /// <summary>Added to a refusal's message when the active account could not be read at that moment.</summary>
    internal const string ActiveNotReadSentence =
        " The active account could not be read just now, so this is the one the last successful read named.";

    public LicenseSwitchCoordinator(ILicenseCommandLine tool, LicenseKeyHolderLock holder, LicenseSwitchResultStore results,
        LicenseUsageReporter reporter, Func<DateTime> utcNow, bool enabled)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _enabled = enabled;
    }

    /// <summary>The sentence every switch is answered with while license switching is off on this Director.</summary>
    internal const string TurnedOffMessage = "License switching is turned off on this Director, so nothing was changed.";

    /// <summary>Carries out <paramref name="request"/>, whose SwitchId has already been checked for shape.</summary>
    public async Task<LicenseSwitchResult> SwitchAsync(LicenseSwitchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write($"[LicenseSwitchCoordinator] SwitchAsync: switchId={request.SwitchId}, target={request.TargetAccountEmail}, expected={request.ExpectedActiveAccountEmail}, reason={request.Reason}");

        // License switching is off (LicenseSwitchingSetting): nothing touches the key store. Answered with the
        // contract's "not the key holder", which is literally true - an off Director never takes the lock - so the
        // contract needs no new reason code. Not stored: the same SwitchId must run on a Director that has it on.
        if (!_enabled)
        {
            FileLog.Write($"[LicenseSwitchCoordinator] switchId={request.SwitchId} refused: license switching is turned off on this Director");
            return Build(request, _utcNow(), LicenseSwitchOutcomes.Failed, LicenseSwitchReasonCodes.NotKeyHolder, "", "", false, 0, TurnedOffMessage);
        }

        // Step 2: a switch that already finished here is answered from the store and never run again. A stored
        // refusal says what was active THEN; the active account is read again, so a late replay never undoes what a
        // report since told the Gateway (fix-round review of 3868).
        var stored = _results.Find(request.SwitchId);
        if (stored is not null)
        {
            FileLog.Write($"[LicenseSwitchCoordinator] SwitchAsync: switchId={request.SwitchId} already finished ({stored.Outcome}), replaying");
            if (stored.ReasonCode is { } code && RefusedBeforeTheExchange.Contains(code))
            {
                var (now, notRead) = await ReadActiveNowAsync(request.SwitchId, cancellationToken);
                return Replay(stored, now, notRead);
            }
            return Replay(stored);
        }

        var started = _utcNow();

        // Step 3: only the key holder touches the key store.
        if (!_holder.IsHeld)
            return Refuse(request, started, LicenseSwitchReasonCodes.NotKeyHolder, "", "This Director is not the one that holds this machine's sign-ins.");

        // Step 4: one switch at a time. The same SwitchId waits for the run under way and answers its result.
        Task<LicenseSwitchResult>? run = null;
        Task<LicenseSwitchResult>? alreadyRunning = null;
        lock (_gate)
        {
            if (_running is { } current)
            {
                if (current.SwitchId == request.SwitchId)
                    alreadyRunning = current.Run;
            }
            else
            {
                run = RunAsync(request, started, cancellationToken);
                _running = (request.SwitchId, run);
            }
        }
        if (run is null && alreadyRunning is null)
        {
            // A finished result for THIS SwitchId (contract 3.6): stored, so a retry of the same id after the other
            // switch ends replays the refusal instead of running a decision the Gateway already saw refused. It carries
            // the active account as it is now (contract 3.4), read outside the lock.
            var (active, notRead) = await ReadActiveNowAsync(request.SwitchId, cancellationToken);
            var refused = Refuse(request, started, LicenseSwitchReasonCodes.SwitchInProgress, active,
                "Another switch is running on this machine." + notRead);
            _results.Save(refused);
            return refused;
        }
        if (alreadyRunning is not null)
        {
            FileLog.Write($"[LicenseSwitchCoordinator] SwitchAsync: switchId={request.SwitchId} is running, waiting for it");
            return Replay(await alreadyRunning);
        }

        try
        {
            return await run!;
        }
        finally
        {
            lock (_gate)
                if (_running?.SwitchId == request.SwitchId) _running = null;
        }
    }

    private async Task<LicenseSwitchResult> RunAsync(LicenseSwitchRequest request, DateTime started, CancellationToken cancellationToken)
    {
        await Task.Yield();
        _reporter.SwitchInProgressId = request.SwitchId;
        try
        {
            var result = await DecideAndExchangeAsync(request, started, cancellationToken);
            _results.Save(result);
            return result;
        }
        finally
        {
            _reporter.SwitchInProgressId = null;
            // Section 2.3: right after every switch, succeeded or not, after the read back.
            _reporter.RequestReport(LicenseReportTriggers.AfterSwitch);
        }
    }

    private async Task<LicenseSwitchResult> DecideAndExchangeAsync(LicenseSwitchRequest request, DateTime started, CancellationToken cancellationToken)
    {
        var target = Normalize(request.TargetAccountEmail);
        var expected = Normalize(request.ExpectedActiveAccountEmail);

        // Step 5: a decision older than its deadline is not acted on.
        if (_utcNow() > request.NotAfterUtc)
            return Failed(request, started, LicenseSwitchReasonCodes.Expired, "", "",
                "The switch arrived after its deadline, so it was not started.");

        // A tool that cannot list the holdings is a fault, not a refusal the contract has a word for: it throws, the
        // tunnel boundary answers Error, and the Gateway logs "director-error". Nothing was changed and nothing is
        // stored, so the same SwitchId sent again runs.
        var holdings = await _tool.ListAsync(cancellationToken);
        var active = holdings.FirstOrDefault(h => h.IsActive)?.AccountEmail ?? "";
        _lastReadActive = active;

        // Step 6: already where the Gateway wants it.
        if (active == target)
            return Finish(request, started, LicenseSwitchOutcomes.AlreadyActive, null, active, active, false, 0,
                $"{target} is already the active account.");

        // Step 7: the Gateway decided on facts that are no longer true.
        if (active != expected)
            return Failed(request, started, LicenseSwitchReasonCodes.ActiveAccountChanged, active, active,
                $"The active account is {Display(active)}, not {expected} as the Gateway expected.");

        // Step 8: no spare to switch to.
        if (!holdings.Any(h => !h.IsActive && h.AccountEmail == target))
            return Failed(request, started, LicenseSwitchReasonCodes.SpareNotHeld, active, active,
                $"This machine holds no spare for {target}.");

        // Step 9: a spare whose sign-in the provider rejected, or whose sign-in has expired by this machine's clock,
        // would leave the machine signed out in effect.
        var spareError = _reporter.LastReadErrorFor(target);
        if (spareError is LicenseReadErrors.Unauthorized or LicenseReadErrors.RefreshFailed)
            return Failed(request, started, LicenseSwitchReasonCodes.SpareNotUsable, active, active,
                $"The spare for {target} needs a new sign-in ({spareError}).");
        var spareExpiry = _reporter.SignInExpiryFor(target);
        if (spareExpiry is { } expires && expires <= _utcNow())
            return Failed(request, started, LicenseSwitchReasonCodes.SpareNotUsable, active, active,
                $"The sign-in for {target} expired at {expires.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC; the account needs a new sign-in.");

        // Step 10: the exchange, by the tool, which reads the active account back afterwards.
        var clock = Stopwatch.StartNew();
        var answer = await _tool.SwitchAsync(target, expected, cancellationToken);
        var exchangeMs = (int)clock.ElapsedMilliseconds;
        if (answer.Outcome == LicenseSwitchOutcomes.Failed)
        {
            if (answer.ReasonCode == LicenseSwitchReasonCodes.ExchangeFailedUnknown)
                FileLog.Write($"[LicenseSwitchCoordinator] switchId={request.SwitchId} EXCHANGE STATE UNKNOWN FAILED: the after-switch report will say what is really held");
            return Finish(request, started, LicenseSwitchOutcomes.Failed, answer.ReasonCode ?? LicenseSwitchReasonCodes.ExchangeFailedUnknown,
                answer.PreviousActiveAccountEmail == "" ? active : answer.PreviousActiveAccountEmail,
                answer.ActiveAccountEmailAfter, false, exchangeMs, ExchangeFailureSentence(answer.ReasonCode, target));
        }

        // Step 11: the read back must name the target, and the new sign-in is read once.
        var after = answer.ActiveAccountEmailAfter;
        _lastReadActive = after;
        if (after != target)
            return Finish(request, started, LicenseSwitchOutcomes.Failed, LicenseSwitchReasonCodes.VerifyFailed, active, after, false, exchangeMs,
                $"After the exchange the active account reads {Display(after)}, not {target}.");

        var verified = await VerifyByUsageReadAsync(request.SwitchId, target, cancellationToken);

        // Step 12.
        return Finish(request, started, LicenseSwitchOutcomes.Switched, null, active, after, verified, exchangeMs,
            $"Switched from {Display(active)} to {target}.");
    }

    /// <summary>One usage read with the new active sign-in. A failed read does not undo the switch (contract 3.3 step 11).</summary>
    private async Task<bool> VerifyByUsageReadAsync(string switchId, string target, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _tool.ReadUsageAsync(activeOnly: true, cancellationToken);
            var row = rows.FirstOrDefault(h => h.IsActive && h.AccountEmail == target);
            var ok = row is not null && row.ReadError is null;
            FileLog.Write($"[LicenseSwitchCoordinator] switchId={switchId} verify read: {(ok ? "answered" : $"did not answer ({row?.ReadError ?? "no row"})")}");
            return ok;
        }
        catch (LicenseCommandLineException ex)
        {
            FileLog.Write($"[LicenseSwitchCoordinator] switchId={switchId} verify read FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The active account as it is now, for a refusal (contract 3.4: "or now, on a refusal"). When the tool cannot list
    /// the holdings, the last successful read stands, and the second value is a sentence saying so for the message.
    /// With nothing read yet there is no honest answer: it throws, the tunnel answers Error, and nothing is stored -
    /// never an empty account, which the Gateway would take as the machine's state.
    /// </summary>
    private async Task<(string Active, string NotReadSentence)> ReadActiveNowAsync(string switchId, CancellationToken cancellationToken)
    {
        try
        {
            var holdings = await _tool.ListAsync(cancellationToken);
            var active = holdings.FirstOrDefault(h => h.IsActive)?.AccountEmail ?? "";
            _lastReadActive = active;
            return (active, "");
        }
        catch (LicenseCommandLineException ex)
        {
            var known = _lastReadActive;
            FileLog.Write($"[LicenseSwitchCoordinator] switchId={switchId} ReadActiveNowAsync FAILED: {ex.Message}; last read={known ?? "none"}");
            if (known is null) throw;
            return (known, ActiveNotReadSentence);
        }
    }

    private LicenseSwitchResult Refuse(LicenseSwitchRequest request, DateTime started, string reasonCode, string active, string message)
    {
        // Stored only by the caller that decides it is a finished result for this SwitchId (switch-in-progress is).
        // "Not the key holder" is not stored: the results folder is shared by every Director on the machine, and the
        // one that holds the key must run that SwitchId when it is sent there.
        var result = Build(request, started, LicenseSwitchOutcomes.Failed, reasonCode, active, active, false, 0, message);
        FileLog.Write($"[LicenseSwitchCoordinator] switchId={request.SwitchId} refused: {reasonCode}");
        return result;
    }

    private LicenseSwitchResult Failed(LicenseSwitchRequest request, DateTime started, string reasonCode, string previous, string after, string message)
        => Finish(request, started, LicenseSwitchOutcomes.Failed, reasonCode, previous, after, false, 0, message);

    private LicenseSwitchResult Finish(LicenseSwitchRequest request, DateTime started, string outcome, string? reasonCode,
        string previous, string after, bool verified, int exchangeMs, string message)
    {
        var result = Build(request, started, outcome, reasonCode, previous, after, verified, exchangeMs, message);
        FileLog.Write($"[LicenseSwitchCoordinator] switchId={request.SwitchId} finished: outcome={outcome}, reason={reasonCode ?? "-"}, after={after}, verified={verified}, exchangeMs={exchangeMs}");
        return result;
    }

    private LicenseSwitchResult Build(LicenseSwitchRequest request, DateTime started, string outcome, string? reasonCode,
        string previous, string after, bool verified, int exchangeMs, string message) => new()
    {
        SwitchId = request.SwitchId,
        Outcome = outcome,
        ReasonCode = reasonCode,
        Message = message,
        PreviousActiveAccountEmail = previous,
        ActiveAccountEmailAfter = after,
        VerifiedByUsageRead = verified,
        StartedAtUtc = started,
        FinishedAtUtc = _utcNow(),
        ExchangeMilliseconds = exchangeMs,
        Replayed = false,
    };

    private static LicenseSwitchResult Replay(LicenseSwitchResult stored) =>
        Replay(stored, null, "");

    /// <summary>A stored result answered again. <paramref name="activeNow"/>, when given, replaces both accounts: a
    /// refusal changed nothing, so before and after are both what is active now.</summary>
    private static LicenseSwitchResult Replay(LicenseSwitchResult stored, string? activeNow, string notReadSentence) => new()
    {
        SwitchId = stored.SwitchId,
        Outcome = stored.Outcome,
        ReasonCode = stored.ReasonCode,
        Message = stored.Message + notReadSentence,
        PreviousActiveAccountEmail = activeNow ?? stored.PreviousActiveAccountEmail,
        ActiveAccountEmailAfter = activeNow ?? stored.ActiveAccountEmailAfter,
        VerifiedByUsageRead = stored.VerifiedByUsageRead,
        StartedAtUtc = stored.StartedAtUtc,
        FinishedAtUtc = stored.FinishedAtUtc,
        ExchangeMilliseconds = stored.ExchangeMilliseconds,
        Replayed = true,
    };

    /// <summary>
    /// The switch log's sentence for a failure the tool reported. Written here, never taken from the tool's own words:
    /// the contract (rule 6.1) lets no message quote a process's output.
    /// </summary>
    internal static string ExchangeFailureSentence(string? reasonCode, string target) => reasonCode switch
    {
        LicenseSwitchReasonCodes.ExchangeFailedRestored => $"The exchange to {target} failed and was undone; nothing changed.",
        LicenseSwitchReasonCodes.ExchangeFailedUnknown => $"The exchange to {target} failed and could not be undone; the next report says what is held.",
        LicenseSwitchReasonCodes.SpareNotHeld => $"This machine holds no spare for {target}.",
        LicenseSwitchReasonCodes.ActiveAccountChanged => "The active account changed before the exchange could start.",
        LicenseSwitchReasonCodes.SwitchInProgress => "Another switch was running on this machine.",
        LicenseSwitchReasonCodes.VerifyFailed => $"After the exchange the active account did not read back as {target}.",
        _ => $"The license tool refused the switch to {target} without a reason this Director recognises.",
    };

    private static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static string Display(string email) => email.Length == 0 ? "no account" : email;
}
