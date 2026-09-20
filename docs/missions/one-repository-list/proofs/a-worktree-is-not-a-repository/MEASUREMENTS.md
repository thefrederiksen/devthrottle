# A worktree is not a repository - what the ground actually looks like

Mission: One repository list, held on the Gateway. A follow-on agreed with the owner on 20 September
2026 after he opened the Cockpit's New Session dialog against a Windows machine and read

> "556 repositories on this machine, most recently used first."

**Measured before anything was built**, because every seat on this mission that measured first found the
brief wrong about something, and twice that stopped the mission building the wrong thing. This document
is the measurement and the recommendation it leads to. **Nothing in the catalogue has been changed.**

**The machines are lettered rather than named, and no row is quoted in full, because this repository is
public.** Machine A is the Windows machine the owner was looking at; machine B is the Mac this work is
written on; machine C is the Windows laptop.

---

## 1. How it was measured

Read from the LIVE hosted Gateway on 20 September 2026 at about 22:20 UTC, with the Director's own
token, through the two routes a client uses:

- `GET /directors/{id}/known-repositories` - the one catalogue, exactly as the Cockpit and the phone
  read it, already ordered;
- `GET /repositories` - the Directors' pushed root-folder scan, which carries for each scanned
  repository the list of its **live git worktrees**, with their paths.

The second is what makes a worktree answerable from here at all: a worktree is invisible to the scan as
a repository, but every repository the scan DOES find reports the worktrees hanging off it. A catalogue
row whose path appears in some repository's worktree list is a live worktree of that repository, stated
by git on the machine that owns the path, not inferred from the path's text.

On machine B, every catalogue row was additionally checked against the disk directly, which is the only
machine here that can be.

---

## 2. The rows, per machine

| | A (Windows) | B (Mac) | C (Windows laptop) |
|---|---|---|---|
| catalogue rows served | **559** | 94 | 31 |
| rows that are a scanned REPOSITORY | 71 | 3 | 27 |
| rows that are a LIVE WORKTREE of one of those | **110** | 9 (disk-checked) | 1 |
| rows that are neither | **378** | 82 | 3 |
| distinct parent repositories the live worktrees fold onto | **4** | 3 | 1 |

**The owner's 556 is machine A's 559, give or take the churn of an hour.**

**Every row he named by hand is a live worktree, and every one of them resolves.** `idle-p5-a`,
`idle-phase1`, `idle-phase5` and `idle-p5-b` are worktrees of one repository under
`D:\ReposMindzie\worktrees`; `devthrottle-p5-run-a`, `devthrottle-smart-restart-p5b` and
`devthrottle-smart-restart-once` are worktrees of `D:\ReposFred\devthrottle`. Of the **top twenty rows
of the served list - the part of it a person actually reads - four are repositories, thirteen are live
worktrees and three are folders that are gone.**

So the defect is exactly as he described it at the top of the list.

---

## 3. THE BRIEF IS RIGHT ABOUT THE DEFECT AND WRONG ABOUT THE SIZE OF IT

**Two thirds of machine A's list is not worktrees at all. It is folders that no longer exist.**

Of the 559 rows, 181 are folders that exist right now (71 repositories, 110 worktrees). The other
**378 rows point at nothing on disk**. On machine B, where the disk could be read directly, the same
shape is sharper still: **94 rows, 13 of which exist.**

That half of the list is ALREADY FIXED, in code, and is reaching nobody:

- `KnownRepositoryStore.ObserveDiscovered`'s forgetting rule landed on `main` in `516b1ce9b` (#3223) on
  20 September;
- the newest release is `v2.8.1`, cut on 19 September, and **`516b1ce9b` is not an ancestor of it**;
- and it shows in the live data: not one served row on any of the three machines carries a
  `rootFolders` listing, which is the fact that authorises forgetting. The Directors in the field are
  2.8.1, 2.8.1 and 2.7.0.

