# cc-ship

Take a finished change from a session all the way to **merged on origin/main**
(issue 2935, phase 1). The author session runs it; everything else is a normal,
tracked fleet session. There is no background service.

```
cc-ship start --intent ~/notes/intent.md   # open a run (intent.md lives OUTSIDE the worktree)
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
| (any step) | What ships is exactly what was checked, reviewed and verified: if HEAD moves during a run, every step runs again on the new head; uncommitted or untracked files stop the run until they are committed or removed (ignored files do not count; keep `intent.md` outside the worktree) |
| Local checks | The commands in `.ship.yaml` **from origin/main**. A failure goes back to the author with the output |
| Review | A fresh session of another agent family reads the diff, `intent.md` and the owner's decisions, and writes `review-rN.json`. Invalid files get at most two correction turns |
| Fix | `auto-fix` findings go to the author: commit, `cc-ship continue`, and a **new** reviewer reviews the whole change. After 3 rounds the run parks and the owner decides with `cc-ship respond fix-limit --fix` (exactly one more round), `--keep` (ship as is, findings recorded as kept) or `--drop` |
| Owner calls | `ask-owner` findings park the run (`waiting-on-owner`). The author relays them word for word and ends its turn. `cc-ship respond` records each answer. A kept or dropped finding is never raised again on the branch: the reviewer's brief lists kept and dropped decisions with ids (D1, D2...) as closed, and "fix" decisions as things to re-check. An identical finding is dropped; a reworded one the reviewer is CERTAIN is the same may carry `same_as_decision` and is noted on the pull request instead of asked; anything else, including a similar-looking one, goes to the owner |
| Verify | A separate session always verifies. It drives the real product and writes `verify.json` plus screenshots. For a documents-only change it confirms there is nothing to run ("Nothing to run live: documents only") |
| Risk | The highest of the reviewer's level and the rules below |
| Pull request | Push, publish evidence to the `ship-evidence` branch, open or update the pull request with the fixed body and the attestation line |
| Merge | Low or medium risk, every step completed, verdict go or documents-only, no FAILED check: squash-merge that exact head and delete the branch. Otherwise the pull request is parked for the owner. Pending or missing checks never delay anything. If an earlier attempt merged but did not record it, `cc-ship continue` finds the merge instead of repeating it |

States: `working`, `waiting-on-author`, `waiting-on-owner`, `failed`, `merged`,
`aborted`. On `waiting-on-owner` the author tells the owner and **ends its turn**.

## Risk

High: a `risk.high_paths` match; any path whose words (split at separators and
camelCase) include auth, oauth, login, signin, signup, password, credential, secret,
apikey or "api key", key, keys, keyring, keystore, keychain, crypto, encrypt, decrypt,
hmac, signing, vault, token, jwt, tenant, rls, migration or schema, or that ends in
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

`reviewer_model` (optional, ClaudeCode only, a full model id such as `claude-fable-5-1` -
not an alias like `opus`): the model the reviewer runs, set on its command line (the
owner's default model is never changed). A reviewer of the
author's own family is refused unless `reviewer_model` names a model different from the
author's; the pull request then says the review came from the same family on a different
model, which is a weaker check than another family (owner decision, 2026-09-16).

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
- For Vercel previews: the cc-secrets entry `vercel-automation-bypass-secret` (the
  secret from Vercel project Settings, Deployment Protection, Protection Bypass for
  Automation), and curl 8.3 or newer. cc-ship runs its one curl call through
  `cc-secrets run`, so neither cc-ship nor the verifier ever sees the secret, and it is
  only ever sent over https to a `*.vercel.app` preview.
- The DevThrottle installer puts `cc-ship` on your PATH, in the one tools folder on the
  machine, with every other shipped tool. Nothing here needs installing by hand.

  To run a CHECKOUT instead of the installed copy - developing the tool itself - use
  `python tools/cc-ship/main.py ...`, or put a launcher for one checkout on your own PATH
  with `install.py`. A hand-made launcher and the installed tool are two sources for one
  name, so keep the launcher out of the folder the installer owns.

## Run folder

`<cc-director data>/ship/runs/<run id>/`: `run.json`, `intent.md`, `brief-*.md`,
`review-r*.json`, `diff-r*.patch`, `verify.json`, `evidence/`, `checks-r*.log`,
`pr-body.md`. Owner decisions: `<cc-director data>/ship/decisions/<repo>/<branch>.jsonl`.

## Not in phase 1

- Mission briefs pre-answering kinds of finding (decision D8): every pull request says
  "Pre-answered by mission brief: none".
- The daily audit of merges without an attestation, and retiring `/commit` and
  `/review-code` (phase 2).

Why the design is the way it is, and what the probes proved: `PROBES.md`.
