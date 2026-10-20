# Review - phase 3 pages (Developer B)

Reviewer: "Session Rules - Reviewer - phase 3 pages", 20 October 2026. Read only; nothing was built or fixed.

Change reviewed: `3fbccbdea..47b66c1f3` on `mission/session-rules-hooks-p3-pages` (25 files, all new apart from the
rail, the drawer, the route tables and the package export list). Compared against the Gateway side at
`mission/session-rules-hooks-p3-gateway` commit `6c136491b` (`RulesPageFold.cs`, `SessionRuleEndpoints.cs`,
`SessionRuleWire.cs`).

**Verdict: one defect to fix before merge (finding 1), three low findings for the Tech Lead to rule on, and the
rest holds.** The pages are genuinely dumb, the content is one implementation in client-core, the types match
what the Gateway sends, and the error, loading and empty states are the Gateway's words.

## Findings

### 1. The preview cannot be retried after a failure: pressing Preview with the same inputs does nothing (medium)

`packages/client-core/src/rules/RulesPreview.tsx`. The read runs from an effect keyed on the three address values
(`machine`, `repo`, `command`). Submit only writes those values to the address. When they have not changed, the
effect does not run again, so nothing is read, no loading state shows, and the error banner stays.

The case: the Gateway answers 503 (or any failure) for `?machine=devbox`. The banner shows the Gateway's sentence.
The person presses Preview again, as the banner invites. Nothing happens - no read, no "Previewing...", no change
on the screen. The only way to retry is to edit a field to a different value. The list page and the rule page both
have a "Try again" button; the preview has no retry path at all.

Proven with a probe test (written, run, deleted; not part of the change): after the staged failure, a second press of
Preview with unchanged inputs left `getRulesPreview` called once, the alert still shown and no headline. This breaks
CLAUDE.md rule 1 (every user action gives immediate visual feedback) and the spirit of T9 (an error is shown and
can be acted on). The existing test `RulesPreview_GatewayError_ShowsTheGatewaysSentenceAndKeepsTheForm` stops at
the banner and never presses Preview again, which is how it stayed green.

### 2. The fixture and the client assert a wire term P2 does not give and the Gateway does not honour: a blank `overrodeLabel` when a hook firing overrode nothing (low)

`rulesClient.ts` documents `overrodeLabel` as "Blank when this firing overrode nothing"; `rulesFixture.testkit.ts`
gives the second firing `overrodeLabel: ""`, and `RulePage.test.tsx` pins "a firing that overrode nothing has a
blank label and draws no override block". P2 says no such thing. The Gateway (`RulesPageFold.OverrodeLabel`) sends
`"Overrode no rule"` with an empty `overrode` list for every hook firing that overrode nothing; only a screen firing
gets a blank.

What the screen shows with the real Gateway: `RulePage.tsx` draws the override block whenever the label is non-blank,
so a hook firing that overrode nothing shows "Overrode no rule" followed by an empty bulleted list (an empty
`<ul class="rules-references">` with its 18px indent). The words are the Gateway's, so this is not a rule 7 breach
and the screen is not wrong - but the test proves a case the Gateway never sends, and the case it does send is
untested. This is exactly the question 7 shape: the fixture agrees with the component, not with the contract.
Either the contract is amended to say blank (and the Gateway follows), or the client draws the label whenever it is
non-blank and the list only when it is non-empty, with the fixture carrying "Overrode no rule" and `[]`.

### 3. A deleted overridden rule is drawn as a link to a page that answers 404 (low)

P2: an overridden rule that has since been deleted is named "(deleted rule <id>)". The Gateway sends that sentence
with the deleted rule's id (`RulesPageFold.Reference`). `RulePage.tsx` (`Firing`) and `RulesPreview.tsx`
(`References`) link every reference to `ruleHref(other.id)`. The case: rule A fired and overrode rule B; B is later
deleted; on A's page the firing lists "(deleted rule 3f2c...)" as a link; following it opens the rule page, which
shows the Gateway's 404 sentence "there is no rule with the id 3f2c...". On the phone the same, with Back to the list.

An error is shown, not a guess, so T9 holds; but the page offered a link it knew led nowhere. The client cannot tell a
deleted reference apart without reading the sentence, which would be the client deciding from words. The fix is a
contract change (a `deleted` boolean on a reference, or an empty id) - the Tech Lead's call, not this seat's.

### 4. A rule address whose id is not a Guid shows "its answer is not valid JSON", not "no such rule" (low; reasoned, not run)

Question 5 asked. `/rules/<unknown Guid>` is fine on both shells: the Gateway's `{id:guid}/page` route answers 404
`{ error: "there is no rule with the id ..." }`, `GatewayError.from` reads `error`, and the page shows that sentence
with Try again (proven by the client tests with the staged 404 and by reading `NoSuchRule`). But `/rules/nope`
(anything that is not a Guid) matches no Gateway route, and `CockpitReactApp.MapFallback("{*path}")` serves
`index.html` with 200 for any extensionless GET - there is no exclusion for `/gateway/` paths. The client then reads
HTML, and `readJson` shows "The Gateway sent a Rules answer this app cannot read: its answer is not valid JSON." An
honest error, nothing guessed, but the sentence blames the Gateway for a typo in the address. The fix belongs on the
Gateway (a non-Guid `{id}/page` route answering the same 404 sentence) and is for seat A; noted here so the case is
not lost between the two seats.

## Mismatches with no visible harm (for the record)

- `PreviewDecision.decidingRuleId` is typed `string | null` with "Null when no rule matched". The Gateway's record is
  a non-nullable `string` and sends `""` when nothing matched. The reader accepts both, and no row's id is `""`, so
  nothing is marked. The client's comment documents a value the Gateway never sends.
