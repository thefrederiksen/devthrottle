# Review: the factory inbox thread finds its held recordings from the pending index (pull request 4690)

Reviewed on 6 November 2026 by a separate review session (fleet session 8e98e6b5, "Factory Inbox - Reviewer - thread
held lookup fast (4690)"), on machine devlinux. Pull request 4690, worktree `devthrottle-fi-review-4690`, detached at
commit `ddadf2dc6`, one commit on top of `origin/main` at `48e9c99c8` (`git diff origin/main...HEAD`: four files,
183 lines added, 37 removed).

**Verdict: CHANGES.** One proven regression against the rule of pull request 4630 (finding 1), one proven but narrow
window during a deploy (finding 2), and the first-read cost moves rather than disappears (observation 1). The fix of
the live defect itself is right: a thread read no longer walks the partition.

## Scope

What I read: the one commit's diff; `DictationLockIndex` in full; in `VoiceUploadStore` the constructors and
`ForTenant`, `OpenPending`, `MarkPending`, `WriteRecordMarker`, `WriteTombstone`, `Delete`, the three sweeps,
`QuarantineLegacyUploads`, `IsSessionLocked`, `EnumeratePendingRecordsWithId`, `ReadRecordFile` and
`ReadAllTextNonBlocking`; the thread route in `FactoryInboxEndpoints`; the register and complete routes in
`FactoryInboxVoiceEndpoints`; the host's wiring of `_dictationUploads` (one instance, one index, every tenant through
`ForTenant`) and its sweep timer; the client's retry in `factoryInboxVoice.ts` and `FactoryComposer.tsx`; the deploy
workflow's note on container overlap; the 4630 review and section 5 of `MISSION-FINISH.md`.

What I ran: the suite filter named in the brief, with `DOTNET_USE_POLLING_FILE_WATCHER=1` -
`FullyQualifiedName~FactoryInbox|FullyQualifiedName~Dictation|FullyQualifiedName~Voice` - 912 passed, 0 failed, 1
minute 56 seconds. And two throwaway probe tests, written only to answer the brief's question 1, run once against this
commit and once against the parent commit's four files, then deleted. They are not committed; the tree holds nothing
but this review. Their text is in finding 1 and finding 2 so anyone can re-run them.

What I did not do: measure anything on the hosted file share; review the client; run the parked suites beyond the
filter. I never changed product code.

## The defect and the fix, as read

On `origin/main`, `VoiceUploadStore.PendingFor` listed every directory of the partition and, for each canonical
upload directory, took its record gate, checked it existed, read its record and read its timestamp. On the hosted
Gateway the partition is an Azure Files share where each of those is a network round trip, so a thread read for ANY
factory cost one pass over every upload the account ever staged.

After the commit, `PendingFor` asks `DictationLockIndex.PendingUploadsFor` for the upload identifiers whose
in-memory PENDING entry names exactly this target, and re-reads only those from disk under their own gates. The index
is the one the session lock has used since issue 1188: one object per Gateway process, keyed by partition root,
handed to every tenant's store through `ForTenant` (confirmed: `GatewayHost._dictationUploads` is built once and the
thread, the register, the complete, the held-delivery driver and the sweeps all reach it through `ForTenant`). It is
kept true by `WriteRecordMarker`, the one place a record is written, after the atomic move; `Delete` removes one
entry; the two wholesale sweeps and the legacy quarantine call `Invalidate`, which clears the map and marks the
partition unhydrated.

The commit also removes a swallow: `EnumeratePendingRecordsWithId` no longer catches a failed directory listing and
yields nothing. The failure now reaches the index, which leaves the partition unhydrated and answers null; the session
lock still reads null as no lock, and `PendingFor` turns it into an `InvalidOperationException`. There is no handler
for that exception in the thread route or in the host (no `UseExceptionHandler`), so the whole thread answers a bare
500 with no body, typed messages included. That is loud rather than silent, which is what the rule asks; the phone and
the Cockpit will show whatever they show for a bodiless 500, which I did not review.

## Question 1: can a held recording be missing from the index on any path?

I went through every way a PENDING factory-inbox record comes to exist or stops existing on disk, and whether the
index learns of it.

| Path | Index kept true? |
|---|---|
| Register (`OpenPending` writes PENDING with the target in the session field) | Yes, through `WriteRecordMarker`. |
| Register before a restart, read after it | Yes: the new process hydrates from disk on its first question. Tested by `Thread_AfterAGatewayRestart_StillShowsTheRecordingHeldBeforeIt`. |
| Complete that fails retryably (503) | Writes nothing; the entry stays PENDING in the index and on disk. Correct. |
| Complete that refuses for good, or delivers, or hears nothing | Tombstone through `WriteRecordMarker`, entry removed. Correct. |
| Owner discards (abandon) | Tombstone through `WriteRecordMarker`. Correct. |
| `ExpireStalePending` | Skips factory-inbox targets (the 4630 fix); other expiries go through `MarkAbandoned`, no invalidation needed. Correct. |
| `SweepResolvedTombstones`, `SweepAbandoned`, `QuarantineLegacyUploads` | `Invalidate` when anything was removed; the next question re-hydrates. Correct. |
| A write landing while hydration is walking the disk | Both hold the partition gate, so the write is either seen by the walk or re-asserted after it. Correct, and unchanged. |
| **One record that cannot be read at the moment of hydration** | **No. Finding 1.** |
| **A record written by another process over the same share after this process hydrated** | **No. Finding 2.** |

