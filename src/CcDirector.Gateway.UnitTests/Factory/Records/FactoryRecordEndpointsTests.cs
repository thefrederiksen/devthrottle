using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Records;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Records;

/// <summary>
/// A FACTORY'S RECORDS, THROUGH THEIR ROUTES (Factory Definition mission, phase 2), driven through the route handlers
/// over the real stores and a real database. These pin what only the route knows: WHO IS ASKING, and the status and
/// words a refusal carries. A person and a session of the factory read and write; a session of another factory reaches
/// nothing; the machine token writes only the rows the one-time upload names.
///
/// A refusal is proven by the answer that carries it - its status and its sentence - never by the absence of an error.
/// </summary>
[Trait("Category", "FactoryRecords")]
public sealed class FactoryRecordEndpointsTests : IDisposable
{
    private const string TheFactory = "devthrottle";
    private const string FactorySession = "7b000000-0000-4000-8000-000000000001";
    private const string SecondFactorySession = "7b000000-0000-4000-8000-000000000002";
    private const string OtherFactorySession = "7b000000-0000-4000-8000-000000000003";
    private const string NoFactorySession = "7b000000-0000-4000-8000-000000000004";
    private const string Ledger = "outreach/ledger.json";
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryJournalStore _journal;
    private readonly FactoryDocumentStore _documents;
    private readonly FactoryAttachmentStore _attachments;

    public FactoryRecordEndpointsTests()
    {
        var db = _h.Open();
        _attachments = new FactoryAttachmentStore(db);
        _journal = new FactoryJournalStore(db, _attachments);
        _documents = new FactoryDocumentStore(db);
    }

    public void Dispose() => _h.Dispose();

    // ---------- who is asking ----------

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

