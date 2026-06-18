using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Tests for <see cref="WorkspaceHandoverWriter"/> (issue #512): the orchestration that has
/// the wingman write one handover note per session over a REAL session
/// (<see cref="SessionAskRunner"/>, no /handover skill, no --print) and persists it. Both the
/// ask and the persistence are injected seams, so every test runs against fakes - no live
/// agent, no network, no real filesystem write.
/// </summary>
public class WorkspaceHandoverWriterTests : IDisposable
{
    private readonly string _repoDir;

    public WorkspaceHandoverWriterTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), $"WorkspaceHandoverWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task WriteForSessionAsync_AsksWingmanAndPersistsAnswer_ReturnsPath()
    {
        string? askedWorkdir = null;
        string? askedPrompt = null;
        string? writtenContent = null;
        string? writtenTitle = null;

        var writer = new WorkspaceHandoverWriter(
            ask: (kind, exe, args, workdir, prompt, timeout, ct) =>
            {
                askedWorkdir = workdir;
                askedPrompt = prompt;
                return Task.FromResult(new SessionAskResult { Answer = "Did X, next do Y." });
            },
            writeHandover: (title, content, repos, sessionName) =>
            {
                writtenTitle = title;
                writtenContent = content;
                return @"C:\handovers\note.md";
            },
            log: _ => { });

        var result = await writer.WriteForSessionAsync(new WorkspaceHandoverRequest
        {
            RepoPath = _repoDir,
            AgentKind = AgentKind.ClaudeCode,
            SessionName = "Frontend",
        });

        Assert.True(result.Success);
        Assert.Equal(@"C:\handovers\note.md", result.HandoverPath);
        Assert.Equal(_repoDir, askedWorkdir);
        Assert.Contains("Frontend", askedPrompt);
        Assert.Equal("Did X, next do Y.", writtenContent);
        Assert.Equal("Frontend handover", writtenTitle);
    }

    [Fact]
    public async Task WriteForSessionAsync_MissingRepo_FailsWithoutAsking()
    {
        var asked = false;
        var writer = new WorkspaceHandoverWriter(
            ask: (kind, exe, args, workdir, prompt, timeout, ct) =>
            {
                asked = true;
                return Task.FromResult(new SessionAskResult { Answer = "x" });
            },
            writeHandover: (_, _, _, _) => "unused",
            log: _ => { });

        var result = await writer.WriteForSessionAsync(new WorkspaceHandoverRequest
        {
            RepoPath = Path.Combine(_repoDir, "does-not-exist"),
            SessionName = "Gone",
        });

        Assert.False(result.Success);
        Assert.Null(result.HandoverPath);
        Assert.False(asked);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    [Fact]
    public async Task WriteForSessionAsync_EmptyAnswer_FailsAndDoesNotPersist()
    {
        var wrote = false;
        var writer = new WorkspaceHandoverWriter(
            ask: (kind, exe, args, workdir, prompt, timeout, ct) =>
                Task.FromResult(new SessionAskResult { Answer = "   " }),
            writeHandover: (_, _, _, _) => { wrote = true; return "unused"; },
            log: _ => { });

        var result = await writer.WriteForSessionAsync(new WorkspaceHandoverRequest
        {
            RepoPath = _repoDir,
            SessionName = "Empty",
        });

        Assert.False(result.Success);
        Assert.False(wrote);
        Assert.Contains("empty", result.ErrorMessage);
    }

    [Fact]
    public async Task WriteForSessionAsync_AskThrows_FailsGracefullyWithReason()
    {
        var writer = new WorkspaceHandoverWriter(
            ask: (kind, exe, args, workdir, prompt, timeout, ct) =>
                throw new NotSupportedException("agent RawCli cannot answer an ask"),
            writeHandover: (_, _, _, _) => "unused",
            log: _ => { });

        var result = await writer.WriteForSessionAsync(new WorkspaceHandoverRequest
        {
            RepoPath = _repoDir,
            AgentKind = AgentKind.RawCli,
            SessionName = "Raw",
        });

        Assert.False(result.Success);
        Assert.Contains("NotSupportedException", result.ErrorMessage);
        Assert.Contains("RawCli", result.ErrorMessage);
    }

    [Fact]
    public void BuildHandoverPrompt_IsAHandoverNotATranscript()
    {
        var prompt = WorkspaceHandoverWriter.BuildHandoverPrompt("Backend");

        Assert.Contains("Backend", prompt);
        Assert.Contains("handover note", prompt);
        Assert.Contains("NOT a transcript", prompt);
    }

    [Fact]
    public void BuildTitle_NamedAndUnnamed()
    {
        Assert.Equal("Backend handover", WorkspaceHandoverWriter.BuildTitle("Backend"));
        Assert.Equal("Session handover", WorkspaceHandoverWriter.BuildTitle("  "));
    }
}
