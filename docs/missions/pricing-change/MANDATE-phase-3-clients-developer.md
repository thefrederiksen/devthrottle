# Mandate: Pricing Change - Developer - phase 3 phone screen and Director copy

Written 2026-10-13 by the Tech Lead (session "Pricing Change - Tech Lead - Gateway plan checks").
This file is your whole mandate. You answer to the Tech Lead. Do not commit this file.

## Why you exist

The Pricing Change mission replaces the 14-day trial and the generous Free plan with a paid ladder: Free
keeps the Director only, and the Cockpit and the phone app need the Remote plan. A second Developer is
teaching the hosted Gateway to refuse a Free account's phone and Cockpit with a 402. You build what a
person sees when that happens, and you take the trial promise out of the Director's first-run wizard.

Read first:

- the mission document, sections 3, 4 and 5 (Track B, item B5 and the phone bullet):
  `git -C D:/ReposFred/devthrottle_internal show origin/main:docs/missions/pricing-change-2026-10-11/MISSION.md`
- `CLAUDE.md` in this repository, above all rule 7 (the client is dumb, the Gateway owns all ruling)
  and rule 8 (Settings is one page on two surfaces), `docs/CodingStyle.md` and `docs/VisualStyle.md`.

## Your worktree

`D:/ReposFred/devthrottle.worktrees/pricing-phase3-clients`, branch `pricing-change/phase-3-clients`,
cut from `origin/main`. Work only here. You touch `apps/`, `packages/` and
`src/CcDirector.Avalonia/FirstRunWizardDialog.axaml` (and its tests). You do NOT touch
`src/CcDirector.Gateway`: that is the other Developer's.

## The contract the Gateway will answer

The Gateway side is being built now, so you build against this, not against a running server.

HTTP 402, flat JSON, in two places: `POST /mobile/enroll` (the phone app and the Cockpit sign in), and
ANY authenticated request from a phone or a browser whose account has lost the plan:

    { "error": "...", "code": "remote_plan_required", "message": "<one finished sentence>", "subscribeUrl": "<the pricing page>" }

The sentence and the address are the Gateway's. The client shows `message` as it is and opens
`subscribeUrl`; it never writes its own sentence about plans and never decides what the state means. The
other 402 that already exists (`hosted_subscription_required`, and an enrolment 402 with no code) must
keep working as it does today. A 503 with `entitlement_unknown` is temporary and is never shown as a
plan problem.

## The task

1. **The phone: a clear "Remote plan needed" screen on the 402 at enrolment.** Check
   `apps/mobile/src/components/CreditsNotice.tsx` first, as the mission says: reuse it, or the pattern
   it follows, rather than building a second notice. The screen shows the Gateway's sentence, one
   button that opens the pricing page, and a way to try again after subscribing. Find out and say what
   the phone shows today on that 402, so the pull request can state what changed.
2. **The same account refused in the middle of a signed-in session** (a `remote_plan_required` 402 on
   an ordinary request): the phone and the Cockpit must not show a raw error or loop. Find the one place
   in `packages/client-core` where a response is turned into an error, handle the code there once, and
   lead the person to the same notice. The Cockpit signs in through the same `/mobile/enroll` door, so
   its sign-in screen needs the same notice: put the shared part in `packages/client-core` and let each
   shell supply only its frame. If the Cockpit half is more than two hours, stop and tell me what is
   left before building it.
3. **B5: the Director wizard.** In `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml` the "free
   14-day Pro trial ... No credit card" line (near lines 598 and 638 on main) becomes plan-neutral: it
   promises no trial, names no price and no number of days. Search the whole of
   `src/CcDirector.Avalonia` and the Director's other projects for the same promise ("14-day", "free
   trial", "no credit card") and fix every hit, or list each one you leave with the reason. A search
   that finds nothing is a broken search until it has found a planted hit.

## The check

- The web tests for what you touched pass, and each new behaviour has a test that fails without it:
  the 402 with `remote_plan_required` at enrolment shows the notice with the Gateway's sentence and the
  link; the same 402 on an ordinary request leads to the notice; the older 402 shapes behave as before;
  a 503 `entitlement_unknown` is not shown as a plan problem. Find the test command in the repository
  (`package.json` scripts); say exactly what you ran and its counts.
- The type check and the build of both shells pass.
- A screenshot of the phone notice and of the Cockpit notice, committed in
  `docs/missions/pricing-change/`, taken from the built app with the 402 answered by a stub. Do not
  start an Android emulator; a browser at phone width is enough.
- For the wizard: the Avalonia tests that cover `FirstRunWizardDialog` pass
  (`.\scripts\test-local.ps1` default run is the gate for that project; do not pass `-Parked` or
  `-Gateway`), and the proof quotes the old and the new line.

## Proof and hand-in

- `docs/missions/pricing-change/phase-3-clients-proof.md`: the commands, their result lines with
  counts, one plain sentence per new test, the screenshots, and what the proof does not cover.
- Commit and push `pricing-change/phase-3-clients` (plain push). Do NOT open a pull request and do NOT
  merge: your branch is merged into the Gateway branch and goes out as one pull request.
- Report: `cc-devthrottle session report "<what you did, the head commit, the test counts, anything
  left open>"`. Then stay open: review findings come back to you.

## Rules that bind you

- **Never sign anything.** No assistant, model or vendor name, no co-author trailer and no "generated
  with" line in any commit, pull request, issue or comment. Check the text before every commit.
- **Plain English, no abbreviations**, ASCII characters only, in commits, comments, documents and in
  every word a person reads on screen.
- **You never deploy and never merge.** This mission does not release the Director or the phone app.
- **Foreground only.** No background or detached task, no sub-agents inside your session. Never pipe a
  long run through `tail` or `head`.
- **No fallback programming.** Never kill a process you did not start.
- If something cannot be decided from the mission document or this file, ask me with
  `cc-devthrottle message send <the session that started you> "<question>" --reply-wanted` and carry on
  with everything else.