Target matching is ordinal, as the old walk's was; upload identifiers are matched without case, and the store only ever
creates the canonical lower-case spelling. `BelongsHere` is applied in the hydration and again on the re-read. The
Director's `DictationLockReader` only reads records; it writes none. No other writer of these records exists in the
repository.

### Finding 1 (blocking): a record unreadable once during hydration is cached as "not held" for the life of the process

`ReadRecordFile` answers `CouldNotRead` for any failure that is not "file not found": a share fault, a permission
error, a file another writer holds. `EnumeratePendingRecordsWithId` matches only `Present` and PENDING, so a
`CouldNotRead` record is skipped, and `Hydrated` then marks the partition hydrated anyway. From that moment the thread
answers from the map, and the record is absent from it. Nothing re-reads it: not the next thread read, not the next
day's. Only a sweep that actually removes something (the six-hour tombstone timer, and only when a tombstone older
than thirty days exists), a restart, or a fresh register of the same recording from the device repairs it. The commit's
own words name this as the loss the thread exists to prevent - "a held message hidden by a disk error" - and apply the
rule to the directory listing, but not to the record reads inside it.

On the parent commit the same transient failure hid the recording for one thread read; the next read found it.

Why it matters for the 4630 rule and not only for the session lock: the session lock fails open on purpose. The thread
is the one place a held recording is shown when the device that holds the other copy is off or gone ("never only on
one device", `MISSION-FINISH.md` section 5). A recording held for a day whose record read fails once during the first
hydration after a deploy is then shown nowhere until the next invalidation. The device's own retry (every fifteen
seconds, `FactoryComposer.tsx`, `RETRY_MS`) re-registers and repairs the index, but only while that device's composer
is open.

Proof. This probe passes on the parent commit's four files and fails on `ddadf2dc6` at the last line with
`Assert.Single() Failure: The collection was empty`:

```csharp
[Fact]
public void OneRecordUnreadableDuringHydration_StaysHiddenAfterItIsReadableAgain()
{
    var writer = new VoiceUploadStore(_root, TenantId.Local);
    var open = writer.OpenPending(null, "factory-inbox:invoices");
    var record = Path.Combine(_root, open.UploadId, "record.json");
    File.SetUnixFileMode(record, UnixFileMode.None);
    var reader = new VoiceUploadStore(_root, TenantId.Local); // a fresh process: nothing hydrated yet
    var whileUnreadable = reader.PendingFor("factory-inbox:invoices");
    File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    var afterReadable = reader.PendingFor("factory-inbox:invoices");
    var freshProcess = new VoiceUploadStore(_root, TenantId.Local).PendingFor("factory-inbox:invoices");
    Assert.Empty(whileUnreadable);
    Assert.Single(freshProcess);   // the disk serves it now
    Assert.Single(afterReadable);  // FAILS on 4690: the miss was cached
}
```

What would close it, for the developer to decide: a `CouldNotRead` answer during hydration should be treated like a
failed listing (the partition stays unhydrated, the thread errors, the next question tries again), while `Malformed`
should keep being skipped as the old walk skipped it, because a corrupt record is a durable fact the sweeps leave
standing as evidence and must not make the thread error forever. The index could also remember the identifiers it
could not read and re-read exactly those on each `PendingUploadsFor`. Either way the thread must never answer from a
map that was built around a read it knows failed.

### Finding 2 (proven, narrow): a record written by another process after this process hydrated is not shown

The index is a per-process cache with no cross-process signal. `deploy-hosted-gateway.yml` (lines 131 to 135) records
that the platform runs the outgoing and incoming containers together across a restart, on one share, and that an
overlap of 144 seconds was observed on 30 July 2026. A register served by the outgoing container after the incoming
container has hydrated writes a PENDING record the incoming container's map never contains. If that recording's
transcription then fails retryably, the thread on the new container does not show it as held.

Bounded by: the device's fifteen-second retry re-registers through the new container, which writes PENDING there and
repairs the map - so the gap lasts about one retry while the composer is open on the device that recorded it. It is
not bounded when that device goes away right after sending. The parent commit read the disk on every thread read and
had no such window.

Proof, same shape, same results (passes on the parent, fails on `ddadf2dc6` at the last line):

```csharp
[Fact]
public void RecordWrittenByAnotherProcessAfterHydration_IsNotShown()
{
    var reader = new VoiceUploadStore(_root, TenantId.Local);
    Assert.Empty(reader.PendingFor("factory-inbox:invoices")); // hydrates: nothing yet
    var otherProcess = new VoiceUploadStore(_root, TenantId.Local); // its own index, same share
    otherProcess.OpenPending(null, "factory-inbox:invoices");
    Assert.Single(new VoiceUploadStore(_root, TenantId.Local).PendingFor("factory-inbox:invoices"));
    Assert.Single(reader.PendingFor("factory-inbox:invoices")); // FAILS on 4690
}
```

I do not ask for a cross-process index here; that is a design question above this change. I ask that the window be
written down beside `PendingFor`, with the deploy overlap named, so the next reader of a "held recording missing after
a deploy" report knows where to look. If finding 1 is closed by re-reading unreadable identifiers, this one stays as
it is.

## Question 2: the first read after a process start

