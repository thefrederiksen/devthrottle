# cc-launcher

A tiny localhost REST daemon (Python stdlib, no deps) that an agent can POST to in
order to **launch CC Director and control windows** on the Mac — without a human in
the loop.

## Why it exists

When the Claude Code agent spawns `cc-director` from its own process tree, the
child `claude` processes inherit the agent's non-TTY pseudo-console and die. This
daemon is registered with **launchd**, so it runs in the clean Aqua GUI session
*outside* the agent's process tree. Because the daemon becomes the parent,
cc-director (and its child claudes) launch cleanly every time — enabling an
edit → rebuild → restart → screenshot loop with no human relaunch.

Since it already lives in the GUI session, it's also the right place to drive any
app's windows via the macOS Accessibility API.

## Endpoints (127.0.0.1 only)

| Method | Path | Body / query | Purpose |
|--------|------|--------------|---------|
| GET  | `/status` | — | `{running, pid, uptime_s, bin, exists}` |
| POST | `/start` | — | start cc-director if not running |
| POST | `/stop` | — | stop the cc-director we started |
| POST | `/restart` | — | stop + start |
| GET  | `/screenshot` | — | capture screen → PNG path under `shots/` |
| GET  | `/logs` | `?n=80` | last n lines of cc-director stdout/stderr |
| GET  | `/windows` | `?app=NAME` (optional) | list windows — all foreground apps, or just `NAME` |
| POST | `/window/minimize` | `{"app":"NAME","window":1}` | minimize a window (`AXMinimized = true`) |
| POST | `/window/restore`  | `{"app":"NAME","window":1}` | un-minimize (`AXMinimized = false`) |
| POST | `/window/maximize` | `{"app":"NAME","window":1}` | press the green **zoom** button (`AXZoomButton`) |
| POST | `/window/focus`    | `{"app":"NAME","window":1}` | bring app + window to the front |

`app` is the process name as shown by `/windows` (e.g. `Finder`, `cc-director`).
`window` defaults to `1` (frontmost). Every response is JSON with an `ok` flag.

## Required permissions (one-time, manual — cannot be scripted)

The daemon's interpreter (`/usr/bin/python3`) must be granted, in
**System Settings → Privacy & Security**:

- **Accessibility** — for all `/window/*` ops and `/windows`. Without it those calls
  return `ok:false` with an `AppleEvent`/`not permitted` error (a TCC prompt also
  appears the first time).
- **Screen Recording** — for `/screenshot`.

After granting, restart the agent: `launchctl kickstart -k gui/$(id -u)/com.centerconsulting.cc-launcher`.

## Install

```bash
# Optionally override CCD_BIN / DOTNET_ROOT first; defaults target local_builds/mac.
./install.sh
curl -s http://127.0.0.1:8765/status
```

Idempotent: re-running re-renders the plist and reloads the launchd agent
`com.centerconsulting.cc-launcher`.

## Examples

```bash
curl -s "http://127.0.0.1:8765/windows"
curl -s "http://127.0.0.1:8765/windows?app=cc-director"
curl -s -X POST http://127.0.0.1:8765/window/maximize -d '{"app":"cc-director"}'
curl -s -X POST http://127.0.0.1:8765/window/minimize -d '{"app":"Finder","window":1}'
curl -s -X POST http://127.0.0.1:8765/restart
```

## Config (env vars, set in the plist)

| Var | Default | Meaning |
|-----|---------|---------|
| `CCD_BIN` | `~/ReposFred/cc-director/local_builds/mac/cc-director-mac1` | binary to launch |
| `DOTNET_ROOT` | `~/.dotnet` | .NET runtime for the framework-dependent build |
| `CCL_PORT` | `8765` | listen port |
| `CCL_DIR` | this dir | working/log directory |
