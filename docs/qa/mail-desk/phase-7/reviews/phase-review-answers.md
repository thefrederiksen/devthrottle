# Phase 7 - answers to the phase review

The building seat's answers to `PHASE-REVIEW.md`. The page's original Developer is closed, so the Developer of the
last-check fix (branch `mail-desk-p7-lastcheck`) answers, by the phase 7 Tech Lead's instruction of 20 October 2026.

## F1. For an account with the switch OFF, any failed read draws the "Mail Desk" entry - ACCEPTED

**Fixed in `5f2c4c47e`.** The contract was corrected first (section 1, row "A failed read that is NOT a 404", at
`426f4a49b`); the code now matches it.

- `packages/client-core/src/maildesk/useMailDeskEntry.ts` draws the entry ONLY after a channels read that succeeded.
  A failed read - offline, a timeout, a 5xx, an answer that breaks the contract - leaves the entry hidden, is reported
  to the client error log (`surface: "mail-desk-entry"`, not shown to the person) and is asked again on the shell's
  rhythm (`retryDelayMs`: 15 seconds, 30, then every 60). A successful read is settled for the page load, so no later
  failure is asked and none can take the entry away.
- The page itself, reached by its address, still shows the error, so a failure never looks like "switched off" where a
  person is looking at the Mail Desk.
- **Why accepted:** the hook cannot know the account's switch while the read fails. Showing the entry then shows the
  Mail Desk to every account that is off during every offline spell and every Gateway deploy, which breaks D21
  ("hidden when off") and the condition of the deploy grant D16. Hiding a working entry for a minute costs the owner one
  menu row; the page stays reachable by its address.
- **Tests**, one per failure kind, each proving the entry stays hidden and the failure is reported once:
  - client-core `useMailDeskEntry.test.tsx`: `useMailDeskEntry_ReadFails_<kind>_KeepsTheEntryHiddenAndReportsIt` for
    offline, a timeout, a 5xx and a broken answer; `..._AsksAgainOnTheShellsRhythmAndOffersTheEntryOnlyOnceAReadSucceeds`;
    `..._ReadSucceededThisPageLoad_ALaterFailureDoesNotTakeTheEntryAway`. The old test that asserted the entry shows
    on a failure is replaced.
  - Cockpit `AppShell.test.tsx`: "is NOT drawn, and the failure reported, on <kind>" for the same four kinds, and
    "asks again on the shell's rhythm after a failure, and draws the entry only when the Gateway then answers with its
    list".
  - Phone `NavDrawer.test.tsx`: `NavDrawer_MailDeskReadFails_<kind>_DrawsNoMailDeskEntryAndReportsIt` for the four kinds.
  - Run against the OLD hook, these fail: 5 of 11 in client-core, 4 of 41 in the Cockpit file, 4 of 8 in the phone
    file. Against the new hook all pass.

## Note 1. A green "Last check" beside a red "Signed in" - CHANGED (not by this review)

The owner side ruled on it the same day (contract section 2, ruling of 20 October 2026): every last-check label now
names the account's run ("The Mail Desk check last ran <time>: ..."), and on a mailbox whose own check is failing it
says "This mailbox has not been read since its check failed at <time>" in the bad tone. Gateway fold change and tests in
`9b76eebb2`; the retaken pictures show it.

## Note 2. A stale comment in `mailDeskClient.ts` - ACCEPTED

**Fixed in `5f2c4c47e`.** The header now says "Keep for you" (the Gateway's word) and "the sentence naming the machines
that may read a mailbox" instead of "which machine is tried first"; the `machines` field's comment no longer says
"In order of preference" but "the machines that may read the mailbox, in the order the channel lists them; no claim
about which reads first", matching the fold after the Gateway fold review's F1. Comments only; no behaviour changed.

## Note 3. One extra request per app load for every account - no change

Noted by the review as no change needed, and none is made: the answer for an account that is off is still the gate's
404, and the one log line per load is expected.
