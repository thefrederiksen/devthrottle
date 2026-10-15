using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Records;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Records;

/// <summary>
/// The Records tab of a factory's page (Factory Definition mission, phase 6): journals by seat, working documents with
/// every version, attachments. The fold's sentences, and the tab's routes over real record stores in memory (no port).
/// </summary>
public sealed class FactoryRecordsTabTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId T = TenantId.Local;
    private const string F = "qa-outreach";
    private const string Session = "75000000-0000-4000-8000-000000000001";

    private static RegisteredFactoryDto Outreach() => new()
    {
        Factory = F,
        Title = "QA Outreach",
        Folder = "/f/qa-outreach",
        Computer = "SOREN_NORTH",
        BossSeat = "boss",
        Seats = new List<RegisteredFactorySeatDto>
        {
            new() { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "seats/boss.yaml" },
            new() { Id = "writer", Name = "Writer", Role = "Writer", BriefFile = "seats/writer.yaml" },
        },
    };

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;
    private readonly FactoryJournalStore _journal;
    private readonly FactoryDocumentStore _documents;
    private readonly FactoryAttachmentStore _attachments;

    // The account the routes resolve for a request: this test's, unless a test asks as another account.
    private TenantId _asTenant = T;

    public FactoryRecordsTabTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        var registry = new FactoryRegistryStore(_db);
        registry.Register(T, new RegisterFactoryRequest
        {
            Factory = F, Title = "QA Outreach", Folder = "/f/qa-outreach", Computer = "SOREN_NORTH", BossSeat = "boss",
            Seats = new List<FactorySeatManifest>
            {
                new() { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "seats/boss.yaml" },
                new() { Id = "writer", Name = "Writer", Role = "Writer", BriefFile = "seats/writer.yaml" },
            },
        }, "test", Now);
        _attachments = new FactoryAttachmentStore(_db);
        _journal = new FactoryJournalStore(_db, _attachments);
        _documents = new FactoryDocumentStore(_db);
        var record = new FactoryActivityRecord(_db);
        var settings = new TenantSettingsStore(_db);
        var sources = new FactoriesScreenSources(
            new FactoryAgentsSources(
                Query: (tenant, q) => record.Query(tenant, q.Factory, q.Agent, q.Outcome, q.FromUtc, q.ToUtc, q.OldestFirst, q.Offset, q.Limit),
                Append: (tenant, request, actor) => record.Append(tenant, request, actor),
                Triggers: _ => Array.Empty<FactoryTriggerFacts>(),
                SetTriggerPaused: (_, _, _, _, _) => Task.FromResult(false),
                LiveSessionIds: _ => new HashSet<string>(),
                TimeZone: _ => TimeZoneInfo.Utc,
                NowUtc: () => Now,
                Reports: new FactoryReportStore(settings),
                Maps: new FactoryMapStore(settings)),
            registry,
            _ => Array.Empty<CronJobDto>(),
            (_, _, _) => new Dictionary<string, IReadOnlyList<CronRunRecord>>(),
            (_, f) => new CronRunProblemsDto { Factory = f },
            (_, f) => new FactoryEmailsDto { Factory = f });
        FactoryRecordsTabEndpoints.Map(_routes, _ => _asTenant, sources, _journal, _documents, _attachments, () => Now);
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    // ---------- the fold ----------

    [Fact]
    public void Tab_NothingYet_SaysSoInEachSection_InTheGatewaysWords()
    {
        var view = FactoryRecordsTabFold.Tab(Outreach(), Array.Empty<FactoryJournalGroup>(), Array.Empty<FactoryDocumentSummary>(),
            FactoryAttachmentPage.Empty, TimeZoneInfo.Utc, Now);

        Assert.Equal("The records of QA Outreach", view.Heading);
        Assert.Equal("No journal entry yet. A seat of QA Outreach adds one with cc-devthrottle factory journal add.", view.Journals.EmptyText);
        Assert.StartsWith("No working document yet.", view.Documents.EmptyText);
        Assert.StartsWith("No attachment yet.", view.Attachments.EmptyText);
        Assert.Empty(view.JournalGroups);
    }

    [Fact]
    public void Tab_JournalsAreTheFactorysOwnFirst_ThenSeatsInRegistryOrder_ThenSeatsNoLongerInIt()
    {
        var view = FactoryRecordsTabFold.Tab(Outreach(), new[]
            {
                new FactoryJournalGroup("scout", 2, "2026-10-01"),
                new FactoryJournalGroup("writer", 12, "2026-10-14"),
                new FactoryJournalGroup(null, 1, "2026-10-13"),
                new FactoryJournalGroup("boss", 3, "2026-10-15"),
            }, Array.Empty<FactoryDocumentSummary>(), FactoryAttachmentPage.Empty, TimeZoneInfo.Utc, Now);

        Assert.Equal(new[] { "The factory as a whole", "Boss (boss)", "Writer (writer)", "scout (no longer a seat of this factory)" },
            view.JournalGroups.Select(g => g.Title));
        Assert.Equal("12 entries, the newest of 2026-10-14.", view.JournalGroups[2].SummaryText);
        Assert.Equal("1 entry, the newest of 2026-10-13.", view.JournalGroups[0].SummaryText);
        Assert.Equal("4 journals, 18 entries in all.", view.Journals.SummaryText);
    }

    [Fact]
    public void Who_NamesASessionAPersonAndTheUpload_NeverCutShort()
    {
        Assert.Equal($"session {Session}", FactoryRecordsTabFold.Who("session", Session));
        Assert.Equal("a person (device browser abc)", FactoryRecordsTabFold.Who("person", "device browser abc"));
        Assert.Equal("the one-time upload from SOREN_NORTH", FactoryRecordsTabFold.Who("upload", "SOREN_NORTH"));
    }

    // ---------- through the routes ----------

    // ---------- paging (review of #4032, finding 1) ----------

    /// <summary>45 entries in the writer's journal: 25 written at ONE instant on one day, as an upload writes them,
    /// and 20 more over other days. The pages must hold each of them exactly once, in the page order.</summary>
    private List<Guid> SeedJournal()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
            ids.Add(_journal.Append(T, F, "writer", "2026-10-10", $"uploaded {i}", null, "upload", "SOREN_NORTH", Now.AddDays(-3), $"up:{i}").Entry!.Id);
        for (var i = 0; i < 20; i++)
            ids.Add(_journal.Append(T, F, "writer", $"2026-09-{10 + i:00}", $"day {i}", null, "session", Session, Now.AddDays(-30 + i)).Entry!.Id);
        return ids;
    }

    [Fact]
    public void JournalPage_WalkedToTheEnd_HoldsEveryEntryOnce_EvenTwentyFiveWrittenAtOneInstant()
    {
        var all = SeedJournal();

        var seen = new List<Guid>();
        Guid? before = null;
        var pages = 0;
        do
        {
            var page = _journal.Page(T, F, "writer", before, 20)!;
            Assert.Equal(45, page.Total);
            Assert.Equal(seen.Count, page.Newer);
            seen.AddRange(page.Entries.Select(e => e.Id));
            before = page.NextBefore;
            pages++;
        } while (before is not null);

        Assert.Equal(3, pages);
        Assert.Equal(45, seen.Distinct().Count());
        Assert.Equal(all.OrderBy(x => x), seen.OrderBy(x => x));
        // Newest day first: the 25 uploaded on 2026-10-10 come before every September day.
        var days = seen.Select(id => _journal.Get(T, F, id)!.EntryDate).ToList();
        Assert.Equal(days.OrderByDescending(d => d, StringComparer.Ordinal), days);
    }

    [Fact]
    public void AttachmentPage_WalkedToTheEnd_HoldsEveryFileOnce_EvenFilesKeptAtOneInstant()
    {
        var all = new List<Guid>();
        for (var i = 0; i < 7; i++)
            all.Add(_attachments.Add(T, F, null, $"same/f{i}.txt", "text/plain", new byte[] { 1 }, "upload", "SOREN_NORTH", Now, $"a:{i}").Attachment!.Id);
        for (var i = 0; i < 5; i++)
            all.Add(_attachments.Add(T, F, "writer", $"later/g{i}.txt", "text/plain", new byte[] { 2 }, "session", Session, Now.AddMinutes(i)).Attachment!.Id);

        var seen = new List<Guid>();
        Guid? before = null;
        do
        {
            var page = _attachments.Page(T, F, before, 4)!;
            Assert.Equal(seen.Count, page.Newer);
            seen.AddRange(page.Attachments.Select(a => a.Id));
            before = page.NextBefore;
        } while (before is not null);

        Assert.Equal(12, seen.Distinct().Count());
        Assert.Equal(all.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task THE_JOURNAL_ROUTE_pages_with_before_and_says_which_entries_it_shows()
    {
        SeedJournal();

        var first = await Get($"/gateway/factories/{F}/records/journal?seat=writer");
        Assert.Equal("45 entries. Entries 1 to 20 are shown, newest first.", first.Body.GetProperty("summaryText").GetString());
        Assert.Equal("Older entries", first.Body.GetProperty("olderLabel").GetString());
        Assert.Equal(JsonValueKind.Null, first.Body.GetProperty("newestLabel").ValueKind);

        var second = await Get($"/gateway/factories/{F}/records/journal?seat=writer&before={first.Body.GetProperty("nextBefore").GetGuid()}");
        Assert.Equal("45 entries. Entries 21 to 40 are shown, newest first.", second.Body.GetProperty("summaryText").GetString());
        Assert.Equal("Newest entries", second.Body.GetProperty("newestLabel").GetString());

        var last = await Get($"/gateway/factories/{F}/records/journal?seat=writer&before={second.Body.GetProperty("nextBefore").GetGuid()}");
        Assert.Equal("45 entries. Entries 41 to 45 are shown, newest first.", last.Body.GetProperty("summaryText").GetString());
        Assert.Equal(JsonValueKind.Null, last.Body.GetProperty("nextBefore").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.Body.GetProperty("olderLabel").ValueKind);
    }

    [Fact]
    public async Task THE_TAB_lists_the_first_page_of_files_and_the_attachments_route_the_next()
    {
        for (var i = 0; i < FactoryRecordsTabFold.AttachmentPage + 3; i++)
            _attachments.Add(T, F, null, $"shots/s{i:000}.png", "image/png", new byte[] { 1 }, "session", Session, Now.AddSeconds(i));

        var tab = await Get($"/gateway/factories/{F}/records");
        Assert.EndsWith(" Files 1 to 100 are listed, newest first.", tab.Body.GetProperty("attachments").GetProperty("summaryText").GetString());
        Assert.Equal("Older files", tab.Body.GetProperty("attachmentsOlderLabel").GetString());

        var next = await Get($"/gateway/factories/{F}/records/attachments?before={tab.Body.GetProperty("attachmentsNextBefore").GetGuid()}");
        Assert.Equal(200, next.Status);
        Assert.EndsWith(" Files 101 to 103 are listed, newest first.", next.Body.GetProperty("summaryText").GetString());
        Assert.Equal(new[] { "shots/s002.png", "shots/s001.png", "shots/s000.png" },
            next.Body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, next.Body.GetProperty("olderLabel").ValueKind);
        Assert.Equal("Newest files", next.Body.GetProperty("newestLabel").GetString());
    }

    [Theory]
    [InlineData("/records/journal?seat=writer&before=x", 400, "before is the id of the last row the page before showed, not 'x'")]
    [InlineData("/records/attachments?before=x", 400, "before is the id of the last row the page before showed, not 'x'")]
    [InlineData("/records/journal?seat=writer&before=00000000-0000-0000-0000-000000000009", 404,
        "00000000-0000-0000-0000-000000000009 is not an entry of this journal, so no page starts after it")]
    [InlineData("/records/attachments?before=00000000-0000-0000-0000-000000000009", 404,
        "00000000-0000-0000-0000-000000000009 is not an attachment of qa-outreach, so no page starts after it")]
    public async Task A_CURSOR_THAT_IS_NOT_AN_ID_OR_NOT_A_ROW_is_refused_in_words(string rest, int status, string sentence)
    {
        var answer = await Get($"/gateway/factories/{F}" + rest);

        Assert.Equal(status, answer.Status);
        Assert.Equal(sentence, answer.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task THE_TAB_lists_journals_by_seat_documents_as_they_stand_and_attachments_and_opens_each()
    {
        var picture = _attachments.Add(T, F, "writer", "shots/home.png", "image/png", new byte[] { 1, 2, 3 }, "session", Session, Now).Attachment!;
        Assert.True(_journal.Append(T, F, "writer", "2026-10-14", "Wrote the post.", new[] { picture.Id }, "session", Session, Now).Ok);
        Assert.True(_journal.Append(T, F, null, "2026-10-13", "The factory started.", null, "person", "device browser abc", Now).Ok);
        Assert.True(_documents.Write(T, F, "outreach/ledger.json", "row-a", 0, "session", Session, Now).Ok);
        Assert.True(_documents.Write(T, F, "outreach/ledger.json", "row-a\nrow-b", 1, "person", "device browser abc", Now).Ok);

        var tab = await Get($"/gateway/factories/{F}/records");
        Assert.Equal(200, tab.Status);
        Assert.Equal(new[] { "The factory as a whole", "Writer (writer)" },
            tab.Body.GetProperty("journalGroups").EnumerateArray().Select(g => g.GetProperty("title").GetString()));
        var doc = tab.Body.GetProperty("documentRows")[0];
        Assert.Equal(("outreach/ledger.json", "Version 2", "History"),
            (doc.GetProperty("name").GetString(), doc.GetProperty("versionText").GetString(), doc.GetProperty("historyLabel").GetString()));
        Assert.Equal($"/gateway/factories/{F}/records/attachments/{picture.Id}",
            tab.Body.GetProperty("attachmentRows")[0].GetProperty("downloadHref").GetString());

        var journal = await Get($"/gateway/factories/{F}/records/journal?seat=writer");
        Assert.Equal("The journal of Writer (writer)", journal.Body.GetProperty("heading").GetString());
        var entry = journal.Body.GetProperty("entries")[0];
        Assert.Equal("Wrote the post.", entry.GetProperty("text").GetString());
        Assert.Equal("shots/home.png", entry.GetProperty("attachments")[0].GetProperty("name").GetString());

        var own = await Get($"/gateway/factories/{F}/records/journal");
        Assert.Equal("The factory started.", own.Body.GetProperty("entries")[0].GetProperty("text").GetString());

        var older = await Get($"/gateway/factories/{F}/records/documents/read?name=outreach/ledger.json&version=1");
        Assert.Equal("row-a", older.Body.GetProperty("text").GetString());
        Assert.StartsWith("An earlier version; the newest is version 2.", older.Body.GetProperty("lineText").GetString());

        var history = await Get($"/gateway/factories/{F}/records/documents/history?name=outreach/ledger.json");
        Assert.Equal(new[] { "Newest", "Earlier" },
            history.Body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("stateWord").GetString()));

        var file = await Send($"/gateway/factories/{F}/records/attachments/{picture.Id}");
        Assert.Equal((200, "image/png"), (file.Status, file.ContentType));
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Bytes);
        // Always a download, never rendered in this origin (review of #4032, finding 2), and named with its folder.
        Assert.StartsWith("attachment;", file.Disposition);
        Assert.Contains("filename=shots-home.png", file.Disposition);
        Assert.Equal("nosniff", file.NoSniff);
    }

    [Fact]
    public async Task A_DOCUMENT_THAT_IS_NOT_THERE_and_a_version_that_is_not_answer_404_in_words()
    {
        Assert.True(_documents.Write(T, F, "focus.md", "x", 0, "session", Session, Now).Ok);

        var none = await Get($"/gateway/factories/{F}/records/documents/read?name=nope.md");
        Assert.Equal(404, none.Status);
        Assert.Equal("qa-outreach has no working document 'nope.md'", none.Body.GetProperty("error").GetString());
        var noVersion = await Get($"/gateway/factories/{F}/records/documents/read?name=focus.md&version=9");
        Assert.Equal("'focus.md' has no version 9; its newest is version 1", noVersion.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AN_HTML_ATTACHMENT_is_a_download_with_nosniff_never_a_page_in_the_gateways_origin()
    {
        var page = _attachments.Add(T, F, null, "notes/page.html", "text/html", Encoding.UTF8.GetBytes("<script>1</script>"), "session", Session, Now).Attachment!;

        var file = await Send($"/gateway/factories/{F}/records/attachments/{page.Id}");

        Assert.Equal((200, "text/html"), (file.Status, file.ContentType));
        Assert.StartsWith("attachment;", file.Disposition);
        Assert.Equal("nosniff", file.NoSniff);
    }

    [Theory]
    [InlineData("/records")]
    [InlineData("/records/journal?seat=writer")]
    [InlineData("/records/documents/read?name=focus.md")]
    [InlineData("/records/documents/history?name=focus.md")]
    [InlineData("/records/attachments/ATTACHMENT")]
    public async Task A_CALLING_SESSION_is_refused_403_by_every_route_of_the_tab_itself(string rest)
    {
        var file = _attachments.Add(T, F, null, "a.txt", "text/plain", new byte[] { 1 }, "session", Session, Now).Attachment!;
        Assert.True(_documents.Write(T, F, "focus.md", "x", 0, "session", Session, Now).Ok);

        var answer = await Send($"/gateway/factories/{F}" + rest.Replace("ATTACHMENT", file.Id.ToString()), asSession: true);

        Assert.Equal(403, answer.Status);
    }

    [Theory]
    [InlineData("/records")]
    [InlineData("/records/journal")]
    [InlineData("/records/documents/read?name=focus.md")]
    [InlineData("/records/attachments/ATTACHMENT")]
    public async Task ANOTHER_ACCOUNT_finds_no_such_factory_and_none_of_its_records(string rest)
    {
        var file = _attachments.Add(T, F, null, "a.txt", "text/plain", new byte[] { 1 }, "session", Session, Now).Attachment!;
        Assert.True(_documents.Write(T, F, "focus.md", "x", 0, "session", Session, Now).Ok);
        _asTenant = new TenantId("another-account");

        var answer = await Get($"/gateway/factories/{F}" + rest.Replace("ATTACHMENT", file.Id.ToString()));

        Assert.Equal(404, answer.Status);
        Assert.Equal("There is no factory 'qa-outreach' in this account.", answer.Body.GetProperty("error").GetString());
    }

    [Fact]
    public void Who_AnAuthorKindTheStoreNeverWrites_IsABrokenRow_NeverNamedAsTheUpload()
    {
        Assert.Throws<InvalidOperationException>(() => FactoryRecordsTabFold.Who("robot", "x"));
    }

    [Fact]
    public void THE_TABS_ROUTES_ARE_THE_OWNERS_a_session_key_is_refused_by_the_guard()
    {
        Assert.False(SessionKeyGuard.Check("GET", $"/gateway/factories/{F}/records").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", $"/gateway/factories/{F}/records/journal").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", $"/gateway/factories/{F}/records/attachments/{Guid.NewGuid()}").Allowed);
    }

    // ---------- the in-memory pipeline ----------

    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private async Task<(int Status, string ContentType, byte[] Bytes, string Disposition, string NoSniff)> Send(string path, bool asSession = false)
    {
        var query = path.Contains('?') ? path[path.IndexOf('?')..] : "";
        if (query.Length > 0) path = path[..path.IndexOf('?')];
        (RouteEndpoint Endpoint, RouteValueDictionary Values)? matched = null;
        foreach (var endpoint in _routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
                     .OrderBy(e => e.RoutePattern.Parameters.Count))
        {
            var values = new RouteValueDictionary();
            var template = endpoint.RoutePattern.RawText!.Replace("{id:guid}", "{id}", StringComparison.Ordinal);
            if (new TemplateMatcher(TemplateParser.Parse(template), new RouteValueDictionary()).TryMatch(path, values))
            {
                matched = (endpoint, values);
                break;
            }
        }
        Assert.True(matched is not null, $"no mapped route matches GET {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        ctx.Request.RouteValues = matched!.Value.Values;
        ctx.Response.Body = new MemoryStream();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-1", T.Value, "browser", "active");
        if (asSession)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.Parse(Session), T, "director-a");

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        return (ctx.Response.StatusCode, (ctx.Response.ContentType ?? "").Split(';')[0], ((MemoryStream)ctx.Response.Body).ToArray(),
            ctx.Response.Headers.ContentDisposition.ToString(), ctx.Response.Headers["X-Content-Type-Options"].ToString());
    }

    private async Task<(int Status, JsonElement Body)> Get(string path)
    {
        var answer = await Send(path);
        using var document = JsonDocument.Parse(answer.Bytes);
        return (answer.Status, document.RootElement.Clone());
    }
}
