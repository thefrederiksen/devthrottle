# Pricing Change, phase 3, clients: proof

Branch `pricing-change/phase-3-clients`, cut from `origin/main` at `d9ca4957a`. Written 2026-10-13 by the
Developer seat "Pricing Change - Developer - phase 3 phone screen and Director copy".

## What was built

1. **One shared notice, "Remote plan needed"**, in `packages/client-core/src/plan/`. It shows the
   Gateway's `message` as it is, one button that opens the Gateway's `subscribeUrl`, and "Try again".
   It follows the pattern of the phone's `CreditsNotice`: the Gateway's sentence unchanged, and a button
   only when the Gateway sent an address. Both shells mount it; neither has a notice of its own.
2. **At sign-in** (`POST /mobile/enroll` answers 402 with `remote_plan_required`): the shared
   `DeviceCallback` screen, which both the phone and the Cockpit mount, shows the notice. "Try again"
   starts the sign-in again.
3. **In a signed-in session** (the same 402 on any request): the refusal is recognised once, in
   `gatewayFetch`, and the notice replaces the whole app through `RemotePlanGate`, which each shell wraps
   its router in. The screens that poll are unmounted, so nothing keeps asking and no raw error shows.
   "Try again" loads the app afresh.
4. **The Director's setup wizard** (`FirstRunWizardDialog.axaml`): the hosted gateway card promises no
   trial, names no plan, no price and no number of days.

### What the phone showed before, on the 402 at enrolment

The generic error phase of `DeviceCallback`: the heading "Something went wrong", under it the bare
`error` text from the body, and one button, "Try again", which went back to sign-in and was refused
again for the same reason. It was also reported to the Gateway as a client error. The Cockpit showed
the same, because it mounts the same screen.

### Where the code is handled, and why there

The mandate asked for the one place where a response is turned into an error. There is no such place:
outside tests there are 104 calls of `GatewayError.from`, about 100 separate `new GatewayError(...)`
sites spread over the feature clients, and 9 `creditsErrorFrom` sites. The one place nearly every
request does pass is the transport choke point, `gatewayFetch` (125 call sites), so the code is
recognised there, once. It reads a copy of a 402 and nothing else, so every caller still reads the body
it always read.

### The wizard lines, old and new

Plan line on the card (was line 638):

- Old: `Included with Pro - and with the free 14-day Pro trial that new accounts get when they first connect here. No credit card.`
- New: `What the hosted gateway includes depends on your plan - see the plans at devthrottle.com/pricing.`

The name a screen reader is given for the card (was line 598), its last sentences:

- Old: `... Included with Pro, and with the free 14-day Pro trial that new accounts get when they first connect here. No credit card.`
- New: `... What the hosted gateway includes depends on your plan - see the plans at devthrottle.com/pricing.`

Opening line of the card (was line 611), changed on the Tech Lead's yes of 2026-10-13:

- Old: `We run it for you. Sign in and this machine is enrolled - phone access, voice, and the morning report work immediately.`
- New: `We run it for you. Sign in and this machine is enrolled.`

### The search for the trial promise

Pattern, case-insensitive, over `src` and `tools`, leaving out `bin`, `obj`, `node_modules` and the
Gateway projects (`src/CcDirector.Gateway*`, which belong to the other Developer):

    14[- ]day|fourteen|free trial|free pro trial|pro trial|no credit card|no card|credit card|days of pro|trial

The search was proven first: a planted file `src/CcDirector.Avalonia/__planted_hit.txt` holding
"free 14-day Pro trial. No credit card." was found, then deleted. Every hit was read.

Fixed: `FirstRunWizardDialog.axaml`, the two sentences above and the comment that explained the trial.

Left, each with its reason:

- `src/CcDirector.Core/HostedAi/HostedAiMessages.cs` line 67, "This account's trial or plan isn't
  active - see the plans at devthrottle.com/pricing." It promises no trial and names no number of days.
  Trials already running are still honoured (goal 6), so "trial or plan" stays true.
- `src/CcDirector.Core/HostedAi/HostedAiMessages.cs` line 109, "free trial" in the list of phrases
  hosted-account copy must NOT contain. It is a ban, not a promise.
