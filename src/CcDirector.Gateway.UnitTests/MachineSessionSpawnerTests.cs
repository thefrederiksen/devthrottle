using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="MachineSessionSpawner"/> - the single resolve-then-create path shared by
/// the cron firing engine and the interactive POST /machines/{machine}/sessions relay ("start a session
/// on another computer"). A stub resolver stands in for the live registry/launcher and a fake create
/// delegate stands in for the Director's session-create call, so the resolve-then-create DECISION is
/// verified without a live Director. Covers the good path (resolver returns a DirectorId -> create ->
/// success), the fail-loud path (resolver returns an offline Error -> failure, and the create is NEVER
/// attempted, i.e. no local fallback), and - through the REAL resolver - the tunnel-only case of a
/// Director with a blank control endpoint (issue #1727).
/// </summary>
public sealed class MachineSessionSpawnerTests
{
    /// <summary>A resolver that returns a fixed <see cref="DirectorTargetResult"/> and counts its calls.</summary>
    private sealed class StubResolver : IDirectorTargetResolver
    {
        private readonly DirectorTargetResult _result;
        public int ResolveCount { get; private set; }
        public string? LastMachine { get; private set; }
        public string? LastDirector { get; private set; }

        public StubResolver(DirectorTargetResult result) => _result = result;

        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
        {
            ResolveCount++;
            LastMachine = machine;
            LastDirector = director;
            return Task.FromResult(_result);
        }
    }

    /// <summary>A launcher that must never be called: the Director is already registered on the machine.</summary>
    private sealed class ThrowingLauncher : IDirectorLauncher
    {
        public Task<bool> StartAsync(CcDirector.Core.Tenancy.TenantId tenant, string machine, CancellationToken ct) =>
            throw new InvalidOperationException("launcher must not be called: the Director is already registered");
    }

    [Fact]
    public async Task Spawn_ResolverReturnsDirectorId_CreatesSession_ReturnsIt()
    {
        var resolver = new StubResolver(new DirectorTargetResult("d-new", null));
        string? seenDirectorId = null;
        NewSessionRequest? seenReq = null;
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
        {
            seenDirectorId = directorId;
            seenReq = req;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-123" }, null));
        });

        var request = new NewSessionRequest { RepoPath = @"C:\repo", Agent = "ClaudeCode" };
        var (ok, dto, error, directorId) = await spawner.SpawnOnMachineAsync("MACHINE_A", request, CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(dto);
        Assert.Equal("sid-123", dto!.SessionId);
        Assert.Null(error);
        Assert.Equal("d-new", directorId);
        // The create was made against the resolved Director id (the tunnel leg) with the same request.
        Assert.Equal("d-new", seenDirectorId);
        Assert.Same(request, seenReq);
        Assert.Equal("MACHINE_A", resolver.LastMachine);
    }

    // The Fleet Manager mission, step 5: a caller may refuse the RESOLVED Director before anything is sent to it -
    // the only moment a Director the launcher just started is known.
    [Fact]
    public async Task Spawn_RefuseDirectorAnswers_NoCreateIsSentAndTheSentenceIsReturned()
    {
        var resolver = new StubResolver(new DirectorTargetResult("d-old", null));
        var creates = 0;
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
        {
            creates++;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        });
        string? asked = null;

        var (ok, dto, error, directorId) = await spawner.SpawnOnMachineAsync("MACHINE_A", new NewSessionRequest(), CancellationToken.None,
            id => { asked = id; return "that Director must be updated"; });

        Assert.False(ok);
        Assert.Null(dto);
        Assert.Equal("that Director must be updated", error);
        Assert.Equal("d-old", directorId);
        Assert.Equal("d-old", asked);
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task Spawn_RefuseDirectorAllows_TheCreateIsSent()
    {
        var resolver = new StubResolver(new DirectorTargetResult("d-new", null));
        var creates = 0;
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
        {
            creates++;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        });

        var (ok, _, _, _) = await spawner.SpawnOnMachineAsync("MACHINE_A", new NewSessionRequest(), CancellationToken.None, _ => null);

