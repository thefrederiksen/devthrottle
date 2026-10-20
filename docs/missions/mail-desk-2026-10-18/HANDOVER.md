# Mail Desk - handover to the Implementation Lead

Date: 19 October 2026. From: the first Delivery Lead, session 110 ("Mail Desk - Delivery Lead - drive to observe
mode in production"), on devlinux. To: the Implementation Lead the owner asked for (D24), who drives the mission to
section 9 from here. Read `MISSION.md` in this folder first, from origin/main; this file is the state it does not
hold. Everything below was true at the time of writing - check each point before relying on it.

## 1. What is merged to main

| Pull request | What |
|---|---|
| 4212 | The mission document |
| 4214 | Phase 0, the proofs (`docs/qa/mail-desk/phase-0/README.md`, with its review) |
| 4215, 4219, 4256 | Decisions D19 to D21, grants D22 and D23 |
| 4224 | Phase 2, the screening check `checks/screen.py` and the built-in template (`docs/qa/mail-desk/phase-2/`) |
| 4235 | Phase 1, the store, the `maildesk` command, the switch, the routes (`docs/qa/mail-desk/phase-1/`) |

The Delivery Lead re-ran each merged phase's own tests before merging: phase 2, 210 Python and 26 Mail Desk Gateway
tests; phase 1, 262 Mail Desk Gateway and 95 `maildesk` command tests. Each finished phase was reviewed as a whole
and accepted with no findings (phase 0 and 2 on Fable, phase 1 on Opus).

## 2. What is in flight

**Phases 3 and 5, one phase branch, `mail-desk-phase-3`.** Every task is built, reviewed and merged into the phase
branch: per-mailbox failure in `screen.py` (4229), the model call (4230), the mailbox problems and the 08:00 section
(4239), the four layers and their settings (4240), the hand-off with the run, the memory, the channel list and the
template draft offer (4243), the backup machines (4232), plus a rebase onto main with the migrations regenerated in
order. Its Tech Lead, session "Mail Desk - Tech Lead - phase 3 screening on the Gateway and phase 5 backup"
(id bb3e82f5-521a-41ab-be8b-c6d91823ce72), was running the `-Parked` gate on the final commit at 21:34 on
19 October, then writes the phase 3 and phase 5 READMEs and opens ONE phase pull request. It also found a test on
main broken by an unrelated change (pull request 4252 retired an alarm kind that test still raises); it is main's
to fix, not this mission's.

That Tech Lead is handed to you (section 6). When its pull request arrives: run the Mail Desk tests yourself, send
the finished phase to a Reviewer, merge.

## 3. What is not started

- **Phase 4, live mode** - section 6 row 4 and section 7.2's live-mode cases. Proven ONLY on a throwaway Gateway
  with a slot 5 or higher test Director and the fake Graph. Merged, and OFF in production.
- **Phase 7, the Mail Desk page** (D21) - section 6 row 7. Phase 1 already serves the folded channel rows at
  `GET /gateway/mail-desk/channels` (see `docs/qa/mail-desk/phase-1/CONTRACT.md` section 5). Proof is a QA report
  of screenshots at desktop and phone width (the `web-qa` skill), switch on and off.
- **Phase 6, deploy and production QA** - section 6 row 6, section 7, and the pre-deploy checks in section 5 here.

## 4. Rulings made during the run - all binding

1. **One mailbox failing never stops the others.** Each fails as its own named problem; the rest are screened.
2. **A model failure** rereads only that email's mailbox; a reread is idempotent (one decision row per mailbox and
   message key, no second model call or charge); the third failure keeps the email for the owner, lists it in the
   08:00 section, and lets the mailbox move on. In the phase 3 contract, with tests.
3. **The token can send mail** (phase 0 proof a). The "never sends" guarantee is `screen.py`'s request guard:
   only GET and `POST /me/messages/{id}/move` to `archive` or `junkemail`.
