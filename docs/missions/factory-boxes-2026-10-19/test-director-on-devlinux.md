# Factory Boxes phase 1 - a test Director on devlinux, start and stop

Mission: Factory Boxes (issue #4258), phase 1. Done on devlinux on 20 October 2026, from main at `c6da2ab54` (the relay
leg, #4280, merged). This record is written so a phase 2 Developer can run the same test Director without asking.

## Why a test Director at all

devlinux's real Director is the **installed release** (`~/.local/share/cc-director/app/cc-director`). It carries none of
the hosted-desktop list or the desktop relay, and every session on the machine lives on it, so it is never restarted,
replaced, reconfigured or signalled for this work, and nothing here touches `~/.local/share/cc-director`. Proving Boxes
needs a Director built from main: this one, on its own scratch folder.

## The answer, in short

- **Built and run from main, it works.** Against a local hosted Gateway built from the same commit, `GET /boxes` lists
  `demo-desktop-soren` online on the test Director; a ticket opens the real desktop through the Gateway's desktop host in
  headless Chromium (screenshots below); a box the Director does not list is refused **at the Director** and the refusal
  is in its log; it stops cleanly on its own signal in about a second; `~/.local/bin` is unchanged.
- **Three things the brief had differently, found by running it:**
  1. The hosted-desktop list lives at `<root>/instances/default/config/director/hosted-desktops.json`, **not**
     `<root>/config/director/`. The Director moves its storage into the instance folder at start; a file at the brief's
     path is never read and nothing says so (no `HostedDesktopRegistry` line at all).
  2. A test Director **sends reports to production unless told otherwise.** It reports its first start and, until it
     has a key, its errors to the hosted Gateway, which defaults to `gateway.devthrottle.com`. The first start here did
     exactly that before I knew (see "What reached production"). The start script now requires the Gateway to be named.
  3. A fresh root opens the first-run wizard on the screen. The configuration marks it done.
- **Production enrollment cannot be done from the owner's browser profile alone today.** The Director-owned profile
  `centerconsulting` (soren@centerconsulting.com) is **not signed in to devthrottle.com**: `devthrottle.com/account`
  sends it to the sign-in page. Chrome has the owner's email and a saved password filled in, so it is one click, but that
  click is signing in as the owner and it is the owner's to make. The exact clicks are under "Enrolling into production".

## What was built, and how

```bash
export PATH=$HOME/.local/bin:$HOME/.local/share/node/bin:$HOME/.dotnet:$PATH
git fetch origin
git worktree add ../devthrottle-boxes-testdirector -b boxes/phase1-test-director origin/main
cd ../devthrottle-boxes-testdirector

# The Director: the repository's own Linux build script, slot 5 (self-contained, single file, Release, the Session
# Rules hook embedded). It writes local_builds/linux/cc-director-linux5 in the worktree; about 50 seconds.
bash scripts/local-build-linux.sh --slot 5
mkdir -p ~/boxes-test-director/build && cp local_builds/linux/cc-director-linux5 ~/boxes-test-director/build/

# The local hosted Gateway (only for a local run): the hosted launcher, Debug; about 2.5 minutes.
dotnet build src/CcDirector.Gateway.Host -c Debug --nologo -v q
```

`scripts/local-build-avalonia.ps1` (the slot script rule 0b names) needs PowerShell, which devlinux does not have;
`scripts/local-build-linux.sh --slot 5` is its Linux counterpart and is what ran. Build: 108 megabytes, sha256
`ea6f86b6...fc995433`, version `2.26.0+c6da2ab54`.

## The scratch layout

Everything lives outside the repository, in `~/boxes-test-director/`:

| Path | What |
|---|---|
| `build/cc-director-linux5` | The test Director. Stopping and identifying it both go by this exact path. |
| `rig/` | The scripts in this record's `test-director-on-devlinux/rig/` folder. |
| `root/` | The test Director's `CC_DIRECTOR_ROOT`. Its own state is under `root/instances/default/`. |
| `root/instances/default/config/config.json` | Its configuration: `onboarding.completed` and, once enrolled, `gateway.url`, `gateway.token`, `gateway.streamMode`. |
| `root/instances/default/config/director/hosted-desktops.json` | **The hosted-desktop list.** |
| `root/instances/default/config/director/director-id-<hash>.txt` | Its Director id (`8d9a0213-f6e9-4e90-99ac-f9326cebbcfb` here), made on the first start and kept. |
| `root/instances/default/logs/director/director-<date>-<process>.log` | **Its log.** |
| `root/config/lifecycle-signals/` | Where its stop request goes. |
| `root/bin`, `root/instances/default/...` | The Python tools it downloads on every start (about 190 megabytes; see below). |
| `home/` | Its `HOME`. Without this it links its tools into the real `~/.local/bin`. |
| `gateway-root/`, `gateway-home/` | The local Gateway's own root and home (local runs only; deleted afterwards). |

## The environment

`rig/director.sh` sets all of it and refuses to start without the two variables that matter:

| Variable | Value | Why |
|---|---|---|
| `CC_DIRECTOR_ROOT` | `~/boxes-test-director/root` | Its own storage; never the installed Director's. |
| `HOME` | `~/boxes-test-director/home` | A Director on a fresh root links its `cc-*` tools into `$HOME/.local/bin`. |
| `DEVTHROTTLE_HOSTED_GATEWAY_URL` | from `TARGET_GATEWAY`: `http://127.0.0.1:7996` locally, `https://gateway.devthrottle.com` only for the production run | Where it sends its first-start report and its errors before it has a key. **Unset means production.** |
| `DISPLAY`, `XAUTHORITY` | `:0`, `/run/user/1000/gdm/Xauthority` | The owner's X session (see "The display"). Both absolute, so the scratch `HOME` does not break them. |
| `DOTNET_USE_POLLING_FILE_WATCHER` | `1` | devlinux runs out of file-watch instances. |
| removed | `CC_GATEWAY_URL`, `CC_GATEWAY_SESSION_KEY`, `CC_SESSION_ID`, `CC_DIRECTOR_INSTANCE` | So nothing in the test Director acts as the agent session that starts it. |

No `--instance` argument: an unregistered instance name is refused, and a registered one is the owner's.

## The display

The Director is a desktop (Avalonia) application with no headless mode: `Program.Main` always starts the classic desktop
lifetime with platform detection. On devlinux it runs on the owner's existing X session, `DISPLAY=:0` with
`XAUTHORITY=/run/user/1000/gdm/Xauthority`, the same two values the installed Director has. **Its window appears on the
physical screen of devlinux.** There is no virtual display server here: `Xvfb` is not installed, and `Xephyr` (which
is) is a nested server that itself opens a window on `:0`. If a phase needs the window off the screen, `xvfb` must be
installed first, which needs the owner (`sudo apt install xvfb`, through a terminal the owner types in), and then
`DISPLAY` points at it. Not needed for anything in this record. With a fresh root the first start opens the first-run
wizard on that screen; `onboarding.completed: true` in `config.json` stops it.

## The hosted-desktop list

`~/boxes-test-director/root/instances/default/config/director/hosted-desktops.json`:

```json
{
  "desktops": [
    { "boxId": "demo-desktop-soren", "displayName": "Soren's demo desktop", "port": 3110 }
  ]
}
```

Port 3110 is where `demo-desktop-soren` listens on `127.0.0.1` (`docker ps`: `127.0.0.1:3110->3000/tcp`). The container
itself was not touched. The proof it was read is the Director's own line:
`[HostedDesktopRegistry] Reload: this Director hosts 1 desktop(s): demo-desktop-soren on port 3110`. No such line means
the file is in the wrong folder.

## Starting it

```bash
RIG_DIR=$HOME/boxes-test-director TARGET_GATEWAY=http://127.0.0.1:7996 bash ~/boxes-test-director/rig/director.sh
```

Run it from the agent's own shell as a visible background command (it does not return while the Director runs), never
under `timeout`. Ready when its log says `[GatewayStreamClient] connected to <gateway>` and `reseeded full snapshot`.

## Enrolling it - locally

The local hosted Gateway is the relay record's rig, unchanged in shape (`rig/postgres.sh`, `rig/gateway.sh`,
`rig/mint.py`): the hosted launcher with `CC_GATEWAY_HOSTED=1`, a throwaway PostgreSQL 16 container
(`boxes-testdir-postgres`, `127.0.0.1:55432`), a throwaway signing key for one made-up account on the reserved
`.invalid` domain, every website call pointed at a closed port, the desktop host `http://localhost:7996` beside the main
host `http://127.0.0.1:7996`. Nothing in it reaches production sign-in.

