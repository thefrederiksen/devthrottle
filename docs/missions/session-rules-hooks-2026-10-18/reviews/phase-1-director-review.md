# Review - Session Rules part 2, phase 1, Developer C (the Director side)

Written 2026-10-18 by the Reviewer seat "Session Rules - Reviewer - phase 1 Director rules file and
forwarding". Reviewed: `git diff mission/session-rules-hooks-p1...HEAD` in
`devthrottle-session-rules-p1-review-director` at `1e8cc2afb` (three commits: `GitRemoteIdentity`
shared with Developer A, the Core piece, the Gateway client and host wiring). Checked against
`phase-1-plan.md` rulings P1 to P16 and wire shapes W1 to W5, and against the other two Developers'
pushed branches where a shape crosses (`origin/mission/session-rules-hooks-p1-gateway` at `d46f95f32`,
`origin/mission/session-rules-hooks-p1-hook` at `ad9ad6f8d`).

**Verdict: four findings, two of them worth fixing before the branch merges into the mission branch
(findings 1 and 2). No finding fails open on the hook path, blocks a command, or forwards a decision
for the wrong session.**

---

## Findings

### 1. A worktree whose parent checkout is gone, or any repository git refuses, silently becomes "no repo" - and stays that way for the session's life

**Where:** `src/CcDirector.Core/Rules/SessionRepoIdentity.cs` lines 48 to 52, and the cache in
`src/CcDirector.Core/Rules/SessionRulesMaintainer.cs` lines 125 to 141.

**What the code does.** `ResolveAsync` treats git exit code 128 the same as exit code 2: the empty
identity, written into the rules file as `"repo": ""`, after which only `everywhere` and `machine`
rules reach the session. The maintainer then caches that identity in `_identities` and never asks git
again for that session.

**Why that is wrong.** Exit 128 is git's code for EVERY fatal error, not only "this folder is not a
repository". I measured it with git 2.43.0 in the scratchpad:

```
--- worktree, parent intact:      https://github.com/TheFrederiksen/devthrottle.git   exit 0
--- worktree, parent deleted:     fatal: not a git repository: .../main/.git/worktrees/wt   exit 128
--- plain folder:                 fatal: not a git repository (or any of the parent directories): .git   exit 128
--- no origin:                    error: No such remote 'origin'   exit 2
```

A worktree whose main checkout was moved or deleted is a real shape in this fleet (every mission cuts
worktrees beside the shared checkout, and the owner has deleted shared checkouts). On Windows, a
checkout git refuses with "detected dubious ownership" (a clone made by another account, a network
share) is also exit 128. A corrupt `.git` file or config is exit 128. In every one of those cases the
session gets a rules file that says `"repo": ""`, every repo-scoped rule for that repository stops
reaching it, the file looks complete, the evaluation log says `no_match`, and nothing anywhere says
why. That is the shape the Tech Lead named in ruling P4 as the worst a defect takes: a silent
never-fire. The Developer's own doc comment on `ResolveAsync` says an exit code git does not use for
"no origin" or "not a repository" must be a failure "because answering the empty identity then would
quietly drop every repo rule that should reach the session" - and then the code does exactly that for
the one code git uses for everything.

The test `ResolveAsync_FolderThatIsNotAGitRepository_ReturnsTheEmptyIdentity` pins this behaviour, so
it is a decision, not an oversight; but it was made on the belief that 128 means one thing.

**What must change (the Tech Lead's call which):**
- Treat exit 2 alone as the empty identity. Treat 128 as a failure (no file, logged with git's
  standard error text, asked again on the next refresh) - the same path the code already takes for
  exit -1 and exit 1. A folder that truly is not a repository then never gets a rules file, which is
  honest: the hook logs `no_rules_file`, and the Director's log says why. Or,
- keep 128 as the empty identity only when git's standard error says "not a git repository (or any
  of the parent directories)" AND no `.git` entry exists in the folder or any ancestor; every other 128
  is a failure. This keeps a file for a plain folder, which the maintainer's doc comment wants.
- Either way: do not cache an empty identity for the session's life. A session whose folder gains an
  origin later (a clone made inside it, a `git remote add`, a repaired worktree) should pick it up on
  the next refresh. One git call per refresh for sessions with no identity is cheap at the 60 second
  cadence, and `_identities` can keep every non-empty answer exactly as it does now.

### 2. Decisions of sessions that are alive when the Director exits are never forwarded, and their directories are never cleaned up

