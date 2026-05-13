using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for Session.VerifyClaudeSession and the SessionVerificationStatus enum.
/// </summary>
public class SessionVerificationTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionVerificationTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void VerifyClaudeSession_NoClaudeSessionId_NotVerified()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        // New sessions get a preassigned ClaudeSessionId; clear it to test this scenario
        session.ClaudeSessionId = null;

        session.VerifyClaudeSession();

        Assert.Equal(SessionVerificationStatus.NotLinked, session.VerificationStatus);
    }

    [Fact]
    public void VerifyClaudeSession_NonexistentSessionId_StaysNotLinked()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        session.ClaudeSessionId = "nonexistent-session-id-that-wont-exist";

        session.VerifyClaudeSession();

        // No .jsonl file means no content to verify, so stays NotLinked
        Assert.Equal(SessionVerificationStatus.NotLinked, session.VerificationStatus);
    }

    [Fact]
    public void VerificationStatus_DefaultIsNotVerified()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.Equal(SessionVerificationStatus.NotLinked, session.VerificationStatus);
    }

    [Fact]
    public void SessionVerificationStatus_HasExpectedValues()
    {
        // Verify the enum has all expected values
        Assert.Equal(0, (int)SessionVerificationStatus.Verified);
        Assert.Equal(1, (int)SessionVerificationStatus.FileNotFound);
        Assert.Equal(2, (int)SessionVerificationStatus.NotLinked);
        Assert.Equal(3, (int)SessionVerificationStatus.Error);
        Assert.Equal(4, (int)SessionVerificationStatus.ContentMismatch);
    }

    [Fact]
    public void SessionExists_NullOrEmpty_ReturnsFalse()
    {
#pragma warning disable CS8625 // Testing null safety — deliberately passing null to non-nullable parameter
        Assert.False(ClaudeSessionReader.SessionExists(null, Path.GetTempPath()));
#pragma warning restore CS8625
        Assert.False(ClaudeSessionReader.SessionExists("", Path.GetTempPath()));
    }

    [Fact]
    public void SessionExists_NonexistentId_ReturnsFalse()
    {
        Assert.False(ClaudeSessionReader.SessionExists(
            "nonexistent-session-id-12345", Path.GetTempPath()));
    }

    [Fact]
    public void SessionExists_JsonlFileExists_ReturnsTrue()
    {
        // Create a temp directory simulating a Claude project folder
        var tempRepoPath = Path.Combine(Path.GetTempPath(), $"test_repo_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRepoPath);

        // Get the project folder path that ClaudeSessionReader will look in
        var projectFolder = ClaudeSessionReader.GetProjectFolderPath(tempRepoPath);
        Directory.CreateDirectory(projectFolder);

        try
        {
            var sessionId = Guid.NewGuid().ToString();
            var jsonlPath = Path.Combine(projectFolder, $"{sessionId}.jsonl");

            // Before creating the file, SessionExists should return false
            Assert.False(ClaudeSessionReader.SessionExists(sessionId, tempRepoPath));

            // Create a .jsonl file (simulating Claude's session file)
            File.WriteAllText(jsonlPath, "{\"type\":\"user\",\"message\":\"hello\"}\n");

            // Now SessionExists should return true
            Assert.True(ClaudeSessionReader.SessionExists(sessionId, tempRepoPath));
        }
        finally
        {
            if (Directory.Exists(projectFolder))
                Directory.Delete(projectFolder, recursive: true);
            if (Directory.Exists(tempRepoPath))
                Directory.Delete(tempRepoPath, recursive: true);
        }
    }

    [Fact]
    public void SessionExists_NoSessionsIndex_StillFindsJsonl()
    {
        // This is the exact bug that caused sessions not to resume:
        // SessionExists was checking sessions-index.json instead of the .jsonl file.
        // When sessions-index.json doesn't exist, it returned false even though
        // the .jsonl file was there.
        var tempRepoPath = Path.Combine(Path.GetTempPath(), $"test_repo_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRepoPath);

        var projectFolder = ClaudeSessionReader.GetProjectFolderPath(tempRepoPath);
        Directory.CreateDirectory(projectFolder);

        try
        {
            var sessionId = Guid.NewGuid().ToString();
            var jsonlPath = Path.Combine(projectFolder, $"{sessionId}.jsonl");

            // Create ONLY the .jsonl file — NO sessions-index.json
            File.WriteAllText(jsonlPath, "{\"type\":\"user\",\"message\":\"test\"}\n");
            Assert.False(File.Exists(Path.Combine(projectFolder, "sessions-index.json")));

            // SessionExists MUST return true based on .jsonl file alone
            Assert.True(ClaudeSessionReader.SessionExists(sessionId, tempRepoPath));
        }
        finally
        {
            if (Directory.Exists(projectFolder))
                Directory.Delete(projectFolder, recursive: true);
            if (Directory.Exists(tempRepoPath))
                Directory.Delete(tempRepoPath, recursive: true);
        }
    }

    [Fact]
    public void RestoreFlow_SessionWithJsonl_WouldResume()
    {
        // End-to-end test of the restore decision logic from RestorePersistedSessions.
        // Simulates: sessions.json has a ClaudeSessionId, .jsonl file exists on disk,
        // so the session SHOULD be resumed with --resume (not started fresh).
        var tempRepoPath = Path.Combine(Path.GetTempPath(), $"test_repo_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRepoPath);
        var projectFolder = ClaudeSessionReader.GetProjectFolderPath(tempRepoPath);
        Directory.CreateDirectory(projectFolder);

        try
        {
            var claudeSessionId = Guid.NewGuid().ToString();
            var jsonlPath = Path.Combine(projectFolder, $"{claudeSessionId}.jsonl");
            File.WriteAllText(jsonlPath, "{\"type\":\"user\",\"message\":\"fix the bug\"}\n");

            // Simulate the exact logic from RestorePersistedSessions (MainWindow.xaml.cs:308-324)
            string? resumeSessionId = null;
            if (!string.IsNullOrEmpty(claudeSessionId))
            {
                if (ClaudeSessionReader.SessionExists(claudeSessionId, tempRepoPath))
                {
                    resumeSessionId = claudeSessionId;
                }
            }

            // resumeSessionId MUST be set — this means --resume will be used
            Assert.NotNull(resumeSessionId);
            Assert.Equal(claudeSessionId, resumeSessionId);
        }
        finally
        {
            if (Directory.Exists(projectFolder))
                Directory.Delete(projectFolder, recursive: true);
            if (Directory.Exists(tempRepoPath))
                Directory.Delete(tempRepoPath, recursive: true);
        }
    }

    [Fact]
    public void RestoreFlow_SessionWithoutJsonl_StartsFresh()
    {
        // When .jsonl file does NOT exist, session should start fresh (resumeSessionId = null)
        var tempRepoPath = Path.Combine(Path.GetTempPath(), $"test_repo_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRepoPath);

        try
        {
            var claudeSessionId = Guid.NewGuid().ToString();

            string? resumeSessionId = null;
            if (!string.IsNullOrEmpty(claudeSessionId))
            {
                if (ClaudeSessionReader.SessionExists(claudeSessionId, tempRepoPath))
                {
                    resumeSessionId = claudeSessionId;
                }
            }

            // resumeSessionId should be null — no .jsonl file, so start fresh
            Assert.Null(resumeSessionId);
        }
        finally
        {
            if (Directory.Exists(tempRepoPath))
                Directory.Delete(tempRepoPath, recursive: true);
        }
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
