# Proof - the Mentor page in the Cockpit (devthrottle_internal#2305, screens S6 and S7)

Third round, after the review of devthrottle#3538 and its delta review (`reviews/review-2305-cockpit.md` in the mission record).

## What these screenshots are, said plainly

The two screenshots are a **real render of the real Cockpit shell and the real Mentor page, with the real styles**,
against the **contract's own example data** - NOT against a running Gateway. The Gateway part of #2305 is not merged
yet; the screenshots against a running Gateway come once it is.

The data is the contract's 200 example as it stands on 4 October 2026, field for field (ids included): the readers are
`olivia@example.com` (Owner) and `priya@example.com` (Manager), who ran no sessions that week and so have **no
block**; `rob@example.com` (Developer) has one block, with one quoted prompt. The team therefore has three members
rather than the brief's two: the contract lists the Owner as a reader on every answer, so a team with a Manager and a
Developer and no Owner is not one the contract can describe.

The page's Gateway reads (`GET /teams` and `GET /teams/{teamId}/mentor`) are answered in the browser by
`harness/proofMentor.tsx`. Rerun with `python docs/proof/teams-2305/harness/take-screenshots.py`.

| File | What it shows |
|---|---|
| `mentor-manager.png`, `mentor-manager.txt` | S6, as Priya the Manager: every block of the week (only Rob's - Olivia and Priya have none and the page says nothing about them), "Mentor" in the rail after Skills |
| `mentor-developer.png`, `mentor-developer.txt` | S7, as Rob the Developer: his own block, the same words, and "olivia@example.com (the team's Owner) and priya@example.com (your Manager) read this same page." |

## Tests

| File | What it is |
|---|---|
| `test-output-client.txt` | The client against the contract (33 tests): the contract example verbatim; the quote byte for byte; null emails accepted; 403 refused; 404 and Teams-dark not offered; 400 thrown with the Gateway's sentence; a body that is not JSON thrown as a Gateway error; twenty ways of breaking the contract each thrown (empty-string emails and roles, a week whose dates do not match it, an answer for a week not asked for among them); the week arithmetic |
| `test-output-cockpit.txt` | The page and the rail (45 tests). Page tests are named by what the Gateway sent (scope everyone, scope own, refused, not offered) - the page never reads a role, so per-role proof is the Gateway's. Rail: shown on a page, hidden on refused and not offered, SHOWN and reported on a network failure, a fault and a contract-breaking answer, asked again on the shell's rhythm after a failure (and hidden when the Gateway then refuses), and it follows the team now on screen when a read is still in flight |
| `test-output-packages.txt` | Both whole packages and both TypeScript checks: client-core 1696 passed, Cockpit 638 passed |
| `red-run.txt` | Written by `harness/red-run.py`: eleven deliberate breaks, one at a time, each naming the change it made, the tests that went red with it, and the same tests green after the restore. The script fails if a run reports nothing, turns nothing red, or is not green again |
| `test-local.txt` | `.\scripts\test-local.ps1`: all ten suites Completed. No C# changed in this pull request |
