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
and 500 output tokens (more than one to two seconds of streaming can produce).

## Runs

| Date | Seat | Purpose | Input | Output | Cache read | Cache write | Estimated dollars |
|---|---|---|---|---|---|---|---|
| 2026-10-16 | Architect | Proof in the issue's first comment (one session's stats as posted; earlier Architect runs not recorded) | 15 | 55 | 15,268 | 3,874 | 0.0293 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: plain | 3 | 6 | 0 | 3,742 | 0.0190 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: tool | 6 | 30 | 3,753 | 3,791 | 0.0221 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: abort (estimate) | 3,800 | 500 | 0 | 0 | 0.0390 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: resume | 3 | 6 | 3,742 | 30 | 0.0023 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: refusal | 6 | 68 | 3,751 | 3,826 | 0.0238 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 1: exit (estimate) | 3,800 | 500 | 0 | 0 | 0.0390 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: plain | 3 | 6 | 0 | 3,743 | 0.0190 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: tool | 6 | 30 | 3,754 | 3,792 | 0.0221 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: abort (estimate) | 3,800 | 500 | 0 | 0 | 0.0390 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: resume | 3 | 6 | 3,743 | 30 | 0.0023 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: refusal | 6 | 68 | 3,752 | 3,827 | 0.0238 |
| 2026-10-16 | Developer, phase 0 | Fixture recording 2: exit (estimate) | 3,800 | 500 | 0 | 0 | 0.0390 |

Recording 1 was discarded and recorded again because its fixtures carried text that this public
repository should not publish (the fleet's skill descriptions and the user name inside Pi's session
folder name). Recording 2 is what is committed.

## Totals

| Scope | Estimated dollars |
|---|---|
| Phase 0 (both recordings) | 0.29 |
| Mission so far, including the Architect's posted run | 0.32 |
| Left of the 10 dollar grant | 9.68 |
