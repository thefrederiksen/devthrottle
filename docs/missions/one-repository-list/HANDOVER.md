# Handover to the Delivery Lead

The Architect's seat is done driving. This note is everything a fresh Delivery Lead needs; it is kept
short and current on purpose, so that resetting a seat costs nothing.

## Where things stand

- **Mission document:** `docs/missions/one-repository-list/MISSION.md`, in this folder. It is finished and
  it is where your answers come from. Read it before anything else.
- **Mission id:** `9d744f5c-c0d3-48b2-ae42-c8ed6ea04755`. Every seat you open is attached to it.
- **Branch:** `mission/one-repository-list`, cut from `origin/main`. The worktree you are in is the
  mission's only worktree. Never work in the shared checkout and never pull it.
- **Done so far:** this document and the mission document. No product code has been touched.
- **Next:** phase 1 of section 6. One Developer, no Tech Lead.

## What you own

You drive this to the goal in section 3 of the mission document. You check in on your own heartbeat
rather than waiting to be called. A blocked mission asks and keeps going; writing a status note and
stopping is not a state this mission may rest in.

You run the mission check yourself before accepting any finished work - a "done" report is a claim, your
own run is the evidence. You send every Developer's code and every finished phase to a Reviewer running a
**different agent family**; the seat being judged never arranges its own review. You never build and you
never read diffs: the moment your context fills with code you can no longer hold the goal, which is what
a Tech Lead is for.

You shut down every seat you open. You do not ask a seat to shut itself down, and you never kill
uncommitted work - make it commit and push first.

## Naming every seat you open

The fleet map groups by Mission, and a session with no Mission is invisible on it. Every seat:

```
cc-devthrottle session spawn "<this worktree>" \
  --mission 9d744f5c-c0d3-48b2-ae42-c8ed6ea04755 \
  --controlled-by self \
  --role <Manager|Worker> \
  --name "One Repo List - <seat> - <what it does>" \
  --prompt "Read docs/missions/one-repository-list/MISSION.md - it is your whole mandate, and read the handover beside it. Follow them."
```

The mission short name is **One Repo List** for every seat, so they group together. The method's seat
names are Delivery Lead, Tech Lead, Developer and Reviewer; the product's `--role` field only accepts
Architect, Manager and Worker, so use `Manager` for a Tech Lead and `Worker` for a Developer or a
Reviewer, and put the method's name in the session name where a person reads it.

Put this convention into the mandate of any seat that will itself open seats, with the mission id in it.

## Bothering the owner

Once, at the end, with the QA report - what changed, what it does for him, what is proven, what is not.
Not per phase, and not for approval to continue.

The exception is real: if something genuinely undecidable turns up - a product question nobody has
answered, or a discovery that changes what the mission is for - raise it as a dev report
(`cc-dev-reports open <file>`), recommend one option, and carry on with everything that does not depend on
the answer. Guessing to look autonomous is the worse failure by a distance.

## Two things that will bite

1. **`main` moves fast in this repository.** Rebase onto `origin/main` before each pull request. A branch
   built on a base fetched hours ago is already stale, and reading a stale tree and reporting what you
   find as fact is the most expensive mistake made here.
2. **Zero failing tests, on every platform.** Do not quote a baseline of known-red tests. A red test is a
   defect in the test or in the product, and you name which.
