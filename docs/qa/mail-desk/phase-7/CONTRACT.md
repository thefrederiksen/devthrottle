# Mail Desk, phase 7: the contract the Mail Desk page is built to

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), section 6 row 7, decision D21, goal 8. Written
by the phase 7 Tech Lead on 19 October 2026, before any code, so the Gateway's additions to the channel fold and the
page that renders them can be built at the same time against one shape. Where this file and the mission document
disagree, the mission document wins and this file is corrected.

Everything here is behind the Mail Desk switch, which is off for every account by default. The page lists; it never
edits (mission 5.11). It changes nothing in any mailbox and starts nothing.

## 1. Decisions this phase settles

| Point | Decision | Why |
|---|---|---|
| Which route the page reads | `GET /gateway/mail-desk/channels` (phase 1 contract, section 5), the active channels only. | The phase 1 contract names it as this page's route. `?all=true` is the command's, not the page's: a removed mailbox is not a supported address. |
| How a client knows the switch is off | That same read answering **404** - or answering the app's own web page with a 200, which only a Gateway built before phase 1 does (its page fallback answers every unmapped address; reached after a rollback while a phone still holds the newer app). That Gateway has no Mail Desk, so off is the true answer (page review, note on question 1). Any other content type is an error. The page then shows the shell's ordinary "Page not found", and the navigation entry is not drawn. | The gate answers 404 with no body so an account that is off cannot tell Mail Desk exists (`MailDeskGate`); a second, ungated "is it on?" route would tell it. The Cockpit's Mentor entry already works this way (`apps/cockpit/src/mentor/useMentorEntry.ts`). |
| A failed read that is NOT a 404 | **The entry is drawn ONLY on a positive answer** - a channels read that succeeded. A failed read (offline, a timeout, a 5xx, a broken answer) draws no entry, and is reported to the client error log and asked again on the shell's own rhythm (`retryDelayMs`); once a read has succeeded in this page load, a later failure does not take the entry away. On the page itself (reached only by its address) a failed read shows the error. **Corrected after the phase review (`reviews/PHASE-REVIEW.md`, finding F1):** the first version kept the entry on any failure, copying the Mentor entry - but then every account with the switch OFF would see a Mail Desk entry whenever the phone is offline or the Gateway is restarting, which breaks D21 ("hidden when off") and the condition of the deploy grant D16 (no other account sees a change). | Hiding a working entry during an outage costs the owner a menu row for a minute; showing it to every other account breaks the grant. |
| What the Gateway adds to the fold | The words and tones in section 2. Nothing the page shows is worked out in the client. | Repository rule 7. Today the row carries `provider: "outlook"` and decision words like `keep-for-owner`; turning those into "Outlook" and "Keep for you" in the client is exactly the ruling rule 7 forbids. |
| Where the page lives | `packages/client-core/src/maildesk/`: the read (`mailDeskClient.ts`), the page body (`MailDeskPage.tsx`), its styles (`mailDesk.css`), and the entry question (`useMailDeskEntry.ts`). Each shell supplies only its frame. | Repository rule 8, the Settings pattern: content once, layout per shell. |
| Where the entry sits | Cockpit: the WORK section of the rail, directly after Factories, label "Mail Desk", address `/mail-desk`. Phone: the navigation drawer, label "Mail Desk", address `/mail-desk`. | The Mail Desk is a built-in factory, so it sits beside Factories. The same address on both surfaces, so one link opens the page on either. |

## 2. What the Gateway adds (additive only - every existing field keeps its name and meaning)

The `--json` of `cc-devthrottle maildesk` reads these same rows; nothing it reads changes, so its shape holds (the
AXI standard). Every new field is decided in `MailDeskChannelFold` and nowhere else.

**On each channel row:**

