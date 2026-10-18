# Session Rules phase 1 - the Linux live rig, and how to run the live check with it

Written 2026-10-18 by Developer D ("Session Rules - Developer - phase 1 Linux live rig") for the phase 1
Tech Lead. The rig is `scripts/session-rules-rig.sh`. This file says what it stands up, what it needs,
and exactly how to run live check rows 1 and 2 of `mission.md` section 7, and the September usage-limit
screen rule, on this Linux machine. Results go into `phase-1-proof.md`, as `phase-1-plan.md` says.

---

## 1. What the rig is

One script, one folder (`RIG_DIR`, default `~/.local/state/session-rules-rig`), two processes:

| Part | What it is |
|---|---|
| Gateway | `src/CcDirector.Gateway` published from the worktree you name: the self-host console Gateway, on its own port (default 7931), on its own storage root, auth ON. On Linux this is the desktop Gateway - the tray build (`CcDirector.GatewayApp`) targets Windows only. Built without the Cockpit, the mobile app and the web typecheck, because phase 1 has no page and all three need Node.js. |
| Director | The worktree's own `scripts/local-build-linux.sh --slot 6 --framework-dependent` (slot from `RIG_SLOT`, 5 or higher), connected to the rig Gateway with the rig Gateway's token and nothing else. |
| Tool | The worktree's own `tools/cc-devthrottle`, run with the machine's tool Python, acting with the rig token and as no session. |

**How it keeps away from the owner's fleet.**

- Both processes share the rig's storage root (`CC_DIRECTOR_ROOT=<rig>/root`) and a HOME of their own
  (`<rig>/home`). A Director on a fresh root links its cc-* tools into `~/.local/bin` at start-up whatever
  `CC_DIRECTOR_ROOT` says; with the rig's HOME those links land in `<rig>/home/.local/bin`. Proven on
  2026-10-18: the owner's `~/.local/bin` listing was identical before and after a full up and down, and
  the twelve links appeared under the rig's HOME instead.
- Both start from an empty environment (`env -i`) plus an allow-list: no `CC_GATEWAY_URL`, session key,
  session id or Director root of the calling session reaches them. `CC_GATEWAY_NO_AUTH` and
  `CC_GATEWAY_AUTH` are never passed, and `up` refuses to go on unless `/sessions` without the token is
  refused (it answered 401).
- The port 7878 and slots below 5 are refused. The rig folder must carry `session-rules-rig` in its name,
  must not be inside or around `~/.local/share/cc-director`, and is deleted (`reset`) only if it holds the
  script's own marker file.
- Every process the script acts on is found by its executable path under the rig folder - never by name.
  The Director is stopped with its OWN named shutdown signal (on Linux the request file
  `<rig>/root/config/lifecycle-signals/cc-director-shutdown-<directorId>.request`); the Gateway, which has
  no shutdown request, with the ordinary terminate signal. Nothing is force-killed; `down` says what is
  left if anything does not exit.
