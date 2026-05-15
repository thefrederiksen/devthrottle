using System.IO.Compression;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

// Serializes execution with other tests that mutate the CC_DIRECTOR_ROOT env var.
// xUnit runs tests in the same collection sequentially, preventing global-state interference.
[Collection("CcStorageRoot")]
public class SessionHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectDir;
    private readonly string _sessionId;

    public SessionHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cc-history-test-{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_tempDir, "projects");
        Directory.CreateDirectory(_projectDir);

        // Set CC_DIRECTOR_ROOT so SessionHistory.HistoryDir points to our temp dir
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _tempDir);

        _sessionId = Guid.NewGuid().ToString();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", null);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string CreateJsonl(string content)
    {
        var jsonlPath = Path.Combine(_projectDir, $"{_sessionId}.jsonl");
        File.WriteAllText(jsonlPath, content);
        return jsonlPath;
    }

    private SessionHistory CreateHistory(string jsonlPath)
    {
        return new SessionHistory(_sessionId, "fake-repo", jsonlPath);
    }

    [Fact]
    public void TakeSnapshot_NoJsonlFile_ReturnsNegativeOne()
    {
        var nonExistentPath = Path.Combine(_projectDir, "does-not-exist.jsonl");
        var history = CreateHistory(nonExistentPath);

        var result = history.TakeSnapshot();

        Assert.Equal(-1, result);
        Assert.Equal(0, history.SnapshotCount);
    }

    [Fact]
    public void TakeSnapshot_WithJsonlFile_CreatesArchiveEntry()
    {
        var jsonlContent = "{\"type\":\"user\",\"message\":\"hello\"}\n{\"type\":\"assistant\",\"message\":\"hi\"}\n";
        var jsonlPath = CreateJsonl(jsonlContent);
        var history = CreateHistory(jsonlPath);

        var result = history.TakeSnapshot();

        Assert.Equal(0, result);
        Assert.Equal(1, history.SnapshotCount);

        var archivePath = SessionHistory.GetArchivePath(_sessionId);
        Assert.True(File.Exists(archivePath));

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Single(archive.Entries);
        Assert.Equal("0000.jsonl", archive.Entries[0].Name);
    }

    [Fact]
    public void TakeSnapshot_MultipleSnapshots_IncrementsEntryNumber()
    {
        var jsonlPath = CreateJsonl("{\"type\":\"user\",\"message\":\"hello\"}\n");
        var history = CreateHistory(jsonlPath);

        Assert.Equal(0, history.TakeSnapshot());

        // Append more content
        File.AppendAllText(jsonlPath,
            "{\"type\":\"assistant\",\"message\":\"hi\"}\n{\"type\":\"user\",\"message\":\"continue\"}\n");

        Assert.Equal(1, history.TakeSnapshot());
        Assert.Equal(2, history.SnapshotCount);

        var archivePath = SessionHistory.GetArchivePath(_sessionId);
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.Name == "0000.jsonl");
        Assert.Contains(archive.Entries, e => e.Name == "0001.jsonl");

        // Second snapshot should be larger than first
        var entry0 = archive.GetEntry("0000.jsonl")!;
        var entry1 = archive.GetEntry("0001.jsonl")!;
        Assert.True(entry1.Length > entry0.Length);
    }

    [Fact]
    public void RestoreSnapshot_ValidEntry_WritesNewJsonlFile()
    {
        var jsonlContent = "{\"type\":\"user\",\"message\":\"hello\"}\n";
        var jsonlPath = CreateJsonl(jsonlContent);
        var history = CreateHistory(jsonlPath);
        history.TakeSnapshot();

        // Append more content and take second snapshot
        File.AppendAllText(jsonlPath, "{\"type\":\"assistant\",\"message\":\"hi\"}\n");
        history.TakeSnapshot();

        // RestoreSnapshot writes to Claude's real project folder.
        // Use an actual repo path so we can locate and clean up the file.
        var repoPath = _projectDir;
        var newSessionId = history.RestoreSnapshot(0, repoPath);

        Assert.NotNull(newSessionId);
        Assert.NotEqual(_sessionId, newSessionId);

        var expectedPath = ClaudeSessionReader.GetJsonlPath(newSessionId, repoPath);
        Assert.True(File.Exists(expectedPath));

        var restoredContent = File.ReadAllText(expectedPath);
        Assert.Equal(jsonlContent, restoredContent);

        // Clean up
        File.Delete(expectedPath);
    }

    [Fact]
    public void RestoreSnapshot_InvalidEntry_ReturnsNull()
    {
        var jsonlPath = CreateJsonl("{\"type\":\"user\",\"message\":\"hello\"}\n");
        var history = CreateHistory(jsonlPath);
        history.TakeSnapshot();

        var result = history.RestoreSnapshot(999, "fake-repo");

        Assert.Null(result);
    }

    [Fact]
    public void GetSnapshots_ReturnsMetadata()
    {
        var jsonlPath = CreateJsonl("{\"type\":\"user\",\"message\":\"hello\"}\n");
        var history = CreateHistory(jsonlPath);
        history.TakeSnapshot();
        history.TakeSnapshot();

        var snapshots = history.GetSnapshots();

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(0, snapshots[0].EntryNumber);
        Assert.Equal(1, snapshots[1].EntryNumber);
        Assert.True(snapshots[0].OriginalSize > 0);
        Assert.True(snapshots[0].CompressedSize > 0);
    }

    [Fact]
    public void DeleteArchive_RemovesFile()
    {
        var jsonlPath = CreateJsonl("{\"type\":\"user\",\"message\":\"hello\"}\n");
        var history = CreateHistory(jsonlPath);
        history.TakeSnapshot();

        var archivePath = SessionHistory.GetArchivePath(_sessionId);
        Assert.True(File.Exists(archivePath));

        history.DeleteArchive();

        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public void Constructor_ResumesFromExistingArchive()
    {
        var jsonlPath = CreateJsonl("{\"type\":\"user\",\"message\":\"hello\"}\n");

        // Create first instance and take snapshots
        var history1 = CreateHistory(jsonlPath);
        history1.TakeSnapshot();
        history1.TakeSnapshot();

        // Create second instance (simulating app restart)
        var history2 = CreateHistory(jsonlPath);

        Assert.Equal(2, history2.SnapshotCount);

        // Next snapshot should be entry 2
        var result = history2.TakeSnapshot();
        Assert.Equal(2, result);
        Assert.Equal(3, history2.SnapshotCount);
    }

    [Fact]
    public void TakeSnapshot_ContentPreservedExactly()
    {
        // Use binary-unfriendly content to verify exact preservation
        var jsonlContent = "{\"type\":\"user\",\"message\":{\"content\":\"line with \\\"quotes\\\" and \\ttabs\"}}\n"
                         + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"response\"}]}}\n";
        var jsonlPath = CreateJsonl(jsonlContent);
        var history = CreateHistory(jsonlPath);
        history.TakeSnapshot();

        // Read back from archive
        var archivePath = SessionHistory.GetArchivePath(_sessionId);
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("0000.jsonl")!;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var restored = reader.ReadToEnd();

        Assert.Equal(jsonlContent, restored);
    }

    [Fact]
    public void CleanupOldArchives_DeletesExpiredArchives()
    {
        // Create some archive files with old timestamps
        var historyDir = Path.Combine(_tempDir, "session-history");
        Directory.CreateDirectory(historyDir);

        var oldFile = Path.Combine(historyDir, "old-session.zip");
        var newFile = Path.Combine(historyDir, "new-session.zip");
        File.WriteAllText(oldFile, "old data");
        File.WriteAllText(newFile, "new data");

        // Set the old file to 60 days ago
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-60));

        SessionHistory.CleanupOldArchives(maxAgeDays: 30, maxTotalSizeMb: 500);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void CleanupOldArchives_EnforcesSizeLimit()
    {
        var historyDir = Path.Combine(_tempDir, "session-history");
        Directory.CreateDirectory(historyDir);

        // Create files that exceed size limit (use 1MB limit for test)
        var data = new byte[600 * 1024]; // 600KB each
        for (int i = 0; i < 3; i++)
        {
            var path = Path.Combine(historyDir, $"session-{i:D3}.zip");
            File.WriteAllBytes(path, data);
            // Stagger timestamps so oldest is deleted first
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3 + i));
        }

        // 3 files x 600KB = 1800KB > 1MB limit -> should delete oldest
        SessionHistory.CleanupOldArchives(maxAgeDays: 365, maxTotalSizeMb: 1);

        var remaining = Directory.GetFiles(historyDir, "*.zip");
        // Should have deleted at least 1 file to get under 1MB
        Assert.True(remaining.Length < 3);
        // Newest file should survive
        Assert.Contains(remaining, f => Path.GetFileName(f) == "session-002.zip");
    }
}
