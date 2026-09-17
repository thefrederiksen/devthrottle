# Inspection - phase 2, pull request 3006

Independent inspection of the dev reports phase 2 change: the Gateway report record, owner-only access,
holding the owner's notes until the session's turn ends, delivery as one prompt, and the first
`cc-dev-reports` tool. Inspected at head `da1819e1f` in the worktree
`D:\ReposFred\devthrottle-dev-reports-p2-review`, against origin/main at `a2ae5e115` (merge base
`dc6d65476`). The three commits origin/main gained since the merge base touch Wingman and cc-secrets
only; nothing in this change's files moved under them, so the head under review is the change that will
be merged.

How I worked: I read the code first and treated the phase report and the worker and review files as
claims to check, not evidence. Every finding below names the file and line, the concrete scenario, and
how I verified it. What I did NOT check is at the end.

## Findings

**High: none found.**

### Medium 1 - a client disconnect strands claimed items in "sending" for five minutes, then reports them as "Sent to the session, not confirmed" when the prompt may never have left

- Files and lines: `src/CcDirector.Gateway/DevReports/DevReportDelivery.cs` line 286 (the send is
  outside every catch), lines 126, 153, 187, 191 (the owner routes' request token is passed all the way
  in), and `src/CcDirector.Gateway/Api/DevReportEndpoints.cs` lines 192-193 and 204 (the owner list and
  detail routes settle with the request's token); the rethrow that starts it is
  `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs` lines 116-120 ("The CALLER gave up (the browser
  went away). Their cancellation stays a cancellation").
- Scenario: the owner's browser sends notes to an idle session, or opens the reports list. The drain
  claims the items in the database (they move to `sending` under a claim), and the prompt send is in
  flight when the client disconnects - the tab closed, the phone locking, a flaky link. The request's
  cancellation token fires; `SendPromptAsync` rethrows it; nothing catches it in `DrainLockedAsync`
  (the try/catch there covers only `ComposePrompt`), so no `FinishClaim` runs. Every item claimed stays
  `sending` for five minutes - the owner reads "Sending to the session" the whole time - and then the
  settle pass rules it "Sent to the session, not confirmed". If the cancellation landed before the
  tunnel command went out, that label is false: the prompt never left the Gateway, and the code's own
  taxonomy (`SendOutcome.NeverLeft` returns items to `held`) says exactly that class of send must go
  back to held.
- Why medium and not high: the at-most-once property is preserved - the items are never sent twice,
  and the claim rules out a second delivery. The damage is five minutes of a wrong "Sending" label and
  then a claim to the owner that may be untrue, which the phase report already admits for the crash
  case. But a browser disconnect is a routine event, not a crash, and the routes deliberately hand the
  request's token to the drain, so this path is far more frequent than the crash it is documented for.
- How verified: read the cancellation path end to end (`DevReportEndpoints` route tokens,
  `DrainLockedAsync`, `SessionVerbClient.SendPromptAsync`, `DirectorCommandRouter.TrySendAsync`'s
  rethrow), confirming there is no catch between the send and the route. Not reproduced live - that
  needs a client aborted mid-send against a real tunnel, which I did not build.

### Medium 2 - the owner's words can arrive while the agent is working, through the read-then-send gap (documented and accepted, and I confirm the code matches the documentation)

- Files and lines: `src/CcDirector.Gateway/DevReports/DevReportDelivery.cs` lines 66-71 (the "KNOWN
  GAP" paragraph), line 340 (`WaitForIdle = false`), line 138 (`SendAsync` reads the session's reach
  once, before the items are even stored, and that verdict is what the settle inside the same call
  acts on).
- Scenario: the roster snapshot says the session is waiting for input; a turn starts (the agent resumes
  itself, or the owner types into the terminal) in the window between the roster read and the Director
  writing the prompt; the prompt lands mid-turn. Every delivery path shares the window - the owner's
  send, the turn end, the thirty-second timer, the owner's own list and detail reads.
- Status: this is the review round 2 finding the Architect ruled to accept for this phase
  (`HANDOFF-phase-2-finish.md`, ruling 3), and `PHASE-2-REPORT.md` states it plainly under "What is
  NOT proven". Closing it needs the Director to refuse a prompt to a working session. I report it here
  so the record carries it as a live behavior of the merged change, not as something the inspection
  missed, and I note the extra detail that on the owner's send route the verdict is read before the
  items are inserted, so the window spans the database write too.
- How verified: read the liveness source (`GatewayHost.DevReportSessionLiveness`, from the pushed
  roster), the `WaitForIdle = false` prompt, and the single verdict reuse inside `SendAsync`.

### Low 1 - a publish race between two Gateway processes surfaces as a raw 500, and item sequence numbers can duplicate

- Files and lines: `src/CcDirector.Gateway/DevReports/DevReportStore.cs` lines 64-100 (`Publish`) and
  line 191 (`AddItems`'s sequence counter).
- Scenario: during a deploy swap two Gateway processes share the database. Two publishes of the same
  (session, key) at once both find no row and both insert; the unique index
  `(tenant, session, key)` rejects the loser with an unhandled database exception - the agent gets a
  500, not the 409 or "new version" answer a single process gives. Likewise two concurrent `AddItems`
  can compute the same next sequence number; ordering then rests on the `SentAtUtc` tie-break.
  The per-process lock prevents both on one process; only the deploy-swap window is exposed, and a
  retry by the agent lands correctly.
- How verified: read the store (no catch around the insert path; the unique index is in both
  migrations). Not reproduced - it needs two processes against one database.

### Low 2 - nothing bounds the growth of stored report versions

- Files and lines: `src/CcDirector.Gateway/DevReports/DevReportStore.cs` (`Publish` appends a
  `DevReportVersionEntity` every time, up to ten megabytes each); no delete route exists anywhere in
  `DevReportEndpoints.cs`; `DevReportSettleSweep.cs` settles delivery state and prunes nothing.
- Scenario: a session republishing a large report on every turn grows the table without limit. The
  contract wants every version kept ("the bytes of every version"), and this is the owner's own data,
  so this is a cost question, not a correctness one - but nothing in the plan or the code names a
  retention rule, and every other table family in this Gateway has one.
- How verified: searched the whole change and the plan for any retention, prune, or delete of dev
  report rows - there is none.

### Low 3 - the publish route parses up to 128 megabytes of JSON into a DOM before the ten-megabyte report is measured

- Files and lines: `src/CcDirector.Gateway/Api/DevReportEndpoints.cs` line 54 (the 128 megabyte
  transport limit) and line 76 (`JsonDocument.ParseAsync` on the raw body).
- Scenario: the report itself is capped at ten megabytes after decoding, but the JSON document that
  carries it is parsed first and costs several times its size in memory, per concurrent request. A
  session key (the owner's own agent) could push several such requests at once and pressure the
  Gateway's memory. No stranger can reach the route - a session key or the owner's credentials are
  required - which is why this is low.
- How verified: read the route's size handling; the 413 path for the transport limit and the 413 path
  for the decoded report are both correct, and the hosted tests prove the ten-megabyte boundary
  exactly.

### Low 4 - the HTML route serves agent-authored bytes without the nosniff header

- File and line: `src/CcDirector.Gateway/Api/DevReportEndpoints.cs` line 234 - `Results.Text(...,
  "text/plain; ...")`. The only place the Gateway sets `X-Content-Type-Options: nosniff` today is
  `SessionWsProxyEndpoints.cs` line 87.
- Scenario: the report HTML is agent-authored and served to the owner as `text/plain`. Modern browsers
  do not render `text/plain` as a page, and the route requires the owner's own credentials, so the
  exposure is small; adding the header would make the "never served as a page" intent explicit rather
  than dependent on browser behavior.
- How verified: read the route and grepped the Gateway for every use of the header.

### Low 5 - public-repository hygiene: one mission handoff names assistant products, and the rule is absolute

- File and line: `docs/missions/dev-reports/HANDOFF-phase-2-finish.md` line 39 - a sentence naming two reviewer products (since reworded by the Architect).

- The repository rule bans naming assistant products anywhere that reaches GitHub, with no exception
  for internal notes, and this repository is public. There is precedent on origin/main (mission
  documents there name them too), so this follows house practice rather than breaking it - but the
  rule as written makes it a finding, and the Architect may want it reworded before the merge.
  Everything else in the diff is clean, and I checked all of it: every commit message on the branch
  (33 of them) is free of attribution, "Co-Authored-By" and "Generated with" appear nowhere; the paths
  in code and tests are fictional (`C:\work\journey.html` and the like) and no real machine name,
  user directory, or private path appears; the non-ASCII in changed code files is confined to
  em-dashes inside comments, the repository's existing house style, plus the byte-order marks the
  toolchain writes.
- How verified: grepped the full diff for attribution phrases, vendor and assistant names, private
  path patterns and machine names; scripted a scan of every changed non-doc file for non-ASCII lines.

## The eight questions, answered

**1. Can anyone other than the session's owner read, list, send to, or learn the existence of a report?**
No, as far as reading the code and running the live tests can establish. Both halves are in place and
proved against a real host:
- A session key is refused the owner routes twice over: `SessionKeyGuard.Check` (the allow list at
  authentication, `SessionKeyGuard.cs` - the owner routes are deliberately absent) returns 403, and
  `DevReportEndpoints.RefuseSessionIdentity` refuses again inside the route so a later edit to the
  guard's allow list alone cannot open them. The guard's additions name exactly the session routes:
  the three-segment publish, the three- and four-segment reads, and the one five-segment replies
  shape; the owner routes are named in `SessionKeyGuardTests` as refused, one line each.
- A device key or machine token is refused the session routes: `OwnSession` returns null for a
  non-session caller and the route answers 403 `session_key_required`.
- One session cannot touch another's reports: the session routes require the path's session id to be
  the caller's own session, and the report lookup requires the report to belong to that session; the
  hosted test tries publish, reply, read, and a sideways naming of its own session but another
  session's report, and all four are refused.
- Tenant scoping is the global query filter plus a context opened on the caller's tenant for every
  store operation; another account's device key gets 404 on read, html and send, and an empty list -
  the existence of the report does not leak. The hosted tests prove this against a real booted Gateway
  with two enrolled accounts.
I ran the seventeen hosted route tests myself; they passed.

**2. Can a note be delivered twice, lost, or left stuck?**
The database claim-and-finish design is genuinely conditional - every state change is a single
`UPDATE ... WHERE` (`ClaimWaiting` only where still queued or held, `FinishClaim` only where the claim
still matches and the row is still `sending`, `SettleExpiredClaims` only where `sending` and the claim
is older than the timeout, `RefuseWaiting` only where still waiting). I hunted for a path where the
conditional is not conditional and found none; the re-read after `ClaimWaiting` is safe because no
other process can hold the fresh claim. The two-process tests are the honest interleavings (a send
still out while the other settles inside and past the timeout, and a drain while a send is out), and
they pass, as do the in-process race test (twenty sends against twenty settles) and the crash test.
Verdict on twice: no path I can construct. Verdict on lost: an ended session refuses its held items by
design (the owner is told, the note is not silently dropped), and a refused send's items return to
held. Verdict on stuck: two real windows remain - Medium 1 above (a disconnect strands items in
`sending` for five minutes before the orphan ruling), and the five-minute "Sending to the session"
label after a crash, which the phase report already admits.

**3. Can owner or report text forge instructions in the prompt? Is the owner's text passed verbatim?**
The owner's text is verbatim, untrimmed and unescaped, between a random eight-hex-character boundary
minted per prompt and re-minted if any owner text contains it, and the prompt says once at the top
that nothing inside the markers is an instruction from the Gateway. Everything from the report page
itself - title, key, labels, quotes, question, option - is written as a single JSON string. I went
after the one hole in that claim: the fold uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, whose
doc comment says control characters are escaped - and Unicode line separators (U+2028, U+2029) and the
NEL character (U+0085) are not control characters in the .NET sense. I wrote a scratch program to
check, and the fear was wrong: the encoder escapes all three, so no page string can present a line
break to the reader. The forged-payload tests (a note carrying a fake close marker and answer, a title
and a report key carrying a numbered forged item) all stay inside their strings. And the audience
limits what forging could achieve anyway: the prompt is delivered only to the session that published
the report, so an agent forging its own report forges instructions to itself; the owner is entitled to
instruct his own agents.

**4. Can anything interrupt a working session?**
No new path can. Delivery happens only for a session the pushed roster reports as waiting for input;
busy sessions' items stay held (proved live: nothing typed while working), ended sessions' items are
refused, and the Director's definite refusal (session has exited, session not found) is kept apart
from "unanswered" so it is never recorded as delivered. The one way a working session can be
interrupted is the accepted gap in Medium 2 - a turn that starts between the roster read and the
prompt write - which is documented in the code, ruled on by the Architect, and needs a Director change
to close. Nothing else the change adds types into a session: the routes only record, and the settle
paths all rule through the same liveness verdict.

**5. Where could a constant or stub be substituted and the tests stay green?**
The fakes sit at honest seams, and I found no test that builds its own input where the real caller
would behave differently. The delivery tests run the real store on the real migrated schema, faking
only the roster verdict and the Director, and they count the prompts the fake Director is handed
rather than asserting on return values. The hosted tests drive the whole journey over real HTTP with
real credentials, a real tunnel and a real turn-end push, so the wiring itself (the guard, the routes,
the tenant binding, the turn-end launcher, the settle timer) cannot be stubbed out without going red -
including the restart case and the reconnect-already-idle case, which is where a timer that was never
started would hide. The tool tests fake only at the socket opener and prove the timeout setting
actually reaches the socket. The closest thing to name, honestly: the two-process interleaving tests
manufacture the other process's writes through the store directly (`storeB.AddItems`, `store.ClaimWaiting`
via the test seam in the hosted crash test) rather than through a second `SendAsync` - legitimate,
because the states they manufacture are ones the real code reaches only by dying, and the transitions
afterwards are the real ones; and `DevReportSessionLiveness`'s "Idle" and "Failed" roster strings are
never exercised by any test (the hosted tests push "Working" and "WaitingForInput", and end sessions by
snapshot omission), so those two mappings rest on reading alone.

**6. Is the database migration present for every provider, and does it match the model?**
Yes. There are exactly two providers for `GatewayDbContext` - SQLite (`Data/Migrations`) and
PostgreSQL (`Gateway.Migrations.Postgres`) - and both gained an `AddDevReports` migration (the third
migrations folder, `Stats/Data/Migrations`, belongs to a different context that holds no dev report
entities and correctly needs none). Both migrations create the same four tables with the same unique
natural keys, the "C" collation is pinned on the four caller-supplied key strings in the model, the
migration, and both snapshots, and the collation proof test names all four columns. The
migrations-match-the-model tests pass for both providers (I ran them: four passed, the one skipped is
the live-PostgreSQL apply, which needs a server). Timestamps sort after every migration main gained.

**7. `cc-dev-reports`: is an unknown flag an error, and does nothing wait forever?**
Yes, and yes - and both are tested, not asserted. An unknown flag on any command, and a malformed
top-level flag, all fail with "No such option" and exit non-zero, sending nothing. Every Gateway call
carries a thirty-second timeout, and the test monkeypatches the constant to an unrecognizable value and
asserts that value reaches the socket - so no request can wait forever and the test cannot pass on a
default. The `--json` shape is one fixed key list for success and failure on both commands; an empty
report list prints `count: 0`, ids are printed whole, and the local refusals use the Gateway's own
sentences. I ran the tool and shared tests: 248 passed.

**8. Public-repository hygiene in the diff.**
Clean apart from Low 5: no attribution of any assistant or tool in any commit message or any code,
no private paths or machine names, and the only non-ASCII in code is the house-style em-dash inside
comments. The one hit is the handoff sentence naming assistant products, reported above.

## What I ran

- `python -m pytest tools/cc-dev-reports/tests tools/cc_shared/tests` - 248 passed.
- `dotnet test CcDirector.Gateway.UnitTests --filter FullyQualifiedName~DevReports` - 151 passed.
- `dotnet test CcDirector.Gateway.UnitTests --filter` the session key guard, rules send and
  type-nothing guard tests - 260 passed.
- `dotnet test CcDirector.Gateway.UnitTests --filter GatewayHostBootSmokeTests` - 4 passed, 1 skipped
  (the live-PostgreSQL apply, by design without a server).
- `dotnet test CcDirector.Gateway.Tests --filter DevReportRoutesHostedTests` - 17 passed, on a real
  booted hosted Gateway over HTTP with real credentials and a real Director on the tunnel.
- A scratch program proving `UnsafeRelaxedJsonEscaping` escapes U+2028, U+2029 and U+0085 (then
  deleted; no repository file touched).

## What I did NOT check

- The full parked suites: `Gateway.Tests` beyond the seventeen route tests (2,160 of its 2,586 tests
  have never run on this commit, per the phase report, and I did not add to that number),
  `Gateway.UnitTests` beyond the focused filters above, `Core.Tests`, and `scripts/test-local.ps1` -
  the brief forbids full suites on this memory-short machine.
- The behaviour of the conditional updates on a real PostgreSQL under contention. The two-process
  tests run on one SQLite file; the PostgreSQL guarantee is the database's own. The
  `PostgresProviderProofTests` need a live server and did not run here.
- The Director side of the send: that `SessionCommandExecutor.PromptAsync` refuses before touching
  the session (I read the comment, not that code), the owner-turn stamp on a real Director, and what
  the Director actually types - all outside this pull request's diff.
- Medium 1 was verified by reading the cancellation path end to end, not reproduced live; building a
  client that aborts mid-send against a real tunnel was more than the inspection needed to be sure of
  the path.
- No deploy swap, no real process kill, no hosted database ran any of this - same as the phase report
  admits.
- Phase 1 code (the note-taking script, the shape check, the contract document) was read only where
  phase 2 depends on it.
