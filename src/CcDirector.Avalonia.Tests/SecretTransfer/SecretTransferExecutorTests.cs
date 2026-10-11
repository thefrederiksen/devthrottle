using CcDirector.ControlApi;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.SecretTransfer;

/// <summary>
/// The Secret Handoff mission, phase 4: the Director's two secret transfer verbs. Every test runs against a fake
/// cc-secrets process, never a real one and never a real secret store. The payload, standard output and standard
/// error each carry a planted marker, so a test can prove none of them reached an error or the log.
/// </summary>
public sealed class SecretTransferExecutorTests
{
    private const string TransferId = "0f6c1f2e-6d3a-4f43-9d61-2b8a3c4d5e6f";
    private const string PayloadMarker = "PAYLOAD-MARKER-7c1e";
    private const string StdoutMarker = "STDOUT-MARKER-91ab";
    private const string StderrMarker = "STDERR-MARKER-44de";

    private static readonly string Payload =
        $"{{\"transferId\":\"{TransferId}\",\"entry\":\"demo-entry\",\"envelope\":\"{PayloadMarker}\"}}";

    // ===================== registration =====================

    [Theory]
    [InlineData(SecretTransferExecutor.SendVerb)]
    [InlineData(SecretTransferExecutor.ReceiveVerb)]
    public void AreaFor_SecretTransferVerb_IsOwnedByTheSecretTransferArea(string verb)
    {
        // Act
        var area = SessionCommandExecutor.AreaFor(verb);

        // Assert
        Assert.IsType<SecretTransferExecutor>(area);
    }

    // ===================== what goes in =====================

