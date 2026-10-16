# Phase 4 review: an interrupted API-mode session continues in API mode (pull request #4091)

Reviewer seat on mission #4070, opened by the Tech Lead. The seat reads and never fixes. Every finding
below states the harm it proves; the seat that built the work decides what happens to it and answers
each one on the line left for it.

## Scope

**What I read**, at commit 1f6b7a189 (the pull request's head, cut from 85263c060 on main):

- The whole diff against main: `DirectorCrashJournal.cs`, `SessionManager.cs` (`ToCrashJournalSession`),
  `InterruptedSessionDto.cs` (`CrashJournalSessionDto` and `InterruptedSessionDto`), `GatewayEndpoints.cs`
  (the `GET /interrupted` row builder and `ContinuationRequest`), and the new test class
  `ApiModeSessionSurvivesACrashTests`.
- Every reader and writer of the journal and of the two contracts, found by searching the Director, the
  Gateway, the desktop, the web client and the phone client for their names:
  - Writers: `SessionManager.BuildCrashJournalRoster` and the desktop's routine save in
    `MainWindow.axaml.cs`, both of which build rows through `ToCrashJournalSession`.
  - Director readers: `CatalogReadExecutor.InterruptedList` (serves `ListPendingRecoveries` whole),
    `DirectorInstanceLocator.ReadSessionCount` (counts rows), the desktop's start-up `DetectAndClaim`
    (logs a count), the Launcher tests that read a count.
  - Gateway readers: `GET /interrupted`, `POST /interrupted/{director}/{pid}/restore`, the two dismiss
    routes (remove rows and never read their fields).
  - Clients: `packages/client-core/src/fleet/fleetClient.ts` (`InterruptedSession`, list, dismiss and
    restore through the Gateway routes). No component in `apps/cockpit` or `apps/mobile` renders the
    interrupted list today; the only reader of the client interface is its own test. The Avalonia desktop
    has no interrupted-list screen of its own.
- The Director's side of the continuation: the create verb in `SessionCommandExecutor` (reads the mode
  with `SessionConnectionModeWords.FromWord`, refuses an unknown word with a bad-request answer),
  `SessionManager.CreateSession` and `RequireApiModeSupported`, and `Session.DeliverPrePromptAsync` (an
  API-mode session hands the context prompt to the agent's queue at once).
- The words: `ConnectionModes` in the contracts, `SessionConnectionModeWords` in Core, and how the mode is
  carried on `PersistedSession` (`SessionStateStore`), `WorkspaceSeat` (`WorkspaceDtos`) and
  `NewSessionRequest`, with the two restart paths that already hand it back (`DirectorRestore.BuildRequest`,
  `DirectorWayUp.BuildReopen`).
- `RestoreContextBuilder` (the continuation rule from issue #212), the mission's `event-model.md` (section
  3.7, resume by session id), the phase 1 integration review's "Weighed and not raised" item on the
  journal, the sibling test `FactorySeatSurvivesARestartTests.ToCrashJournalSession_TheSeatAndModelAreJournalledAndReadBack`
  in `Core.UnitTests`, and the `DirectorStreamHello` capability flags.
- The pull request description, read as the Developer's own testimony and checked against the code
  rather than believed.

**What I ran**, on devlinux (Linux), from this checkout:

    export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
    dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true \
      --filter "FullyQualifiedName~ApiModeSessionSurvivesACrashTests|FullyQualifiedName~Continuation|FullyQualifiedName~Interrupted|FullyQualifiedName~CrashJournal|FullyQualifiedName~Restore"

Result: 200 passed, 0 failed, 0 skipped, 27 seconds. The four new tests are among them.

**What I could not reach:**

- No Director was crashed and no continuation was created on a live fleet. The chain is proven by the
  tests from the journal file on disk to the `NewSessionRequest` the Gateway would send, and from there
  I read the Director's create verb; I did not run it.
- I did not run `Core.UnitTests` or the default gate; the pull request description reports those counts
  and I did not check them.
- The phone and Cockpit screens were read as code, not opened.

## Verdict

**Accept, with one minor finding.** The mode travels the whole way the pull request claims: the row
builder writes `api` for an API-mode session and nothing for a terminal one; the Director serves the row
unchanged; the Gateway reads it, puts it on `InterruptedSessionDto`, and `ContinuationRequest` carries it
to the create; the Director reads it with the one translation and refuses an unknown word in words,
which the restore route returns to the caller as a failed create with the Director's sentence. A journal
with no field reads as terminal at every step, and a Director that does not send the field yet is read
exactly as it was before. Terminal sessions are unchanged in behaviour. I found no consumer that was
missed. The context prompt reaches an API-mode continuation: `DeliverPrePromptAsync` hands it to the
agent's queue rather than waiting for terminal output.

On the six things the Tech Lead named:

1. **Every consumer carries the mode.** Yes. The list above is complete as far as a search of every
   project finds. The two paths that build a `NewSessionRequest` from a captured seat rather than from
   the journal (`DirectorRestore`, `DirectorWayUp`) already carry it from #4088.
2. **Absent is terminal; unknown fails loudly.** Absent is terminal at `FromWord`, proven by the
   old-shape journal test. An unknown word is refused by the Director's create verb with a bad-request
   answer naming the word, and the restore route returns a 502 whose text carries that sentence. The
   Gateway itself never judges the word, which is the right place for it: only the Director knows which
   modes it speaks. The journal is written only by `ToWord`, so an unknown word can only come from a
   hand-edited file or a newer Director's journal read by an older one.
3. **Terminal sessions unchanged.** In behaviour, yes. On disk a terminal row now carries the field with
   a null value, because the journal's serializer does not drop nulls. See "Weighed and not raised".
4. **Mixed versions, Gateway side.** A Director that does not send the field yields null on the
   contract, and null is terminal. Safe. The other direction is weighed below.
5. **Tests.** Fakes only: a fake clock on the API-mode backend, a process factory that throws if called,
   a terminal stand-in that runs nothing, a scratch folder under the temporary path that is deleted on
   dispose. Named `MethodName_Scenario_ExpectedResult`. Each proves what it names, and the end-to-end
   chain is the real one (the real journal file, the real Director serializer, the real Gateway reader).
   One gap in where the proof lives is finding 1.
6. **Rules.** Logging: the two changed methods are pure mappers with no entry-point work, like their
   neighbours, and the restore route logs its steps. No fallback: nothing guesses a mode. The client is
   dumb: the field is on the contract, no client reads it, and no display decision was added anywhere.
   Plain English: comments and the description read clean.

## Findings

### 1. The only test of the Core row builder carrying the mode lives in a suite the default gate does not run (minor)

**Where:** `src/CcDirector.Gateway.UnitTests/ApiMode/ApiModeSessionSurvivesACrashTests.cs`, all four tests;
`src/CcDirector.Core/Sessions/SessionManager.cs`, `ToCrashJournalSession`.

**What I saw:** the change to `ToCrashJournalSession` is in Core, and its proof is in `Gateway.UnitTests`.
The project instructions say `Gateway.UnitTests` is parked out of the default run of
`scripts\test-local.ps1`; it runs only with `-Parked`. The phase 5 sibling that proved the seat and the
model reach the journal row sits in `Core.UnitTests`
(`FactorySeatSurvivesARestartTests.ToCrashJournalSession_TheSeatAndModelAreJournalledAndReadBack`), which
the default gate does run. Nothing in `Core.UnitTests` names `ConnectionMode` on a journal row.

**The harm:** a later change to `ToCrashJournalSession` in Core that drops the mode, or a change to the
journal's serializer, leaves the default gate green. The pull request's own sentence "with the row's
mode taken out of `ToCrashJournalSession`, the two API-mode tests fail" is true only for a seat that
runs the parked suite. The instructions do tell a seat that touched the Gateway to run `-Parked`; a seat
that touches only Core is not told to, and Core is where the row is built.

**Why it must change, or be answered:** the mission's law is proof committed beside the code. The
end-to-end chain belongs where it is, because it needs `GatewayEndpoints.ContinuationRequest`. But the
Core half of the claim, that a session in API mode writes `api` and a terminal session writes nothing,
is one short test next to its phase 5 sibling. The Developer may equally answer that the end-to-end test
is deliberately kept in one place and the gate rule covers it; that is a decision for the building seat,
not for this one.

Developer's answer:

## Weighed and not raised

Each of these was looked at and judged not to prove a harm that must change in this pull request. They
are written down so the next seat knows they were seen.

- **A continuation sent to an older Director becomes a terminal session without a word.** The restore
  route lets the caller name a target Director (`toDirectorId`). A Director from before #4088 does not
  know `ConnectionMode`; its reader drops the unknown property and starts a terminal Pi, which is exactly
  the silent change of mode this pull request closes, now only in a mixed-version fleet. The route already
  refuses a continuation onto a Director that cannot build a kit (`SpawnFactory.DirectorCannotBuildKit`,
  fed by a `DirectorStreamHello` flag), so the pattern for a capability check at this spot exists, and
  `DirectorStreamHello` carries no API-mode flag today. Not raised because the outcome is the behaviour
  of main before this change, the row of the new session says `terminal` honestly, the usual target is
  the reporting Director on the same machine, and adding a Hello capability is wider than this phase.
  The Tech Lead may want it on the mission's list.
- **"A terminal session's row stays exactly what it was before the field existed."** The comment in
  `ToCrashJournalSession` and the test's assertion both speak of the reader's view, where the value is
  null. On disk the journal's serializer (`DirectorCrashJournal.JsonOptions`: indented, case-insensitive,
  no null-dropping) writes `"ConnectionMode": null` into every terminal row. No reader is harmed: an older
  Director on the same machine ignores a property it does not know, and a newer one reads null as
  terminal. The sentence is a little stronger than the file.
