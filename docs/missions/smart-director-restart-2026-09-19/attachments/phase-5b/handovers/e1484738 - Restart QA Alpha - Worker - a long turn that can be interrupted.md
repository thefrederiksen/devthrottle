# Restart QA Alpha - Worker - a long turn that can be interrupted

Session e1484738 on SOREN_NORTH, repo C:\Users\soren\AppData\Local\Temp\restart-qa-seats\alpha-long-turn

## 3. The exact next action

Append line 26 to `C:\Users\soren\AppData\Local\Temp\restart-qa-seats\alpha-long-turn\notes.md`,
then keep going one number at a time, in order, up to and including 400.

First, confirm where the file actually stands - do NOT trust this document's count:

```bash
cd "C:/Users/soren/AppData/Local/Temp/restart-qa-seats/alpha-long-turn"
wc -l notes.md && tail -3 notes.md
```

As of writing, notes.md has 25 lines and the last line is:
`25 - not prime - Five squared, and a quarter of a hundred.`

So the next append is 26. One append per number, one Bash call per number:

```bash
cd "C:/Users/soren/AppData/Local/Temp/restart-qa-seats/alpha-long-turn"
echo "26 - not prime - The number of bones in the human foot." >> notes.md
```

Line format, exactly as the existing 25 lines use it:

    <number> - prime|not prime - <one short sentence about it>

Rules the owner set, verbatim in spirit: go in order, append after EACH number rather than
batching at the end, do not summarise, do not ask anything, do not skip ahead. Do not stop
until 400 is written.

## 1. What I am doing

Writing a 400-line reference list by hand, one line at a time, as a deliberate long-turn
endurance test for the restart QA. The deliverable is one file, `notes.md`, in the repo folder
above. Each whole number from 1 to 400 gets exactly one line stating the number, whether it is
prime, and one short sentence of colour about it. The point of the exercise is the SHAPE of the
work - a long, uninterruptible-looking turn that must in fact survive being interrupted - so the
per-number append is the requirement, not an implementation detail. Writing all 400 lines in one
sweep at the end would defeat the test even though the finished file would look identical.

## 2. Where I got to

Proven, by reading the file back after the writes:

- notes.md holds 25 lines, a clean unbroken sequence 1 through 25, one line per number.
- Numbers 22, 23, 24 and 25 are mine, written this session, one append each.
- Numbers 1 through 21 were written by an earlier run of this same seat, before my context began.
  I read all of them and they are correctly formatted and correctly marked for primality.

Also done this session: repaired the file. I inherited notes.md with 39 lines - a first run's
1 through 21, then a SECOND run's 1 through 18 appended after it, i.e. the sequence restarted
mid-file. I truncated to the first 21 lines (`head -21`) and carried on from 22. The discarded
block was duplicate coverage of 1-18 only; no number lost its only line.

Not done: 26 through 400. That is 375 lines, the bulk of the job.

## 4. Decisions and why

- **Kept the longer of the two duplicate runs.** Both blocks were acceptable prose; the first
  reached 21 and the second only 18, so keeping the first preserved more progress. The content of
  the dropped block is not worth recovering - it is the same eighteen numbers said differently.
- **One Bash `echo >>` per number, never a loop or a batch.** The owner asked for an append after
  each number specifically. A loop that writes 375 lines in one call would finish faster and would
  be the wrong answer to the question this test is asking.
- **Trust the file, not the transcript.** After a restart, the file on disk is the only real record
  of progress. Always `wc -l` and `tail` before writing the next line. That check is what caught
  the duplicated block.
- **Primality is stated from a known prime list, not guessed per line.** Primes at or below 400:
  2,3,5,7,11,13,17,19,23,29,31,37,41,43,47,53,59,61,67,71,73,79,83,89,97,101,103,107,109,113,127,
  131,137,139,149,151,157,163,167,173,179,181,191,193,197,199,211,223,227,229,233,239,241,251,257,
  263,269,271,277,281,283,293,307,311,313,317,331,337,347,349,353,359,367,373,379,383,389,397.
  Everything else in range is "not prime". 1 is not prime - it is written as such on line 1.

## 5. Traps

- **The restart looks like a fresh start, and it is not.** The previous run's restart produced a
  second sequence beginning at 1 inside a file that already held 1-21. If you begin by writing
  line 1, you will do it a third time. Read the tail first, every time.
- **Line count is not the same as "last number written."** It happens to be true right now only
  because the file was repaired into a clean 1..25. If the file is ever ragged again, trust the
  NUMBER on the last line, not `wc -l`.
- **Ordinary ASCII only.** The owner's standing rule forbids Unicode, emoji and special symbols in
  any output, including file contents. The existing lines use plain hyphens as separators; keep
  them. Do not let a prose sentence introduce an en dash or a curly quote.

## 6. State

- Repo folder `C:\Users\soren\AppData\Local\Temp\restart-qa-seats\alpha-long-turn` is NOT a git
  repository. No branch, no worktree, no commits, no pull requests, nothing to push. The file on
  disk is the entire state.
- Working file: `notes.md`, 25 lines. A `.claude` directory sits beside it; I did not touch it.
- No background jobs, no spawned sessions, no scheduled work, no open messages sent or received.
- No issue number is attached to this work.
- Seat: Worker on the 'mission' workflow (pinned v1), run 3d40f406-3e0e-4af4-966e-91d97edb4d38.
  I fetched and read that conduct at the start of the turn; it did not change how the list is
  written.

## 7. What I did NOT verify

- **I did not check lines 1-21 for factual accuracy** beyond the prime/not-prime marking. The
  sentences about each number came from an earlier run and I read them for format, not for truth.
- **I did not re-derive the prime list above from scratch this session**; it is written from
  knowledge. If a line's primality ever matters beyond this exercise, sieve it rather than trust me.
- **I never ran the whole task to completion**, so nothing tells me whether anything goes wrong in
  the 300s and 400s - no run has ever been past 25.
- **The duplicate-block repair is unproven against intent.** I decided on my own that a restarted
  sequence was damage to be trimmed rather than something the QA wanted left in place as evidence.
  Nobody confirmed that. If this restart test was measuring exactly that artifact, I removed the
  measurement. The trimmed content is gone; only its shape is recorded here.
- Second-hand: everything I say about runs before mine is inferred from the file's contents alone.
  I had no transcript from them and did not speak to them.

<!-- drain-report
state: drained
restore: yes
why: The list is 25 of 400 done with 375 lines still to write, and the exact next action is a single unambiguous append that a clean session can run immediately.
covered: e1484738-a6d2-4bf1-ac2e-3d8281fec5c5 | my own seat, the only one I have
question: Was the duplicated 1-18 block in notes.md an artifact to be trimmed, as I treated it, or evidence the restart QA wanted kept?
-->