```bash
H=$HOME/boxes-test-director; T=/home/soren/ReposFred/devthrottle-boxes-testdirector
bash $H/rig/postgres.sh
RIG_DIR=$H TREE=$T bash $H/rig/gateway.sh ""          # background; Boxes off until the account exists
# the one table the Gateway does not create (the payment side writes it), with one active hosted row for the account
docker exec boxes-testdir-postgres psql -U testdir -d gateway \
  -c "CREATE TABLE IF NOT EXISTS gateway.entitlements (subject uuid NOT NULL PRIMARY KEY, status text NOT NULL, current_period_end timestamptz NULL, stripe_subscription_id text NULL, updated_at timestamptz NULL, livemode boolean NULL, tier text NULL);" \
  -c "INSERT INTO gateway.entitlements (subject, status, livemode, tier) VALUES ('0b0c5e1e-0000-4000-8000-000000004259', 'active', true, 'hosted');"
```

1. **Start the Director once with no Gateway** to make its id, read it from its log
   (`[DirectorIdentitySlot] LoadOrCreate: minted id=...`), and stop it (below).
2. **Two keys from the Director door**, `POST /devices/enroll-hosted` with the made-up account's token
   (`python3 rig/mint.py token`) as the bearer and `{deviceId, machineName, platform:"linux", deviceType:"workstation"}`:
   one with the Director's own id, one with a made-up id that plays the Cockpit (the relay record explains why the second
   key stands in for the Cockpit's).
