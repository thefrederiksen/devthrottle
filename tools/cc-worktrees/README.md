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
| `return` | Runs the landed-work check. Landed: reset (`git read-tree --reset -u` then `git clean -fd`, no `-x`, so ignored build output stays) and free. Not landed or cannot tell: held with the reason, nothing reset. `--lease` is required: a return without it is a usage error (exit 2), and a lease that no longer matches is refused; neither changes anything. |
| `list` | Every slot with its state (`free`, `in-use`, `held`), holder and reason. `--fields` picks from `repo,slot,path,state,holder,reason,updated`. |
| `lease` | Takes one specific free slot (checked and reset like `get`). A held slot only with `--reclaim-held`, which takes it as it is, without a reset. |
| `destroy` | Dry run by default: says what it would remove. `--yes` removes it with `git worktree remove` (never `--force`, so git itself refuses a worktree with modified or untracked files). A held slot needs `--allow-held`, an in-use slot `--allow-in-use`. One slot per call; there is no destroy-all. |

A slot is named `wt01` (with `--repo`, or the repository of the current directory) or by its path.

## The landed-work rule

A worktree is reset only when all of these are positively proven, in this order:

1. `git status --porcelain --untracked-files=all` is empty: nothing uncommitted, no untracked file
   that is not ignored.
2. The default branch is read from the remote with `git ls-remote --symref origin HEAD`. It is never
   assumed to be `main` and the local `origin/HEAD` is never used.
3. The remote was fetched just now (`git fetch --prune origin +refs/heads/*:refs/remotes/origin/*`).
   `--prune` matters: a tracking ref for a branch deleted on the remote must not count as proof.
4. Every commit reachable from HEAD is checked on its own: it is on an `origin` branch, or it is the
   same patch as a commit in the default branch (`git cherry`, which recognises a rebase). Files that
   merely end up the same are never proof for the commits behind them.
   - A commit that differs from a landed one only in its message counts as landed, so the message
     itself is not protected.
   - Work landed only by a squash merge is held, because a squash is not the same patch as any one of
     the commits it combined. It counts as landed while its commits are still on a remote branch (for
     example the pull request branch). A later phase may prove a squash through the pull request.
   - A merge commit on no remote branch is held: `git cherry` does not compare merge commits.
5. The caller's lease matches. The lease is required, because without it the tool has no evidence the holder let go.

Any failure - including a fetch that fails for an unreachable remote, bad credentials or an expired
token - holds the worktree with a plain reason such as `1 commit is on no remote: <commit>`,
`2 uncommitted changes`, or `cannot verify: <git's error>`. Every git call runs with
`GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`, so a credential problem fails at once instead of
waiting at a prompt.

Immediately before the reset the tool takes `HEAD.lock` in the worktree's git directory and re-checks
HEAD and cleanliness under it. If the reset fails part way (for example a file locked by another
process on Windows) the slot is held with the reason.

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
lock: `msvcrt.locking` on Windows, `flock` elsewhere), written to a temp file and replaced. The lock
is held for the whole command, including the fetch, so commands for different repositories wait for
each other.

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