Yes, the first question to a partition in a process is still a full walk of the share: one directory listing plus one
record read per upload directory (`EnumeratePendingRecordsWithId`), which is fewer round trips than the old
`PendingFor` (which also took the gate, checked existence and read the timestamp per directory) but of the same
order - tens of seconds for the account that measured seventy. It is paid again after every sweep that removed
something, at most every six hours.

Who pays it: whoever asks first. For a tenant with a session online, that is almost always the fleet doorbell's
session-lock question (`GatewayHost`, `dictationInFlight`), in the background, before any thread read arrives. For a
tenant with no session online - the owner on the phone while the Director is off - the thread read itself pays it,
once per process and once per invalidation. That thread read then takes as long as the old defect did, once, instead
of every time; and while hydration holds the partition gate, every record write for that tenant (a register, a
complete) waits for it, which is unchanged from the session lock's behaviour since issue 1188.

I find this acceptable as the fix of the live defect. As follow-up, not a condition of this merge: warm each tenant's
partition in the start-up tenant pass, and let a sweep that removed something re-hydrate the partition itself rather
than invalidate it (it has already walked the root), so that no request ever pays the walk.

## Question 3: is the new test a real proof?

`Thread_ManyUnrelatedUploads_ReadsOnlyTheRecordsHeldForThatFactory` stages one thousand unrelated uploads (half
PENDING for a session, half delivered tombstones) and one held recording, pays the first read, then records every
record file opened by `PendingFor` and by the hydration behind it through the new `RecordReadForTests` hook, and
asserts the only record read was the held one - for the factory with the held recording and for an empty factory.
The hook is wired into both `PendingFor` and `EnumeratePendingRecordsWithId`, so on the old walk the list holds one
thousand and one identifiers per read and the assertion cannot pass. It is a proof by construction; it does not compile
on the parent commit (the hook did not exist there), so I did not run it red, and I do not need to: the old walk reads
every record by definition.

`Thread_AfterAGatewayRestart_StillShowsTheRecordingHeldBeforeIt` builds a second store over the same root, with its own
index, and finds the recording from disk. Good: it pins the restart path. The two index tests pin the per-target filter
and the "failed hydration is not cached" rule for the listing. No test pins the per-record case of finding 1, which is
why it was missed.

## Question 4: no fallback, no stale cache

The swallow in `EnumeratePendingRecordsWithId` is removed and the failure surfaces - good. The thread raises on a null
answer rather than showing nothing - good. The cache is invalidated by every wholesale delete and kept true by the one
writer. The two places it goes stale are findings 1 and 2; there are no others I could find.

## Observations

1. The thread's bare 500 (no body) on an unreadable partition is correct in spirit but is the only error on these
   routes without the `{ error, code }` shape every other refusal uses. A caller that parses the body will see nothing
   to show. Worth one line in the route, not a condition here.
2. `RecordReadForTests` is a null-checked delegate on the hot path; its cost is nothing. It is internal and per
   instance, as its comment says, so parallel tests cannot see each other.
3. The quarantine directory under the base root is not a partition container, so hydration opens a record file there
   on every walk of the local partition and finds none. One extra round trip; harmless.

## The developer's answers (6 November 2026)

### Finding 1: accepted, fixed at the root

When the partition is first read, a record that answers `Unreadable` is no longer skipped. The index records it in its
own set of upload identifiers whose target is unknown (`DictationLockIndex.Partition.Unreadable`). The partition is
still marked as read, so nothing walks the share again. On every call, `PendingFor` re-reads exactly those identifiers,
under each one's own lock:

- **Still unreadable:** the thread does not answer "nothing held". `PendingFor` raises an error naming the upload
  identifiers, and the thread route turns that error into a 503 with the usual body (see observation 1).
- **Readable again:** what the disk says becomes the index's answer, through `RecordWritten`. If the record is PENDING
  for this factory, it is shown. The same answer reaches the session lock.
- **Gone:** the entry is removed.
- **Malformed, or belonging to another tenant:** skipped, exactly as the old directory walk skipped it, and no longer
  re-read. A corrupt record therefore cannot break every thread read for good.
- **Rewritten by a later write:** the record leaves the unreadable set, because `RecordWritten` clears it there.
  `Removed` and `Invalidate` clear the set as well.

The session lock ignores unreadable records, as it always has, so it still fails open.

Why this instead of leaving the whole partition unread: that would make a single persistently unreadable file trigger a
full walk of the share on every question, which is the 70-second defect again. The reviewer's second option keeps the
walk to one, and re-reads only what failed.

The probe is committed as `PendingFor_OneRecordUnreadableDuringHydration_IsShownOnceItCanBeReadAgain`, with one
change. While the record cannot be read, the probe expected an empty answer. That empty answer was the silent case, so
the test now expects the error. The test makes the record unreadable by holding the file open with no sharing, not by
changing its mode. That works on Windows, Linux and macOS, and the gate runs on all three.

Also added:
- `PendingFor_UnreadableRecordOfASessionDictation_IsSettledByOneReadAndStopsBeingReRead`: once readable, the record is
  read once, then never again, and the session lock is right.
- `PendingFor_MalformedRecord_IsSkippedAndDoesNotMakeTheThreadError`.
- `Thread_ARecordThatCannotBeRead_AnswersInWordsThatHeldIsUnknown`: the route answers 503 with code
  `held_recordings_unknown`, then shows the recording once it can be read.

### Finding 2: accepted as asked, written down, not closed