    /// <summary>The account's shared machine token: a verified credential that is neither a person's device nor a
    /// session's key. The upload names the machine it runs on; <paramref name="machine"/> null leaves that out.</summary>
    private static DefaultHttpContext Machine(string? machine = "SOREN_NORTH")
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
        if (machine is not null) ctx.Request.Headers[FactoryRecordEndpoints.UploadMachineHeader] = machine;
        return ctx;
    }

    private static SessionFactoryLookup FactoryOf(string session) => session switch
    {
        FactorySession or SecondFactorySession => SessionFactoryLookup.In(TheFactory),
        OtherFactorySession => SessionFactoryLookup.In("warmforward"),
        NoFactorySession => SessionFactoryLookup.InNoFactory,
        _ => SessionFactoryLookup.NotKnown,
    };

    private static TenantId? InA(HttpContext _) => A;

    // ---------- the routes ----------

    private IResult WriteDoc(HttpContext ctx, string text, int expect, string? importKey = null) =>
        FactoryRecordEndpoints.WriteDocument(ctx, TheFactory,
            new WriteFactoryDocumentRequest { Name = Ledger, Text = text, ExpectedVersion = expect, ImportKey = importKey },
            _documents, InA, FactoryOf, () => Now);

    private IResult ReadDoc(HttpContext ctx, string query)
    {
        ctx.Request.QueryString = new QueryString("?" + query);
        return FactoryRecordEndpoints.ReadDocument(ctx, TheFactory, _documents, InA, FactoryOf);
    }

    private IResult AddFile(HttpContext ctx, byte[] content, string name = "walk/01-home.png", string? importKey = null) =>
        FactoryRecordEndpoints.AddAttachment(ctx, TheFactory,
            new AddFactoryAttachmentRequest
            {
                Seat = "onboarding", Name = name, ContentType = "image/png",
                ContentBase64 = Convert.ToBase64String(content), ImportKey = importKey,
            },
            _attachments, InA, FactoryOf, () => Now);

    private IResult AddEntry(HttpContext ctx, string text, List<Guid>? attachments = null, string? importKey = null) =>
        FactoryRecordEndpoints.AddJournal(ctx, TheFactory,
            new AddFactoryJournalEntryRequest { Seat = "outreach", Date = "2026-10-14", Text = text, AttachmentIds = attachments, ImportKey = importKey },
            _journal, InA, FactoryOf, () => Now);

    // ---------- reading an answer ----------

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static FactoryRecordWriteResponse Wrote(IResult result) =>
        Assert.IsType<JsonHttpResult<FactoryRecordWriteResponse>>(result).Value!;

    private static object? Field(IResult result, string name)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        var property = value.GetType().GetProperty(name);
        Assert.True(property is not null, $"the answer has no '{name}'");
        return property!.GetValue(value);
    }

    private static string ErrorOf(IResult result) => (string)Field(result, "error")!;

    // ---------- the ledger, through the route ----------

    [Fact]
    public void TWO_SESSIONS_WRITING_THE_LEDGER_THE_SECOND_IS_REFUSED_409_WITH_THE_CURRENT_TEXT()
    {
        Assert.Equal("Written", Wrote(WriteDoc(Session(FactorySession), "row-a", expect: 0)).Outcome);
        Assert.Equal("Written", Wrote(WriteDoc(Session(FactorySession), "row-a\nrow-b", expect: 1)).Outcome);

        var refused = WriteDoc(Session(SecondFactorySession), "row-a\nrow-c", expect: 1);

        Assert.Equal(409, Status(refused));
        Assert.Equal("'outreach/ledger.json' is at version 2, not 1; read it again, merge, and write once more", ErrorOf(refused));
        Assert.Equal("Stale", Field(refused, "outcome"));
        var current = Assert.IsType<FactoryDocumentDto>(Field(refused, "current"));
        Assert.Equal(2, current.Version);
        Assert.Equal("row-a\nrow-b", current.Text);
        Assert.Equal(FactorySession, current.AuthorId);
    }

    [Fact]
    public void A_written_document_is_read_back_newest_or_by_version_and_a_write_answer_carries_no_text()
    {
        var written = Wrote(WriteDoc(Person(), "v1", expect: 0));
        WriteDoc(Person(), "v2", expect: 1);

        Assert.Null(written.Document!.Text);
        Assert.Equal("person", written.Document.AuthorKind);
        Assert.Equal("v2", Assert.IsType<JsonHttpResult<FactoryDocumentDto>>(ReadDoc(Person(), "name=outreach/ledger.json")).Value!.Text);
        Assert.Equal("v1", Assert.IsType<JsonHttpResult<FactoryDocumentDto>>(ReadDoc(Session(FactorySession), "name=outreach/ledger.json&version=1")).Value!.Text);
        Assert.Equal(404, Status(ReadDoc(Person(), "name=outreach/ledger.json&version=9")));
        Assert.Equal(404, Status(ReadDoc(Person(), "name=nothing.md")));
        Assert.Equal(400, Status(ReadDoc(Person(), "name=../x")));
    }

    // ---------- who may read and write ----------

    [Fact]
    public void A_session_of_another_factory_reaches_none_of_these_records()
    {
        WriteDoc(Person(), "v1", expect: 0);

        var read = ReadDoc(Session(OtherFactorySession), "name=outreach/ledger.json");
        var write = WriteDoc(Session(OtherFactorySession), "v2", expect: 1);
        var noFactory = AddEntry(Session(NoFactorySession), "a day");

        Assert.Equal(403, Status(read));
        Assert.Equal(FactoryNaming.ActRefusal("record", TheFactory), ErrorOf(read));
        Assert.Equal(403, Status(write));
        Assert.Equal(403, Status(noFactory));
        Assert.Equal("v1", _documents.Get(A, TheFactory, Ledger)!.Text);
    }

    [Fact]
    public void The_machine_token_writes_nothing_of_its_own_and_only_the_rows_the_upload_names()
    {
        var plain = WriteDoc(Machine(), "v1", expect: 0);
        var upload = WriteDoc(Machine(), "v1", expect: 0, importKey: "devthrottle/outreach/ledger.json");

        Assert.Equal(403, Status(plain));
        Assert.Equal("a factory's records are written by a person, by a session of the factory, or by the one-time upload", ErrorOf(plain));
        var stored = Wrote(upload);
        Assert.Equal("Written", stored.Outcome);
        Assert.Equal("upload", stored.Document!.AuthorKind);
        Assert.Equal("SOREN_NORTH", stored.Document.AuthorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SOREN NORTH")]
    [InlineData("a/b")]
    public void The_upload_that_names_no_machine_is_refused_and_stores_nothing(string? machine)
    {
        var refused = WriteDoc(Machine(machine), "v1", expect: 0, importKey: "devthrottle/outreach/ledger.json");

        Assert.Equal(400, Status(refused));
        Assert.Equal("the one-time upload names the machine it runs on in the X-DevThrottle-Upload-Machine header, and this request did not",
            ErrorOf(refused));
        Assert.Null(_documents.Get(A, TheFactory, Ledger));
    }

    [Fact]
    public void The_machine_token_reads_no_record_on_any_of_the_read_routes()
    {
        WriteDoc(Person(), "v1", expect: 0);
        var entry = Wrote(AddEntry(Person(), "a day")).Entry!.Id;
        var file = Wrote(AddFile(Person(), new byte[] { 1 })).Attachment!.Id;

        var refusals = new[]
        {
            ReadDoc(Machine(), "name=outreach/ledger.json"),
            FactoryRecordEndpoints.ListDocuments(Machine(), TheFactory, _documents, InA, FactoryOf),
            ReadDocHistory(Machine()),
            FactoryRecordEndpoints.ListJournal(Machine(), TheFactory, _journal, InA, FactoryOf),
            FactoryRecordEndpoints.ReadJournal(Machine(), TheFactory, entry.ToString(), _journal, InA, FactoryOf),
            FactoryRecordEndpoints.ListAttachments(Machine(), TheFactory, _attachments, InA, FactoryOf),
            FactoryRecordEndpoints.ReadAttachment(Machine(), TheFactory, file.ToString(), _attachments, InA, FactoryOf),
        };

        Assert.All(refusals, r =>
        {
            Assert.Equal(403, Status(r));
            Assert.Equal("a factory's records are read by a person, or by a session of the factory", ErrorOf(r));
        });

        // NOR THROUGH A REFUSED WRITE. A stale write is answered with the record in the way so its writer can merge;
        // the upload naming a document a person wrote must not be handed that document's text by the refusal.
        var stale = WriteDoc(Machine(), "anything", expect: 0, importKey: "devthrottle/outreach/ledger.json");
        Assert.Equal(409, Status(stale));
        Assert.Equal("Stale", Field(stale, "outcome"));
        Assert.Equal("'outreach/ledger.json' is at version 1, not 0; read it again, merge, and write once more", ErrorOf(stale));
        Assert.Null(Field(stale, "current"));
        Assert.Equal(1, _documents.History(A, TheFactory, Ledger).Total);
    }

    [Fact]
    public void An_upload_whose_file_changed_is_refused_without_the_row_it_stored()
    {
        Wrote(AddEntry(Machine(), "the day as uploaded", importKey: "devthrottle/journal/2026-10-01.md"));

        var changed = AddEntry(Machine(), "the day, grown since", importKey: "devthrottle/journal/2026-10-01.md");

        Assert.Equal(409, Status(changed));
        Assert.Equal("ImportConflict", Field(changed, "outcome"));
        Assert.Null(Field(changed, "current"));
    }

    private IResult ReadDocHistory(HttpContext ctx)
    {
        ctx.Request.QueryString = new QueryString("?name=outreach/ledger.json");
        return FactoryRecordEndpoints.DocumentHistory(ctx, TheFactory, _documents, InA, FactoryOf);
    }

    [Fact]
    public void A_person_or_a_session_that_names_a_row_as_the_upload_does_is_refused()
    {
        var person = WriteDoc(Person(), "v1", expect: 0, importKey: "devthrottle/outreach/ledger.json");
        var session = AddEntry(Session(FactorySession), "a day", importKey: "devthrottle/outreach/journal/2026-10-14.md");

        Assert.Equal(400, Status(person));
        Assert.Equal("only the one-time upload names the rows it stores; leave importKey out", ErrorOf(person));
        Assert.Equal(400, Status(session));
        Assert.Null(_documents.Get(A, TheFactory, Ledger));
    }

    [Fact]
    public void The_upload_sent_twice_is_answered_already_stored_the_second_time()
    {
        var first = Wrote(AddFile(Machine(), new byte[] { 1, 2, 3 }, importKey: "devthrottle/onboarding/a.png"));
        var again = Wrote(AddFile(Machine(), new byte[] { 1, 2, 3 }, importKey: "devthrottle/onboarding/a.png"));

        Assert.Equal("Written", first.Outcome);
        Assert.Equal("AlreadyStored", again.Outcome);
        Assert.Equal(first.Attachment!.Id, again.Attachment!.Id);
    }

    // ---------- attachments ----------

    [Fact]
    public void AN_OVERSIZED_ATTACHMENT_IS_REFUSED_413_IN_WORDS_AND_NOTHING_IS_KEPT()
    {
        var big = new byte[FactoryAttachmentStore.MaxAttachmentBytes + 1];

        var refused = AddFile(Session(FactorySession), big);

        Assert.Equal(413, Status(refused));
        Assert.Equal("TooLarge", Field(refused, "outcome"));
        Assert.Equal($"'walk/01-home.png' holds {FactoryAttachmentStore.MaxAttachmentBytes + 1} bytes; one attachment holds at " +
                     $"most {FactoryAttachmentStore.MaxAttachmentBytes} ({FactoryAttachmentStore.MaxAttachmentBytes / (1024 * 1024)} MB). Nothing was stored. Make the file smaller - a " +
                     "screenshot at a lower resolution, or as a JPEG - and add it again", ErrorOf(refused));
        Assert.Equal(0, _attachments.List(A, TheFactory).Total);
    }

    [Fact]
    public void A_kept_file_is_read_back_with_its_bytes_and_the_listing_carries_none()
    {
        var bytes = new byte[] { 137, 80, 78, 71 };
        var id = Wrote(AddFile(Session(FactorySession), bytes)).Attachment!.Id;

        var one = Assert.IsType<JsonHttpResult<FactoryAttachmentDto>>(
            FactoryRecordEndpoints.ReadAttachment(Person(), TheFactory, id.ToString(), _attachments, InA, FactoryOf)).Value!;
        var list = Assert.IsType<JsonHttpResult<FactoryAttachmentListResponse>>(
            FactoryRecordEndpoints.ListAttachments(Person(), TheFactory, _attachments, InA, FactoryOf)).Value!;

        Assert.Equal(bytes, Convert.FromBase64String(one.ContentBase64!));
        Assert.Null(list.Attachments.Single().ContentBase64);
        Assert.Equal(4, list.FactoryBytes);
        Assert.Equal(FactoryAttachmentStore.MaxFactoryBytes, list.MaxFactoryBytes);
    }

    [Fact]
    public void Bytes_that_are_not_base64_are_refused_rather_than_kept_as_nothing()
    {
        var refused = FactoryRecordEndpoints.AddAttachment(Person(), TheFactory,
            new AddFactoryAttachmentRequest { Name = "a.png", ContentType = "image/png", ContentBase64 = "not base64!" },
            _attachments, InA, FactoryOf, () => Now);
        var absent = FactoryRecordEndpoints.AddAttachment(Person(), TheFactory,
            new AddFactoryAttachmentRequest { Name = "a.png", ContentType = "image/png" },
            _attachments, InA, FactoryOf, () => Now);

        Assert.Equal(400, Status(refused));
        Assert.Equal(400, Status(absent));
        Assert.Equal(0, _attachments.List(A, TheFactory).Total);
    }

    // ---------- the journal ----------

    [Fact]
    public void An_entry_naming_a_kept_file_is_added_and_the_listing_carries_no_text()
    {
        var file = Wrote(AddFile(Session(FactorySession), new byte[] { 1 })).Attachment!.Id;

        var added = Wrote(AddEntry(Session(FactorySession), "Walked onboarding.", new List<Guid> { file }));
        var ctx = Person();
        ctx.Request.QueryString = new QueryString("?seat=outreach");
        var list = Assert.IsType<JsonHttpResult<FactoryJournalListResponse>>(
            FactoryRecordEndpoints.ListJournal(ctx, TheFactory, _journal, InA, FactoryOf)).Value!;
        var one = Assert.IsType<JsonHttpResult<FactoryJournalEntryDto>>(
            FactoryRecordEndpoints.ReadJournal(Person(), TheFactory, added.Entry!.Id.ToString(), _journal, InA, FactoryOf)).Value!;

        Assert.Equal(new[] { file }, added.Entry.AttachmentIds);
        Assert.Null(list.Entries.Single().Text);
        Assert.Equal(new[] { file }, list.Entries.Single().AttachmentIds);
        Assert.Equal("Walked onboarding.", one.Text);
    }

    [Theory]
    [InlineData("since=14-10-2026")]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("seat=outreach&factoryOnly=true")]
    public void A_journal_listing_asked_for_something_it_cannot_answer_is_refused_rather_than_answered_otherwise(string query)
    {
        var ctx = Person();
        ctx.Request.QueryString = new QueryString("?" + query);

        Assert.Equal(400, Status(FactoryRecordEndpoints.ListJournal(ctx, TheFactory, _journal, InA, FactoryOf)));
    }

    [Fact]
    public void A_factory_id_that_is_not_one_spelling_is_refused_400_before_anything_is_read()
    {
        var result = FactoryRecordEndpoints.ListDocuments(Person(), "Not A Factory", _documents, InA, FactoryOf);

        Assert.Equal(400, Status(result));
    }
}