- The Gateway listens on all interfaces (the product's own choice, `GatewayHost`), gated by its token.

**Detached, not hidden.** Both processes start with `setsid` and `nohup` so they outlive the shell that
ran `up`; `up` and `status` print their process ids, and they log to `<rig>/logs/` and
`<rig>/root/instances/default/logs/director/`.

---

## 2. What it needs on the machine

1. **.NET 10** at `~/.dotnet/dotnet` (the script finds it there).
2. **gcc**, once the hook branch is merged: per ruling P16 the Director's publish builds `cc-rule-hook`
   natively and carries it. A Director built without the native compiler carries no hook and registers no
   PreToolUse hook; `status` says whether `<rig>/root/instances/default/claude-hooks/cc-rule-hook` exists.
3. **inotify headroom - THE BLOCKER ON 2026-10-18.** The Director watches files with inotify, and Linux
   allows each user 128 inotify instances by default. On devlinux all 128 were in use (the owner's Claude
   Code sessions held 25, the owner's Director 12, the desktop and browsers the rest), so the rig Director
   opened its window but logged `Director services failed to start: The configured user limit (128) on
   the number of inotify instances has been reached` and never joined a Gateway. `up` now counts first
   and refuses with the fix when fewer than 24 are free. The fix needs the machine's owner (sudo):

       sudo sysctl fs.inotify.max_user_instances=1024
       echo fs.inotify.max_user_instances=1024 | sudo tee /etc/sysctl.d/60-inotify-instances.conf

   The first line raises it now; the second keeps it after a restart. It changes a per-user ceiling only.
4. **A Claude Code sign-in with at least two hours left.** A Claude Code session under the rig's HOME has
   no sign-in, so `spawn-session` copies `~/.claude/.credentials.json` and `~/.claude.json` (without its
   per-folder history) into the rig's HOME. A copy that refreshed would rotate the owner's own sign-in out
   from under the owner's real file, so the copy is refused with under `RIG_MIN_SIGNIN_MINUTES` (120)
   left, and `down` deletes it. This is the in-repository precedent, `ScratchClaudeConfig` in
   `src/CcDirector.DeliveryQualification/Rig.cs`. Keep a rig run short; `down` when finished.
5. **For the screen rule only: a DevThrottle inference key** - see section 5.

---

## 3. The commands

    scripts/session-rules-rig.sh build <mission worktree>   # publish Gateway, Director and tool from that worktree (about a minute)
    scripts/session-rules-rig.sh up                         # Gateway, then Director; prints both process ids
    scripts/session-rules-rig.sh status
    scripts/session-rules-rig.sh make-repos                 # <rig>/repos/repo-a and repo-b
    scripts/session-rules-rig.sh spawn-session <dir>        # a Claude Code session on the rig Director
    scripts/session-rules-rig.sh spawn-session <dir> --agent RawCli --command bash
    scripts/session-rules-rig.sh prompt <session> "<text>"
    scripts/session-rules-rig.sh cli <any cc-devthrottle arguments>   # e.g. cli session list
    scripts/session-rules-rig.sh rules list | get <id> | firings <id> | all-firings | delete <id> | promote <id>
    scripts/session-rules-rig.sh rules create <body.json>
    scripts/session-rules-rig.sh rules create-hook-probe <everywhere|machine|repo|repo_on_machine> [--repo R] [--machine M] [--except-machine M] [--match TEXT] [--result deny|allow]
    scripts/session-rules-rig.sh rules create-usage-limit
    scripts/session-rules-rig.sh rules effective [<machine> [<repo>]]   # W1, what the Gateway sends a Director
    scripts/session-rules-rig.sh rules evidence [<session id>]          # W2 rules files, W4 evaluation logs, W3 drops waiting
    scripts/session-rules-rig.sh commands [<filter>]        # each shell command a rig Claude Code session ran, with its call id
    scripts/session-rules-rig.sh down
    scripts/session-rules-rig.sh reset                      # down, then delete the rig folder

**The hook-rule field names** in `create-hook-probe` - `kind`, `appliesTo` (P4), `event`, `tool`,
`handler`, `match`, `result` and `instruction` - were first taken from the plan, before the Gateway piece
existed, and were then checked against the merged `SessionRuleWire.CreateFromBody`: they are the names it
reads. `hook_probe_body` in the script is the one place to change if they ever move. `rules create
<body.json>` sends any body unchanged.

---

## 4. Live check rows 1 and 2, step by step

Run from a worktree on the merged mission branch (`mission/session-rules-hooks-p1`). Write each result
into `phase-1-proof.md` with the commit `build` printed and the exit code.

### 4.0 Stand it up

    R=scripts/session-rules-rig.sh
    $R build ../devthrottle-session-rules-p1        # whatever worktree holds the merged branch
    $R up                                            # note both process ids for the report
    $R make-repos

`make-repos` makes two git repositories with different `origin` remotes, in the two spellings P6 must
treat as one form:

| Repo | origin | normalised (P6) |
|---|---|---|
| A, `<rig>/repos/repo-a` | `https://github.com/session-rules-rig/repo-a.git` | `github.com/session-rules-rig/repo-a` |
| B, `<rig>/repos/repo-b` | `git@github.com:session-rules-rig/repo-b.git` | `github.com/session-rules-rig/repo-b` |

The machine name the Director reports is this machine's (`devlinux`); `up` prints it.

### 4.1 The rules

Four rules, with match strings chosen so none CONTAINS another (P1 is literal containment, so
`echo RULE-PROBE-X` would also match a rule on `echo RULE-PROBE`):

    $R rules create-hook-probe repo    --repo github.com/session-rules-rig/repo-a --match "echo RULE-PROBE"
    $R rules create-hook-probe machine --machine SOREN_NORTH                      --match "echo ELSEWHERE-PROBE"
    $R rules create-hook-probe machine --machine devlinux                         --match "echo HERE-PROBE"
    $R rules list

The third rule is the twin of the second: the same scope kind, aimed at this machine. Without it, a
"no firing" for the other-machine rule cannot be told apart from machine scoping not working at all.

Before any session, read what the Gateway would send (W1) - each is a presence or an absence of a NAMED
rule id, not a count:

    $R rules effective devlinux github.com/session-rules-rig/repo-a    # repo-A rule and HERE rule; NOT the SOREN_NORTH rule
    $R rules effective devlinux github.com/session-rules-rig/repo-b    # HERE rule only
    $R rules effective SOREN_NORTH github.com/session-rules-rig/repo-a # repo-A rule and SOREN_NORTH rule; NOT the HERE rule

### 4.2 The sessions

    $R spawn-session <rig>/repos/repo-a --name "Session Rules rig - repo A"
    $R spawn-session <rig>/repos/repo-b --name "Session Rules rig - repo B"
    $R status                                        # cc-rule-hook must be present now
    $R rules evidence                                # one rules file per session (W2)

Check each session's rules file says the right `repo`, the right `machine`, and holds exactly the rules
W1 gave above. The repo B file must hold the HERE rule and must NOT hold the repo-A rule: that is the
first half of row 2's proof, and it is a presence check on the file's contents.

### 4.3 Row 1 - the positive

    $R prompt <repo A session> "Run exactly this shell command and nothing else: echo RULE-PROBE"

Then, in order:

1. `$R commands repo-a` - the `Bash` call `echo RULE-PROBE` and its call id.
2. `$R rules evidence <repo A session>` - an evaluation log line ending in THAT call id, with decision
   `would_deny`. (A decision file under `decisions/` exists only until the Director forwards it.)
3. `$R rules all-firings` - a firing on the repo-A rule: `decision` `would_deny`, `subject`
   `echo RULE-PROBE`, `repo` `github.com/session-rules-rig/repo-a`, `machine` `devlinux`, event
   `before_command`, tool `shell`.
4. The command still ran: the session's reply shows `RULE-PROBE` printed. Dry run never blocks (P10).

### 4.4 Row 2a - the other repository, the negative control

    $R prompt <repo B session> "Run exactly this shell command and nothing else: echo RULE-PROBE"

1. `$R commands repo-b` - the call id of `echo RULE-PROBE` in repo B.
2. `$R rules evidence <repo B session>` - an evaluation log line ending in THAT call id whose decision is
   `no_match`. **This line is the proof.** No new firing on the repo-A rule is corroboration only:
   silence also follows from a hook that never ran.

### 4.5 Row 2b - the other machine, the negative control, with its twin

In the repo B session (where the repo-A rule does not reach, so nothing else can match):

    $R prompt <repo B session> "Run exactly this shell command and nothing else: echo ELSEWHERE-PROBE"
    $R prompt <repo B session> "Run exactly this shell command and nothing else: echo HERE-PROBE"

1. `$R commands repo-b` - both call ids.
2. `$R rules evidence <repo B session>` - `echo ELSEWHERE-PROBE`'s call id has a `no_match` line;
   `echo HERE-PROBE`'s call id has a `would_deny` line.
3. `$R rules all-firings` - a firing on the HERE rule, none on the SOREN_NORTH rule.
4. Together with 4.1's W1 reads: the SOREN_NORTH rule reaches SOREN_NORTH and not devlinux, the HERE
   rule reaches devlinux, and the hook acted on each accordingly.

The phase 1 check also asks for an `exceptMachines` form; `create-hook-probe machine --except-machine
devlinux --match "..."` makes one, and its command gives `no_match` here.

### 4.6 Stand it down

    $R down        # Director by its named signal, Gateway by terminate, the sign-in copy deleted
    ls -la ~/.local/bin | grep cc-devthrottle   # must still point at ~/.local/share/cc-director/pyenv/bin

---

## 5. The September usage-limit screen rule on the rig

**What it needs.** The Gateway's screen evaluator (`RuleEvaluator`, through
`GatewayRuleEnvironment.AskAgentAsync` and `GatewayHost.WingmanBrainAsync`) asks one model call per pass
at `https://devthrottle.com/api/v1/chat/completions`, model `devthrottle/wingman`, with the key named
`DEVTHROTTLE_API_KEY` read from the Gateway's key vault, `<root>/keyvault.json`. A `dt_live_` or
`dt_test_` key; there is no way to point the self-host Gateway at another endpoint. The plan gate
(`TenantAiGate`) does not stand in the way: a self-hosted Gateway is "not gated". Nothing else is needed.

**How to supply it without a secret in the repository.** The Gateway seeds its vault ONCE, at start-up,
from its own environment variable `DEVTHROTTLE_API_KEY` (`SeedKeyVaultFromEnvironment`), and the rig
passes that variable to the Gateway only when it is given. Two ways:

- The owner stores the key once in the machine's secret store, which nobody reads back in plain text:

      cc-secrets add devthrottle-api-key --env-name DEVTHROTTLE_API_KEY --uses run --notes "DevThrottle inference key for the Session Rules live rig"

  and every rig start uses it, never printed:

      RIG_SCREEN_KEY_SECRET=devthrottle-api-key scripts/session-rules-rig.sh up

  `up` starts the Gateway under `cc-secrets run devthrottle-api-key --via env --env-name
  DEVTHROTTLE_API_KEY`, so the key goes from the store into the Gateway's environment and its vault.
- Or `DEVTHROTTLE_API_KEY` is already set in the shell that runs `up`.

The vault file sits in the rig root (mode 600), outside every repository; `reset` deletes it with the
rig. On 2026-10-18 the machine's secret store held no DevThrottle key, so the owner has to add one before
this row can run; without it the rig still runs hook rules (they call no model), and a screen pass
records that the model could not be asked.

**The steps** (demonstration A of `docs/missions/session-rules-2026-09-02/demonstration-rig.md`, on Linux):

    $R rules create-usage-limit                       # the September rule, in dry run, scoped to RawCli sessions
    $R spawn-session <rig>/repos/repo-b --agent RawCli --command bash --name "Session Rules rig - usage-limit target"
    $R prompt <shell session> "echo \"You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.\""
    # wait for the shell to go quiet (about ten seconds) - nothing is nudged
    $R rules firings <rule id>

The firing must say `decision` `act` with the understanding and reason, and `dry run: nothing was typed.
It would have typed: /usage-credits`. On the merged branch every existing screen rule is `everywhere`
(P4) and a screen rule may not be anything else. Checked on the merged branch: the September body is
accepted unchanged and stored as `kind` `screen`, `appliesTo` `everywhere`.

---

## 6. What was proven on 2026-10-18, and what was not

### On the merged mission branch (`mission/session-rules-hooks-p1` at `4f21e04a7`, rig commit `162f35a7f`)

| Step | Result |
|---|---|
| `build` | PASS, exit 0. The Director build printed `Session Rules hook: embedded (verified in the assembly's managed resources by the build)`. |
| Hook rules through `rules create-hook-probe` | PASS. A repo rule given as `https://github.com/Session-Rules-Rig/repo-a.git` was stored as `github.com/session-rules-rig/repo-a` (P6 at write time). The SOREN_NORTH and devlinux machine rules were stored. `--result block` was refused with the Gateway's own reason. |
| W1 through `rules effective` | PASS, by named rule. devlinux + repo A: the repo-A rule (rank 3) and the devlinux rule (rank 2), and NOT the SOREN_NORTH rule. devlinux + repo B: the devlinux rule only. SOREN_NORTH + repo A: the repo-A rule and the SOREN_NORTH rule, and NOT the devlinux rule. |
| The September body through `rules create-usage-limit` | PASS. Accepted unchanged and stored as `kind` `screen`, `appliesTo` `everywhere`. |
| Gateway stop | PASS. Terminate signal; nothing ran from the rig afterwards. |
| Director up, a Claude Code session answering, rows 1 and 2 | NOT RUN. The inotify limit was still 128 (section 2, item 3). The `sudo` command is waiting for the owner. |

### Earlier, on the rig branch before the merge (`5910331ba`)

| Step | Result |
|---|---|
| `build` | PASS - Gateway, slot-6 Director and tool built from the worktree in 74 seconds, exit 0. |
| `up`, Gateway | PASS - process 2844512, `/healthz` reporting `2.23.0+5910331ba...`; `/sessions` without the token answered 401. |
| `up`, Director | FAIL, machine limit - process 2845650 started, minted id `bf2fe528-f6c9-4171-9787-7755ef91b368`, armed its shutdown signal, then `Director services failed to start: ... inotify instances (128)`; it never joined. See section 2, item 3. |
| `rules create-usage-limit` | PASS - rule `d4675b14-27d1-42ba-a561-2e01e5c8667f` stored in dry run, read back by `rules list`, `rules firings` answered an empty list. |
| `rules create-hook-probe` | Refused by this branch's Gateway with its own reason (`'checks' has to be given...`) - the hook fields are not built here yet, as expected. |
| `down` | PASS - the Director exited on its named signal (request file), the Gateway on terminate, nothing forced; `~/.local/bin` identical before and after. |
| A Claude Code session spawned on the rig and answering | NOT PROVEN - blocked by the inotify limit. |
| Rows 1 and 2, the screen rule | NOT RUN - they need the merged branch, the inotify limit raised, and (screen rule) a key. |