```json
{
  "providerLabel": "Outlook",
  "machinesLabel": "devlinux or SOREN_NORTH, whichever runs the Mail Desk check",
  "signedIn":  { "state": "...", "label": "...", "tone": "good" | "bad" | "neutral" },
  "lastCheck": { "utc": "...", "outcome": "...", "label": "...", "tone": "good" | "bad" | "neutral" },
  "modeTone": "neutral" | "attention",
  "today": {
    "total": 3, "byDecision": { ... unchanged ... },
    "label": "3 emails decided today",
    "counts": [
      { "decision": "route",           "label": "Route to a seat",     "count": 1 },
      { "decision": "archive",         "label": "Archive",             "count": 2 },
      { "decision": "junk",            "label": "Junk",                "count": 0 },
      { "decision": "keep-for-owner",  "label": "Keep for you",        "count": 0 },
      { "decision": "mail-desk-agent", "label": "Hand to the Mail Desk agent", "count": 0 },
      { "decision": "failed",          "label": "Could not be screened", "count": 0 }
    ]
  }
}
```

- `providerLabel`: one constant per provider, held beside the provider list (`outlook` -> "Outlook"). A provider with
  no label is an error, never the raw word (the same rule `ModeLabels` already follows).
- `machinesLabel`: the machines that may read this mailbox, with no claim about order. One machine is its name; two
  or more read "A or B, whichever runs the Mail Desk check" ("A, B or C, ..." for three). **Corrected after the
  review of the Gateway fold (finding F1):** the first version said "A, then B if A is not answering", but a
  channel's machine list only says which machines may read it (phase 3 `MailDeskMemory.HandedTo`); which machine runs
  the check, and the order of failover, belong to the Mail Desk start (mission 5.9). So the order was a claim the
  Gateway does not decide.
- `tone` is the ONLY thing a client turns into a colour. `signedIn`: `yes` -> good, `failing` -> bad, `unknown` ->
  neutral. `lastCheck`: no check yet -> neutral; nothing-to-do, started and screened -> good; paused and skipped-running -> neutral; failed -> bad; any other outcome throws, so a new outcome cannot show as green unseen (review finding F2, `reviews/gateway-fold-answers.md`). `modeTone`:
  `observe` -> neutral, `live` -> attention (live mode acts on the mailbox; it must stand out).
- **`lastCheck` on a mailbox whose own check is failing (ruling of 20 October 2026, after the Implementation Lead read
  the screenshots).** The last check is the account's Mail Desk start's run - one run checks every mailbox, and phase
  3's kept checks cannot give a mailbox its own last-check time. But a card that says "Signed in: Failing" in red and
  "Last check: ... the mail it read was screened" in green says two contradictory things about one mailbox. So:
  - every `lastCheck.label` names the run as the account's, for example "The Mail Desk check last ran 2026-10-19
    23:29: the mail it read was screened", never words that read as this mailbox being screened;
  - when the mailbox's `signedIn.state` is `failing` (its newest kept check is a failure - a good check after it would
    have been kept, so it has not been read since), `lastCheck.label` says so with both times, for example "The Mail
    Desk check last ran 2026-10-19 23:29. This mailbox has not been read since its check failed at 2026-10-19 23:29",
    and `lastCheck.tone` is `bad`, whatever the run's outcome;
  - otherwise the tones above stand. `lastCheck.utc` and `lastCheck.outcome` stay the run's, unchanged.
- `today.label`: "No emails decided today", "1 email decided today", "N emails decided today".
- `today.counts`: every decision word in the order `MailDeskWords` lists them, zeros included, each with its label.
  `byDecision` stays as it is.

**On the `GET /channels` envelope** (beside `count` and `channels`):

```json
{ "count": 0, "channels": [], "title": "Mail Desk",
  "summary": "The mailboxes the Mail Desk screens. Each is checked every few minutes; nothing is filed while a mailbox is in observe mode.",
  "emptyLabel": "No mailbox is on the Mail Desk yet. Add one with: cc-devthrottle maildesk channel add <address> --provider outlook --machine <machine> --why \"<why>\"" }
```

