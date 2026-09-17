# Architect handover - Remove the Network Port mission

You are the incoming Architect. The outgoing one was reset for context, not because anything went
wrong. **Everything you need is in files; nothing important lives only in the old conversation.**

Read in this order, then act:
1. `MISSION.md` in `D:\ReposFred\devthrottle-noport` - the charter, every ruling, the running state.
2. `MISSION-PLAN.md` - the phases, and **five design premises the outgoing Architect got WRONG** which
   Managers corrected on evidence. Do not re-derive them; they are corrected in place.
3. `QA-REPORT-REQUIREMENTS.md` - what the owner's final report must prove, written before the evidence
   so it could not be fitted to whatever turned up.
4. `PHASE-*-REPORT.md` - one per phase. Each has a "what is NOT proven" section. That habit is why
   this mission's claims can be trusted; keep demanding it.

## Where it stands

**The Director is portless and PROVEN.** Two Directors from the new build, alive and registered, own
zero listening sockets while holding 14 outbound Gateway connections. The same query in the same
instant caught the owner's own Directors listening on 7879 and 7881 - that positive control is what
makes zero mean zero rather than a broken query. Evidence at `docs/qa/phase5-noport/`.

| Phase | State |
|---|---|
| 1, 1b, 2, 2b, 3, 4 | DONE and proven, Windows and macOS |
| 5 - delete the Director's listener | DONE and proven; final suite re-run was the last item |
| 6 - delete the launcher's listener + the guard (phase 7 folded in) | IN PROGRESS on its own branch |
| Independent inspection of the whole mission | NOT STARTED - yours to call |
| QA report | Draft exists; see below |
| Merge to main, then release | Yours alone |

## Open seats

- `967c051d` Phase 5 Manager - retire once its final suite re-run lands.
- `f5c7ebf9` Phase 6 Manager - working in `D:\ReposFred\devthrottle-noport-p6` on branch
  `mission/remove-network-port-p6`. **You must merge that branch into the mission branch** when done.

Retire a Manager at every phase boundary (`cc-devthrottle session done <id>`) and seat a fresh one.
They are disposable on purpose; their state lives in the branch and the reports.

## What remains, in order

1. Phase 6 finishes; merge `mission/remove-network-port-p6` into `mission/remove-network-port`.
2. **Call the independent inspection** - Codex, its own detached worktree at the branch tip, told to be
   adversarial and NOT to trust the mission's own reports. Both previous inspections found real
   defects every green suite had missed; one would have silently refused commands the owner's agents
   need. Have it write to a FILE and report with ONE line pointing at it. (Corrected 17 September 2026:
   since the Message Load mission a message is queued in the inbox and read in full, and sessions report
   with `cc-devthrottle session raise`; the review still belongs in a file.)
3. Fix whatever it finds - **the inspector never fixes; a Manager does.**
4. Finish the QA report. Draft with phases 1-4 written and cited is in the outgoing Architect's
   scratchpad as `QA-REPORT-DRAFT.md`; move it into the branch.
5. Merge to main. **Only the Architect lands anything.**
6. The owner wants a new Director built and tested on this - a portless release after the merge.

## Standing rulings - do not re-open

- Session communication always goes through the Gateway. No local fast path, no second door.
- Process lifecycle is NOT session communication and must work when the Gateway is down.
- An agent may change how the product BEHAVES, not WHO IS ALLOWED IN. Settings and handovers allowed;
  device enrolment, account identity and force-kill refused. This is the owner's own ruling.
- No fallbacks. Never "try the Gateway and fall back to the port".
- The launch-window race is SETTLED, not deferred. No fix; all three candidates are worse.

## Hazards that have already cost this mission real time

- **Incremental builds serve STALE assemblies while reporting success.** Delete `obj` and `bin` for any
  project whose result you intend to trust. This produced three consecutive wrong diagnoses.
- **The local gate is not a reliable pass/fail signal.** The landing criterion is COMPARATIVE: a run
  counts only against a run of its PARENT, and **the parent must be run more than once** - a parent's
  first run once came back clean and a single control would have convicted the mission of a regression
  it did not cause. Largely traced to one cause, filed as issue #2445.
- **In a shared worktree, `git add <your file>` does NOT scope your commit.** The index is shared;
  `git commit` takes the whole index. The outgoing Architect swept a Manager's half-staged rename into
  a mission commit this way and left four commits that do not compile. Use `git commit -- <path>`, or
  do not write in a Manager's tree at all.
- **A test rig will run the INSTALLED tools unless you fight the machine PATH.** Two independent
  Managers hit this; it would have produced a green proof of the OLD tooling working.

## The habit that made this work

Every claim gets checked against the artifact, not the report of it. This mission found: a branch
everyone believed carried a fix and carried nothing; a test passing for 22 days while testing nothing;
a macOS mechanism completely inert while every test was green; and tests that stayed green with the
defect restored. **All of them looked like success from the outside.** Verify, then believe.
