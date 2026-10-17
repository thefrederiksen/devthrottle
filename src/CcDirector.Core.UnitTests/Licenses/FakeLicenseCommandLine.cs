using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>Stands in for <c>cc-devthrottle license</c>: holds accounts by email, answers usage from a table, and
/// exchanges the active account with a spare. No sign-in exists anywhere in it - only emails and numbers.</summary>
internal sealed class FakeLicenseCommandLine : ILicenseCommandLine
{
    /// <summary>Planted in every sentence the fake tool writes, so a test can prove none of it reaches the Gateway.</summary>
    public const string ToolTextMarker = "TOOL-TEXT-MARKER-5e2a";

    public List<(string Email, bool Active)> Held { get; } = new();
    public Dictionary<string, double> FiveHour { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ReadErrors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTime> SignInExpires { get; } = new(StringComparer.Ordinal);
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public int ListCalls { get; private set; }
    public List<bool> UsageCalls { get; } = new();
    public List<(string Target, string Expected)> SwitchCalls { get; } = new();

    /// <summary>When set, the next calls fail as a tool that gave no readable answer.</summary>
    public string? FailWith { get; set; }

    /// <summary>When set, SwitchAsync answers this instead of exchanging.</summary>
    public LicenseExchangeAnswer? SwitchAnswer { get; set; }

    /// <summary>When set, SwitchAsync waits for this before it exchanges.</summary>
    public TaskCompletionSource? SwitchGate { get; set; }

    public FakeLicenseCommandLine Hold(string email, bool active = false)
    {
        Held.Add((email, active));
        return this;
    }

    public string Active => Held.FirstOrDefault(h => h.Active).Email ?? "";

    public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<LicenseHolding>>(Held
            .OrderByDescending(h => h.Active)
            .Select(h => new LicenseHolding { AccountEmail = h.Email, IsActive = h.Active, Plan = "max" })
            .ToList());
    }

