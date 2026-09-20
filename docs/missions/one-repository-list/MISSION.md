# Mission: One repository list, held on the Gateway

**Status: ACTIVE.** Chartered by the owner on 19 September 2026.
**Mission id:** `9d744f5c-c0d3-48b2-ae42-c8ed6ea04755`
**Branch:** `mission/one-repository-list`, cut from `origin/main`.

Conduct is the DevThrottle Method (the `devthrottle-method` skill) and the mission run-book (the
`mission` skill). Neither is restated here, and nothing in this document grants any seat an authority
the method does not already give it.

---

## 1. The mission

Move the catalogue of repositories, and the time each one was last used, onto the Gateway, and have
the Director, the Cockpit and the phone all show that one list in that one order.

## 2. The why

The owner opened the New Session screen twice on the same machine - once on the Director, once on the
Cockpit - and got three repositories on one and none on the other. Neither screen was lying. They are
reading two different things and calling both of them "your repositories".

Underneath that, the ordering is wrong everywhere except by accident. The last-used time that decides
which repository sits at the top is written in exactly one place in the whole product, and that place
is the desktop dialog's own Start Session button. Every other way of starting a session - the Cockpit,
the phone, a schedule, an agent - leaves it untouched. On a machine where nobody uses that button, the
recently-used list is permanently empty no matter how much work happens there.

In the owner's words:

> "We don't want to have to call down into the Director to see this, so you'd have to synchronise this
> to the Gateway. And also the last access would make sense to keep on the Gateway ... have the same
> view anywhere we use these dialogues, because the latest, I think that's wrong on some of the
> Directors, and it should be held on the Gateway instead of just held locally."

> "Make sure that it also gets the new synchronised last used list of repository ... the ones we use
> more frequently should bubble to the top, just like it does on the Director screen."

He should not have to know which screen he is on to know what he will see.

## 3. The goal

All three New Session screens, pointed at the same machine, list the same repositories in the same
order, most recently used first - and that order is correct because it counts every session start
however it was started.

Specifically, all of these are true at the end:

1. Starting a session from the Cockpit or the phone moves that repository to the top of the Director's
   list. Today it does not move at all.
2. A repository under a registered root folder that has never been opened appears on all three screens,
   below everything that has been used.
3. The Cockpit's New Session tab is the Director's layout, plus a machine row.
4. The phone's New Session flow is unchanged in shape and shows the corrected, ordered list.
5. The Director's dialog still opens and still lists repositories when the Gateway cannot be reached,
   and says on screen that it is showing its local fallback rather than quietly showing a different
   order.

## 4. Decisions

Every one of these is the owner's, in his words or as a direct answer to a question put to him. The
research that produced them is thrown away; what is not written here is lost.

| Decision | His words, or the question he answered |
|---|---|
| The catalogue and the last-access time live on the Gateway, not only on each Director | "It should be held on the Gateway instead of just held locally." |
| Every screen reads that one list rather than calling down into a Director | "We don't want to have to call down into the Director to see this." |
| Scope is the New Session tab only | "It's only the new session we're dealing with right now." |
| The phone's approval-prompt behaviour is left exactly as it is | "Leave the phone the way it is. The phone is actually working pretty well." |
| The phone receives the synchronised, ordered list | "Make sure that it also gets the new synchronised last used list of repository." |
| The Director reads the Gateway list, and falls back to its own local scan only when the Gateway cannot be reached - saying on screen when it is on the fallback | Asked directly; he chose this over Gateway-only and over leaving the Director alone. |
| The order is most recently used. Frequency was offered and declined | Asked directly, because he had used the word "frequently" in conversation. The order is recency. Nobody is to "correct" it into a count later. |

### Inferred, not stated

These three were decided by the Architect because the work cannot start without them. They are marked
so that any of them can be overturned without reopening the design.

| Inferred decision | Reasoning |
|---|---|
| The catalogue is keyed by machine, not by Director | Repositories live on a machine's disk, and one machine runs several Directors. Keying by Director would split one disk's repositories into several lists. |
| The Director pushes its root-folder scan up to the Gateway; the Gateway does not pull it on demand | A push survives the Director going offline afterwards. A pull returns nothing the moment the Director is unreachable, which is exactly when the other two screens still need the list. |
| A repository found but never opened carries no last-access time and sorts below everything that has one | This is already the Director dialog's own rule. Changing it here would be a second change nobody asked for. |

## 5. Design

### What is already true, and is the foundation

The Gateway has been recording the right thing all along and nothing reads it.