**Where:** `src/CcDirector.Core/Rules/HookDecisionForwarder.cs` - `Start()` lines 97 to 122,
`TryOwnedDirectory` lines 199 to 204, `Dispose()` lines 380 to 398; and the host's stop order in
`src/CcDirector.ControlApi/ControlApiHost.cs` (the Gateway client is disposed near line 2100, the
forwarder near line 2165).

**What the code does.** The drop token is minted per `Session` object and never persisted
(`Session.cs` line 192). Sessions do not survive a Director restart: production restores them through
`CreateSession`, which mints a new session id and a new token, so after a restart every
`decisions/<id>.<token>/` directory from the previous run names a session that is not on the roster.
`TryOwnedDirectory` logs it once ("not on this Director's roster") and leaves it. `Dispose()` makes no
last sweep, and in the host the Gateway client is disposed before the forwarder is, so a sweep in
flight at shutdown cannot post.

**The harm.** Every Director stop loses, per live policed session (agent kind ClaudeCode), every decision dropped after the
last sweep (up to five seconds of commands at the steady cadence) plus every decision held back by a
backoff or a Gateway outage at that moment - which is exactly the case where decisions pile up. They
are never forwarded afterwards: the next Director cannot verify their token and, by this design,
rightly refuses to. Phase 1 exists to "gather real would-have-denied records before anything
enforces" (mission section 6); these are those records, and the proof's live rows will not see the
loss because the drop sits on disk looking like evidence. Secondarily, one directory per live session
per restart is left under `session-pointers/decisions/` for ever - most of them empty - and each
Director start logs one line per directory. The pointer drop box beside it purges at start precisely
because its leftovers are known to be dead; this box neither purges nor forwards.

The test `SweepAsync_SessionRemovedWhileTheGatewayIsDown_KeepsItsDecisionsForTheNextSweep` covers a
session removed while the Director keeps running; nothing covers the Director itself stopping.

**What must change:**
- A final `SweepAsync` at shutdown, after the sessions are killed and BEFORE the Gateway client is
  disposed, so the box is drained while the credential still works. The forwarder's own doc comment
  promises "a drop is deleted only when its fate is known"; at shutdown today its fate is "lost".
- At start, delete leftover directories that hold no decision file: an empty directory is not a
  record. For directories that still hold decisions, the Tech Lead should decide and write down the
  policy: keep them as evidence (and then the proof document must say that a Director stop can lose
  decisions and how many), or persist the token in `PersistedSession` so a restored directory can be
  verified and forwarded. The second is the only option that actually delivers them.

### 3. The "answer accounts for every decision" check can be satisfied by a refusal the forwarder never sent

**Where:** `src/CcDirector.Core/Rules/HookDecisionForwarder.cs` lines 310 to 313 and 322 to 326.

**What the code does.** `accountedFor` is `accepted + duplicates + refused.Count == batch.Count`,
where `refused` is every refusal in the answer, keyed by decision id, whether or not that id was in
the batch. When it is true, every batch item not in `refused` is deleted as delivered.

**The harm.** Send two decisions A and B. The Gateway answers `accepted: 1, refused: [X]` where X is
an id not in the batch (a Gateway defect, a refusal from a different batch, an unreadable item the
Gateway reports under an empty id). The sum is 2, the check passes, and both A and B are deleted
although only one was accepted. The guard exists to catch an answer that does not cover what was
sent; counting ids that were not sent lets it fail open. Count only refusals whose id is in the batch
(`batch.Count(b => refused.ContainsKey(b.DecisionId))`), and treat a refusal naming an unknown id as
the answer not accounting for the batch. One line, and a test with a stray refusal id.

### 4. The hook's evaluation log outlives its session for ever

**Where:** `src/CcDirector.Core/Rules/SessionRulesFile.cs` lines 65 to 70 (`DeleteFor`), and W4 in
the plan, which names no owner for deletion.