- `HostedAiReadiness.cs`, `HostedAiState.cs` and `HostedAiReadinessTests.cs`: code comments about a
  trial account in a test, shown to nobody.
- Every other hit is the word "fourteen", "trial" or "14 days" in an unrelated sense (fourteen months,
  a handover named trial, "Last 14 days" in the statistics period list, "no card" for a setup screen card).

## Commands and results

All run in this worktree on 2026-10-13, Node 22.17.0, after `npm ci`.

| Command | Where | Result |
|---|---|---|
| `npm run typecheck` | repository root | exit 0, all four workspaces |
| `npm test` | `packages/client-core` | Test Files 166 passed (166), Tests 2038 passed (2038) |
| `npm test` | `apps/mobile` | Test Files 29 passed (29), Tests 138 passed (138) |
| `npm test` | `apps/cockpit` | Test Files 1 failed, 107 passed (108); Tests 1 failed, 1111 passed (1112) |
| `npm run build` | `apps/mobile` | exit 0, "built in 3.79s" |
| `npm run build` | `apps/cockpit` | exit 0, "built in 4.79s" |
| `npx eslint` on the touched files | repository root | exit 0 |
| `.\scripts\test-local.ps1` (default run) | repository root | exit 1: nine suites Completed, `CcDirector.Core.UnitTests` Failed with 1 of 1686 |

The Avalonia suite in that gate run: `CcDirector.Avalonia.Tests outcome=Completed total=1082 executed=1082`.

### The two failures, neither in files this branch touches

- **`apps/cockpit/src/licenses/fixtures.test.ts`**, "expected 28 to be 16". It counts the examples in
  `missions/license-switching/cockpit-api.md` against `fixtures.ts`. `git diff origin/main --stat --
  missions apps/cockpit/src/licenses` is empty for this branch; the document last changed in `d9ca4957a`
  (pull request 3943), which is the commit this branch was cut from. Not rerun on a clean checkout of
  main, so "it fails on main too" is an inference from that diff, not an observation.
- **`CcDirector.Core.UnitTests`, `GateTestsDoNotDependOnTheMachineTests`**. It names four Gateway test
  files as new uses of the real clock: `HostedAiGateMenuCheckRouteTests.cs`, `HostedAiGateRouteTests.cs`,
  `DictionarySuggestionDailySweepTests.cs` and `Tenancy/TenantAiGateTests.cs`. None is in this branch's
  diff; three of them are the per-tenant AI gate from phase 1.

## The new tests, one sentence each

`packages/client-core/src/plan/remotePlan.test.ts` (10 tests)

- The recogniser carries the Gateway's sentence and address unchanged.
- It does not take `hosted_subscription_required`, a 402 with no code, or an empty body for the refusal.
- It never takes a 503 `entitlement_unknown` for a plan problem, even when that body carries a sentence and an address.
- It is not the refusal without the Gateway's sentence, because the client has none of its own.
- It carries no address when the Gateway sent none, or sent one a browser should not open.
- The signal holds the refusal, tells subscribers once when the same refusal repeats, and forgets it on request.
- An ordinary request (the session list) refused with `remote_plan_required` raises the signal, and the caller still gets its error.
- The same request refused with the older 402 raises nothing and fails with the Gateway's reason as before.
- The same request answered 503 `entitlement_unknown` raises nothing.
- A 402 whose body is not JSON raises nothing and does not become a parse error.

`packages/client-core/src/plan/RemotePlanNotice.test.tsx` (4 tests)

- The notice shows the Gateway's sentence exactly, exactly one link to its address, and a working "Try again".
- With no address there is no link at all.
- The gate shows the app until the refusal arrives, then the notice instead of the app.
- "Try again" on the gate forgets the refusal and reloads.

`packages/client-core/src/auth/deviceCallbackPlanNotice.test.tsx` (6 tests; the real screen and the real enrolment code, only `fetch` replaced)

