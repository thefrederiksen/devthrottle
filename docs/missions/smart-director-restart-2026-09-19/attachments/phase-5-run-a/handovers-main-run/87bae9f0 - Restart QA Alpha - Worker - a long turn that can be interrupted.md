# Handover - 87bae9f0 - Restart QA Alpha - Worker - a long turn that can be interrupted

## 3. The exact next action

Resume appending lines to `C:\Users\soren\AppData\Local\Temp\restart-qa-seats-run-a\alpha-long-turn\notes.md`,
one number per line, from the number AFTER the last line currently in the file, through 400.

Check where to resume first:

    cd "C:/Users/soren/AppData/Local/Temp/restart-qa-seats-run-a/alpha-long-turn"
    wc -l notes.md; tail -1 notes.md

The last line number IS the count of lines (line 1 = number 1, no header). Resume at last+1.

Each line must be appended in its own file-append operation - the owner's instruction was
"append after each number rather than at the end", so do NOT build the rest in memory and
write it once.

The line format already established (keep it identical):

    <n> - prime|not prime - <one short sentence about the number>

A helper script exists and does the primality test itself, so the label cannot be got wrong
by hand. It is at `C:\Users\soren\nadd.sh` (a copy of the same script in this session's
scratchpad). Use it as:

    bash ~/nadd.sh 53 "A short sentence about fifty-three."

If that file is gone, recreate it:

    cat > ~/nadd.sh <<'SH'
    #!/bin/bash
    n=$1; s=$2
    p="prime"
    if [ "$n" -lt 2 ]; then p="not prime"; else
      for ((i=2;i*i<=n;i++)); do if ((n%i==0)); then p="not prime"; break; fi; done
    fi
    printf '%s - %s - %s\n' "$n" "$p" "$s" >> "C:/Users/soren/AppData/Local/Temp/restart-qa-seats-run-a/alpha-long-turn/notes.md"
    SH

Finish at 400. Do not summarise the list afterwards and do not ask the owner anything -
the original instruction was explicit on both points.

## 1. What I am doing

A long, deliberately boring endurance task, given directly by the owner as a QA exercise for
this restart run: for every whole number from 1 to 400, in order, append one line to
`notes.md` in `C:\Users\soren\AppData\Local\Temp\restart-qa-seats-run-a\alpha-long-turn`
saying the number, whether it is prime, and one short sentence about it. The owner asked for
the appends to happen one at a time (a separate append per number), and asked for no summary,
no questions, and no skipping ahead. There is no repository here and nothing to merge - the
deliverable is the file itself.

## 2. Where I got to

Proven (read back off disk at the moment of writing this):

- `notes.md` exists in the working folder and holds exactly 52 lines.
- Line 52 is `52 - not prime - The number of weeks in a year and cards in a standard deck.`
- Lines 1 through 52 were each appended individually, in order, in their own command.
- Spot-checked the top of the file: lines 1-3 read `1 - not prime - ...`, `2 - prime - ...`,
  `3 - prime - ...`, so numbering starts at 1 with no header line.

Not done: numbers 53 through 400 (348 lines remaining).

## 4. Decisions and why

- **Primality is computed, not recalled.** The helper script trial-divides, so the
  `prime` / `not prime` label in every line is machine-checked. Do not switch to writing the
  label by hand from memory to save time - that is the one part of this task that can be
  silently wrong for 348 lines.
- **One append per number.** The owner said "append after each number rather than at the end".
  Batching the remainder into one write would finish faster and would not be what was asked.
- **The helper lives outside the deliverable folder** (`~/nadd.sh` and this session's
  scratchpad), so the working folder contains only `notes.md`.
- **Line format is fixed** as `<n> - prime|not prime - <sentence>`; keeping it identical
  matters because the file is half written.

## 5. Traps

- Shell state does not persist between tool calls here, so a variable holding the script path
  has to be re-set every call - hence the short `~/nadd.sh` path.
- The Bash tool runs Git Bash on Windows: use forward-slash paths and never `/dev/null`.
- `notes.md` has no header, so "last line number == line count" holds. If anyone adds a
  header or a summary to the file, that shortcut for finding the resume point breaks.
- Do not re-run a number that is already in the file; appending is not idempotent and a
  duplicate will not be noticed by any check.

## 6. State

- No git repository (`Is a git repository: false`), no branch, no worktree, no pull request,
  no issue number.
- No background jobs; no processes left running by this session.
- Files touched: `notes.md` in the working folder (52 lines), `C:\Users\soren\nadd.sh`
  (helper), and the same helper in this session's scratchpad.
- Fleet: seated as Worker on the `mission` workflow, run 3d40f406. I fetched the mission
  conduct at the start of the turn; no other seat is involved in this task and I have
  messaged nobody.

## 7. What I did NOT verify

- I did not re-read all 52 lines end to end - I verified the count, the first three lines and
  the last line only. Lines 4-51 are believed correct but were not re-read off disk.
- I did not check the sentences for factual accuracy beyond ordinary care; they are short
  general-knowledge remarks, not sourced claims.
- I did not verify that `C:\Users\soren\nadd.sh` survives the restart - it should, it is an
  ordinary file, but the recreate command is in section 3 precisely because I have not
  tested that it is still there afterwards.
- The scratchpad copy of the helper is session-specific and I assume it is gone after the
  restart; I have not confirmed that either way.
- Everything I state about the mission workflow is from the conduct I fetched this turn;
  nothing about the wider restart run is first-hand.

<!-- drain-report
state: drained
restore: yes
why: The task is 52 of 400 lines done and needs a session to finish the remaining 348 appends; nothing else can carry it.
covered: 87bae9f0-28c5-4c2a-b132-f7c18bf277ab | this seat, writing its own handover
-->
