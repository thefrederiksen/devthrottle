using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseSignInTestData;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE APPROVAL RELAY over the real approver store and the real license store on a throwaway SQLite file, with fake
/// Directors on the other end of each command (License Switching, contract section 8.3). The clock is the test's own,
/// and nothing here waits on one: a relay is awaited through <c>WhenIdleAsync</c>.
///
/// What these hold down: the hub's call returns before the approving machine answers; the approve command goes to the
/// Director that last reported for the machine the owner set, with the asking Director's own key; the answer goes back
/// to the asking Director; a request that cannot be passed on is answered at once with the contract's reason; the two
/// waits are the contract's; and one DevThrottle account never reaches a machine or an approver of another.
/// </summary>
// It captures the process-wide log, so no other test class runs beside it.
[Collection(FileLogCaptureCollection.Name)]
public sealed class LicenseApprovalRelayTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");

    private const string Target = "the-windows-machine";
    private const string ApprovingMachine = "the-mac";
    private const string TargetDirector = "dir-target";
    private const string ApprovingDirector = "dir-approver";
    private const string Profile = "work";
    private const string Address = "https://claude.example/authorize?challenge=abc";
    private const string SealedValue = "c2VhbGVkLXRvLXRoZS10YXJnZXQ=";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseApproverStore _approvers;
    private readonly TenantSettingsStore _settings;
    private readonly FakeLicenseDirectors _directors = new();
    private readonly LicenseApprovalRelay _relay;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseApprovalRelayTests()
    {
        var db = _harness.Open();
        _store = new LicenseStore(db);
        _settings = new TenantSettingsStore(db);
        _approvers = new LicenseApproverStore(_settings);
        _relay = new LicenseApprovalRelay(_approvers, new LicenseReadModel(_store), _directors.IsConnected, _directors.SendAsync,
            _directors.EnterScope, () => _now, CancellationToken.None);
    }

    public void Dispose() => _harness.Dispose();

    // ----- helpers -----

    /// <summary>The machine's Director sends a report holding nothing, and is connected.</summary>
    private void Reports(TenantId tenant, string machine, string director)
    {
        _store.ApplyReport(tenant, director, ++_sequence, HoldsNothing(machine, _now), _now, TimeSpan.Zero);
        _directors.Connect(tenant, director);
    }

    /// <summary>Tenant one as most tests want it: the asking machine and the approving machine both reporting, and
    /// the approving machine set to approve account A.</summary>
    private void OneIsSetUp()
    {
        Reports(One, Target, TargetDirector);
        Reports(One, ApprovingMachine, ApprovingDirector);
        Assert.True(_approvers.Set(One, A, ApprovingMachine, "owner", Profile, _now).Accepted);
    }

    private LicenseApprovalRequest Request(string signInId = SignIn1, string account = A) => new()
    {
        SignInId = signInId,
        AccountEmail = account,
        AuthorizeUrl = Address,
        NotAfterUtc = _now.AddMinutes(3),
    };

    private async Task Ask(TenantId tenant, LicenseApprovalRequest request, string? key, string director = TargetDirector)
    {
        _relay.Accept(tenant, director, key, request);
        await _relay.WhenIdleAsync();
    }

    private Task Ask(LicenseApprovalRequest? request = null) => Ask(One, request ?? Request(), PublicHalf);

    /// <summary>The approving Director approves and hands back a sealed value; the asking Director takes its answer.</summary>
    private void TheApproverApproves() => _directors.AnswerWith(sent => sent.Verb == LicenseContract.ApproveVerb
        ? FakeLicenseDirectors.Ok(new LicenseApprovalResult
        {
            SignInId = ((LicenseApproveRequest)sent.Payload).SignInId,
            Outcome = LicenseApprovalOutcomes.Approved,
            Message = "Approved in the browser profile.",
            SealedApproval = SealedValue,
        })
        : FakeLicenseDirectors.Ok(new { delivered = true }));

    /// <summary>The approving Director answers <paramref name="answer"/>; the asking Director takes whatever follows.</summary>
    private void TheApproverAnswers(Func<LicenseApproveRequest, DirectorCommandResult?> answer) => _directors.AnswerWith(sent =>
        sent.Verb == LicenseContract.ApproveVerb ? answer((LicenseApproveRequest)sent.Payload) : FakeLicenseDirectors.Ok(new { delivered = true }));

    private LicenseApprovalResult TheAnswer() => (LicenseApprovalResult)Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb)).Payload;

    private void AssertRefused(string reason)
    {
        var answer = TheAnswer();
        Assert.Equal((SignIn1, LicenseApprovalOutcomes.Refused, reason), (answer.SignInId, answer.Outcome, answer.ReasonCode));
        Assert.Null(answer.SealedApproval);
        Assert.False(string.IsNullOrWhiteSpace(answer.Message));
    }

    // ----- the hub's call returns at once -----

    [Fact]
    public async Task Accept_ReturnsBeforeTheApprovingDirectorAnswers()
    {
        OneIsSetUp();
        var approving = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = sent =>
        {
            if (sent.Verb != LicenseContract.ApproveVerb)
                return Task.FromResult<DirectorCommandResult?>(FakeLicenseDirectors.Ok(new { delivered = true }));
            reached.SetResult();
            return approving.Task;
        };

        // The call is given a limit so that a call that waits for the Director is a red test, not a run that never ends.
        await Task.Run(() => _relay.Accept(One, TargetDirector, PublicHalf, Request())).WaitAsync(TimeSpan.FromSeconds(60));

        // Accept has returned, and the approving Director has the command and has not answered it.
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(_directors.Of(LicenseContract.ApprovalResultVerb));

        approving.SetResult(FakeLicenseDirectors.Ok(new LicenseApprovalResult
            { SignInId = SignIn1, Outcome = LicenseApprovalOutcomes.Approved, SealedApproval = SealedValue }));
        await _relay.WhenIdleAsync();
        Assert.Equal(LicenseApprovalOutcomes.Approved, TheAnswer().Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-identifier")]
    [InlineData("1111111111111111111111111111111")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    // 32 lower-case hex with a line break after them: it would put a line break into the relay's log lines.
    [InlineData("11111111111111111111111111111111\n")]
    [InlineData("11111111111111111111111111111111\r\n")]
    public async Task Accept_ARequestWhoseIdentifierIsNotASignInIdentifier_IsRefusedOnTheCall_AndNothingIsSent(string signInId)
    {
        OneIsSetUp();

        Assert.Throws<ArgumentException>(() => _relay.Accept(One, TargetDirector, PublicHalf, Request(signInId)));
        Assert.Throws<ArgumentException>(() => _relay.Accept(One, TargetDirector, PublicHalf, null));

        await _relay.WhenIdleAsync();
        Assert.Empty(_directors.Sends);
    }

    // ----- the approve command -----

    [Fact]
    public async Task Relay_WithTheApproverConnected_SendsLicenseApproveToItsDirector_WithTheAskingDirectorsOwnKey()
    {
        OneIsSetUp();
        TheApproverApproves();

        await Ask();

        var sent = Assert.Single(_directors.Of(LicenseContract.ApproveVerb));
        Assert.Equal((ApprovingDirector, ApprovingMachine), (sent.DirectorId, sent.MachineName));
        var approve = Assert.IsType<LicenseApproveRequest>(sent.Payload);
        Assert.Equal((SignIn1, A, Address, Profile, PublicHalf), (approve.SignInId, approve.AccountEmail, approve.AuthorizeUrl, approve.BrowserProfile, approve.SealToRecipient));
        Assert.Equal(_now.AddMinutes(3), approve.NotAfterUtc);
        Assert.Equal(DateTimeKind.Utc, approve.NotAfterUtc.Kind);
    }

    [Fact]
    public async Task Relay_Approved_SendsTheSealedApprovalToTheDirectorThatAsked_AndToNoOther()
    {
        OneIsSetUp();
        TheApproverApproves();

        await Ask();

        var delivered = Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb));
        Assert.Equal(TargetDirector, delivered.DirectorId);
        var answer = (LicenseApprovalResult)delivered.Payload;
        Assert.Equal((SignIn1, LicenseApprovalOutcomes.Approved, SealedValue), (answer.SignInId, answer.Outcome, answer.SealedApproval));
        Assert.Null(answer.ReasonCode);
        Assert.Equal(2, _directors.Sends.Count);
    }

    [Fact]
    public async Task Relay_UsesTheWaitsOfTheContract()
    {
        OneIsSetUp();
        TheApproverApproves();

        await Ask();

        Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(_directors.Of(LicenseContract.ApproveVerb)).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb)).Timeout);
    }

    [Fact]
    public async Task Relay_SendsToTheDirectorThatSentTheApprovingMachinesLastReport()
    {
        Reports(One, Target, TargetDirector);
        Reports(One, ApprovingMachine, "dir-approver-old");
        Reports(One, ApprovingMachine, ApprovingDirector);
        _approvers.Set(One, A, ApprovingMachine, "owner", Profile, _now);
        TheApproverApproves();

        await Ask();

        Assert.Equal(ApprovingDirector, Assert.Single(_directors.Of(LicenseContract.ApproveVerb)).DirectorId);
    }

    [Fact]
    public async Task Relay_FindsTheApprovingMachineWithoutRegardToCase()
    {
        Reports(One, Target, TargetDirector);
        Reports(One, ApprovingMachine, ApprovingDirector);
        _approvers.Set(One, A.ToUpperInvariant(), "THE-MAC", "Owner", Profile, _now);
        TheApproverApproves();

        await Ask();

        Assert.Equal(ApprovingDirector, Assert.Single(_directors.Of(LicenseContract.ApproveVerb)).DirectorId);
        Assert.Equal(LicenseApprovalOutcomes.Approved, TheAnswer().Outcome);
    }

    // ----- answered at once when nothing can be asked -----

    [Fact]
    public async Task Relay_NoApproverSetForTheAccount_AnswersRefusedNoApprover_AndAsksNoMachine()
    {
        Reports(One, Target, TargetDirector);
        Reports(One, ApprovingMachine, ApprovingDirector);
        _approvers.Set(One, B, ApprovingMachine, "owner", Profile, _now);

        await Ask();

        AssertRefused(LicenseRenewalErrors.NoApprover);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    [Fact]
    public async Task Relay_TheApprovingMachineHasNeverReported_AnswersApproverOffline_AndAsksNoMachine()
    {
        Reports(One, Target, TargetDirector);
        _approvers.Set(One, A, ApprovingMachine, "owner", Profile, _now);

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApproverOffline);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    [Fact]
    public async Task Relay_TheApprovingMachinesDirectorIsNotConnected_AnswersApproverOffline_AndAsksNoMachine()
    {
        OneIsSetUp();
        _directors.Disconnect(One, ApprovingDirector);

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApproverOffline);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    [Fact]
    public async Task Relay_TheApprovingMachineIsAnotherUserOfThatComputer_AnswersApproverOffline()
    {
        // A machine is a machine name AND an operating system user: the report is from "owner", the approver names "guest".
        Reports(One, Target, TargetDirector);
        Reports(One, ApprovingMachine, ApprovingDirector);
        _approvers.Set(One, A, ApprovingMachine, "guest", Profile, _now);

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApproverOffline);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    [Fact]
    public async Task Relay_TheRequestArrivedAfterItsOwnDeadline_AnswersExpired_AndAsksNoMachine()
    {
        OneIsSetUp();
        var request = Request();
        request.NotAfterUtc = _now.AddSeconds(-1);

        await Ask(request);

        AssertRefused(LicenseRenewalErrors.Expired);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("c2hvcnQ=")]
    public async Task Relay_TheAskingDirectorOfferedNoKeyInItsHello_AnswersRefused_AndAsksNoMachine(string? key)
    {
        OneIsSetUp();

        await Ask(One, Request(), key);

        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    public static TheoryData<string, string?, string?> RequestsThatAreNotTaken() => new()
    {
        { "no account", "", Address },
        { "a null account", null, Address },
        { "an account that is not an email", "no-at-sign", Address },
        { "an account with a line break", "account-a@example.com\nINJECTED", Address },
        { "an account with a control character", "account-a\u0007@example.com", Address },
        { "an account with a format character", "account-a\u202e@example.com", Address },
        { "an account far too long", new string('a', 2_000_000) + "@example.com", Address },
        { "no address", A, "" },
        { "a null address", A, null },
        { "an address of spaces", A, "   " },
        { "an address one character too long", A, "https://claude.example/" + new string('a', LicenseApprovalRelay.MaxAuthorizeUrlLength - 22) },
        { "an address far too long", A, "https://claude.example/" + new string('a', 2_000_000) },
    };

    [Theory]
    [MemberData(nameof(RequestsThatAreNotTaken))]
    public async Task Accept_ARequestWhoseAccountOrAddressIsNotOne_IsRefusedOnTheCall_AndHoldsAndSendsAndQuotesNothing(
        string what, string? account, string? address)
    {
        Assert.NotEmpty(what);
        OneIsSetUp();
        var request = Request();
        request.AccountEmail = account!;
        request.AuthorizeUrl = address!;
        using var capture = new CapturedLog();

        var refused = Assert.Throws<ArgumentException>(() => _relay.Accept(One, TargetDirector, PublicHalf, request));
        var lines = capture.Lines();
        var log = string.Join(" | ", lines);

        Assert.DoesNotContain("INJECTED", refused.Message);
        Assert.True(refused.Message.Length < 200);

        Assert.Equal(0, _relay.InFlightCount());
        await _relay.WhenIdleAsync();
        Assert.Empty(_directors.Sends);
        // The refusal is logged, and no line says anything the request said - not even its identifier.
        Assert.Contains("[LicenseApprovalRelay] Accept: REFUSED", log);
        Assert.DoesNotContain("INJECTED", log);
        Assert.DoesNotContain("aaaaaaaa", log);
        Assert.DoesNotContain("claude.example", log);
        Assert.DoesNotContain(lines, l => l.Contains($"signIn={SignIn1}"));
    }

    [Fact]
    public async Task Accept_AnAddressOfExactlyTheLongestLength_IsTaken()
    {
        OneIsSetUp();
        TheApproverApproves();
        var request = Request();
        request.AuthorizeUrl = "https://claude.example/" + new string('a', LicenseApprovalRelay.MaxAuthorizeUrlLength - 23);
        Assert.Equal(LicenseApprovalRelay.MaxAuthorizeUrlLength, request.AuthorizeUrl.Length);

        await Ask(request);

        Assert.Equal(LicenseApprovalOutcomes.Approved, TheAnswer().Outcome);
    }

    // ----- what the approving Director's answer becomes -----

    [Fact]
    public async Task Relay_TheApprovingDirectorRefuses_PassesOnItsReasonAndItsSentence_AndNoSealedValue()
    {
        OneIsSetUp();
        TheApproverAnswers(approve => FakeLicenseDirectors.Ok(new LicenseApprovalResult
        {
            SignInId = approve.SignInId,
            Outcome = LicenseApprovalOutcomes.Refused,
            ReasonCode = LicenseRenewalErrors.WrongAccount,
            Message = "The browser profile is signed in as someone else.",
            SealedApproval = SealedValue,
        }));

        await Ask();

        AssertRefused(LicenseRenewalErrors.WrongAccount);
        Assert.Equal("The browser profile is signed in as someone else.", TheAnswer().Message);
    }

    [Fact]
    public async Task Relay_ARefusalWithAReasonTheContractDoesNotHave_IsPassedOnAsApprovalFailed()
    {
        OneIsSetUp();
        TheApproverAnswers(approve => FakeLicenseDirectors.Ok(new LicenseApprovalResult
            { SignInId = approve.SignInId, Outcome = LicenseApprovalOutcomes.Refused, ReasonCode = "whatever a tool printed" }));

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
    }

    [Fact]
    public async Task Relay_TheApproveCommandGetsNoAnswerInTime_AnswersRefusedApprovalFailed()
    {
        OneIsSetUp();
        // What the router itself returns when the wait ends; made here so no test waits three minutes for it.
        TheApproverAnswers(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout,
            DirectorCommandRouter.DescribeTimeout(ApprovingMachine, LicenseContract.ApproveCommandTimeout)));

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
        Assert.Contains("180 seconds", TheAnswer().Message);
    }

    [Fact]
    public async Task Relay_TheTunnelToTheApprovingDirectorDrops_AnswersApproverOffline()
    {
        OneIsSetUp();
        TheApproverAnswers(_ => DirectorCommandResult.Fail(DirectorCommandStatus.TunnelDropped, "dropped"));

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApproverOffline);
    }

    [Fact]
    public async Task Relay_TheApprovingDirectorDisconnectsBetweenTheCheckAndTheSend_AnswersApproverOffline()
    {
        OneIsSetUp();
        TheApproverAnswers(_ => null);

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApproverOffline);
    }

    [Fact]
    public async Task Relay_TheApprovingDirectorDoesNotKnowTheCommand_AnswersRefusedApprovalFailed()
    {
        OneIsSetUp();
        TheApproverAnswers(_ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "unknown verb"));

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
    }

    public static TheoryData<string, string> AnswersThatAreNotTheResultOfThisApproval() => new()
    {
        { "an empty body", "" },
        { "a body that is not JSON", "{ this is not JSON" },
        { "an approval for another sign-in", $$"""{"signInId":"{{SignIn2}}","outcome":"approved","sealedApproval":"{{SealedValue}}"}""" },
        { "an approval with no sealed value", $$"""{"signInId":"{{SignIn1}}","outcome":"approved"}""" },
        { "an outcome the contract does not have", $$"""{"signInId":"{{SignIn1}}","outcome":"maybe","sealedApproval":"{{SealedValue}}"}""" },
    };

    [Theory]
    [MemberData(nameof(AnswersThatAreNotTheResultOfThisApproval))]
    public async Task Relay_AnAnswerThatIsNotTheResultOfThisApproval_AnswersRefused_AndPassesOnNoSealedValue(string what, string body)
    {
        Assert.NotEmpty(what);
        OneIsSetUp();
        TheApproverAnswers(_ => DirectorCommandResult.Success(body));

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
    }

    // ----- one account never reaches another's -----

    [Fact]
    public async Task Relay_EveryCommandIsSentInsideTheAskingDirectorsOwnTenantScope()
    {
        OneIsSetUp();
        TheApproverApproves();

        await Ask();

        Assert.Equal(2, _directors.Sends.Count);
        Assert.All(_directors.Sends, sent => Assert.Equal(One, sent.Scope));
    }

    [Fact]
    public async Task Relay_AnApproverSetInAnotherAccount_IsNeverUsed_AndThatAccountsDirectorIsSentNothing()
    {
        // Tenant two has everything: the same machine names, a connected approving Director, an approver for account A.
        Reports(Two, Target, TargetDirector);
        Reports(Two, ApprovingMachine, ApprovingDirector);
        _approvers.Set(Two, A, ApprovingMachine, "owner", Profile, _now);
        // Tenant one has only the machine that asks.
        Reports(One, Target, "dir-target-of-one");

        await Ask(One, Request(), PublicHalf, "dir-target-of-one");

        AssertRefused(LicenseRenewalErrors.NoApprover);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
        Assert.All(_directors.Sends, sent => Assert.Equal((One, "dir-target-of-one"), (sent.Scope, sent.DirectorId)));
    }

    [Fact]
    public async Task Relay_AnApproverNamingAMachineOnlyAnotherAccountHas_AnswersApproverOffline_AndThatMachineIsSentNothing()
    {
        // The approving machine exists, connected, in tenant two only. Tenant one's owner names it.
        Reports(Two, ApprovingMachine, ApprovingDirector);
        Reports(One, Target, TargetDirector);
        _approvers.Set(One, A, ApprovingMachine, "owner", Profile, _now);
        TheApproverApproves();

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApproverOffline);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
        Assert.DoesNotContain(_directors.Sends, sent => sent.DirectorId == ApprovingDirector || Two.Equals(sent.Scope));
    }

    [Fact]
    public async Task Relay_TwoAccountsAskingForTheSameAccount_EachReachesOnlyItsOwnApprover()
    {
        Reports(One, Target, "dir-target-one");
        Reports(One, ApprovingMachine, "dir-approver-one");
        _approvers.Set(One, A, ApprovingMachine, "owner", "profile-of-one", _now);
        Reports(Two, Target, "dir-target-two");
        Reports(Two, ApprovingMachine, "dir-approver-two");
        _approvers.Set(Two, A, ApprovingMachine, "owner", "profile-of-two", _now);
        TheApproverApproves();

        await Ask(One, Request(SignIn1), PublicHalf, "dir-target-one");
        await Ask(Two, Request(SignIn2), PublicHalf, "dir-target-two");

        var approves = _directors.Of(LicenseContract.ApproveVerb);
        Assert.Equal(new[] { (One, "dir-approver-one", "profile-of-one"), (Two, "dir-approver-two", "profile-of-two") },
            approves.Select(s => (s.Scope!.Value, s.DirectorId, ((LicenseApproveRequest)s.Payload).BrowserProfile)));
        var results = _directors.Of(LicenseContract.ApprovalResultVerb);
        Assert.Equal(new[] { (One, "dir-target-one", SignIn1), (Two, "dir-target-two", SignIn2) },
            results.Select(s => (s.Scope!.Value, s.DirectorId, ((LicenseApprovalResult)s.Payload).SignInId)));
    }

    // ----- repeats and the limit -----

    [Fact]
    public async Task Accept_TheSameSignInAgainWhileItsRelayRuns_IsDropped()
    {
        OneIsSetUp();
        var approving = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = sent =>
        {
            if (sent.Verb != LicenseContract.ApproveVerb)
                return Task.FromResult<DirectorCommandResult?>(FakeLicenseDirectors.Ok(new { delivered = true }));
            reached.TrySetResult();
            return approving.Task;
        };
        _relay.Accept(One, TargetDirector, PublicHalf, Request());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

        _relay.Accept(One, TargetDirector, PublicHalf, Request());

        approving.SetResult(DirectorCommandResult.Fail(DirectorCommandStatus.TunnelDropped, "dropped"));
        await _relay.WhenIdleAsync();
        Assert.Single(_directors.Of(LicenseContract.ApproveVerb));
        Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb));
    }

    [Fact]
    public async Task Accept_EveryRequestPastTheLimit_IsRefusedOnTheCall_AndTheHundredthHoldsNothingMoreThanTheFirst()
    {
        OneIsSetUp();
        // Account two is set up the same way, to show the limit is each account's own.
        Reports(Two, Target, "dir-target-of-two");
        Reports(Two, ApprovingMachine, "dir-approver-of-two");
        Assert.True(_approvers.Set(Two, A, ApprovingMachine, "owner", Profile, _now).Accepted);
        var approving = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = 0;
        var allReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = sent =>
        {
            if (sent.Verb != LicenseContract.ApproveVerb)
                return Task.FromResult<DirectorCommandResult?>(FakeLicenseDirectors.Ok(new { delivered = true }));
            if (Interlocked.Increment(ref reached) == LicenseApprovalRelay.MaxInFlightPerTenant)
                allReached.SetResult();
            return approving.Task;
        };
        for (var i = 0; i < LicenseApprovalRelay.MaxInFlightPerTenant; i++)
            _relay.Accept(One, TargetDirector, PublicHalf, Request(i.ToString("x").PadLeft(32, 'a')));
        await allReached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var sendsAtTheLimit = _directors.Sends.Count;
        Assert.Equal(LicenseApprovalRelay.MaxInFlightPerTenant, _relay.InFlightCount(One));

        // A hundred more, each with an identifier of its own, as a renewal loop gone wrong would send them.
        for (var i = 0; i < 100; i++)
        {
            var busy = Assert.Throws<LicenseApprovalsBusyException>(
                () => _relay.Accept(One, TargetDirector, PublicHalf, Request(i.ToString("x").PadLeft(32, 'b'))));
            Assert.Equal("The Gateway is already relaying 8 approvals for this account. Try the sign-in again when one has finished.", busy.Message);
        }

        // Nothing is held for any of them: no entry, no task, no command to either Director.
        Assert.Equal(LicenseApprovalRelay.MaxInFlightPerTenant, _relay.InFlightCount(One));
        Assert.Equal(LicenseApprovalRelay.MaxInFlightPerTenant, _relay.InFlightCount());
        Assert.Equal(sendsAtTheLimit, _directors.Sends.Count);
        Assert.Empty(_directors.Of(LicenseContract.ApprovalResultVerb));

        // Another account is not held back by this one's eight.
        _relay.Accept(Two, "dir-target-of-two", PublicHalf, Request(SignIn2));
        Assert.Equal(1, _relay.InFlightCount(Two));

        // When the eight end, the account's places are free again and nothing is left behind.
        approving.SetResult(DirectorCommandResult.Fail(DirectorCommandStatus.TunnelDropped, "dropped"));
        await _relay.WhenIdleAsync();
        Assert.Equal(0, _relay.InFlightCount());
        TheApproverApproves();
        _relay.Accept(One, TargetDirector, PublicHalf, Request(SignIn1));
        Assert.Equal(LicenseApprovalOutcomes.Approved, (await WaitForAnswerTo(SignIn1)).Outcome);
        await _relay.WhenIdleAsync();
    }

    // ----- a relay that fails still answers, and says nothing it was given -----

    [Fact]
    public async Task Relay_ASendToTheApprovingDirectorThatThrows_StillAnswersTheDirectorThatAsked_AndTheLogHasNoAddress()
    {
        OneIsSetUp();
        _directors.Answer = sent => sent.Verb == LicenseContract.ApproveVerb
            // A failure whose message quotes the payload, as a serializer's or a transport's could.
            ? throw new InvalidOperationException($"could not send {((LicenseApproveRequest)sent.Payload).AuthorizeUrl} sealed to {PublicHalf}")
            : Task.FromResult<DirectorCommandResult?>(FakeLicenseDirectors.Ok(new { delivered = true }));
        using var capture = new CapturedLog();

        await Ask();
        var log = string.Join(" | ", capture.Lines());

        // The asking Director is told now, with a reason, instead of waiting out its own deadline.
        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
        Assert.Equal("The Gateway failed while relaying this approval (InvalidOperationException).", TheAnswer().Message);
        Assert.Equal(TargetDirector, Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb)).DirectorId);
        Assert.Contains($"signIn={SignIn1}, account={A} FAILED while relaying: InvalidOperationException", log);
        Assert.DoesNotContain(Address, log);
        Assert.DoesNotContain("claude.example", log);
        Assert.DoesNotContain("could not send", log);
        Assert.DoesNotContain(PublicHalf, log);
    }

    [Fact]
    public async Task Relay_AnApproversSettingThatCannotBeRead_StillAnswersTheDirectorThatAsked()
    {
        OneIsSetUp();
        _settings.Set(One, TenantSettingKeys.LicenseApprovers, "{ this is not the list", _now);
        _directors.AnswerWith(_ => FakeLicenseDirectors.Ok(new { delivered = true }));

        await Ask();

        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    [Fact]
    public async Task Relay_AScopeThatCannotBeEntered_IsLoggedByItsTypeOnly_AndLeavesNothingInFlight()
    {
        var relay = new LicenseApprovalRelay(_approvers, new LicenseReadModel(_store),
            _directors.IsConnected, _directors.SendAsync, _ => throw new InvalidOperationException($"no scope for {Address}"),
            () => _now, CancellationToken.None);
        using var capture = new CapturedLog();

        relay.Accept(One, TargetDirector, PublicHalf, Request());
        await relay.WhenIdleAsync();
        var log = string.Join(" | ", capture.Lines());

        Assert.Equal(0, relay.InFlightCount());
        Assert.Contains($"signIn={SignIn1}, account={A} FAILED: InvalidOperationException", log);
        Assert.DoesNotContain("claude.example", log);
        Assert.Empty(_directors.Sends);
    }

    [Fact]
    public async Task Accept_CalledWhileAnotherAccountsScopeIsInEffect_StillRelaysInTheAskingDirectorsOwnScope()
    {
        OneIsSetUp();
        // Account two has a machine and a Director of the same names, and an approver of its own for the account.
        Reports(Two, ApprovingMachine, ApprovingDirector);
        Assert.True(_approvers.Set(Two, A, ApprovingMachine, "owner", "profile-of-two", _now).Accepted);
        TheApproverApproves();

        // The calling thread is inside account TWO's scope when account ONE's Director asks.
        using (_directors.EnterScope(Two))
            _relay.Accept(One, TargetDirector, PublicHalf, Request());
        await _relay.WhenIdleAsync();

        Assert.Equal(new TenantId?[] { One, One }, _directors.Sends.Select(s => s.Scope));
        var approve = (LicenseApproveRequest)Assert.Single(_directors.Of(LicenseContract.ApproveVerb)).Payload;
        Assert.Equal(Profile, approve.BrowserProfile);
        Assert.Equal(LicenseApprovalOutcomes.Approved, TheAnswer().Outcome);
    }

    // ----- the wait for the approving Director ends at the sign-in's own deadline -----

    [Fact]
    public async Task Relay_WhenTheSignInsDeadlineIsSoonerThanTheGatewaysWait_WaitsOnlyUntilTheDeadline_AndAnswersExpired()
    {
        OneIsSetUp();
        TheApproverAnswers(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer"));
        var request = Request();
        request.NotAfterUtc = _now.AddSeconds(50);

        await Ask(request);

        Assert.Equal(TimeSpan.FromSeconds(50), Assert.Single(_directors.Of(LicenseContract.ApproveVerb)).Timeout);
        AssertRefused(LicenseRenewalErrors.Expired);
        Assert.Equal("No approval came back from the Director on the-mac before the sign-in's own deadline.", TheAnswer().Message);
    }

    [Fact]
    public async Task Relay_WhenTheSignInsDeadlineIsLaterThanTheGatewaysWait_WaitsTheGatewaysWait()
    {
        OneIsSetUp();
        TheApproverAnswers(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer"));
        var request = Request();
        request.NotAfterUtc = _now.AddMinutes(10);

        await Ask(request);

        Assert.Equal(LicenseContract.ApproveCommandTimeout, Assert.Single(_directors.Of(LicenseContract.ApproveVerb)).Timeout);
        AssertRefused(LicenseRenewalErrors.ApprovalFailed);
    }

    [Fact]
    public async Task Relay_ARequestArrivingExactlyAtItsDeadline_AnswersExpired_AndAsksNoMachine()
    {
        OneIsSetUp();
        var request = Request();
        request.NotAfterUtc = _now;

        await Ask(request);

        AssertRefused(LicenseRenewalErrors.Expired);
        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
    }

    /// <summary>The answer to one sign-in, once its relay has delivered it.</summary>
    private async Task<LicenseApprovalResult> WaitForAnswerTo(string signInId)
        => (LicenseApprovalResult)(await _directors.WaitFor(sent => sent.Verb == LicenseContract.ApprovalResultVerb
                                                                    && ((LicenseApprovalResult)sent.Payload).SignInId == signInId)).Payload;
}
