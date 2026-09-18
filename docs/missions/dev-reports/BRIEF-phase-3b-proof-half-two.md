# Worker brief - phase 3b, half two: the after pictures and the end to end

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-proof2`, cut from `mission/dev-reports-p3b`
AFTER every piece of phase 3b was merged into it. Your worktree is yours alone. Commit and push as you go.
NEVER merge to main and never touch `mission/dev-reports-p3b` itself.

Read first, and in this order - the first two are a previous Worker's record of the rig you are about to
run, and they will save you an afternoon:

1. `docs/missions/dev-reports/WORKER-phase-3b-proof.md` - especially "The rig recipe, as run" and
   "What half two still needs", which is a numbered list written for you.
2. `docs/missions/dev-reports/proof/rig.ps1` - the rig, as a script. You run it; you do not rebuild it.
3. `docs/missions/dev-reports/HANDOFF-phase-3b.md` and `STATE-phase-3b.md`.

## THE WHY

The owner used the live Reports tab, counted two conversations, two Send buttons and three scrollbars, and
is not happy. Five Workers have changed the page, the Gateway, both apps, the tool and the signed-out
landing, and every one of them is green in its own tests. NONE of that is a picture of a working screen, and
none of it has sent a single word into a real session. He asked for proof. You are it.

The before pictures are already in `docs/missions/dev-reports/proof/before/`. Your job is the other half of
every pair, plus the loop no screenshot can fake.

## THE WORK

### 1. Stand the rig up on the MERGED code

`cd docs\missions\dev-reports\proof`, then `.\rig.ps1 build -Force`, `.\rig.ps1 down`, `.\rig.ps1 all`.
Record the new commit exactly as the previous Worker did: every executable's stamped product version AND the
running Gateway's own `/healthz` version. A picture of a build you did not verify is a picture of nothing.

Two things that previous run learned, so you do not pay for them again:

- **A `502` from the `session` command can mean the session was created anyway** - the response timed out,
  the work did not. LIST the sessions before retrying; a retry seats a duplicate.
- The fixture session takes a message whose first line is exactly `FIXTURE-COMMAND` and runs the shell
  command that follows. That is how you make the agent reply without it reacting to the notes themselves.

### 2. The after pictures - the same shots, the same report, the same two widths

Into `docs/missions/dev-reports/proof/after/`, named for what they show, one per before picture so the pairs
line up. At 1400x900 and at 390x844:

- ONE conversation and ONE Send on the screen. Count every button on the whole screen whose words start with
  "Send" and write the number down - do not judge it by eye.
- The note box beside a table cell, not over it, and not over the question being answered. Read the
  rectangles out of the page and write the numbers down.
- ONE scrollbar. Measure it the way the before run did - `scrollWidth` against `clientWidth`,
  `scrollHeight` against `clientHeight`, plus any panel with its own scroller - and say what you measured.
- The back link reading `back to 100 dev report proof fixture`, with the real number and the real name.

### 3. LOOK AT HOW WIDE THE REPORT IS, AND SAY WHAT YOU SEE

`STATE-phase-3b.md` records a defect the handoff never named: in the before picture the report frame is about
200 pixels wide at 1400x900, because the Cockpit's session detail column is roughly 540 and the conversation
rail takes a fixed 340. Removing the page's conversation does not widen the report by one pixel.

Measure the report frame's width in the after run and WRITE THE NUMBER DOWN. If the report is still squeezed,
say so plainly and put a picture of it in `after/`. Do NOT fix it - it is outside this phase and would be a
guess - and do NOT quietly leave it out. The owner is told, either way.

### 4. The end to end - the part no screenshot can fake

From the ONE address `cc-dev-reports open` printed, on a desktop-shaped browser AND a phone-shaped one:

1. Open the address and land INSIDE the report in the right app, both times.
2. Note a table cell. Answer the question. Press Send ONCE.
3. The session receives ONE prompt naming the cell and the option. Read the prompt back out of the Gateway's
   own database, not off the screen, and paste it into your report verbatim.
4. Make the agent reply with `cc-dev-reports reply`. The reply appears in the app's conversation.

### 5. Signed out, which is what the fifth Worker just built

Clear the browser's device key, open the printed address, sign in, and land ON THE REPORT - not on a home
screen, not on Not found. Do it on BOTH device shapes: the desktop path and the phone path fail differently
and only one of them was ever broken in the obvious way.

## WHAT COUNTS AS PROOF

- Say which commit every picture is of, and how you know.
- Where you can read a number out of the page, read it. "It looks right" is not a measurement.
- **No internal identifier in any picture the owner will look at.** A session id or report id visible on
  screen is a DEFECT to report, not something to crop out.
- Say plainly what you did NOT prove. The previous Worker's list is a good model: no real phone, one browser,
  one machine.

## DONE MEANS

Pushed on `mission/dev-reports-p3b-proof2`, with `docs/missions/dev-reports/WORKER-phase-3b-proof2.md`
holding the commit, every picture named with what it shows, every number you measured, the verbatim prompt
the session received, and what you did not prove. Tell your Manager in ONE line - and know that fleet
messages have mostly NOT arrived this phase, so the file is the real report.

Do not guess. If something is genuinely undecidable, write it in the report rather than inventing an answer.