**So nothing has been forgotten yet on any machine, and nothing will be until a Director carrying it
ships.** That is not this work's to fix and it is not a defect in that work; it is stated here because
it is most of the number the owner read off the screen, and a report that let him believe the worktree
rule alone would take 556 to a handful would be wrong.

### What each rule is worth on machine A, measured

| Rule | Rows left |
|---|---|
| today | 559 |
| the worktree rule, applied to the existing rows | 449 |
| the worktree rule + the forgetting rule already on `main` | **274** |
| what is on disk and is a repository | **71** |

The gap between 274 and 71 is a third thing, and it is worth naming because it is invisible from any
screen: **203 of machine A's rows sit more than one level below a registered root folder** - under
`D:\ReposMindzie\worktrees`, `D:\ReposFred\worktrees`, `D:\ReposFred\devthrottle.worktrees`, and so on.
The forgetting rule compares a row's DIRECT PARENT with the roots the Director listed, deliberately
(`proofs/the-catalogue-forgets/`, revert D), so a row two levels down is never forgotten however long
its folder has been gone. Those 203 rows are permanent under every rule that exists today.

**The worktree rule reaches into that set and the forgetting rule cannot**, because a worktree is
resolved by asking git on the machine, not by asking where the folder sits. That is an argument FOR
doing the historical collapse rather than only the record-time change.

---

## 4. Can every worktree row be resolved to a parent repository? No - and the failures are clean

Probed on a real disk with real git (`git rev-parse --path-format=absolute --git-common-dir`, which is
the call `RepositoryMonitor.DefaultResolvePrimary` already makes in this product):

| Case | `.git` | git says | Verdict |
|---|---|---|---|
| a repository proper | directory | its own `.git` | not a worktree - never asked |
| a live worktree | file | the parent's `.git` | **resolves** - parent is that folder |
| a worktree whose parent repository was deleted | file | exit 128, "not a git repository" | **cannot resolve - LEFT ALONE** |
| a `.git` file pointing at a path that does not exist | file | exit 128 | **cannot resolve - LEFT ALONE** |
| a folder that was never a repository | absent | exit 128, walks UP looking for one | not a worktree - never asked |
| a folder that does not exist | - | exit 128 | **cannot resolve - LEFT ALONE** |
| a bare repository's worktree | file | a common dir not ending in `.git` | **no primary checkout - LEFT ALONE** |
| a git SUBMODULE | file | `<super>/.git/modules/<name>` | **not a worktree - LEFT ALONE**, and correctly so: a submodule IS a repository |

Two things follow, and both matter for the design:

1. **The guard is `.git` is a FILE, and it must be, not "git can answer".** Asked from a plain folder
   INSIDE a repository, `rev-parse` happily answers with the repository above it - so a session started
   in a sub-folder would be credited to a repository the user did not pick. The existing
   `RepositoryMonitor` code takes exactly this guard (`gitIsFile`) before it canonicalizes, and this
   work takes the same one.
2. **A submodule survives the rule for free**, because its common directory's last segment is the
   submodule's name and not `.git`. `DefaultResolvePrimary` already returns null for that shape. No new
   rule is needed and none should be written.

**A dead worktree row cannot be resolved by anything, ever.** Its `.git` file is gone with the folder.
Whatever the historical collapse does, those rows are the forgetting rule's business and not this
rule's, and they must be left alone rather than guessed at.

---

## 5. Is session observation the only source of worktree rows? No - there are two, and a third that protects them

### Source one: session observation. Yes, and it is the big one.

`SessionHistoryRecorder.ObserveKnownRepository` -> `KnownRepositoryStore.Observe`, off
`RepositoryUsage.StartedIn(session.RepoPath, session.PooledWorktree?.Repo)`. Every agent session runs
in a worktree, so every worktree that ever hosted a session became a row. It is the only writer of the
USED half.

### Source two: THE REGISTRY, and the brief is right to have asked.

