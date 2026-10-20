# Mail Desk, phase 7: review of the finished phase as a whole

Reviewer seat, Mail Desk mission (issue #4213), phase 7, pull request 4279. Written 20 October 2026 for the Mail Desk
Implementation Lead.

## Scope

**What was reviewed:** the whole phase, `origin/main` (`66658a098`) to `mail-desk-phase-7` at `a9b863bcc`, in the
worktree `devthrottle-il-review4279`, detached at that head after a fetch. 57 files: the Gateway fold and route, the
client-core page, read and entry question, both shells' frames, routes, navigation entries and styles, the phone's
not-found refactor, every test, the README, the contract, the two earlier task reviews, the seed and capture scripts,
the committed answer and screen files, and all sixteen pictures.

**Read against:** from `origin/main`, `docs/missions/mail-desk-2026-10-18/MISSION.md` (goal 8, D16, D21, sections 5.10
and 5.11, section 6 row 7) and `HANDOVER.md` section 4; repository rules 7 and 8; the precedent
`apps/cockpit/src/mentor/useMentorEntry.ts`; `MailDeskGate.cs`; `TriggerRunOutcome`; the shared `gatewayFetch`.

**What was run, on this head, all green:**

| Command | Result |
|---|---|
| `npx vitest run --minWorkers=1 --maxWorkers=2` in `packages/client-core` | 2,297 of 2,297 passed (182 files) |
| the same in `apps/cockpit` | 1,334 of 1,334 passed (126 files) |
| the same in `apps/mobile` | 183 of 183 passed (37 files) |
| `npx tsc --noEmit` in all three | clean |
| `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk"` (polling file watcher, scratch `LOCALAPPDATA`) | 540 of 540 passed |

**What was not reached:** the repository gate (`test-local.ps1 -Parked`) - I did not re-run it, so the README's
branch-against-main comparison is its own testimony; the Python tests of `cc-devthrottle maildesk`; a running Gateway
or browser (I did not re-take the pictures; I opened and read every committed one); any Windows machine; the hosted
Gateway, which I did not touch.

## Verdict

**One finding, to be decided before the phase 6 deploy. Three notes that need no change.** Rules 7 and 8, the switch,
the Gateway answer for an account that is off, the public-repository rule and the pictures all hold, with the one
exception in F1.

## Finding

### F1. For an account with the switch OFF, any failed read draws the "Mail Desk" entry

`packages/client-core/src/maildesk/useMailDeskEntry.ts`, the failure branch: on any outcome other than 200 or 404 (no
network, a timeout, a 5xx), it calls `setOffered(true)` and asks again 15, 30, then every 60 seconds. It does not know
the account's switch at that moment, so this happens to every account, including every account that is off. The test
`useMailDeskEntry_AnyOtherFailure_OffersTheEntryReportsItAndAsksAgainOnTheShellsRhythm` asserts exactly this.

**What breaks, for a customer who is not the owner:**

- The phone app opened with no network (the shell is served by the service worker) draws "Mail Desk" in the menu, and
  keeps it there for as long as the phone stays offline, because every retry fails the same way.
- Every Gateway deploy is a known outage of up to two minutes by design (repository instructions, the deploy section).
  A Cockpit or phone already open through it draws "Mail Desk" for every account until the Gateway answers again.
- Tapping it opens a page with the error banner "read the Mail Desk ...", for a feature that account does not have.

**Why it must be decided:** D21 says the page "is hidden when the account's switch is off", and D16's grant "holds only
while the feature is off for every other account - a change that affects any other account is outside this grant".
The gate itself answers 404 with no body precisely so that "an account that is off cannot tell Mail Desk exists"
(contract section 1); the entry tells it anyway, on any failure. Phase 6 deploys this to every customer of the hosted
Gateway, so the deploy would ship a visible change to accounts that are off.

The Mentor entry makes the same choice, but there the hidden answer is a refusal for a page the product does offer;
here the hidden answer is the whole feature being absent for that account, under a deploy grant that depends on it.
The contract's ruling ("a failure must never look like switched off") still has a home without the entry: the page at
`/mail-desk` shows the error itself. How to resolve it - for example, the entry stays hidden until a 200 has been seen
on this device, while the page keeps showing its own error - is the building seat's decision, not mine.

## Notes, no change needed

1. **A green "Last check" beside a red "Signed in".** In `desktop-on.png` and `phone-on.png`, `desk@example.com` reads
   "Failing - the last check could not read the mailbox" in red, and right under it "Checked 2026-10-19 23:29: the mail
   it read was screened" in green. Both sentences are true - the second is the account's one run, as the README's
   ruling "last check is per account, by design" says - and the red line beside it says what went wrong. An owner
   reading a card as one mailbox may still take the green line as "this mailbox was checked fine". Worth a look when
   phase 6's real pictures come back; not a defect today.
2. **A stale comment.** The header of `packages/client-core/src/maildesk/mailDeskClient.ts` lists "Kept for you" (the
   Gateway says "Keep for you") and "which machine is tried first" (the fold no longer claims an order, after the fold
   review's F1), and the `machines` field is commented "In order of preference". Comments only; the code renders what
   the Gateway sends.
3. **One extra request per app load for every account.** Every account that is off now makes one
   `GET /gateway/mail-desk/channels` per page load and the Gateway writes one `[MailDeskGate] REFUSED ... answering 404`
   log line for it. The answer is unchanged and the cost is small; it is noted only so a reader of the Gateway log is
   not surprised after the deploy.

## The six questions, answered

**1. Rule 7.** Every sentence and every tone on a card comes from the Gateway: `providerLabel`, `modeLabel` and
`modeTone`, `machinesLabel`, `signedIn.label` and `tone`, `lastCheck.label` and `tone`, `today.label`, and every
`today.counts` entry, and the envelope's `title`, `summary` and `emptyLabel`. The client branches on none of them; a
tone reaches the stylesheet only as a `data-tone` attribute. The client's own words are the four fixed slot names
("Mode", "Machines", "Signed in", "Last check"), the loading line and its error sentences for an answer that breaks the
contract - none of which changes with the state, so none is a verdict. The fold covers every outcome in
`TriggerRunOutcome.All` (six of six), every sign-in state and both modes, and throws on an unknown one rather than
picking a colour. The client refuses a missing label or an unlisted tone. Holds.

**2. Rule 8.** The page, its read, its entry question and its styles live once in `packages/client-core/src/maildesk/`.
`apps/cockpit/src/maildesk/MailDeskView.tsx` and `apps/mobile/src/pages/MailDesk.tsx` pass only a frame, their own
not-found and their error banner's class. The phone's `styles.css` addition is layout only, under `.screen`: one
column, the fact name above its sentence, a font size. No card, label or wording is edited in either shell; the
navigation labels "Mail Desk" in the rail and the drawer are the shells' own navigation, as every other entry is.
Holds.

**3. The switch.** With the read answering 404 the rail omits the entry (`withMailDesk` is not applied), the drawer
omits its link, and both routes render the shell's ordinary not-found; the tests cover each, and `desktop-off.png`,
`phone-off.png` and `phone-drawer-off.png` show it. Outside the Mail Desk: `withMailDesk` only inserts after
Factories and is not called when off, so the rail is the old list; the `StaleShellRecovery.tsx` change only moves the
message markup into `RouteMessage`, and `RouteRecoveryBoundary` still self-heals once and then shows the same text; the
`styles.css` rules all start with `.screen .mail-desk`; the two route tables gain only `/mail-desk`. Holds, except F1.

**4. D16, the Gateway for an account that is off.** Every Mail Desk route is mapped inside `MailDeskGate.Group`, which
answers 404 with no body before any handler runs when the account is off. The phase changes only the
`ListChannels` handler and the fold the handlers call, so an account that is off gets the same 404 as before. Nothing
outside the gated group calls the fold. Holds.

**5. The public-repository rule.** Every address added in the diff is `email@devthrottle.com` (and a made-up long
`...@devthrottle.com` in a test) or on a reserved example domain: `example.com`, `example.org`, `example.net`,
`shop.example`, `vendor.example`, `post.example`, `letters.example`, `lottery.example`, `service.example`. Subjects
and senders in `seed.py` are invented; the decisions' reason is "invented for the phase 7 screenshots". The only
tokens are made per run (`"qa-admin-" + uuid4`) and never written to a committed file; the committed answer and
screen files hold made-up identifiers and times only. In the sixteen pictures I read, the only addresses are
`email@devthrottle.com`, `desk@example.com` and `letters@example.org`. Holds.

**6. The pictures.** Each matches its row in the README: `desktop-on` and `phone-on` (1440 and 390 wide) show three
cards, one signed in with 7 decisions, one not signed in in red with the sign-in sentence, two in observe and one in
live mode in amber, all three cards whole; `desktop-unpublished` shows the three neutral "not known / not checked"
states; `desktop-empty` shows the empty sentence with "Mail Desk" active in the rail after Factories;
`desktop-off` and `phone-off` show "Page not found" with no rail entry; `phone-drawer-on` has "Mail Desk" after
Repos and `phone-drawer-off` does not. The three switch-on drawer pictures are byte-identical (same checksum), as the
README says. Holds.