    public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        UsageCalls.Add(activeOnly);
        ThrowIfFailing();
        var now = UtcNow();
        return Task.FromResult<IReadOnlyList<LicenseHolding>>(Held
            .Where(h => !activeOnly || h.Active)
            .OrderByDescending(h => h.Active)
            .Select(h => ReadErrors.TryGetValue(h.Email, out var error)
                ? new LicenseHolding
                {
                    AccountEmail = h.Email, IsActive = h.Active, ReadError = error, ReadErrorDetail = ToolTextMarker,
                    SignInExpiresAtUtc = SignInExpires.TryGetValue(h.Email, out var errorExpiry) ? errorExpiry : null,
                }
                : new LicenseHolding
                {
                    AccountEmail = h.Email,
                    IsActive = h.Active,
                    Plan = "max",
                    RateLimitTier = "default_claude_max_20x",
                    FiveHour = new LicenseWindow { Utilization = FiveHour.GetValueOrDefault(h.Email), ResetsAtUtc = now.AddHours(2) },
                    SevenDay = new LicenseWindow { Utilization = 10, ResetsAtUtc = now.AddDays(3) },
                    ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
                    ReadAtUtc = now,
                    SignInExpiresAtUtc = SignInExpires.TryGetValue(h.Email, out var expiry) ? expiry : null,
                })
            .ToList());
    }

    public async Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken)
    {
        SwitchCalls.Add((targetAccountEmail, expectedActiveAccountEmail));
        if (SwitchGate is not null) await SwitchGate.Task;
        if (SwitchAnswer is not null) return SwitchAnswer;

        var previous = Active;
        for (var i = 0; i < Held.Count; i++)
            Held[i] = (Held[i].Email, Held[i].Email == targetAccountEmail);
        return new LicenseExchangeAnswer
        {
            Outcome = LicenseSwitchOutcomes.Switched,
            PreviousActiveAccountEmail = previous,
            ActiveAccountEmailAfter = Active,
            ExchangeMilliseconds = 5,
        };
    }

    // ---- Contract section 8: renewing, signing in, approving ----

    /// <summary>What CheckRenewalsAsync answers: one row per account, as <c>license renew --check</c> would.</summary>
    public List<LicenseRenewalRow> RenewRows { get; } = new();

    public int RenewCalls { get; private set; }

    /// <summary>What a sign-in approved in a browser profile on THIS machine answers, by account. An account with no
    /// entry has no profile here: the tool asks for another machine's approval when told it may, and otherwise
    /// finishes failed, no-approver.</summary>
    public Dictionary<string, LicenseSignInAnswer> LocalSignIns { get; } = new(StringComparer.Ordinal);

    /// <summary>Every sign-in started: the account, and whether the tool was told it may ask another machine.</summary>
    public List<(string Email, bool MayAsk)> SignInCalls { get; } = new();

    /// <summary>When set, a sign-in ends before it prints its authorize URL, with this answer.</summary>
    public LicenseSignInAnswer? RemoteStartFailure { get; set; }

    /// <summary>What a completed remote sign-in answers.</summary>
    public LicenseSignInAnswer RemoteAnswer { get; set; } = new() { Outcome = LicenseSignInOutcomes.Renewed };

    public List<FakeRemoteSignIn> RemoteSignIns { get; } = new();

    public LicenseApproveAnswer ApproveAnswer { get; set; } = new() { Approved = true, Approval = FakeRemoteSignIn.ApprovalMarker };

    public List<(string Url, string Email, string Profile)> ApproveCalls { get; } = new();

    public Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken cancellationToken)
    {
        RenewCalls++;
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<LicenseRenewalRow>>(RenewRows.Select(r => new LicenseRenewalRow
        {
            AccountEmail = r.AccountEmail, IsActive = r.IsActive, Result = r.Result, Error = r.Error,
            HasBrowserProfileHere = LocalSignIns.ContainsKey(r.AccountEmail),
            SignedInAtUtc = r.SignedInAtUtc, SignInExpiresAtUtc = r.SignInExpiresAtUtc,
        }).ToList());
    }

    /// <summary>Every account the Director asked "is a profile set here?" about.</summary>
    public List<string> BrowserQuestions { get; } = new();

    /// <summary>When true the tool gives no readable answer to that question.</summary>
    public bool BrowserQuestionUnanswered { get; set; }

    /// <summary>An account has a profile here exactly when a local sign-in is scripted for it.</summary>
    public Task<bool?> HasBrowserProfileAsync(string accountEmail, CancellationToken cancellationToken)
    {
        BrowserQuestions.Add(accountEmail);
        return Task.FromResult<bool?>(BrowserQuestionUnanswered ? null : LocalSignIns.ContainsKey(accountEmail));
    }

    public Task<LicenseSignInStart> StartSignInAsync(string accountEmail, bool askAnotherMachineWhenNoBrowser, CancellationToken cancellationToken)
    {
        SignInCalls.Add((accountEmail, askAnotherMachineWhenNoBrowser));
        if (LocalSignIns.TryGetValue(accountEmail, out var here))
            return Task.FromResult(new LicenseSignInStart { Finished = here });
        if (!askAnotherMachineWhenNoBrowser)
            return Task.FromResult(new LicenseSignInStart { Finished = LicenseSignInAnswer.Failed(LicenseRenewalErrors.NoApprover) });
        if (RemoteStartFailure is not null)
            return Task.FromResult(new LicenseSignInStart { Finished = RemoteStartFailure });
        var waiting = new FakeRemoteSignIn(accountEmail, RemoteAnswer);
        RemoteSignIns.Add(waiting);
        return Task.FromResult(new LicenseSignInStart { Waiting = waiting });
    }

    public Task<LicenseApproveAnswer> ApproveAsync(string authorizeUrl, string accountEmail, string browserProfile, CancellationToken cancellationToken)
    {
        ApproveCalls.Add((authorizeUrl, accountEmail, browserProfile));
        return Task.FromResult(ApproveAnswer);
    }

    /// <summary>Hand sign-ins the next tool call "healed"; handed out once by <see cref="TakeHandSwitches"/>.</summary>
    public List<LicenseHandSwitch> Healed { get; } = new();

    public IReadOnlyList<LicenseHandSwitch> TakeHandSwitches()
    {
        var taken = Healed.ToList();
        Healed.Clear();
        return taken;
    }

    // ---- Phase 6, Track C: the daily approver check ----

    /// <summary>What a check of a profile answers, by profile identifier and account (null: no account given). A check
    /// with no entry answers as a tool that gave no readable answer.</summary>
    public Dictionary<(string Profile, string? Email), LicenseApproverToolAnswer> ApproverChecks { get; } = new();

    /// <summary>Every check asked for, in order.</summary>
    public List<(string Profile, string? Email)> ApproverCheckCalls { get; } = new();

    /// <summary>When set, a check waits for this before it answers, so a test can hold one running.</summary>
    public TaskCompletionSource? ApproverCheckGate { get; set; }

    /// <summary>Set while a check is running; a test reads it to prove nothing else ran beside it.</summary>
    public bool ApproverCheckRunning { get; private set; }

    public async Task<LicenseApproverToolAnswer> CheckApproverAsync(string browserProfile, string? accountEmail, CancellationToken cancellationToken)
    {
        ApproverCheckCalls.Add((browserProfile, accountEmail));
        ApproverCheckRunning = true;
        try
        {
            if (ApproverCheckGate is not null) await ApproverCheckGate.Task;
            return ApproverChecks.TryGetValue((browserProfile, accountEmail), out var answer)
                ? answer
                : LicenseApproverToolAnswer.NoAnswer(accountEmail);
        }
        finally
        {
            ApproverCheckRunning = false;
        }
    }

    private void ThrowIfFailing()
    {
        if (FailWith is not null) throw new LicenseCommandLineException(FailWith);
    }
}