**What the code does.** On removal the maintainer deletes `<id>.json`. The hook writes
`<id>.eval.log` beside it (Developer B's `EvaluationLog.PathFor`), which nothing ever deletes. W2 gives
the rules file "the same life as the preamble file"; the log has no stated life at all.

**The harm.** One file per policed session, kept for ever under `session-rules/`, capped at one
megabyte each by the hook's own cut. Low, but it is the brief's question "does the rules file ...
outlive its session" answered yes for half of what lives in that folder, and the answer should be a
decision rather than an accident. It cannot be "delete on removal" without thought: the log is the
PRESENCE proof for the negative controls in the live check, and a session closed before the Tech Lead
reads its log would take the proof with it. Suggest: delete it with the rules file, and have the live
check read the log before the session is closed; or sweep logs older than some days at Director
start. The Tech Lead decides; the plan should say.

---

## Questions from the brief, answered

**Can a decision be lost?** Yes, at Director stop (finding 2), and by finding 3 under a malformed
answer. Not otherwise: a drop is deleted only after an accepted or duplicate answer, a refused one is
deleted with its reason logged, a failed post leaves everything and backs off (2x, 4x ... up to five
minutes, proven by `SweepAsync_RepeatedFailures_WaitLongerEachTimeUpToTheCeiling`), and an answer
that under-counts keeps the unaccounted drops for the next sweep, where a retry is a duplicate by
decision id.

**Duplicated?** No. A watcher sweep and a timer sweep are serialised by `_sweepGate`; a file deleted
between enumeration and read is treated as already forwarded; a retried post is a duplicate on the
Gateway side by decision id, and the Gateway branch de-duplicates per account (`SessionRuleStore.
RecordHookFirings`).

**Forwarded for the wrong session?** No. The directory name must be `<id>.<token>` with the exact
token of a session on the roster (or just removed), compared in constant time; the body's `sessionId`
must parse to the directory's id or the file is deleted; a directory with the wrong token is left in
place and never read. All three are tested.

**Is the drop token really checked?** Yes: `TryOwnedDirectory` reads the roster's `PointerDropToken`
and compares with `CryptographicOperations.FixedTimeEquals`; the directory path itself is minted by
`DecisionDirectoryFor`, which refuses anything but 32 lowercase hex characters, and the hook never
creates the directory (it refuses to run if it is missing). The one soft spot: between the
environment stamp (`SessionManager.cs` line 1164) and the roster add (line 1340) a sweep tick logs
the new directory as "not on this Director's roster"; harmless and rare, and it is re-evaluated on
every sweep, but the line is misleading.

**Does the rules file go stale, or outlive its session?** The file: no. It is rewritten at the end of
every 60 second store refresh and once on creation, compare-before-write so an unchanged answer does
not touch it, deleted on removal under a lock that stops a refresh in flight from writing it back
(tested). A failed fetch keeps the last answer and NEVER writes an empty list (tested, and the
Gateway client refuses a non-JSON, wrong-machine or wrong-repo answer as a failure). The repo identity
can go stale in the other direction (finding 1). The evaluation log outlives everything (finding 4).

**Is the repo identity right for a worktree, a checkout with no origin, odd remote spellings?** A
worktree with its parent intact: yes, git answers from the shared config (measured). No origin: exit
2, the empty identity, correct. Spellings: the Contracts `Normalise` covers the forms in P6, user and
password, port, trailing slash and `.git`, the Azure scp form, and is idempotent for its own output
(tested). A worktree whose parent is gone: wrong (finding 1). Not proven: an IPv6 host literal
(`[::1]`) re-normalises differently because the colon is read as the scp separator; exotic, noted
only.

**Gateway unreachable, error, refusals?** Unreachable or any non-2xx: a failure with the status and
the Gateway's `error` sentence; the maintainer keeps the last file or writes none; the forwarder backs
off. I checked in a scratch .NET 10 program that a 200 with a `text/html` body, an empty body and a
204 all throw `JsonException` from `ReadFromJsonAsync`, which the client catches - so the "never
throws" promise on `ISessionRuleGateway` holds for those. Refused decisions are logged with the reason
and deleted.

---

## Contract check (W1 to W5, against the other branches)

- **W1:** route `gateway/rules/effective?kind=hook&machine=..&repo=..` with the Director bearer token;
  matches `SessionRuleEndpoints.MapGet` and `ReadEffectiveQuery` on the Gateway branch, which also
  accepts an empty `repo`. Answer compared to the question: machine case-insensitively (P5), repo
  ordinally. Field names in `HookRuleEntry` match `ProjectEffective` exactly.
- **W2:** `SessionRulesFile.Render` is pinned field-by-field and in order by
  `Render_RulesFile_HasExactlyTheW2Shape`; `generatedUtc` is seven fractional digits with Z. Developer
  B's `RulesFile` reader names the same fields. Path `<root>/session-rules/<id>.json`, environment
  variable `CC_SESSION_RULES_FILE`, atomic write, same names on both sides.
- **W3:** directory `<pointers>/decisions/<id>.<token>` created at launch, `CC_SESSION_DECISION_DIR`;
  the hook writes `<decisionId>.tmp` then moves to `.json`; the forwarder reads `*.json` only (tested
  with a `.tmp` left in place). The body is forwarded verbatim as a `JsonElement`, unknown fields kept
  (tested).
- **W5:** body `{ "decisions": [...] }`, answer `accepted`, `duplicates`, `refused[{decisionId,
  reason}]`; matches `ProjectHookFiringsAnswer`. Deletion policy as the plan states, except finding 2.
- **No network call and no port on the hook path:** this piece puts nothing on it; it stamps two
  environment variables and maintains a file. The launch path does not wait on the Gateway for the
  file (it is written by the maintainer afterwards, and the hook logs `no_rules_file` until then,
  which W2 allows).

## What could be substituted and the suite stay green

`MaxBackoff` (five minutes) and `SweepInterval` are referenced symbolically by the tests, so any value
passes; `docs/BackgroundWork.md` states five minutes and five seconds - a reader of the table has
no test behind those numbers. `MaxDecisionBytes` (64 KB) has no test at all: an oversized file is
never dropped in a test. Neither is a defect; both are unpinned.

## Scope

**Read:** every file in the diff (19 files), `SessionPointerWatcher`, `SessionPreambleMaintainer`,
`GitCommandRunner.RunAsync`, `BackgroundJobs` failure handling, `Session.PointerDropToken`,
`SessionManager.CreateSession` around the environment stamp, `RemoveSession`,
`RestoreEmbeddedSession` (test-only in production), the host's start and stop order; on the other
branches, the Gateway's `SessionRuleEndpoints`, `SessionRuleWire` (W1 and W5 halves) and
`SessionRuleStore.RecordHookFirings`, and the hook's `HookRunner`, `RulesFile`, `EvaluationLog`,
`Program`.

**Ran:**
- `~/.dotnet/dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Rules"`: 79 passed,
  0 failed, 0 skipped (Debug, Linux, `DOTNET_USE_POLLING_FILE_WATCHER=1`).
- `~/.dotnet/dotnet test src/CcDirector.Gateway.UnitTests --filter
  "FullyQualifiedName~GatewayClientSessionRules|FullyQualifiedName~GitRemoteIdentity"`: 39 passed,
  0 failed, 0 skipped (the first attempt failed to build because `npm ci` needs node on the shell PATH;
  with `~/.local/share/node/bin` on the PATH it built and ran).
- A scratch .NET 10 console program for `ReadFromJsonAsync` on non-JSON, empty and 204 answers (all
  `JsonException`, so the client's catch covers them).
- git 2.43.0 exit codes for the four folder shapes quoted in finding 1.

**Not reached:** no live Director and no Windows run; the file-system watcher path is not exercised by
any test (the sweep is the guarantee, and the tests drive it directly, which is the design); the full
`test-local.ps1` gate and the parked suites were not run here; nothing in this piece was watched
failing by me (the Developer's report says the tests were written first - that is self-testimony and I
did not repeat it).

---

## The Tech Lead's calls on the choices this review left open

The Developer answers every finding below this section, accepted or declined with the reason.

1. **Finding 1: the reviewer's second option.** Exit 2 is the empty identity. Exit 128 is the empty
   identity ONLY when git says "not a git repository" AND there is no `.git` entry in the folder or any
   parent - a plain folder still gets a rules file, so `everywhere` and `machine` rules still reach it.
   Every other 128 is a failure: no file, logged with git's own error text, asked again on the next
   refresh. An empty identity is never cached; non-empty ones are, as now.
2. **Finding 2:** a final sweep at shutdown, after the sessions are stopped and before the Gateway
   client is disposed. At start, delete leftover decision directories that hold no decision. For
   leftovers that do hold decisions: the Director keeps a small local record of the session id and
   drop token pairs it minted (written at launch, beside the decisions folder, removed when a
   directory is drained), and at start it forwards a leftover directory only when that record vouches
   for its name; a directory the record does not vouch for is left and logged once.
3. **Finding 3:** fix as the reviewer says - count only refusals for ids in the batch; a refusal for an
   id not sent means the answer does not account for the batch. Add the stray-id test.
4. **Finding 4:** delete `<id>.eval.log` with the rules file when the session is removed. The live check
   reads the log before the session is closed.
5. **Unpinned constants:** add a test that an oversized decision file (over `MaxDecisionBytes`) is
   refused and deleted with a logged reason.