- With the phone's profile, a 402 `remote_plan_required` at enrolment shows the notice with the sentence and the link, not "Something went wrong".
- The same with the Cockpit's profile.
- "Try again" goes to the sign-in screen, and enrolment was asked exactly once.
- A 402 `hosted_subscription_required` is still "Something went wrong" with the Gateway's reason.
- A 402 with no code is still "Something went wrong".
- A 503 `entitlement_unknown` is "Something went wrong" with its own reason, and the plan sentence is not on screen.

`packages/client-core/src/api/enroll.test.ts` (2 new tests, 1 widened)

- Both enrolment requests turn the refusal into a `RemotePlanRequiredError` carrying the sentence and the address.
- The older 402 code and a 503 `entitlement_unknown` do not.
- The existing "402 with no paid entitlement" test now also checks it is not that error.

`src/CcDirector.Avalonia.Tests/GatewayHostedCardWordingTests.cs` (3 tests)

- The card's plan line has no "trial", "credit card", "no card", "free", "included with", the word "Pro", a digit or a dollar sign, and names devthrottle.com/pricing.
- The name a screen reader is given for the card passes the same check.
- The opening line is exactly "We run it for you. Sign in and this machine is enrolled." and neither it nor the screen reader name mentions the phone, voice, the morning report or "immediately".

## Each test fails without its behaviour

Each change was removed in turn, the four web test files run, and the source restored with
`git checkout`. Baseline and after-restore: 30 passed of 30.

| Removed | Failed |
|---|---|
| `gatewayFetch` no longer looks at a 402 | 1: the ordinary request raising the signal |
| Enrolment no longer recognises the refusal | 4: the enrolment error test and three callback screen tests |
| The callback screen has no plan phase | 3: the three callback screen tests |
| The recogniser ignores the status | 1: the 503 `entitlement_unknown` recogniser test |
| The recogniser ignores the code | 2: the older 402 shapes, in the recogniser and at enrolment |
| The gate always shows the app | 2: both gate tests |
| The notice writes its own sentence | 5: every test that reads the sentence off the screen |

The wizard: with the three old sentences put back and the project rebuilt, `Failed: 3, Passed: 0, Total: 3`;
restored and rebuilt, `Failed: 0, Passed: 3, Total: 3`.

Not caught by a removal: the two screen-level 503 tests still pass when the recogniser ignores the
status, because the code check behind it also rejects `entitlement_unknown`. The status check has its
own test, which does fail.

## Screenshots

Taken from the built shells (`apps/mobile/dist`, `apps/cockpit/dist`) in a headless browser, with the
Gateway replaced by a stub that answers every Gateway call with the 402 of the contract, and the
devthrottle.com sign-in page replaced by an immediate return to the callback. No emulator. The sentence
on screen begins "[stub sentence]" because it is the stub's, not the Gateway's real one.

- `phase-3-phone-sign-in-remote-plan-notice.png`: phone width (390 by 844), after pressing "Sign in".
- `phase-3-cockpit-sign-in-remote-plan-notice.png`: the Cockpit (1280 by 800), after pressing "Sign in".
- `phase-3-phone-signed-in-remote-plan-notice.png`: phone width, already signed in, first requests refused.
- `phase-3-cockpit-signed-in-remote-plan-notice.png`: the Cockpit, already signed in, first requests refused.

In the two signed-in runs the stub refused, among others, `GET /sessions`, `GET /directors`,
`GET /teams` and `GET /diag/ping`, and the notice was the only thing on screen.

## What this proof does not cover

- **The real Gateway.** Everything ran against a stub built from the contract in the mandate. If the
  Gateway's body differs (a different code, `message` missing, another field name), the notice does not
  appear and the person sees the older error. That has to be checked once the two branches are together.
- **Requests that do not pass through `gatewayFetch`.** About 100 calls use the browser's `fetch`
  directly (account, settings, schedules, skills, lists and others). A 402 on one of those alone would
  not raise the notice. In practice both shells poll through `gatewayFetch` within seconds of opening,
  which is what the signed-in screenshots show, but no test makes a direct-`fetch` call the first one.
- **A hosted-AI call refused with this code.** Those 402s go to the credits notice through
  `creditsErrorFrom`, which was not changed. It would show the body's `error` text in the credits strip.