A paragraph beside `PendingFor` names the deploy overlap (`deploy-hosted-gateway.yml`, 144 seconds observed on 30 July
2026). It says what lags: the thread's view on the new container. It says what does not: the recording is kept, and
nothing sweeps it. It says what repairs it: the device's next retry, a restart, or a sweep that removed something. And
it says where to look when someone reports a held recording missing after a deploy.

Closing the gap needs a signal between processes, which is the design question the review put above this change. The
probe is not committed, because it would fail by design.

### Question 2 (the first read after a start): accepted, moved off the request path

Plainly: without a change, the first question to a partition after a deploy still walks the whole partition. The walk
is one directory listing plus one record read per upload. The old `PendingFor` did about three round trips per upload,
so on the live share this is likely about a third of the 70 seconds measured, which is still tens of seconds. This is
an estimate. I have not measured it on the share.

So I moved it:
- **At start-up:** the Gateway reads every tenant's dictation partition into the index in the background, through
  `GatewayHost`, the tenant pass and `LockedSessionIds`. It does this after the legacy quarantine, so the quarantine's
  invalidation comes first.
- **After a sweep:** the six-hourly tombstone pass reads the partition again right after `SweepResolvedTombstones`.
  When that sweep removed something it cleared the index, and now the timer pays for the walk instead of a request.

What is left: a thread read that arrives in the first tens of seconds after a deploy, before the warm-up has finished
that tenant, waits for that partition's read. That read holds the partition's lock, so the request waits rather than
starting a second walk. That is the only request that can still be slow, once, per process. A failure in the warm-up is
logged and leaves the partition unread, so the first question tries again.

### Observation 1: accepted

The thread route catches the `InvalidOperationException` from the held lookup, and only that call. It answers 503
with the usual error shape: code `held_recordings_unknown`, and words saying whether a spoken message is held is not
known, try again shortly.

### Observations 2 and 3: no change

Agreed as written.

### What was run

On devlinux, rebased onto `origin/main` at `42e96dabd`, with .NET 10.0.403, `DOTNET_USE_POLLING_FILE_WATCHER=1` and
`-p:EnableWindowsTargeting=true`:
- `CcDirector.Gateway.UnitTests`, filtered to `FullyQualifiedName~FactoryInbox|FullyQualifiedName~Dictation|FullyQualifiedName~Voice`:
  916 passed, 0 failed.
- `CcDirector.Gateway.Tests`, filtered to `DictationTenantIsolationTests|DictationOmittedTenantBoundaryTests`, which boot
  the real host with the start-up warm-up: 14 passed, 0 failed.

## Re-review after the answers (6 November 2026)

Re-reviewed by a second, separate review session (fleet session 27205aa7, "Factory Inbox - Reviewer - thread held lookup
re-review (4690)"), on machine devlinux. Worktree `devthrottle-fi-review-4690`, detached at commit `9779b9915`, three
commits on top of `origin/main` at `42e96dabd`. I read; I changed no product code. The tree holds nothing but this section.

**Verdict: CHANGES.** The fix of finding 1 closes the case it names (a read that throws) and does not bring back a walk of
the share. It leaves open the same shape for a read that returns the wrong bytes, which this share has been seen to do
(finding 3 below, proven by probe), and the confirming re-read of a record the index names still turns a failed read into
"nothing held" (finding 4, proven by probe). The start-up warm-up is safe, but on the hosted Gateway it reads nothing at
start-up and says it read every tenant (finding 5). The 503 is right. Finding 2 as documented is acceptable.

### Scope

Only what changed since the first review: commit `9779b9915` (seven files, 341 lines added, 28 removed) and the
developer's answers above. I read `DictationLockIndex` in full again; in `VoiceUploadStore` the new `PendingFor`, the
unreadable re-read loop, `EnumeratePendingEntries`, `EnumeratePendingRecordsWithId`, `ReadRecordFile`, `WriteRecordMarker`
and the record gate's ordering; the thread route's new catch and the `Error` helper; the host's two new call sites (the
sweep timer and the start-up task) and their placement in `StartAsync`; `TenantPass` and `PushedSessionStore.KnownTenants`;
the four new tests. I did not review the client's handling of a 503 on the thread (the first review did not review the
client either), and I measured nothing on the hosted share.

What I ran: the brief's filter on `CcDirector.Gateway.UnitTests` with `DOTNET_USE_POLLING_FILE_WATCHER=1` and
`-p:EnableWindowsTargeting=true`: 916 passed, 0 failed, 1 minute 9 seconds. And three throwaway probe tests, run once
against `9779b9915` and then deleted; their text is below so anyone can re-run them. One passed and two failed, each
exactly as predicted from reading the code.

### Question 1: is finding 1 closed on every path, without a full walk per read?

Closed for the case the finding named. A record whose read THROWS during the one-time partition read now lands in the
partition's unreadable set, the partition is still marked read (so the walk happens once), and every `PendingFor` re-reads
exactly those identifiers under their own gates. Still unreadable: the thread errors. Readable: the disk's answer goes into
the index through `RecordWritten`, for the session lock too. Gone: removed. The committed test
`PendingFor_OneRecordUnreadableDuringHydration_IsShownOnceItCanBeReadAgain` is the first review's probe with its empty
answer turned into an error, as the developer says. No path re-walks the share: `Invalidate` is still only reached from the
two wholesale sweeps and the legacy quarantine. The cost per thread read is the records named for this target plus the
records still unreadable, never the partition.

