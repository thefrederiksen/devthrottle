using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// EXPORT AND IMPORT, THROUGH THEIR ROUTES (Factory Definition mission, phase 3), over the real store, the real check
/// and a real database. Export gives a stored version's files exactly as stored, so two exports are the same bytes; a
/// factory with no definition is written from what is live. Import is checked EXACTLY as a draft save is - the same
/// check, the same callers, the same refusal with every finding by file and line - and NEVER publishes.
///
/// A refusal is proven by the answer that carries it, never by the absence of an error.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionExportImportEndpointsTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private const string FactorySession = "76000000-0000-4000-8000-000000000001";
    private const string OtherFactorySession = "76000000-0000-4000-8000-000000000002";
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryDefinitionStore _store;
    private readonly FactoryPublishApply _apply;
    private readonly FactoryDefinitionEndpoints.Account _account;

    /// <summary>What the live source answers for a factory with no definition; null means "not registered".</summary>
    private FactoryLiveParts? _live;

    public FactoryDefinitionExportImportEndpointsTests()
    {
        var db = _h.Open();
        _store = new FactoryDefinitionStore(db);
        // A PUBLISH DRIVES THE FACTORY (phase 4), so the factory is registered and the definition published is the
        // fixture without the start that has a check.
        _apply = new FactoryPublishApply(db);
        new CcDirector.Gateway.Factory.Registry.FactoryRegistryStore(db).Register(A, DefinitionFixture.Registration(), "test", Now);
        _account = new FactoryDefinitionEndpoints.Account(_ => DefinitionFixture.FleetSkills, _ => DefinitionFixture.Machines, DefinitionFixture.AskNobody);
    }

    public void Dispose() => _h.Dispose();

    private static DefaultHttpContext Person()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return ctx;
    }

    private static DefaultHttpContext Session(string id)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.Parse(id), A, "dir-a");
        return ctx;
    }

    private static DefaultHttpContext Machine()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
        return ctx;
    }

    private static SessionFactoryLookup FactoryOf(string session) => session switch
    {
        FactorySession => SessionFactoryLookup.In(TheFactory),
        OtherFactorySession => SessionFactoryLookup.In("invoice-factory"),
        _ => SessionFactoryLookup.NotKnown,
    };

    private static TenantId? InA(HttpContext _) => A;

    private IResult Export(HttpContext ctx, string factory = TheFactory, int? version = null)
    {
        if (version is not null) ctx.Request.QueryString = new QueryString($"?version={version}");
        return FactoryDefinitionEndpoints.Export(ctx, factory, _store, InA, FactoryOf, (_, _) => _live);
    }

    private IResult Import(HttpContext ctx, IReadOnlyDictionary<string, string?> content, string? note = null) =>
        FactoryDefinitionEndpoints.Import(ctx,
            new ImportFactoryDefinitionRequest { Note = note, Content = new Dictionary<string, string>(content!) },
            _store, InA, FactoryOf, _account, () => Now);

    private IResult Save(HttpContext ctx, IReadOnlyDictionary<string, string?> content, int expect) =>
        FactoryDefinitionEndpoints.SaveDraft(ctx, TheFactory,
            new SaveFactoryDefinitionDraftRequest { ExpectedVersion = expect, Content = new Dictionary<string, string>(content!) },
            _store, InA, FactoryOf, _account, () => Now);

    private IResult Publish(int version) =>
        FactoryDefinitionEndpoints.Publish(Person(), TheFactory, new PublishFactoryDefinitionRequest { Version = version },
            _store, _apply, InA, FactoryOf, _account, () => Now.AddMinutes(1)).GetAwaiter().GetResult();

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static FactoryDefinitionDto Version(IResult result) => Assert.IsType<JsonHttpResult<FactoryDefinitionDto>>(result).Value!;

    private static FactoryDefinitionExportDto Folder(IResult result) => Assert.IsType<JsonHttpResult<FactoryDefinitionExportDto>>(result).Value!;

    private static object? Field(IResult result, string name)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        var property = value.GetType().GetProperty(name);
        Assert.True(property is not null, $"the answer has no '{name}'");
        return property!.GetValue(value);
    }

    private static List<FactoryDefinitionFindingDto> Findings(IResult result) =>
        Assert.IsAssignableFrom<IEnumerable<FactoryDefinitionFindingDto>>(Field(result, "findings")).ToList();

    private static Dictionary<string, string?> Valid() => DefinitionFixture.Publishable().ToDictionary(p => p.Key, p => (string?)p.Value);

    // ---------- export ----------

    [Fact]
    public void A_STORED_VERSION_EXPORTS_EXACTLY_AS_STORED_AND_TWICE_IS_THE_SAME()
    {
        var crlf = DefinitionFixture.With("files/template.md", "line one\r\nline two \u00E6\r\n");
        Assert.Equal(200, Status(Save(Person(), crlf.ToDictionary(p => p.Key, p => (string?)p.Value), 0)));

        var first = Folder(Export(Person()));
        var second = Folder(Export(Session(FactorySession)));

        Assert.Equal("definition", first.Source);
        Assert.Equal(1, first.Version);
        Assert.Equal("draft", first.State);
        Assert.Equal(crlf, first.Files);
        Assert.Equal(first.Files, second.Files);
        Assert.Equal(first.Files.Keys.OrderBy(k => k, StringComparer.Ordinal), first.Files.Keys);
        Assert.Empty(first.Reported);
        Assert.Contains("No version is published, so the newest is exported.", first.Summary);
    }

    [Fact]
    public void WITHOUT_A_VERSION_THE_PUBLISHED_ONE_IS_EXPORTED_AND_A_NEWER_DRAFT_IS_NAMED()
    {
        Save(Person(), Valid(), 0);
        Publish(1);
        var second = DefinitionFixture.Edit("seats/boss.yaml", "Run the factory.", "Run the factory well.");
        Save(Person(), second.ToDictionary(p => p.Key, p => (string?)p.Value), 1);

        var published = Folder(Export(Person()));
        var draft = Folder(Export(Person(), version: 2));

        Assert.Equal(1, published.Version);
        Assert.Equal("published", published.State);
        Assert.Equal(DefinitionFixture.Publishable(), published.Files);
        Assert.Contains("A newer draft, version 2, is not exported", published.Summary);
        Assert.Equal(2, draft.Version);
        Assert.Equal(second, draft.Files);
    }

    [Fact]
    public void A_VERSION_THAT_DOES_NOT_EXIST_IS_A_404_IN_WORDS()
    {
        Save(Person(), Valid(), 0);

        var refused = Export(Person(), version: 7);

        Assert.Equal(404, Status(refused));
        Assert.Equal($"version 7 of the definition of {TheFactory} does not exist; its newest version is 1", Field(refused, "error"));
    }

    [Fact]
    public void A_FACTORY_WITH_NO_DEFINITION_IS_EXPORTED_FROM_WHAT_IS_LIVE_WITH_WHAT_IT_COULD_NOT_CARRY()
    {
        _live = new FactoryLiveParts(
            new RegisteredFactoryDto
            {
                Factory = TheFactory, Title = "DevThrottle", Folder = @"D:\factory", Computer = "SOREN_NORTH", BossSeat = "boss",
                Seats = { new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "BOSS.md", Computer = "SOREN_NORTH" } },
            },
            new Dictionary<string, string> { ["boss"] = "Run it.\n" },
            Array.Empty<CronJobDto>(),
            Array.Empty<LiveTrigger>());

        var folder = Folder(Export(Person()));

        Assert.Equal("live", folder.Source);
        Assert.Null(folder.Version);
        Assert.Equal(new[] { "factory.yaml", "seats/boss.yaml" }, folder.Files.Keys);
        Assert.Equal("seat: boss\nname: Boss\nrole: Boss\nbrief: |\n  Run it.\n", folder.Files["seats/boss.yaml"]);
        Assert.Contains(folder.Reported, r => r.File == "factory.yaml" && r.Message.Contains(@"The factory's folder 'D:\factory'"));
        Assert.Contains($"{folder.Reported.Count} things it has are not in the folder", folder.Summary);
        // Nothing was stored by an export.
        Assert.Null(_store.Standing(A, TheFactory).Newest);
    }

    [Fact]
    public void A_FACTORY_WITH_NO_DEFINITION_THAT_IS_NOT_REGISTERED_HAS_NOTHING_TO_EXPORT()
    {
        var refused = Export(Person());

        Assert.Equal(404, Status(refused));
        Assert.Equal($"{TheFactory} has no definition on the Gateway and is not a registered factory, so there is nothing to export",
            Field(refused, "error"));
    }

    [Fact]
    public void A_SESSION_OF_ANOTHER_FACTORY_CANNOT_EXPORT_THIS_ONE()
    {
        Save(Person(), Valid(), 0);

        var refused = Export(Session(OtherFactorySession));

        Assert.Equal(403, Status(refused));
    }

    // ---------- import ----------

    [Fact]
    public void AN_IMPORT_OF_A_NEW_FACTORY_CREATES_ITS_FIRST_VERSION_AS_A_DRAFT_AND_PUBLISHES_NOTHING()
    {
        var imported = Import(Person(), Valid(), note: "from the folder");

        Assert.Equal(200, Status(imported));
        var v = Version(imported);
        Assert.Equal(TheFactory, v.Factory);
        Assert.Equal(1, v.Version);
        Assert.Equal("draft", v.State);
        Assert.True(v.Created);
        Assert.Null(v.PublishedVersion);
        Assert.Equal("from the folder", v.ChangeNote);
        Assert.Equal("person", v.AuthorKind);
        Assert.Null(_store.Standing(A, TheFactory).Published);
        Assert.Equal(DefinitionFixture.Publishable(), _store.Find(A, TheFactory, 1)!.Files);
    }

    [Fact]
    public void AN_IMPORT_OVER_A_PUBLISHED_DEFINITION_IS_THE_NEXT_DRAFT_AND_THE_PUBLISHED_VERSION_STAYS()
    {
        Save(Person(), Valid(), 0);
        Publish(1);
        var changed = DefinitionFixture.Edit("seats/boss.yaml", "Run the factory.", "Run the factory from the folder.");

        var before = _store.Standing(A, TheFactory).Published!.Version;
        var imported = Import(Session(FactorySession), changed.ToDictionary(p => p.Key, p => (string?)p.Value));
        var after = _store.Standing(A, TheFactory);

        Assert.Equal(200, Status(imported));
        var v = Version(imported);
        Assert.Equal(2, v.Version);
        Assert.Equal("draft", v.State);
        Assert.False(v.Created);
        Assert.Equal("imported from a folder", v.ChangeNote);
        Assert.Equal(1, before);
        Assert.Equal(1, after.Published!.Version);
        Assert.Equal(1, v.PublishedVersion);
        Assert.Equal(2, after.Newest!.Version);
    }

    [Fact]
    public void AN_IMPORT_THAT_FAILS_THE_CHECK_IS_REFUSED_AS_A_SAVE_IS_WITH_FILE_AND_LINE_AND_NOTHING_IS_STORED()
    {
        var broken = DefinitionFixture.With("seats/writer.yaml", DefinitionFixture.Writer + "\ncolour: red");
        var asContent = broken.ToDictionary(p => p.Key, p => (string?)p.Value);

        var imported = Import(Person(), asContent);
        var saved = Save(Person(), asContent, 0);

        Assert.Equal(422, Status(imported));
        Assert.Equal("CheckFailed", Field(imported, "outcome"));
        Assert.StartsWith($"The folder was not imported: the check found 1 error in the definition of {TheFactory}.", (string)Field(imported, "error")!);
        var error = Assert.Single(Findings(imported), f => f.Severity == "error");
        Assert.Equal("seats/writer.yaml", error.File);
        Assert.Equal(5, error.Line);
        Assert.Contains("'colour' is not a key", error.Message);
        // Exactly the save's findings: the same check, not a copy of it.
        Assert.Equal(Findings(saved).Select(f => (f.Severity, f.File, f.Line, f.Message)), Findings(imported).Select(f => (f.Severity, f.File, f.Line, f.Message)));
        Assert.Null(_store.Standing(A, TheFactory).Newest);
    }

    [Fact]
    public void AN_IMPORT_WITH_NO_FACTORY_YAML_IS_REFUSED_SAYING_SO()
    {
        var withoutFactory = DefinitionFixture.Without("factory.yaml").ToDictionary(p => p.Key, p => (string?)p.Value);

        var imported = Import(Person(), withoutFactory);

        Assert.Equal(422, Status(imported));
        Assert.Equal("The folder was not imported: its factory.yaml does not say which factory it is for. Fix factory.yaml.", Field(imported, "error"));
        var error = Assert.Single(Findings(imported), f => f.File == "factory.yaml");
        Assert.Contains("no factory.yaml", error.Message);
    }

    [Fact]
    public void A_SESSION_OF_ANOTHER_FACTORY_AND_A_MACHINE_TOKEN_CANNOT_IMPORT()
    {
        var other = Import(Session(OtherFactorySession), Valid());
        var machine = Import(Machine(), Valid());

        Assert.Equal(403, Status(other));
        Assert.Equal(403, Status(machine));
        Assert.Equal("a factory's definition is written by a person, or by a session of that factory", Field(machine, "error"));
        Assert.Null(_store.Standing(A, TheFactory).Newest);
    }

    [Fact]
    public void A_SESSION_OF_THE_FACTORY_MAY_IMPORT_AND_ITS_IMPORT_IS_STILL_ONLY_A_DRAFT()
    {
        var imported = Import(Session(FactorySession), Valid());

        Assert.Equal(200, Status(imported));
        Assert.Equal("session", Version(imported).AuthorKind);
        Assert.Equal(FactorySession, Version(imported).AuthorId);
        Assert.Null(_store.Standing(A, TheFactory).Published);
    }

    [Fact]
    public void A_MACHINE_TOKEN_IS_REFUSED_BEFORE_ITS_CONTENT_IS_READ()
    {
        // Review of 4007, finding 3: content that would be refused for itself - a null path, no factory.yaml - must not
        // be answered for a caller that may never write.
        var nullPath = Valid();
        nullPath["factory.yaml"] = null;
        var noFactory = DefinitionFixture.Without("factory.yaml").ToDictionary(p => p.Key, p => (string?)p.Value);

        foreach (var content in new[] { nullPath, noFactory })
        {
            var refused = Import(Machine(), content);
            Assert.Equal(403, Status(refused));
            Assert.Equal("a factory's definition is written by a person, or by a session of that factory", Field(refused, "error"));
        }
    }

    // ---------- a path that maps to no text (review 2 of 4004) ----------

    [Fact]
    public void A_DRAFT_SAVE_THAT_MAPS_A_PATH_TO_NULL_IS_A_400_NAMING_THE_PATH()
    {
        var content = Valid();
        content["seats/writer.yaml"] = null;

        var refused = Save(Person(), content, 0);

        Assert.Equal(400, Status(refused));
        Assert.Equal("NotText", Field(refused, "outcome"));
        Assert.Equal("'seats/writer.yaml' has no text; every file in a definition is text, even if it is empty", Field(refused, "error"));
        Assert.Null(_store.Standing(A, TheFactory).Newest);
    }

    [Fact]
    public void AN_IMPORT_THAT_MAPS_A_PATH_TO_NULL_IS_THE_SAME_400()
    {
        var content = Valid();
        content["factory.yaml"] = null;

        var refused = Import(Person(), content);

        Assert.Equal(400, Status(refused));
        Assert.Equal("NotText", Field(refused, "outcome"));
        Assert.Equal("'factory.yaml' has no text; every file in a definition is text, even if it is empty", Field(refused, "error"));
    }

    // ---------- the round trip the mission's check names ----------

    [Fact]
    public void EXPORT_IMPORT_INTO_A_NEW_ID_AND_EXPORT_AGAIN_GIVES_THE_SAME_BYTES_FOR_THE_FOLDER_IMPORTED()
    {
        Save(Person(), Valid(), 0);
        Publish(1);
        var exported = Folder(Export(Person())).Files;

        // A new id is a new factory.yaml id line; that is the one edit, and the folder imported is the folder compared.
        var renamed = new Dictionary<string, string>(exported, StringComparer.Ordinal)
        {
            ["factory.yaml"] = exported["factory.yaml"].Replace("factory: devthrottle", "factory: devthrottle-copy"),
        };
        var imported = Import(Person(), renamed.ToDictionary(p => p.Key, p => (string?)p.Value));
        Assert.Equal(200, Status(imported));
        Assert.Equal("devthrottle-copy", Version(imported).Factory);
        Assert.True(Version(imported).Created);

        var again = Folder(Export(Person(), factory: "devthrottle-copy")).Files;

        Assert.Equal(renamed, again);
        var differing = exported.Keys.Where(k => exported[k] != again[k]).ToList();
        Assert.Equal(new[] { "factory.yaml" }, differing);
    }
}
