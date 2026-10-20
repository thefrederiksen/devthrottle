# Phase 1 Gateway review - Developer A's piece

Reviewer session a0c4b326 ("Session Rules - Reviewer - phase 1 Gateway store and routes"), 2026-10-18.
Reviewed `git diff mission/session-rules-hooks-p1...HEAD` in `../devthrottle-session-rules-p1-review-gateway`,
HEAD `afd7efce2` (four commits on top of the plan commit `88e044cd8`). The Developer's own report was not
read as evidence; every claim below was checked against the code or a run.

## Verdict

Two findings, both low severity, neither a breach of a ruling or a wire shape. Every question in the brief
has a presence answer below. The piece is fit to merge once the two findings are decided; neither needs
the merge held.

## Scope - what was read, what was run, what was not reached

Read in full: `GitRemoteIdentity.cs` (Contracts); `RuleKinds.cs`, `SessionRule.cs`, `SessionRuleStore.cs`,
`SessionRuleRecordRules.cs`, `RuleCandidateFilter.cs`, `RuleEvaluator.cs` (the pass); `SessionRuleWire.cs`,
`SessionRuleEndpoints.cs`; both entities; the model builder and the `SaveChanges` gate in
`GatewayDbContext.cs`; both migrations (SQLite and PostgreSQL, the hand-written `Up` and `Down`, not the
generated designers); both model snapshots (the changed lines); all nine new or changed test files;
`SessionKeyGuard.Check` and the authentication middleware registration in `GatewayHost.cs` for the
"who may call" question; and, on the Director branch, `HookDecisionForwarder.cs` only to learn how a
batch is sized and how a refusal or a failed post is handled.

Ran:

- `~/.dotnet/dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules|FullyQualifiedName~SessionRuleKinds"`
  on `afd7efce2`: 526 passed, 0 failed, 0 skipped (38 seconds). This includes `RulesTypeNothingGuardTests`,
  `RuleAgentContractTests`, every new test, and the SQLite migration proof.