Not closed for two neighbouring paths, below.

### Finding 3 (proven): a record read as the wrong bytes is cached as "not held" for the life of the process

`ReadRecordFile` has four answers. The fix handles `Unreadable` (the open or the read threw). It does not handle a read that
SUCCEEDS and returns bytes that are not the record: `Malformed`. At the partition read a malformed record is skipped and
goes into neither set, and the partition is marked read, so it is never looked at again. In the re-read loop a malformed
answer calls `Settled`, which forgets the identifier for good. Both rest on the assumption written in the code and in the
answers: a malformed record is "a durable fact".

On this share that assumption is not safe. The store's own read-back check (`VoiceUploadStore.cs`, the comment above
`ReadBackVerdict`, lines 821 to 826) records that on 27 August 2026 the hosted share answered a read of a staged chunk with
ZERO bytes twice inside five seconds, and reported a 177 megabyte file as 0 bytes while a full read returned all of it. A
record file read as zero bytes is `Malformed` ("the file does not hold a JSON object" is not reached; `JsonDocument.Parse`
of an empty string throws, and the catch answers `Malformed`). So one empty read of a held recording's record during the
partition read hides that recording from every thread read until the next invalidation or restart, exactly the loss of
finding 1, through the other read answer. On `origin/main` the thread re-read every record on every call, so an empty read
hid the recording for one read.

What my evidence does not cover: I have not seen the share return an empty read of a `record.json`. The incident is a chunk
file and a log file. The mechanism is proven; the trigger is the share's recorded behaviour on other files of the same kind
of read.

Proof. Both probes fail on `9779b9915` at their last line with `Assert.Single() Failure: The collection was empty`:

```csharp
[Fact]
public void RecordReadAsEmptyOnceDuringHydration_StaysHiddenForTheLifeOfTheProcess()
{
    var writer = NewStore();
    var open = writer.OpenPending(null, "factory-inbox:invoices");
    var record = Path.Combine(_root, open.UploadId, "record.json");
    var good = File.ReadAllText(record);
    File.WriteAllText(record, "");            // the share answers a read with no bytes, once
    var reader = NewStore();                   // a fresh process: first read of the partition
    var whileEmpty = reader.PendingFor("factory-inbox:invoices");
    File.WriteAllText(record, good);           // the bytes are back
    var afterGood = reader.PendingFor("factory-inbox:invoices");
    var freshProcess = NewStore().PendingFor("factory-inbox:invoices");
    Assert.Empty(whileEmpty);
    Assert.Single(freshProcess);
    Assert.Single(afterGood);                  // FAILS: the empty read was cached
}

[Fact]
public void RecordUnreadableAtHydration_ThenReadAsEmptyOnReRead_IsSettledAndNeverReReadAgain()
{
    var writer = NewStore();
    var open = writer.OpenPending(null, "factory-inbox:invoices");
    var record = Path.Combine(_root, open.UploadId, "record.json");
    var good = File.ReadAllText(record);
    var reader = NewStore();
    using (new FileStream(record, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Assert.Throws<InvalidOperationException>(() => reader.PendingFor("factory-inbox:invoices"));
    File.WriteAllText(record, "");
    var whileEmpty = reader.PendingFor("factory-inbox:invoices");
    File.WriteAllText(record, good);
    var reads = new List<string>(); reader.RecordReadForTests = reads.Add;
    var afterGood = reader.PendingFor("factory-inbox:invoices");
    Assert.Empty(whileEmpty);
    Assert.Empty(reads);        // settled: never re-read
    Assert.Single(afterGood);   // FAILS
}
```

What would close it, for the developer to decide: put a malformed record in the same re-read set as an unreadable one, and
in the re-read loop SKIP it while it is still malformed rather than error, so a record that is corrupt for good costs one
small read per thread read and never breaks the thread, while a record that was read wrongly once is shown the moment it
reads right. That keeps the developer's reason for not erroring on corruption, and it keeps the walk to one: the cost is
bounded by the number of malformed records, not by the partition. The first review's words "`Malformed` should keep being
skipped as the old walk skipped it" were mine to inherit, and they were imprecise: the old walk skipped it on EACH read,
which is a re-read, not a memory.

### Finding 4 (proven, transient): the confirming re-read of a record the index names turns a failed read into "nothing held"

The first loop of `PendingFor` re-reads each upload the index names as PENDING for this target. When that read answers
`Unreadable`, the loop returns without adding it and without recording the failure, and the thread answers 200 with
nothing held. The second loop, written for this commit, errors in the same situation. The commit's own rule, in its comment
and in the thread route, is that the thread never says "nothing held" on the strength of a read that failed; the first loop
still does. It recovers on the next read, because the index entry is kept, so this is one read, not the life of the
process; the device that is polling every fifteen seconds sees the held recording vanish and come back. `origin/main`
behaved the same way for one read. The fix is two lines: an `Unreadable` answer in the first loop goes into
`stillUnreadable` as well.

Proof. This probe PASSES on `9779b9915`, which is the finding:

