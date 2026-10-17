using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Licenses;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.Licenses;

/// <summary>
/// License Switching contract section 8: the <c>license-sign-in</c>, <c>license-approve</c> and
/// <c>license-approval-result</c> tunnel verbs, and the cc-secrets seam that seals an approval. A payload that cannot
/// be read, or a SignInId of the wrong shape, is BadRequest with no body; every other outcome - a refusal included -
/// is a successful answer. Runs against a fake license tool and a fake cc-secrets; no sign-in exists anywhere.
/// </summary>
public sealed class LicenseSignInExecutorTests : IDisposable
{
    private const string SignInId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
    private const string AccountB = "account-b@example.com";
    private const string Approval = "APPROVAL-MARKER-91c7";
    private static readonly string Recipient = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly DateTime Now = new(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "license-sign-in-executor-" + Guid.NewGuid().ToString("N"));
    private readonly LicenseSwitching _licenses;
    private readonly SignInTool _tool = new();

    public LicenseSignInExecutorTests()
    {
        Directory.CreateDirectory(_folder);
        _licenses = new LicenseSwitching(_tool, _folder, () => Now, "the-windows-machine", "owner", enabled: true);
        Assert.True(_licenses.HolderLock.TryAcquire());
    }

    public void Dispose()
    {
        _licenses.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static DirectorCommand Command(string verb, string payload) =>
        new() { CommandId = "cmd-1", Verb = verb, SessionId = "", PayloadJson = payload };

    private static T Body<T>(DirectorCommandResult result) => JsonSerializer.Deserialize<T>(result.BodyJson!, Web)!;

    [Theory]
    [InlineData(LicenseContract.SignInVerb)]
    [InlineData(LicenseContract.ApproveVerb)]
    [InlineData(LicenseContract.ApprovalResultVerb)]
    public void AreaFor_EverySignInVerb_IsOwnedByTheSignInArea(string verb)
        => Assert.IsType<LicenseSignInExecutor>(SessionCommandExecutor.AreaFor(verb));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"signInId\":\"6F1C2A9E4B7D4E0F9A3C5B8D2E1F7A40\",\"accountEmail\":\"account-b@example.com\"}")]
    [InlineData("{\"signInId\":\"../../escape\",\"accountEmail\":\"account-b@example.com\"}")]
    [InlineData("{\"signInId\":\"6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\"}")]
    public async Task SignIn_UnreadablePayloadOrWrongShapedId_IsBadRequest_AndNothingRuns(string payload)
    {
        var result = await LicenseSignInExecutor.SignInAsync(_licenses.SignIns, Command(LicenseContract.SignInVerb, payload), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Null(result.BodyJson);
        Assert.Empty(_tool.SignIns);
        Assert.Empty(Directory.Exists(Path.Combine(_folder, "sign-in-results")) ? Directory.GetFiles(Path.Combine(_folder, "sign-in-results")) : Array.Empty<string>());
    }

    [Fact]
    public async Task SignIn_AReadableRequest_AnswersTheResult_AFailureIncluded()
    {
        var payload = JsonSerializer.Serialize(new LicenseSignInRequest
        {
            SignInId = SignInId, AccountEmail = "Account-B@Example.com", Reason = LicenseSignInReasons.Manual, NotAfterUtc = Now + LicenseContract.SignInNotAfter,
        }, Web);

        var result = await LicenseSignInExecutor.SignInAsync(_licenses.SignIns, Command(LicenseContract.SignInVerb, payload), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var answer = Body<LicenseSignInResult>(result);
        Assert.Equal(SignInId, answer.SignInId);
        Assert.Equal(LicenseSignInOutcomes.Added, answer.Outcome);
        Assert.Equal(new[] { AccountB }, _tool.SignIns);
    }

    [Fact]
    public async Task Approve_AReadableRequest_AnswersOnlyTheSealedApproval()
    {
        var runner = new FakeSecrets(exitCode: 0, stdout: "c2VhbGVkLWZvci10aGUtdGFyZ2V0\n");
        _licenses.Approver.AttachSeal(new CcSecretsLicenseApprovalSeal(runner, TimeSpan.FromSeconds(30)));
        var payload = JsonSerializer.Serialize(new LicenseApproveRequest
        {
            SignInId = SignInId, AccountEmail = AccountB, AuthorizeUrl = "https://claude.ai/oauth/authorize?login_hint=account-b%40example.com",
            BrowserProfile = "Profile B", SealToRecipient = Recipient, NotAfterUtc = Now + LicenseContract.SignInNotAfter,
        }, Web);

        var result = await LicenseSignInExecutor.ApproveAsync(_licenses.Approver, Command(LicenseContract.ApproveVerb, payload), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var answer = Body<LicenseApprovalResult>(result);
        Assert.Equal(LicenseApprovalOutcomes.Approved, answer.Outcome);
        Assert.Equal("c2VhbGVkLWZvci10aGUtdGFyZ2V0", answer.SealedApproval);
        // The approval went to cc-secrets on standard input and is in no argument and no part of the answer.
        Assert.Equal(new[] { "seal", "--to", Recipient, "--purpose", "license-approval", "--id", SignInId }, runner.Args);
        Assert.Equal(Approval + "\n", runner.StandardInput);
        Assert.DoesNotContain(Approval, result.BodyJson);
        Assert.DoesNotContain(runner.Args!, a => a.Contains(Approval));
    }

    [Theory]
    [InlineData("{\"signInId\":\"6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\",\"accountEmail\":\"account-b@example.com\"}")]
    [InlineData("{\"signInId\":\"nope\",\"accountEmail\":\"account-b@example.com\",\"authorizeUrl\":\"https://claude.ai/x\"}")]
    public async Task Approve_NoUrlOrWrongShapedId_IsBadRequest_AndNoBrowserOpens(string payload)
    {
        var result = await LicenseSignInExecutor.ApproveAsync(_licenses.Approver, Command(LicenseContract.ApproveVerb, payload), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Empty(_tool.Approvals);
    }

    [Fact]
    public async Task ApprovalResult_ForASignInThatIsWaiting_IsDelivered_AndOneNobodyWaitsFor_IsDropped()
    {
        var payload = JsonSerializer.Serialize(new LicenseApprovalResult { SignInId = SignInId, Outcome = LicenseApprovalOutcomes.Refused, ReasonCode = LicenseRenewalErrors.ApproverOffline }, Web);
        using var expected = _licenses.Approvals.Expect(SignInId);

        var delivered = LicenseSignInExecutor.DeliverApproval(_licenses.Approvals, Command(LicenseContract.ApprovalResultVerb, payload));
        var late = LicenseSignInExecutor.DeliverApproval(_licenses.Approvals, Command(LicenseContract.ApprovalResultVerb, payload));

        Assert.Equal(DirectorCommandStatus.Ok, delivered.Status);
        Assert.Contains("\"delivered\":true", delivered.BodyJson);
        Assert.True(expected.Answer.IsCompletedSuccessfully);
        Assert.Equal(LicenseRenewalErrors.ApproverOffline, (await expected.Answer).ReasonCode);
        // The same answer again - or one that arrives after the sign-in gave up - reaches nobody. Still Ok.
        Assert.Equal(DirectorCommandStatus.Ok, late.Status);
        Assert.Contains("\"delivered\":false", late.BodyJson);
    }

    [Theory]
    [InlineData("{\"signInId\":\"6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\",\"outcome\":\"something-new\"}")]
    [InlineData("{\"signInId\":\"nope\",\"outcome\":\"approved\"}")]
    [InlineData("")]
    public void ApprovalResult_UnreadableOrUnknownOutcome_IsBadRequest_AndTheWaitGoesOn(string payload)
    {
        using var expected = _licenses.Approvals.Expect(SignInId);

        var result = LicenseSignInExecutor.DeliverApproval(_licenses.Approvals, Command(LicenseContract.ApprovalResultVerb, payload));

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.False(expected.Answer.IsCompleted);
    }

    [Fact]
    public async Task Seal_Open_HandsTheSealedTextOnStandardInput_AndAnswersWhatCcSecretsPrinted()
    {
        var runner = new FakeSecrets(exitCode: 0, stdout: Approval + "\n");
        var seal = new CcSecretsLicenseApprovalSeal(runner, TimeSpan.FromSeconds(30));

        var opened = await seal.OpenAsync("c2VhbGVkLWZvci10aGUtdGFyZ2V0", SignInId, CancellationToken.None);

        Assert.Equal(Approval, opened);
        Assert.Equal(new[] { "open", "--purpose", "license-approval", "--id", SignInId }, runner.Args);
        Assert.Equal("c2VhbGVkLWZvci10aGUtdGFyZ2V0\n", runner.StandardInput);
    }

    [Theory]
    [InlineData("not a key", SignInId)]
    [InlineData("c2hvcnQ=", SignInId)]
    [InlineData(null, "../../escape")]
    public async Task Seal_ARecipientOrIdOfTheWrongShape_NeverReachesTheCommandLine(string? recipient, string signInId)
    {
        var runner = new FakeSecrets(exitCode: 0, stdout: "c2VhbGVk\n");
        var seal = new CcSecretsLicenseApprovalSeal(runner, TimeSpan.FromSeconds(30));

        Assert.Null(await seal.SealAsync(Approval, recipient ?? Recipient, signInId, CancellationToken.None));
        Assert.Null(await seal.OpenAsync("c2VhbGVk", "../../escape", CancellationToken.None));
        Assert.Null(await seal.OpenAsync("not base64 & more", SignInId, CancellationToken.None));

        Assert.Null(runner.Args);
    }

    [Theory]
    [InlineData(2, "", true)]
    [InlineData(0, "", true)]
    [InlineData(0, "this is not an envelope", true)]
    [InlineData(-1, "", false)]
    public async Task Seal_CcSecretsFailsOrIsMissingOrPrintsNoEnvelope_AnswersNothing(int exitCode, string stdout, bool started)
    {
        var seal = new CcSecretsLicenseApprovalSeal(new FakeSecrets(exitCode, stdout, started), TimeSpan.FromSeconds(30));

        Assert.Null(await seal.SealAsync(Approval, Recipient, SignInId, CancellationToken.None));
    }

    /// <summary>Stands in for cc-secrets: answers one fixed result and keeps what it was handed.</summary>
    private sealed class FakeSecrets : ICcSecretsRunner
    {
        private readonly ProcessRunner.Result _result;

        public FakeSecrets(int exitCode, string stdout, bool started = true) => _result = new ProcessRunner.Result(exitCode, stdout, "", started);

        public IReadOnlyList<string>? Args { get; private set; }

        public string? StandardInput { get; private set; }

        public Task<ProcessRunner.Result> RunAsync(IReadOnlyList<string> args, string? standardInput, CancellationToken cancellationToken)
        {
            Args = args.ToArray();
            StandardInput = standardInput;
            return Task.FromResult(_result);
        }
    }

    /// <summary>Stands in for <c>cc-devthrottle license</c>: a browser profile for every account on this machine.</summary>
    private sealed class SignInTool : ILicenseCommandLine
    {
        public List<string> SignIns { get; } = new();

        public List<(string Url, string Email, string Profile)> Approvals { get; } = new();

        public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LicenseHolding>>(new[] { new LicenseHolding { AccountEmail = "account-a@example.com", IsActive = true } });

        public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken) => ListAsync(cancellationToken);

        public Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool?> HasBrowserProfileAsync(string accountEmail, CancellationToken cancellationToken) => Task.FromResult<bool?>(true);

        public Task<LicenseSignInStart> StartSignInAsync(string accountEmail, bool askAnotherMachineWhenNoBrowser, CancellationToken cancellationToken)
        {
            SignIns.Add(accountEmail);
            return Task.FromResult(new LicenseSignInStart { Finished = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Added, SignedInAtUtc = Now } });
        }

        public Task<LicenseApproveAnswer> ApproveAsync(string authorizeUrl, string accountEmail, string browserProfile, CancellationToken cancellationToken)
        {
            Approvals.Add((authorizeUrl, accountEmail, browserProfile));
            return Task.FromResult(new LicenseApproveAnswer { Approved = true, Approval = Approval });
        }

        public Task<LicenseApproverToolAnswer> CheckApproverAsync(string browserProfile, string? accountEmail, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