`RepositoryRegistry.TryAdd` performs **no git check of any kind** - it normalizes the path, refuses a
duplicate, takes the folder name and stores it. Browse on the desktop, and the `repo-add` verb the
Cockpit and the phone use, therefore accept a worktree as readily as a clone. Since "the registry
reaches the Gateway" (#3203) the Director's push is scan UNION registry, so **a hand-added worktree
becomes a DISCOVERED row in the catalogue** - a never-opened one, at the bottom of the list, but a row.

Measured: no machine has one today. Machine A's registry holds seven entries, six clones and one
document folder that is not a repository at all; machines B and C hold none. So this is a real second
door that nobody has yet walked through. It is worth closing at the same time as the first, and it
closes the same way - by resolving on the Director, where the answer can be had.

### The third thing: the root-folder listing PROTECTS a worktree row. Confirmed - and it changes the recommendation.

The brief asks whether the root-folder listing added by the catalogue-forgets work is an add-set or a
keep-set. **It is a keep-set, and it protects worktree rows from ever being forgotten.**

In `ObserveDiscovered`, rows are INSERTED only from `snapshot` - the scan - which cannot see a worktree.
The listing's child paths go into `stillThere`, and `stillThere` is read in exactly one place: the
forgetting test, `!stillThere.Contains(key)`. So a live worktree's row is held OUT of the forgotten set
by the very listing that exists to make forgetting safe. That is correct and deliberate - it is what
stopped the first version of that rule deleting eleven live folders.

**The consequence for this work: the 110 live worktree rows on machine A will never leave the catalogue
on their own.** They are protected while the folder lives, and they are unreachable at depth once it
dies. A record-time rule alone stops the list growing; it does not shorten it. **If the existing rows
are to go, something must positively collapse them.**

---

## 6. What I recommend, for the ruling

**Part one - the record-time rule, which is already agreed. Build it.** At session start the Director
resolves a worktree to its parent repository and that is what both catalogues record. It is the same
idea as `RepositoryUsage.StartedIn`'s pooled rule and the same call `RepositoryMonitor` already makes.
Only the Director can answer it, so the resolved repository is stamped on the session and travels up
the one feed that already exists; the Gateway infers nothing. This is what stops row 560.

**Part two - the historical collapse. My recommendation is to DO IT, on the same feed, and only for
rows the Director positively resolves.** The shape that meets the standard this mission holds:

- the Director, which is the only thing that can tell, reports for each folder it listed under a
  registered root **the parent repository when that folder is a worktree it could resolve**, and says
  nothing at all about every other folder;
- the Gateway, on a real observation only, merges such a row into the row for that parent - **keeping
  the NEWEST last-used time of the two** - and deletes the worktree row;
- **anything not positively resolved is left exactly as it is**: a row the Director said nothing about,
  a folder that is gone, a folder under a root the Director could not read, a submodule, a bare
  repository's worktree, a plain folder. The enumerated set is what to CHANGE, never what to skip.

It is worth doing rather than waiting for the rows to age out, because **they cannot age out**: section
5 shows the listing protects them while they live and the parent test cannot reach them once they die.
On machine A it is 110 rows of the 559, and it is the 110 sitting at the TOP of the list, which is the
part the owner actually reads.

**What it costs, said plainly.** A collapsed row's own last-access time disappears into the parent's
if the parent's is newer. Nothing else is lost - the row holds no other fact a screen reads - but there
is no undo and no tombstone, exactly as for the forgetting rule.

**What I do NOT recommend**, and both would be easy to reach for:
- resolving anything on the Gateway. It is a Linux container holding Windows and macOS paths and it is
  never the machine the path describes;
- any test on the path's text. `D:\ReposFred\devthrottle-p5-run-a` is a worktree and carries no
  "worktrees" segment; `D:\ReposMindzie\worktrees\idle-p5-a` is one and does. Neither fact is in the
  string, and both were measured that way above.

**And one thing the owner should know whatever is ruled here:** the worktree rule takes machine A from
559 to 449. It takes the top of his list from thirteen worktrees in twenty rows to none. It does not
take 556 to a handful on its own - **the release that carries the forgetting rule is what does that**,
and today nothing in the field carries it.