- `modeTone` can only be `live` or `neutral` from the Gateway; the client accepts all five tones. Harmless.

## The sharp questions, answered

| Question | Answer |
|---|---|
| 1. Rule 7: does any client code decide what a state means? | No verdict is made on the client. Every conditional, listed: the list picks the tab the address names or the first tab, filters rows by the tab ids the Gateway put on each rule, shows the tab's `emptyText` when the filter is empty, and hides a blank `changedLabel`. The rule page hides a blank `subject` and a blank `overrodeLabel` and shows the Gateway's empty texts when `firings` or `changes` are empty. The preview marks rows whose id equals `decidingRuleId`, is in `overriddenRuleIds` or in `matchedRuleIds` - a style only (edge colour, opacity), and the words are the Gateway's `summary`. Client-written English: the page titles ("Rules", "Rule", "Preview a machine and repository"), "Loading the rules...", "Loading the rule...", "Reading the rules for <machine>...", "Try again", "Preview", "Previewing...", the form labels, "Rule groups", and the section captions "Firing record", "Changes", "Overrides", "Overridden by". None of them is a verdict, an empty state or a label about a rule. One observation: "Overrides" and "Overridden by" caption lists that the Gateway's `verdictLabel` already states in words directly above ("Overrides 1 rule: ..."), so each preview card says it twice. Not a breach; redundancy the Tech Lead may want gone. |
| 2. Rule 8: one shared component, two shells? | Yes. The list, the rule page and the preview live in `packages/client-core/src/rules/`; `apps/cockpit/src/rules/RulesView.tsx` (55 lines) and `apps/mobile/src/pages/Rules.tsx` (38 lines) supply only a frame, an error banner class and their own addresses. The phone re-tunes layout under `.screen` in `styles.css` and the Cockpit adds only the crumb. Nothing of the content is duplicated in either shell. The phone has no preview, which T8 allows. |
| 3. Do the types match P1-P3 and the Gateway? | Names match field for field. The Gateway serialises with the host defaults (no `ConfigureHttpJsonOptions`, no naming policy on these routes): camel case, `Guid` as a string, lists as arrays, and a null `decision` written as `null` (not omitted), which the client's `"decision" in source` check requires. Tab ids and the five tones are the same constants on both sides. 400 and 404 bodies are `{ error }`, which `GatewayError.from` reads as the shown sentence. The two differences are findings 2 and the `decidingRuleId` note. Caveat: this is by reading the Gateway's records, not by feeding a captured answer through the readers - see the gap below. |
| 4. Error, loading, empty states? | Loading shows at once on the list, the rule page and on preview submit (tests pin all three). A Gateway sentence is shown verbatim (`serverReason` wins in `gatewayErrorMessage`); with no sentence the status line shows ("error 500"). Nothing is swallowed: an unreadable answer is thrown as a 502 with a reason, not drawn. Empty texts are the Gateway's on every tab and section. The gap is finding 1: the preview's error cannot be retried. |
| 5. Is `tz` always sent? Unknown rule id? | `tz` is sent on both page reads, always, from `Intl.DateTimeFormat().resolvedOptions().timeZone`; the preview sends none, as P3 has none. A browser that yields no zone would send `tz=undefined`, which the Gateway refuses with its 400 sentence, shown. An unknown Guid shows the Gateway's 404 sentence on both shells; a non-Guid is finding 4. |
| 6. Rail and phone? | Rules sits directly after Workflows in the Set up group; `AppShell.test.tsx` pins the order. On the phone, Rules is in the drawer after Mail Desk, `/mobile/rules` and `/mobile/rules/:id` are in the real route table, the list has no button at all (pinned) and the rule page has none but Try again on an error. Both reachable, both read-only. |
| 7. Where could a test pass while the screen is wrong? | Finding 2 is the one place the fixture disagrees with the contract. Structurally: every page test mocks `rulesClient` whole, and the fixture is typed against the client's own interfaces, so a field the client had misnamed would be misnamed in the fixture too and every test would stay green. The names were checked here by reading the Gateway's records, not by a machine. One test that feeds a JSON answer captured from the real Gateway (seat C's rig) into `readRulesPage`, `readRulePage` and `readPreview` would close that for good. |

## Scope

Read: all 25 files of the diff; the phase 3 plan (T1-T10, P1-P3); CLAUDE.md rules 1, 7 and 8; the headings and
binding rules of `docs/VisualStyle.md` and `docs/CockpitVisualStyle.md` (not every line of the WPF sections);
`GatewayError`, `gatewayErrorMessage` and `describeAndReport` in client-core; the Workflows detail header the crumb
copies; on the Gateway branch, `RulesPageFold.cs` in full, the three routes in `SessionRuleEndpoints.cs`,
`ReadPageSource` in `SessionRuleWire.cs`, and the Cockpit fallback in `CockpitReactApp.cs`. Both shells define
every colour token the stylesheet names (`--code-bg` is missing on the phone and has a fallback).

Ran: vitest for `packages/client-core/src/rules` (4 files, 39 tests), `apps/cockpit` rules and rail (2 files, 45
tests), `apps/mobile` Rules and drawer (2 files, 11 tests) - all green; `tsc --noEmit` in all three workspaces -
clean; one probe test for finding 1, deleted after the run (the tree is clean).

Could not reach: a real Gateway, so no screenshots and no captured answer through the readers; whether a device key
is admitted to `/gateway/rules/*` (the Gateway seat's and seat C's ground); finding 4 is reasoned from the Gateway
code, not run.
