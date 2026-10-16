using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tenant-boundary hardening (release 2026-07-31, finding CR-4): the <c>read-file</c> stream verb used to
/// serve ANY absolute path after only <c>File.Exists</c>, so on a hosted Gateway any active device key could
/// read <c>gateway-token.txt</c>, <c>.ssh</c> keys, or <c>.env</c> files on any machine in the account. These
/// tests pin the containment: a file read is session-scoped and refused outside the session's working
/// directory, while the screenshot verb beside it (which was always contained through
/// <c>ControlEndpoints.ResolveScreenshot</c>) keeps working.
///
/// CC_DIRECTOR_ROOT is redirected to an isolated temp dir (screenshots resolve under it), so the tests never
/// touch the user's real files. In the "DirectorRoot" collection (serializes root-touching tests).
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorUpStreamHandlerContainmentTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _sessionDir;
    private readonly string _outsideDir;
    private readonly SessionManager _sm;
    private readonly Session _session;
    private readonly List<DirectorStreamFrame> _frames = new();
    private readonly DirectorUpStreamHandler _handler;

    public DirectorUpStreamHandlerContainmentTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-containment-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        // The session's working directory and a sibling directory OUTSIDE it, both real on disk.
        _sessionDir = Path.Combine(_root, "session-repo");
        _outsideDir = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(_outsideDir);

        _sm = new SessionManager(new AgentOptions());
        _session = _sm.CreateEmbeddedSession(_sessionDir, null, new ExecuteActionTestBackend());

        _handler = new DirectorUpStreamHandler(_sm, async (_, frames) =>
        {
            await foreach (var frame in frames)
            {
                lock (_frames) _frames.Add(frame);
            }
        });
    }

    public void Dispose()
    {
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DirectorCommandResult OpenFile(string sessionId, string path) => _handler.Handle(new DirectorCommand
    {
        Verb = "read-file",
        SessionId = sessionId,
        PayloadJson = SessionCommandExecutor.Serialize(new OpenStreamRequest
        {
            StreamId = Guid.NewGuid().ToString("N"),
            Path = path,
        }),
    });

    // ---------------------------------------------------------------- the refusal (the CR-4 fix) ----

    [Fact]
    public void ReadFile_absolutePathOutsideTheSessionWorkingDirectory_isRefused()
    {
        // The reported symptom: an EXISTING file outside the session's working directory (the stand-in for
        // gateway-token.txt / .ssh / .env) was served whole. It must now be refused, and no producer stream
        // may have been started for it.
        var secret = Path.Combine(_outsideDir, "gateway-token.txt");
        File.WriteAllText(secret, "the-secret");

        var result = OpenFile(_session.Id.ToString(), secret);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        // Issue #4078: the refusal now says why in the Director's own sentence. This file is both outside the
        // working folder AND on the secrets list (gateway-token.txt), and the secrets list answers first.
        Assert.Equal(SessionFileAccess.SecretSentence, result.Error);
        Assert.Equal(0, _handler.ActiveStreamCount);
        lock (_frames) Assert.Empty(_frames);
    }

    [Fact]
    public void ReadFile_traversalThatResolvesOutsideTheRoot_isRefused()
    {
        var secret = Path.Combine(_outsideDir, "escaped.txt");
        File.WriteAllText(secret, "escaped");
        var traversal = Path.Combine(_sessionDir, "..", "outside", "escaped.txt");

        var result = OpenFile(_session.Id.ToString(), traversal);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal(0, _handler.ActiveStreamCount);
    }

    [Fact]
    public void ReadFile_unknownSession_isNotFound()
    {
        var inside = Path.Combine(_sessionDir, "a.txt");
        File.WriteAllText(inside, "x");

        var result = OpenFile(Guid.NewGuid().ToString(), inside);

        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        Assert.Contains("session not found", result.Error);
    }

    // ------------------------------------------------------- the allowed paths still work (controls) ----

    [Fact]
    public void ReadFile_missingFileInsideTheRoot_isNotFound()
    {
        var result = OpenFile(_session.Id.ToString(), Path.Combine(_sessionDir, "no-such.txt"));
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        Assert.Equal(SessionFileAccess.NotOnDiskSentence, result.Error);
    }

    // -------------------------------------------------------------- the resolver, pinned directly ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSessionFile_blankPath_isNull(string? path)
    {
        Assert.Null(ControlEndpoints.ResolveSessionFile(@"C:\work\repo", path));
    }

    [Fact]
    public void ResolveSessionFile_insideTheRoot_resolves()
    {
        var inside = Path.Combine(_sessionDir, "sub", "file.txt");
        Assert.Equal(Path.GetFullPath(inside), ControlEndpoints.ResolveSessionFile(_sessionDir, inside));
    }

    [Fact]
    public void ResolveSessionFile_relativePath_resolvesAgainstTheRoot()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_sessionDir, "file.txt")),
            ControlEndpoints.ResolveSessionFile(_sessionDir, "file.txt"));
    }

    [Fact]
    public void ResolveSessionFile_outsideTheRoot_isNull()
    {
        Assert.Null(ControlEndpoints.ResolveSessionFile(_sessionDir, Path.Combine(_outsideDir, "x.txt")));
    }

    [Fact]
    public void ResolveSessionFile_traversal_isNull()
    {
        Assert.Null(ControlEndpoints.ResolveSessionFile(_sessionDir, Path.Combine(_sessionDir, "..", "outside", "x.txt")));
    }

    [Fact]
    public void ResolveSessionFile_siblingWithTheRootAsPrefix_isNull()
    {
        // "C:\work\repo-evil" starts with "C:\work\repo" as a STRING; the separator-suffixed comparison
        // must not be fooled by it.
        var evilSibling = _sessionDir + "-evil";
        Directory.CreateDirectory(evilSibling);
        Assert.Null(ControlEndpoints.ResolveSessionFile(_sessionDir, Path.Combine(evilSibling, "x.txt")));
    }

    [Fact]
    public void ResolveSessionFile_theRootItself_isNull()
    {
        Assert.Null(ControlEndpoints.ResolveSessionFile(_sessionDir, _sessionDir));
    }
}
