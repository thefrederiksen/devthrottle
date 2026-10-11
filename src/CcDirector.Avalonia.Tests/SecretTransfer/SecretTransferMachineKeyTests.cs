using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.SecretTransfer;

/// <summary>
/// The Secret Handoff mission, phase 4: this machine's public key, read once from <c>cc-secrets machine-key --json</c>,
/// and the Hello that carries it. Every test runs against a fake cc-secrets process.
/// </summary>
public sealed class SecretTransferMachineKeyTests
{
    /// <summary>Base64 of 32 bytes - the shape of an X25519 public key. Made up; it is no machine's key.</summary>
    private static readonly string PublicKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    // ===================== the read =====================

    [Fact]
    public async Task Start_MachineKeyAnswers_CurrentIsThePublicKeyAndTheToolRanOnce()
    {
        // Arrange
        var runner = new FakeRunner(0, $"{{\"publicKey\":\"{PublicKey}\",\"fingerprint\":\"{new string('a', 64)}\",\"created\":false}}\n");
        var machineKey = new SecretTransferMachineKey(runner, TimeSpan.FromSeconds(60));

        // Act
        await machineKey.Start();
        await machineKey.Start();

        // Assert
        Assert.Equal(PublicKey, machineKey.Current);
        Assert.Equal(1, runner.Runs);
        Assert.Equal(new[] { "machine-key", "--json" }, runner.Args);
        Assert.Null(runner.StandardInput);
    }

    [Theory]
    [InlineData(-1, "", false)]                                   // not installed
    [InlineData(2, "", true)]                                     // too old: no machine-key command
    [InlineData(1, "Traceback (most recent call last):", true)]  // crashed
    [InlineData(0, "{\"fingerprint\":\"ab\"}", true)]             // no publicKey
    [InlineData(0, "{\"publicKey\":\"c2hvcnQ=\"}", true)]         // not 32 bytes
    public async Task Start_ToolMissingFailingOrTooOld_CurrentIsEmptyAndTheReasonIsLoggedOnce(int exitCode, string stdout, bool started)
    {
        // Arrange
        var machineKey = new SecretTransferMachineKey(new FakeRunner(exitCode, stdout, started: started), TimeSpan.FromSeconds(60));

        // Act
        IReadOnlyList<string> lines;
        using (var log = FileLog.RedirectForTests())
        {
            await machineKey.Start();
            _ = machineKey.Current;
            _ = machineKey.Current;
            lines = log.DrainAndReadLines();
        }

        // Assert
        Assert.Equal("", machineKey.Current);
        Assert.Single(lines, l => l.Contains("cannot take part in a secret transfer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_ToolHangs_CurrentIsEmpty()
    {
        // Arrange
        var machineKey = new SecretTransferMachineKey(new FakeRunner(0, "", hang: true), TimeSpan.FromMilliseconds(200));

        // Act
        var key = await machineKey.Start();

        // Assert
        Assert.Equal("", key);
        Assert.Equal("", machineKey.Current);
    }

    [Fact]
    public void Current_BeforeTheReadHasFinished_IsEmptyWithoutWaiting()
    {
        // Arrange
        var gate = new TaskCompletionSource();
        var machineKey = new SecretTransferMachineKey(new FakeRunner(0, $"{{\"publicKey\":\"{PublicKey}\"}}", waitFor: gate.Task), TimeSpan.FromSeconds(60));

        // Act
        machineKey.Start();
        var whileRunning = machineKey.Current;
        gate.SetResult();

        // Assert
        Assert.Equal("", whileRunning);
    }

    // ===================== the Hello =====================

    [Fact]
    public async Task ReseedAsync_MachineKeyRead_HelloCarriesThePublicKey()
    {
        // Arrange
        var machineKey = new SecretTransferMachineKey(new FakeRunner(0, $"{{\"publicKey\":\"{PublicKey}\"}}"), TimeSpan.FromSeconds(60));
        await machineKey.Start();
        var hub = new HelloRecordingHub();
        var client = NewClient(() => machineKey.Current);

        // Act
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        // Assert
        Assert.Equal(PublicKey, Assert.Single(hub.Hellos).SecretTransferPublicKey);
    }

    [Fact]
    public async Task ReseedAsync_ToolFailed_HelloCarriesAnEmptyKey()
    {
        // Arrange
        var machineKey = new SecretTransferMachineKey(new FakeRunner(2, ""), TimeSpan.FromSeconds(60));
        await machineKey.Start();
        var hub = new HelloRecordingHub();
        var client = NewClient(() => machineKey.Current);

        // Act
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        // Assert
        Assert.Equal("", Assert.Single(hub.Hellos).SecretTransferPublicKey);
    }

    [Fact]
    public async Task ReseedAsync_KeyProviderThrows_HelloStillGoesWithAnEmptyKey()
    {
        // Arrange
        var hub = new HelloRecordingHub();
        var client = NewClient(() => throw new InvalidOperationException("the key could not be read"));

        // Act
        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        // Assert
        Assert.True(report.Completed);
        Assert.Equal("", Assert.Single(hub.Hellos).SecretTransferPublicKey);
    }

    // ===================== helpers =====================

    private static GatewayStreamClient NewClient(Func<string> secretTransferPublicKey)
    {
        // No URL: the client is never started, so nothing dials. Every call goes to the recording hub.
        return new GatewayStreamClient(new GatewayConfig(), "dir-A", "test",
            snapshot: () => new List<SessionDto>(),
            secretTransferPublicKey: secretTransferPublicKey);
    }

    /// <summary>Stands in for cc-secrets: counts its runs, records what it was given and answers as told.</summary>
    private sealed class FakeRunner : ICcSecretsRunner
    {
        private readonly ProcessRunner.Result _result;
        private readonly bool _hang;
        private readonly Task? _waitFor;

        public FakeRunner(int exitCode, string stdout, bool started = true, bool hang = false, Task? waitFor = null)
        {
            _result = new ProcessRunner.Result(exitCode, stdout, "", started);
            _hang = hang;
            _waitFor = waitFor;
        }

        public int Runs { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }
        public string? StandardInput { get; private set; }

        public async Task<ProcessRunner.Result> RunAsync(IReadOnlyList<string> args, string? standardInput, CancellationToken cancellationToken)
        {
            Runs++;
            Args = args.ToArray();
            StandardInput = standardInput;
            if (_hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (_waitFor is not null) await _waitFor;
            return _result;
        }
    }

    /// <summary>Records every Hello; every other hub call succeeds and records nothing.</summary>
    private sealed class HelloRecordingHub : IDirectorHubCalls
    {
        public List<DirectorStreamHello> Hellos { get; } = new();
        public bool IsConnected => true;

        public Task<GatewayCapabilities?> HelloAsync(DirectorStreamHello hello)
        {
            Hellos.Add(hello);
            return Task.FromResult<GatewayCapabilities?>(null);
        }

        public Task RegisterSessionKeyAsync(SessionKeyRegistration registration) => Task.CompletedTask;
        public Task PushSnapshotAsync(long sequence, SessionDto[] sessions) => Task.CompletedTask;
        public Task PushDeltaAsync(long sequence, SessionDto session) => Task.CompletedTask;
        public Task<TurnWatermark?> PushTurnsAsync(long sequence, TurnPushBatch batch, CancellationToken ct) => Task.FromResult<TurnWatermark?>(null);
        public Task RevokeSessionKeyAsync(string sessionId) => Task.CompletedTask;
        public Task PushRepoSnapshotAsync(long sequence, RepoStatusDto[] repositories) => Task.CompletedTask;
    }
}