    [Theory]
    [InlineData(SecretTransferExecutor.SendVerb, "transfer-send")]
    [InlineData(SecretTransferExecutor.ReceiveVerb, "transfer-receive")]
    public async Task ExecuteAsync_Verb_PayloadGoesOnStandardInputAndTheArgumentsCarryOnlyTheId(string verb, string subcommand)
    {
        // Arrange
        var runner = new FakeRunner(exitCode: 0, stdout: "{\"ok\":true,\"stored\":\"demo-entry\"}");
        var executor = new SecretTransferExecutor(runner, TimeSpan.FromSeconds(60));

        // Act
        await executor.ExecuteAsync(default, Command(verb), CancellationToken.None);

        // Assert
        Assert.Equal(new[] { subcommand, TransferId }, runner.Args);
        Assert.Equal(Payload, runner.StandardInput);
        Assert.DoesNotContain(runner.Args!, a => a.Contains(PayloadMarker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"entry\":\"demo-entry\"}")]
    [InlineData("{\"transferId\":\"--help\"}")]
    [InlineData("{\"transferId\":\"abc & calc\"}")]
    [InlineData("{\"transferId\":42}")]
    public async Task ExecuteAsync_PayloadWithoutAUsableTransferId_BadRequestAndNothingRuns(string payload)
    {
        // Arrange
        var runner = new FakeRunner(exitCode: 0, stdout: "{\"ok\":true}");
        var executor = new SecretTransferExecutor(runner, TimeSpan.FromSeconds(60));

        // Act
        var result = await executor.ExecuteAsync(default, Command(SecretTransferExecutor.SendVerb, payload), CancellationToken.None);

        // Assert
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Null(runner.Args);
    }

    // ===================== what comes back =====================

    [Theory]
    [InlineData(0, "{\"ok\":true,\"envelope\":\"ZW52ZWxvcGU=\",\"senderPublicKey\":\"cHVi\",\"senderFingerprint\":\"ab\"}")]
    [InlineData(2, "{\"ok\":false,\"reason\":\"The approval has expired.\"}")]
    public async Task ExecuteAsync_SuccessOrRefusalLine_SuccessWithThatLineAsTheBody(int exitCode, string line)
    {
        // Arrange
        var runner = new FakeRunner(exitCode, stdout: line + "\n", stderr: StderrMarker);
        var executor = new SecretTransferExecutor(runner, TimeSpan.FromSeconds(60));

        // Act
        var result = await executor.ExecuteAsync(default, Command(SecretTransferExecutor.SendVerb), CancellationToken.None);

        // Assert
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        Assert.Equal(line, result.BodyJson);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(1, "Traceback (most recent call last): " + StdoutMarker)]
    [InlineData(0, "")]
    [InlineData(0, "{\"stored\":\"" + StdoutMarker + "\"}")]
    [InlineData(0, "{\"ok\":\"yes\",\"note\":\"" + StdoutMarker + "\"}")]
    [InlineData(0, "[\"" + StdoutMarker + "\"]")]
    [InlineData(0, "{\"ok\":true}\n{\"ok\":true,\"x\":\"" + StdoutMarker + "\"}")]
    public async Task ExecuteAsync_CrashOrAnswerWithoutAnOkField_FailWithNoOutputInTheError(int exitCode, string stdout)
    {
        // Arrange
        var runner = new FakeRunner(exitCode, stdout, stderr: "boom " + StderrMarker);
        var executor = new SecretTransferExecutor(runner, TimeSpan.FromSeconds(60));

        // Act
        var result = await executor.ExecuteAsync(default, Command(SecretTransferExecutor.ReceiveVerb), CancellationToken.None);

        // Assert
        Assert.Equal(DirectorCommandStatus.Error, result.Status);
        Assert.Equal($"cc-secrets transfer-receive failed (exit {exitCode}).", result.Error);
        Assert.Null(result.BodyJson);
    }

    [Fact]
    public async Task ExecuteAsync_ToolNotInstalled_FailNamingTheCommand()
    {
        // Arrange
        var runner = new FakeRunner(exitCode: -1, stdout: "", stderr: StderrMarker, started: false);
        var executor = new SecretTransferExecutor(runner, TimeSpan.FromSeconds(60));

        // Act
        var result = await executor.ExecuteAsync(default, Command(SecretTransferExecutor.SendVerb), CancellationToken.None);

        // Assert
        Assert.Equal(DirectorCommandStatus.Error, result.Status);
        Assert.DoesNotContain(StderrMarker, result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessOutlivesTheLimit_FailNamingTheLimit()
    {
        // Arrange
        var runner = new FakeRunner(exitCode: 0, stdout: "{\"ok\":true}", hang: true);
        var executor = new SecretTransferExecutor(runner, TimeSpan.FromMilliseconds(200));

        // Act
        var result = await executor.ExecuteAsync(default, Command(SecretTransferExecutor.SendVerb), CancellationToken.None);

        // Assert
        Assert.Equal(DirectorCommandStatus.Error, result.Status);
        Assert.Equal("cc-secrets transfer-send did not finish within 0.2 seconds.", result.Error);
        Assert.True(runner.WasCancelled);
    }

    // ===================== the log =====================

    [Fact]
    public async Task ExecuteAsync_EveryOutcome_TheLogCarriesNoPayloadStandardOutputOrStandardError()
    {
        // Arrange: one run of every outcome, each one's input and output carrying a planted marker.
        var outcomes = new[]
        {
            new FakeRunner(0, $"{{\"ok\":true,\"envelope\":\"{StdoutMarker}\"}}", StderrMarker),
            new FakeRunner(2, $"{{\"ok\":false,\"reason\":\"{StdoutMarker}\"}}", StderrMarker),
            new FakeRunner(1, $"Traceback {StdoutMarker}", StderrMarker),
            new FakeRunner(-1, StdoutMarker, StderrMarker, started: false),
            new FakeRunner(0, StdoutMarker, StderrMarker, hang: true),
        };

        // Act
        IReadOnlyList<string> lines;
        using (var log = FileLog.RedirectForTests())
        {
            foreach (var runner in outcomes)
            {
                var executor = new SecretTransferExecutor(runner, TimeSpan.FromMilliseconds(200));
                await executor.ExecuteAsync(default, Command(SecretTransferExecutor.SendVerb), CancellationToken.None);
                await executor.ExecuteAsync(default, Command(SecretTransferExecutor.ReceiveVerb), CancellationToken.None);
            }
            lines = log.DrainAndReadLines();
        }

        // Assert: the runs were logged (a silent log would pass the absence checks for nothing), and none of the
        // markers reached it.
        Assert.Equal(20, lines.Count(l => l.Contains("[SecretTransferExecutor]", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.Contains("exit=0, ok=true", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("exit=2, ok=false", StringComparison.Ordinal));
        foreach (var marker in new[] { PayloadMarker, StdoutMarker, StderrMarker })
            Assert.DoesNotContain(lines, l => l.Contains(marker, StringComparison.Ordinal));
    }

    // ===================== helpers =====================

    private static DirectorCommand Command(string verb, string? payload = null) => new()
    {
        CommandId = "cmd-1",
        Verb = verb,
        SessionId = "",
        PayloadJson = payload ?? Payload,
    };

    /// <summary>Stands in for cc-secrets: records what it was given and answers as told.</summary>
    private sealed class FakeRunner : ICcSecretsRunner
    {
        private readonly ProcessRunner.Result _result;
        private readonly bool _hang;

        public FakeRunner(int exitCode, string stdout, string stderr = "", bool started = true, bool hang = false)
        {
            _result = new ProcessRunner.Result(exitCode, stdout, stderr, started);
            _hang = hang;
        }

        public IReadOnlyList<string>? Args { get; private set; }
        public string? StandardInput { get; private set; }
        public bool WasCancelled { get; private set; }

        public async Task<ProcessRunner.Result> RunAsync(IReadOnlyList<string> args, string? standardInput, CancellationToken cancellationToken)
        {
            Args = args.ToArray();
            StandardInput = standardInput;
            if (_hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    WasCancelled = true;
                    throw;
                }
            }
            return _result;
        }
    }
}