```csharp
[Fact]
public void RecordNamedPendingByTheIndex_WhoseConfirmingReadFails_IsAnsweredAsNothingHeld()
{
    var store = NewStore();
    var open = store.OpenPending(null, "factory-inbox:invoices");
    var record = Path.Combine(_root, open.UploadId, "record.json");
    Assert.Single(store.PendingFor("factory-inbox:invoices")); // the index names it
    IReadOnlyList<PendingUpload>? answer = null; Exception? error = null;
    using (new FileStream(record, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        error = Record.Exception(() => answer = store.PendingFor("factory-inbox:invoices"));
    Assert.Null(error);     // no 503: the thread answers normally...
    Assert.Empty(answer!);  // ...with nothing held, on the strength of a failed read
    Assert.Single(store.PendingFor("factory-inbox:invoices")); // and it is back on the next read
}
```

### Question 2: the start-up warm-up and the sweep's re-read

Safe, as far as the process is concerned:

- **It does not block the start.** The task is started with `Task.Run` at `GatewayHost.cs` line 7366, after `_app.StartAsync()`
  at line 7097: the host is already listening. It is not awaited.
- **No exception can kill the process.** The whole pass is inside a `try` that logs. Inside it, `LockedSessionIds` never
  throws (the index catches the hydration failure, logs it, leaves the partition unread and answers null, which the method
  turns into an empty set); the only other call, `ForTenant`, can throw from `EnsureRoot`'s `CreateDirectory`, and that lands
  in the same catch. The sweep timer's new `LockedSessionIds()` sits inside the timer's existing `try`.
- **Tenant scoping is by `AsyncLocal`.** `TenantPass.Current` reads the ambient scope, so the warm-up running at the same time
  as a timer pass or a request cannot see another pass's tenant. Each partition is reached through `ForTenant`, so the root
  is the tenant's own.
- **No race with writes.** Lock order is unchanged: a writer takes the record gate, writes the file (temp and move), then
  takes the partition gate in `RecordWritten`; hydration takes only the partition gate and never a record gate, so there is
  no cycle. A write that lands during the walk is seen by the walk or re-asserted after it, as the first review said. A
  request that arrives mid-walk waits on the partition gate rather than starting a second walk, as the answer says.
- **It logs per partition.** `DictationLockIndex` writes one line per partition with the pending count and the could-not-read
  count, and the failure line names the root.

### Finding 5 (proven by reading): on the hosted Gateway the warm-up reads no partition at start-up, and its log line says it read every tenant

`TenantPass.ForEachTenant` on hosted iterates `PushedSessions.KnownTenants()`, which is the key set of the push store's
in-memory map. That map is filled only by `DirectorsFor`, reached when a Director binds a stream connection; nothing loads it
from the database at start. The host's own comment beside the seam (`GatewayHost.cs` lines 2437 to 2443) says exactly this:
"KnownTenants would silently skip every tenant whose Director is offline, which is the tenant most likely to need a
scheduled job". The warm-up task runs the moment the host starts listening, before any Director can have reconnected, so on
the hosted Gateway the pass runs for zero tenants and then writes `dictation pending index read for every tenant at
start-up`. A tenant with no Director bound, which is the "owner on the phone while the Director is off" case the first review
named as the one that pays the walk on the thread read, is never in the list at any time, so it is never warmed, and the
six-hour re-read never reaches it either (its tombstone sweep never ran before this change, so for it nothing changes).

On self-host the pass is one Local pass and the warm-up does what it says.

So the developer's answer to question 2, "the Gateway reads every tenant's dictation partition into the index in the
background", is true on self-host and false on the hosted Gateway, where the seventy seconds were measured. The fix of the
live defect is unaffected: no thread read walks the share more than once per process. What remains is what the first review
already accepted as follow-up and not a merge condition: on hosted, the first thread read or session-lock question per tenant
per process still pays the walk. The harm that is new is the log line, which reports work that did not happen, and the
written answer. At least: log the number of partitions read, and correct the answer. Warming tenants that have no Director
needs a tenant list from the database, which the host's comment calls its own increment; I do not ask for it here.

### Question 3: finding 2 documented, not closed

Acceptable. The paragraph beside `PendingFor` names the overlap, the observed 144 seconds, what lags, what does not, what
repairs it and where to look. Closing it needs a cross-process signal, which the first review put above this change.

### Question 4: the 503

Right. The route catches `InvalidOperationException` from `parts.Held` only, logs the cause, and answers 503 through the
same `Error` helper every other refusal on these routes uses (`{ error, code }`), with code `held_recordings_unknown` and
words the owner can read. `parts.Held` is wired straight to `PendingFor` (`GatewayHost.cs` line 5913), whose two throws are
both `InvalidOperationException`; `ForTenant`'s refusal of an invalid tenant is an `ArgumentException` and is not caught, so
it cannot be dressed as a 503. `Thread_ARecordThatCannotBeRead_AnswersInWordsThatHeldIsUnknown` pins the status, the code,
and the recovery.

### Observations

1. A session dictation whose record was unreadable at the partition read now gets its lock the first time ANY thread read
   re-reads it (`PendingFor_UnreadableRecordOfASessionDictation_IsSettledByOneReadAndStopsBeingReRead`), but the session
   lock itself never re-reads the unreadable set. That is the lock's fail-open as it was before this pull request, and an
   improvement on it, not a regression. Noted so nobody later reads the unreadable set as a lock input.
2. The `FileShare.None` hold in the three new tests works on Linux here (916 green) because .NET maps it to an advisory lock
   that another `FileStream` in the same process honours. It is the right way to make a read fail on all three platforms.

## The developer's answers to the re-review (6 November 2026)

### Finding 3: accepted, fixed as the reviewer suggested

