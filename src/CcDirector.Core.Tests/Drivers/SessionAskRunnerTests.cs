using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

/// <summary>
/// Tests for <see cref="SessionAskRunner"/> (issue #509): the engine that runs a short
/// throwaway question over a REAL driver-backed session and reads back a delimited answer,
/// replacing the metered --print one-shot path. Every test runs against fakes / fixture
/// transcripts - no live agent, no network.
/// </summary>
public class SessionAskRunnerTests : IDisposable
{
    private readonly string _workDir;

    public SessionAskRunnerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "session-ask-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private SessionAskRunner FastRunner(Func<AgentKind, IAgentDriver> driverFactory, FakeAskBackend backend) =>
        new(
            driverFactory: driverFactory,
            backendFactory: () => backend,
            log: _ => { },
            quietSeconds: 0.0,
            startTimeoutSeconds: 2.0,
            pollIntervalSeconds: 0.01,
            replyStableSeconds: 0.05);

    // ---------------------------------------------------- prompt contract

    [Fact]
    public void WrapPrompt_EmbedsBothDelimiters()
    {
        var wrapped = SessionAskRunner.WrapPrompt("What is 2 + 2?");

        Assert.Contains("What is 2 + 2?", wrapped);
        Assert.Contains(SessionAskRunner.AnswerBeginMarker, wrapped);
        Assert.Contains(SessionAskRunner.AnswerEndMarker, wrapped);
    }

    [Fact]
    public void ExtractAnswer_FixtureTranscriptBlock_ReturnsExactlyTheBlock()
    {
        // A reply with prose around the delimited block (what an agent really writes).
        var reply =
            "Sure, here is the answer you asked for.\n" +
            SessionAskRunner.AnswerBeginMarker + "\n" +
            "FORTY-TWO\n" +
            SessionAskRunner.AnswerEndMarker + "\n" +
            "Let me know if you need anything else.";

        var answer = SessionAskRunner.ExtractAnswer(reply);

        Assert.Equal("FORTY-TWO", answer);
    }

