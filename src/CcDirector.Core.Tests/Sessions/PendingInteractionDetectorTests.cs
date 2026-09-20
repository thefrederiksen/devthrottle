using CcDirector.Core.Agents;

using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The detection that finally makes <see cref="Session.PendingInteraction"/> mean something: a session
/// is holding a question box when the agent's own transcript carries a call to Claude Code's
/// AskUserQuestion or ExitPlanMode with no tool result yet.
///
/// EVERY TEST HERE DRIVES REAL TRANSCRIPT CONTENT. Each one writes a JSONL file in the shape Claude
/// Code writes and hands the session that file, so the product's own parser
/// (<see cref="Core.Claude.StreamMessageParser"/>) is in the path. Hand-built ContentBlock objects
/// would prove the sorting logic and nothing about whether a real transcript ever produces it.
/// </summary>
public sealed class PendingInteractionDetectorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "PendingInteraction_" + Guid.NewGuid().ToString("N"));

    public PendingInteractionDetectorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory that will not go is not a test failure */ }
    }

    // ---- the transcript lines, in the shape Claude Code writes them -------------------------------

    private const string UserAsks =
        """
        {"type":"user","message":{"role":"user","content":"Cut me a worktree"},"uuid":"11111111-1111-4111-8111-111111111111","timestamp":"2026-09-20T10:00:00.000Z"}
        """;

    private const string AssistantSpeaks =
        """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I need one thing from you first."}]},"uuid":"22222222-2222-4222-8222-222222222222","timestamp":"2026-09-20T10:00:01.000Z"}
        """;

    private const string AsksWhichBranch =
        """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01Branch","name":"AskUserQuestion","input":{"questions":[{"question":"Which branch should I cut the worktree from?","header":"Branch","multiSelect":false,"options":[{"label":"origin/main","description":"the trunk"},{"label":"the current branch","description":"whatever is checked out here"}]}]}}]},"uuid":"33333333-3333-4333-8333-333333333333","timestamp":"2026-09-20T10:00:02.000Z"}
        """;

    private const string WhichBranchAnswered =
        """
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01Branch","content":"origin/main"}]},"uuid":"44444444-4444-4444-8444-444444444444","timestamp":"2026-09-20T10:01:00.000Z"}
        """;

    private const string AsksWhichAgent =
        """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_02Agent","name":"AskUserQuestion","input":{"questions":[{"question":"Which agent should run it?","header":"Agent","multiSelect":false,"options":[{"label":"Codex","description":"a different family"},{"label":"Claude Code","description":"the same family"}]}]}}]},"uuid":"55555555-5555-4555-8555-555555555555","timestamp":"2026-09-20T10:02:00.000Z"}
        """;

    private const string OffersThePlan =
        """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_03Plan","name":"ExitPlanMode","input":{"plan":"## The plan\n\nOne: read the code. Two: write the test."}}]}}
        """;

    private const string RunsABuild =
        """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_04Bash","name":"Bash","input":{"command":"dotnet build cc-director.sln","description":"Build the solution"}}]}}
        """;

    private const string ReadsAFile =
        """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_05Read","name":"Read","input":{"file_path":"D:\\ReposFred\\devthrottle\\README.md"}}]}}
        """;

    // ---- the fixtures ----------------------------------------------------------------------------

    private string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// A Claude Code session pointed at a transcript on disk, exactly as the SessionStart hook points a
    /// real one. Parked at a turn end, because that is when the detection runs.
    /// </summary>
    private static Session ClaudeSessionOn(string? transcriptPath)
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: new QuietBackend(),
            claudeSessionId: null,
            activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow,
            customName: "asking",
            customColor: null);

        if (transcriptPath is not null)
            session.UpdateClaudeSessionPointer(null, transcriptPath, "test");

        return session;
    }

    // ---- what the detection must say -------------------------------------------------------------

    /// <summary>
    /// The case the whole mission turns on: the agent called AskUserQuestion and no result has come
    /// back, so a question box is on the user's screen right now - and it is reported with the
    /// question's own words and its own options, not a generic label.
    /// </summary>
    [Fact]
    public void Detect_AskUserQuestionWithNoResult_ReportsTheQuestionBoxWithItsOwnText()
    {
        using var session = ClaudeSessionOn(WriteTranscript(UserAsks, AssistantSpeaks, AsksWhichBranch));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.Pending, result.Reading);
        Assert.NotNull(result.Interaction);
        Assert.Equal(PendingInteractionKind.Question, result.Interaction!.Kind);
        Assert.Equal("Which branch should I cut the worktree from?", result.Interaction.Prompt);
        Assert.Equal(["origin/main", "the current branch"], result.Interaction.Options.Select(o => o.Label));
        Assert.Equal("the trunk", result.Interaction.Options[0].Description);
    }

    /// <summary>
    /// The same transcript once the user has answered: Claude Code writes the tool result only then, so
    /// the box is gone and the reading is a definite nothing - which CLEARS the property rather than
    /// leaving a stale question standing in the shutdown dialog.
    /// </summary>
    [Fact]
    public void Detect_AskUserQuestionAnsweredByItsResult_ReportsNothingPending()
    {
        using var session = ClaudeSessionOn(
            WriteTranscript(UserAsks, AssistantSpeaks, AsksWhichBranch, WhichBranchAnswered));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.NothingPending, result.Reading);
        Assert.Null(result.Interaction);
    }

    /// <summary>A plan offered for approval and not yet approved is the other thing the owner should be
    /// asked about before a shutdown, and it carries the plan body.</summary>
    [Fact]
    public void Detect_ExitPlanModeWithNoResult_ReportsAPlanWaiting()
    {
        using var session = ClaudeSessionOn(WriteTranscript(UserAsks, OffersThePlan));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.Pending, result.Reading);
        Assert.Equal(PendingInteractionKind.Plan, result.Interaction!.Kind);
        Assert.Equal("Plan ready - approve?", result.Interaction.Prompt);
        Assert.Contains("read the code", result.Interaction.PlanBody);
    }

    /// <summary>
    /// THE TEST THAT STOPS THIS FIRING ON THE WHOLE FLEET. A Bash and a Read with no result yet are an
    /// ordinary busy session - every working session has one of those outstanding most of the time. Only
    /// the two interaction tools count as a box on the screen.
    /// </summary>
    [Fact]
    public void Detect_OrdinaryToolCallWithNoResult_IsNotAQuestionBox()
    {
        using var session = ClaudeSessionOn(WriteTranscript(UserAsks, RunsABuild, ReadsAFile));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.NothingPending, result.Reading);
        Assert.Null(result.Interaction);
    }

    /// <summary>
    /// Two questions in one transcript, the first answered and the second not: the second is the box on
    /// the screen, so it is the one held. Reporting the first would send the owner looking for a
    /// question he has already answered.
    /// </summary>
    [Fact]
    public void Detect_TwoQuestionsWithTheSecondUnfinished_HoldsTheSecond()
    {
        using var session = ClaudeSessionOn(WriteTranscript(
            UserAsks, AsksWhichBranch, WhichBranchAnswered, AsksWhichAgent));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.Pending, result.Reading);
        Assert.Equal("Which agent should run it?", result.Interaction!.Prompt);
    }

    /// <summary>
    /// Two questions BOTH unfinished - the agent asked again without the first being resolved. The newer
    /// one is what the user is looking at, so it is the one held.
    /// </summary>
    [Fact]
    public void Detect_TwoUnfinishedQuestions_HoldsTheNewer()
    {
        using var session = ClaudeSessionOn(WriteTranscript(UserAsks, AsksWhichBranch, AsksWhichAgent));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal("Which agent should run it?", result.Interaction!.Prompt);
    }

    /// <summary>
    /// A session of another agent is not read at all, and the proof is that the file it is pointed at is
    /// the very same transcript that makes a Claude Code session report a question. The difference is
    /// the agent, not the file. AskUserQuestion and ExitPlanMode are Claude Code's own tools; nobody
    /// else writes them, and guessing at another agent's screen is exactly what this replaces.
    /// </summary>
    [Fact]
    public void Detect_SessionOfAnotherAgent_GivesNoAnswer()
    {
        var transcript = WriteTranscript(UserAsks, AssistantSpeaks, AsksWhichBranch);

        using var claude = ClaudeSessionOn(transcript);
        Assert.Equal(PendingInteractionReading.Pending, PendingInteractionDetector.Detect(claude).Reading);

        using var codex = ClaudeSessionOn(transcript);
        codex.AgentKind = AgentKind.Codex;

        var result = PendingInteractionDetector.Detect(codex);

        Assert.Equal(PendingInteractionReading.NoAnswer, result.Reading);
        Assert.Null(result.Interaction);
    }

    /// <summary>A transcript file that is not there answers "no answer", so whatever the session was
    /// holding stays exactly as it was. It does not throw and it does not clear.</summary>
    [Fact]
    public void Detect_TranscriptThatDoesNotExist_GivesNoAnswer()
    {
        using var session = ClaudeSessionOn(Path.Combine(_directory, "never-written.jsonl"));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.NoAnswer, result.Reading);
    }

    /// <summary>A file that exists but is not a transcript - every line unparseable - is a failed read,
    /// not an empty conversation, so it answers "no answer" too.</summary>
    [Fact]
    public void Detect_TranscriptThatIsNotValid_GivesNoAnswer()
    {
        using var session = ClaudeSessionOn(WriteTranscript(
            "this is not json at all", "{ neither is this", "<html><body>nor this</body></html>"));

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.NoAnswer, result.Reading);
    }

    /// <summary>A session with no transcript pointer and no agent session id has nothing to resolve. It
    /// is a logged fact, not a guess at the answer.</summary>
    [Fact]
    public void Detect_NoTranscriptPathResolves_GivesNoAnswer()
    {
        using var session = ClaudeSessionOn(transcriptPath: null);

        var result = PendingInteractionDetector.Detect(session);

        Assert.Equal(PendingInteractionReading.NoAnswer, result.Reading);
    }
}
