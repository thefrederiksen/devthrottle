# Phase 3b state - one conversation, one Send, and a link that lands where you are

Issue #3025 (child of #2936). Mission branch `mission/dev-reports-p3b`. ACTIVE.

## The shape of the phase

Four Workers, four worktrees, all branched from `mission/dev-reports-p3b`. Their files are disjoint on
purpose, so the merges back into the mission branch are trivial. The Manager merges each branch in; nothing
goes to main - the Architect lands it.

| Worker | Branch and worktree | Owns | Brief |
|---|---|---|---|
| the page | `mission/dev-reports-p3b-page`, `D:\ReposFred\devthrottle-p3b-page` | `dev-report-notes.js`, its tests, `CONTRACT.md`, the notes browser proof | BRIEF-phase-3b-page-worker.md |
| the Gateway | `mission/dev-reports-p3b-gateway`, `D:\ReposFred\devthrottle-p3b-gateway` | everything under `src/`, the Gateway tests | BRIEF-phase-3b-gateway-worker.md |
| the apps | `mission/dev-reports-p3b-apps`, `D:\ReposFred\devthrottle-p3b-apps` | `apps/`, the client-core view files, `tools/cc-dev-reports` | BRIEF-phase-3b-apps-worker.md |
| the proof | `mission/dev-reports-p3b-proof`, `D:\ReposFred\devthrottle-p3b-proof` | the rig, the sample report, the screenshots | BRIEF-phase-3b-proof-worker.md |

## The interfaces the Workers build to - settled by the Manager up front, not negotiated later

- **Hosted** means the page has received a valid `restore` over the port. Hosted, the page draws the note
  taking parts only; the app draws the one conversation and the one Send. Unhosted keeps today's full tray.
- **The report record carries two finished strings** the clients render verbatim (repository rule 7):
  `sessionLabel` = `"<number> <session name>"`, `backLabel` = `"back to <number> <session name>"`. No
  internal identifier is ever in them, and a client shows nothing rather than composing its own.
- **The one address** is `GET /r/{reportId}` on the Gateway. A phone goes to
  `/mobile/session/{sessionId}/reports/{reportId}`; anything else to
  `/session/{sessionId}?tab=reports&report={reportId}`. The route is registered BEFORE the mobile redirect,
  or that middleware eats every phone link.

## What is explicitly out of scope

The Director's own reports pane (that is phase 4, and a Worker is already on it in another worktree).
Anything about the shape check, publishing, delivery, or the prompt fold - phases 1 and 2 shipped those and
the owner did not complain about them.

## The seats

| Worker | Session |
|---|---|
| the page | `71a27ce3` |
| the Gateway | `9ee22395` |
| the apps | `f67d115d` |
| the proof | `fdb2d403` |

The page and proof seats were re-seated once: their first prompt never submitted - it sat corrupted and
unsent in the input box and the seat never started. A spawn that returns an identifier is not a seat that
started; read the terminal before believing one is working. The replacement prompts were one line each.
