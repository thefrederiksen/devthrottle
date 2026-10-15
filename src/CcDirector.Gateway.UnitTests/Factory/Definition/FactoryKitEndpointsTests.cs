using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Records;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE KIT ROUTE (Factory Definition, phase 5, design section 4), driven through its handler over the real stores and
/// a real database. These pin what only the route knows: that only a Director's own credential gets a kit, that a
/// draft and a seat the version does not hold are refused in words, and that the records it reads are the seat's own.
/// Every refusal is proven by the sentence it carries.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryKitEndpointsTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryKitEndpoints.Stores _stores;

    public FactoryKitEndpointsTests()
    {
        var db = _h.Open();
        var attachments = new FactoryAttachmentStore(db);
        _stores = new FactoryKitEndpoints.Stores(new FactoryDefinitionStore(db), new FactoryJournalStore(db, attachments),
            new FactoryDocumentStore(db), attachments);
    }

    public void Dispose() => _h.Dispose();

    // ---------- who is asking ----------

    /// <summary>A Director: a verified credential that is neither a session's key nor a person's device.</summary>
    private static DefaultHttpContext Director(string seat = "outreach", string kind = "seat")
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-director-key";
        ctx.Request.QueryString = new QueryString($"?seat={seat}&kind={kind}");
        return ctx;
    }

    private static DefaultHttpContext Person()
    {
        var ctx = Director();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return ctx;
    }

    private static DefaultHttpContext SessionKey()
    {
        var ctx = Director();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
            new SessionCredentialIdentity(Guid.Parse("77000000-0000-4000-8000-000000000001"), A, "dir-a");
        return ctx;
    }

    private static DefaultHttpContext NoCredential()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?seat=outreach&kind=seat");
        return ctx;
    }

    private IResult Kit(HttpContext ctx, string version = "1", string factory = TheFactory) =>
        FactoryKitEndpoints.Kit(ctx, factory, version, _stores, _ => A, () => Now);

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string ErrorOf(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        return (string)value.GetType().GetProperty("error")!.GetValue(value)!;
    }

    private static FactoryKitResponse KitOf(IResult result) => Assert.IsType<JsonHttpResult<FactoryKitResponse>>(result).Value!;

    // ---------- the definition ----------

    private int SaveDraft()
    {
        var write = _stores.Definitions.SaveDraft(A, TheFactory, DefinitionFixture.Publishable(),
            _stores.Definitions.Standing(A, TheFactory).Newest?.Version ?? 0, FactoryMemoryAuthorKinds.Person, "owner", null, Now);
        Assert.True(write.Ok, write.Refusal);
        return write.Version!.Version;
    }

    private int Published()
    {
        var version = SaveDraft();
        var write = _stores.Definitions.Publish(A, TheFactory, version, FactoryMemoryAuthorKinds.Person, "owner", Now);
        Assert.True(write.Ok, write.Refusal);
        return version;
    }

    // ---------- only a Director ----------

    [Fact]
    public void Kit_APersonsDevice_IsRefusedInWords()
    {
        Published();

        var result = Kit(Person());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Equal(FactoryKitEndpoints.OnlyADirector, ErrorOf(result));
    }

    [Fact]
    public void Kit_ASessionsKey_IsRefusedInWords()
    {
        Published();

        var result = Kit(SessionKey());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Equal(FactoryKitEndpoints.OnlyADirector, ErrorOf(result));
    }

    [Fact]
    public void Kit_ARequestWithNoVerifiedCredential_IsNotADirector()
    {
        Published();

        var result = Kit(NoCredential());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Equal(FactoryKitEndpoints.OnlyADirector, ErrorOf(result));
    }

    [Fact]
    public void SessionKeyGuard_DoesNotLetASessionKeyReachTheKitRoute()
    {
        // The route refuses a session's key itself (above); the guard refuses it before the route is reached, so a seat
        // cannot read any kit with its own key. If the guard ever listed this path, this fails.
        Assert.False(SessionKeyGuard.Check("GET", $"/factory-definitions/{TheFactory}/versions/1/kit").Allowed);
    }

    // ---------- what is refused ----------

    [Fact]
    public void Kit_ADraftVersion_IsRefusedBecauseAKitIsNeverBuiltFromADraft()
    {
        var draft = SaveDraft();

        var result = Kit(Director(), draft.ToString());

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal($"version {draft} of the definition of {TheFactory} is a draft, and a kit is built only from a published version, so no kit was built. The owner publishes it first.",
            ErrorOf(result));
    }

    [Fact]
    public void Kit_ASeatTheVersionDoesNotHold_IsRefusedAndTheSeatsAreNamed()
    {
        var version = Published();

        var result = Kit(Director(seat: "nobody"), version.ToString());

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Equal($"version {version} of the definition of {TheFactory} has no seat 'nobody', so no kit was built. Its seats are: boss, outreach, writer.",
            ErrorOf(result));
    }

    [Fact]
    public void Kit_AVersionThatDoesNotExist_IsRefusedWithTheNewestVersion()
    {
        Published();

        var result = Kit(Director(), "9");

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Equal($"version 9 of the definition of {TheFactory} does not exist, so no kit was built; its newest version is 1", ErrorOf(result));
    }

    [Fact]
    public void Kit_UNDEFINED_FACTORY_IsRefusedAsHavingNoDefinition()
    {
        // A factory with no definition has no kit: its seats start exactly as they always did, and nothing asks.
        var result = Kit(Director(), "1", "invoice-factory");

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Equal("invoice-factory has no definition on the Gateway, so no kit was built", ErrorOf(result));
    }

    [Fact]
    public void Kit_AKindThatIsNeitherSeatNorRulesOnly_IsRefused()
    {
        Published();

        var result = Kit(Director(kind: "everything"));

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.StartsWith("a kit is 'seat' or 'rules-only', not 'everything'", ErrorOf(result));
    }

    [Fact]
    public void Kit_NoSeatNamed_IsRefused()
    {
        Published();

        var result = Kit(Director(seat: ""));

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("name the seat the kit is for: pass ?seat=<id>", ErrorOf(result));
    }

    // ---------- what a Director gets ----------

    [Fact]
    public void Kit_ADirector_GetsTheSeatKitWithTheSeatsOwnJournalOnly()
    {
        var version = Published();
        Assert.True(_stores.Journal.Append(A, TheFactory, "outreach", "2026-10-14", "wrote to Ada", null,
            FactoryMemoryAuthorKinds.Session, "s-1", Now).Ok);
        Assert.True(_stores.Journal.Append(A, TheFactory, "writer", "2026-10-14", "the writer's own note", null,
            FactoryMemoryAuthorKinds.Session, "s-2", Now).Ok);
        Assert.True(_stores.Documents.Write(A, TheFactory, "outreach/ledger.json", "{}", 0,
            FactoryMemoryAuthorKinds.Session, "s-1", Now).Ok);

        var kit = KitOf(Kit(Director(), version.ToString()));

        Assert.Equal(TheFactory, kit.Factory);
        Assert.Equal("outreach", kit.Seat);
        Assert.Equal(version, kit.Version);
        Assert.Equal("seat", kit.Kind);
        var files = kit.Files.ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);
        Assert.Contains("wrote to Ada", files["records/journal.md"]);
        Assert.DoesNotContain("the writer's own note", files["records/journal.md"]);
        Assert.Contains("outreach/ledger.json", files["records/documents.md"]);
        Assert.Contains("\"builtAtUtc\": \"2026-10-15T12:00:00Z\"", files["kit.json"]);
    }

    [Fact]
    public void Kit_ADirectorAskingForARulesOnlyKit_GetsKitJsonAndRulesJsonOnly()
    {
        var version = Published();

        var kit = KitOf(Kit(Director(kind: "rules-only"), version.ToString()));

        Assert.Equal(new[] { "kit.json", "rules.json" }, kit.Files.Select(f => f.Path).ToArray());
    }
}