- **The words on the notice that are the client's own:** the heading "Remote plan needed", the button
  labels "Open the pricing page" and "Try again", and the line "Subscribed already? Press Try again."
  The contract carries no heading and no labels. If the Gateway should own those too, the contract needs them.
- **A real phone, and the installed app with its service worker.** The screenshots blocked service workers.
- **The appearance in a light theme**, and the look of "Try again", which is the browser's default
  button as on the existing Sign in screen.
- **The wizard on screen.** The tests read the text of the built dialog in a headless run; no screenshot
  of the wizard was taken.

## The public documentation (added on the Delivery Lead's instruction, 2026-10-13)

The two getting-started pages described the hosted gateway card as it used to read. They now say what
the card says on this branch. The check was against the wizard text itself
(`FirstRunWizardDialog.axaml`, `GatewayHostedSummaryText` and `GatewayHostedPlanText`), not against a
committed screen dump: the dumps in `devthrottle_internal` were taken before the card changed, so they
would confirm the old words.

`docs/public/getting-started/02-installation.md`, the hosted gateway row (line 253):

- Old: `We run it. Sign in and this machine is enrolled -- phone, voice and the morning report work immediately. Part of **Pro**; see [pricing](https://devthrottle.com/pricing). New accounts start on a 14-day Pro trial, no card.`
- New: `We run it. Sign in and this machine is enrolled. What the hosted gateway includes depends on your plan; see [pricing](https://devthrottle.com/pricing).`

`docs/public/getting-started/04-setup-wizard-walkthrough.md`, the hosted gateway row (line 201): the same
old sentence without the trial, and the same new one.

`docs/public/getting-started/04-setup-wizard-walkthrough.md`, the caption under the step 2 screenshot (line 195):

- Old: `Hosted is pre-selected, marked RECOMMENDED, and priced as "Part of Pro" with no figure.`
- New: `Hosted is pre-selected and marked RECOMMENDED. Its card names no plan and no price, and points at the pricing page.`

### The search over `docs/public`

Pattern, case-insensitive, proven first on a planted file `docs/public/__planted_hit.md` that was found
and then deleted:

    14[- ]day|fourteen|free trial|free pro trial|pro trial|no credit card|no card|credit card|days of pro|trial|morning report|work immediately|included with|part of \*\*pro|part of pro

Fixed: the three passages above.

Left, each with its reason:

- `02-installation.md` line 10 ("Connect a gateway. Your agents on your phone, voice, the morning
  report.") and line 249 ("it is what makes DevThrottle reachable past this one machine: your agents on
  your phone, voice control, and the morning report"). Both say what a gateway is for, not what signing
  in gives an account, and neither names a plan or a trial. They are true of the product and silent on
  which plan includes what. If the Delivery Lead wants the plans named there, that is new copy to write.
- `release-notes/v1.8.0.md` line 28, `v1.9.7.md` line 7, `v1.9.11.md` lines 5, 6 and 14, `v2.0.4.md`
  lines 65 and 66: dated notes that say a 14-day trial existed when that release shipped. That is
  history and stays true as history.
- `release-notes/v1.8.1.md` lines 39 and 41, `v2.0.3.md` lines 42 and 43, `v2.9.2.md` line 36,
  `v2.0.4.md` line 77: dated notes about a fix to trial or morning report behaviour; no promise.
- `release-notes/v2.14.0.md` line 33, `v2.2.0.md` line 11, `v2.9.0.md` line 51: "14 days old",
  "fourteen-row rail", "no card around it"; unrelated senses of the words.

## Left open

- **The step 2 screenshot is out of date.** `docs/public/getting-started/assets/setup-02-gateway.png`
  still shows the old card with the trial sentence, directly above the corrected caption and table. It
  can only be retaken from a build of the Director with this change, on a clean machine, which this
  mandate did not do.
- **No committed screen dump shows the new card.** The next clean-machine wizard run in
  `devthrottle_internal` is what will confirm these three passages against a real screen.
- **These pages now describe a Director that is not released.** Until the next Director release, an
  installed wizard still shows the old card while the pages describe the new one.