3. **The account's tenant id**: `select "Id" from gateway.tenants` (one row). Restart the Gateway with it:
   `RIG_DIR=$H TREE=$T bash $H/rig/gateway.sh "<tenant id>"`. Its log says
   `[BoxesSwitch] Factory Boxes is on for 1 configured account(s)`.
4. **The Director's configuration** (`root/instances/default/config/config.json`):
   `{"gateway": {"url": "http://127.0.0.1:7996", "token": "<the Director's key>", "streamMode": true}, "onboarding": {"completed": true}}`,
   and the same key in `root/instances/default/config/director/gateway-token.txt` (what `GatewayCredentialStore.SaveEnrolledKey` writes).
5. Start the Director again (above).

## Enrolling it - into production (found out, NOT done)

Not done, on purpose: the brief holds it until the Delivery Lead has deployed, because production before the deploy
carries code without the list, the push or the relay and would prove nothing.

**How a Director joins the hosted Gateway.** Only through its own window: Settings, the Gateway tab, **Use a hosted
Gateway** (`GatewayConnectionPanel.HostedEnrollAndHandshakeAsync`). There is no command for it: the panel's comments
mention a command-line `enroll --hosted`, which no longer exists. The Director starts a listener on a free port on
`127.0.0.1`, opens `https://devthrottle.com/signin?redirect_uri=http://127.0.0.1:<port>/...`, and the page hands the
signed-in account's token back to that listener. The Director then calls `POST /devices/enroll-hosted` itself, stores
the key and the address, and connects. The address is also shown in the panel with a **Copy** button (issue #3504),
because opening a browser can fail.

**What I found about the owner's profile** (`cc-devthrottle browser list --json`: `centerconsulting`, account
soren@centerconsulting.com, running, port 9310). Opened in a new tab and closed again; nothing typed or clicked:

- `https://devthrottle.com/signin` with no `redirect_uri` answers "Can't sign in here ... Open it from the Director's
  Sign in button", as it should.
- `https://devthrottle.com/account` goes to `https://devthrottle.com/login`. **The profile is not signed in to
  devthrottle.com.** Chrome has filled the email (the owner's) and a saved password; only the "Sign in" click is
  missing. I did not click it: that is signing in as the owner.
- Not checked, because it needs a real sign-in: whether the sign-in page, once signed in, hands back at once or asks
  for a click; and which browser the Director's own "open the browser" starts under the scratch `HOME` (it goes through
  `xdg-open` with no browser settings in that home, so probably not the owner's profile). The Copy box covers both.

**The steps, once the Delivery Lead has deployed** (the owner's account in `DEVTHROTTLE_BOXES_TENANT_IDS` and
`CC_BOXES_DESKTOP_ORIGIN` set on production, through the deploy skill):

1. **The owner, by hand, once:** in the Chrome window of the CenterConsulting profile on devlinux, open
   `https://devthrottle.com/login` and click **Sign in** (the email and password are already filled), or **Continue
   with Google** if that is how the account signs in. After this the profile stays signed in; the next steps need no
   owner.
2. Empty the test Director's `gateway` block from `config.json` if one is left from a local run (this record's clean-up
   already did), and start it against production:
   `RIG_DIR=$HOME/boxes-test-director TARGET_GATEWAY=https://gateway.devthrottle.com bash ~/boxes-test-director/rig/director.sh`.
3. In the test Director's window (on devlinux's screen): **Settings** (top right), the **Gateway** tab, **Use a hosted
   Gateway**. The owner can click these; an agent cannot, because nothing on devlinux clicks into another program's
   window (`xdotool` is not installed; only `wmctrl`, which moves windows and does not click).