    [Fact]
    public void ExtractAnswer_MissingMarkers_ThrowsNoFallback()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SessionAskRunner.ExtractAnswer("an answer with no markers at all"));
        Assert.Contains("opening answer marker", ex.Message);
    }

    [Fact]
    public void ExtractAnswer_OpeningButNoClosingMarker_Throws()
    {
        var reply = SessionAskRunner.AnswerBeginMarker + "\nhalf an answer";
        var ex = Assert.Throws<InvalidOperationException>(() => SessionAskRunner.ExtractAnswer(reply));
        Assert.Contains("closing", ex.Message);
    }

    // ------------------------------------------ launch spec: NO --print / -p

    [Fact]
    public async Task AskAsync_LaunchSpec_ContainsNoPrintFlag()
    {
        var backend = new FakeAskBackend();
        var driver = new FixtureAskDriver(AgentKind.ClaudeCode);
        driver.OnSubmit = sid => driver.SetReply(sid, Delimited("ANSWER"));
        var runner = FastRunner(_ => driver, backend);

        await runner.AskAsync(AgentKind.ClaudeCode, null, null, _workDir, "q", TimeSpan.FromSeconds(2));

        Assert.NotNull(backend.StartedArgs);
        Assert.DoesNotContain("--print", backend.StartedArgs);
        Assert.DoesNotContain("-p", backend.StartedArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void ClaudeDriver_DefaultLaunchSpec_HasNoPrintFlag()
    {
        // The real ClaudeDriver spec, asserted directly (the ask path uses this verbatim).
        var spec = new ClaudeDriver().BuildLaunchSpec(null, resumeSessionId: null);
        Assert.DoesNotContain("--print", spec.Arguments);
        Assert.DoesNotContain("-p", spec.Arguments.Split(' '));
    }

    // --------------------------------- per-driver: ClaudeCode + Pi answers

    [Fact]
    public async Task AskAsync_ClaudeCode_RealDriverWithFixtureTranscript_ReturnsParsedAnswer()
    {
        // Drive the REAL ClaudeDriver, feeding it a fixture transcript through the
        // injectable ITranscriptReader seam - no profile, no network.
        var backend = new FakeAskBackend();
        var transcripts = new FakeAskTranscriptReader();
        IAgentDriver driver = new ClaudeDriver(transcripts);

        // The launch spec preassigns the session id; capture it so the fixture is keyed to it.
        string capturedSessionId = "";
        backend.OnRawWriteText = _ => { };

        var runner = new SessionAskRunner(
            driverFactory: _ => driver,
            backendFactory: () => backend,
            log: _ => { },
            quietSeconds: 0.0,
            startTimeoutSeconds: 2.0,
            pollIntervalSeconds: 0.01,
            replyStableSeconds: 0.05);

        // When the prompt is submitted, the real ClaudeDriver echo-gates then writes Enter.
        // We satisfy the echo by echoing typed bytes, then seed the fixture transcript.
        backend.EchoTypedText = true;
        backend.OnSubmitSeen = sid =>
        {
            capturedSessionId = sid;
            transcripts.SetReply(sid, _workDir, Delimited("CLAUDE-FIXTURE-ANSWER"));
        };
        backend.SessionIdProvider = () => capturedSessionId;

        // The session id is only known after BuildLaunchSpec runs inside AskAsync, so the
        // backend learns it from the args it is Started with.
        var result = await runner.AskAsync(
            AgentKind.ClaudeCode, null, null, _workDir, "what is the answer?", TimeSpan.FromSeconds(2));

        Assert.Equal("CLAUDE-FIXTURE-ANSWER", result.Answer);
    }

    [Fact]
    public async Task AskAsync_Pi_TranscriptReadingDriverWithFixture_ReturnsParsedAnswer()
    {
        // A Pi-kind driver that DOES declare TranscriptRead (the future verified driver)
        // flows through the kind-agnostic runner. The real PiDriver lacks TranscriptRead
        // today and is covered by the unsupported test below.
        var backend = new FakeAskBackend();
        var driver = new FixtureAskDriver(AgentKind.Pi);
        driver.OnSubmit = sid => driver.SetReply(sid, "prelude\n" + Delimited("PI-FIXTURE-ANSWER"));
        var runner = FastRunner(_ => driver, backend);

        var result = await runner.AskAsync(
            AgentKind.Pi, null, null, _workDir, "ask pi", TimeSpan.FromSeconds(2));

        Assert.Equal("PI-FIXTURE-ANSWER", result.Answer);
        Assert.Single(driver.Submits);
    }

    // ----------------------------------------- unsupported: RawCli + generic

    [Fact]
    public async Task AskAsync_RawCli_RealDriver_ThrowsNotSupportedNamingTheAgent()
    {
        var backend = new FakeAskBackend();
        // Use the real driver registry: RawCli resolves to a GenericDriver with no TranscriptRead.
        var runner = new SessionAskRunner(
            driverFactory: AgentDrivers.For,
            backendFactory: () => backend,
            log: _ => { });

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            runner.AskAsync(AgentKind.RawCli, null, null, _workDir, "q", TimeSpan.FromSeconds(2)));

        Assert.Contains("RawCli", ex.Message);
        Assert.Null(backend.StartedArgs); // never even launched
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Gemini)]
    [InlineData(AgentKind.OpenCode)]
    public async Task AskAsync_GenericKindsWithoutTranscriptRead_ThrowNotSupported(AgentKind kind)
    {
        var backend = new FakeAskBackend();
        var runner = new SessionAskRunner(
            driverFactory: AgentDrivers.For,
            backendFactory: () => backend,
            log: _ => { });

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            runner.AskAsync(kind, null, null, _workDir, "q", TimeSpan.FromSeconds(2)));

        Assert.Contains(kind.ToString(), ex.Message);
    }

    // ------------------------------------------------- teardown + timeout

    [Fact]
    public async Task AskAsync_OnSuccess_ClosesTheSession()
    {
        var backend = new FakeAskBackend();
        var driver = new FixtureAskDriver(AgentKind.ClaudeCode);
        driver.OnSubmit = sid => driver.SetReply(sid, Delimited("X"));
        var logged = new List<string>();
        var runner = new SessionAskRunner(
            driverFactory: _ => driver,
            backendFactory: () => backend,
            log: logged.Add,
            quietSeconds: 0.0,
            startTimeoutSeconds: 2.0,
            pollIntervalSeconds: 0.01,
            replyStableSeconds: 0.05);

        await runner.AskAsync(AgentKind.ClaudeCode, null, null, _workDir, "q", TimeSpan.FromSeconds(2));

        Assert.True(backend.HasExited);
        Assert.Contains(logged, l => l.Contains("[SessionAskRunner] session closed"));
    }

    [Fact]
    public async Task AskAsync_OnFailure_StillClosesTheSession()
    {
        var backend = new FakeAskBackend();
        var driver = new FixtureAskDriver(AgentKind.ClaudeCode);
        // Reply with no delimiters -> ExtractAnswer throws, but the finally must still close.
        driver.OnSubmit = sid => driver.SetReply(sid, "no markers here");
        var logged = new List<string>();
        var runner = new SessionAskRunner(
            driverFactory: _ => driver,
            backendFactory: () => backend,
            log: logged.Add,
            quietSeconds: 0.0,
            startTimeoutSeconds: 2.0,
            pollIntervalSeconds: 0.01,
            replyStableSeconds: 0.05);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.AskAsync(AgentKind.ClaudeCode, null, null, _workDir, "q", TimeSpan.FromSeconds(2)));

        Assert.True(backend.HasExited);
        Assert.Contains(logged, l => l.Contains("[SessionAskRunner] session closed"));
    }

    [Fact]
    public async Task AskAsync_NoReplyWithinTimeout_ThrowsSessionAskTimeout()
    {
        var backend = new FakeAskBackend();
        var driver = new FixtureAskDriver(AgentKind.ClaudeCode); // never sets a reply
        var runner = FastRunner(_ => driver, backend);

        var ex = await Assert.ThrowsAsync<SessionAskTimeoutException>(() =>
            runner.AskAsync(AgentKind.ClaudeCode, null, null, _workDir, "q", TimeSpan.FromMilliseconds(200)));

        Assert.Contains("No reply", ex.Message);
        Assert.True(backend.HasExited); // timeout still tore the session down
    }

    [Fact]
    public async Task AskAsync_MissingWorkingDirectory_Throws()
    {
        var runner = new SessionAskRunner(driverFactory: _ => new FixtureAskDriver(AgentKind.ClaudeCode));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            runner.AskAsync(AgentKind.ClaudeCode, null, null,
                Path.Combine(_workDir, "nope"), "q", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AskAsync_EmptyPrompt_Throws()
    {
        var runner = new SessionAskRunner(driverFactory: _ => new FixtureAskDriver(AgentKind.ClaudeCode));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.AskAsync(AgentKind.ClaudeCode, null, null, _workDir, "   ", TimeSpan.FromSeconds(1)));
    }

    // ------------------------------------------- proof: the logged sequence

    [Fact]
    public async Task AskAsync_LogsLaunchSubmitReadCloseSequence()
    {
        // Captures the FileLog-format sequence for one full ask, asserts the required
        // launch -> submit -> reply-read -> session-closed order, and writes it to the
        // committed proof file (issue #509 proof target item 2). No live agent / network.
        var backend = new FakeAskBackend();
        var driver = new FixtureAskDriver(AgentKind.ClaudeCode);
        driver.OnSubmit = sid => driver.SetReply(sid, "thinking out loud\n" + Delimited("THE-PROVEN-ANSWER"));
        var logged = new List<string>();
        var runner = new SessionAskRunner(
            driverFactory: _ => driver,
            backendFactory: () => backend,
            log: logged.Add,
            quietSeconds: 0.0,
            startTimeoutSeconds: 2.0,
            pollIntervalSeconds: 0.01,
            replyStableSeconds: 0.05);

        var result = await runner.AskAsync(
            AgentKind.ClaudeCode, null, null, _workDir, "what is the proven answer?", TimeSpan.FromSeconds(2));

        Assert.Equal("THE-PROVEN-ANSWER", result.Answer);

        // The four phases must appear, in order.
        var launchIndex = logged.FindIndex(l => l.Contains("AskAsync: launched"));
        var askIndex = logged.FindIndex(l => l.Contains("AskAsync: kind="));
        var okIndex = logged.FindIndex(l => l.Contains("AskAsync OK"));
        var closedIndex = logged.FindIndex(l => l.Contains("session closed"));
        Assert.True(askIndex >= 0 && launchIndex > askIndex && okIndex > launchIndex && closedIndex > okIndex,
            "Expected order: kind -> launched -> OK(reply read) -> session closed. Lines:\n"
            + string.Join("\n", logged));

        WriteProofLog(logged, result);
    }

    private static void WriteProofLog(IReadOnlyList<string> logged, SessionAskResult result)
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return; // running outside the repo tree (CI artifact dir) - skip the file write
        var proofDir = Path.Combine(repoRoot, "docs", "cencon", "proof", "issue-509");
        Directory.CreateDirectory(proofDir);

        var sb = new StringBuilder();
        sb.AppendLine("SessionAskRunner ask sequence (issue #509 proof target item 2)");
        sb.AppendLine("Captured from AskAsync_LogsLaunchSubmitReadCloseSequence (fixture transcript, no live network).");
        sb.AppendLine("Sequence: launch (spec without -p) -> submit -> reply read -> session closed.");
        sb.AppendLine(new string('-', 78));
        foreach (var line in logged)
            sb.AppendLine(Redact(line));
        sb.AppendLine(new string('-', 78));
        sb.AppendLine($"Parsed answer: {result.Answer}");
        File.WriteAllText(Path.Combine(proofDir, "ask-sequence.log"), sb.ToString());
    }

    /// <summary>Replace the test runner's real temp working directory (which carries the
    /// machine's Windows user name) with a generic placeholder, so the committed public
    /// proof file leaks no personal information.</summary>
    private static string Redact(string line)
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        return line.Replace(temp, "<TEMP>", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string Delimited(string answer) =>
        SessionAskRunner.AnswerBeginMarker + "\n" + answer + "\n" + SessionAskRunner.AnswerEndMarker;
}

