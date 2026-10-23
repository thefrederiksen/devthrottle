using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

/// <summary>
/// Issue #2150 - the driver half of compaction: WHICH command each tool compacts with, which tools
/// declare they cannot, and how claude decides a compaction has finished.
///
/// The point of these tests is that the answer differs per tool and is never guessed. Gemini compacts
/// with a different word than everyone else, and Copilot cannot compact at all - a driver layer that
/// quietly typed "/compact" at Copilot, or "/compact" at gemini, would be typing nonsense into a live
/// session. And a driver must never substitute a CLEAR for a compaction: that would destroy exactly
/// the work compaction exists to preserve.
/// </summary>
public sealed class CompactContextDriverTests
{
    private static ClaudeDriver NewClaudeDriver(ITranscriptReader? transcripts = null) =>
        new(transcripts ?? new EmptyTranscriptReader(), TimeSpan.Zero, TimeSpan.Zero);

    [Fact]
    public void ClaudeDriver_CompactContext_AsksForSlashCompact()
    {
        var input = NewClaudeDriver().CompactContext();

        Assert.Equal("/compact", input.Command);
    }

    /// <summary>
    /// The command must be /compact and NOT /clear. They differ by one word and by everything that
    /// matters: one summarizes the conversation, the other throws it away.
    /// </summary>
    [Fact]
    public void ClaudeDriver_CompactContext_DoesNotClear()
    {
        var input = NewClaudeDriver().CompactContext();

        Assert.NotEqual("/clear", input.Command);
    }

    [Fact]
    public void CodexDriver_CompactContext_AsksForSlashCompact()
    {
        var input = new CodexDriver().CompactContext();

        Assert.Equal("/compact", input.Command);
    }

    [Fact]
    public void PiDriver_CompactContext_AsksForSlashCompact()
    {
        var input = new PiDriver().CompactContext();

        Assert.Equal("/compact", input.Command);
    }

    /// <summary>Gemini's own catalog spells compaction /compress. This is the whole reason the command
    /// belongs to the driver rather than to one shared string at the call site.</summary>
    [Fact]
    public void GeminiDriver_CompactContext_AsksForSlashCompress()
    {
        var input = AgentDrivers.For(AgentKind.Gemini).CompactContext();

        Assert.Equal("/compress", input.Command);
    }

    [Theory]
    [InlineData(AgentKind.Grok, "/compact")]
    [InlineData(AgentKind.OpenCode, "/compact")]
    public void RegisteredGenericDrivers_CompactContext_AskForTheirOwnCommand(AgentKind kind, string expected)
    {
        var input = AgentDrivers.For(kind).CompactContext();

        Assert.Equal(expected, input.Command);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Gemini)]
    [InlineData(AgentKind.Grok)]
    [InlineData(AgentKind.OpenCode)]
    public void CompactingDrivers_DeclareTheCapability(AgentKind kind)
    {
        Assert.True(AgentDrivers.For(kind).Capabilities.HasFlag(DriverCapabilities.CompactContext));
    }

    /// <summary>
    /// Copilot's command catalog has /clear and no compaction of any kind. The absence must be DECLARED
    /// and the call must throw - a driver that fell back to /clear here would silently destroy the
    /// conversation the caller was trying to preserve.
    /// </summary>
    [Fact]
    public void CopilotDriver_DeclaresNoCompaction_AndThrows()
    {
        IAgentDriver driver = new CopilotDriver();
        var backend = new RecordingSessionBackend();

        Assert.False(driver.Capabilities.HasFlag(DriverCapabilities.CompactContext));
        Assert.Throws<NotSupportedException>(() => driver.CompactContext());
        Assert.Empty(backend.SentTexts);
    }

    /// <summary>Cursor's compaction is not live-verified, exactly as its context clear is not. An
    /// unverified verb stays absent rather than being typed hopefully at a real session.</summary>
    [Fact]
    public void CursorDriver_DeclaresNoCompaction_AndThrows()
    {
        IAgentDriver driver = new CursorDriver();
        var backend = new RecordingSessionBackend();

        Assert.False(driver.Capabilities.HasFlag(DriverCapabilities.CompactContext));
        Assert.Throws<NotSupportedException>(() => driver.CompactContext());
        Assert.Empty(backend.SentTexts);
    }

    /// <summary>A generic driver built with no compaction command has none, and says so.</summary>
    [Fact]
    public void GenericDriver_WithoutACompactCommand_DeclaresNothingAndThrows()
    {
        var driver = new GenericDriver(AgentKind.RawCli);
        var backend = new RecordingSessionBackend();

        Assert.False(driver.Capabilities.HasFlag(DriverCapabilities.CompactContext));
        Assert.Throws<NotSupportedException>(() => driver.CompactContext());
        Assert.Empty(backend.SentTexts);
    }

    // ===== The completion signal =====

    [Fact]
    public void ClaudeDriver_HasCompactedSince_TrueWhenTheMarkIsNewerThanTheRequest()
    {
        var submittedAt = new DateTime(2026, 7, 25, 10, 0, 0, DateTimeKind.Utc);
        var driver = NewClaudeDriver(new StubTranscriptReader(submittedAt.AddSeconds(30)));

        Assert.True(driver.HasCompactedSince("agent-1", @"C:\repo", submittedAt));
    }

    /// <summary>
    /// The session being rescued has almost always compacted BEFORE - that is how it filled up again.
    /// An older mark must not be read as the compaction we just asked for, or the follow-up prompt
    /// would fire immediately, into a composer still busy summarizing.
    /// </summary>
    [Fact]
    public void ClaudeDriver_HasCompactedSince_FalseForAnEarlierCompaction()
    {
        var submittedAt = new DateTime(2026, 7, 25, 10, 0, 0, DateTimeKind.Utc);
        var driver = NewClaudeDriver(new StubTranscriptReader(submittedAt.AddHours(-3)));

        Assert.False(driver.HasCompactedSince("agent-1", @"C:\repo", submittedAt));
    }

    [Fact]
    public void ClaudeDriver_HasCompactedSince_FalseWhenTheTranscriptHasNeverBeenCompacted()
    {
        var driver = NewClaudeDriver(new StubTranscriptReader(compactionUtc: null));

        Assert.False(driver.HasCompactedSince("agent-1", @"C:\repo", DateTime.UtcNow));
    }

    [Fact]
    public void ClaudeDriver_DeclaresItCanReportCompletion()
    {
        Assert.True(NewClaudeDriver().Capabilities.HasFlag(DriverCapabilities.CompactCompletionReport));
    }

    /// <summary>
    /// Typing a command is not the same competence as knowing when it finished. These tools can be told
    /// to compact but their records are not read here, so they must NOT claim a completion report -
    /// that claim is what compact-and-continue times the follow-up on.
    /// </summary>
    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Gemini)]
    [InlineData(AgentKind.Grok)]
    [InlineData(AgentKind.OpenCode)]
    public void DriversWithoutRecordsWeRead_DoNotClaimACompletionReport(AgentKind kind)
    {
        var driver = AgentDrivers.For(kind);

        Assert.False(driver.Capabilities.HasFlag(DriverCapabilities.CompactCompletionReport));
        Assert.Throws<NotSupportedException>(() => driver.HasCompactedSince("agent-1", @"C:\repo", DateTime.UtcNow));
    }
}
