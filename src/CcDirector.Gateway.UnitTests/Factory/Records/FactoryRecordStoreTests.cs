using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Records;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Records;

/// <summary>
/// A FACTORY'S RECORDS ON THE GATEWAY: the three stores' contract (Factory Definition mission, phase 2, section 5.6).
/// Runs over the real Entity Framework store on a throwaway SQLite file, exactly as the Gateway runs it locally.
///
/// The case that matters most is the ledger pair - <see cref="TWO_RUNS_WRITING_THE_LEDGER_ONE_IS_REFUSED_WITH_THE_CURRENT_TEXT_AND_NOTHING_IS_LOST"/>
/// and <see cref="THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_DOCUMENT_VERSION"/>. A store that simply
/// overwrote would pass every other test here and delete one run's ledger rows while looking healthy.
/// </summary>
[Trait("Category", "FactoryRecords")]
public sealed class FactoryRecordStoreTests : IDisposable
{
    private const string TheFactory = "devthrottle";
    private const string Other = "warmforward";
    private const string Outreach = "outreach";
    private const string RunOne = "7a000000-0000-4000-8000-000000000001";
    private const string RunTwo = "7a000000-0000-4000-8000-000000000002";
    private const string Ledger = "outreach/ledger.json";
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private FactoryDocumentStore Documents() => new(_h.Open());
    private FactoryAttachmentStore Attachments() => new(_h.Open());
    private FactoryJournalStore Journal() => new(_h.Open(), Attachments());

    private static FactoryDocumentWrite WriteBySession(FactoryDocumentStore store, string text, int expect,
        string session = RunOne, string name = Ledger, TenantId? tenant = null)
        => store.Write(tenant ?? A, TheFactory, name, text, expect, FactoryRecordAuthorKinds.Session, session, Now);

    // ---------- working documents ----------

