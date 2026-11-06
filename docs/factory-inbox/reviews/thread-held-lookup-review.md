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