- **The continuation prompt tells a Pi session to look for its transcript under another agent's
  projects folder.** `RestoreContextBuilder` writes a prior-conversation line that names
  `~/.claude/projects/` whenever the row carries a conversation id. Pi's transcripts live under
  `~/.pi/agent/sessions/` (`PiSessionLocator`). This is wrong for a terminal Pi as much as for an API-mode
  one, so it is not a harm this pull request introduces, and the Tech Lead ruled that the continuation
  rule (a continuation, never a resume, issue #212) stands unless API mode specifically is harmed. It is
  not: the API-mode continuation is a fresh conversation like every other agent's. Noted for whoever owns
  `RestoreContextBuilder`.
- **The Smart Restart reopen resumes; the crash continuation does not.** `DirectorWayUp.BuildReopen`
  sets `ResumeSessionId` from the captured seat and `ConnectionMode` beside it, so an API-mode seat reopens
  on its own conversation after a planned restart, while a crash continuation starts a new one. That is
  the #212 rule applied to one path and not the other, by design, and the Tech Lead has kept it. No
  API-mode harm found: the prompt reaches the new conversation at once, and nothing in API mode depends
  on the old conversation id being reused.
- **The test's scratch folder is the bare temporary path.** The new class writes under
  `Path.GetTempPath()` with a unique name; the sibling uses a `cc-director-tests` subfolder. The gate's
  run folder sets the temporary path, so both land inside it. No harm.
- **`Agent = s.AgentKind.ToString()` and the create verb's agent name.** Unchanged by this pull request
  and proven by the existing continuation tests.