// ----------------------------------------------------------------- fakes

/// <summary>
/// In-memory backend for the ask tests: records the args it was started with, lets the
/// driver's submit see the preassigned session id (parsed from the args), and goes quiet
/// immediately so the runner's quiet gate passes. Optionally echoes typed bytes so the
/// real ClaudeDriver's echo gate is satisfied.
/// </summary>
internal sealed class FakeAskBackend : ISessionBackend
{
    private readonly CircularTerminalBuffer _buffer = new(64 * 1024);

    public string? StartedArgs { get; private set; }
    public int ProcessId { get; private set; }
    public string Status { get; private set; } = "NotStarted";
    public bool IsRunning => ProcessId != 0 && !HasExited;
    public bool HasExited { get; private set; }
    public CircularTerminalBuffer? Buffer => _buffer;

    /// <summary>Echo typed bytes back into the buffer (satisfies ClaudeDriver's echo gate).</summary>
    public bool EchoTypedText { get; set; }

    /// <summary>Invoked once when the prompt submit is observed, with the session id.</summary>
    public Action<string>? OnSubmitSeen { get; set; }

    /// <summary>Lets a test read back the session id parsed from StartedArgs.</summary>
    public Func<string>? SessionIdProvider { get; set; }

    public Action<string>? OnRawWriteText { get; set; }

