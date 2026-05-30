---
name: cc-settings-api
description: Read and write CC Director app settings (screenshots location, gateway connection, any config.json key) by calling a running Director's Control API. Triggers on "/cc-settings-api", "set the gateway", "connect this director to the gateway", "set screenshots location", "configure cc-director settings".
---

# CC Director Settings API Skill

Configure CC Director's `config.json` programmatically through a **running Director's**
Control API. Writes are round-trip-preserving (only the keys you set change; every other
section is kept) and gateway changes apply live - the Director re-registers with the
gateway with no app restart.

Use this when the user asks to set the screenshots folder, connect a Director to a central
gateway, or change any cc-director setting from the agent side.

## How it works

A running Director exposes `GET /settings` and `PUT /settings` on a loopback port. The
script `configure_settings.py` discovers the newest live Director from its instance files
at `<config>/director/instances/*.json` (reading `ControlEndpoint` + checking the `Pid` is
alive) and calls those endpoints.

- `<config>` is `%LOCALAPPDATA%\cc-director\config` on Windows, `~/.local/share/cc-director/config`
  on macOS/Linux, or `$CC_DIRECTOR_ROOT/config` if that env var is set.
- Auth is OFF by default (single-user trust boundary), so no token is needed. If a Director
  has auth enabled, pass `--director-token` or the script reads it from
  `<config>/director/gateway-token.txt`.

## Commands

Run from the repo root (the script needs no dependencies beyond the standard library):

```
python .claude/skills/cc-settings-api/configure_settings.py show
python .claude/skills/cc-settings-api/configure_settings.py get screenshots.source_directory
python .claude/skills/cc-settings-api/configure_settings.py set-screenshots "/Users/soren/Desktop"
python .claude/skills/cc-settings-api/configure_settings.py set-gateway --url http://GATEWAY-HOST:7878 --advertised http://THIS-HOST:7879
python .claude/skills/cc-settings-api/configure_settings.py set <dotted.key> <value>
```

## Connecting a Director to a gateway (the main use case)

To make a Director on ANY network show up on a central gateway, set two things:

1. `--url` - the gateway's base URL, reachable from this machine (e.g. `http://gw-host:7878`).
2. `--advertised` - THIS Director's own URL, reachable **from the gateway's machine**. This is
   required whenever the gateway runs on a different machine. Without it the Director
   advertises loopback (`127.0.0.1`), which the gateway cannot call back to. On a Mac this is
   mandatory (the Tailscale auto-detection is Windows-only).

Example - connect a Mac Director (control port 7879) to a gateway on `gw-host`:

```
python .claude/skills/cc-settings-api/configure_settings.py set-gateway \
  --url http://gw-host:7878 \
  --advertised http://mac-host:7879
```

After this the Director re-registers immediately. Verify it appears in the gateway's
`GET /directors`, or re-run `show` to confirm the `gateway` block on disk.

## Notes

- Do NOT hand-edit `config.json` while a Director is running and also use this API - go
  through the API so the live gateway re-apply happens. (The Python `cc-settings` CLI is
  the offline path and is also round-trip-preserving.)
- If no Director is running, the script fails loudly telling you to start CC Director. It
  does not silently write the file - starting the Director is the fix.
- Output is ASCII-only.
