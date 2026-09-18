# Worker brief - phase 3b, the proof: a real browser, a real Gateway, a real session, before and after

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-proof`, cut from `mission/dev-reports-p3b`.
Your worktree is yours alone. Commit and push as you go. NEVER merge anything to main, and never touch
`mission/dev-reports-p3b` itself - your Manager merges your branch in.

Read first:
- `docs/missions/dev-reports/HANDOFF-phase-3b.md` - the three defects the owner saw, and the proof he asked for.
- `docs/missions/dev-reports/PHASE-3-REPORT.md` - how phase 3 proved things; do not repeat what it got wrong.
- `packages/client-core/src/devreports/CONTRACT.md` sections 1 and 2 - so your sample report is a VALID report.

## THE WHY

The owner used the live Reports tab, counted two conversations, two Send buttons and three scrollbars, and is
not happy. Unit tests will say the code changed. Only a picture says the screen is fixed. He asked for proof,
so the pictures ARE the deliverable of this phase - and a "before" picture is what makes an "after" mean
anything.

## YOUR JOB, IN TWO HALVES

### Half one - NOW. The rig, a sample report, and the BEFORE pictures.

Your worktree is today's shipped behaviour: this is the "before". Three other Workers are changing the page,
the Gateway and the apps in their own worktrees right now. Do not wait for them and do not touch their files.

1. **Stand up an isolated local Gateway.** Do not touch the owner's installed Gateway, do not take port 443,
   do not restart anything of his. The proven recipe is in the mission memory and in
   `docs/features/mobile-voice-recorder/PROOF.md` (pull request 2219): publish the Gateway from your worktree
   with `-p:IncludeNativeLibrariesForSelfExtract=true`, copy your own `apps/mobile/dist` into
   `<stage>/wwwroot/mobile` and `apps/cockpit/dist` into `<stage>/wwwroot/c`, launch it through a SCHEDULED
   TASK (it is an Avalonia tray app and dies in an agent shell) with `CC_DIRECTOR_ROOT=<scratch root>`,
   `CC_GATEWAY_NO_TAILSCALE=1`, `CC_GATEWAY_NO_AUTH=1`, on a port nothing else uses. Verify `/healthz` before
   trusting a single thing you see. Write the whole recipe down as a script in your worktree so the AFTER run
   is one command, not an afternoon.
2. **A real session and a real report.** You need a live session on that rig so delivery is real, and a sample
   dev report that is a VALID report by the contract: a header with a status, a summary, a questions section
   with at least one question that has at least two radio options and one `data-recommended`, at least one
   detail section, and A TABLE - because the owner's complaint is about a note box covering a table cell and
   the question being answered. Publish it with `cc-dev-reports open`. Keep the report file in your worktree
   under `docs/missions/dev-reports/proof/` so the after run uses the same one.
3. **BEFORE screenshots**, in a real browser (browser-harness; see the browsers skill), at BOTH widths -
   phone 390x844 and desktop 1400x900 - showing today's behaviour:
   - the Cockpit Reports tab with the report open: both conversations visible at once, both Send buttons in
     one frame, and the scrollbars.
   - the floating panel covering the question being answered.
   - a table cell being noted, with the note box over what is being noted.
   - what the report view shows today where a way back to the session should be.
   Name each file for what it shows, in plain words, and put them in `docs/missions/dev-reports/proof/before/`.

### Half two - LATER, when your Manager tells you the three branches are merged in.

Rebuild from the merged branch and take the AFTER pictures at the same two widths, same report, same shots,
into `docs/missions/dev-reports/proof/after/`, plus:
- ONE conversation and ONE Send on the screen.
- a note box beside a table cell, not covering the cell and not covering the question.
- ONE scrollbar.
- the back link showing `back to <three-digit number> <session name>`, with the real number and name.

And the END TO END, which is the part no screenshot can fake: open the ONE address `cc-dev-reports open`
printed, on a desktop-shaped browser AND on a phone-shaped one, land inside the report in the right app both
times; note a table cell; answer the question; press Send once; the session receives ONE prompt naming the
cell and the option; make the agent reply with `cc-dev-reports reply`; the reply appears in the app's
conversation. Screenshot each step and capture the actual prompt text the session received.

Also do the signed-out case: clear the browser's device key, open the address, sign in, and land on the
report - not on a home screen.

## WHAT COUNTS AS PROOF, AND WHAT DOES NOT

- A screenshot of a page you did not verify is the built code is a picture of nothing. Check the Gateway's
  health version and the bundle you copied, and say in the report which commit each picture is of.
- "The screenshot looks right" is not a pass for something you can assert. Where you can read the page's own
  measurements - scroll heights, element rectangles - read them and write the numbers down.
- Say plainly what you did NOT prove. Anything you could not stand up, say so rather than implying coverage.
- No internal identifier in any picture the owner will look at: if a session id or report id is visible on
  screen, that is a DEFECT to report to your Manager, not something to crop out.

## DONE MEANS

Everything committed and pushed on `mission/dev-reports-p3b-proof`, including the screenshots, the sample
report, and the rig script. A file `docs/missions/dev-reports/WORKER-phase-3b-proof.md` holding: the rig
recipe as run, the commit each picture is of, every picture named with what it shows, the end-to-end
transcript including the prompt the session actually received, and what you did NOT prove. Tell your Manager
in ONE line after each half - fleet messages truncate at the first newline.

Do not guess. If something is genuinely undecidable, ask your Manager - do not invent a product decision.