I could not show that a malformed read is durable, and the share's own recorded behaviour says otherwise, so a malformed
read is now treated as unsettled:

- **When the partition is first read:** a canonical upload whose record reads as `Malformed` goes into the same re-read
  set as an `Unreadable` one.
- **In the re-read loop:** a record that is still malformed is skipped for that read and kept in the set. It never makes
  the thread error, so a record that is corrupt for good costs one small read per thread read. A record that was read
  wrongly once is shown the moment it reads right.
- **What settles it:** only a successful read or a write. Another tenant's record is still settled.

Both probes are committed:
- `PendingFor_RecordReadAsEmptyOnceDuringHydration_IsShownOnceItReadsRight` is the first probe as written.
- `PendingFor_RecordUnreadableAtHydrationThenEmptyOnReRead_IsKeptForReReadAndShownOnceItReadsRight` is the second probe.
  Its `Assert.Empty(reads)` was the finding, so it now expects exactly one re-read of that record.

### Finding 4: accepted, fixed

In the first loop of `PendingFor`, a confirming read that answers `Unreadable` now goes into the same still-unreadable
list as the second loop's. The thread therefore errors (a 503 with code `held_recordings_unknown`) and never answers
"nothing held". A malformed confirming read is skipped and the index entry kept, for the reason in finding 3.

The probe is committed as `PendingFor_ConfirmingReadOfARecordTheIndexNamesFails_IsAnErrorNotNothingHeld`. Before, it
expected no error and an empty answer. Now it expects the error, and then the recording shown once the record reads.

### Finding 5: accepted, fixed, and my earlier answer was wrong

My answer to question 2 said the Gateway reads every tenant's partition at start-up. That was true on self-host and
false on the hosted Gateway, because the tenant pass there only knows tenants whose Director is connected.

The warm-up no longer uses the tenant pass. A new method, `VoiceUploadStore.WarmEveryPartition(hosted)`, finds the
partitions on disk:
- **Hosted:** every folder under `tenants/` whose name is a minted account identifier, in the exact form a partition is
  created under. Each one is read into the index. This includes tenants with no Director connected, which is the owner
  on the phone while the Director is off.
- **Self-host:** the one local partition.

It returns how many partitions were read and how many could not be read. The log line in `GatewayHost` and the store's
own log line state those counts, not an intention.

Tests:
- `WarmEveryPartition_Hosted_ReadsEveryAccountPartitionOnDisk_SoAThreadReadReadsOnlyItsOwnRecords`: two account
  partitions plus a folder that is not a tenant. It expects 2 read and 0 failed, and that a thread read afterwards reads
  only the held record.
- `WarmEveryPartition_SelfHost_ReadsTheLocalPartition`.

What stays as the reviewer described:
- **Tenants without a connected Director:** the six-hourly re-read after the sweep only reaches tenants the tenant pass
  knows. A tenant the pass does not know is not swept either, so its index is never cleared and the start-up read stands.
- **Requests during the warm-up:** a thread read that arrives before the warm-up has reached its tenant still waits for
  that one read.

### Question 3, question 4 and observations 1 and 2

Agreed as written, no change.

### What was run

On devlinux, rebased onto `origin/main`, with .NET 10.0.403, `DOTNET_USE_POLLING_FILE_WATCHER=1` and
`-p:EnableWindowsTargeting=true`:
- `CcDirector.Gateway.UnitTests`, filtered to `FullyQualifiedName~FactoryInbox|FullyQualifiedName~Dictation|FullyQualifiedName~Voice`:
  921 passed, 0 failed.

## Third look (6 November 2026)

