# Phase 7 - review of the last-check and entry fixes

Reviewer seat, Mail Desk mission (issue #4213), phase 7, for the phase 7 Tech Lead. Reviewed 20 October 2026.

## Scope

**What was reviewed:** the diff `origin/mail-desk-phase-7...origin/mail-desk-p7-lastcheck` (the worktree was checked
to be exactly `30b53c54f`, with base `426f4a49b` equal to `origin/mail-desk-phase-7`), so three commits:

- `9b76eebb2` - `MailDeskChannelFold` (the `LastRanPrefix` label, `LastCheckFor`, `MailDeskLastCheck.Tone` made
  settable) and its seven new tests in `MailDeskEndpointTests`.
- `5f2c4c47e` - `useMailDeskEntry.ts`, its tests, the Cockpit `AppShell.test.tsx` and the phone `NavDrawer.test.tsx`,
  and the comment fix in `mailDeskClient.ts`.
- `30b53c54f` - `seed.py`, the README, `reviews/phase-review-answers.md`, the four answer and seen files, and the
  pictures `desktop-on.png` and `phone-on.png`.

**Other code read to test the claims:** the rest of `MailDeskChannelFold.cs`; `MailDeskChannelCheckStore.cs` (what is
kept, and in what order); `TriggerService.cs` (the Mail Desk hand-off, `FinishScreeningAsync`, and where
`RecordChecks` is called); `TriggerStore.cs` (where `LastCheckUtc` is set); `MailDeskGate.cs` (how the tenant is
resolved); `accountStore.ts` and `accountActions.ts` (whether an account switch reloads); `api/client.ts` and
`CurrentTeam.tsx` (whether a team pick changes the account the read answers for). Contract sections 1, 2 and 3.

**What was run:**

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.MailDesk"`:
  545 passed, 0 failed, 0 skipped.
- vitest, `packages/client-core` `src/maildesk`: 3 files, 35 tests passed.
- vitest, `apps/cockpit` `src/AppShell.test.tsx`: 41 passed.
- vitest, `apps/mobile` `src/components/NavDrawer.test.tsx`: 8 passed.
- The two retaken pictures were looked at, and every address in the shots, README, answers and seed was listed.

**What was not reached:** no test was run against the old hook (that would mean changing code), so the claim in the
answers that the new tests fail against the old hook is unchecked, though each new test asserts "not drawn" where
the old one asserted "drawn". No live Gateway or browser was driven; the pictures were judged as committed. The
`-Parked` suites and the repository gate were not run.

## Verdict

**No findings.** Both fixes do what the contract says, and nothing in the scope above proves a harm.

## What was checked, and why it holds

### Can an account with the switch OFF see the entry?

No path was found.

1. **Every failure kind hides it.** The failure branch of `useMailDeskEntry` no longer calls `setOffered`, the
   initial state is `settled ?? false`, and `settled` is only set on a resolved read, to `answer.kind === "channels"`.
   A 404 (or the pre-phase-1 page answer) resolves as `off`, so it is false. Anything the client throws - offline, a
   timeout, a 5xx, a 401 or 403, a broken answer - goes through the failure branch, which leaves the entry hidden.
2. **The tests cover each kind the contract names, on all three surfaces:** offline, a timeout, a 5xx and a broken
   answer, one test each, in client-core, the Cockpit rail and the phone drawer. A 401 or 403 has no test of its
   own, but it is a `GatewayError` through the same branch as the 5xx, so no separate path is left untested.
3. **Races between reads.** An aborted read is ignored on both branches. Two mounted instances can each ask, but
   both ask for the same account, so whichever settles first cannot be wrong for the other. A failing instance whose
   retry is pending while another settles goes on showing "not drawn" until its own next read. That errs toward
   hidden, never toward showing.
4. **A cached answer across an account switch.** `settled` lives for the page load. An account switch, sign-in and
   sign-out all reload the app (`accountActions.ts` `window.location.assign`), and a change of the active account in
   another tab reloads this one (`accountStore.ts`). A team pick does not reload, but it does not change the answer
   either: `gatewayFetch` sends no team header, and `MailDeskGate` resolves the signed-in account's tenant. So the
   cache cannot carry one account's "on" to another.
5. **No other way in.** The only places that link `/mail-desk` are the Cockpit rail and the phone drawer, and both
   are gated on `useMailDeskEntry`. The route itself can be reached only by typing its address, which the contract
   allows.

### Is the failing-mailbox sentence ever false?

No false case was found. "Has not been read since its check failed at X" rests on one store rule: a good check is
written whenever the mailbox's newest kept row (by `CheckedUtc`) is not already a good one. So if the newest kept row
is a failure, no screening has read that mailbox at any later `CheckedUtc`.

- **A failure older than the run:** "last ran 08:55 ... failed at 08:20" is true; the run did not read this mailbox.
- **A mailbox not handed to the machine that ran:** the run records no check for it, so its newest row stays the old
  failure, and "not read since" is still true.
- **A removed channel:** the page reads active channels only. The command's `?all=true` would show a removed
  failing channel in bad tone, and the sentence is still true of it.
- **A run that records no checks** (paused, skipped while running, a whole-run failure): the Mail Desk start never
  records "no new mail" (its reports are always handed to the screening, `HandToMailDesk`), and the three outcomes
  that skip the screening screen no mail. So "not read since" holds in the sense the page uses, that the Mail Desk
  read no mail from it.
- **A failure resolved by hand:** `signedIn` stays failing, as before this change, and the sentence is still true.
- **Not published, or never checked:** `LastCheckFor` returns the run's own label unchanged. This is tested.

### The pictures

`desktop-on.png` and `phone-on.png` show the new sentences exactly as `answer-on.json` holds them. `desk@example.com`
is red on both "Signed in" and "Last check". The two signed-in cards are green on both. No card now contradicts
itself. The only addresses in the shots, answers, README and seed are `email@devthrottle.com` and example domains
(`example.com`, `example.org`, `example.net`, `*.example`). The `@1.60.0` and `@4.14.0` matches are package versions,
not addresses.

## Notes (no change asked)

1. **Two times for one run.** The run's time is when the Gateway recorded the run, which comes after the screening
   finishes (`TriggerStore.RecordCheck` sets `LastCheckUtc = now`). The failure's time is the report's own check
   time. On a slow screening, one run can read "last ran 09:07. This mailbox has not been read since its check failed
   at 09:00". Both statements are true, so this is not a finding. It is noted in case the pictures are ever compared
   against a live run and the two times differ.
2. **`Tone` is now settable.** `MailDeskLastCheck.Tone` is an init property defaulted from the outcome. A future
   `with { Outcome = ... }` would carry the old tone across. Nothing does that today; `LastCheckFor` is the only
   `with`, and it sets the tone itself.
