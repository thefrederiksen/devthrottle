using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE WIRE for the repository a session's folder is a linked worktree of (the one-repository-list
/// mission, "a worktree is not a repository"): the Director resolves it on the machine that holds the
/// disk, the mapper puts it on <see cref="SessionDto.PrimaryRepoPath"/>, and it survives the Gateway's
/// pushed-session cache so the catalogue sees it.
///
/// <para><b>WHY THIS FILE EXISTS SEPARATELY, and it is the lesson this mission has paid for twice.</b>
/// The Director's own tests prove the resolution, and the Gateway's catalogue tests prove what it does
/// with the answer - but every one of those hands itself the answer. <b>Drop this one line from the
/// mapper and the whole feature stops working in the product while every other test in it still
/// passes</b>, because the Gateway is on a Linux container and is forbidden to work the answer out for
/// itself: if the Director does not send it, nobody has it. This is the joint, and these are the only
/// tests that look at it.</para>
/// </summary>
public sealed class AWorktreeIsNotARepositoryWireTests
{
    [Fact]
    public void Map_CarriesTheResolvedRepositoryOntoTheDto()
    {
        using var session = NewSession(@"D:\ReposFred\devthrottle-p5-run-a");
        session.PrimaryRepoPath = @"D:\ReposFred\devthrottle";

        var dto = ControlEndpoints.Map(session, "dir-A");

        Assert.Equal(@"D:\ReposFred\devthrottle", dto.PrimaryRepoPath);
        // And it does NOT move the session: RepoPath is still where the session is, which is what
        // every reader of it means.
        Assert.Equal(@"D:\ReposFred\devthrottle-p5-run-a", dto.RepoPath);
    }

    [Fact]
    public void Map_ASessionInARepositoryProper_CarriesNull()
    {
        // The contrast, and the shape every Director that predates this field sends. Null is "this is
        // not a worktree, or I could not prove which repository it belongs to", and the Gateway then
        // records the folder exactly as it did before.
        using var session = NewSession(@"D:\ReposFred\devthrottle");

        var dto = ControlEndpoints.Map(session, "dir-A");

        Assert.Null(dto.PrimaryRepoPath);
    }

    [Fact]
    public void PushedSessionStore_ServesTheResolvedRepositoryBackUnchanged()
    {
        // The store hands out copies so one request cannot contaminate the cache for the next. A copy
        // that forgot this field would put every worktree back in the catalogue with no other symptom.
        var now = new DateTime(2026, 9, 20, 22, 0, 0, DateTimeKind.Utc);
        var store = new PushedSessionStore(() => now);
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");

        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[]
        {
            new SessionDto
            {
                SessionId = "s-worktree",
                ActivityState = "Working",
                RepoPath = @"D:\ReposFred\devthrottle-p5-run-a",
                PrimaryRepoPath = @"D:\ReposFred\devthrottle",
            },
            new SessionDto
            {
                SessionId = "s-clone",
                ActivityState = "Working",
                RepoPath = @"D:\ReposFred\devthrottle",
            },
        });

        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", TimeSpan.FromSeconds(20));

        Assert.NotNull(fresh);
        Assert.Equal(@"D:\ReposFred\devthrottle",
            Assert.Single(fresh!, s => s.SessionId == "s-worktree").PrimaryRepoPath);
        Assert.Null(Assert.Single(fresh!, s => s.SessionId == "s-clone").PrimaryRepoPath);
    }

    private static Session NewSession(string repoPath)
        => new(Guid.NewGuid(), repoPath, repoPath, null, new NullBackend(), SessionBackendType.ConPty);

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