Reviewed by a third, separate review session (fleet session e7b86379, "Factory Inbox - Reviewer - thread held lookup third
look (4690)"), on machine devlinux. Worktree `devthrottle-fi-review-4690`, detached at commit `20675fac8`. I read; I
changed no product code. The tree holds nothing but this section.

**Verdict: APPROVE.** Findings 3, 4 and 5 of the re-review are each closed by the newest commit, each with its probe
committed as a real test that fails on the previous commit's behaviour. No path brings back a walk of the share per thread
read. The start-up warm-up cannot block the start and cannot bring the process down. I found no new harm. Two observations,
neither a merge condition.

### Scope

Only the newest commit, `git show 20675fac8` (four files, 241 lines added, 20 removed), against the three findings the
re-review returned CHANGES on, plus the developer's answers to them. I read the whole of `PendingFor` and
`EnumeratePendingRecordsWithId` as they now stand, the new `WarmEveryPartition`, `ReadRecordFile`'s four answers,
`IsMintedAccountTenant` and `PartitionRootFor`, `DictationLockIndex` in full (for the gate's granularity), the host's
start-up task and its position relative to `_app.StartAsync()`, the host's construction of `_dictationUploads`, the
`TenantId` constructor, the tenant pass's hosted contract, the tombstone sweep's treatment of a malformed record, and the
five new tests with their helpers. I did not re-review anything the first two reviews settled, and I measured nothing on the
hosted share.

What I ran: the brief's filter on `CcDirector.Gateway.UnitTests` with `DOTNET_USE_POLLING_FILE_WATCHER=1` and
`-p:EnableWindowsTargeting=true`: 921 passed, 0 failed, 0 skipped, 1 minute 18 seconds, exit code 0.

### Finding 3: closed

At the partition read, `EnumeratePendingRecordsWithId` now yields a null record for a canonical upload whose read answers
`Unreadable` OR `Malformed` (the `is ... or ...` pattern ends before the `&&`, so the canonical-name check applies to both,
as the C# grammar requires and as the first new test proves by passing). The index puts both into the partition's
re-read set. In `PendingFor`'s re-read loop, `Malformed` now has its own case: return without `Settled`, so the identifier
stays in the set; `Settled` is reached only for a record that belongs to another tenant. A later read that answers
`Present` goes through `RecordWritten`, which removes it from the re-read set and puts it in the pending map, so the
recording is shown on that read and on every later one, for the session lock too. A record that reads as malformed on a
confirming read of an index-named upload (first loop) is skipped and the index entry is kept, so it is confirmed again on
the next read. Nothing errors on a malformed record, which keeps the developer's reason for not breaking the thread on
corruption.

The two probes are committed as `PendingFor_RecordReadAsEmptyOnceDuringHydration_IsShownOnceItReadsRight` (the first probe
verbatim, with its last line now the expectation) and
`PendingFor_RecordUnreadableAtHydrationThenEmptyOnReRead_IsKeptForReReadAndShownOnceItReadsRight`, whose
`Assert.Equal(new[] { open.UploadId }, reads)` pins that the record was kept in the re-read set and read exactly once on
the read that showed it. Both fail on `9779b9915` by the re-review's own run and pass here.

### Finding 4: closed

In the first loop of `PendingFor`, a confirming read that answers `Unreadable` now adds the upload to `stillUnreadable`
before any other test on the read, so the method throws `InvalidOperationException` and the thread route's existing catch
answers 503 with code `held_recordings_unknown`. The index entry is untouched, so the next read confirms it again; the
committed test `PendingFor_ConfirmingReadOfARecordTheIndexNamesFails_IsAnErrorNotNothingHeld` asserts the error type and
then the recording shown once the hold is released. The re-review's probe passed on `9779b9915` with `Assert.Null(error)`;
this test is that probe with the opposite assertion, so it would fail on the old code.

### Finding 5: closed

`WarmEveryPartition(hosted)` does not use the tenant pass. On hosted it lists the directories under
`<base>/tenants/` and keeps only names that `IsMintedAccountTenant` accepts, the same predicate `PartitionRootFor` uses to
create a partition, so the set it reads is exactly the set that can exist; a folder of any other spelling is skipped (the
test's `not-a-tenant` folder). Each is reached through `ForTenant`, and `_lockIndex.LockedSessions(store._root, ...)`
hydrates that partition under its own gate; null counts as failed, anything else as read. On self-host it reads the one
Local partition. Both the store's log line and the host's state the two counts, not an intention. The host calls it on
`_dictationUploads`, which is the base handle built on `CcStorage.DictationUploads()` with `TenantId.Local`, so
`_partitionBase` is the base root and `tenants/` resolves correctly.

Start-up safety, re-checked on this commit:

- **Does not block the start.** The task is `Task.Run` at `GatewayHost.cs` line 7366, after `_app.StartAsync()` at line
  7097, and is not awaited.
- **Cannot crash the process.** Everything in the task is inside a `try` that logs. `Directory.EnumerateDirectories` on a
  share fault, `ForTenant`'s `EnsureRoot`, and anything else that throws lands in that catch; `LockedSessions` never
  throws, because the index catches the hydration failure, leaves the partition unread and answers null.
- **Does not stall other tenants' requests.** The index's gate is per partition root, so hydrating tenant A's partition
  holds no lock tenant B's thread read needs. A thread read for tenant A that arrives mid-read waits on A's gate for that
  one read instead of starting a second walk.
- **Does not depend on a connected Director.** No tenant pass, no push store, no database: the disk is the only input.
- **Hosted never warms the Local partition.** That matches the tenant pass's own hosted contract ("never a Local pass"), so
  nothing that was warmed before is left out now.

The committed test `WarmEveryPartition_Hosted_ReadsEveryAccountPartitionOnDisk_SoAThreadReadReadsOnlyItsOwnRecords` asserts
the counts (2 read, 0 failed, the third folder skipped), that a thread read for account A afterwards reads exactly one
record out of twenty-one on disk, and that account B's session lock is already answered from the index.
`WarmEveryPartition_SelfHost_ReadsTheLocalPartition` does the same for self-host.

### No walk of the share per thread read is brought back

`Invalidate` is still reached only from the two sweeps and the legacy quarantine (`VoiceUploadStore.cs` lines 457, 552 and
940). `Hydrated` is still set once per partition per process. A thread read still reads only the uploads the index names
for its target plus the re-read set. The two new tests on `WarmEveryPartition` pin the "one record read on the request"
property with `RecordReadForTests`.

### Observations (not merge conditions)

1. **A record corrupt for good is now re-read on every thread read.** That is what the re-review asked for, and the cost is
   one small open per such record per read, bounded by how many malformed records the partition holds. The tombstone sweep
   leaves a malformed record standing on purpose (issue #2745, `VoiceUploadStore.cs` line 523), so that count can only
   grow over the life of a partition and is never trimmed. I have no evidence that any partition holds more than a handful,
   so this is not a finding; it is where to look if a thread read is ever slow again on a partition with no held
   recordings.
2. **One stale comment.** The summary on `DictationLockIndex.Settled` still says a malformed record is settled as "durable
   evidence". After this commit only another tenant's record settles that way. Words only; the code is right.
