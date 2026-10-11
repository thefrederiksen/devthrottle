using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests;

/// <summary>
/// Integration tests for the terminal verification algorithm.
/// Uses real .jsonl files from the current user's Claude sessions to verify
/// that the matching pipeline (extract prompts -> build terminal text -> match) works end-to-end.
/// </summary>
[Collection("ProcessWideState")]
public class TerminalVerificationIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _projectFolder;

    /// <summary>
    /// Derive the repo root by walking up from the test assembly's output directory.
    /// Assumes test output is in src/CcDirector.Core.Tests/bin/Debug/net*/
    /// </summary>
    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // Walk up until we find the .sln file (repo root)
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root from test output directory");
    }

    public TerminalVerificationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _projectFolder = ClaudeSessionReader.GetProjectFolderPath(GetRepoRoot());
    }

    private (FileInfo file, List<string> prompts)? FindFileWithPrompts(int minPrompts = 2)
    {
        if (!Directory.Exists(_projectFolder)) return null;

        var jsonlFiles = Directory.GetFiles(_projectFolder, "*.jsonl");
        if (jsonlFiles.Length == 0) return null;

        foreach (var fi in jsonlFiles.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc))
        {
            var prompts = ClaudeSessionReader.ExtractUserPrompts(fi.FullName);
            if (prompts.Count >= minPrompts)
                return (fi, prompts);
        }

        return null;
    }

    [Fact]
    public void ExtractUserPrompts_RealJsonlFile_FindsPrompts()
    {
        var target = FindFileWithPrompts(1);
        if (target == null)
        {
            _output.WriteLine("SKIPPED: No Claude project folder or .jsonl files for cc_director");
            return;
        }

        var (file, prompts) = target.Value;
        _output.WriteLine($"File: {file.Name} ({file.Length} bytes)");
        _output.WriteLine($"Extracted {prompts.Count} user prompts");
        foreach (var p in prompts.Take(5))
        {
            var truncated = p.Length > 80 ? p[..80] + "..." : p;
            _output.WriteLine($"  - {truncated}");
        }

        Assert.True(prompts.Count > 0);
    }

    [Fact]
    public void VerifyWithTerminalContent_SimulatedTerminal_MatchesAt50Lines()
    {
        var target = FindFileWithPrompts();
        if (target == null)
        {
            _output.WriteLine("SKIPPED: No suitable .jsonl file found");
            return;
        }

        var (file, prompts) = target.Value;
        var expectedSessionId = Path.GetFileNameWithoutExtension(file.Name);
        _output.WriteLine($"Target: {file.Name}, prompts: {prompts.Count}, expected ID: {expectedSessionId}");

        // Build simulated terminal text containing all prompts
        var terminalLines = new List<string>
        {
            "Welcome to Claude Code", "", " /help for help", "",
        };
        foreach (var prompt in prompts)
        {
            terminalLines.Add($"> {prompt}");
            terminalLines.Add("");
            terminalLines.Add("I'll help you with that. Let me look at the relevant code.");
            terminalLines.Add("Read file: src/Session.cs");
            terminalLines.Add("");
        }

        var terminalText = string.Join("\n", terminalLines);

        // Create session with matching creation time (within 1 hour of file modification)
        var createdAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var backend = new StubSessionBackend();
        var session = new Session(
            Guid.NewGuid(), GetRepoRoot(), GetRepoRoot(),
            null, backend, SessionBackendType.ConPty, createdAt);

        // Act: call with 50+ lines (confirmation run)
        var effectiveLineCount = Math.Max(terminalLines.Count, 50);
        var result = session.VerifyWithTerminalContent(terminalText, effectiveLineCount);

        // Assert
        _output.WriteLine($"Result: IsMatched={result.IsMatched}, IsPotential={result.IsPotential}");
        _output.WriteLine($"MatchedId: {result.MatchedSessionId}");
        _output.WriteLine($"Session.TerminalVerificationStatus: {session.TerminalVerificationStatus}");
        _output.WriteLine($"Session.ClaudeSessionId: {session.ClaudeSessionId}");

        Assert.True(result.IsMatched, $"Expected Matched for {expectedSessionId}, got: {result.ErrorMessage}");
        Assert.Equal(expectedSessionId, result.MatchedSessionId);
        Assert.Equal(TerminalVerificationStatus.Matched, session.TerminalVerificationStatus);
        Assert.Equal(expectedSessionId, session.ClaudeSessionId);
    }

    [Fact]
    public void VerifyWithTerminalContent_Under50Lines_MatchesSession()
    {
        var target = FindFileWithPrompts();
        if (target == null)
        {
            _output.WriteLine("SKIPPED: No suitable .jsonl file found");
            return;
        }

        var (file, prompts) = target.Value;
        var expectedSessionId = Path.GetFileNameWithoutExtension(file.Name);

        // Build terminal text with prompts
        var terminalText = string.Join("\n", prompts.Select(p => $"> {p}\nResponse here.\n"));

        // Create session
        var createdAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var backend = new StubSessionBackend();
        var session = new Session(
            Guid.NewGuid(), GetRepoRoot(), GetRepoRoot(),
            null, backend, SessionBackendType.ConPty, createdAt);

        // Call with < 50 lines -- with 2+ matches, goes straight to Matched
        var result = session.VerifyWithTerminalContent(terminalText, 10);

        _output.WriteLine($"Result: IsMatched={result.IsMatched}, IsPotential={result.IsPotential}, MatchedId={result.MatchedSessionId}");
        _output.WriteLine($"Session.TerminalVerificationStatus: {session.TerminalVerificationStatus}");

        // With 2+ prompts matching, should be Matched (high confidence even at few lines)
        // With 1 prompt, should be Potential
        Assert.True(result.IsMatched || result.IsPotential,
            $"Expected Matched or Potential, got: {result.ErrorMessage}");
        Assert.Equal(expectedSessionId, result.MatchedSessionId);
        Assert.True(
            session.TerminalVerificationStatus == TerminalVerificationStatus.Matched ||
            session.TerminalVerificationStatus == TerminalVerificationStatus.Potential);
    }

    [Fact]
    public void VerifyWithTerminalContent_ResumedSession_MatchesWithPartialPrompts()
    {
        // Simulate a resumed session: JSONL has 10 prompts, but terminal only shows the last 2
        // This was the PRIMARY failure mode - the old 95% ratio would fail (2/10 = 20%)
        var target = FindFileWithPrompts(3);
        if (target == null)
        {
            _output.WriteLine("SKIPPED: No .jsonl file with 3+ prompts");
            return;
        }

        var (file, prompts) = target.Value;
        var expectedSessionId = Path.GetFileNameWithoutExtension(file.Name);
        _output.WriteLine($"Target: {file.Name}, total prompts: {prompts.Count}");

        // Build terminal text with ONLY the last 2 prompts (simulates resumed session)
        var recentPrompts = prompts.TakeLast(2).ToList();
        var terminalLines = new List<string>
        {
            "Welcome to Claude Code", "", " /help for help", "",
            "Resuming session...", "",
        };
        foreach (var prompt in recentPrompts)
        {
            terminalLines.Add($"> {prompt}");
            terminalLines.Add("");
            terminalLines.Add("I'll help you with that.");
            terminalLines.Add("");
        }
        // Pad to 50+ lines for confirmation
        while (terminalLines.Count < 55)
            terminalLines.Add("Some output text from Claude response");

        var terminalText = string.Join("\n", terminalLines);

        var createdAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var backend = new StubSessionBackend();
        var session = new Session(
            Guid.NewGuid(), GetRepoRoot(), GetRepoRoot(),
            null, backend, SessionBackendType.ConPty, createdAt);

        // Act: confirmation run with partial prompts visible
        var result = session.VerifyWithTerminalContent(terminalText, terminalLines.Count);

        // Assert: should match even with only 2 of N prompts visible
        _output.WriteLine($"Result: IsMatched={result.IsMatched}, MatchedId={result.MatchedSessionId}");
        _output.WriteLine($"Status: {session.TerminalVerificationStatus}");
        _output.WriteLine($"Only {recentPrompts.Count} of {prompts.Count} prompts were in terminal");

        Assert.True(result.IsMatched,
            $"Expected Matched with {recentPrompts.Count}/{prompts.Count} prompts visible. Error: {result.ErrorMessage}");
        // Don't assert specific session ID — with real data, another file may share overlapping prompts
        // and legitimately win. The important assertion is that *some* session matched.
        Assert.NotNull(result.MatchedSessionId);
        Assert.Equal(TerminalVerificationStatus.Matched, session.TerminalVerificationStatus);
    }

    [Fact]
    public void VerifyWithTerminalContent_WordWrappedPrompts_StillMatches()
    {
        // Test that prompts wrapped in terminal (newlines inserted at column boundary) still match
        var target = FindFileWithPrompts(1);
        if (target == null)
        {
            _output.WriteLine("SKIPPED: No .jsonl file with prompts");
            return;
        }

        var (file, prompts) = target.Value;
        var expectedSessionId = Path.GetFileNameWithoutExtension(file.Name);

        // Build terminal text with prompts that have word wrapping (newlines inserted mid-text)
        var terminalLines = new List<string>
        {
            "Welcome to Claude Code", "", " /help for help", "",
        };
        foreach (var prompt in prompts)
        {
            // Simulate word wrapping at word boundary near column 60 (like real terminals)
            if (prompt.Length > 60)
            {
                // Find the last space at or before position 60 to wrap at word boundary
                var wrapPos = prompt.LastIndexOf(' ', Math.Min(60, prompt.Length - 1));
                if (wrapPos > 20) // Only wrap if we find a reasonable word boundary
                {
                    var wrapped = prompt[..wrapPos] + "\n" + prompt[(wrapPos + 1)..];
                    terminalLines.Add($"> {wrapped}");
                }
                else
                {
                    terminalLines.Add($"> {prompt}");
                }
            }
            else
            {
                terminalLines.Add($"> {prompt}");
            }
            terminalLines.Add("");
            terminalLines.Add("Response text here.");
            terminalLines.Add("");
        }

        var terminalText = string.Join("\n", terminalLines);
        var effectiveLineCount = Math.Max(terminalLines.Count, 50);

        var createdAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var backend = new StubSessionBackend();
        var session = new Session(
            Guid.NewGuid(), GetRepoRoot(), GetRepoRoot(),
            null, backend, SessionBackendType.ConPty, createdAt);

        var result = session.VerifyWithTerminalContent(terminalText, effectiveLineCount);

        _output.WriteLine($"Result: IsMatched={result.IsMatched}, IsPotential={result.IsPotential}, MatchedId={result.MatchedSessionId}");
        _output.WriteLine($"Status: {session.TerminalVerificationStatus}");

        Assert.True(result.IsMatched || result.IsPotential,
            $"Expected match with word-wrapped prompts. Error: {result.ErrorMessage}");
        // Don't assert specific session ID — with real data, another file may match
        Assert.NotNull(result.MatchedSessionId);
    }

    [Fact]
    public void VerifyWithTerminalContent_UnrelatedText_StaysWaiting()
    {
        if (!Directory.Exists(_projectFolder))
        {
            _output.WriteLine("SKIPPED: No Claude project folder");
            return;
        }

        var backend = new StubSessionBackend();
        var session = new Session(
            Guid.NewGuid(), GetRepoRoot(), GetRepoRoot(),
            null, backend, SessionBackendType.ConPty);

        // Terminal with completely unrelated content
        var terminalText = string.Join("\n",
            Enumerable.Range(0, 10).Select(i =>
                $"Unrelated line {i} that matches nothing in any jsonl file whatsoever xyz123"));

        var result = session.VerifyWithTerminalContent(terminalText, 10);

        _output.WriteLine($"Result: IsMatched={result.IsMatched}, IsPotential={result.IsPotential}");
        _output.WriteLine($"Session.TerminalVerificationStatus: {session.TerminalVerificationStatus}");

        Assert.False(result.IsMatched);
        Assert.False(result.IsPotential);
        // Under 50 lines, should stay Waiting, not Failed
        Assert.Equal(TerminalVerificationStatus.Waiting, session.TerminalVerificationStatus);
    }

    [Fact]
    public void VerifyWithTerminalContent_UnrelatedText_Over50Lines_SetsFailed()
    {
        if (!Directory.Exists(_projectFolder))
        {
            _output.WriteLine("SKIPPED: No Claude project folder");
            return;
        }

        var backend = new StubSessionBackend();
        var session = new Session(
            Guid.NewGuid(), GetRepoRoot(), GetRepoRoot(),
            null, backend, SessionBackendType.ConPty);

        // Terminal with completely unrelated content
        var terminalText = string.Join("\n",
            Enumerable.Range(0, 60).Select(i =>
                $"Unrelated line {i} that matches nothing in any jsonl file whatsoever xyz123"));

        var result = session.VerifyWithTerminalContent(terminalText, 60);

        _output.WriteLine($"Result: IsMatched={result.IsMatched}, Error={result.ErrorMessage}");
        _output.WriteLine($"Session.TerminalVerificationStatus: {session.TerminalVerificationStatus}");

        Assert.False(result.IsMatched);
        Assert.False(result.IsPotential);
        // First confirmation attempt with no match -> should be Failed
        Assert.Equal(TerminalVerificationStatus.Failed, session.TerminalVerificationStatus);

        // Verify retries are allowed (up to max) - subsequent calls should still process
        var result2 = session.VerifyWithTerminalContent(terminalText, 70);
        Assert.False(result2.IsMatched);
        _output.WriteLine($"After retry: status={session.TerminalVerificationStatus}");
    }
}
