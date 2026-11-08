# Build Locally - build on your own machines instead of GitHub's

Move a repository's GitHub Actions jobs off GitHub's machines and onto machines you own: register a
self-hosted runner, point the workflows at it, prove every job ran there, and keep it healthy.

**Why it pays.** GitHub bills its own machines by the minute for PRIVATE repositories, beyond a small
monthly allowance (since 1 January 2026: Linux about $0.006 a minute, Windows about 1.7 times that,
macOS about 10 times). A job on your own machine is not billed today. (GitHub announced a small
per-minute charge for self-hosted runners in private repositories in December 2025 and then postponed
it with no new date - check GitHub's Actions billing page before promising a customer "free".) GitHub even
runs self-hosted jobs while the account is blocked for billing ("recent account payments have
failed or your spending limit needs to be increased") and refuses only its own machines - proven on
8 Nov 2026, when every hosted job in a private repository was refused and the same jobs ran on our
own Linux box. A software factory runs around the clock, so a build that takes a few minutes longer
costs nothing that matters.

**When it does NOT pay, and is a risk: a PUBLIC repository.** Hosted minutes are free there, and a
self-hosted runner on a public repository can run code from any stranger's pull request from a fork,
on your machine, with your machine's access. Do not put a self-hosted runner on a public repository
unless the owner decides to, and then only with "Require approval for all external contributors" set
under Settings -> Actions -> General -> "Approval for running fork pull request workflows from
contributors" (the API reports it as `approval_policy: all_external_contributors` at
`repos/OWNER/REPO/actions/permissions/fork-pr-contributor-approval`). GitHub's own advice: "We
recommend that you only use self-hosted runners with private repositories."

This skill is optional. Use it only for the work the owner asked for: registering runners and moving
workflows changes how every build in the repository runs.

## The whole job, in order

1. Take stock: which jobs, which operating systems, which machine.
2. Get a registration token.
3. Install and register the runner (usually no admin rights on Linux or Mac).
4. Run it as a service that survives logout and reboot.
5. Fix what a shared, persistent machine does differently from a fresh hosted one.
6. Change the workflows.
7. Prove it.
8. Keep it healthy.

## 1. Take stock

- **List every job and its `runs-on`:** `grep -rn "runs-on" .github/workflows/`. Note each operating
  system you will need a machine for. A job that must build on Windows or macOS needs a Windows or
  macOS machine; nothing else substitutes.
- **Pick the machine.** It must be on, and online, whenever builds should run. A job aimed at an
  offline runner WAITS in the queue (for up to 24 hours, then GitHub cancels it - the run shows as
  cancelled, not failed) - nothing deploys meanwhile. A
  laptop that sleeps is a poor runner; a box that stays on is a good one.
- **List what the hosted image gave you for free** and check the machine has it. Typical gaps found
  moving to our own Linux box: `python` (the box had only `python3`), a system library a test needed
  (`libtk8.6`), Docker (and the runner's user in the `docker` group). Read each workflow's `run:`
  lines and try them on the machine first. Installing a system package needs the machine's admin;
  that is the owner's step - ask, do not work around it.
- **Runners belong to one repository** (or to an organisation, when the repository is in one). A
  machine can host several runners, each in its own folder - one per repository, or two for one
  repository so two jobs run at once. **One runner runs one job at a time.**

## 2. Get a registration token

With a GitHub token that has admin rights on the repository (the owner's `gh` login does):

```bash
TOKEN=$(gh api -X POST repos/OWNER/REPO/actions/runners/registration-token --jq .token)
```

**Capture it into a variable, exactly like this - never run the command bare.** A bare run prints the
token on the screen, and a session's screen is saved. For one hour that token lets anyone register a
runner on the repository, which is then handed its jobs and their secrets. Never echo it, log it,
commit it or put it in a message. For an organisation-wide runner:
`orgs/ORG/actions/runners/registration-token`.

## 3. Install and register (Linux and macOS: no sudo, usually)

Use the runner version GitHub offers today
(`gh api repos/actions/runner/releases/latest --jq .tag_name`) and the archive for the machine
(`linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`).

```bash
mkdir ~/actions-runner-REPO && cd ~/actions-runner-REPO
curl -fsSL -o runner.tar.gz \
  https://github.com/actions/runner/releases/download/vX.Y.Z/actions-runner-linux-x64-X.Y.Z.tar.gz
tar xzf runner.tar.gz
./config.sh --unattended \
  --url https://github.com/OWNER/REPO \
  --token "$TOKEN" \
  --name MACHINE-REPO-1 \
  --labels REPO-linux \
  --work _work
```

- **If `config.sh` stops with "Libicu's dependencies is missing" or "Dependencies is missing"**, the
  machine lacks a system library the runner needs (common on a bare server; a desktop Linux usually
  has them). The fix is `sudo ./bin/installdependencies.sh` in the runner folder - an admin step, so
  it is the owner's. Ask; do not work around it.
- **Give every repository its OWN label** (`REPO-linux`, not just `linux`). Workflows target the
  label, so a job can never land on another repository's runner by accident, and you can tell from
  the label alone which repository a runner serves.
- Name the runner after the machine and the repository, and number it: `devlinux-internal-2`.
- A second runner for the same repository: a second folder, its own name, the same label.

## 4. Run it as a service

### Linux - a systemd USER service

GitHub's `svc.sh install` writes a system service and needs sudo. A USER service does the same job
without it. One unit per runner folder, in `~/.config/systemd/user/gh-runner-REPO-1.service`:

```ini
[Unit]
Description=GitHub Actions runner - REPO 1

[Service]
WorkingDirectory=%h/actions-runner-REPO-1
ExecStart=%h/actions-runner-REPO-1/run.sh
Nice=10
# No screen, like a GitHub-hosted runner: tests that need a display skip instead of opening
# windows on the machine's live desktop.
UnsetEnvironment=DISPLAY WAYLAND_DISPLAY
Restart=always
KillSignal=SIGTERM
TimeoutStopSec=5min

[Install]
WantedBy=default.target
```

```bash
loginctl enable-linger "$USER"     # keep user services running with nobody logged in, and after a reboot
loginctl show-user "$USER" -p Linger   # must say Linger=yes
systemctl --user daemon-reload
systemctl --user enable --now gh-runner-REPO-1
```

- **NEVER `KillMode=process`** on a unit that runs `run.sh`. Stopping it leaves the old
  `Runner.Listener` running, and the restart starts a second one: two copies of one runner. (This
  happened.) Keep the default kill mode. GitHub's own `svc.sh` unit carries `KillMode=process`
  because it runs a different script (`runsvc.sh`); do not copy that line.
- **No `After=network-online.target`.** A user service manager has no such target and silently
  ignores the line. If the runner starts before the network is up, it retries on its own and
  `Restart=always` covers the rest.
- **`Nice=10`** keeps builds behind interactive work on the machine.
- **No CPU or memory cap.** Owner ruling, 8 Nov 2026: "there's no cap per machine because this is
  our own hardware." Do not add `CPUQuota` or `MemoryMax` unless the owner asks for one.
- **`UnsetEnvironment=DISPLAY WAYLAND_DISPLAY`** matters on any machine with a desktop. Without it a
  user service inherits the desktop session, and a test that opens a window opens it on the
  machine's monitor instead of skipping as it does on GitHub's machines.

### macOS

`./svc.sh install` on macOS installs a LaunchAgent for the current user (no sudo), then
`./svc.sh start`. The machine must stay logged in to that user, and must not sleep:
`sudo pmset -a sleep 0` is the owner's step (it needs admin).

### Windows

`config.cmd` asks to run as a Windows service; that install needs an administrator PowerShell, so it
is the owner's step (or the step of whoever holds admin on that machine). The service runs as
`NT AUTHORITY\NETWORK SERVICE` by default. A build on someone's working machine is FELT - pick a
machine nobody is typing on, or accept that.

**Windows and macOS have not yet been proven end to end by DevThrottle the way Linux has.** When
you do one, write down what you met in this section.

## 5. What a shared, persistent machine does differently

GitHub gives every job a fresh, empty machine. Your machine is the same machine every time, and
several runners may share it. Each difference below broke a real build:

- **The workspace persists.** Checkout is fast after the first clone (the first took 9 minutes; then
  about 1 second). But anything a job writes stays. **Remove secrets the job pulled into the
  workspace** in a step that runs pass or fail:

  ```yaml
  - name: Remove pulled secrets
    if: always()
    run: rm -rf .vercel
  ```

- **Fixed ports collide.** Two builds on one machine that both serve on, say, port 4199 fail each
  other. Give each runner its own port in its folder's `.env` file (`PRERENDER_PORT=4291`; the
  runner reads `.env` when it starts, so restart the service after editing) and make the job FAIL
  if the value is missing rather than fall back to the shared default.
- **Fixed container names collide.** Docker containers with fixed names cannot run twice at once.
  Put those jobs in one `concurrency:` group so they run one at a time. GitHub keeps one running
  and one pending job per group; a third cancels the pending one.
- **Tool caches persist.** A Python or Node the runner set up last time is still there, with
  whatever packages it had. Install with `pip install --upgrade --upgrade-strategy eager ...` when a
  job needs the latest versions, or an older run's packages answer instead.
- **Drop the GitHub download cache** (`actions/setup-node` `cache: npm`, and the like). On your own
  machine it downloads GitHub's stored cache over your internet connection - it cost 3 of a 10
  minute deploy over a home connection - while the machine already keeps its own `~/.npm`.

## 6. Change the workflows

```yaml
runs-on: [self-hosted, REPO-linux]
```

Change one workflow first, prove it (step 7), then the rest. Put a comment at each `runs-on` saying
which machine it is and what happens if it is off ("if MACHINE is off, this job waits in the queue
and nothing deploys"), so the next person reading a queued run knows where to look.

## 7. Prove it

A green run is not the proof - a hosted job turns green too. The proof is WHICH MACHINE ran it:

```bash
gh run list -R OWNER/REPO --limit 5
gh api repos/OWNER/REPO/actions/runs/RUN_ID/jobs --jq '.jobs[] | "\(.name): \(.runner_name)"'
```

Every job must name your runner. A job naming `GitHub Actions NN` ran on GitHub's machine. For
something that deploys, also stamp the build: write the commit (`GITHUB_SHA`) and the runner
(`RUNNER_NAME`) into a small file in the output (we serve `build-info.json`), and read it back from
the live site.

Expect the first run to be slow (one-time clone and caches: 22 minutes for a deploy that took 8 on
GitHub), the second faster, and later runs about as fast as GitHub's.

## 8. Keep it healthy

- **Exactly one `Runner.Listener` per runner folder**, after every restart:
  `pgrep -af Runner.Listener`.
- **Every runner online:** `gh api repos/OWNER/REPO/actions/runners --jq '.runners[] | "\(.name) \(.status) \(.busy)"'`.
- **A queued job that never starts usually means the machine is off or asleep**, not that the
  workflow is broken. Check the runner list before anything else.
- **A runner missing from the list was removed by GitHub.** GitHub deletes a runner that has not
  connected for more than 14 days. When the machine comes back, its service starts, finds its
  registration gone and exits, over and over, while every job for its label queues and is cancelled
  after 24 hours. `./config.sh` will NOT re-register over the old files ("Cannot configure the runner
  because it is already configured"), and `./config.sh remove` cannot help either - it needs a
  registration GitHub no longer has. GitHub's own fix: stop the service, delete `.runner`,
  `.credentials` and `.credentials_rsaparams` in the runner folder, run the step 3 `./config.sh`
  command again with a new token (step 2), same name and labels, then start the service.
- **To retire a runner, in this order:** stop and disable its service FIRST
  (`systemctl --user disable --now gh-runner-REPO-1`) - removed the other way round, the still-running
  service restarts in a tight loop with "Not configured". Then get a removal token, captured, never
  printed (`TOKEN=$(gh api -X POST repos/OWNER/REPO/actions/runners/remove-token --jq .token)`), run
  `./config.sh remove --token "$TOKEN"` in its folder, and delete the folder. A runner deleted only
  from GitHub leaves its service on the machine retrying forever.
