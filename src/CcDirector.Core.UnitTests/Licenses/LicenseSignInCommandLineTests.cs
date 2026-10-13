using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// Reading <c>cc-devthrottle license renew | sign-in | approve --json</c>, and the conversation with a sign-in that
/// waits for its approval from another machine. The conversation runs against in-memory streams that behave as the
/// tool does: no process, no sign-in.
/// </summary>
public sealed class LicenseSignInCommandLineTests
{
    private const string Url = "https://claude.ai/oauth/authorize?code=true&state=s1&login_hint=account-b%40example.com";
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void ReadRenewalRows_EveryResultTheToolWrites_ReadsIt_AndNeverItsSentence()
    {
        var marker = FakeLicenseCommandLine.ToolTextMarker;
        var json = """
            {"count": 4, "failed": 2, "accounts": [
              {"accountEmail": "Account-A@Example.com", "role": "active", "result": "renewed",
               "signedInAtUtc": "2026-10-12T03:00:00Z", "signInExpiresAtUtc": "2026-11-10T02:00:00Z", "browser": "P", "detail": null},
              {"accountEmail": "account-b@example.com", "role": "spare", "result": "not-due", "signInExpiresAtUtc": "2026-11-01T00:00:00Z"},
              {"accountEmail": "account-e@example.com", "role": "spare", "result": "due", "browser": null},
              {"accountEmail": "account-c@example.com", "role": "spare", "result": "failed-browser-not-signed-in", "detail": "MARKER"},
              {"accountEmail": "account-d@example.com", "role": "spare", "result": "failed-MARKER", "detail": "MARKER"}
            ]}
            """.Replace("MARKER", marker);

        var rows = CcDevthrottleLicenseCommandLine.ReadRenewalRows(json);

        Assert.Equal("account-a@example.com", rows[0].AccountEmail);
        Assert.True(rows[0].IsActive);
        Assert.Equal(LicenseRenewalResults.Renewed, rows[0].Result);
        Assert.Equal(new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc), rows[0].SignInExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, rows[0].SignedInAtUtc!.Value.Kind);
        Assert.Equal(LicenseRenewalResults.NotDue, rows[1].Result);
        Assert.Equal((LicenseRenewalResults.Due, null), (rows[2].Result, rows[2].Error));
        // Whether a profile is set here is carried, so a due account with none is never signed in just to fail.
        Assert.False(rows[2].HasBrowserProfileHere);
        Assert.True(rows[0].HasBrowserProfileHere);
        Assert.Equal((LicenseRenewalResults.Failed, LicenseRenewalErrors.BrowserNotSignedIn), (rows[3].Result, rows[3].Error));
        // A failure word outside the contract could be anything the tool printed: it never travels.
        Assert.Equal((LicenseRenewalResults.Failed, LicenseRenewalErrors.LoginFailed), (rows[4].Result, rows[4].Error));
        Assert.DoesNotContain(rows, r => (r.Error ?? "").Contains(marker) || r.Result.Contains(marker));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"accounts\": [{\"accountEmail\": \"a@example.com\", \"role\": \"owner\", \"result\": \"renewed\"}]}")]
    [InlineData("{\"accounts\": [{\"accountEmail\": \"a@example.com\", \"role\": \"spare\", \"result\": \"something-new\"}]}")]
    [InlineData("{\"accounts\": [{\"role\": \"spare\", \"result\": \"renewed\"}]}")]
    public void ReadRenewalRows_AnAnswerItCannotRead_Throws_SoNoRunIsCountedAsDone(string output)
        => Assert.Throws<LicenseCommandLineException>(() => CcDevthrottleLicenseCommandLine.ReadRenewalRows(output));

    [Fact]
    public void ReadSignInAnswer_SuccessAfterTheUrlLine_ReadsTheDates()
    {
        var output = "authorize-url: " + Url + "\n" +
                     "{\"accountEmail\": \"account-b@example.com\", \"result\": \"added\", \"role\": \"spare\", " +
                     "\"signedInAtUtc\": \"2026-10-12T03:00:00Z\", \"signInExpiresAtUtc\": \"2026-11-10T02:00:00Z\"}\n";

        var answer = CcDevthrottleLicenseCommandLine.ReadSignInAnswer(output);

        Assert.True(answer.Succeeded);
        Assert.Equal(LicenseSignInOutcomes.Added, answer.Outcome);
        Assert.Equal(new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc), answer.SignedInAtUtc);
        Assert.Equal(new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc), answer.SignInExpiresAtUtc);
    }

    [Theory]
    [InlineData("{\"result\": \"failed\", \"reason\": \"no-approver\"}", LicenseRenewalErrors.NoApprover)]
    [InlineData("{\"result\": \"failed\", \"reason\": \"store-refused\"}", LicenseRenewalErrors.StoreRefused)]
    [InlineData("{\"result\": \"failed\", \"reason\": \"TOOL-TEXT-MARKER-5e2a\"}", LicenseRenewalErrors.LoginFailed)]
    [InlineData("{\"result\": \"failed\"}", LicenseRenewalErrors.LoginFailed)]
    [InlineData("{\"result\": \"something-new\"}", LicenseRenewalErrors.LoginFailed)]
    [InlineData("Error: it broke", LicenseRenewalErrors.LoginFailed)]
    [InlineData("", LicenseRenewalErrors.LoginFailed)]
    public void ReadSignInAnswer_AFailureOrNoAnswer_IsFailedWithAContractWord(string output, string expected)
    {
        var answer = CcDevthrottleLicenseCommandLine.ReadSignInAnswer(output);

        Assert.False(answer.Succeeded);
        Assert.Equal(expected, answer.ReasonCode);
    }

    [Theory]
    [InlineData("{\"result\": \"approved\", \"code\": \"abc#state\"}", true, null)]
    [InlineData("{\"result\": \"approved\", \"code\": \"\"}", false, LicenseRenewalErrors.ApprovalFailed)]
    [InlineData("{\"result\": \"approved\"}", false, LicenseRenewalErrors.ApprovalFailed)]
    [InlineData("{\"result\": \"refused\", \"reason\": \"wrong-account\"}", false, LicenseRenewalErrors.WrongAccount)]
    [InlineData("{\"result\": \"refused\", \"reason\": \"TOOL-TEXT-MARKER-5e2a\"}", false, LicenseRenewalErrors.ApprovalFailed)]
    [InlineData("abc#state", false, LicenseRenewalErrors.ApprovalFailed)]
    public void ReadApproveAnswer_OnlyAnApprovedAnswerWithItsApproval_IsApproved(string output, bool approved, string? reason)
    {
        var answer = CcDevthrottleLicenseCommandLine.ReadApproveAnswer(output);

        Assert.Equal(approved, answer.Approved);
        Assert.Equal(reason, answer.ReasonCode);
        Assert.Equal(approved ? "abc#state" : null, answer.Approval);
    }

    [Fact]
    public async Task Conversation_Complete_TypesTheApprovalAsOneLine_ThenReadsTheAnswer()
    {
        var tool = new FakeSignInTool(Url);

        var start = await tool.Conversation(Step).StartAsync(CancellationToken.None);
        var answer = await start.Waiting!.CompleteAsync("  the-approval#state  ", CancellationToken.None);

        Assert.Equal(Url, start.Waiting.AuthorizeUrl);
        Assert.Equal("the-approval#state" + Environment.NewLine, tool.Typed);
        Assert.True(tool.InputClosed);
        Assert.Equal(LicenseSignInOutcomes.Added, answer.Outcome);
        Assert.False(tool.Killed);
        Assert.True(tool.Released);
    }

    [Fact]
    public async Task Conversation_Abandon_ClosesTheInputWithNothingTyped_SoTheToolEndsTheLoginItself()
    {
        var tool = new FakeSignInTool(Url);
        var start = await tool.Conversation(Step).StartAsync(CancellationToken.None);

        await start.Waiting!.AbandonAsync(CancellationToken.None);

        Assert.Equal("", tool.Typed);
        Assert.True(tool.InputClosed);
        Assert.False(tool.Killed);   // ended by end of input, never by a kill: the tool removes its own empty folder
        Assert.True(tool.Released);
        await Assert.ThrowsAsync<InvalidOperationException>(() => start.Waiting.CompleteAsync("late", CancellationToken.None));
    }

    [Fact]
    public async Task Conversation_DisposedWithoutAnEnd_IsAbandoned()
    {
        var tool = new FakeSignInTool(Url);
        var start = await tool.Conversation(Step).StartAsync(CancellationToken.None);

        await start.Waiting!.DisposeAsync();

        Assert.True(tool.InputClosed);
        Assert.Equal("", tool.Typed);
    }

    [Fact]
    public async Task Conversation_DisposedAfterCompleting_TypesNothingMore()
    {
        var tool = new FakeSignInTool(Url);
        var start = await tool.Conversation(Step).StartAsync(CancellationToken.None);
        await start.Waiting!.CompleteAsync("the-approval#state", CancellationToken.None);

        await start.Waiting.DisposeAsync();

        Assert.Equal("the-approval#state" + Environment.NewLine, tool.Typed);
    }

    [Fact]
    public async Task Conversation_ToolEndsBeforeAUrl_AnswersItsReason_AndNothingWaits()
    {
        var tool = new FakeSignInTool(url: null, endsWith: "{\"result\": \"failed\", \"reason\": \"store-refused\"}");

        var start = await tool.Conversation(Step).StartAsync(CancellationToken.None);

        Assert.Null(start.Waiting);
        Assert.Equal(LicenseRenewalErrors.StoreRefused, start.Finished!.ReasonCode);
        Assert.True(tool.Released);
    }

    [Theory]
    [InlineData("{\"accountEmail\": \"account-b@example.com\", \"browser\": \"P\"}", true)]
    [InlineData("{\"accountEmail\": \"account-b@example.com\", \"browser\": null}", false)]
    [InlineData("{\"accountEmail\": \"account-b@example.com\", \"browser\": \"  \"}", false)]
    public void ReadHasBrowserProfile_ReadsTheToolsAnswer(string output, bool expected) =>
        Assert.Equal(expected, CcDevthrottleLicenseCommandLine.ReadHasBrowserProfile(output));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"accountEmail\": \"account-b@example.com\"}")]
    [InlineData("[]")]
    public void ReadHasBrowserProfile_AnAnswerItCannotRead_IsNotGuessed(string output) =>
        Assert.Throws<LicenseCommandLineException>(() => CcDevthrottleLicenseCommandLine.ReadHasBrowserProfile(output));

    [Fact]
    public void TheDirectorsTimeLimits_FitInsideTheGatewaysWaits()
    {
        // Review of 3916, finding 2. An approval made for a Gateway that stopped listening spends a one-use answer
        // for nothing; a sign-in that succeeds after the Gateway recorded a timeout is a lie in the Gateway's log.
        Assert.True(LicenseContract.ApproveToolTimeout < LicenseContract.ApproveCommandTimeout);
        Assert.True(LicenseContract.SignInApprovalWait + LicenseContract.SignInFinishAllowance < LicenseContract.SignInCommandTimeout);
        Assert.True(CcDevthrottleLicenseCommandLine.SignInTimeout < LicenseContract.SignInCommandTimeout);
        Assert.Equal(LicenseContract.SignInFinishAllowance, CcDevthrottleLicenseCommandLine.FinishTimeout);
        // The other machine's approval, at its slowest, still lands inside the target's wait for it.
        Assert.True(LicenseContract.ApproveToolTimeout < LicenseContract.SignInApprovalWait);
        Assert.True(LicenseContract.SignInApprovalWait <= LicenseContract.SignInNotAfter);
    }

    [Fact]
    public async Task Conversation_ToolSignsInHereWithoutAsking_IsFinishedWithItsAnswer()
    {
        // A browser profile is set for the account on this machine: the tool approves there and never prints a URL.
        var tool = new FakeSignInTool(url: null, endsWith: "{\"result\": \"renewed\", \"signInExpiresAtUtc\": \"2026-11-10T02:00:00Z\"}");

        var start = await tool.Conversation(Step).StartAsync(CancellationToken.None);

        Assert.Null(start.Waiting);
        Assert.True(start.Finished!.Succeeded);
        Assert.Equal(LicenseSignInOutcomes.Renewed, start.Finished.Outcome);
        Assert.Equal(new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc), start.Finished.SignInExpiresAtUtc);
        Assert.Equal("", tool.Typed);
        Assert.False(tool.Killed);
    }

    [Fact]
    public async Task Conversation_NoUrlInTime_KillsTheToolAndFails()
    {
        var tool = new FakeSignInTool(url: null, hangs: true);

        var start = await tool.Conversation(Short, Step).StartAsync(CancellationToken.None);

        Assert.Null(start.Waiting);
        Assert.Equal(LicenseRenewalErrors.LoginFailed, start.Finished!.ReasonCode);
        Assert.True(tool.Killed);
    }

    [Fact]
    public async Task Conversation_ToolDoesNotEndAfterTheApproval_IsKilled_AndTheAnswerIsAFailedLogin()
    {
        var tool = new FakeSignInTool(Url, hangsAfterInput: true);
        var conversation = tool.Conversation(Step, Short);
        var start = await conversation.StartAsync(CancellationToken.None);

        var answer = await start.Waiting!.CompleteAsync("the-approval#state", CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.LoginFailed, answer.ReasonCode);
        Assert.True(tool.Killed);
    }

    /// <summary>
    /// Behaves as <c>license sign-in --remote-approval --json</c> does on its two pipes: prints the authorize URL line,
    /// then prints nothing until its input is closed - an approval typed in first makes the answer "added", none makes
    /// it failed with approval-failed, exactly the tool's own rule.
    /// </summary>
    private sealed class FakeSignInTool
    {
        private readonly string? _url;
        private readonly string? _endsWith;
        private readonly bool _hangs;
        private readonly bool _hangsAfterInput;
        private readonly StringWriter _typed = new();
        private readonly TaskCompletionSource _inputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeSignInTool(string? url, string? endsWith = null, bool hangs = false, bool hangsAfterInput = false)
        {
            _url = url;
            _endsWith = endsWith;
            _hangs = hangs;
            _hangsAfterInput = hangsAfterInput;
        }

        public string Typed => _typed.ToString();
        public bool InputClosed => _inputClosed.Task.IsCompleted;
        public bool Killed { get; private set; }
        public bool Released { get; private set; }

        public LicenseRemoteSignInConversation Conversation(TimeSpan startTimeout, TimeSpan? finishTimeout = null) => new(
            new Output(this), new Input(this),
            waitForExit: ct => _inputClosed.Task.WaitAsync(ct),
            kill: () => Killed = true,
            release: () => Released = true,
            startTimeout, finishTimeout ?? startTimeout);

        private sealed class Input : StringWriter
        {
            private readonly FakeSignInTool _tool;

            public Input(FakeSignInTool tool) => _tool = tool;

            public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            {
                _tool._typed.Write(buffer.ToString() + Environment.NewLine);
                return Task.CompletedTask;
            }

            public override void Close() => _tool._inputClosed.TrySetResult();
        }

        private sealed class Output : TextReader
        {
            private readonly FakeSignInTool _tool;
            private bool _urlPrinted;

            public Output(FakeSignInTool tool) => _tool = tool;

            public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                if (_tool._hangs) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (_tool._url is not null && !_urlPrinted)
                {
                    _urlPrinted = true;
                    return CcDevthrottleLicenseCommandLine.AuthorizeUrlPrefix + _tool._url;
                }
                if (_tool._endsWith is { } last && !_urlPrinted)
                {
                    _urlPrinted = true;
                    return last;
                }
                return null;
            }

            public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
            {
                // The tool prints its answer only once its input has ended.
                await _tool._inputClosed.Task.WaitAsync(cancellationToken);
                if (_tool._hangsAfterInput) await Task.Delay(Timeout.Infinite, cancellationToken);
                return _tool.Typed.Trim().Length > 0
                    ? "{\"accountEmail\": \"account-b@example.com\", \"result\": \"added\"}\n"
                    : "{\"accountEmail\": \"account-b@example.com\", \"result\": \"failed\", \"reason\": \"approval-failed\"}\n";
            }
        }
    }
}
