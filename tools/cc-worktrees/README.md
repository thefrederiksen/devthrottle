# cc-worktrees

Pooled git worktrees for agent sessions. A session gets its own worktree with `get` and gives it
back with `return`. A returned worktree is reset and reused, so build output stays warm - but it is
only reset when its work has **provably landed** on the remote. Anything unproven is held, with the
reason, and nothing in it is touched.

Inspired by, and partly adapted from, [treehouse](https://github.com/kunchenguid/treehouse) by Kun
Chen (MIT). See `THIRD_PARTY_NOTICES.md`. The output follows the AXI standard
(`docs/axi-standard.md`, principles from https://axi.md).

Runs from a checkout with Python 3.11 or newer and git. No third-party packages.

```
python tools/cc-worktrees/main.py get --repo <path> --holder <text> [--pool-size N]
python tools/cc-worktrees/main.py return <path-or-slot> --lease <id> [--repo <path>]
python tools/cc-worktrees/main.py list [--repo <path>] [--fields a,b]
python tools/cc-worktrees/main.py lease <path-or-slot> --holder <text> [--reclaim-held] [--repo <path>]
python tools/cc-worktrees/main.py destroy <path-or-slot> [--yes] [--allow-held] [--allow-in-use] [--repo <path>]
```

Every command takes `--json` and `--help`, and never prompts.

## Commands

| Command | What it does |
|---|---|
| `get` | Hands out the first free slot after proving it landed and resetting it to the freshly fetched default branch. With no free slot and fewer slots than `--pool-size` (default 4), creates a new one at `<repo-parent>/<repo-name>.worktrees/wtNN` on a detached HEAD at the remote default branch. Returns the slot, path and lease id. A free slot that fails the check becomes held and is skipped. |
| `return` | Runs the landed-work check. Landed: reset with the two-tree merge `git read-tree -m -u <checked HEAD> <default tip>` (ignored build output stays; there is no `git clean`) and free. Not landed or cannot tell: held with the reason, nothing reset. `--lease` is required: a return without it is a usage error (exit 2), and a lease that no longer matches is refused; neither changes anything. |
| `list` | Every slot with its state (`free`, `in-use`, `held`), holder and reason. `--fields` picks from `repo,slot,path,state,holder,reason,updated`. |
| `lease` | Takes one specific free slot (checked and reset like `get`). A held slot only with `--reclaim-held`, which takes it as it is, without a reset. |
| `destroy` | Runs the full landed-work check at that moment, whatever the recorded state says - a free slot is only free as of its last check - and refuses (exit 3, held, with the reason) unless it passes. No flag skips that check. Dry run by default: says what it would remove. `--yes` removes it with `git worktree remove` under `HEAD.lock` (never `--force`, so git itself refuses a worktree with modified or untracked files). A held slot also needs `--allow-held`, an in-use slot `--allow-in-use`. A slot whose directory is gone cannot be checked, so it is refused too. One slot per call; there is no destroy-all. |

A slot is named `wt01` (with `--repo`; without it, the registered slot the current directory is inside,
or else the repository of the current directory) or by its path. Both are looked up in the tool's own
registry of pools, never by asking git through the slot's `.git`: that pointer is one of the things the
check proves, so a pointer swapped to another repository reaches the check and holds the slot. Because
the lookup needs the registry, a lost registry makes these commands fail with `no-inventory`.

## The landed-work rule

A worktree is reset only when all of these are positively proven, in this order:

1. The slot's `.git` leads to the slot's own record in this repository: `git rev-parse --git-dir`
   inside the slot must be the `.git/worktrees/<name>` record whose back-link names the slot (found
   from the repository's side), and the one recorded when the slot was made. A `.git` file copied from
   another slot would make every later answer describe that other slot, so a mismatch holds the slot
   and nothing in either slot is touched.
2. `git status --porcelain --untracked-files=all` is empty: nothing uncommitted, no untracked file
   that is not ignored.
3. The default branch is read from the remote with `git ls-remote --symref origin HEAD`. It is never
   assumed to be `main` and the local `origin/HEAD` is never used.
4. The remote was fetched just now (`git fetch --prune origin +refs/heads/*:refs/remotes/origin/*`).
   `--prune` matters: a tracking ref for a branch deleted on the remote must not count as proof.
5. Every commit reachable from HEAD is checked on its own. It counts as landed only when it is on an
   `origin` branch, or when both of these hold: it is the same patch as a commit in the default
   branch's history (`git cherry`, which recognises a rebase), AND, for every path that any commit on
   no remote branch touches, the slot's content equals the content of the current default branch tip.
   A patch in the history is not content in the tip: a patch that was landed and then reverted is held.
   So is landed work whose paths upstream changed again afterwards, even though nothing is lost there;
   that is accepted, and a later phase may release it through the host's own record of the merge. For
   a commit found only in the reflog, the same comparison uses that commit's own content. Files that
   merely end up the same are never proof for the commits behind them.
   - A commit that differs from a landed one only in its message counts as landed, so the message
     itself is not protected.
   - Work landed only by a squash merge is held, because a squash is not the same patch as any one of
     the commits it combined. It counts as landed while its commits are still on a remote branch (for
     example the pull request branch). A later phase may prove a squash through the pull request.
   - A merge commit on no remote branch is held: `git cherry` does not compare merge commits.
6. Every commit in the slot's HEAD reflog after the tool's own mark passes the same check. A commit
   abandoned with `git reset --hard` lives only in that reflog, which a later destroy deletes. The mark
   is a reflog line the tool itself writes, while it holds `HEAD.lock`, each time it resets or creates a
   slot: `cc-worktrees: reset to <commit> <nonce>`, with a new random nonce. Still under the lock, the
   tool proves the whole reflog is exactly the entries the check examined plus that one line, and saves
   that line's position, commit and nonce. It never saves "whatever entry is newest", so a commit made
   after the reset is after the mark and is checked next time. A mark that is not found as that line at
   its recorded position, a reflog that lost it, or `core.logAllRefUpdates` turned off, cannot be
   vouched for and holds the slot. A slot with no mark on record (its state was lost) has every reflog
   entry checked.
7. The caller's lease matches. The lease is required, because without it the tool has no evidence the holder let go.

Any failure - including a fetch that fails for an unreachable remote, bad credentials or an expired
token - holds the worktree with a plain reason such as `1 commit is on no remote: <commit>`,
`2 uncommitted changes`, or `cannot verify: <git's error>`. Every git call runs with
`GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`, so a credential problem fails at once instead of
waiting at a prompt. The two network calls, `ls-remote` and `fetch`, are stopped after 120 seconds
(`CC_WORKTREES_NETWORK_TIMEOUT` sets another number of seconds); a timeout holds the worktree as
`cannot verify: timed out after N seconds`. Only the git process the tool started is stopped. A remote
helper that git itself started may keep running until its own network call ends.

Immediately before the reset the tool takes `HEAD.lock` in the worktree's git directory and re-checks
the `.git` binding, HEAD, the whole reflog and cleanliness under it. It then refreshes the index
(`git update-index --refresh`; a tracked file that changed holds the slot) and resets with a two-tree
merge from the checked HEAD. That merge checks every path it would write before writing any, and refuses
(held, nothing written) when an untracked file or a local change sits on one of them, so a file an editor
writes after the status check is not overwritten. A local change on a path the default branch did not
touch is carried forward instead, and the status check after the reset holds the slot, naming it.

The reset never writes over or deletes an ignored file, as far as the tool can see. `read-tree` has no
way to keep an ignored file on a path it writes, in either mode, so the tool compares the ignored files
with the target tree under the lock, as late as it can: if the default branch now tracks a path that is
an ignored file in the slot (or a directory above one), the slot is held and the reason names the files.
An ignored file created in the few milliseconds between that comparison and `read-tree` is not seen.
There is no `git clean`: after `read-tree` the only untracked files left can be ones the new default
branch no longer ignores, and those are kept; the slot is then held, naming them. If the reset fails part
way (for example a file locked by another process on Windows) the slot is held with the reason.

The tool never kills a process, never deletes a branch, and never pushes, merges or rewrites history.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Error; the message says what happened and what to run next (for example `lease-mismatch`, `in-use`) |
| 2 | Usage error: unknown flag, unknown field, missing argument |
| 3 | Not returned: the worktree is held, with the reason |
| 4 | Pool full: no free slot and the pool is at its size; nothing was created, and the message names every slot |

## State

One JSON file per repository pool under `pools/`, all guarded by one machine-wide lock file (an OS
lock: `msvcrt.locking` on Windows, `flock` elsewhere), written to a temp file and replaced.

The network is never touched under the machine-wide lock. A command fetches first, under a lock for
that repository only (`fetch-locks/`), then takes the machine-wide lock, reads the state again and runs
the whole landed check against what it fetched. A slow remote therefore holds up commands for its own
repository and nothing else. Between the fetch and the check, the tracking refs can only be changed by
another fetch, which records the remote as it is later; nothing local is trusted from before the lock.

Location: `%LOCALAPPDATA%\cc-worktrees` on Windows, `~/Library/Application Support/cc-worktrees` on
macOS, `$XDG_DATA_HOME/cc-worktrees` or `~/.local/share/cc-worktrees` elsewhere. Set
`CC_WORKTREES_HOME` to use another directory.

The state file is checked field by field: the version this tool reads, the same repository, a known
state for every slot, a free slot with no holder, lease or reason, an in-use slot with both a holder
and a lease, a held slot with a reason. A file that cannot be parsed, or that parses but fails any one
of those checks, is not trusted at all: it is kept beside itself as `<name>.corrupt-<time>`, and every
slot directory on disk comes back held as `state lost, cannot verify`. So does a `wtNN` directory the
state does not know about. None of them is ever treated as free.

`registry.json` beside `pools/` lists every repository with a pool on this machine. `list` without
`--repo` reads it, so a pool whose state file has gone missing is still listed: its slot directories
come back held as `state missing, cannot verify`. If the registry itself is missing or unreadable, or a
state file is not in it, `list` fails with `no-inventory` or `unreadable-registry` rather than print a
`count: 0` it cannot back. That includes a machine where no pool was ever made. The next `get` rebuilds
a missing registry from the state files that exist; a pool whose state file was lost as well cannot be
found that way.

## Tests

```
python -m pytest tools/cc-worktrees/tests -q
```

The tests use real git against a local bare repository. To run the hosted scenarios too, set
`CC_WORKTREES_TEST_REMOTE_GITHUB` and/or `CC_WORKTREES_TEST_REMOTE_AZURE` to a clone URL of a
testbed repository; credentials come from your own git setup. Those runs push only to branches named
`cc-worktrees-test/<random>` and delete only the branches they created. When a hosted variable is not
set, the run says so in capitals at the end: a skipped hosted run is not a pass.