        Assert.True(ok);
        Assert.Equal(1, creates);
    }

    // A spawn that named ONE Director must reach the resolver as a named target. The spawner is the only
    // thing between the request body and the resolve, so if it drops req.Director the resolve quietly
    // falls back to "first Director on the machine" - which on a machine running several instances lands
    // the session somewhere else and reports success. That failure is invisible from the caller's side:
    // it gets a session id back either way.
    [Fact]
    public async Task Spawn_RequestNamesADirector_ThatNamePinsTheResolve()
    {
        var resolver = new StubResolver(new DirectorTargetResult("d-slot-5", null));
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
            Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null)));

        var request = new NewSessionRequest { RepoPath = @"C:\repo", Director = "North build" };
        var (ok, _, _, directorId) = await spawner.SpawnOnMachineAsync("MACHINE_A", request, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("North build", resolver.LastDirector);
        Assert.Equal("d-slot-5", directorId);
    }

    // The other direction of the same guard: an ORDINARY machine spawn must NOT arrive at the resolver
    // carrying a name. A resolver handed a stray name pins to it and stops launching on demand, so a
    // cron job or a plain --machine spawn would fail on a machine whose Director is not running yet.
    [Fact]
    public async Task Spawn_RequestNamesNoDirector_ResolvesByMachineAlone()
    {
        var resolver = new StubResolver(new DirectorTargetResult("d-first", null));
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
            Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-2" }, null)));

        await spawner.SpawnOnMachineAsync(
            "MACHINE_A", new NewSessionRequest { RepoPath = @"C:\repo" }, CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(resolver.LastDirector));
    }

    [Fact]
    public async Task Spawn_ResolverReturnsOfflineError_FailsLoud_DoesNotCreateLocally()
    {
        var resolver = new StubResolver(
            new DirectorTargetResult(null, "no Director on 'MACHINE_B' and the launcher could not start one"));
        var createCalled = false;
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
        {
            createCalled = true;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "must-not-happen" }, null));
        });

        var (ok, dto, error, _) = await spawner.SpawnOnMachineAsync(
            "MACHINE_B", new NewSessionRequest { RepoPath = @"C:\repo" }, CancellationToken.None);

        Assert.False(ok);
        Assert.Null(dto);
        Assert.NotNull(error);
        Assert.Contains("could not start one", error!);
        // No local fallback: the create is never attempted when the machine cannot be resolved.
        Assert.False(createCalled);
    }

    [Fact]
    public async Task Spawn_CreateFails_ReportsTheCreateError_KeepsResolvedDirectorId()
    {
        var resolver = new StubResolver(new DirectorTargetResult("d-live", null));
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
            Task.FromResult<(bool, SessionDto?, string?)>((false, null, "director returned 500: boom")));

        var (ok, dto, error, directorId) = await spawner.SpawnOnMachineAsync(
            "MACHINE_A", new NewSessionRequest { RepoPath = @"C:\repo" }, CancellationToken.None);

        Assert.False(ok);
        Assert.Null(dto);
        Assert.Equal("director returned 500: boom", error);
        Assert.Equal("d-live", directorId);   // still carries the resolved Director for the run record
    }

    // Issue #1727 regression: a genuinely remote, tunnel-only Director registers with a BLANK
    // ControlEndpoint. Resolving it through the REAL RegistryDirectorTargetResolver (not a stub that
    // injects a fake endpoint the production resolver never emits) and spawning must SUCCEED - delivery
    // is by DirectorId over the tunnel. The old guard rejected on the blank endpoint with a 502-style
    // "could not resolve a director" error; this test fails against that code and passes with the fix.
    [Fact]
    public async Task Spawn_RealResolver_DirectorWithBlankControlEndpoint_CreatesSession()
    {
        var director = new DirectorDto
        {
            DirectorId = "d-remote",
            MachineName = "Sorens-Mac-mini",
            ControlEndpoint = "",       // tunnel-only mode: always blank
            Source = "stream",
        };
        var resolver = new RegistryDirectorTargetResolver(
            listDirectors: _ => new[] { director },
            resolveTenant: () => TenantId.Local,
            launcher: new ThrowingLauncher());

        string? seenDirectorId = null;
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
        {
            seenDirectorId = directorId;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-remote" }, null));
        });

        var (ok, dto, error, directorId) = await spawner.SpawnOnMachineAsync(
            "Sorens-Mac-mini", new NewSessionRequest { RepoPath = "/repo" }, CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(dto);
        Assert.Equal("sid-remote", dto!.SessionId);
        Assert.Null(error);
        Assert.Equal("d-remote", directorId);
        Assert.Equal("d-remote", seenDirectorId);   // create invoked with the resolved DirectorId
    }
}
