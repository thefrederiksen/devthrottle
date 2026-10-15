using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A SEAT START GOES ONLY TO A DIRECTOR THAT BUILDS THE KIT (Factory Definition mission, phase 5, design section 2).
/// An older Director would drop the seat-start fields and start the seat as an ordinary session with none of its
/// limits, so the create is refused before it leaves, with a sentence that says which Director and what to do. A
/// create with none of the fields goes to an older Director exactly as it does today.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryKitCapabilityTests
{
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private static readonly DirectorDto Older = new() { DirectorId = "d-old", MachineName = "SOREN_SOUTH", Version = "2.21.0" };

    private const string OlderRefusal =
        "The Director on SOREN_SOUTH is version 2.21.0, which cannot start a seat of a defined factory (it cannot build " +
        "the seat's kit), so no session was started. Update this Director and try again.";

    private static NewSessionRequest Ordinary() => new() { RepoPath = @"D:\repos\devthrottle", Agent = "ClaudeCode", Factory = "website-factory" };

    public static TheoryData<string> SixFields() => new() { "seat", "version", "kit", "worktree", "home", "model" };

    private static NewSessionRequest With(string field)
    {
        var req = Ordinary();
        switch (field)
        {
            case "seat": req.FactorySeat = "outreach"; break;
            case "version": req.FactoryDefinitionVersion = 3; break;
            case "kit": req.FactoryKit = FactoryKits.Seat; break;
            case "worktree": req.FactoryWorktree = "devthrottle"; break;
            case "home": req.FactoryHome = true; break;
            case "model": req.Model = "claude-opus-5-5"; break;
        }
        return req;
    }

    // ---------- the refusal ----------

    [Theory]
    [MemberData(nameof(SixFields))]
    public void DirectorCannotBuildKit_AnyOfTheSixFieldsToAnOlderDirector_IsRefusedInWords(string field)
    {
        Assert.Equal(OlderRefusal, SpawnFactory.DirectorCannotBuildKit(With(field), Older, buildsFactoryKit: false));
    }

    [Fact]
    public void DirectorCannotBuildKit_UNDEFINED_FACTORY_ACreateWithNoneOfTheFieldsToAnOlderDirector_GoesUnchanged()
    {
        Assert.Null(SpawnFactory.DirectorCannotBuildKit(Ordinary(), Older, buildsFactoryKit: false));
    }

    [Fact]
    public void DirectorCannotBuildKit_FactoryHomeFalse_IsNotASeatStart()
    {
        var req = Ordinary();
        req.FactoryHome = false;

        Assert.Null(SpawnFactory.DirectorCannotBuildKit(req, Older, buildsFactoryKit: false));
    }

    [Fact]
    public void DirectorCannotBuildKit_ADirectorThatBuildsTheKit_IsSentTheSeatStart()
    {
        Assert.Null(SpawnFactory.DirectorCannotBuildKit(With("seat"), Older, buildsFactoryKit: true));
    }

    [Fact]
    public void DirectorCannotBuildKit_ADirectorThatReportsNoVersion_SaysSo()
    {
        var director = new DirectorDto { DirectorId = "d-x", MachineName = "SOREN_NORTH", Version = "" };

        Assert.Equal(
            "The Director on SOREN_NORTH is a version it does not report, which cannot start a seat of a defined factory " +
            "(it cannot build the seat's kit), so no session was started. Update this Director and try again.",
            SpawnFactory.DirectorCannotBuildKit(With("seat"), director, buildsFactoryKit: false));
    }

    // ---------- the registry ----------

    [Fact]
    public void Registry_RecordsWhatEachDirectorSaid_PerAccount()
    {
        var registry = new FactoryKitCapabilityRegistry();
        registry.Record(A, "d-new", buildsFactoryKit: true);
        registry.Record(A, "d-old", buildsFactoryKit: false);

        Assert.True(registry.BuildsFactoryKit(A, "d-new"));
        Assert.False(registry.BuildsFactoryKit(A, "d-old"));
        // The same Director id under another account is a different Director: a Director id is written by the client.
        Assert.False(registry.BuildsFactoryKit(B, "d-new"));
        // A Director never heard from has not said it builds the kit.
        Assert.False(registry.BuildsFactoryKit(A, "d-never"));
        Assert.False(registry.BuildsFactoryKit(A, null));
    }

    [Fact]
    public void Registry_ADirectorThatComesBackOlder_IsRecordedAsOlder()
    {
        var registry = new FactoryKitCapabilityRegistry();
        registry.Record(A, "d-1", buildsFactoryKit: true);
        registry.Record(A, "d-1", buildsFactoryKit: false);

        Assert.False(registry.BuildsFactoryKit(A, "d-1"));
    }

    // ---------- the one spawn path every machine-addressed door shares ----------

    private sealed class Found : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
            => Task.FromResult(new DirectorTargetResult("d-old", null));
    }

    private static (MachineSessionSpawner Spawner, List<NewSessionRequest> Sent) Spawner(Func<NewSessionRequest, string, string?>? guard)
    {
        var sent = new List<NewSessionRequest>();
        var spawner = new MachineSessionSpawner(new Found(), (directorId, req, ct) =>
        {
            sent.Add(req);
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        })
        { RefuseSeatStart = guard };
        return (spawner, sent);
    }

    [Fact]
    public async Task Spawner_SeatStartToAnOlderDirector_IsRefusedBeforeTheCreateIsSent()
    {
        var registry = new FactoryKitCapabilityRegistry();
        registry.Record(A, "d-old", buildsFactoryKit: false);
        var (spawner, sent) = Spawner((req, id) => SpawnFactory.DirectorCannotBuildKit(req, Older, registry.BuildsFactoryKit(A, id)));

        var (ok, _, error, directorId) = await spawner.SpawnOnMachineAsync("SOREN_SOUTH", With("seat"), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(OlderRefusal, error);
        Assert.Equal("d-old", directorId);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Spawner_SeatStartToADirectorThatBuildsTheKit_IsSent()
    {
        var registry = new FactoryKitCapabilityRegistry();
        registry.Record(A, "d-old", buildsFactoryKit: true);
        var (spawner, sent) = Spawner((req, id) => SpawnFactory.DirectorCannotBuildKit(req, Older, registry.BuildsFactoryKit(A, id)));

        var (ok, _, _, _) = await spawner.SpawnOnMachineAsync("SOREN_SOUTH", With("seat"), CancellationToken.None);

        Assert.True(ok);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Spawner_UNDEFINED_FACTORY_ACreateWithNoneOfTheFields_GoesToAnOlderDirectorUnchanged()
    {
        var (spawner, sent) = Spawner((req, id) => SpawnFactory.DirectorCannotBuildKit(req, Older, buildsFactoryKit: false));
        var req = Ordinary();

        var (ok, _, _, _) = await spawner.SpawnOnMachineAsync("SOREN_SOUTH", req, CancellationToken.None);

        Assert.True(ok);
        Assert.Same(req, Assert.Single(sent));
    }

    [Fact]
    public async Task Spawner_WithNoGuard_RefusesASeatStartAndSendsAnOrdinaryCreate()
    {
        var (spawner, sent) = Spawner(guard: null);

        var (seatOk, _, seatError, _) = await spawner.SpawnOnMachineAsync("SOREN_SOUTH", With("kit"), CancellationToken.None);
        var (plainOk, _, _, _) = await spawner.SpawnOnMachineAsync("SOREN_SOUTH", Ordinary(), CancellationToken.None);

        Assert.False(seatOk);
        Assert.Equal(MachineSessionSpawner.CannotTellKit, seatError);
        Assert.True(plainOk);
        Assert.Single(sent);
    }
}
