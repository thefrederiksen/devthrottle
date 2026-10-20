# Spend: Pi model calls

The owner granted **10 dollars in total** for Pi model calls for the whole mission (issue #4070,
section 4). Phase 0's share is capped at 1 dollar.

Pi reports a cost of 0 because the configured Azure model has no price set, so this record counts
**tokens** and prices them at a deliberately high rate:

| Token kind | Rate used here |
|---|---|
| Input (uncached) | 5 dollars per million |
| Cached input (Pi's `cacheRead`) | 0.50 dollars per million |
| Output | 40 dollars per million |
| Cache write (Pi's `cacheWrite`) | charged as uncached input, 5 dollars per million (the brief named no rate for it, so the high one is used) |

Tokens come from `get_session_stats` at the end of each run. Those totals are cumulative for a Pi
session, so a run that resumes an earlier session is entered as the difference.

**Aborted and killed runs report zero tokens.** Pi did not record usage for a reply that was cut
off, but the provider still read the prompt and streamed some thinking. Those rows are an
**estimate**, not a measurement: 3,800 uncached input tokens (the size of the whole first request)
and 500 output tokens (more than one to two seconds of streaming can produce). The proof script
estimates each such reply the same way: the size of the run's first request plus 500 output tokens.

**Two figures per row (2026-10-20).** The token columns hold Pi's recorded tokens plus that estimate.
**Recorded dollars** prices only what Pi recorded (from the run's `spend.txt`, or the row itself
when it holds no estimate), and is the real spend. **Upper bound dollars** prices the token
columns, estimate included. The two differ most on Windows runs 7 and 8, where the provider refused
nearly every attempt with 429 engine_overloaded: the script counted each empty attempt as a reply cut
off by Stop, although a refused request streamed nothing.

## Runs

| Date | Seat | Purpose | Input | Output | Cache read | Cache write | Recorded dollars | Upper bound dollars |
|---|---|---|---|---|---|---|---|---|
| 2026-10-16 | Architect | Proof in the issue's first comment (one session's stats as posted; earlier Architect runs not recorded) | 15 | 55 | 15,268 | 3,874 | 0.0293 | 0.0293 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: plain | 3 | 6 | 0 | 3,742 | 0.0190 | 0.0190 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: tool | 6 | 30 | 3,753 | 3,791 | 0.0221 | 0.0221 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: abort (estimate) | 3,800 | 500 | 0 | 0 | 0.0000 | 0.0390 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: resume | 3 | 6 | 3,742 | 30 | 0.0023 | 0.0023 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: refusal | 6 | 68 | 3,751 | 3,826 | 0.0238 | 0.0238 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: exit (estimate) | 3,800 | 500 | 0 | 0 | 0.0000 | 0.0390 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: plain | 3 | 6 | 0 | 3,743 | 0.0190 | 0.0190 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: tool | 6 | 30 | 3,754 | 3,792 | 0.0221 | 0.0221 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: abort (estimate) | 3,800 | 500 | 0 | 0 | 0.0000 | 0.0390 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: resume | 3 | 6 | 3,743 | 30 | 0.0023 | 0.0023 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: refusal | 6 | 68 | 3,752 | 3,827 | 0.0238 | 0.0238 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: exit (estimate) | 3,800 | 500 | 0 | 0 | 0.0000 | 0.0390 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 3: exit before acknowledgement (Pi killed before it reached the model) | 0 | 0 | 0 | 0 | 0.0000 | 0.0000 |
| 2026-10-16 | Developer, phase 1 integration | Preamble proof, run 1 (one prompt through the API-mode host; the same as run 2 without get_commands) | 3 | 6 | 0 | 4,842 | 0.0245 | 0.0245 |
| 2026-10-16 | Developer, phase 1 integration | Preamble proof, run 2, the one recorded in preamble-proof.md (get_commands made no model call) | 3 | 6 | 0 | 4,836 | 0.0244 | 0.0244 |
| 2026-10-18 | Developer, phase 6 | Dry run by hand, devlinux, before the script (PONG, tool, Stop, OTTER-58 in the terminal, code word, QUEUED-OK, a long terminal turn); one reply cut off by Stop, estimated | 5,799 | 873 | 35,262 | 5,966 | 0.1114 (split not kept: the upper bound) | 0.1114 |
| 2026-10-18 | Developer, phase 6 | Proof script first dry run, devlinux (evidence not kept: the run that changed the user's tool links); one reply cut off by Stop, estimated | 5,727 | 616 | 28,974 | 5,900 | 0.0487 | 0.0973 |
| 2026-10-18 | Developer, phase 6 | Proof script second dry run, devlinux, the committed revision (evidence not committed; every step passed); one reply cut off by Stop, estimated | 5,727 | 605 | 28,963 | 5,889 | 0.0482 | 0.0968 |
| 2026-10-18 | Developer, phase 6 | Proof script third dry run, devlinux, with the test Director's own HOME (proofs/phase-6/linux-dry-run-20261018-122023); one reply cut off by Stop, estimated | 4,568 | 606 | 23,169 | 4,731 | 0.0396 | 0.0823 |
| 2026-10-19 | Developer, phase 6 | Screens explored by hand on devlinux (one prompt, stopped mid-turn; Pi recorded zero, estimated per this file) | 4,700 | 500 | 0 | 0 | 0.0000 | 0.0435 |
| 2026-10-19 | Developer, phase 6 | Proof script fourth dry run, devlinux, with the screens (evidence not kept: the screen checks' words were cut short by a quoting fault, since fixed); one reply cut off by Stop, estimated | 4,568 | 1,874 | 24,437 | 5,999 | 0.0973 | 0.14 |
| 2026-10-19 | Developer, phase 6 | Proof script fifth dry run, devlinux, with the screens, the revision committed for the QA run (evidence not kept: a dry run, superseded by the recorded run); one reply cut off by Stop, estimated | 4,566 | 593 | 23,146 | 4,716 | 0.0390 | 0.0817 |
| 2026-10-20 | Developer, phase 6 | Proof script recorded run, devlinux, commit 602beeb0f built fresh in a clean worktree (proofs/phase-6/linux-20261020-003750); one reply cut off by Stop, estimated | 4,565 | 609 | 23,157 | 4,731 | 0.0397 | 0.0824 |
| 2026-10-20 | Developer, Cockpit mid-turn screen | Diagnostic run, devlinux, the recorded run's tree with the mid-turn capture timed (evidence not kept; its probe held the phone back, so Stop came after the turn ended and the 600-line reply ran in full); spend file not kept, estimated | 4,600 | 1,900 | 23,000 | 4,800 | 0.1000 (split not kept: the upper bound) | 0.10 |
| 2026-10-20 | Developer, Cockpit mid-turn screen | Diagnostic run, devlinux, the same tree with the probe corrected (evidence not kept: it measured the Cockpit drawing Pi's first word 83 milliseconds after the Director stored it); one reply cut off by Stop, estimated | 5,152 | 620 | 26,103 | 5,329 | 0.0446 | 0.0903 |
| 2026-10-20 | Developer, Cockpit mid-turn screen | Proof script run with the mid-turn screen waiting for the reply, commit 26a227479 built fresh (proofs/phase-6/linux-20261020-010149); one reply cut off by Stop, estimated | 4,568 | 707 | 23,270 | 4,832 | 0.0442 | 0.0869 |
| 2026-10-20 | Developer, phase 6 | Proof script, Windows, runs 1 to 3 (each stopped before Pi was started, on faults in the script's Windows branch, since fixed) | 0 | 0 | 0 | 0 | 0.0000 | 0.0000 |
| 2026-10-20 | Developer, phase 6 | Proof script run 4, Windows, Pi 1.3.0 (proofs/phase-6/windows-20261020-012624); the first reply took over seven minutes and two replies were cut off by Stop, estimated | 13,590 | 2,780 | 36,032 | 0 | 0.0921 | 0.1972 |
| 2026-10-20 | Developer, phase 6 | Proof script run 5, Windows, Pi 1.3.0 (proofs/phase-6/windows-20261020-015945) | 5,750 | 1,676 | 44,992 | 0 | 0.1183 | 0.1183 |
| 2026-10-20 | Developer, phase 6 | Proof script run 6, Windows, Pi 1.3.0 (proofs/phase-6/windows-20261020-021210); two replies cut off by Stop, estimated | 26,406 | 1,190 | 49,600 | 0 | 0.0997 | 0.2044 |
| 2026-10-20 | Developer, phase 6 | Proof script, Windows, run 7 first attempt (stopped in setup: the test Gateway did not answer within two minutes on a saturated machine; Pi never started) | 0 | 0 | 0 | 0 | 0.0000 | 0.0000 |
| 2026-10-20 | Developer, phase 6 | Proof script run 7, Windows, Pi 1.3.0, every model-reply wait five minutes (proofs/phase-6/windows-20261020-041719); Pi recorded zero tokens: the provider refused the turns with 429 engine_overloaded, and the script counted its 21 empty replies as replies cut off by Stop, so this is an upper estimate | 0 | 10,500 | 0 | 0 | 0.0000 | 0.42 |
| 2026-10-20 | Developer, phase 6 | Proof script run 8, Windows, Pi 1.3.0, the last try (proofs/phase-6/windows-20261020-061534); Pi recorded 6,493 input and 33 output tokens: the provider refused nearly every attempt with 429 engine_overloaded and Pi retried each turn three times, and the script counted its 24 empty attempts as replies cut off by Stop, so this is an upper estimate | 162,325 | 12,033 | 0 | 0 | 0.0338 | 1.2929 |

Recording 1 was discarded and recorded again because its fixtures carried text that this public
repository should not publish (the fleet's skill descriptions and the user name inside Pi's session
folder name). Recording 2 is what is committed, plus recording 3, the exit before acknowledgement, added at the Delivery Lead's request.

## Totals

Both ways, as the Delivery Lead asked on 2026-10-20: **recorded** is what Pi's own session files say was used,
priced at the rates above, and is the real spend; the **upper bound** adds the estimate for every reply Pi recorded as
zero. Two rows kept no split (the 2026-10-18 dry run by hand and the first mid-turn diagnostic run), so they count at
their upper bound in both columns.

| Scope | Recorded dollars | Upper bound dollars |
|---|---|---|
| Phase 0 (both recordings) | 0.13 | 0.29 |
| Phase 1 integration (the two preamble proof runs) | 0.05 | 0.05 |
| Phase 6 (proof runs; its share is capped at 2 dollars) | 0.96 | 3.25 |
| Mission so far, including the Architect's posted run | 1.17 | 3.61 |
| Left of the 10 dollar grant | 8.83 | 6.39 |
