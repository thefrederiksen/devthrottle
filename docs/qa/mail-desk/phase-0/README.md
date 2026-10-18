# Mail Desk, phase 0: the proofs

Date: 18 October 2026. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), the phase 0 row of
section 6. Built from origin/main `d091f9083`. No product code changed: everything here is a proof script, its tests
and its output.

Each proof was done by its own Developer in its own worktree and checked by the Tech Lead before it was accepted:
the Tech Lead re-ran (b) against the real mailbox and got the same numbers, ran the tests of (a) and (c) from a clean
copy (16 and 31 pass), checked the file and line references of (d) on origin/main, and searched every committed file
for email addresses, tokens and assistant attribution.

## The results

| Proof | Result | Folder |
|---|---|---|
| (a) The sign-in premise | **Mostly proven. One point NOT PROVEN: the token can send mail.** Two findings change design 5.4. | `a-sign-in/` |
| (b) Read-only measurements of the real mailbox | **Proven** for this mailbox; it is small, so paging at scale is not proven. | `b-mailbox/` |
| (c) The model on a sample | **NOT PROVEN against the real model.** The real model was never called. The pipeline is proven against a fake model. | `c-model/` |
| (d) Where the account switch belongs | **Settled**, with open points for phase 1. | `d-switch/` |

### (a) The sign-in premise

A test instance of the installed 2.22.0 Director (scratch storage root, scratch HOME), connected to a throwaway
Gateway on this machine, ran a check from a published factory version three times.

- **Proven:** the check saw only the four `CC_CHECK_` variables. It found the owner's real saved sign-in, and
  exactly one. It got a token without any sign-in prompt, and a read of the Inbox worked with it. The renewed sign-in
  was written back to the same file with owner-only permissions (0600), the next run read it again silently, and
  cc-outlook still showed the account as signed in.
- **Proven by tests, not by the Director:** with no saved sign-in the check fails and names the fix (`run cc-outlook
  -a devthrottle auth`); with more than one it fails and names every path found, never picking one. The tests ran on
  scratch folders of dummy files with every `CC_` variable removed.
- **NOT PROVEN: a token without `Mail.Send`.** Asking for `Mail.ReadWrite` alone, even forcing a fresh token,
  returns one that carries `Mail.Send` too: Microsoft puts every permission the app was granted for Graph into the
  token. **The token itself can send mail.** The promise "the Mail Desk never sends mail" therefore rests only on
  `screen.py`'s own request guard (GET, and the move to `archive` or `junkemail`, nothing else), which phase 2 must
  build and prove with tests. A narrower token would need a second Azure app, which this mission is not granted.
- **Finding 1, where the sign-in lives.** cc-outlook saves it under `CC_DIRECTOR_ROOT`, which a Director sets to its
  instance folder, so on devlinux it is
  `~/.local/share/cc-director/instances/default/config/outlook/tokens/email_devthrottle_com_msal.json`. The file name
  is the address with `@` and `.` turned into `_`. A check has every `CC_` variable stripped, so it cannot follow
  cc-outlook's own path rule. The first version of the mission document said the opposite. The rule `screen.py` must
  follow is now in mission section 5.4: look only under the Director's per-user base folder, at
  `instances/*/config/outlook/tokens/`, taking the home folder from the operating system's account record (the test
  Director's HOME was a scratch folder, and a lookup from HOME found nothing). It must fail on none and on more than one.
- **Finding 2, uv is missing from the live Director's PATH on devlinux.** The owner's Director was started with a
  PATH that does not include `~/.local/bin`, the only place `uv` is. A check that declares packages, as `screen.py`
  must for MSAL, fails there with "the check declares packages and uv is not on this machine". The run was
  reproduced with that exact PATH, and the publish also warned about it. This needs a fix on the machine before
  phase 6. It needs no product change.
- **Not proven:** Windows or macOS; a sign-in that has expired, as opposed to one that is missing.

### (b) Read-only measurements of the real mailbox

- **Reading changed nothing:** Inbox 19 messages and 4 unread, before and after; the saved sign-in file was
  byte-for-byte unchanged. Only GET requests were made, through a guard with a self-test.
- **Inbox:** 19 messages, the oldest 6.6 days old, so the "most recent 200 or 30 days" rule yields 19, in one page.
  Paging with `@odata.nextLink` keeps the newest-first order (tested with small pages), and a filter on the receive
  time works together with that order.