    private string _sessionId = "";

#pragma warning disable CS0067 // events required by the interface, not raised by this fake
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067

    public void Start(string executable, string args, string workingDir, short cols, short rows,
        Dictionary<string, string>? environmentVars = null)
    {
        StartedArgs = args;
        ProcessId = 4242;
        Status = "Running";
        _sessionId = ParseSessionId(args);
        _buffer.Write(Encoding.UTF8.GetBytes("agent banner"));
    }

    private static string ParseSessionId(string args)
    {
        const string flag = "--session-id ";
        var idx = args.IndexOf(flag, StringComparison.Ordinal);
        if (idx < 0) return "";
        var rest = args[(idx + flag.Length)..].TrimStart();
        var end = rest.IndexOf(' ');
        return end < 0 ? rest : rest[..end];
    }

    public void Write(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        OnRawWriteText?.Invoke(text);
        if (EchoTypedText)
            _buffer.Write(data); // echo so ClaudeDriver's WaitForEcho sees the typed text
        // A non-empty typed chunk that is not a bare control byte is the prompt submit.
        if (text.Length > 1)
            OnSubmitSeen?.Invoke(_sessionId);
    }

    public Task SendTextAsync(string text)
    {
        OnSubmitSeen?.Invoke(_sessionId);
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows) { }

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        HasExited = true;
        Status = "Exited (0)";
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

/// <summary>
/// A transcript-reading driver for an arbitrary kind, used to prove the runner is
/// kind-agnostic and capability-gated. Serves a fixture reply once a submit is seen.
/// </summary>
internal sealed class FixtureAskDriver : IAgentDriver
{
    private readonly Dictionary<string, List<TurnWidgetDto>> _widgets = new();