- `KnownRepositoryStore` holds, per tenant and per machine, every repository observed in a session,
  with the time. Its only writer is `SessionHistoryRecorder`, which runs for every session the Gateway
  sees regardless of which surface started it. This is the correct recency signal and it already exists.
- `GET /directors/{id}/known-repositories` already serves it. The phone already reads it. The Cockpit
  and the Director do not.

### What is wrong

- `RepositoryRegistry.MarkUsed` has exactly one caller in the product: the desktop New Session dialog's
  own start path in `MainWindow.axaml.cs`. The remote create path, `SessionWriteExecutor`, does not
  touch it. That is why a machine can have an empty recently-used list after days of work.
- The Director's dialog builds its list in `NewSessionDialog.BuildRepositoryList()` as a union of the
  registry and the root-folder scan held by `RepositoryMonitor`. `GET /directors/{id}/repos` returns the
  registry half alone, so the Cockpit is handed half a list.
- ~~The root-folder scan has no route to the Gateway at all. Searching the Gateway and the Director's
  control surface for the root-folder concept returns nothing.~~ **This was false, and it is corrected
  below.** It was written from a search for the words "root folder" on the Gateway, which returns nothing
  because the scan travels under a different name - as a repository snapshot. The road exists and it ships.

### The root-folder scan already reaches the Gateway - what is actually there

Corrected on 19 September 2026 during phase 2, after the Tech Lead and the Delivery Lead each verified it
against `origin/main` independently, and the Developer checked every line below before writing anything.
Phases 3, 5 and 6 all build on this same road and must not have to rediscover it.

**The inferred decision in section 4 - that the Director PUSHES its root-folder scan rather than the
Gateway pulling it - turned out to be RIGHT, and it was already built.** Nothing in this mission needs a
new Director-to-Gateway feed, and a second pusher on a second cadence would be two feeds describing one
machine. Two feeds describing one machine disagree, which is the defect this mission exists to end.

The road, end to end:

- `ControlApiHost.SnapshotRepositories()` (`src/CcDirector.ControlApi/ControlApiHost.cs`) maps
  `RepositoryMonitor.Snapshot()` - the root-folder scan itself - into `RepoStatusDto`, carrying the path,
  the name, the machine name and the Director id.
- `ControlApiHost.WireRepositoryPush()` pushes it up the tunnel, debounced three seconds, on every
  `Upserted`, `Removed` and `ScanCompleted`, plus the ten-second reseed in
  `GatewayStreamClient.ReseedAsync`.
- `DirectorHub.PushRepoSnapshot` (`src/CcDirector.Gateway/Streaming/DirectorHub.cs`) receives it, and
  hands each ACCEPTED push to its observers.

There are now THREE observers on that one accepted push, and each keeps the snapshot for a different
length of time:

1. `PushedRepositoryStore` - IN MEMORY, per Director, time-gated. It returns nothing once that Director
   has been offline longer than the staleness window. This is what `GET /repositories` serves.
2. `RepoHistoryStore` - durable, file-backed daily rows, for the morning report's drift numbers.
3. `DiscoveredRepositoryObserver` - **added by phase 2** - which folds the same push into
   `KnownRepositoryStore` as the DISCOVERED half of the one catalogue: durable, machine-keyed, and
   surviving the Director going away, which is exactly when the other two screens still need the list.

What was missing was therefore never the road. It was a durable, machine-keyed catalogue at the end of
it.

### The shape of the fix

One catalogue on the Gateway, holding both halves, ordered once, served to everyone.

- The Gateway owns the order. It serves the union already sorted, most recently used first, with
  never-opened repositories beneath. No client sorts for itself, and no client can invent a different
  answer. This is Critical Rule 7 applied to a list instead of a verdict.
- A repository carries its last-access time from the Gateway's observation of session starts, not from
  any Director's local file. The local file stops being the thing that decides the order.
- The Director pushes what it finds under its registered root folders; the Gateway keeps those as
  found-but-never-opened entries for that machine.
- The Director's dialog reads the Gateway list, and falls back to its existing local union only when
  the Gateway cannot be reached, showing a line on screen that says so.

### In scope

The New Session tab on all three clients, and the catalogue and ordering behind it.

### Out of scope

- Named Sessions, Resume Session, Handovers and the GitHub tab, on every client.
- The alpha-gated quick-launch cards.
- The phone's missing approval-prompt control. Ruled out by the owner, not forgotten.
- The Cockpit defect where clicking a repository row starts the session immediately instead of selecting
  it. It is real, and phase 4 rebuilds that screen anyway, so it is fixed there rather than twice. If
  phase 4 is cut, it returns as its own small pull request.
- Any change to what a repository is, or to worktrees.