4. **The sign-in is found only** under the Director's per-user base, `instances/*/config/outlook/tokens/`, failing
   on none and on more than one. File names follow cc-outlook's rule (`email_devthrottle_com_msal.json`).
5. **Batches:** at most 7 KB of context and 10 messages per run (not 20). Catching up 200 emails is about 20 runs.
6. **Confidence thresholds are provisional:** 0.90 for a route, 0.95 before a spam answer may junk. Set the real
   ones from the observe-mode decisions in phase 6. The real model was never called in phase 0.
7. **The public repository** never holds the second mailbox's address, any real sender's address or domain (example
   domains only in test data), any token, or any email text. Decisions on real mail go only to the owner's report.
8. **The gate on Linux:** `-Parked` cannot be clean here. Run it on the branch and on unchanged main, compare suite
   by suite, show no failure main does not also have, and say plainly no Windows gate ran.
9. **Reviewers:** Codex is not installed on devlinux and Fable is out of its monthly limit, so reviews run on
   Claude Code on Opus (`--model claude-opus-5-5`), never Pi, Grok or Gemini.
10. **Hard order:** the `mail-desk` factory is published only after phase 3's hand-off is deployed - before that, a
    published factory would start a seat on any count. Phase 3 also makes the Gateway refuse the ordinary path.
11. **The backup machine** is listed per channel: `maildesk channel add <address> --provider outlook --machine
    devlinux [--machine SOREN_NORTH]`.

## 5. Before the deploy (phase 6)

- **Reserved name check (phase 1 review):** `mail-desk` with `checks/screen.py` is now reserved for every account.
  Check, read-only, that no other account on the hosted Gateway has a published factory by that name.
- **`uv` on the live Director's PATH (phase 0 finding 2):** the owner's running Director cannot find `uv`, which
  the check needs. The owner is asked (question `uv-path` in his report) to choose a link into `/usr/local/bin` by
  sudo in a terminal you open with the `user-input-terminal` skill (recommended), or a Director restart.
- **Turning the switch on** is the administrator route `/gateway/admin/mail-desk`, by the written command phase 1
  added (see its README).
- **D22:** you publish the factory and add both channels in observe mode yourself. Never `maildesk mode ... live`.
- **The second mailbox's address** is the cc-outlook account with the nickname `mindzie` on devlinux
  (`cc-outlook accounts list`, `cc-outlook -a mindzie profile`). Give it only to the Gateway and the owner's report,
  never to the repository.
- **QA needs one fresh email** from the owner (the mission sends none) - ask him when you are ready.

## 6. Seats, and how to take them

- Phase 3's Tech Lead (above) is released to the owner by the first Delivery Lead. Take it with
  `cc-devthrottle session hand-over bb3e82f5 --to me` - the owner told you to (D24).
- Nothing else is open. Every Developer and Reviewer opened so far is closed, and their worktrees are removed.

## 7. Product defects that will slow you

- **Doorbells (issue 3930):** a doorbell is often typed into a seat's prompt box and never sent, or a message never
  arrives; seven stalls in two days, one a deadlock no command could clear. Check every seat on a heartbeat (every
  25 to 30 minutes), and tell every seat you open to run `cc-devthrottle message inbox` itself when idle. A seat
  you own that is waiting can be reached with `cc-devthrottle session prompt <id> "<text>"`.
- **One message per recipient every 10 minutes.** Plan messages; a refused one is not queued.
- **Usage:** the account hit its monthly spend limit once (19 October). D23 lets the mission use credits; the
  limit is the owner's setting - if a seat stops on that menu, tell him and never touch it.

## 8. The owner's open questions

Published in the dev report `https://gateway.devthrottle.com/r/8e1c2f05-9739-4037-942e-e083e3651ce1` and again in
the handover report: the spend limit, the `uv` fix, the reviewer, notification senders, the backup machine in
production, the citizenship rule. None blocks the work except `uv-path` before phase 6. His answers arrive as fleet
messages to the session that published the report; the handover report is published again from your seat's side
only if you republish it, so ask him again in your own report if you need an answer.
