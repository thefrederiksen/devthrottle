using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The session lock is answered from memory, not by re-reading the staging root once per session.
///
/// WHAT THESE PIN AND WHY. The lock used to be computed by listing the staging root and reading every
/// upload's record.json on EVERY call, and it is called once per session by the five-second display-state
/// fold - so the cost was O(sessions x staged uploads) per pass, and it never fell, because a staging
/// directory only disappears on the success path. On the hosted Gateway that root is a billed network share
/// and it measured 5.5 million file opens a day.
///
/// The behaviour that matters is therefore twofold and BOTH halves need a test: the answer must still be
/// correct through every state transition (or the fix is a regression wearing a speed-up), and the hot path
/// must not go back to disk (or the fix silently rots the first time someone re-adds a read). The last two
/// tests are the ones that fail if the scan ever comes back.
/// </summary>
public sealed class DictationSessionLockIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-lockidx-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
    }

    private VoiceUploadStore NewStore() => new(_root, TenantId.Local);

    [Fact]
    public void MarkPending_LocksTheSession()
    {
        var store = NewStore();
        var id = store.Register(null);
        store.MarkPending(id, "session-a");

        Assert.True(store.IsSessionLocked("session-a"));
        Assert.False(store.IsSessionLocked("session-b"));
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("abandoned")]
    [InlineData("failed")]
    public void TerminalAndParkedStates_ReleaseTheLock(string transition)
    {
        var store = NewStore();
        var id = store.Register(null);
        store.MarkPending(id, "session-a");
        Assert.True(store.IsSessionLocked("session-a"));

        switch (transition)
        {
            case "delivered": store.MarkDelivered(id, submitted: true, movedOn: false, "words"); break;
            case "abandoned": store.MarkAbandoned(id, "user_abandoned"); break;
            case "failed": store.MarkFailed(id, "out_of_credits"); break;
        }

        Assert.False(store.IsSessionLocked("session-a"));
    }

    [Fact]
    public void ClearFailed_RelocksTheSession()
    {
        var store = NewStore();
        var id = store.Register(null);
        store.MarkPending(id, "session-a");
        store.MarkFailed(id, "transcription_error");
        Assert.False(store.IsSessionLocked("session-a"));

        Assert.True(store.ClearFailed(id));
        Assert.True(store.IsSessionLocked("session-a"));
    }

    [Fact]
    public void Delete_ReleasesTheLock()
    {
        var store = NewStore();
        var id = store.Register(null);
        store.MarkPending(id, "session-a");

        store.Delete(id);

        Assert.False(store.IsSessionLocked("session-a"));
    }

    [Fact]
    public void OnlyPendingSessionsAreReported()
    {
        var store = NewStore();
        var a = store.Register(null);
        var b = store.Register(null);
        var c = store.Register(null);
        store.MarkPending(a, "session-a");
        store.MarkPending(b, "session-b");
        store.MarkPending(c, "session-c");
        store.MarkDelivered(c, submitted: true, movedOn: false, "done");

        var locked = store.LockedSessionIds();

        Assert.Equal(new[] { "session-a", "session-b" }, locked.OrderBy(x => x).ToArray());
    }

    /// <summary>
    /// A cache in front of a durable record must still be right after a restart, which is the one case the
    /// write path cannot supply: the process that wrote the marker is gone. A brand new store over the same
    /// directory must therefore read the lock back off disk.
    /// </summary>
    [Fact]
    public void ANewStoreOverTheSameRoot_HydratesTheLockFromDisk()
    {
        var first = NewStore();
        var id = first.Register(null);
        first.MarkPending(id, "session-a");

        var afterRestart = NewStore();

        Assert.True(afterRestart.IsSessionLocked("session-a"));
    }

    /// <summary>
    /// THE COST TEST. Once the partition has been read, the answer comes from memory - so removing the
    /// record file behind the store's back does NOT change the answer. That is a strange-looking assertion
    /// on purpose: it is the only way to state "this call did not open a file" as a behaviour rather than as
    /// a hope, and it goes red the moment someone puts a disk read back on the hot path.
    /// </summary>
    [Fact]
    public void AfterTheFirstRead_TheLockIsAnsweredWithoutTouchingDisk()
    {
        var store = NewStore();
        var id = store.Register(null);
        store.MarkPending(id, "session-a");
        Assert.True(store.IsSessionLocked("session-a"));   // hydrates / write-through

        // Reach around the store and delete its durable marker. Nothing legitimate does this; it stands in
        // for "the disk was not consulted".
        File.Delete(Path.Combine(_root, id, "record.json"));

        Assert.True(store.IsSessionLocked("session-a"));
    }

    /// <summary>
    /// FAIL OPEN, exactly as the disk enumeration did. An unreadable partition must report NO lock rather
    /// than a lock nobody can clear - a false lock silently refuses the user's typing, which is strictly
    /// worse than an unenforced one.
    /// </summary>
    [Fact]
    public void AnUnreadableRoot_ReportsNoLock_RatherThanGuessingOne()
    {
        var store = NewStore();
        var id = store.Register(null);
        store.MarkPending(id, "session-a");

        // A different store over a root that does not exist and was never written: the question is
        // answerable only from disk, and there is nothing there.
        var elsewhere = new VoiceUploadStore(
            Path.Combine(Path.GetTempPath(), "cc-lockidx-empty-" + Guid.NewGuid().ToString("N")), TenantId.Local);

        Assert.False(elsewhere.IsSessionLocked("session-a"));
        Assert.Empty(elsewhere.LockedSessionIds());
    }

    /// <summary>
    /// The cache is keyed by PARTITION ROOT, so it cannot become a hole in the tenant boundary the
    /// directory layout enforces (issue #1884). One tenant's pending dictation must never lock a session
    /// read through another tenant's handle.
    /// </summary>
    [Fact]
    public void OneTenantsPendingDictation_DoesNotLockAnotherTenant()
    {
        var baseStore = NewStore();
        var a = baseStore.ForTenant(new TenantId("11111111-1111-1111-1111-111111111111"));
        var b = baseStore.ForTenant(new TenantId("22222222-2222-2222-2222-222222222222"));

        var id = a.Register(null);
        a.MarkPending(id, "shared-session-id");

        Assert.True(a.IsSessionLocked("shared-session-id"));
        Assert.False(b.IsSessionLocked("shared-session-id"));
    }

    /// <summary>
    /// Two handles onto the SAME partition share one index, so a transition written through one is seen by
    /// the other. GatewayHost builds a fresh store with ForTenant for every session of every fold, so if
    /// these did not agree the lock would flap depending on which handle happened to ask.
    /// </summary>
    [Fact]
    public void TwoHandlesOntoTheSamePartition_AgreeAboutTheLock()
    {
        var baseStore = NewStore();
        var tenant = new TenantId("33333333-3333-3333-3333-333333333333");
        var writer = baseStore.ForTenant(tenant);
        var reader = baseStore.ForTenant(tenant);

        var id = writer.Register(null);
        writer.MarkPending(id, "session-a");
        Assert.True(reader.IsSessionLocked("session-a"));

        writer.MarkDelivered(id, submitted: true, movedOn: false, "done");
        Assert.False(reader.IsSessionLocked("session-a"));
    }

    [Fact]
    public void PendingUploadsFor_NamesOnlyTheUploadsPendingForThatTarget()
    {
        var index = new DictationLockIndex();
        var hydrate = () => new[] { HydratedRecord.PendingFor("u1", "factory-inbox:invoices"), HydratedRecord.PendingFor("u2", "session-a"), HydratedRecord.PendingFor("u3", "factory-inbox:invoices") };

        var found = index.PendingUploadsFor(_root, "factory-inbox:invoices", hydrate)!;
        index.RecordWritten(_root, "u1", "factory-inbox:invoices", pending: false);

        Assert.Equal(new[] { "u1", "u3" }, found.Pending.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "u3" }, index.PendingUploadsFor(_root, "factory-inbox:invoices", hydrate)!.Pending);
        Assert.Empty(index.PendingUploadsFor(_root, "factory-inbox:payroll", hydrate)!.Pending);
    }

    [Fact]
    public void PendingUploadsFor_PartitionCannotBeRead_AnswersUnknownAndTriesAgain()
    {
        // A failed read must never be cached as "nothing is held": the factory inbox thread turns null into an error.
        var index = new DictationLockIndex();
        var calls = 0;
        IEnumerable<HydratedRecord> Hydrate()
        {
            calls++;
            if (calls == 1) throw new IOException("the share is unreachable");
            return new[] { HydratedRecord.PendingFor("u1", "factory-inbox:invoices") };
        }

        var first = index.PendingUploadsFor(_root, "factory-inbox:invoices", Hydrate);
        var second = index.PendingUploadsFor(_root, "factory-inbox:invoices", Hydrate);

        Assert.Null(first);
        Assert.Equal(new[] { "u1" }, second!.Pending);
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// Holds a record file open with no sharing, so the store's read of it fails exactly as a share fault or another
    /// writer's hold does - on Windows, Linux and macOS alike.
    /// </summary>
    private static FileStream HoldUnreadable(string recordPath)
        => new(recordPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void PendingFor_OneRecordUnreadableDuringHydration_IsShownOnceItCanBeReadAgain()
    {
        // Finding 1 of the 4690 review, its probe made a committed test: one failed read while the partition was first
        // read used to be cached as "not held" for the life of the process.
        var writer = NewStore();
        var open = writer.OpenPending(null, "factory-inbox:invoices");
        var record = Path.Combine(_root, open.UploadId, "record.json");
        var reader = NewStore(); // a fresh process: nothing read yet

        Exception? whileUnreadable;
        using (HoldUnreadable(record))
            whileUnreadable = Record.Exception(() => reader.PendingFor("factory-inbox:invoices"));
        var afterReadable = reader.PendingFor("factory-inbox:invoices");
        var freshProcess = NewStore().PendingFor("factory-inbox:invoices");

        // While it cannot be read the thread cannot say whether it is held, and says so rather than "nothing".
        Assert.IsType<InvalidOperationException>(whileUnreadable);
        Assert.Equal(open.UploadId, Assert.Single(freshProcess).UploadId);
        Assert.Equal(open.UploadId, Assert.Single(afterReadable).UploadId);
    }

    [Fact]
    public void PendingFor_UnreadableRecordOfASessionDictation_IsSettledByOneReadAndStopsBeingReRead()
    {
        var writer = NewStore();
        var open = writer.OpenPending(null, "session-a");
        var record = Path.Combine(_root, open.UploadId, "record.json");
        var reader = NewStore();
        using (HoldUnreadable(record))
            Assert.Throws<InvalidOperationException>(() => reader.PendingFor("factory-inbox:invoices"));
        var reads = new List<string>();
        reader.RecordReadForTests = reads.Add;

        var first = reader.PendingFor("factory-inbox:invoices");
        var second = reader.PendingFor("factory-inbox:invoices");

        // Read once to learn it is a session's dictation, which also gives the session lock its answer; then never again.
        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(new[] { open.UploadId }, reads);
        Assert.True(reader.IsSessionLocked("session-a"));
    }

    [Fact]
    public void PendingFor_MalformedRecord_IsSkippedAndDoesNotMakeTheThreadError()
    {
        // A corrupt record is a durable fact the sweeps leave standing as evidence; it must not break every thread read.
        var store = NewStore();
        var held = store.OpenPending(null, "factory-inbox:invoices");
        var corrupt = store.Register(null);
        File.WriteAllText(Path.Combine(_root, corrupt, "record.json"), "{ not json");

        var found = NewStore().PendingFor("factory-inbox:invoices");

        Assert.Equal(held.UploadId, Assert.Single(found).UploadId);
    }
}