### The style guides this mission builds to

`docs/CodingStyle.md` for code, `docs/VisualStyle.md` for every screen this mission touches. Both are in
this repository and both are binding; a seat that has not read the visual guide must not draw a screen.

## 6. Phases

Six phases, each landing as its own pull request, in this order. A phase's fix and the thing that stops
it regressing are one unit and never split across pull requests.

| Phase | What it does | Tech Lead? |
|---|---|---|
| 1. The order becomes correct | The Gateway catalogue becomes the source of the last-access time for the repository list. The one-caller write on the desktop side stops being what decides the order. | No - one Developer |
| 2. Root folders reach the Gateway | The Director sends the repositories it finds under registered root folders to the Gateway, which holds them as found-but-never-opened. The only genuinely new plumbing in the mission. | Yes - it crosses the Director, the tunnel and the Gateway store |
| 3. One list, one order, one route | The Gateway serves the union already ordered, so no client sorts for itself. | No - one Developer |
| 4. The Cockpit New Session tab | Redrawn to the Director's layout: search box, Name / Path / Last Used table with sortable headings, per-row remove, Browse, and the Director's wording throughout. Plus the machine row. | Yes - it is the largest surface and it has a visual guide to satisfy |
| 5. The phone reads the same list | The phone's repository step takes the one ordered list. No other change to that screen. | No - one Developer |
| 6. The Director reads the same list | The dialog reads the Gateway list, falling back to the local scan with a line on screen when the Gateway cannot be reached. Lands last, because it is the screen that works today and has the most to lose. | No - one Developer |

Phases 4 and 5 may run in parallel once phase 3 has merged; they touch different applications and share
only the route phase 3 landed. Nothing else in this list may be reordered: each phase depends on the one
before it.

## 7. The check

**What counts as proven here.** Green tests alone do not. Every phase owes the mission check below AND
its own behavioural proof from the table in section 6 - the flow and the failure case, not one success
run. Each proof is committed beside the code.

**The mission check, which any seat can run by itself**, from the worktree root:

```
npm run typecheck
npm test --workspaces --if-present
dotnet test src/CcDirector.Gateway.UnitTests
dotnet test src/CcDirector.Core.Tests
dotnet test src/CcDirector.Avalonia.Tests
```

Zero failures. Not "zero new failures", and no baseline of known-red tests is to be quoted in this
mission on any platform - a test that is red is a defect in the test or in the product, and the seat that
finds it names which and fixes it or deletes it.

**Each phase additionally proves its own row**, and proves it can fail: revert the change, watch that
proof go red with the reported symptom, watch the rest stay green, restore. A proof nobody has watched
fail is decoration.

Phase 6 owes one extra proof that is easy to skip and must not be: make the Gateway unreachable and
screenshot the Director's dialog. It must still list repositories, and it must say it is on the fallback.
A fallback path that has never been seen working is not a proven one.

## 8. Merge plan

One pull request per phase, merged to `main` as each phase finishes, in the order above. Small and often:
this touches three clients and a shared route, and a single large pull request across all of them would be
waved through rather than read.

A pull request that cannot merge the day it is opened was too big - split it. The branch is rebased onto
`origin/main` before each pull request, because `main` moves fast in this repository and a branch built on
a base fetched hours ago is already stale.

Every pull request is reviewed by a Reviewer running a **different agent family** from the seat that wrote
the code, before it opens. The seat being judged never arranges its own review.

## 9. Where it ends

Merged to `origin/main`, all six phases. Committed is not done, pushed is not done, and an open pull
request is not done.

The mission's record - this document, the decisions, the reviews and the proofs - lands merged as well,
in this folder, alongside the code.

No release is part of this mission. Shipping it to the fleet is a separate decision and it is the owner's.

## 10. Questions

Asked up front and all answered before the mission was chartered. They are recorded in section 4 with the
owner's answers.

| Question | Recommendation | His answer |
|---|---|---|
| Where should the empty repository list be fixed? | On the Director, so every client gains it at once | Went further: hold the catalogue and the last-access time on the Gateway |
| How far should the Cockpit's New Session dialog go? | The New Session tab only | The New Session tab only |
| Should the phone gain an approval-prompt control? | Yes | No - leave the phone as it is, but give it the synchronised list |
| Should the Director's own dialog read the Gateway too? | Gateway first, local scan as the fallback | Gateway first, local scan as the fallback |
| Recency or frequency? | Most recently used | Most recently used |

**Nothing is open.** If something genuinely undecidable turns up mid-run, the Delivery Lead brings the
owner in with a dev report rather than guessing - and does not stop the rest of the mission while it waits.