    [Fact]
    public void A_document_written_by_one_run_is_read_back_by_the_next_with_who_wrote_it()
    {
        var write = WriteBySession(Documents(), "[{\"to\":\"a@example.com\"}]", expect: 0);

        Assert.Equal(FactoryRecordOutcome.Written, write.Outcome);
        Assert.Equal(1, write.Document!.Version);
        var read = Documents().Get(A, TheFactory, Ledger)!;
        Assert.Equal("[{\"to\":\"a@example.com\"}]", read.Text);
        Assert.Equal(FactoryRecordAuthorKinds.Session, read.AuthorKind);
        Assert.Equal(RunOne, read.AuthorId);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(read.Text), read.TextBytes);
    }

    [Fact]
    public void NewestUnder_hands_the_newest_version_of_each_document_under_the_prefix_with_its_text_and_nothing_else()
    {
        var store = Documents();
        WriteBySession(store, "chain v1", expect: 0, name: "check-inputs/chain.json");
        WriteBySession(store, "chain v2", expect: 1, name: "check-inputs/chain.json");
        WriteBySession(store, "2026-10-14", expect: 0, name: "check-inputs/boss/last-review.txt");
        WriteBySession(store, "not an input", expect: 0, name: "check-inputs-old/chain.json");
        WriteBySession(store, "the ledger", expect: 0);
        WriteBySession(store, "another account's input", expect: 0, name: "check-inputs/hold.txt", tenant: B);
        Assert.True(store.Write(A, Other, "check-inputs/hold.txt", "another factory's input", 0,
            FactoryRecordAuthorKinds.Session, RunOne, Now).Ok);

        var under = Documents().NewestUnder(A, TheFactory, "check-inputs/");

        Assert.Equal(new[] { ("check-inputs/boss/last-review.txt", 1, "2026-10-14"), ("check-inputs/chain.json", 2, "chain v2") },
            under.Select(d => (d.Name, d.Version, d.Text)));
    }

    [Fact]
    public void NewestUnder_a_factory_with_nothing_under_the_prefix_is_empty()
    {
        WriteBySession(Documents(), "the ledger", expect: 0);

        Assert.Empty(Documents().NewestUnder(A, TheFactory, "check-inputs/"));
    }

    [Fact]
    public void Only_the_newest_three_versions_are_kept_and_the_numbers_still_climb()
    {
        var store = Documents();
        for (var v = 1; v <= 5; v++)
            Assert.True(WriteBySession(store, $"v{v}", expect: v - 1).Ok);

        var page = store.History(A, TheFactory, Ledger);
        Assert.Equal(3, page.Total);
        Assert.Equal(new[] { 5, 4, 3 }, page.Versions.Select(v => v.Version));
        Assert.Null(store.Find(A, TheFactory, Ledger, 2));
        Assert.Equal("v3", store.Find(A, TheFactory, Ledger, 3)!.Text);
        // The next writer still states the newest version it read; a stale one is still refused.
        Assert.Equal(FactoryRecordOutcome.Stale, WriteBySession(store, "late", expect: 4).Outcome);
        Assert.Equal(6, WriteBySession(store, "v6", expect: 5).Document!.Version);
        Assert.Equal(new[] { 6, 5, 4 }, store.History(A, TheFactory, Ledger).Versions.Select(v => v.Version));
    }

    [Fact]
    public void The_version_the_upload_stored_is_kept_so_a_re_run_of_the_upload_is_already_stored()
    {
        var store = Documents();
        Assert.True(store.Write(A, TheFactory, Ledger, "from the machine", 0, FactoryRecordAuthorKinds.Upload, "SOREN_NORTH",
            Now, importKey: "outreach/ledger.json").Ok);
        for (var v = 2; v <= 5; v++) Assert.True(WriteBySession(store, $"v{v}", expect: v - 1).Ok);

        Assert.Equal(new[] { 5, 4, 3, 1 }, store.History(A, TheFactory, Ledger).Versions.Select(v => v.Version));
        var again = store.Write(A, TheFactory, Ledger, "from the machine", 0, FactoryRecordAuthorKinds.Upload, "SOREN_NORTH",
            Now, importKey: "outreach/ledger.json");
        Assert.Equal(FactoryRecordOutcome.AlreadyStored, again.Outcome);
    }

    [Fact]
    public void Trimming_one_document_leaves_every_other_document_factory_and_account_alone()
    {
        var store = Documents();
        WriteBySession(store, "other", expect: 0, name: "playbook.md");
        WriteBySession(store, "theirs", expect: 0, tenant: B);
        for (var v = 1; v <= 5; v++) WriteBySession(store, $"v{v}", expect: v - 1);

        Assert.Equal(1, store.History(A, TheFactory, "playbook.md").Total);
        Assert.Equal("theirs", store.Get(B, TheFactory, Ledger)!.Text);
        Assert.Equal(1, store.History(B, TheFactory, Ledger).Total);
    }

    [Fact]
    public void A_second_write_adds_a_version_and_every_version_is_kept()
    {
        var store = Documents();
        WriteBySession(store, "v1", expect: 0);
        WriteBySession(store, "v2", expect: 1);
        WriteBySession(store, "v3", expect: 2);

        Assert.Equal("v1", store.Find(A, TheFactory, Ledger, 1)!.Text);
        Assert.Equal("v3", store.Get(A, TheFactory, Ledger)!.Text);
        var page = store.History(A, TheFactory, Ledger);
        Assert.Equal(3, page.Total);
        Assert.Equal(new[] { 3, 2, 1 }, page.Versions.Select(v => v.Version));
    }

    [Fact]
    public void TWO_RUNS_WRITING_THE_LEDGER_ONE_IS_REFUSED_WITH_THE_CURRENT_TEXT_AND_NOTHING_IS_LOST()
    {
        // Two runs of the Outreach seat both read version 1 of the ledger, each adds the row it sent. The first is
        // stored. The second is refused and handed the CURRENT text - the first run's row included - so it can merge.
        var store = Documents();
        WriteBySession(store, "row-a", expect: 0);

        var first = WriteBySession(store, "row-a\nrow-b (run one)", expect: 1, session: RunOne);
        var second = WriteBySession(store, "row-a\nrow-c (run two)", expect: 1, session: RunTwo);

        Assert.Equal(FactoryRecordOutcome.Written, first.Outcome);
        Assert.Equal(FactoryRecordOutcome.Stale, second.Outcome);
        Assert.False(second.Ok);
        Assert.Equal("'outreach/ledger.json' is at version 2, not 1; read it again, merge, and write once more", second.Refusal);
        Assert.Equal(2, second.Document!.Version);
        Assert.Equal("row-a\nrow-b (run one)", second.Document.Text);

        // Run two merges its row into what it was handed and writes again, stating the version it now read.
        var merged = WriteBySession(store, second.Document.Text + "\nrow-c (run two)", expect: 2, session: RunTwo);
        Assert.Equal(FactoryRecordOutcome.Written, merged.Outcome);
        Assert.Equal("row-a\nrow-b (run one)\nrow-c (run two)", store.Get(A, TheFactory, Ledger)!.Text);
        Assert.Equal(3, store.History(A, TheFactory, Ledger).Total);
    }

    [Fact]
    public void Two_store_instances_over_one_database_are_held_to_one_order_the_way_two_replicas_are()
    {
        // The hosted Gateway runs several replicas over one database: a lock inside one store cannot be what keeps
        // them apart. Two stores here share nothing but the file.
        var replicaOne = Documents();
        var replicaTwo = Documents();
        WriteBySession(replicaOne, "v1", expect: 0);

        Assert.Equal(FactoryRecordOutcome.Written, WriteBySession(replicaOne, "v2 from one", expect: 1).Outcome);
        var late = WriteBySession(replicaTwo, "v2 from two", expect: 1);

        Assert.Equal(FactoryRecordOutcome.Stale, late.Outcome);
        Assert.Equal("v2 from one", late.Document!.Text);
    }

    [Fact]
    public void THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_DOCUMENT_VERSION()
    {
        WriteBySession(Documents(), "v1", expect: 0);
        using var ctx = _h.Open().CreateContext(A);
        ctx.FactoryDocumentVersions.Add(new FactoryDocumentVersionEntity
        {
            TenantId = A.Value, Factory = TheFactory, Name = Ledger, Version = 1, Text = "a second version 1",
            AuthorKind = FactoryRecordAuthorKinds.Session, AuthorId = RunTwo, WrittenAtUtc = Now,
        });

        Assert.ThrowsAny<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void A_new_document_written_expecting_a_version_is_refused_and_told_to_expect_zero()
    {
        var write = WriteBySession(Documents(), "text", expect: 3);

        Assert.Equal(FactoryRecordOutcome.Stale, write.Outcome);
        Assert.Equal("'outreach/ledger.json' does not exist yet, so write it expecting version 0", write.Refusal);
        Assert.Null(Documents().Get(A, TheFactory, Ledger));
    }

    [Fact]
    public void A_document_over_its_cap_is_refused_and_nothing_is_stored()
    {
        var text = new string('x', FactoryDocumentStore.MaxDocumentBytes + 1);

        var write = WriteBySession(Documents(), text, expect: 0);

        Assert.Equal(FactoryRecordOutcome.TooLarge, write.Outcome);
        Assert.Equal($"'outreach/ledger.json' would take {FactoryDocumentStore.MaxDocumentBytes + 1} bytes; one version of " +
                     $"a working document takes at most {FactoryDocumentStore.MaxDocumentBytes}", write.Refusal);
        Assert.Null(Documents().Get(A, TheFactory, Ledger));
    }

    [Fact]
    public void A_document_at_exactly_its_cap_is_stored()
    {
        var write = WriteBySession(Documents(), new string('x', FactoryDocumentStore.MaxDocumentBytes), expect: 0);

        Assert.Equal(FactoryRecordOutcome.Written, write.Outcome);
    }

    [Theory]
    [InlineData("../ledger.json")]
    [InlineData("outreach/../../x")]
    [InlineData("/outreach/ledger.json")]
    [InlineData("outreach//ledger.json")]
    [InlineData("C:/ledger.json")]
    [InlineData("aux.json")]
    [InlineData("outreach/nul")]
    [InlineData("ledger.")]
    [InlineData(".")]
    [InlineData("outreach/../x")]
    [InlineData("a b.md")]
    [InlineData("")]
    public void A_name_that_could_not_be_a_file_in_a_seats_kit_is_refused(string name)
    {
        var write = WriteBySession(Documents(), "text", expect: 0, name: name);

        Assert.Equal(FactoryRecordOutcome.BadName, write.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(write.Refusal));
    }

    [Theory]
    [InlineData("site/.gitignore")]
    [InlineData("site/.vercel/project.json")]
    [InlineData("form-replies/reply_01.v2.html")]
    public void A_real_file_name_from_a_factory_disk_is_accepted(string name)
    {
        Assert.Equal(FactoryRecordOutcome.Written, WriteBySession(Documents(), "text", expect: 0, name: name).Outcome);
    }

    [Fact]
    public void A_name_is_folded_to_one_spelling_so_two_spellings_are_one_document()
    {
        var store = Documents();
        WriteBySession(store, "v1", expect: 0, name: "Outreach\\Ledger.JSON");

        var second = WriteBySession(store, "v2", expect: 0, name: "outreach/ledger.json");

        Assert.Equal(FactoryRecordOutcome.Stale, second.Outcome);
        Assert.Equal("outreach/ledger.json", store.Get(A, TheFactory, "OUTREACH/LEDGER.JSON")!.Name);
    }

    [Fact]
    public void The_list_says_how_each_document_stands_and_carries_no_text()
    {
        var store = Documents();
        WriteBySession(store, "v1", expect: 0);
        WriteBySession(store, "v2", expect: 1);
        WriteBySession(store, "focus", expect: 0, name: "ceo/focus.md");

        var list = store.List(A, TheFactory);

        Assert.Equal(new[] { "ceo/focus.md", Ledger }, list.Select(d => d.Name));
        Assert.Equal(2, list.Single(d => d.Name == Ledger).Version);
        // FactoryDocumentSummary has no text field at all: the list cannot carry one.
        Assert.DoesNotContain(typeof(FactoryDocumentSummary).GetProperties(), p => p.Name == "Text");
    }

    [Fact]
    public void One_accounts_documents_are_invisible_to_another()
    {
        WriteBySession(Documents(), "A's ledger", expect: 0);

        Assert.Null(Documents().Get(B, TheFactory, Ledger));
        Assert.Empty(Documents().List(B, TheFactory));
        Assert.Equal(FactoryRecordOutcome.Written, WriteBySession(Documents(), "B's ledger", expect: 0, tenant: B).Outcome);
        Assert.Equal("A's ledger", Documents().Get(A, TheFactory, Ledger)!.Text);
    }

    [Fact]
    public void The_upload_stores_a_file_once_and_a_second_run_finds_it_already_stored()
    {
        var store = Documents();
        var first = store.Write(A, TheFactory, Ledger, "rows", 0, FactoryRecordAuthorKinds.Upload, "machine-token", Now,
            importKey: "devthrottle/outreach/ledger.json");
        var again = store.Write(A, TheFactory, Ledger, "rows", 0, FactoryRecordAuthorKinds.Upload, "machine-token", Now,
            importKey: "devthrottle/outreach/ledger.json");

        Assert.Equal(FactoryRecordOutcome.Written, first.Outcome);
        Assert.Equal(FactoryRecordOutcome.AlreadyStored, again.Outcome);
        Assert.True(again.Ok);
        Assert.Equal(1, store.History(A, TheFactory, Ledger).Total);
    }

    [Fact]
    public void The_upload_of_a_file_that_changed_since_it_was_stored_is_refused_and_changes_nothing()
    {
        var store = Documents();
        store.Write(A, TheFactory, Ledger, "rows", 0, FactoryRecordAuthorKinds.Upload, "machine-token", Now,
            importKey: "devthrottle/outreach/ledger.json");

        var changed = store.Write(A, TheFactory, Ledger, "rows plus one", 0, FactoryRecordAuthorKinds.Upload, "machine-token", Now,
            importKey: "devthrottle/outreach/ledger.json");

        Assert.Equal(FactoryRecordOutcome.ImportConflict, changed.Outcome);
        Assert.StartsWith("the upload stored 'devthrottle/outreach/ledger.json' before, as version 1", changed.Refusal);
        Assert.Equal("rows", store.Get(A, TheFactory, Ledger)!.Text);
    }

    [Fact]
    public void Only_the_upload_names_its_rows_and_the_upload_always_does()
    {
        var store = Documents();

        Assert.Throws<ArgumentException>(() => store.Write(A, TheFactory, Ledger, "x", 0,
            FactoryRecordAuthorKinds.Person, "owner", Now, importKey: "k"));
        Assert.Throws<ArgumentException>(() => store.Write(A, TheFactory, Ledger, "x", 0,
            FactoryRecordAuthorKinds.Upload, "machine-token", Now, importKey: null));
        Assert.Throws<ArgumentException>(() => store.Write(A, TheFactory, Ledger, "x", 0, "director", null, Now));
    }

    // ---------- attachments ----------

    private static byte[] Png(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    private static FactoryAttachmentWrite AddBySession(FactoryAttachmentStore store, string name, byte[] content,
        string? seat = "onboarding", string factory = TheFactory)
        => store.Add(A, factory, seat, name, "image/png", content, FactoryRecordAuthorKinds.Session, RunOne, Now);

    [Fact]
    public void A_file_is_kept_with_its_bytes_apart_and_read_back_exactly()
    {
        var content = Png(4096);

        var write = AddBySession(Attachments(), "journal/2026-10-05/01-home.png", content);

        Assert.Equal(FactoryRecordOutcome.Written, write.Outcome);
        var row = Attachments().Get(A, TheFactory, write.Attachment!.Id)!;
        Assert.Equal(4096, row.Bytes);
        Assert.Equal("onboarding", row.Seat);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(), row.Sha256);
        Assert.Equal(content, Attachments().Content(A, TheFactory, row.Id));

        using var ctx = _h.Open().CreateContext(A);
        Assert.Equal(content, ctx.FactoryAttachmentContents.Single(c => c.AttachmentId == row.Id).Content);
    }

    [Fact]
    public void The_same_bytes_are_kept_once_and_the_file_already_held_is_the_answer()
    {
        var store = Attachments();
        var resume = Png(2048);
        var first = AddBySession(store, "applications/2026-10-24/acme/resume.pdf", resume, seat: "applicant");

        var again = AddBySession(store, "applications/2026-10-25/beta/resume.pdf", resume, seat: "applicant");

        Assert.Equal(FactoryRecordOutcome.AlreadyStored, again.Outcome);
        Assert.Equal(first.Attachment!.Id, again.Attachment!.Id);
        Assert.Equal(1, store.List(A, TheFactory).Total);
        Assert.Equal(2048, store.List(A, TheFactory).FactoryBytes);
        using var ctx = _h.Open().CreateContext(A);
        Assert.Single(ctx.FactoryAttachmentContents.ToList());
    }

    [Fact]
    public void The_same_bytes_for_another_seat_are_kept_for_that_seat_so_each_seat_lists_its_own()
    {
        var store = Attachments();
        var bytes = Png(300);
        AddBySession(store, "templates/resume.pdf", bytes, seat: null);

        var mine = AddBySession(store, "applications/2026-10-24/acme/resume.pdf", bytes, seat: "applicant");

        Assert.Equal(FactoryRecordOutcome.Written, mine.Outcome);
        Assert.Equal(1, store.List(A, TheFactory, "applicant").Total);
    }

    [Fact]
    public void A_page_that_ended_on_a_file_removed_since_still_leads_to_the_older_ones()
    {
        var store = Attachments();
        var newest = store.Add(A, TheFactory, null, "c.png", "image/png", Png(3), FactoryRecordAuthorKinds.Session, RunOne, Now).Attachment!;
        var middle = store.Add(A, TheFactory, null, "b.png", "image/png", Png(4), FactoryRecordAuthorKinds.Session, RunOne, Now.AddMinutes(-1)).Attachment!;
        var oldest = store.Add(A, TheFactory, null, "a.png", "image/png", Png(5), FactoryRecordAuthorKinds.Session, RunOne, Now.AddMinutes(-2)).Attachment!;
        var first = store.Page(A, TheFactory, null, 2)!;
        Assert.Equal(new[] { newest.Id, middle.Id }, first.Attachments.Select(a => a.Id));

        store.Remove(A, TheFactory, middle.Id, FactoryRecordAuthorKinds.Session, RunOne, Now);
        var next = store.Page(A, TheFactory, first.NextBefore, 2);

        Assert.NotNull(next);
        Assert.Equal(new[] { oldest.Id }, next!.Attachments.Select(a => a.Id));
    }

    [Fact]
    public void A_removed_cursor_among_files_kept_at_one_instant_starts_the_next_page_right_after_it()
    {
        var store = Attachments();
        var ids = new[] { "a.png", "b.png", "c.png", "d.png" }
            .Select((n, i) => store.Add(A, TheFactory, null, n, "image/png", Png(10 + i), FactoryRecordAuthorKinds.Session, RunOne, Now).Attachment!.Id)
            .ToList();
        var first = store.Page(A, TheFactory, null, 2)!;
        Assert.Equal(ids.Take(2), first.Attachments.Select(a => a.Id));

        store.Remove(A, TheFactory, ids[1], FactoryRecordAuthorKinds.Session, RunOne, Now);
        var next = store.Page(A, TheFactory, first.NextBefore, 2)!;

        Assert.Equal(ids.Skip(2), next.Attachments.Select(a => a.Id));
    }

    [Fact]
    public void The_same_bytes_in_another_factory_or_account_are_kept_there_too()
    {
        var store = Attachments();
        var bytes = Png(512);
        AddBySession(store, "a.png", bytes);

        Assert.Equal(FactoryRecordOutcome.Written, AddBySession(store, "a.png", bytes, factory: Other).Outcome);
        Assert.Equal(FactoryRecordOutcome.Written,
            store.Add(B, TheFactory, null, "a.png", "image/png", bytes, FactoryRecordAuthorKinds.Session, RunOne, Now).Outcome);
    }

    [Fact]
    public void A_removed_file_loses_its_bytes_keeps_its_row_and_no_longer_counts()
    {
        var store = Attachments();
        var kept = AddBySession(store, "applications/2026-07-01/acme/form.html", Png(1000)).Attachment!;
        var later = Now.AddDays(100);

        var removal = store.Remove(A, TheFactory, kept.Id, FactoryRecordAuthorKinds.Session, RunTwo, later);

        Assert.Equal(FactoryRecordOutcome.Removed, removal.Outcome);
        var row = store.Get(A, TheFactory, kept.Id)!;
        Assert.Equal(later, row.RemovedAtUtc);
        Assert.Equal($"session:{RunTwo}", row.RemovedBy);
        Assert.Null(store.Content(A, TheFactory, kept.Id));
        Assert.Equal(0, store.List(A, TheFactory).Total);
        Assert.Equal(0, store.List(A, TheFactory).FactoryBytes);
        Assert.Equal(new[] { kept.Id }, store.Missing(A, TheFactory, new[] { kept.Id }));
        Assert.Single(store.Find(A, TheFactory, new[] { kept.Id }));   // a journal entry that named it still finds it
        using var ctx = _h.Open().CreateContext(A);
        Assert.Empty(ctx.FactoryAttachmentContents.ToList());
    }

    [Fact]
    public void Removing_twice_is_answered_removed_again_and_changes_nothing()
    {
        var store = Attachments();
        var kept = AddBySession(store, "x.png", Png(10)).Attachment!;
        store.Remove(A, TheFactory, kept.Id, FactoryRecordAuthorKinds.Session, RunOne, Now);

        var again = store.Remove(A, TheFactory, kept.Id, FactoryRecordAuthorKinds.Person, "owner", Now.AddDays(1));

        Assert.Equal(FactoryRecordOutcome.Removed, again.Outcome);
        Assert.Equal(Now, store.Get(A, TheFactory, kept.Id)!.RemovedAtUtc);
        Assert.Equal($"session:{RunOne}", store.Get(A, TheFactory, kept.Id)!.RemovedBy);
    }

    [Fact]
    public void A_file_never_held_or_held_by_another_factory_or_account_is_not_found_and_stays()
    {
        var store = Attachments();
        var theirs = AddBySession(store, "x.png", Png(10), factory: Other).Attachment!;

        Assert.Equal(FactoryRecordOutcome.NotFound,
            store.Remove(A, TheFactory, theirs.Id, FactoryRecordAuthorKinds.Session, RunOne, Now).Outcome);
        Assert.Equal(FactoryRecordOutcome.NotFound,
            store.Remove(B, Other, theirs.Id, FactoryRecordAuthorKinds.Session, RunOne, Now).Outcome);
        Assert.Equal(FactoryRecordOutcome.NotFound,
            store.Remove(A, TheFactory, Guid.NewGuid(), FactoryRecordAuthorKinds.Session, RunOne, Now).Outcome);
        Assert.NotNull(store.Content(A, Other, theirs.Id));
    }

    [Fact]
    public void After_a_removal_the_same_bytes_are_kept_again_as_a_new_file()
    {
        var store = Attachments();
        var bytes = Png(64);
        var first = AddBySession(store, "a.png", bytes).Attachment!;
        store.Remove(A, TheFactory, first.Id, FactoryRecordAuthorKinds.Session, RunOne, Now);

        var again = AddBySession(store, "a.png", bytes);

        Assert.Equal(FactoryRecordOutcome.Written, again.Outcome);
        Assert.NotEqual(first.Id, again.Attachment!.Id);
        Assert.Equal(bytes, store.Content(A, TheFactory, again.Attachment.Id));
    }

    [Fact]
    public void An_attachment_over_its_cap_is_refused_in_words_and_nothing_is_stored()
    {
        var store = Attachments();
        var big = Png(FactoryAttachmentStore.MaxAttachmentBytes + 1);

        var write = AddBySession(store, "walk/01-home.png", big);

        Assert.Equal(FactoryRecordOutcome.TooLarge, write.Outcome);
        Assert.Equal($"'walk/01-home.png' holds {FactoryAttachmentStore.MaxAttachmentBytes + 1} bytes; one attachment holds " +
                     $"at most {FactoryAttachmentStore.MaxAttachmentBytes} ({FactoryAttachmentStore.MaxAttachmentBytes / (1024 * 1024)} MB). Nothing was stored. Make the file smaller " +
                     "- a screenshot at a lower resolution, or as a JPEG - and add it again", write.Refusal);
        Assert.Equal(0, store.List(A, TheFactory).Total);
        using var ctx = _h.Open().CreateContext(A);
        Assert.Empty(ctx.FactoryAttachmentContents.ToList());
    }

    [Fact]
    public void A_factory_at_its_total_is_refused_another_file_with_how_much_it_holds()
    {
        // The total is what the rows say the files hold, so the factory is brought to its cap with one row rather
        // than a gigabyte of bytes.
        using (var ctx = _h.Open().CreateContext(A))
        {
            var held = new FactoryAttachmentEntity
            {
                TenantId = A.Value, Factory = TheFactory, Name = "old.png", ContentType = "image/png",
                Bytes = FactoryAttachmentStore.MaxFactoryBytes - 10, Sha256 = "0", AuthorKind = FactoryRecordAuthorKinds.Session,
                WrittenAtUtc = Now,
            };
            ctx.FactoryAttachments.Add(held);
            ctx.FactoryAttachmentContents.Add(new FactoryAttachmentContentEntity { TenantId = A.Value, AttachmentId = held.Id });
            ctx.SaveChanges();
        }

        var store = Attachments();
        Assert.Equal(FactoryRecordOutcome.Written, AddBySession(store, "fits.png", Png(10)).Outcome);
        var over = AddBySession(store, "one-too-many.png", Png(1));

        Assert.Equal(FactoryRecordOutcome.FactoryFull, over.Outcome);
        Assert.Equal($"'devthrottle' already holds {FactoryAttachmentStore.MaxFactoryBytes} bytes of attachments, and with " +
                     $"'one-too-many.png' (1 bytes) it would hold {FactoryAttachmentStore.MaxFactoryBytes + 1}; a factory holds " +
                     $"at most {FactoryAttachmentStore.MaxFactoryBytes} bytes ({FactoryAttachmentStore.MaxFactoryBytes / (1024 * 1024)} MB) of attachments together. Nothing was " +
                     "stored. Ask the owner: the factory has reached the most it may keep", over.Refusal);
        // Another factory's total is its own.
        Assert.Equal(FactoryRecordOutcome.Written, AddBySession(store, "x.png", Png(1), factory: Other).Outcome);
    }

    [Fact]
    public void The_listing_carries_no_bytes_and_says_what_the_factory_holds()
    {
        var store = Attachments();
        AddBySession(store, "a.png", Png(100));
        AddBySession(store, "b.png", Png(200), seat: null);

        var all = store.List(A, TheFactory);
        var onboarding = store.List(A, TheFactory, seat: "onboarding");

        Assert.Equal(2, all.Total);
        Assert.Equal(300, all.FactoryBytes);
        Assert.Equal(new[] { "a.png" }, onboarding.Attachments.Select(a => a.Name));
        Assert.Equal(300, onboarding.FactoryBytes);
        Assert.DoesNotContain(typeof(FactoryAttachmentEntity).GetProperties(), p => p.PropertyType == typeof(byte[]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("image/png; charset=x")]
    [InlineData(null)]
    public void A_content_type_that_is_not_one_word_is_refused(string? type)
    {
        var write = Attachments().Add(A, TheFactory, null, "a.png", type, Png(1), FactoryRecordAuthorKinds.Person, "owner", Now);

        Assert.Equal(FactoryRecordOutcome.BadName, write.Outcome);
    }

    [Fact]
    public void The_upload_keeps_a_file_once_and_refuses_one_whose_bytes_changed()
    {
        var store = Attachments();
        var bytes = Png(64);
        var first = store.Add(A, TheFactory, "onboarding", "a.png", "image/png", bytes, FactoryRecordAuthorKinds.Upload, "machine-token", Now, "devthrottle/onboarding/a.png");
        var again = store.Add(A, TheFactory, "onboarding", "a.png", "image/png", bytes, FactoryRecordAuthorKinds.Upload, "machine-token", Now, "devthrottle/onboarding/a.png");
        var changed = store.Add(A, TheFactory, "onboarding", "a.png", "image/png", Png(65), FactoryRecordAuthorKinds.Upload, "machine-token", Now, "devthrottle/onboarding/a.png");

        Assert.Equal(FactoryRecordOutcome.Written, first.Outcome);
        Assert.Equal(FactoryRecordOutcome.AlreadyStored, again.Outcome);
        Assert.Equal(first.Attachment!.Id, again.Attachment!.Id);
        Assert.Equal(FactoryRecordOutcome.ImportConflict, changed.Outcome);
        Assert.Equal(1, store.List(A, TheFactory).Total);
    }

    // ---------- the journal ----------

    private static FactoryJournalWrite AppendBySession(FactoryJournalStore store, string text, string? date = "2026-10-14",
        string? seat = Outreach, IReadOnlyList<Guid>? attachments = null, string factory = TheFactory)
        => store.Append(A, factory, seat, date, text, attachments, FactoryRecordAuthorKinds.Session, RunOne, Now);

    [Fact]
    public void An_entry_is_added_and_read_back_with_its_day_its_seat_and_who_wrote_it()
    {
        var write = AppendBySession(Journal(), "Sent three follow-ups.");

        Assert.Equal(FactoryRecordOutcome.Written, write.Outcome);
        var entry = Journal().Get(A, TheFactory, write.Entry!.Id)!;
        Assert.Equal("Sent three follow-ups.", entry.Text);
        Assert.Equal("2026-10-14", entry.EntryDate);
        Assert.Equal(Outreach, entry.Seat);
        Assert.Equal(RunOne, entry.AuthorId);
    }

    [Fact]
    public void A_journal_is_append_only_two_entries_for_one_day_are_both_kept_and_the_store_offers_no_edit()
    {
        var store = Journal();
        AppendBySession(store, "Morning run.");
        AppendBySession(store, "Correction: the morning run sent two, not three.");

        Assert.Equal(2, store.List(A, TheFactory, Outreach).Total);
        // Nothing on the store can change or remove an entry: Append is its only write.
        var writes = typeof(FactoryJournalStore).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => n is not ("List" or "Get" or "Journals" or "Page")).ToList();
        Assert.Equal(new[] { "Append" }, writes);
    }

    [Fact]
    public void An_entry_left_without_a_day_is_written_for_today_by_the_gateways_clock()
    {
        var write = AppendBySession(Journal(), "No day given.", date: null);

        Assert.Equal("2026-10-14", write.Entry!.EntryDate);
    }

    [Theory]
    [InlineData("14/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("yesterday")]
    public void A_day_that_is_not_one_is_refused(string date)
    {
        var write = AppendBySession(Journal(), "text", date: date);

        Assert.Equal(FactoryRecordOutcome.BadName, write.Outcome);
        Assert.Equal($"a journal entry's day is written yyyy-MM-dd, such as 2026-10-14, so '{date}' cannot be one", write.Refusal);
    }

    [Fact]
    public void An_empty_entry_and_an_oversized_one_are_refused()
    {
        var store = Journal();

        Assert.Equal(FactoryRecordOutcome.Empty, AppendBySession(store, "   ").Outcome);
        Assert.Equal(FactoryRecordOutcome.TooLarge, AppendBySession(store, new string('x', FactoryJournalStore.MaxEntryBytes + 1)).Outcome);
        Assert.Equal(0, store.List(A, TheFactory).Total);
    }

    [Fact]
    public void An_entry_names_its_attachments_and_may_name_only_files_its_own_factory_holds()
    {
        var files = Attachments();
        var mine = AddBySession(files, "walk/01.png", Png(10)).Attachment!.Id;
        var theirs = AddBySession(files, "walk/01.png", Png(10), factory: Other).Attachment!.Id;
        var store = Journal();

        var good = AppendBySession(store, "Walked onboarding.", attachments: new[] { mine });
        var bad = AppendBySession(store, "Walked onboarding.", attachments: new[] { mine, theirs });

        Assert.Equal(FactoryRecordOutcome.Written, good.Outcome);
        Assert.Equal(mine.ToString(), good.Entry!.AttachmentIds);
        Assert.Equal(FactoryRecordOutcome.NoSuchAttachment, bad.Outcome);
        Assert.Contains(theirs.ToString(), bad.Refusal);
        Assert.Equal(1, store.List(A, TheFactory).Total);
    }

    [Fact]
    public void The_listing_is_newest_day_first_carries_no_text_and_can_be_narrowed()
    {
        var store = Journal();
        AppendBySession(store, "day one", date: "2026-10-07");
        AppendBySession(store, "day two", date: "2026-10-08");
        AppendBySession(store, "the factory's own", date: "2026-10-09", seat: null);
        AppendBySession(store, "another seat", date: "2026-10-10", seat: "defects");

        Assert.Equal(new[] { "2026-10-10", "2026-10-09", "2026-10-08", "2026-10-07" },
            store.List(A, TheFactory).Entries.Select(e => e.EntryDate));
        Assert.Equal(new[] { "2026-10-08", "2026-10-07" }, store.List(A, TheFactory, Outreach).Entries.Select(e => e.EntryDate));
        Assert.Equal(new[] { "2026-10-09" }, store.List(A, TheFactory, factoryOnly: true).Entries.Select(e => e.EntryDate));
        Assert.Equal(new[] { "2026-10-09", "2026-10-08" },
            store.List(A, TheFactory, since: "2026-10-08", until: "2026-10-09").Entries.Select(e => e.EntryDate));
        var page = store.List(A, TheFactory, limit: 1);
        Assert.Single(page.Entries);
        Assert.Equal(4, page.Total);
        Assert.DoesNotContain(typeof(FactoryJournalSummary).GetProperties(), p => p.Name == "Text");
    }

    [Fact]
    public void The_upload_adds_an_entry_once_and_refuses_one_whose_file_changed()
    {
        var store = Journal();
        FactoryJournalWrite Upload(string text) => store.Append(A, TheFactory, Outreach, "2026-10-07", text, null,
            FactoryRecordAuthorKinds.Upload, "machine-token", Now, importKey: "devthrottle/outreach/journal/2026-10-07.md");

        Assert.Equal(FactoryRecordOutcome.Written, Upload("the day").Outcome);
        Assert.Equal(FactoryRecordOutcome.AlreadyStored, Upload("the day").Outcome);
        Assert.Equal(FactoryRecordOutcome.ImportConflict, Upload("the day, rewritten").Outcome);
        Assert.Equal("the day", store.Get(A, TheFactory, store.List(A, TheFactory).Entries.Single().Id)!.Text);
    }

    [Fact]
    public void The_records_of_a_factory_that_is_not_registered_are_kept_as_memory_is()
    {
        // The upload stores every factory folder on a machine, and on 14 October two of them (cfo, vercel-release)
        // were never registered. Refusing them would leave their records on the one disk.
        Assert.Equal(FactoryRecordOutcome.Written, AppendBySession(Journal(), "cfo's day", factory: "cfo", seat: null).Outcome);
        Assert.Equal(FactoryRecordOutcome.Written, Documents().Write(A, "vercel-release", "releases.log", "x", 0,
            FactoryRecordAuthorKinds.Session, RunOne, Now).Outcome);
    }
}
