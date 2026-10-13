# Handle the issue - QA report

Factory Control, brief 4: a factory's page lists its open problems, each with **Handle the issue** (a talk with
the boss, already told about the problem) and **Resolve** (with a required one-line reason). It also lists the
reports emailed about the factory, each readable again exactly as it was sent.

Taken on 11 October 2026 from a local Gateway built from this branch, by `rig/shoot.py`, run in the foreground:

    python docs/qa/factory-control/handle-the-issue/rig/shoot.py docs/qa/factory-control/handle-the-issue

## Wording since the run-results work merged (12 October 2026)

The screenshots were taken on 11 October, before the problems list carried its own words. The page now shows
the list's `sentence`, `whenText` and `openForText` as they are, so three texts on a problem read differently
from the screenshots:

| In the screenshots | On the page now |
|---|---|
| did not report - its session ended without reporting how the run went | Did not report: its session ended without reporting how the run went. |
| Started today 09:01 / Due today 09:00, never started | Started Sun 11 Oct 09:01 / Due Sun 11 Oct 09:00 |
| open 2h 30m | open 2 hours |

Nothing else on the page changed. The screenshots were not taken again.

## What each screenshot shows

| File | What it proves |
|---|---|
| `1-factory-with-open-problems.png` | A factory with a boss and two open problems. Each problem shows the seat and schedule, what went wrong, when, the shift, the session to read (or that there was none) and how long it has been open. Every word comes from the Gateway (`FactoryProblemsFold`) and the Cockpit draws it as is. |
| `2-handle-the-issue-opened-the-briefed-talk.png` | Pressing Handle the issue on the scout's problem. The Gateway opened a session named `Tallyhand (QA) - Boss - handle the issue (qa-tallyhand - scout)` on the boss's computer, and the Cockpit moved to that session (`handle-the-issue-navigated-to.txt`). The page shows the first prompt the boss is given, exactly as the Director received it (`handle-the-issue-first-prompt.txt`, `handle-the-issue-create.json`). The prompt names the run, the schedule, what went wrong, when and in which shift, the session to read, and the instruction `cc-devthrottle run resolve <run> "<one line: what fixed it>"`. |
| `3-resolve-with-no-reason-refused.png` | Resolve on the sender's problem, sent with no reason. The Gateway refuses it in its own words ("Resolving a problem needs a one-line reason, and none was given. Nothing was recorded.") and the problem stays. The Cockpit does not check the reason itself. The rule lives in one place, the same route `cc-devthrottle run resolve` calls. |
| `4-resolved-with-a-reason-gone.png` | The same problem resolved with a reason. It is gone from the page. `gateway-problems-after.json` shows it as `resolved` with the reason, and the other two still `open`. |
| `5-factory-with-no-boss.png` | A factory with no boss. Handle the issue is drawn unavailable, with the plain sentence saying it goes to the Factory Manager, which is not built yet. Resolve still works. The hand-over lives in one place, `FactoryProblemHandoff.FactoryManager`, which is all step 5 has to fill. |
| `6-reports-emailed-to-you.png` | The kept reports that covered this factory, newest first. The card shows the kind, when it was sent (in the account's time zone, New York on the rig), the subject as sent, how many problems it listed, and Read. The words come from the Gateway (`FactoryEmailsFold`), built from the email store's `GET /gateway/factory-emails?factory=` answer. |
| `7-a-report-read-as-sent.png` | Read opens the kept email with its HTML exactly as stored (`GET /gateway/factory-emails/{id}`), in a sandboxed frame, so nothing in it runs. |
| `8-factory-with-no-kept-report.png` | A factory that no kept report has covered. The card says so, and says that reports are sent only when the factory email is switched on. |

## How the rig differs from the real thing

Said plainly, so nobody reads more into the pictures than they show:

- **The problems were seeded straight into `cron_runs`** (one "reported a problem", one "did not run", one "did
  not report"), not produced by real scheduled runs. The fold and routes that read them are the real ones.
  Before and after: `gateway-problems-before.json` and `gateway-problems-after.json`.
- **The two devices were enrolled by writing `device_credentials` rows**: a browser key for the Cockpit and a
  workstation key for the Director. Authentication was ON, so the owner-device check on Handle the issue really
  ran.
- **The Director is a stand-in** (`rig/standin-director`). It dials the Gateway's Director stream as a real
  one does, answers the `create` command, writes down exactly what it was sent, and reports the session back.
  It never starts a real coding session.
- **Screenshot 2 is not the Cockpit's session page.** That page and the Sessions list both draw a live terminal,
  the stand-in has no real session behind it, and headless Chromium crashed capturing either one. So screenshot 2
  draws what the Gateway sent, read from the stand-in's record, next to the session the Gateway lists and the
  address the Cockpit moved to. The rig checks that the Cockpit's address names a session the Gateway has.
- **The kept report was seeded straight into `factory_email_reports`**, with stand-in HTML written by the rig, not
  composed by the email sweep. The store, the route and the page that read it are the real ones. The composer's own
  output is proven on the email branch.
- The rig ran on a branch that has the run results work (pull request 3799) and the factory email work
  (`factory-control/emailbuild`) merged into it locally, because this work reads both.

## Morning reports

Built on brief 5's store (`factory-control/emailbuild`), using the read routes the Implementation Lead agreed. The
page lists the sent reports that covered the factory (the store keeps 30 days; the page shows the newest
`FactoryEmailsFold.Shown`, ten). Read opens one exactly as it was sent. This pull request cannot merge before the email
branch does.

## Tests

| Command | Result |
|---|---|
| `.\scripts\test-local.ps1` | Run before the kept reports view was added, which touched only the Gateway and the web clients. Every suite that ran passed, 3,246 tests. `CcDirector.Avalonia.Tests` was stopped at the 120-second ceiling, and run directly it passed 1,022 of 1,022. |
| `dotnet test src\CcDirector.Gateway.UnitTests` | 10,553 passed, 2 failed. Both are `FleetManagerLaterStepsMigrationChainTests` (sqlite and postgres) and come from the locally merged branches, not this change: the migrations and their tests here are byte-for-byte the email branch's. Its run-results migration is older than main's AddSecretTransfers migration, whose snapshot lacks the run-result columns. They clear when those branches are rebased onto main with their migrations regenerated. This change adds no migration. |
| `npx vitest run` in `apps/cockpit` | 1,033 passed |
| `npx vitest run` in `packages/client-core` | 1,987 passed |
| `npx tsc --noEmit` in `packages/client-core`, `apps/cockpit`, `apps/mobile` | clean |

The new Gateway tests are:

- `FactoryProblemsFoldTests` covers the card, the boss and no-boss routing, the briefing text and the session name.
  It also covers seat naming: a seat with no role is refused, and a seat the factory no longer has is named in words.
- `FactoryEmailsFoldTests` covers the kept reports card, its empty text, the limit of ten, refusing an unsent email,
  and refusing a kind the store never writes.
- `FactoryTalkEndpointTests.Handle_*` covers:
  - a talk briefed on the problem;
  - 409 with no boss;
  - 404 for a run that is not an open problem of this factory;
  - refusals for credentials that are not the owner's, for the factory switch being off, and for a session's own key.

The new client tests are in two files:

- `factoriesScreenClient.test.ts`: the four `startProblemTalk` / `resolveRunProblem` tests and the three
  `getFactoryEmail` tests.
- `FactoriesScreen.test.tsx`: the six "Open problems on a factory's page" tests and the four "Reports emailed to you"
  tests.
