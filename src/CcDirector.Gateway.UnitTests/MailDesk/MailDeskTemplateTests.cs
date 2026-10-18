using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE BUILT-IN MAIL DESK TEMPLATE (Mail Desk mission, design 5.3), as embedded in the Gateway binary: exactly the
/// expected files, the one seat and its checked start in the shape the mission names, a stable template version, and
/// a definition that passes the Gateway's own check through the REAL import and create routes - the ones a person's
/// folder goes through - not a copy of them.
///
/// The import tests need <c>checks/screen.py</c>, which the screening check's Developer adds to the template folder.
/// Until it is there they fail, naming the missing check.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskTemplateTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryDefinitionStore _store;
    private readonly FactoryDefinitionEndpoints.Account _account;

    public MailDeskTemplateTests()
    {
        _store = new FactoryDefinitionStore(_h.Open());
        _account = new FactoryDefinitionEndpoints.Account(_ => DefinitionFixture.FleetSkills, _ => new[] { "devlinux" },
            DefinitionFixture.AskNobody);
    }

    public void Dispose() => _h.Dispose();

    private static DefaultHttpContext Person()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return ctx;
    }

    private static TenantId? InA(HttpContext _) => A;

    private static SessionFactoryLookup NoSession(string _) => SessionFactoryLookup.NotKnown;

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string Body(IResult result) =>
        System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static FactoryDefinition Parsed() =>
        FactoryDefinitionCheck.Read(MailDeskTemplate.Files).Definition
        ?? throw new InvalidOperationException("The embedded template does not parse.");

    // ---------- what is embedded ----------

    [Fact]
    public void Files_Embedded_HoldExactlyTheExpectedPaths()
    {
        // Arrange
        var expected = MailDeskTemplate.ExpectedPaths;

        // Act
        var paths = MailDeskTemplate.Files.Keys.ToList();

        // Assert
        Assert.Equal(expected, paths);
    }

    [Fact]
    public void Files_Embedded_HoldLineFeedsOnlyAndNoByteOrderMark()
    {
        // Arrange
        var files = MailDeskTemplate.Files;

        // Act
        var withCarriageReturn = files.Where(f => f.Value.Contains('\r')).Select(f => f.Key).ToList();
        var withByteOrderMark = files.Where(f => f.Value.StartsWith(DefinitionDocument.ByteOrderMark)).Select(f => f.Key).ToList();

        // Assert
        Assert.NotEmpty(files);
        Assert.Empty(withCarriageReturn);
        Assert.Empty(withByteOrderMark);
    }

    [Fact]
    public void Files_FactoryAndSeat_NameNoAddress()
    {
        // Arrange: the channels are Gateway data (D19, D20), so the definition names no mailbox.
        var address = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
        var definitionFiles = MailDeskTemplate.Files.Where(f => f.Key.EndsWith(".yaml", StringComparison.Ordinal)).ToList();

        // Act
        var withAddress = definitionFiles.Where(f => address.IsMatch(f.Value)).Select(f => f.Key).ToList();

        // Assert
        Assert.Equal(2, definitionFiles.Count);
        Assert.Empty(withAddress);
    }

    [Fact]
    public void Read_Template_IsTheMailDeskFactoryWithOneSeatAndOneCheckedStart()
    {
        // Arrange
        var definition = Parsed();

        // Act
        var seat = Assert.Single(definition.Seats);
        var start = Assert.Single(seat.Starts);

        // Assert
        Assert.Equal(MailDeskTemplate.Factory, definition.Factory);
        Assert.Equal("Mail Desk", definition.Title);
        Assert.Equal("devlinux", definition.Defaults.Machine);
        Assert.Empty(definition.Repositories);
        Assert.Equal(MailDeskTemplate.Seat, seat.Seat);
        Assert.Equal("Mail Desk", seat.Name);
        Assert.Null(seat.Repository);
        Assert.Equal("5m", start.Every);
        Assert.Equal(300, start.EverySeconds);
        Assert.Equal(MailDeskTemplate.CheckPath, start.Check);
        Assert.True(start.PreventOverlap);
        Assert.Contains("{context}", start.Prompt);
    }

    [Fact]
    public void Read_SeatBrief_TeachesTheCommandsAndTheLimits()
    {
        // Arrange
        var brief = Assert.Single(Parsed().Seats).Brief;

        // Act + Assert: what it receives, the commands it uses, and what it never does.
        Assert.Contains("not for me", brief, StringComparison.OrdinalIgnoreCase);
        foreach (var command in new[]
                 {
                     "maildesk allow add", "maildesk block add", "maildesk rule add", "maildesk route add",
                     "maildesk done", "maildesk return", "maildesk status", "maildesk decisions", "maildesk rules history",
                 })
            Assert.Contains(command, brief);
        Assert.Contains("Never send a message", brief);
        Assert.Contains("Never delete a message", brief);
        Assert.Contains("Archive or Junk only", brief);
    }

    // ---------- the template version ----------

    [Fact]
    public void Version_SameFiles_IsTheSameEveryTime()
    {
        // Arrange
        var copy = new Dictionary<string, string>(MailDeskTemplate.Files.Reverse(), StringComparer.Ordinal);

        // Act
        var version = MailDeskTemplate.VersionOf(copy);

        // Assert
        Assert.Equal(MailDeskTemplate.Version, version);
        Assert.Equal(64, version.Length);
    }

    [Fact]
    public void VersionOf_OneCharacterChanged_IsAnotherVersion()
    {
        // Arrange
        var changed = new Dictionary<string, string>(MailDeskTemplate.Files, StringComparer.Ordinal);
        changed["factory.yaml"] = changed["factory.yaml"].Replace("title: Mail Desk", "title: Mail desk");

        // Act
        var version = MailDeskTemplate.VersionOf(changed);

        // Assert
        Assert.NotEqual(MailDeskTemplate.Version, version);
    }

    [Fact]
    public void VersionOf_FileMovedToAnotherPath_IsAnotherVersion()
    {
        // Arrange
        var moved = new Dictionary<string, string> { ["a.yaml"] = "x" };
        var original = new Dictionary<string, string> { ["b.yaml"] = "x" };

        // Act + Assert
        Assert.NotEqual(MailDeskTemplate.VersionOf(original), MailDeskTemplate.VersionOf(moved));
    }

    // ---------- the Gateway's own check, through the real routes ----------

    [Fact]
    public void Import_TemplateFolder_IsStoredAsVersionOneDraftWithNoErrors()
    {
        // Arrange
        var request = new ImportFactoryDefinitionRequest { Content = new Dictionary<string, string>(MailDeskTemplate.Files) };

        // Act
        var result = FactoryDefinitionEndpoints.Import(Person(), request, _store, InA, NoSession, _account, () => Now);

        // Assert
        Assert.True(Status(result) == 200, $"the import was refused: {Body(result)}");
        var dto = Assert.IsType<JsonHttpResult<FactoryDefinitionDto>>(result).Value!;
        Assert.Equal(1, dto.Version);
        Assert.Equal("draft", dto.State);
        Assert.DoesNotContain(dto.Findings, f => f.Severity == DefinitionSeverity.Error);
        Assert.Null(_store.Standing(A, MailDeskTemplate.Factory).Published);
    }

    [Fact]
    public void Create_TemplateFolder_IsStoredAsANewFactorysFirstDraft()
    {
        // Arrange
        var request = new ImportFactoryDefinitionRequest { Content = new Dictionary<string, string>(MailDeskTemplate.Files) };

        // Act
        var result = FactoryDefinitionEndpoints.Create(Person(), request, _store, InA, NoSession, _account, () => Now,
            (_, _) => null);

        // Assert
        Assert.True(Status(result) == 200, $"the create was refused: {Body(result)}");
        var dto = Assert.IsType<JsonHttpResult<FactoryDefinitionDto>>(result).Value!;
        Assert.Equal(1, dto.Version);
        Assert.Equal("draft", dto.State);
    }

    [Fact]
    public void Check_TemplateOnAnAccountWithoutDevlinux_GivesANoticeNotAnError()
    {
        // Arrange: another account receives the same template and has no Director on devlinux.
        var machines = new[] { "SOREN_NORTH" };

        // Act
        var check = FactoryDefinitionCheck.Check(MailDeskTemplate.Files, MailDeskTemplate.Factory,
            DefinitionFixture.FleetSkills, machines);

        // Assert
        Assert.False(check.HasErrors, string.Join(" | ", check.Findings.Select(f => $"{f.File}:{f.Line} {f.Message}")));
        Assert.Contains(check.Findings, f => !f.IsError && f.File == "factory.yaml" && f.Message.Contains("devlinux"));
    }
}