    public FixtureAskDriver(AgentKind kind) => Kind = kind;

    public List<string> Submits { get; } = new();

    /// <summary>Invoked on submit, with the preassigned session id, so a test can seed the reply.</summary>
    public Action<string>? OnSubmit { get; set; }

    private string _lastSessionId = "";

    public AgentKind Kind { get; }

    public DriverCapabilities Capabilities =>
        DriverCapabilities.TranscriptRead | DriverCapabilities.PreassignedSessionId;

    public void SetReply(string agentSessionId, string replyText)
    {
        var list = _widgets.TryGetValue(agentSessionId, out var w) ? w : _widgets[agentSessionId] = new();
        list.Add(new TurnWidgetDto { Kind = "Text", Content = replyText });
    }

    public string ResolveExecutable(string? configuredPath) => configuredPath ?? "fixture-agent.exe";

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId)
    {
        _lastSessionId = Guid.NewGuid().ToString();
        return new AgentLaunchSpec($"{baseArgs ?? "--fixture"} --session-id {_lastSessionId}", _lastSessionId);
    }

    public Task SubmitAsync(ISessionBackend backend, string text)
    {
        Submits.Add(text);
        OnSubmit?.Invoke(_lastSessionId);
        return Task.CompletedTask;
    }

    public Task CancelAsync(ISessionBackend backend) => Task.CompletedTask;
    public Task InterruptAsync(ISessionBackend backend) => Task.CompletedTask;
    public Task ShowHistoryAsync(ISessionBackend backend) => Task.CompletedTask;
    public Task ClearContextAsync(ISessionBackend backend) => Task.CompletedTask;

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        _widgets.TryGetValue(agentSessionId, out var w) ? new List<TurnWidgetDto>(w) : new List<TurnWidgetDto>();

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) => null;

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        _widgets.Keys.Select(k => (k, DateTime.UtcNow)).ToList();
}

/// <summary>In-memory transcript store for the real ClaudeDriver delegation test.</summary>
internal sealed class FakeAskTranscriptReader : ITranscriptReader
{
    private readonly Dictionary<string, List<TurnWidgetDto>> _widgets = new();

    public void SetReply(string claudeSessionId, string repoPath, string replyText)
    {
        var list = _widgets.TryGetValue(claudeSessionId, out var w) ? w : _widgets[claudeSessionId] = new();
        list.Add(new TurnWidgetDto { Kind = "Text", Content = replyText });
    }

    public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) =>
        _widgets.TryGetValue(claudeSessionId, out var w) ? new List<TurnWidgetDto>(w) : new List<TurnWidgetDto>();

    public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) => null;

    public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) =>
        _widgets.Keys.Select(k => (k, DateTime.UtcNow)).ToList();
}
