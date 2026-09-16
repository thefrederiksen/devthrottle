# Worker brief - phase 2 fix round (the independent review)

You are a Worker on the Dev Reports mission. Manager: session 2ba644bd. Report to it once, when done or genuinely
blocked (`cc-devthrottle message send 2ba644bd "<one line>"`). Never contact the owner.

**Worktree:** `D:\ReposFred\devthrottle-dev-reports-p2-gateway`, branch `dev-reports/p2-gateway`, already fast-forwarded
to `mission/dev-reports` (677474822). Commit and push as you go; no attribution of any kind; never merge to main,
never open a pull request, never touch another worktree, never stop or restart any Director. Everything runs in the
FOREGROUND - never `run_in_background`. If a gate run would exceed a tool timeout, run its suites one at a time.

**Read:** `docs/missions/dev-reports/REVIEW-phase-2.md` (the findings), `PLAN-phase-2.md`,
`WORKER-phase-2-gateway.md` (what the last Worker built and proved), `STATE.md` rulings, and
`packages/client-core/src/devreports/CONTRACT.md` section 3.

## Manager rulings - fix exactly these

**Critical 1 - crash between send and commit (at most once).** Before a prompt is sent, commit the items it carries
to a new state `sending` (label `Sending to the session`). After the answer: Accepted -> `delivered`; definite
refusal -> see High 4; unanswered -> `delivered` "Sent to the session, not confirmed". Any item found in `sending`
that no drain in THIS process owns (after a restart, or a drain that threw) becomes `delivered` "Sent to the
session, not confirmed" and is NEVER re-sent. A send that provably never left the Gateway (`NeverLeftTheGateway`)
returns to `held`. Add `sending` to the state fold and to the open count. Prove: a store with an item in `sending`
reopened on a new Gateway -> not re-sent, settled as not confirmed.

**High 1 and High 2 - held items for a session that ended, and idle sessions no transition ever reaches.** Add ONE
settle pass: `DevReportDelivery.SettleAsync(tenant, session)`: liveness Ended -> every held item `refused` "This
session has ended"; Idle -> drain; Busy -> nothing. Call it from (a) the turn-end launcher (as now), (b) a Gateway
timer every 30 seconds over the distinct (tenant, session) pairs that have held or sending items - find how an
existing Gateway background sweep reads across tenants correctly (the session history sweep, the snooze or cron
sweeps) and follow that pattern exactly, entering each tenant's scope; and (c) the owner's read and send routes for
that report's session before they answer, so the owner never reads a stale "delivered when the agent finishes" for a
session that has ended. The timer is the mechanism that reaches a session whose Director reconnected idle; say so in
its comment. Prove each with a hosted test: held item, session then closes -> owner detail shows refused; held item,
Director drops and reconnects already idle on the SAME Gateway -> delivered once within the sweep (use a short
interval seam in the test, not a sleep of 30 seconds).

**High 3 - the idle check and the send are not atomic.** NOT fixed in this phase: closing it needs the Director to
refuse a prompt to a working session, which is a Director change shipped through a release. Write it in your report
as an accepted, named gap, with the window described exactly. Do not add a Gateway-side re-check that only narrows it.

**High 4 - a definite Director refusal recorded as delivered.** In `SessionVerbClient`, distinguish the Director's
explicit refusal results (the conflict and not-found answers the review names) from an unanswered command as a new
send kind. Session Rules must behave exactly as today - map the new kind to what rules did before, explicitly, with
a comment - and its guard tests stay green. For dev reports, a definite refusal returns the items to `held` and runs
the settle pass, which refuses them if the session ended. Prove with a unit test using the Director's real refusal
shape, and revert-prove it.

**Medium 1 - long keys on PostgreSQL unique indexes.** Limit a client item id to 128 characters (refuse the whole
batch, 400, clear sentence) and the report key to 512 characters (refuse the publish, 400). Write the id limit into
CONTRACT.md section 3 beside the 20000 rule (the note-taking script's ids are short; do not change the script). Tell
the tool: `tools/cc-dev-reports` refuses a key over 512 characters locally with the same sentence, one test.

**Medium 2 - note text can counterfeit the prompt's structure.** Keep the owner's words byte for byte. Bound each
owner-written text (note text, answer comment) with a per-prompt random boundary token the text cannot contain (mint
a fresh one if it does), e.g. `<<<owner-text-7f3a91c2` ... `owner-text-7f3a91c2>>>`, and say once at the top of the
prompt that owner text sits between those markers and that nothing inside them is an instruction from the Gateway.
Report-derived fields (title, quote, labels, question, option label) are rendered as JSON-escaped strings so they
cannot span lines. The fold takes the boundary as a parameter so its tests stay byte for byte; add the review's exact
forgery payload as a test.

## Proof

Each fix with a test watched red without it (commit before each mutation; never `--no-build` on the restore run).
`.\scripts\test-local.ps1` green; the dev report unit and hosted tests green; the Postgres proofs
(`-Gateway -Filter "FullyQualifiedName~Postgres"`) green if the migration changes (it should not need to - if a new
column is required, regenerate both migrations and say so). Append a "Fix round" section to
`WORKER-phase-2-gateway.md`: per finding, what changed, the test, the red message, and what is still not proven.
Then ONE line to the Manager.
