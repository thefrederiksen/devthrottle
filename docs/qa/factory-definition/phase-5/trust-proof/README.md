# Phase 5, P7: Claude Code's folder trust and a prompt file outside the folder, proven on the real agent

Date: 2026-10-16. Claude Code 2.1.301 on one Windows machine. Both scripts drive the real `claude` interactively in a
pseudo-terminal, in scratch folders under the temporary directory.

**The two output files were produced by an EARLIER clean-up, not by the scripts committed here.** That clean-up chose
which `~/.claude/projects` folders and `~/.claude.json` keys to delete by matching the scratch folder's name, and a
hand-run delete of the same kind during this work removed another live session's transcript folder. This pull request
replaced it with the ledger described under "What the clean-up deletes" below, and by the Tech Lead's instruction the
scripts have NOT been run since. So the CLEAN-UP lines at the end of each output file (`entries left under the scratch
root`, `removed the scratch session's transcript folder`) are that earlier version's, and the committed scripts print
different lines. The cases themselves (A to F, 1 and 2) do not depend on the clean-up and stand as recorded.

## What was proven (trust-proof-output.txt)

`trust_proof.py` sends no prompt; it reads only the first screen.

| Case | Folder | Trusted beforehand | First screen |
|---|---|---|---|
| A | a new plain folder | nothing | the trust question; answering it records `projects["<that folder>"].hasTrustDialogAccepted = true` |
| B | `factory-run/<id>/work`, not in git | `factory-run` | ready: a trusted parent covers the folders beneath it |
| B | a sibling of `factory-run` | nothing | the trust question |
| C | a git worktree of an untrusted repository | nothing | the trust question; answering it records the MAIN CHECKOUT, not the worktree |
| D | a new worktree under `repo3.worktrees` | `repo3.worktrees` (the pool root) | the trust question: the walk up the parents stops at the worktree's git root |
| E | a worktree, recorded by its own path before Claude Code opened it | that worktree | ready; the worktree beside it still asks |
| F | a worktree of a repository whose main checkout is trusted | the main checkout | ready |

So the Director records the factory-run root once (it covers every kit's `work/` folder) and each pooled slot a seat
takes by its own path. It never records a main checkout: that folder is not one the Director made for the seat.

## A prompt file outside the working folder (prompt-file-proof-output.txt)

`prompt_file_proof.py` types an `@` reference to a file in a kit-like folder outside the working folder, exactly as the
Director now composes it, with `--dangerously-skip-permissions --model haiku`. Both the plain path and a path through a
folder whose name has a space (sent as `@"..."`) were read: the agent answered with the marker word only the file held,
and the transcript's user line holds what was typed, matched the way `PromptArrival` matches it. The working folder
gained no `.temp`.

## How the writes to `~/.claude.json` are made safe

The same way in these scripts and in the Director (`ClaudeFolderTrust`): take Claude Code's own lock, the directory
`~/.claude.json.lock` (proper-lockfile), re-read the file under it, change only the one entry, write a temporary file
beside it and rename it over the file, remove the lock. A lock neither made is never removed, however old: it is
waited for, then the write is refused.

## What the clean-up deletes

Only what the run made, by exact path, never by a name pattern (`Ledger` in `trust_proof.py`, used by both scripts):

- a `~/.claude.json` project key only when it is exactly the key of a folder the run made AND was absent before the run;
- a `~/.claude/projects` folder only when it is exactly the one Claude Code names for a folder the agent ran in AND was
  absent before the run;
- the scratch root, by its recorded path, only when it is directly under the temporary directory;
- the lock, only when the run made it.

## Run

```
uv run --no-project --with pywinpty --with pyte python docs/qa/factory-definition/phase-5/trust-proof/trust_proof.py
uv run --no-project --with pywinpty --with pyte python docs/qa/factory-definition/phase-5/trust-proof/prompt_file_proof.py
```

The agent is started without the variables of the session running the script (any `CLAUDE*` or `CC_*`): a Claude Code
child-session marker otherwise stops it writing a transcript, and the fleet variables would name the wrong session.