/// <summary>Stands in for the Gateway tunnel's approver methods: answers the assignments it is given and keeps every
/// report, or refuses either the way a dropped tunnel would.</summary>
internal sealed class FakeApproverChannel : ILicenseApproverChannel
{
    public bool CanReach { get; set; } = true;

    public LicenseApproverAssignments Assignments { get; } = new();

    public string? AssignmentsFailWith { get; set; }

    public string? ReportFailWith { get; set; }

    public int AssignmentCalls { get; private set; }

    public List<LicenseApproverCheckReport> Reports { get; } = new();

    public FakeApproverChannel Assign(string accountEmail, string profile, string source = LicenseApproverSources.Owner)
    {
        Assignments.Rows.Add(new LicenseApproverAssignment { AccountEmail = accountEmail, BrowserProfile = profile, Source = source });
        return this;
    }

    public Task<LicenseApproverAssignments> GetAssignmentsAsync(CancellationToken cancellationToken)
    {
        AssignmentCalls++;
        if (AssignmentsFailWith is not null) throw new LicenseApprovalRelayException(AssignmentsFailWith);
        return Task.FromResult(Assignments);
    }

    public Task<LicenseApproverCheckAnswer> ReportAsync(LicenseApproverCheckReport report, CancellationToken cancellationToken)
    {
        if (ReportFailWith is not null) throw new LicenseApprovalRelayException(ReportFailWith);
        Reports.Add(report);
        return Task.FromResult(new LicenseApproverCheckAnswer
        {
            Rows = report.Rows.Select(r => new LicenseApproverCheckRowAnswer
            {
                BrowserProfile = r.BrowserProfile, AccountEmail = r.AccountEmail, Outcome = LicenseApproverCheckOutcomes.HealthUpdated,
            }).ToList(),
        });
    }
}

/// <summary>This Director's own browser profiles, as a list a test writes.</summary>
internal sealed class FakeBrowserProfiles : ILicenseBrowserProfiles
{
    public List<LicenseBrowserProfile> Profiles { get; } = new();

    public int ListCalls { get; private set; }

    public FakeBrowserProfiles Add(string id, string name)
    {
        Profiles.Add(new LicenseBrowserProfile(id, name));
        return this;
    }

    public IReadOnlyList<LicenseBrowserProfile> List()
    {
        ListCalls++;
        return Profiles.ToList();
    }
}

/// <summary>Stands in for a <c>license sign-in --remote-approval</c> that printed its URL and is waiting.</summary>
internal sealed class FakeRemoteSignIn : ILicenseRemoteSignIn
{
    /// <summary>Stands in for the authorize answer. Planted so a test can prove it reaches the waiting login and
    /// nothing else: no result, no report, no message.</summary>
    public const string ApprovalMarker = "APPROVAL-MARKER-91c7";

    private readonly LicenseSignInAnswer _answer;