The exact sentences are the Gateway Developer's to word well; the client renders whatever arrives. Two wordings were
settled while building (accepted by the Tech Lead): a decision's label reads as the decision ("Archive", not
"Archived"), because in observe mode nothing was done; and the summary says "every few minutes", because the interval
lives in the factory definition and can change.

**Tests** (in `src/CcDirector.Gateway.UnitTests/MailDesk/`): every tone and every label case above, one row per
machine count (one, two, three), a provider with no label throwing, `today.counts` order and zeros, and the envelope's
three sentences present on an empty list and on a full one. The existing `maildesk` command tests still pass
unchanged.

## 3. The page

One page, `MailDeskPage`, in client-core. Each shell mounts it at `/mail-desk`: the Cockpit inside its pane frame,
the phone inside `.screen` with its app bar and Back link (as `apps/mobile/src/pages/Settings.tsx` does).

- **Heading and summary:** the envelope's `title` and `summary`, verbatim.
- **One card per channel, in the Gateway's order.** Each card shows, verbatim:
  the address (never cut short - it may wrap), `providerLabel`, `modeLabel` (with `modeTone`), `machinesLabel`,
  `signedIn.label` (with its tone), `lastCheck.label` (with its tone), `today.label`, and every entry of
  `today.counts` as label and number. Nothing else is derived; no field is hidden because its value "looks" empty.
- **No channels:** the envelope's `emptyLabel`.
- **While loading:** a loading line at once (repository rule 1: feedback under 100 milliseconds).
- **A failed read (not 404):** the shell's error banner with the reason, reported once to the client error log.
- **404 (switch off):** the shell's ordinary "Page not found", exactly as an unknown address looks.
- **No edit controls of any kind.** No buttons that change a channel, a mode or a list.
- **Styles:** `docs/VisualStyle.md`'s rule for the web shells (its opening note): use the tokens each shell already
  defines (`apps/cockpit/src/styles.css`, `apps/mobile/src/styles.css`) and match the nearest component family - the
  shared Settings cards (`packages/client-core/src/settings/settings.css`) are the nearest. Do not port the guide's
  desktop hex values and do not invent a token. Each tone maps to an existing token (good, bad, attention, neutral),
  and it works in both the light and the dark variants. The phone re-tunes layout only, under `.screen`, the way it
  re-tunes the shared Settings cards: one column, full-width cards.

**Tests (vitest):** the page renders every field of a fixture verbatim (a fixture whose words are deliberately odd,
so a client that invents words fails); the empty sentence; the loading line; an error read shows the banner and
reports once; a 404 renders the not-found page; no `button` or `input` in the page; the entry question shows ONLY on a successful read - it stays hidden on 404 and on every other failure kind (offline, timeout, 5xx, a broken answer), one test each -
shows on success, and shows (and asks again) on any other failure; the Cockpit rail and the phone drawer draw the
entry only when the question says so; both shells' route tables carry `/mail-desk`.

## 4. Proof (section 7 of the mission, for this phase)

- The client tests above, and the Gateway tests in section 2.
- A QA report of full-page screenshots, desktop width (1440) and phone width (390), against a throwaway Gateway on
  devlinux with test data, using the `web-qa` skill:
  - switch ON: at least three channels - one signed in with decisions today, one **not signed in** (its last check
    failed with a sign-in sentence), one in **observe** mode, and one in **live** mode (switched on the throwaway
    Gateway only); the rail entry and the drawer entry visible;
  - switch ON with no channels: the empty sentence;
  - switch OFF: `/mail-desk` is "Page not found" and neither the rail nor the drawer has the entry.
- Screenshots, the seeding script and the README committed under `docs/qa/mail-desk/phase-7/`.

## 5. Test data

The repository is public. Addresses are `email@devthrottle.com` and the reserved example domains only (`example.com`,
`example.org`, `example.net`, `*.example`); machine names are made up (`devlinux` is allowed, it is this machine's
name already in the mission). Never the owner's second mailbox, never a real sender, never email text.