4. The panel shows "Signing in to DevThrottle to join the hosted Gateway..." and, under it, the sign-in address. Copy it
   (the **Copy** button) into a tab of the CenterConsulting profile. An agent can do this part through the profile
   (`browser-harness`, `new_tab(<address>)`), reading the address from the panel. The setup engine also logs it
   (`[GatewayAccountEnrollRunner] SignInViaBrowserAsync: sign-in url=...`, through `EngineLog`); which file that lands in
   on a test Director was not checked. The page hands the session back to the
   Director's listener on `127.0.0.1`, on the same machine.
5. If production has Teams released, the Director asks which team (screen D1): choose the owner's **own account**, not
   a team, so the Director lands in the account the Boxes flag names.
6. The panel ends on "Connected". Within ten seconds the Cockpit's Boxes screen (or `GET /boxes`) lists
   `demo-desktop-soren` on this Director.

One click in step 1 is the owner's, and the clicks in step 3 are the owner's unless `xdotool` is installed (it needs
`sudo`, so the owner again). Afterwards the test Director is a real Director of the owner's account: stop it cleanly
when the check is done, and remove it from the account's devices if it will not be used again.

## Stopping it cleanly

```bash
RIG_DIR=$HOME/boxes-test-director bash ~/boxes-test-director/rig/stop-director.sh
```

It finds the process by the exact path of `build/cc-director-linux5`, reads the Director id from the test Director's own
log line `[LifecycleSignal] Listen: name=cc-director-shutdown-<id>`, writes
`root/config/lifecycle-signals/cc-director-shutdown-<id>.request` holding an ISO 8601 time with **no trailing newline**
(`printf '%s' "$(date -u +%FT%TZ)"`; anything the Director cannot read as a time is deleted with no log line and it keeps
running), and waits up to 30 seconds. **It never kills anything.** If the Director has not gone by then it says so and
stops. Force-killing is then a decision by hand, and only for a process whose `/proc/<pid>/exe` is this build. Both stops
here took about a second; the log ends `[LifecycleSignal] cc-director-shutdown-...: signalled`, then `[CcDirector] Exiting`.

The local Gateway is stopped with `SIGTERM` to the process whose `/proc/<pid>/exe` is the worktree's
`CcDirector.Gateway.Host`, and the PostgreSQL container with `docker rm -f -v boxes-testdir-postgres`.

## The ~/.local/bin check

Before the first start and after every stop:

```bash
readlink ~/.local/bin/cc-devthrottle   # must be /home/soren/.local/share/cc-director/pyenv/bin/cc-devthrottle
ls -la ~/.local/bin | diff - <the listing taken before the first start>
```

Unchanged after both starts here (`local-bin-before.txt`, `local-bin-after.txt`; the first stop checked the same). If it
ever changes, **stop and tell the Tech Lead; do not repair it.**

## What the local run proved (`test-director-on-devlinux/`)