- `SessionRuleKindsPostgresTests` against a throwaway PostgreSQL 16 container started for this review
  (the rig gate's variables set by hand, since `pwsh` is not on this machine): 1 passed, 0 skipped. I then
  queried the migrated database: `session_rules.Kind` defaults to `'screen'`, `AppliesToKind` to
  `'everywhere'`, `AppliesToMachines` and `HookMatch` are `text[]` defaulting to `'{}'`; the unique index
  `IX_session_rule_firings_tenant_id_DecisionId` on `(tenant_id, "DecisionId")` exists; the pre-existing
  rule row reads `screen | everywhere | {}` and the pre-existing firing reads `screen` with a null
  decision id. The container was removed afterwards. Nobody's recorded run covered this test before:
  the state note's "526/526" is the unit suite, which does not contain it.

Not reached: the HTTP routes were not exercised through a running host (the host-bound suite is parked);
the authorization chain was read, not run. `CcDirector.Core.UnitTests` (including `HookScriptContractTests`)
was not run - this piece changes nothing Core compiles except adding one static class to Contracts. The full
local gate was not run. The hook and Director pieces were not reviewed here beyond the one forwarder
file named above.

## The brief's questions, each with its presence answer

**Every write path to `session_rules` refuses what P4 and P7 refuse.** The one checker is
`SessionRuleRecordRules.CheckRule` (`SessionRuleRecordRules.cs:41-70`), kind-aware, and it is called from
three places: `SessionRuleStore.Create` (line 127), `SessionRuleStore.CreateHook` (line 198), and the
`SaveChanges` gate for every Added or Modified rule row (`GatewayDbContext.cs:122`). `Promote` refuses a
non-screen kind before touching state (`SessionRuleStore.cs:399-405`), and the gate refuses a hook rule in
the live state however it arrives (`SessionRuleRecordRules.cs:209-212`), so a hook rule cannot be made live
through the store, the route, or the DbSet - `HookRuleStoreTests.Promote_HookRule_IsRefusedUntilPhase4AndStaysInDryRun`
and `SaveChanges_HookRuleWrittenRoundTheStore_MeetsTheSameKindAwareGate` prove both. A repo-scoped or
machine-scoped screen rule is refused at `SessionRuleRecordRules.cs:223-227` (store and gate), proven by
`Create_ScreenRuleThatAppliesAnywhereNarrowerThanEverywhere_IsRefusedInPhase1`. A hook rule carrying any
September scope part is refused at `SessionRuleRecordRules.cs:152-156` - and strictly: an empty string is
"not null" and refused, not read as "any". On the wire, a hook body with `scope` present in any form is
refused before the store is reached (`SessionRuleWire.cs:167-170`). Raw SQL bypasses the gate; that is
pre-existing and not this piece's to close.

**Every write path to the firings table.** `CheckFiring` (`SessionRuleRecordRules.cs:270-334`) now takes
the rule's kind and refuses a firing of one kind against a rule of the other, both ways. Called from
`RecordFiring` (line 495), `RecordHookFirings` (line 312) and the gate for every Added or Modified firing
row (`GatewayDbContext.cs:160`). The entity's default kind is `screen`, so a firing written round the store
with no kind against a hook rule is refused. Proven by `RecordFiring_ScreenFiringAgainstAHookRule_IsRefused`
and `RecordHookFirings_DecisionNamingNoRuleOrAScreenRule_IsRefusedWithAReasonWhileTheRestAreAccepted`.

**The migration.** Additive only: every statement in both `Up` methods is `AddColumn` or `CreateIndex`;
nothing is renamed or dropped. Every new NOT NULL column carries a default, including the PostgreSQL array
columns (`defaultValueSql: "'{}'"`, the part the generator does not write). Both providers proven over
pre-existing rows: SQLite by the unit test, PostgreSQL by my run above. The September rule round-trips with
its trigger words intact on both. Phase 2 landing second regenerates the snapshot, as the plan says.

**W1 for every `appliesTo` kind.** `RuleAppliesTo.Reaches` (`RuleKinds.cs:122-138`): `everywhere` always;
`machine` with a list reaches a named machine, with `exceptMachines` reaches every machine not named;
`repo` needs a non-empty, equal normalised repo; `repo_on_machine` needs both. Machine names compare
`OrdinalIgnoreCase` after trimming (line 141). An empty repo reaches only `everywhere` and `machine`
rules (lines 132-133). Each case has a positive and a negative in `RuleAppliesToTests`, and
`HookRuleEffectiveAndFiringsTests.EffectiveHookRules_MachineAndRepo_ReturnsOnlyTheHookRulesThatReachThemWithTheirRanks`
proves the whole set through the store, screen rule excluded, ranks per P3. The query is run on a
context whose global filter is `TenantId == ActiveTenant` (`GatewayDbContext.cs:2582`), so another tenant's
rules are not read - see note N2 on the missing presence test. The W1 answer carries exactly the W2 item
fields (`ProjectEffective_HookRule_IsTheW1AndW2ItemShape` pins the set: `appliesTo, event, id, match, rank,
reason, result, state, tool`) and `state` reads `dry_run`.

**W5.** Another tenant's rule: the rule lookup (`SessionRuleStore.cs:280-283`) runs under the same tenant
filter, so the decision is refused as "there is no rule with the id" - the same path the
`nobody` guid test exercises. A screen rule: refused with a reason naming the kind (proven). Twice for one
`decisionId`: the duplicate check (`SessionRuleStore.cs:273-278`) runs before the gate, within one batch
and across posts, and the unique index on `(tenant_id, DecisionId)` holds it in the database; the same id
in two tenants is two firings (proven). A malformed item is refused by its id with a reason while the rest
are read (`ReadHookDecisions_UnreadableItems_AreRefusedByIdWhileTheReadableOnesAreRead`); a body with no
`decisions` list is a 400. An oversized batch: see finding 1 and note N1.

**P8.** `RuleCandidateFilter.Choose` skips any non-screen rule before the scope check, with a stated reason
(`RuleCandidateFilter.cs:112-119`), and `RuleEvaluator.EvaluateOnceAsync` returns before reading the screen
when no screen rule exists (`RuleEvaluator.cs:241-243`). The prompt is built from `candidates.Chosen` only
(line 275); `rules` is used nowhere else in the pass. Proven by
`Choose_HookRuleWhoseTextIsOnTheScreen_IsNotACandidateAndSaysWhy` and
`EvaluateAsync_AccountWithOnlyHookRules_NeverReadsTheScreenOrReachesTheModel` (zero screen reads, zero
model calls).

**Old screen-rule bodies.** A body with no `kind` is a screen rule read by the same calls as before
(`SessionRuleWire.cs:108-118`), `appliesTo` absent becomes `everywhere` (`SessionRuleStore.cs:120`).
`CreateFromBody_SeptemberScreenBodyUnchanged_StillCreatesAScreenRuleThatAppliesEverywhere` runs the
September body verbatim.

**Who can call W1 and W5.** Both routes sit under the host-wide authentication middleware, which accepts a
per-device key (a Director's own, or the user's device) and the shared token; nothing after it restricts a
device key by route, so a Director's credential reaches both. `SessionKeyGuard` is an allow list and
contains no `gateway/rules` entry, so every session key is refused on the whole surface;
`SessionKeyGuard_EffectiveRulesAndHookFiringsRoutes_AreRefusedToASessionKey` pins the two routes.

**Hook path network calls or ports.** Not this piece. The Gateway side makes no outbound call on either route.

## Findings

### Finding 1 (low) - a database refusal of one decision fails the whole W5 post instead of refusing that decision by id

`src/CcDirector.Gateway/Rules/SessionRuleRecordRules.cs:340-383` (`CheckHookFiring`) checks the decision
words, the subject length (4000, matching its column) and the decision id length (64, matching its column),
but not the lengths of `Machine` (column 256), `Repo` (512), `SessionId` (64), `Agent`, `AgentEvent` and
`AgentTool` (64 each) - `src/CcDirector.Gateway/Data/GatewayDbContext.cs:901` and `912-919`. SQLite does not
enforce a `TEXT` length, so every unit test passes; PostgreSQL does, and raises "value too long for type
character varying" from `SaveChanges` at `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:321`. That is a
`DbUpdateException`, not a `RuleRejectedException`, so neither the store (no catch around the save) nor the
route (`src/CcDirector.Gateway/Api/SessionRuleEndpoints.cs:125` catches only `RuleRejectedException`)
turns it into a refusal: the post answers a server error. Decisions earlier in the batch are already
committed (each is saved on its own); decisions after it are never looked at.

What then happens, from the Director forwarder on its branch: a failed post keeps every drop in the batch,
backs off up to five minutes, and re-posts the same batch on the next sweep - the committed ones become
duplicates (harmless), the over-long one fails again, and that drop directory never clears.

Likelihood is low: the Director fills `machine` from the machine name (at most 64 characters on Linux, 15 on
Windows), `sessionId` from a guid, and the agent words from constants; a `repo` identity longer than 512
characters is the only plausible trigger. It is a finding because the store's own contract sentence at
`SessionRuleStore.cs:250` - "Each decision is written on its own, so one refusal never costs the others" -
is what the hosted database breaks: a refusal the database makes costs all of them. Fix: check the six
lengths in `CheckHookFiring` with a stated reason (the pattern the subject already uses), or catch
`DbUpdateException` per decision in `RecordHookFirings` and refuse that decision by id.

### Finding 2 (low) - a hook create body's cooldown and daily cap are dropped silently, against the rule the same file states

`src/CcDirector.Gateway/Api/SessionRuleWire.cs:155-158` says a hook body carrying screen-rule fields "is
refused rather than stored with them quietly dropped - a caller who sent them believed they would do
something", and `RefuseScreenFieldsOnAHookRule` (lines 159-179) does that for `checks`, `scope`,
`screenDescription` and `triggerWords`. It does not look at `cooldownSeconds` or `dailyCap`; the hook branch
of `CreateFromBody` (lines 120-130) never reads them and `CreateHook` never stores them. So a hook body with
`"dailyCap": 1` is stored as a rule with no cap, and the answer says nothing. Ruling P7 says a hook rule
needs no cooldown or daily cap; it does not say one that names them should be accepted and the names
discarded. Harm: a caller who wrote a bound believes the rule carries it. Fix: two more refusals in
`RefuseScreenFieldsOnAHookRule`, with the same shape of reason as the other four.

## Notes - not findings, for the Tech Lead's judgement

- **N1. W5 holds the store's one write lock across the whole batch** (`SessionRuleStore.cs:261-327`): each
  decision costs a pooled context, two queries and a save under the lock that every screen pass (`All()`)
  and every rules route waits on. The Gateway caps nothing; the Director caps a post at 100 decisions
  (`HookDecisionForwarder.MaxBatch`) and a drop at 64 KB. A body is bounded only by the host's default
  request body limit, which is tens of thousands of small decisions. Only a Director or user credential can
  post. A Gateway-side cap equal to the Director's, refusing the remainder by id with a reason, would hold
  the contract at both ends rather than one.
- **N2. Tenant separation of the rule lookup has no presence test.** The suite proves the firings table is
  per tenant (same decision id in two accounts), and the "nobody" guid test exercises the same null path a
  filtered-out rule takes, but no test creates a hook rule in tenant A and shows W1 from tenant B does not
  list it, or W5 from tenant B is refused for it. Both are held by the global filter at
  `GatewayDbContext.cs:2582`; a test would make the claim a presence.
- **N3. `RecordHookFirings(…, DateTime nowUtc)`** never reads `nowUtc` (`SessionRuleStore.cs:252`). The
  firing's time is the hook's `occurredUtc`, as W5 says; the parameter suggests otherwise.
- **N4. `GitRemoteIdentity` on remotes that are local paths** (`GitRemoteIdentity.cs:35-50`):
  `/home/x/repo` and `file:///home/x/repo` both become `/home/x/repo`; `c:\repos\foo` becomes
  `c/\repos\foo`. Each is idempotent, so a rule pasted that way still matches the Director's computation,
  and the write gate's one-spelling check accepts it. P6 does not define these; no harm found. Also by P6,
  the path is lower-cased for every host, which is right for GitHub and wrong for a host with
  case-sensitive paths; the ruling chose it.
- **N5. `"kind": ""`** is refused as an unknown kind rather than read as `screen` (`SessionRuleWire.cs:146-153`).
  Defensible - say it or leave it out - but a client that sends the field blank gets a 400.
- **N6. A product consequence of W5 as written.** A decision whose deciding rule was deleted between the
  hook and the forward is refused ("there is no rule with the id"), and by the contract the Director then
  deletes the drop. A real decision leaves the record. This is what W5 says; it is noted because the
  record is the product.
- **N7. The Gateway records a decision as given.** It does not check that the decision's `decision` agrees
  with the deciding rule's `result`, that the rule reaches the decision's machine and repo, or that the
  overridden and matched ids name rules at all. The contract does not ask it to; the phase 3 page will
  read these columns, so a later phase may want the checks.

## Where a constant could be substituted and the suite stay green

Looked for and not found on this piece: every refusal has a positive beside it (`CreateHook_EveryAppliesToShapeRulingP4Allows_IsStored`,
`SaveChanges_WellFormedHookRuleRoundTheStore_IsAccepted`, the accepted-decision test), the W1 and W5
shapes are pinned by name, and the migration proofs read rows back through the real model rather than
asserting the migration list. The one place a wrong constant would pass is finding 1's set of column
lengths, which no test on either provider exercises.

---

## The Tech Lead's calls

The Developer answers every finding and note below, accepted or declined with the reason.

1. **Finding 1: fix** by checking every column length in `CheckHookFiring` with a stated reason, the way the
   subject already is, and add a test per length. A refusal must stay a refusal by id on PostgreSQL.
2. **Finding 2: fix** - refuse `cooldownSeconds` and `dailyCap` on a hook body, like the other four.
3. **N1: fix** - the Gateway refuses decisions past 100 in one post, each by id with a reason (the
   Director's own batch size), so the contract holds at both ends.
4. **N2: fix** - presence tests that a hook rule in one account is not listed by W1 for another, and that
   W5 from another account is refused for it.
5. **N3: fix** - remove the unused parameter.
6. **N4, N5: keep as built.** P6 chose lower case for every host; a blank `kind` is a refusal.
7. **N6, N7: keep as built for phase 1**, and recorded here for phase 3, whose page reads these columns:
   a decision whose rule was deleted is lost, and the Gateway does not cross-check a decision against its
   rule.
8. **From the hook review (its note N4): fix** - the write gate refuses a match text that is empty or
   only whitespace, with a reason and a test.
