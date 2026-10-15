using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Briefs;

/// <summary>
/// WHAT A TALK OR A RUN IS GIVEN, FOR BOTH KINDS OF FACTORY (Factory Definition, phase 4, design section 9): a DEFINED
/// factory's seat is given its brief from the published definition, recorded as a definition version; any other
/// factory's seat is given exactly what the brief store gives it (goal 14). Over the real stores and a real database.
/// </summary>
[Trait("Category", "FactoryBrief")]
public sealed class FactoryDefinitionBriefsTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryBriefStore _briefs;
    private readonly FactoryDefinitionStore _definitions;
    private readonly FactoryDefinitionBriefs _seatBriefs;

    public FactoryDefinitionBriefsTests()
    {
        var db = _h.Open();
        var registry = new FactoryRegistryStore(db);
        registry.Register(A, new RegisterFactoryRequest
        {
            Factory = TheFactory, Title = "DevThrottle", Folder = "/home/soren/devthrottle-factory", Computer = "SOREN_NORTH", BossSeat = "boss",
            Seats = { new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" } },
        }, "the owner (test)", Now);
        _briefs = new FactoryBriefStore(db, registry);
        _definitions = new FactoryDefinitionStore(db);
        _seatBriefs = new FactoryDefinitionBriefs(_definitions, _briefs);
    }

    public void Dispose() => _h.Dispose();

    private void PublishABrief(string text)
    {
        Assert.True(_briefs.SaveDraft(A, TheFactory, "boss", text, 0, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);
        Assert.True(_briefs.Publish(A, TheFactory, "boss", 1, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);
    }

    private void Define(bool publish)
    {
        Assert.True(_definitions.SaveDraft(A, TheFactory, DefinitionFixture.Valid(), 0, FactoryMemoryAuthorKinds.Person, "owner", null, Now).Ok);
        if (publish) Assert.True(_definitions.Publish(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);
    }

    [Fact]
    public void GivenTo_DefinedFactory_IsTheBriefInThePublishedDefinition_RecordedAsADefinitionVersion()
    {
        PublishABrief("The brief store's text, which a defined factory no longer reads.");
        Define(publish: true);

        var given = _seatBriefs.GivenTo(A, TheFactory, "boss")!;

        Assert.Equal(new FactoryGivenBrief(1, "Run the factory.\n", FactoryBriefVersionKinds.Definition), given);
        Assert.Equal("definition v1", given.Recorded);
        Assert.Equal("definition version 1", given.VersionWords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GivenTo_UNDEFINED_FACTORY_IsExactlyWhatTheBriefStoreGives(bool draftOnly)
    {
        PublishABrief("What this seat is told.");
        if (draftOnly) Define(publish: false);

        var given = _seatBriefs.GivenTo(A, TheFactory, "boss")!;

        Assert.Equal(_briefs.GivenTo(A, TheFactory, "boss"), given);
        Assert.Equal(FactoryBriefVersionKinds.Brief, given.Kind);
        Assert.Equal("1", given.Recorded);
        Assert.Equal("version 1", given.VersionWords);
    }

    [Fact]
    public void GivenTo_UNDEFINED_FACTORY_WithNoBrief_IsNull_AsBefore()
    {
        Assert.Null(_seatBriefs.GivenTo(A, TheFactory, "boss"));
    }

    [Fact]
    public void GivenTo_DefinedFactoryWithNoFileForTheSeat_RefusesInWords()
    {
        Define(publish: true);

        var refused = Assert.Throws<InvalidOperationException>(() => _seatBriefs.GivenTo(A, TheFactory, "ghost"));

        Assert.Equal(FactoryDefinitionBriefs.NotInVersion(TheFactory, "ghost", 1, "published"), refused.Message);
    }

    [Fact]
    public void IsDefined_OnlyAPublishedVersionMakesAFactoryDefined()
    {
        Assert.False(_seatBriefs.IsDefined(A, TheFactory));
        Define(publish: false);
        Assert.False(_seatBriefs.IsDefined(A, TheFactory));
        Assert.True(_definitions.Publish(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);
        Assert.True(_seatBriefs.IsDefined(A, TheFactory));
    }
}
