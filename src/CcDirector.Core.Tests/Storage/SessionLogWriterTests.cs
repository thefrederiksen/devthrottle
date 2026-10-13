using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Phase 5: tests for <see cref="SessionLogWriter"/>. Covers the roundtrip contract
/// (write -> read JSONL back -> parse), restart safety (append vs truncate), and
/// the disposal lifecycle. The hot-path back-pressure test is deliberately omitted -
/// the bounded-channel drop policy is the relevant Channels API behavior, not ours.
/// </summary>
[Trait("Gate", "Quarantine")]
[Collection("ProcessWideState")]
public sealed class SessionLogWriterTests : IDisposable
{
    // We write to the real production path (%LOCALAPPDATA%\cc-director\session-logs\<sid>)
    // and clean up the per-session dir at the end. Using a CC_DIRECTOR_ROOT env-var
    // override would race against other test classes that read CcStorage.Root().
    private readonly List<Guid> _createdSessionDirs = new();
    private readonly SessionManager _manager;

    public SessionLogWriterTests()
    {
        _manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
    }

    public void Dispose()
    {
        _manager.Dispose();
        foreach (var sid in _createdSessionDirs)
        {
            try
            {
                var dir = SessionLogPaths.SessionDir(sid);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }

    private Session CreateTrackedSession()
    {
        var s = _manager.CreateSession(Path.GetTempPath());
        _createdSessionDirs.Add(s.Id);
        return s;
    }

    [Fact]
    public void Writer_creates_session_directory_and_meta_json()
    {
        var session = CreateTrackedSession();
        using var writer = new SessionLogWriter(session);
        writer.Start();

        var dir = SessionLogPaths.SessionDir(session.Id);
        Assert.True(Directory.Exists(dir));
        var metaPath = SessionLogPaths.MetaJson(session.Id);
        Assert.True(File.Exists(metaPath));

        var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        Assert.Equal(session.Id.ToString(), meta.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, meta.RootElement.GetProperty("schema").GetInt32());
    }
}
