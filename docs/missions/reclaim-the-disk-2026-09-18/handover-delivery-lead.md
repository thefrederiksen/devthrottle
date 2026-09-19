# Handover to the Delivery Lead

From the Architect, 18 September 2026. The owner has said go.

## Your mandate

`docs/missions/reclaim-the-disk-2026-09-18/mission.md` in this worktree is your whole mandate. Read
it in full before anything else. Take your answers from it. Where it is silent, ask the Architect
(the session that opened you) with `cc-devthrottle message send --reply-wanted`, and carry on with
everything that does not depend on the answer.

Before you start, fetch and follow your conduct:

    cc-devthrottle workflow instructions mission --version 29
    cc-devthrottle skill get devthrottle-method

You are the Delivery Lead. The spawn flag calls your role Manager because that is the only name the
flag accepts; the method's section "If you are the Delivery Lead" is the one that binds you.

## Where things are

- Issue: https://github.com/thefrederiksen/devthrottle/issues/3120
- Branch: `mission/reclaim-the-disk`, pushed. It holds only the mission folder.
- This worktree: `D:\ReposFred\devthrottle-reclaim-the-disk`. It is yours for the record. You never
  build in it. Every Developer gets its own worktree cut from origin/main.
- `evidence/` holds the two read-only measurement scripts behind the numbers in the document. They
  are evidence, not product code, and they are Python on purpose. The product is C#.

## What the owner has and has not granted

- Granted: building, committing, pull requests, merging to main on local green plus a review by a
  different agent family.
- NOT granted: removing anything on his machine. Every removal in this mission happens on fixture
  trees the tests build. On the real machine the tool is only ever run to scan and recommend.
- NOT granted: releasing, or deploying the Gateway. Phase 5 ends at merged.

## Things that will bite you, from this machine's own history

- The shared checkout `D:\ReposFred\devthrottle` was 193 commits behind origin/main today. Read code
  with `git show origin/main:<path>`, never from that tree.
- A gate run gets its own worktree, never the Developer's.
- Status-check and move untracked files before removing any worktree; a forced removal deletes them.
- List sessions by name before retrying a spawn that timed out; it may have seated.
- A Codex seat that hits its usage limit reads as plain Waiting. Read its screen, not its state.
- Tools on this machine run from `%LOCALAPPDATA%\cc-director\instances\default\bin`, not from
  `cc-director\bin`.

## Naming

Every seat you open is named `Reclaim the Disk - <Role> - <what this seat does>` and is attached to
this mission. Put that sentence in every mandate you hand on.

## Your first moves

1. Land the mission folder on main as a documents-only pull request, so the record exists before the
   code does.
2. Open one Developer on phase 1. Phases 1 and 2 need no Tech Lead. Phase 3 does.
3. Report to the Architect when phase 2 is merged and again when the QA report is ready. Nothing in
   between unless something is undecidable.