| Check | Result | File |
|---|---|---|
| `GET /boxes` lists the desktop | `{"count":1,"boxes":[{"boxId":"demo-desktop-soren","displayName":"Soren's demo desktop","directorId":"8d9a0213-...","directorName":"devlinux","machineName":"devlinux","state":"Online","relativeDesktopPath":"/boxes/demo-desktop-soren/desktop/"}]}`, HTTP 200 | `get-boxes.txt` |
| A ticket opens the desktop through the desktop host | Ticket 200; Chromium crossed from the main host to `http://localhost:7996/boxes/demo-desktop-soren/desktop/` with the `cc-box-desktop` cookie (HttpOnly, Secure, SameSite=Strict, the box's path) and `document.cookie` empty; all 15 responses 200; the WebSocket carried 248 messages | `open-desktop.log`, `open-desktop.cjs` |
| The Xfce desktop | `demo-desktop-soren`'s desktop through the relay | `1-desktop.png` |
| Typed text | Alt+F2, then `typed-through-the-test-director` in Application Finder; Escape after, so nothing started in the owner's desktop | `2-after-typing.png` |
| A box the Director does not list, refused AT the Director, in its log | See below | `refusal-at-the-director.txt`, `refusal-at-the-director.py`, `director-log-key-lines.txt`, `gateway-log.txt` |
| Plain refusals | An unknown box id and a port as a box id: 404 from the Gateway; `/boxes` signed out: 401; the desktop host without the box cookie: 401 | `refusals.txt` |
| Clean stop, `~/.local/bin` unchanged | Exited one second after the request | `stop.txt`, `local-bin-*.txt` |

**The refusal at the Director, and why it took a race to show it live.** The Gateway looks a box up in the list the
Director pushed before it opens any stream, and the Director re-reads its file in the same step that pushes it. So a box
id no Director lists is refused by the **Gateway** (`refusals.txt`) and never reaches one. The relay record could not
show the Director's own refusal live for this reason. It does happen in one real case: the owner takes a box out of the
file, and a request lands between the Director re-reading it and the Gateway taking the new push. The script holds a
valid box session, runs request loops at the desktop page, empties the file, and puts it back afterwards:

- With 4 loops the gap (about 7 milliseconds between two samples) caught nothing: 200 until +8.024 seconds, then the
  Gateway's 404.
- With 24 loops one request landed in it. The Director, 09:36:03:
  `.644 [HostedDesktopRegistry] Reload: this Director hosts 0 desktop(s): (none)`, then
  `.645 [HostedDesktopRegistry] Resolve REFUSED: box=demo-desktop-soren, requester=desktop stream 206403cd...: this Director hosts no desktop with that box id`.
  The Gateway: `.644` it sent `open-desktop-stream` in the same millisecond it took the Director's empty push (`seq=55
  pushed=0`), and `.648 [DesktopRelayLegs] ... the Director refused the open: NotFound This Director does not host a
  desktop for that box. -> 404`. The browser got `{"error":"This Director does not host a desktop for that box."}` once,
  then the Gateway's own 404 (`No connected Director in your account hosts a desktop for that box.`) 8,974 times.

The list was put back and the Director re-read it ten seconds later (`hosts 1 desktop(s)`).

## What reached production, and what else leaves the machine

**On the first start, before the start script named the Gateway, the test Director reported to production.** With no
`DEVTHROTTLE_HOSTED_GATEWAY_URL` it used `gateway.devthrottle.com`:

- `09:31:16 [FirstOpenReport] director-opened -> accepted` - a first-start report under a new install id (the one in
  the scratch root).
- `09:31:36` and `09:31:54 [ErrorReporter] 1 error(s) from before sign-in sent to DevThrottle (HTTP 202)` - the errors
  were the home screen's "No coding agent found" check (the scratch `HOME` hides the real agent tools).

So production's install records and before-sign-in error records hold one install from a test Director on devlinux, at
those times, with version `2.26.0+c6da2ab54`. Nothing else was sent: no key, no enrollment. The second start, with
`TARGET_GATEWAY` set to the local Gateway, sent nothing to production. **For the Tech Lead:** whether to ask for those
rows to be marked as test noise.

Other outside traffic, all reads: the Director downloads the published Python tool bundles on every start
(`cc-python-linux-x64.tar.gz`, 32 megabytes, and `cc-tools-pyenv-linux-x64.tar.gz`, 157 megabytes, into the scratch root),
and reads the release information. Nothing to change for a test, but each start costs that download.

## Clean-up

**Done for stage 1:**

- The test Director stopped on its signal (twice, no force-kill), and the local Gateway stopped with `SIGTERM`.
- `boxes-testdir-postgres` removed with its volume. `demo-desktop-soren` and `teams-test-director` untouched.
- The local Gateway's roots, its two log files under `/tmp/devthrottle/logs/director/`, the throwaway signing key, the
  two local keys, and the Director's local `gateway` block and `gateway-token.txt` deleted. No key or ticket in any file
  of this folder (checked).
- The tab opened in the owner's profile was closed; that profile is otherwise as it was.
- `~/.local/bin` unchanged.

**Kept for stage 2, deliberately:** `~/boxes-test-director/` with the build, the rig, and the test Director's root (its
id, its desktop list, `onboarding.completed`, the downloaded tools), and the worktree with its Gateway build.

**When Boxes no longer needs it:** stop the test Director (above); if it was enrolled into production, remove it from the
owner's devices in the Cockpit; `rm -rf ~/boxes-test-director`; `git worktree remove ../devthrottle-boxes-testdirector`
from the shared checkout; check `~/.local/bin` one last time.

## What this does not cover

- **Production.** Nothing here ran against `gateway.devthrottle.com` (beyond the reports above). The ticket, the desktop
  host on `devthrottle-gw.azurewebsites.net` and the relay over the real network are for after the deploy.
- **The production sign-in page's own behaviour** once signed in (whether it hands back at once or asks for a click), for
  the reason given above.
- **A phone or the Cockpit's own screens.** The Cockpit's key here came from the Director door, as in the relay record.
