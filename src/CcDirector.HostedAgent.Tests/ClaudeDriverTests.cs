using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;
using Xunit;
using CcDirector.Core.Tests.Drivers;

namespace CcDirector.HostedAgent.Tests;

public class ClaudeDriverTests : IDisposable
{
    /// <summary>
    /// This suite asserts submit behaviour ON A MACHINE WITH MEMORY TO SPARE, which is what it has
    /// always asserted - issue #2818 left that path untouched. Pinning it is not a formality: the submit
    /// path reads the machine now, and an unpinned suite passes or fails according to how much memory
    /// the build agent happens to have free. This exact gap was found when a HostedAgent test failed on
    /// a laptop that had drifted into Tight while passing on the same code minutes earlier.
    /// </summary>
    private readonly PinnedMachineMemory _machine = PinnedMachineMemory.Healthy();

    public void Dispose() => _machine.Dispose();


    [Fact]
    public void Capabilities_DeclareTheFullClaudeSet()
    {
        var driver = new ClaudeDriver();
        Assert.Equal(AgentKind.ClaudeCode, driver.Kind);
        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.ClearContext));
        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.Cancel));
        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.TranscriptRead));
        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.PreassignedSessionId));
        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.ModelSelection));
    }

    [Fact]
    public void ModelMetadata_DeclaresFlagAndKnownModels()
    {
        // Issue #527: the driver owns model knowledge (flag + list), mirroring slash commands.
        var driver = new ClaudeDriver();

        Assert.Equal("--model", driver.ModelFlag);
        Assert.NotEmpty(driver.KnownModels);
        // The 1M-context Opus option uses the [1m] suffix - the only way to request 1M context.
        Assert.Contains(driver.KnownModels, m => m.Id == "opus[1m]");
        Assert.Contains(driver.KnownModels, m => m.Id == "sonnet");
        // No entry is the empty "use default" sentinel - that is the unset-model state, not a model.
        Assert.DoesNotContain(driver.KnownModels, m => string.IsNullOrEmpty(m.Id));
    }

    [Fact]
    public void BuildLaunchSpec_NewSession_PreassignsIdAndDefaultArgs()
    {
        var spec = new ClaudeDriver().BuildLaunchSpec(baseArgs: null, resumeSessionId: null);

        Assert.NotNull(spec.PreassignedSessionId);
        // Claude's no-args default is the automatic permission mode, matching the catalog's default
        // preset, so every path agrees on what "the Claude default" means.
        Assert.Contains("--permission-mode auto", spec.Arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", spec.Arguments);
        Assert.Contains($"--session-id {spec.PreassignedSessionId}", spec.Arguments);
    }

    [Fact]
    public void HeadlessDefaultArgs_KeepsFullBypass_NotTheAutomaticMode()
    {
        // A hosted agent has no human to answer a permission prompt, so it must not inherit the
        // interactive automatic default - it would simply hang on the first approval.
        Assert.Equal("--dangerously-skip-permissions", ClaudeDriver.HeadlessDefaultArgs);
        Assert.NotEqual(ClaudeDriver.DefaultArgs, ClaudeDriver.HeadlessDefaultArgs);
    }

    [Fact]
    public void BuildLaunchSpec_Resume_UsesResumeFlagWithoutPreassignment()
    {
        var spec = new ClaudeDriver().BuildLaunchSpec(baseArgs: null, resumeSessionId: "abc-123");

        Assert.Null(spec.PreassignedSessionId);
        Assert.Contains("--resume abc-123", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_CustomBaseArgs_ArePreserved()
    {
        var spec = new ClaudeDriver().BuildLaunchSpec("--model opus", resumeSessionId: null);
        Assert.StartsWith("--model opus", spec.Arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", spec.Arguments);
    }

    [Fact]
    public async Task CancelAsync_WritesASingleEscByte()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new ClaudeDriver().CancelAsync(backend);

        var write = Assert.Single(backend.RawWrites);
        var b = Assert.Single(write);
        Assert.Equal(0x1B, b);
    }

    [Fact]
    public async Task ClearContextAsync_SubmitsSlashClear()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new ClaudeDriver().ClearContextAsync(backend);

        Assert.Contains("/clear", backend.SentTexts);
    }

    private static ClaudeDriver FastDriver(FakeTranscriptReader? reader = null) => new(
        reader ?? new FakeTranscriptReader(),
        echoTimeout: TimeSpan.FromMilliseconds(300),
        echoPollInterval: TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task SubmitAsync_EchoMatches_TypesTextThenEnter()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        // The TUI echoes typed characters back into the terminal stream.
        backend.OnRawWrite = bytes => backend.EmitOutput(System.Text.Encoding.UTF8.GetString(bytes));

        await FastDriver().SubmitAsync(backend, "hello there");

        Assert.Equal(2, backend.RawWrites.Count);
        Assert.Equal("hello there", System.Text.Encoding.UTF8.GetString(backend.RawWrites[0]));
        Assert.Equal(new byte[] { 0x0D }, backend.RawWrites[1]);
    }

    /// <summary>
    /// The live failure: the composer echoes the text (so it provably arrived) but the TUI swallows
    /// the submitting Enter, so the turn never starts and the prompt is parked. The real driver must
    /// FAIL here. Returning quietly is what let a phone dictation sit in the composer while the
    /// session was marked Working and the next send appended to the orphan.
    /// </summary>
    [Fact]
    public async Task SubmitAsync_EchoesButTuiSwallowsTheEnter_ThrowsInsteadOfParkingSilently()
    {
        var backend = new FakeBackend { SwallowEnter = true };
        backend.Start("x", "", ".", 80, 24);
        backend.OnRawWrite = bytes => backend.EmitOutput(System.Text.Encoding.UTF8.GetString(bytes));

        var error = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => FastDriver().SubmitAsync(backend, "a dictation that never sends"));

        Assert.Contains("parked in the composer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAsync_SlashCorruptedEcho_ClearsComposerAndRetypes()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        var typed = 0;
        backend.OnRawWrite = bytes =>
        {
            var s = System.Text.Encoding.UTF8.GetString(bytes);
            if (s == "Write a poem")
            {
                typed++;
                // First attempt: the TUI race prepends a stray "/" (the live HQ-4 bug).
                backend.EmitOutput(typed == 1 ? "/Write a poem" : "Write a poem");
            }
        };

        await FastDriver().SubmitAsync(backend, "Write a poem");

        // text, Esc (composer clear), text again, Enter.
        Assert.Equal(4, backend.RawWrites.Count);
        Assert.Equal(new byte[] { 0x1B }, backend.RawWrites[1]);
        Assert.Equal(new byte[] { 0x0D }, backend.RawWrites[3]);
        Assert.Equal(2, typed);
    }

    [Fact]
    public async Task SubmitAsync_EchoNeverAppears_ThrowsAfterTwoAttempts()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        // No echo is ever emitted - the TUI is swallowing input.

        // The driver throws the specific ComposerNotAcceptingInputException (a subclass of
        // InvalidOperationException) since #1152; xUnit's ThrowsAsync matches the type exactly, so
        // assert the exact thrown type rather than the base type.
        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => FastDriver().SubmitAsync(backend, "hello"));
        Assert.Contains("composer never echoed", ex.Message);
    }

    [Fact]
    public async Task SubmitAsync_MultiLineInput_DelegatesToBackendTempFilePath()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        backend.OnRawWrite = bytes =>
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (text == "line one\nline two")
                backend.EmitOutput(text);
        };

        await FastDriver().SubmitAsync(backend, "line one\nline two");

        Assert.Equal(2, backend.RawWrites.Count);
        Assert.Equal("line one\nline two", System.Text.Encoding.UTF8.GetString(backend.RawWrites[0]));
        Assert.Equal(new byte[] { 0x0D }, backend.RawWrites[1]);
        Assert.Empty(backend.SentTexts);
    }

    [Fact]
    public void StripAnsi_RemovesCsiAndOscSequences()
    {
        var raw = "\x1B[31mhello\x1B[0m \x1B]0;title\aworld\x1B[2K";
        Assert.Equal("hello world", ClaudeDriver.StripAnsi(raw));
    }

    [Fact]
    public void ResolveExecutable_ConfiguredPathMissing_Throws()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => new ClaudeDriver().ResolveExecutable(@"Q:\nope\claude.exe"));
        // ClaudeDriver now resolves through the shared ExecutableResolver, so a missing configured path
        // fails with the unified "could not resolve" message that names the path - still a
        // FileNotFoundException, which HostedAgent.StartAsync catches.
        Assert.Contains("Could not resolve", ex.Message);
        Assert.Contains(@"Q:\nope\claude.exe", ex.Message);
    }

    [Fact]
    public void ResolveExecutable_ConfiguredPathExists_ReturnsIt()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"claude-driver-test-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "fake");
        try
        {
            Assert.Equal(tmp, new ClaudeDriver().ResolveExecutable(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TranscriptMethods_DelegateToTheReader()
    {
        var reader = new FakeTranscriptReader();
        reader.Widgets["sid-1"] = new List<TurnWidgetDto> { new() { Kind = "Text", Content = "hi" } };
        reader.Usage["sid-1"] = new SessionUsageDto { ContextTokens = 42 };
        reader.Transcripts.Add(("sid-1", DateTime.UtcNow));
        var driver = new ClaudeDriver(reader);

        Assert.Single(driver.ReadWidgets("sid-1", "repo"));
        Assert.NotNull(driver.ReadUsage("sid-1", "repo"));
        Assert.Equal(42, driver.ReadUsage("sid-1", "repo")?.ContextTokens);
        Assert.Single(driver.ListTranscripts("repo"));
        Assert.Empty(driver.ReadWidgets("unknown", "repo"));
        Assert.Null(driver.ReadUsage("unknown", "repo"));
    }
}
