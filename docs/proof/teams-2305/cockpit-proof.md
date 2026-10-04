# Proof - the Mentor page in the Cockpit (devthrottle_internal#2305, screens S6 and S7)

## What these screenshots are, said plainly

The two screenshots are a **real render of the real Cockpit shell and the real Mentor page, with the real styles**,
against the **contract's example data** - NOT against a running Gateway. The Gateway part of #2305 is not merged yet;
the screenshots against a running Gateway come once it is.

The data is a test team of two, "Teams test":

- `priya@example.com`, a Manager, who ran no sessions that week and so has **no block**;
- `rob@example.com`, a Developer, who has one block, with one quoted prompt.

The page's Gateway reads (`GET /teams` and `GET /teams/{teamId}/mentor`) are answered in the browser by
`harness/proofMentor.tsx` with JSON shaped exactly as the contract gives it, plus the `readers` list the Tech Lead
ruled the Gateway adds (`[{ email, role }]`, the team's Owner and Managers) and without `personName` (being dropped:
blocks are headed by `personEmail`). Rerun with `python docs/proof/teams-2305/harness/take-screenshots.py`.

| File | What it shows |
|---|---|
| `mentor-manager.png`, `mentor-manager.txt` | S6, as Priya the Manager: every block of the week (only Rob's - Priya has none and the page says nothing about her), "Mentor" in the rail after Skills |
| `mentor-developer.png`, `mentor-developer.txt` | S7, as Rob the Developer: his own block, the same words, and "priya@example.com, your Manager, reads this same page." |

## Tests

| File | What it is |
|---|---|
| `test-output-client.txt` | The client against the contract (14 tests): every field verbatim, the quote byte for byte, 403 refused, 404 and Teams-dark not offered, 400 thrown, malformed bodies thrown, the week arithmetic |
| `test-output-cockpit.txt` | The page and the rail (32 tests): Owner, Manager, Developer, Collaborator, no team, Teams off; quote text exact; empty week invents no block; no leaderboard or ranking; no link to browse prompts; the AI footer; the week chooser |
| `test-output-packages.txt` | Both whole packages and both TypeScript checks: client-core 1677 passed, Cockpit 625 passed |
| `red-run.txt` | The tests going red: the page with the quote trimmed, the blocks sorted by name, the empty-week message removed, and the rail entry decided from the role label (4 red); the client with the quote trimmed and 403 not read as a refusal (3 red). Each mutation was then restored and the same runs went green |
| `test-local.txt` | `.\scripts\test-local.ps1`: all ten suites Completed. It names the parked Gateway and Core suites as a coverage gap; no C# changed in this pull request, so they were not run |