    public FakeRemoteSignIn(string accountEmail, LicenseSignInAnswer answer)
    {
        AccountEmail = accountEmail;
        _answer = answer;
        AuthorizeUrl = "https://claude.ai/oauth/authorize?code=true&state=s1&login_hint=" + accountEmail.Replace("@", "%40");
    }

    public string AccountEmail { get; }

    public string AuthorizeUrl { get; }

    /// <summary>What was typed into the waiting login; null when nothing was.</summary>
    public string? TypedIn { get; private set; }

    public bool Abandoned { get; private set; }

    public Task<LicenseSignInAnswer> CompleteAsync(string approval, CancellationToken cancellationToken)
    {
        if (TypedIn is not null || Abandoned) throw new InvalidOperationException("this sign-in has already been completed or abandoned");
        TypedIn = approval;
        return Task.FromResult(_answer);
    }

    public Task AbandonAsync(CancellationToken cancellationToken)
    {
        if (TypedIn is null) Abandoned = true;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await AbandonAsync(CancellationToken.None);
}

/// <summary>Stands in for the Gateway tunnel's approval relay: keeps what was asked, and can refuse to take it.</summary>
internal sealed class FakeApprovalRelay : ILicenseApprovalRelay
{
    public bool CanRequest { get; set; } = true;

    public string? FailWith { get; set; }

    public List<LicenseApprovalRequest> Requests { get; } = new();

    /// <summary>Run after a request is taken, so a test can answer it the way the Gateway would - later, as a command.</summary>
    public Action<LicenseApprovalRequest>? OnRequest { get; set; }

    public Task RequestAsync(LicenseApprovalRequest request, CancellationToken cancellationToken)
    {
        if (FailWith is not null) throw new LicenseApprovalRelayException(FailWith);
        Requests.Add(request);
        OnRequest?.Invoke(request);
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for <c>cc-secrets seal</c> and <c>open</c>: a "sealed" text that names its recipient and its
/// sign-in and opens only for that sign-in. No cryptography - the real one is tested in the tool.</summary>
internal sealed class FakeApprovalSeal : ILicenseApprovalSeal
{
    private const string Prefix = "sealed|";

    private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);

    public bool Broken { get; set; }

    public List<(string Recipient, string SignInId)> Sealed { get; } = new();

    public Task<string?> SealAsync(string approval, string recipient, string signInId, CancellationToken cancellationToken)
    {
        if (Broken) return Task.FromResult<string?>(null);
        Sealed.Add((recipient, signInId));
        var envelope = Prefix + recipient + "|" + signInId + "|" + _contents.Count;
        _contents[envelope] = approval;
        return Task.FromResult<string?>(envelope);
    }

    public Task<string?> OpenAsync(string sealedApproval, string signInId, CancellationToken cancellationToken)
    {
        if (Broken || !_contents.TryGetValue(sealedApproval, out var approval) || sealedApproval.Split('|')[2] != signInId)
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(approval);
    }
}

/// <summary>Collects what would have gone up the tunnel.</summary>
internal sealed class FakeReportSink : ILicenseReportSink
{
    public bool CanSend { get; set; } = true;
    public List<LicenseUsageReport> Sent { get; } = new();

    public Task SendAsync(LicenseUsageReport report, CancellationToken cancellationToken)
    {
        Sent.Add(report);
        return Task.CompletedTask;
    }
}

/// <summary>A clock a test moves by hand.</summary>
/// <summary>A delay a test ends by hand, so nothing waits on a real clock. A wait cancelled by its caller ends as
/// cancelled, as Task.Delay's does.</summary>
internal sealed class ControlledDelay
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _pending = new();

    /// <summary>Every wait asked for, in order.</summary>
    public List<TimeSpan> Asked { get; } = new();

    public Task Wait(TimeSpan span, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            Asked.Add(span);
            _pending.Add(done);
        }
        cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
        return done.Task;
    }

    /// <summary>Ends every wait still running, as if its time had passed.</summary>
    public void PassAll()
    {
        List<TaskCompletionSource> pending;
        lock (_gate)
        {
            pending = _pending.ToList();
            _pending.Clear();
        }
        foreach (var done in pending) done.TrySetResult();
    }
}

internal sealed class FakeClock
{
    public DateTime Now { get; set; } = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A temporary folder for one test, removed after it.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "license-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