- **Sent folder:** 44 messages over 123 days, written to 38 different addresses. None of today's Inbox senders is
  among them, so every current Inbox sender would count as unknown.
- **Size:** a message id is 152 characters. One message as the check would report it is 446 bytes typical and
  680 bytes at worst. The 8 KB context limit binds, not the 16 KB output limit.
- **For phase 2:** write UTF-8, keep the 200-character preview, fill each batch up to 7 KB of context and stop at
  10 messages (17 percent margin at worst size). The mission's "roughly 20 per run" holds only for typical mail.
- **Not proven:** paging at scale (the mailbox is too small), a true worst-case message size, a sender found in the
  Sent folder, and Microsoft's request limits.

### (c) The model on a sample

- **The real model `devthrottle/wingman-fast` was never called.** This machine holds no DevThrottle or DeepInfra
  key. The hosted Gateway, which holds one, is off limits in this phase, and the only other keys belong to a client.
  The Delivery Lead ruled that no key comes in this phase. **Spend: zero dollars.**
- **The real Inbox** held 19 emails, not 50, and all of them are automated notices. Labelled by hand, every one is
  "person" (not spam) and the notification route. So real mail could not test spam, customers or citizenship.
- **What is proven,** against a fake model, on the 19 real emails and on 18 invented ones (example domains only,
  expected labels written before any run): the prompts, the answer limited to the listed labels (an answer outside
  them became "unsure", 5 and 7 times), the confidence read from the token probabilities, the spend stop, and the
  scoring. 31 tests pass. The real run is one command once a key exists.
- **Provisional confidence threshold: 0.90 for a route, 0.95 before a spam answer may send an email to Junk.**
  This is a reasoned proposal, not a measurement. It costs nothing to be wrong about, because production runs in
  observe mode, which acts on nothing. The real threshold is set in phase 6 from the observe-mode screening of the
  existing Inbox, using the threshold table this script already produces.
- **Finding for phase 2:** in 8 of the 19 real emails the 200-character preview is mostly invisible padding, with
  only 46 to 99 visible characters. `screen.py` must strip invisible characters before cutting the preview to 200.
- **A cheaper model** is not worth a mission at this volume: about $0.00015 per email.
- **Not proven:** anything about the real model's answers, its accuracy or its confidence values; whether DeepInfra
  returns token probabilities for this model.

### (d) Where the account switch belongs

- The factory agents switch is **already per account**: one row in `tenant_settings` (key `factory_agents_switch`),
  written only by `POST /gateway/admin/factory-agents` with the administrator token. Routes sit in a gated route
  group that answers 404 when it is off.
- **Recommendation for phase 1:** `mailDesk.enabled` copies that design: a new `tenant_settings` key
  `mail_desk_switch`, a `MailDeskSwitch` class, a `MailDeskGate` route group and `/gateway/admin/mail-desk`. No new
  table, so no migration pair. Turning it on for the owner's hosted account is one administrator request, with no deploy.
- **Open for phase 1:** the factory gate is already on for the owner's account, so the Mail Desk refusal of a check
  report must sit inside the report handling, not in the gate. Which status the refusal answers is undecided (a 404
  would read as "no such trigger"). Nobody checked whether factory agents are on for the owner's hosted account,
  because no hosted call was allowed. No tool calls the administrator route today, so phase 6 needs a written command.

## What phase 0 changed in the mission document

Sections 5.2 and 5.4 of `MISSION.md` are amended in this pull request, marked as phase 0 findings: where the
sign-in lives and how `screen.py` finds it, that the token can send mail, and that uv is missing from the live
Director's PATH. Moving cc-outlook's saved sign-in to the per-user folder, as issue 3011 did for cc-gmail, is a
follow-up, not part of this mission.

## How to run each proof again

Each folder's README has its exact commands. In short:

- (a) tests: `uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-0/a-sign-in/tests -v`;
  the Director run: `uv run --script docs/qa/mail-desk/phase-0/a-sign-in/rig/run_proof.py docs/qa/mail-desk/phase-0/a-sign-in --runs 3`.
- (b): `uv run --script docs/qa/mail-desk/phase-0/b-mailbox/measure_mailbox.py --out <file>` (read-only).
- (c) tests: `uv run --script docs/qa/mail-desk/phase-0/c-model/test_model_sample.py`.
- (d): read only; the references are to origin/main `d091f9083`.

None of these is in the repository's local test gate. The change is documents and proof scripts under `docs/` only,
so `scripts/test-local.ps1` covers none of it.
