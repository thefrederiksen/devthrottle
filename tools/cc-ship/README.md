# cc-ship

Take a finished change from a session all the way to **merged on origin/main**
(issue 2935, phase 1). The author session runs it; everything else is a normal,
tracked fleet session. There is no background service.

```
cc-ship start --intent intent.md      # open a run and go as far as possible
cc-ship wait                          # while a reviewer or verifier works (8 minutes a call)
cc-ship continue                      # after fixing findings or a failed step
cc-ship respond r1-F2 --keep --note "the owner's words"
cc-ship status [--json]
cc-ship abort
```

Every answer has a `state` and a `next_step`. Errors have `error`, `code` and `help`,
and exit non-zero. Output is ASCII. Unknown flags fail.

## How a run goes

| Step | What happens |
|---|---|
| Start | Refuses a dirty tree, main, an empty intent, a second open run, or a branch with nothing to ship |
| Sync | `git fetch`, rebase onto origin/main. A conflict stops the run with the files named |
| (any step) | What ships is exactly what was checked, reviewed and verified: if HEAD moves during a run, every step runs again on the new head; uncommitted edits to tracked files stop the run until they are committed or discarded (untracked files never ship and do not count) |
| Local checks | The commands in `.ship.yaml` **from origin/main**. A failure goes back to the author with the output |
| Review | A fresh session of another agent family reads the diff, `intent.md` and the owner's decisions, and writes `review-rN.json`. Invalid files get at most two correction turns |
| Fix | `auto-fix` findings go to the author: commit, `cc-ship continue`, and a **new** reviewer reviews the whole change. After 3 rounds the run parks and the owner decides with `cc-ship respond fix-limit --fix` (exactly one more round), `--keep` (ship as is, findings recorded as kept) or `--drop` |
| Owner calls | `ask-owner` findings park the run (`waiting-on-owner`). The author relays them word for word and ends its turn. `cc-ship respond` records each answer. A kept or dropped finding is never raised again on the branch: the reviewer's brief lists every decision with an id (D1, D2...), an identical finding is dropped, a reworded one the reviewer marks `same_as_decision` is dropped and noted on the pull request, and one that only looks similar goes to the owner again, marked as similar |
| Verify | A separate session always verifies. It drives the real product and writes `verify.json` plus screenshots. For a documents-only change it confirms there is nothing to run ("Nothing to run live: documents only") |
| Risk | The highest of the reviewer's level and the rules below |
| Pull request | Push, publish evidence to the `ship-evidence` branch, open or update the pull request with the fixed body and the attestation line |
| Merge | Low or medium risk, every step completed, verdict go or documents-only, no FAILED check: squash-merge that exact head and delete the branch. Otherwise the pull request is parked for the owner. Pending or missing checks never delay anything. If an earlier attempt merged but did not record it, `cc-ship continue` finds the merge instead of repeating it |

States: `working`, `waiting-on-author`, `waiting-on-owner`, `failed`, `merged`,
`aborted`. On `waiting-on-owner` the author tells the owner and **ends its turn**.

## Risk

High: a `risk.high_paths` match; any path whose words (split at separators and
camelCase) include auth, oauth, login, signin, signup, password, credential, secret,
apikey or "api key", token, jwt, tenant, rls, migration or schema, or that ends in
`.sql` or `.prisma` ("AuthContext.jsx" matches, "authoring.md" does not); verify verdict
`inconclusive`; the owner kept an error-severity finding.
At least medium: more than 400 changed lines; more than one fix round; an untested
scenario on a change that has something to run; more than 150 deleted lines.

## .ship.yaml

Lives at the repository root on main. cc-ship reads it **only from origin/main**, so a
branch cannot weaken its own gate. It is written in JSON syntax (valid YAML), so cc-ship
needs nothing beyond the Python standard library.

```json
{
  "checks": ["npm --prefix website ci", "npm --prefix website run lint", "npm --prefix website run build"],
  "rules": ["No secrets or customer data.", "No claims about unshipped features in public copy."],
  "reviewer_agent": "Codex",
  "verifier_agent": "ClaudeCode",
  "verify": {"surface": "vercel-preview"},
  "docs_only_paths": ["*.md", "docs/**"],
  "risk": {"high_paths": ["website/api/**", "vercel.json"]}
}
```

`verify.surface`: `vercel-preview` (wait for the commit's preview and hand the verifier
a sign-in bypass), `none` (no deployed surface; the verifier runs what it can), or
`skip` (no verifier; the pull request shows SKIPPED and never merges by itself).

## Setup on a machine

- Python 3.11 or newer, `git`, `gh` (signed in), `curl`, `cc-devthrottle`, `playwright-cli`.
- The reviewer agent installed and signed in (for a Claude Code author: `codex login`).
- Both agents must trust the repository, or their sessions quit at once (issue 2898):
  Claude Code - open it once in the repository and accept; Codex - in
  `~/.codex/config.toml`: `[projects."<repo root>"]` with `trust_level = "trusted"`.
  cc-ship checks this before spawning and says exactly what to do.
- For Vercel previews: `VERCEL_AUTOMATION_BYPASS_SECRET=<secret>` in
  `<cc-director data>/config/credentials.env` (Vercel project Settings, Deployment
  Protection, Protection Bypass for Automation). The verifier never sees the secret.
- Install the launcher from a checkout that follows origin/main (see `install.py`):

```
git worktree add --detach ../devthrottle-cc-ship-tool origin/main
python3 ../devthrottle-cc-ship-tool/tools/cc-ship/install.py
```

## Run folder

`<cc-director data>/ship/runs/<run id>/`: `run.json`, `intent.md`, `brief-*.md`,
`review-r*.json`, `diff-r*.patch`, `verify.json`, `evidence/`, `checks-r*.log`,
`pr-body.md`. Owner decisions: `<cc-director data>/ship/decisions/<repo>/<branch>.jsonl`.

## Not in phase 1

- Mission briefs pre-answering kinds of finding (decision D8): every pull request says
  "Pre-answered by mission brief: none".
- The daily audit of merges without an attestation, and retiring `/commit` and
  `/review-code` (phase 2).
- Packaging: the launcher runs a checkout; cc-ship is not in the installer.

Why the design is the way it is, and what the probes proved: `PROBES.md`.
