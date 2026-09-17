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

## The signed-out landing is a FIFTH piece of work, and it is not done

The apps Worker fixed the phone's device-key gate to carry `next=` - it found that itself - but that covers
only the journey that starts INSIDE the app. The journey from the PRINTED ADDRESS, signed out, is still
broken on both surfaces, exactly as `RULING-phase-3b-the-one-address.md` sets out:

- desktop: `/r/{id}` bounces to `/signin?next=/r/{id}`, and after the round trip `DeviceCallback` does a
  ROUTER navigate to `/r/{id}`, which is not a Cockpit route. It lands on Not found.
- phone: the bounce to `/signin` is eaten by the mobile front door, which redirects to `/mobile/` and drops
  the query string, so `next` never survives to be followed at all.

Neither Worker built the ruling's answer (a public, tenant-free `/r/{id}` targeting a new `/report/:reportId`
route in both shells), because the ruling did not reach them - see the note on messaging below. The Gateway
Worker's route is authenticated and looks the report up; the apps Worker's shells have no `/report/:reportId`.

**The plan:** merge all four branches into the mission branch first, then seat ONE Worker on the merged
branch whose whole mandate is the ruling - the public route and the two shell routes are one small coupled
change and splitting them across two branches would cost more than it saves.

## Messaging my own Workers did not work, and I misread that once

`cc-devthrottle message send` answered `Not delivered: unknown error` on almost every attempt (issue #3009),
`message ask` has been removed, and an agent may not type into another session. One message reported
`queued.`; the rest did not land. I briefly took the apps Worker's phone `next=` fix as proof that a message
HAD landed - it was not: that Worker found the defect on its own and says so in its report. Read the
terminals and read the branches; do not infer delivery from a Worker doing something you also asked for.
