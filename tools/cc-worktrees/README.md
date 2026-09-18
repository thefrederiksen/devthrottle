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
python tools/cc-worktrees/main.py release <path-or-slot> --confirm-abandon [--repo <path>]
```

Every command takes `--json` and `--help`, and never prompts.

Success or refusal, the output is the same shape: `key: value` pairs rendered by the AXI value
renderer, then a `help[N]:` block of commands. **A help line is a command, not a value: it is written
exactly as the tool composed it, so the path in it can be copied and pasted.** Values are escaped and
quoted when they need it, help lines never are.

## Commands

| Command | What it does |
|---|---|
| `get` | Hands out the first free slot after proving it landed and resetting it to the freshly fetched default branch. With no free slot and fewer slots than `--pool-size` (default 4), creates a new one at `<repo-parent>/<repo-name>.worktrees/wtNN` on a detached HEAD at the remote default branch. Returns the slot, path and lease id. A free slot that fails the check becomes held and is skipped. |
| `return` | Runs the landed-work check. Landed: reset with the two-tree merge `git read-tree -m -u <checked HEAD> <default tip>` (ignored build output stays; there is no `git clean`) and free. Not landed or cannot tell: held with the reason, nothing reset. `--lease` is required: a return without it is a usage error (exit 2), and a lease that no longer matches is refused; neither changes anything. |
| `list` | Every slot with its state (`free`, `in-use`, `held`), holder and reason. `--fields` picks from `repo,slot,path,state,holder,reason,updated`. |
| `lease` | Takes one specific free slot (checked and reset like `get`). A held slot only with `--reclaim-held`, which takes it as it is, without a reset. |
| `release` | The way out of held, and the only command that puts a held slot back in the pool. Never implicit, never the default of anything, and `--confirm-abandon` is required. It pins every commit it cannot prove landed under `refs/cc-worktrees/<slot>/`, where `git gc` can never take it: HEAD, every commit the slot's HEAD reflog names, the branch HEAD is on, the stash entries the slot owns, and the stash entries nothing could attribute to any worktree. Then it removes the directory - the ignored files in it go too - and the slot leaves the pool. It refuses (exit 3, naming what) while anything in the slot cannot be pinned: an uncommitted or untracked file, an edit `git status` cannot see, or a repository of its own. It never stashes a file for you and never deletes one. It never deletes an object, a pin or a branch, and never pushes. The output lists every pin it leaves. |
| `destroy` | Runs the full landed-work check at that moment, whatever the recorded state says - a free slot is only free as of its last check - and refuses (exit 3, held, with the reason) unless it passes. No flag skips that check. Dry run by default: says what it would remove. `--yes` removes it with `git worktree remove` under `HEAD.lock` (never `--force`, so git itself refuses a worktree with modified or untracked files that git status can see; files git status skips are covered by the check, rule 2). A held slot also needs `--allow-held`, an in-use slot `--allow-in-use`. A slot whose directory is gone cannot be checked, so it is refused too. One slot per call; there is no destroy-all. |
| `--version` | The tool's own version, printed as `tool: cc-worktrees` and `version: <the version>`. Installed, it is the distribution's recorded version; from a checkout, where nothing is installed, it is read out of `pyproject.toml`. There is no copy of the version string in the source, so there is nothing to keep in step. It is a flag on the tool, not on a command: `cc-worktrees list --version` is a usage error. The Director's Tools page runs `cc-worktrees --version` on every tool it lists and marks the row failed on a non-zero exit. |

A slot is named `wt01` (with `--repo`; without it, the registered slot the current directory is inside,
or else the repository of the current directory) or by its path. Both are looked up in the tool's own
registry of pools, never by asking git through the slot's `.git`: that pointer is one of the things the
check proves, so a pointer swapped to another repository reaches the check and holds the slot. The
lookup needs the registry, so a lost registry makes the path form and the form without `--repo` fail
with `no-inventory`, and `list` without `--repo` too. A slot NAME with `--repo` does not need it:
`return wt01 --lease <lease> --repo <path>` works with the registry deleted, and rebuilds it as a side
effect of saving the state. That is the recovery route when the registry is lost.

## The landed-work rule

A worktree is reset only when all of these are positively proven, in this order:

1. The slot's `.git` leads to the slot's own record in this repository: `git rev-parse --git-dir`
   inside the slot must be the `.git/worktrees/<name>` record whose back-link names the slot (found
   from the repository's side), and the one recorded when the slot was made. A `.git` file copied from
   another slot would make every later answer describe that other slot, so a mismatch holds the slot
   and nothing in either slot is touched.
2. `git status --porcelain --untracked-files=all` is empty: nothing uncommitted, no untracked file
   that is not ignored. Status skips a tracked file flagged assume-unchanged or skip-worktree, so an edit
   to one is invisible to it, to `git update-index --refresh` and to `git worktree remove`. The flags are
   read with `git ls-files -v`, and any flagged file holds the slot, naming it, whether or not it was
   edited. A sparse checkout marks the files it leaves out skip-worktree, so a sparse slot is held too.

   And the repository's stash holds nothing of this slot's. `git stash` deliberately leaves the tree
   clean, leaves HEAD where it was and adds nothing to the reflog, so stashed work is invisible to every
   answer above while having landed nowhere at all. `refs/stash` is a common ref - one stack shared by
   your own checkout and every worktree of the repository - so its existence says nothing, and neither
   does the fact that it moved. The tool records `refs/stash` when it hands a slot out, and `return`,
   `lease`, a free slot's `get` and `destroy` each look at the entries added since:

   **A stash entry belongs to a slot when the repository can show it does, and to no slot when it
   cannot.** A stash commit's first parent is the HEAD it was made from, which is the only thing the
   repository records about where an entry came from. An added entry whose first parent is a commit this
   slot was at - its HEAD now, or a commit in its own HEAD reflog - is this slot's and holds it, naming
   the entries as `git stash list` prints them. One made in another worktree does not hold it.

   **An entry that cannot be attributed holds the slot**, and says so: a first parent git will not read,
   a stash log that does not agree with `refs/stash` (a log git cannot read comes back EMPTY and exit 0,
   which would otherwise read as "nothing was added"), or the entry the tool recorded no longer being in
   the stack, so which entries are new cannot be said. It never frees on an unknown.

   The cost that remains, and it is not small: **two checkouts sitting on the same commit cannot be told
   apart.** A stash you make in your own checkout while it is on the commit a slot was reset to has that
   slot's HEAD as its first parent, so it holds the slot - and a checkout parked on the default branch
   sits exactly there. Work in a slot of your own, or on a commit of your own, and the entry is plainly
   yours. A slot also stays held once the entry it recorded is dropped from the stack, however that
   happens. `git stash pop` or `git stash drop` in the slot puts `refs/stash` back where it was and the
   slot returns normally; the other way out is `release --confirm-abandon`, which pins the stash commits
   the slot owns, and the ones nothing could attribute, before letting it go. The tool never stashes
   anything for you, never pops, never drops, and a stash survives both `return` and `destroy`.
3. The default branch is read from the remote with `git ls-remote --symref origin HEAD`. It is never
   assumed to be `main` and the local `origin/HEAD` is never used.
4. The remote was fetched just now (`git fetch --prune origin +refs/heads/*:refs/remotes/origin/*`).
   `--prune` matters: a tracking ref for a branch deleted on the remote must not count as proof. The
   refspec is named on the command line because `--prune` only prunes what the refspec maps: a clone whose
   configured refspec covers only the default branch (what `git clone --single-branch` leaves) would keep
   a stale tracking ref for every other branch forever.
5. Nothing in the slot is a git repository of its own. The slot is walked for any entry named `.git`
   (a directory, a file or a link; compared without regard to case on Windows and macOS) other than its
   own top-level `.git`, never following a symlink, a junction or any other reparse point out of the slot.
   A clone under an ignored path is invisible to `git status`, its commits are on no ref of this
   repository, and `git worktree remove` would delete it with its history, so any such entry holds the
   slot, naming the path. A registered submodule is held by the same rule: `git status` proves a
   submodule matches the commit this repository records, not that the submodule's own HEAD, or the
   commits abandoned in it, are on its remote. A directory that cannot be listed holds the slot. The walk
   runs in the check and again as the last step before `read-tree` and before `git worktree remove`;
   measured on a slot with 60,000 ignored files in 600 directories it took 0.1 to 0.2 seconds, and from its
   end to `read-tree` starting took under 0.1 milliseconds. A repository created inside that last gap is
   not seen.
6. Every commit reachable from HEAD is checked on its own. It counts as landed only when it is in the
   default branch tip's history, or on another `origin` branch that the remote itself confirms at the
   moment of the check, or when both of these hold: it is the same patch as a commit in the default
   branch's history (`git cherry`, which recognises a rebase), AND, for every path that any commit on
   no remote branch touches, the slot's content equals the content of the current default branch tip.
   A patch in the history is not content in the tip: a patch that was landed and then reverted is held.
   So is landed work whose paths upstream changed again afterwards, even though nothing is lost there;
   that is accepted, and the host layer below frees it when the host's own record of the merge proves it.
   For a commit found only in the reflog, the same comparison uses that commit's own content. Files that
   merely end up the same are never proof for the commits behind them.
   - A commit that differs from a landed one only in its message counts as landed, so the message
     itself is not protected.
   - Work landed only by a squash merge is not proven by git, because a squash is not the same patch as
     any one of the commits it combined. It counts as landed while its commits are still on a remote
     branch (for example the pull request branch). Once that branch is gone, only the host can prove it:
     see **The host layer** below.
   - **The ordinary rebase-and-force-push is not proven by git.** After `git rebase origin/<default>`
     and `git push --force-with-lease`, the rebased commit proves itself on the remote branch, but the
     commit it replaced lives only in the slot's HEAD reflog: `git cherry` compares a stray commit
     against the default branch alone, never against the remote feature branch that now carries its
     rebased twin. Git holds and pins that predecessor; the host frees it only when it is one of the
     pull request's own commits, which a force-pushed predecessor usually is not. Nothing is lost - it
     is pinned - and `release <slot> --confirm-abandon` is the way back into the pool.
   - A merge commit on no remote branch is not proven by git: `git cherry` does not compare merge
     commits. This is the commonest case the host layer frees, because a branch that had the default
     branch merged into it before its pull request went up carries one.
   - The tracking refs come from the fetch, which runs before the command waits for the machine-wide
     lock, and that wait can last up to 300 seconds. Ancestry of the default tip needs nothing more: an
     older default tip loses nothing. A commit whose only proof is another remote branch has those
     branches asked for again with one `git ls-remote origin refs/heads/<name> ...`, inside the locked
     section that resets or removes the slot. A branch the remote no longer has, one moved to a commit
     that does not contain the proved commit, or a remote that does not answer, holds the slot. This is
     the one network call made while the machine-wide lock is held; it is bounded by the network timeout
     (120 seconds by default), and when no commit needs it, it is not made.
7. Every commit in the slot's HEAD reflog after the tool's own mark passes the same check. A commit
   abandoned with `git reset --hard` lives only in that reflog, which a later destroy deletes. The mark
   is a reflog line the tool itself writes, while it holds `HEAD.lock`, each time it resets or creates a
   slot: `cc-worktrees: reset to <commit> <nonce>`, with a new random nonce. Still under the lock, the
   tool proves the whole reflog is exactly the entries the check examined plus that one line, and saves
   that line's position, commit and nonce. It never saves "whatever entry is newest", so a commit made
   after the reset is after the mark and is checked next time. A mark that is not found as that line at
   its recorded position, a reflog that lost it, or `core.logAllRefUpdates` turned off, cannot be
   vouched for and holds the slot.

   The reflog after the mark is only proof while nothing has removed lines from it. `git reflog expire`
   and `git gc` do: they write a new file and rename it over the old one, even when they expire nothing
   (measured on git 2.49; `git gc --auto` with nothing to do leaves it alone), and an expired commit
   leaves no trace in the file. So the tool also records the log file as it stood right after its line
   was verified - its identity, its size, and a hash of its bytes - and the check requires the same file,
   no shorter, with the same bytes up to that line. Anything else holds the slot ("the HEAD reflog was
   rewritten since cc-worktrees wrote its line"), however old or new the mark is: a hand-run
   `git reflog expire --expire-unreachable=now` ignores every configured expiry, so no age proves it did
   not happen. **This means one `git gc` in the repository holds every slot of its pool**, free ones
   included; measured, a pool of four free slots answered `pool-full` to the next `get` after one
   ordinary `git gc`. `release <slot> --confirm-abandon` is what puts such a slot back: the work in a
   slot held this way is almost always already landed, so the release usually pins nothing at all and
   the slot name comes straight back. A slot with no mark on record (its state was lost) is held the
   same way, and every reflog entry is checked.

   Gap: a file system that reuses file identities (inode numbers, as ext4 can) could give a log rewritten
   twice the old identity; if the rewrite also left every byte before the mark unchanged and the file no
   shorter, the tool would not see it. On git 2.49 a rewrite also changed a byte before the mark in every
   log measured (a line with an empty message gains a tab), which the hash sees, but that is git's
   formatting, not a guarantee.
8. Every commit the check cannot prove landed is pinned: the tool writes
   `refs/cc-worktrees/<slot>/<commit>` in the main repository (never under `refs/heads` or `refs/remotes`,
   never pushed; the fetch names its own refspec, so a fetch cannot write there). A pinned commit cannot be
   removed by `git gc` or reflog expiry, and every later check reads the slot's pins as commits to prove,
   so it stays held until the normal rule proves it landed. A pin is never proof. A check that holds the
   slot for another reason (uncommitted changes, a hidden flag, a nested repository, a rewritten reflog)
   still runs the commit proof and pins what it cannot prove, and names those commits after the first
   reason. A pin that cannot be written is named in the reason. The pins are removed only after a passing
   check's reset or removal succeeded, in the same locked section. So `destroy --allow-held` of a slot
   with a pin refuses, naming the commits, unless every one of them is proven landed at that moment.
   Only commits some check saw are pinned: a commit expired from the reflog before any check looked at it
   is not, and `git gc` may remove it later; its slot is held by rule 7.
9. The caller's lease matches. The lease is required, because without it the tool has no evidence the holder let go.

Any failure - including a fetch that fails for an unreachable remote, bad credentials or an expired
token - holds the worktree with a plain reason such as `1 commit is on no remote: <commit>`,
`2 uncommitted changes`, or `cannot verify: <git's error>`. Every git call runs with
`GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`, so a credential problem fails at once instead of
waiting at a prompt, and with `GIT_NO_REPLACE_OBJECTS=1`, whatever the caller's environment says. A replace
ref (`git replace`) makes git read another commit wherever the replaced one is named; replacing an unlanded
commit with a landed one made every answer above say "landed", and `return` freed the slot and `destroy`
removed it. With replace refs ignored, the commit the slot really holds is the one checked. The network calls, `ls-remote` and `fetch`, are each stopped after 120 seconds
(`CC_WORKTREES_NETWORK_TIMEOUT` sets another number of seconds); a timeout holds the worktree as
`cannot verify: timed out after N seconds`. Only the git process the tool started is stopped. A remote
helper that git itself started may keep running until its own network call ends.

Immediately before the reset the tool takes `HEAD.lock` in the worktree's git directory and re-checks
the `.git` binding, HEAD, the whole reflog, cleanliness and the index flags under it. It then refreshes the index
(`git update-index --refresh`; a tracked file that changed holds the slot) and resets with a two-tree
merge from the checked HEAD. That merge checks every path it would write before writing any, and refuses
(held, nothing written) when an untracked file or a local change sits on one of them, so a file an editor
writes after the status check is not overwritten. A local change on a path the default branch did not
touch is carried forward instead, and the status check after the reset holds the slot, naming it.

The reset never writes over or deletes an ignored file, as far as the tool can see. `read-tree` has no
way to keep an ignored file on a path it writes, in either mode, so the tool compares the ignored files
with the target tree under the lock, as late as it can: if the default branch now tracks a path that is
an ignored file in the slot (or a directory above one), the slot is held and the reason names the files.
An ignored file created between that comparison and `read-tree` is not seen. That window is not a few
milliseconds: measured on a slot with 60,000 ignored files in 600 directories (Windows, git 2.49), listing
the ignored files took 0.25 to 1.1 seconds on its own - measured on one machine, load-dependent, and
re-running the same measurement later gave the higher numbers - and from that listing's end to `read-tree`
starting took 0.1 to 0.2 seconds, most of it the nested-repository walk of rule 5. Read them as the size
of a window, not as a bound your machine will keep. A build that writes an
ignored file inside that window, on a path the default branch has newly started to track, loses that file.
There is no `git clean`: after `read-tree` the only untracked files left can be ones the new default
branch no longer ignores, and those are kept; the slot is then held, naming them. If the reset fails part
way (for example a file locked by another process on Windows) the slot is held with the reason.

The reset runs no git hook. It is `git read-tree -m -u`, which moves the files and the index without
ever running `post-checkout`; measured, a `git checkout --detach HEAD` by hand in a slot called the hook
once and a `return` called it zero times. A project whose build depends on a `post-checkout` hook gets a
slot the hook never saw.

The tool never kills a process, never deletes a branch, and never pushes, merges or rewrites history.

## The host layer

Git cannot recognise every way a host lands a branch. A squash merge makes one commit that is not the
same patch as any of the commits it combined, and a rebase or an Azure DevOps semi-linear merge drops a
merge commit that `git cherry` never compares. The work landed; git simply has no way to say so. The host
does: it still holds the pull request, the commits that went into it, and the commit it landed as.

**The host layer is asked ONLY about commits git has already failed to prove.** So it can turn held into
free, and it can never turn free into held - and an ordinary return, where git proves everything, makes no
call to a host tool at all.

### Which host

The origin remote's URL says which, and nothing else does:

| Host | Remote URL shapes | Asked with |
|---|---|---|
| GitHub.com | `https://github.com/<owner>/<repo>[.git]`, `git@github.com:<owner>/<repo>[.git]`, `ssh://git@github.com/<owner>/<repo>[.git]` | `gh` |
| Azure DevOps | `https://[<user>@]dev.azure.com/<org>/<project>/_git/<repo>[.git]`, `git@ssh.dev.azure.com:v3/<org>/<project>/<repo>`, `https://<org>.visualstudio.com/[<collection>/]<project>/_git/<repo>[.git]` | `az` |
| Any other | anything else, including a path to a local or network repository | nothing - see **checked by git only** |

The match is anchored at the end of the URL, so a directory named after a host somewhere in a path does
not make a local clone that host's.

### Reading the default branch

The default branch comes from `git ls-remote --symref origin HEAD` (rule 3). Both hosts answer it, it
needs no host tool and no sign-in beyond the git credentials the fetch already uses, and it is read again
on every command. It is never assumed to be `main` - the Azure DevOps testbed's default branch is
`develop` - and the local `origin/HEAD` is never used: that ref is written once when the repository is
cloned and then left alone for ever. A remote that does not say which branch is its default is
`cannot verify`, and the slot is held.

### What a pull request may prove

A merged pull request frees the commits git could not prove only when **all** of this holds:

1. The host says the pull request is merged (GitHub) or completed (Azure DevOps), and that it targeted
   the default branch that was just read from the remote.
2. The commit it landed as - GitHub's `mergeCommit`, Azure DevOps's `lastMergeCommit`, which is the one
   commit that maps a semi-linear merge's source back to what is on the default branch - is in this
   repository AND is an ancestor of the default branch tip that was read under the machine-wide lock. Not
   the tip of an earlier fetch, and not a tip on some other branch. The host cannot free work by
   assertion: whatever it says, the landing commit has to be on the default branch we can see.
3. **One** pull request accounts for every commit git could not prove: each of them is in the list of
   source commits the host gives for it, and none of them is outside the ancestry of that pull request's
   last source commit. All of them together or none of them - so one commit made in the slot after the
   merge holds the whole slot, which is exactly the point.

The commands are read-only: `gh pr list --state merged --head <branch> --base <default>` and
`az repos pr list --status completed --source-branch <branch> --target-branch <default>`, plus, for Azure
DevOps, the pull request's own commits (`az repos pr list` leaves that field empty). The tool never
creates, merges, comments on or closes anything.

The branch names put to the host are the local branches that contain those commits, plus the name a
branch's upstream says the remote knows it by. A commit on no local branch names no branch to ask about.

### Checked by git only

**"Checked by git only" means the host was NOT asked, or could not answer - so nothing it might have
proven was used, and the commits stay held.** It is never a "no" from the host. The reason on the slot
says which it was:

- the remote is on no host cc-worktrees can ask;
- `gh` or `az` is not on PATH;
- the tool ran and failed - not signed in, no access to the repository, rate limited, offline;
- it did not answer inside the network timeout (`CC_WORKTREES_NETWORK_TIMEOUT`, 120 seconds by default);
- its answer could not be read;
- the commits are on no local branch, or on more branches than the tool will put to a host (10).

A host that was asked and simply has no pull request covering the work says so differently: *the host was
asked and has no merged pull request that accounts for all of them*.

### The answer names what proved the work

`get`, `return`, `lease` and `destroy` say how the work in the slot was proven, in the plain output and
in `--json` alike. A real `return` of a slot whose two commits were squash-merged:

```
slot: wt01
path: C:\...
epo.worktrees\wt01
state: free
base: main
commit: adc45a77d66f09c9d4b7c2a162c92377bb53ddf0
proved_by: git and the host
host_proof[2]{commit,host,detail}:
  8ccd12f6105aa597af297c4ed771bb8e4d89c02c,github,"pull request #106 on an-owner/a-repo, merged as adc45a77d66f into main"
  d3b53c2a1233de360f445875131b94d4c62f6990,github,"pull request #106 on an-owner/a-repo, merged as adc45a77d66f into main"
```

`proved_by: git` with `host_proof[0]{...}:` is the ordinary case: git proved every commit on its own and
no host was asked. Every commit the host freed is listed with the pull request that freed it, so "free"
is never something to take on trust.


## What a slot is not

- **Ignored files persist to the next holder.** A reset keeps every ignored file, because warm build
  output is the reason the pool exists, and the tool cannot tell build output from anything else that
  is ignored. A `.env`, a local credential or any other ignored file one holder leaves behind is in the
  slot when the next holder gets it.
- **`destroy --yes` and `release --confirm-abandon` take every ignored file with the directory.** Both
  remove the worktree, and removing a directory removes what is in it: the warm build output, the
  `.env`, the local credential. Only a RESET keeps ignored files; a removal never does, and neither
  command warns about them one by one.
- **A slot is not private.** Every holder is the same operating-system user on the same machine, who
  can already read every slot on disk, whoever holds it.
- **The lease is a coordination token, not authentication.** It stops one session returning a slot
  another session holds by mistake. Anyone who can read the state file can read the lease.
- **A slot does not survive `git gc` as free.** Any `git gc` or `git reflog expire` in the repository,
  including one git starts by itself when it decides the repository needs it, holds every slot of the
  pool (rule 7). `release <slot> --confirm-abandon`, one slot at a time, is what gives them back.
- **A released slot's name is retired while its pins stand.** The pins ARE the record of the work that
  was abandoned, so nothing deletes them. A new slot of that name would be handed the old one's commits
  to prove and would be held from its first return, so `get` skips a name that still has pins and uses
  the next number. A release that pinned nothing leaves its name free to come back.
- **A slot is not a place to keep another repository**, a sparse checkout, or files flagged
  assume-unchanged or skip-worktree: each holds the slot.
- **The tool writes refs.** `refs/cc-worktrees/<slot>/<commit>` pins live in the main repository while
  a commit is unproven (rule 8). They are never pushed, and removing one by hand removes that commit's
  protection from `git gc`.

## Known gaps

Each of these needs an unusual git state or a hand-run command at an unlucky moment. None is closed in this
version; each is stated so nobody reads the rules above as covering it.

- **A graft file** (`.git/info/grafts`, deprecated by git) rewrites parents the way a replace ref does, and
  `GIT_NO_REPLACE_OBJECTS` does not turn it off. Not reproduced as a loss; not guarded.
- **Other `GIT_*` variables** inherited from the caller's environment (for example `GIT_GRAFT_FILE`,
  `GIT_OBJECT_DIRECTORY`) reach every git call. `GIT_DIR` and `GIT_WORK_TREE` are caught by the binding check
  (rule 1); the others were not examined.
- **A stale `git status`**: `core.fsmonitor` with a daemon that missed a change, or `core.untrackedCache`
  trusting a directory time that did not change, could report a dirty slot as clean. Argued only; the tool
  does not turn either off.
- **No pins without a reachable remote.** A slot held because the fetch failed (for example an expired token)
  never reaches the commit proof, so nothing is pinned, and a long outage plus git's own reflog expiry can
  remove an abandoned commit from a slot that stays held (rule 7 keeps it held). A slot whose HEAD reflog
  cannot be READ is a different, smaller case: the proof does run, on HEAD and the pins alone, so an unproven
  HEAD is pinned while a commit abandoned in that unreadable reflog ends up on no ref at all.
  `release --confirm-abandon` pins whatever can still be read, so releasing early loses less than waiting.
- **A branch's own reflog is not walked.** `release` pins HEAD, every commit the slot's HEAD reflog names,
  the branch HEAD is on, and the stash entries it owns or could not attribute. A commit abandoned inside a local BRANCH's
  reflog and in no HEAD reflog is in nothing the tool reads. Working in the slot puts every such commit in
  its HEAD reflog too, so this needs the branch to have been moved from somewhere else.
- **A stash made on a commit a slot was at holds that slot** (rule 2). A stash entry records only the commit
  it was made from, so two checkouts sitting on the SAME commit cannot be told apart - and a checkout parked
  on the default branch sits exactly where slots are reset to, which makes this the common case rather than
  an exotic one. The tool holds, because guessing the other way frees work. Working on a commit of your own
  before stashing, putting the stash back where it was, or `release --confirm-abandon`, are the ways on.
- **A slot stays held once the entry it recorded leaves the stack.** The tool records one stash commit when
  it hands a slot out and finds the entries above it. Drop or pop that entry - in your own checkout, at any
  time, including long after the slot went free - and which entries are new cannot be said at all, so the
  slot is held at its next `return`, `get` or `destroy` (rule 2). `release --confirm-abandon` is the way on.
- **A stash entry is attributed, never traced.** Nothing in git says which worktree ran `git stash`. The
  first parent is a good signal and it is the only one there is: a stash made in another worktree that
  happens to be on one of this slot's commits is read as this slot's, and one made in this slot after its
  HEAD reflog was rewritten by `git gc` may no longer match any commit the tool can see, in which case the
  entry is attributed to no slot and holds nothing. The reflog rewrite itself holds the slot (rule 7).
- **Reused file identities** could hide a rewritten reflog (rule 7), a **repository created** in the last
  microseconds before `read-tree` or `git worktree remove` is not seen (rule 5), an **ignored file written**
  inside the measured window before `read-tree` can be overwritten, and a **hand-run `git fetch` or
  `update-ref`** can move tracking refs at any moment (State).
- **A reparse point that is not a link** (for example a cloud-files placeholder directory) is not descended
  into by the rule 5 walk, so a repository inside one is not seen.
- **The host layer needs a branch name.** The host is asked about the local branches that still contain
  the commits. Work committed on a detached HEAD and pushed with `git push origin HEAD:refs/heads/x`, with
  no local branch left behind, names no branch, so it is checked by git only however it was merged.
- **Work split across two merged pull requests is held.** One pull request must account for every commit
  git could not prove. Taking each pull request's word for its own part would leave nothing that says the
  slot holds no commit made after the last of them landed, so the rule does not do it.
- **A merge that was reverted afterwards is still freed by the host.** The rule asks that the pull
  request's landing commit is an ancestor of the current default tip; a `git revert` on top does not
  remove it from the history, so the host frees work git alone would have held (rule 6 holds a reverted
  patch). The content is still in the default branch's history, in the commit the pull request landed as,
  so it can be read back from there - but it is not in the current tip and the slot is reset away from it.
- **`release` does not ask the host.** A held slot being released pins every commit git cannot prove,
  including one the host could have proven landed. That writes more refs than strictly needed and loses
  nothing.
- **Every commit must be in the list of source commits the host gives in one answer.** A pull request
  whose commit list the host pages or truncates cannot account for all of them, so the slot is held.
- **The list of source commits is the host's word.** The landing commit must be on the default branch we
  can see, so a host cannot free work by assertion alone; which commits went into the pull request is not
  independently checked against git.
- **A repository path with a character outside printable ASCII cannot be printed as a command.** The output
  is pure ASCII by contract and a help line is written exactly as composed so it can be pasted, and the two
  cannot both hold. Measured: the renderer raises rather than print a command that would not work if pasted,
  on the success path and the refusal path alike, so this is the whole tool and not the error path. It is
  the `key: value` lines that survive such a path - they are values, so they come back quoted and escaped
  and read back exactly - and `--json` is unaffected for the same reason. The refusal path is the worse of
  the two: on a success the raise is reported as an internal error, while on a refusal it escapes as a
  traceback. Not fixed here, and no path like that has been staged end to end.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Error; the message says what happened and what to run next (for example `lease-mismatch`, `in-use`) |
| 2 | Usage error: unknown flag, unknown field, missing argument |
| 3 | Not returned: the worktree is held, with the reason. A `release` that refuses also exits 3, and the slot stays held |
| 4 | Pool full: no free slot and the pool is at its size; nothing was created, and the message names every slot |

## State

One JSON file per repository pool under `pools/`, all guarded by one machine-wide lock file (an OS
lock: `msvcrt.locking` on Windows, `flock` elsewhere), written to a temp file and replaced.

The fetch never runs under the machine-wide lock; the one network call that does is the remote-branch
confirmation in rule 6, bounded by the network timeout. A command fetches first, under a lock for
that repository only (`fetch-locks/`), and keeps that lock until its act is done. It then takes the
machine-wide lock, reads the state again, reads the default branch's tracking ref again, and runs the
whole landed check and the reset against THAT commit, never the one the fetch returned. The order is
always the fetch lock, then the machine-wide lock. A slow remote therefore holds up commands for its own
repository and nothing else. Two commands for one repository run their fetch-and-act one after the
other, so one never hands out a tip another has already fetched past. A plain `git fetch` run by hand
outside the tool can still move the tracking refs at any moment; that records the remote as it is later,
and it is an accepted gap.

Location: `%LOCALAPPDATA%\cc-worktrees` on Windows, `~/Library/Application Support/cc-worktrees` on
macOS, `$XDG_DATA_HOME/cc-worktrees` or `~/.local/share/cc-worktrees` elsewhere. Set
`CC_WORKTREES_HOME` to use another directory.

The state file is checked field by field: the version this tool reads, the same repository, a known
state for every slot, a free slot with no holder, lease or reason, an in-use slot with both a holder
and a lease, a held slot with a reason, and a recorded `refs/stash` that is a commit or nothing. A file that cannot be parsed, or that parses but fails any one
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

The host-layer tests open and merge REAL pull requests on those testbeds, so they also need `gh` signed
in for GitHub and `az` with `AZURE_DEVOPS_EXT_PAT` set for Azure DevOps. They merge by squash, by rebase
and by Azure DevOps semi-linear merge, and each merged branch is deleted by the merge that used it. The
commits the host lands on a testbed's default branch stay there.
