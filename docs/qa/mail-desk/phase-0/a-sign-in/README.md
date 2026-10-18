# Mail Desk, phase 0, proof (a): the sign-in premise

Date: 18 October 2026. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md`, design 5.4 and the phase 0 row
(issue #4213). Tree: `d091f9083` (origin/main), plus only this folder. No product code changed.

## The verdict

| Point | Verdict | Evidence line (from `transcript.txt`) |
|---|---|---|
| 1. The check finds the real saved sign-in, with every `CC_` variable stripped | **PROVEN** | every run: `"ccVariableNames": ["CC_CHECK_FACTORY", "CC_CHECK_SEAT", "CC_CHECK_SECRET_VARS", "CC_CHECK_STATE_FILE"]`, and `fromAccountRecord.found` is exactly one file: `<home>/.local/share/cc-director/instances/default/config/outlook/tokens/email_devthrottle_com_msal.json` |
| 2. A token for `Mail.ReadWrite` is renewed silently, and a read-only GET works with it | **PROVEN** | every run: `"silentToken": "yes"`, `"tokenSource": "identity_provider"` (freshly minted, not served from the cache), `"inboxTotalItemCount": 19` |
| 3. The token does not carry `Mail.Send` | **NOT PROVEN: the token carries Mail.Send; the guarantee must come from the check's code** | every run: `"scp": ["Calendars.ReadWrite", "Mail.ReadWrite", "Mail.Send", "MailboxSettings.Read", "User.Read", "email", "openid", "profile"]`, `"scpHasMailSend": true` |
| 4. The renewed cache is written back to the same file, owner-only, and cc-outlook still sees the account | **PROVEN** | the file's modification time moved on every run (14:38:18 before, then 14:41:37, 14:44:06, 14:46:36), its hash prefix changed (`82e3b1a9e309` before), `"cacheStateChanged": true`, mode `0o600` before and after, and `cc-outlook accounts list` shows `email@devthrottle.com ... devthrottle ... Yes` before and after |

The Tech Lead was told about point 3 at once, by message, when the first run showed it.

**What point 3 means.** Asking for the narrow scope, even with `force_refresh` so a new token is minted for that one
request, does not narrow the token: Microsoft's version 2 endpoint puts every scope already consented to for Graph
into it. A token that can send mail is in the check's hands whatever it asks for. So "screen.py never sends mail" can
only be true because of screen.py's own code: one function through which every Graph call goes, refusing every
method but GET and the one `POST /me/messages/{id}/move` to `archive` or `junkemail` (design 5.5, step 2), and a test
proving nothing else can leave it. This check has that guard for GET only; the transcript records it refusing POST,
PATCH, DELETE and PUT on every run (`guardRefused`). Changing the Azure app (for example a second app consented only
to `Mail.ReadWrite`) was forbidden for this mission and was not tried.

## A finding that the brief did not ask about: uv on the live Director's PATH

The check declares a package (`msal`), so the Director runs it with `uv run --script`, and looks for `uv` on its own
PATH. A second run of the rig (`transcript-live-path.txt`) started the same installed Director with the PATH the
owner's live Director was started with on devlinux (read from its process environment: `/usr/local/sbin:...:/snap/bin`,
no `~/.local/bin`, where uv is). Result: the check never ran.

- The publish warned: `devlinux has no uv (looked for uv on PATH), and the check 'checks/sign_in_probe.py' declares
  packages, so it runs under uv. Install uv there; the check's run will fail without it.`
- The run failed: `failed ... the check could not start: the check declares packages and uv is not on this machine`.
- The Director's log: `[CheckProcesses] FindOnPath: name=uv, found=(none)`.

This run is not a production verdict: the test Director had a scratch HOME. But the evidence points the same way in
production: this very session, started by the owner's live Director, inherited exactly that PATH with no
`~/.local/bin`, and the checks are given the same inherited environment. So as devlinux stands today, the real
`screen.py` would most likely fail to start on the owner's Director. Fixing it is a decision for the seat above (for
example a `uv` link in `/usr/local/bin`, or starting the Director with `~/.local/bin` on its PATH); I changed nothing
on the machine. In the main run the rig put `~/.local/bin` first on the test Director's PATH, as the brief asked.

## The HOME question

- **Which HOME the check saw.** `HOME=<rig>/home`, the test Director's scratch HOME (`homeVariable` in every run).
  The test Director had to have one: a Director on a fresh root links the cc-* tools into `$HOME/.local/bin`, and the
  scratch HOME's `.local/bin` did receive those links, while the real `~/.local/bin` was unchanged (`~/.local/bin
  unchanged by the run: True`, both runs).
- **The control, from HOME.** It searched `<rig>/home/.local/share/cc-director` and found nothing, on every run
  (`controlFromHomeVariable.found: []`).
- **The real lookup, from the account record.** `pwd.getpwuid(os.getuid()).pw_dir` gave `<home>` (the user's real
  home). It searched `<home>/.local/share/cc-director` under `instances/*/config/outlook/tokens/` and found exactly one
  file, on every run.
- **Why that folder.** A check has every `CC_` variable stripped, so `CC_DIRECTOR_ROOT` (which pointed cc-outlook at
  the instance folder when the owner signed in) is gone, and cc-outlook's own fallback without it (`~/.cc-director` on
  Linux) holds no tokens. The Director's per-user base folder is the one place every instance's saved sign-ins live.
- **The Tech Lead's proposed rule holds.** Search only the Director's per-user base folder (Linux
  `~/.local/share/cc-director`, Windows `%LOCALAPPDATA%\cc-director`, macOS `~/Library/Application Support/cc-director`)
  under `instances/*/config/outlook/tokens/`, and fail naming the fix when there is none or more than one. Two
  corrections to the wording:
  - the file is named `email_devthrottle_com_msal.json`, not `email@devthrottle.com_msal.json`: cc-outlook turns
    "@" and "." into "_" (`get_msal_token_path` in `tools/cc-outlook/src/auth.py`);
  - on Linux and macOS the home must come from the account record, not HOME. On Windows there is no account record in
    Python's standard library, but `LOCALAPPDATA` is not a `CC_` variable and reaches the check; this check's Windows
    branch uses it and was never run.
- **What it means for the real screen.py in production.** The owner's live Director on devlinux runs with
  `HOME=/home/<user>` (the user's real home), which is also what the account record says, so in production both lookups would find the same
  file; the account record is what makes the check independent of how the Director was started. One more thing seen
  on devlinux: a second, separate Director runs inside a container there with `HOME=/config` and an account record
  that also says `/config`. It is not the owner's Director for this mission and was not touched; if screen.py ever runs
  on a Director like that, the account-record lookup would search `/config/.local/share/cc-director`, and whether the
  saved sign-in is there depends on what that container mounts.
- The check's missing-sign-in failure ("email@devthrottle.com is not signed in on this machine (looked under ...):
  run cc-outlook -a devthrottle auth") is written in the check but was never triggered, because the owner had already
  signed in when the rig ran. Not proven.

## How it was run

- **A throwaway Gateway** built from this tree (`dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj`), on a
  free port of 127.0.0.1 (46121 in the main run), with an empty storage root of its own under `/tmp/mail-desk-p0-a-rig`,
  Tailscale off, authentication on, factory agents on in that root's own `config/config.json`. A person (a browser
  device row) and the Director (a workstation device row), each with its own key, as the phase 5b rig did. Every `CC_`
  variable of the shell was removed from its environment; it never talked to gateway.devthrottle.com.
- **devlinux's real Director**: the installed binary `~/.local/share/cc-director/app/cc-director`, version 2.22.0
  (its own registration says so), started by the rig as a separate process with `CC_DIRECTOR_ROOT=<rig>/director-root`,
  `HOME=<rig>/home`, auto-update off, Tailscale off, every other `CC_` variable removed, pointed at the throwaway
  Gateway with the workstation key. It was stopped by its lifecycle request file
  (`<rig>/director-root/config/lifecycle-signals/cc-director-shutdown-<its id>.request`) and exited with code 0 both
  times; nothing was killed. The owner's Director was never touched.
- **The definition** (`definition/`): factory `qa-mail-sign-in`, a boss and one seat `signin` whose start runs
  `checks/sign_in_probe.py` every two minutes. Saved and published with `cc-devthrottle` as the person. The check
  always prints `{"count": 0}`, so no session was ever started (every run's `SessionId` is empty).
- **The check** (`definition/checks/sign_in_probe.py`) uses cc-outlook's client id and authority
  (`https://login.microsoftonline.com/common`), calls only `acquire_token_silent` (there is no device-code or
  interactive sign-in anywhere in it), makes one GET to Graph, and writes the cache back the way cc-outlook's
  `MSALTokenBackend._save_cache` does: only when the state changed, the same path, then `chmod 0600`. Its evidence went
  into its memory file (`CC_CHECK_STATE_FILE`); the rig read it back from the throwaway Gateway's
  `factory_check_state` rows (three rows, one per run, each holding the runs before it, which is also how run 2 and 3
  knew their run number).
- **Three runs** in the main transcript, so runs 2 and 3 read the cache that runs 1 and 2 wrote back
  (`cacheBefore` of each run equals `cacheAfter` of the one before), silently.

Run it again (from the repository root, after building the Gateway; the mailbox must be signed in):

```
export PATH=$HOME/.local/bin:$HOME/.local/share/node/bin:$HOME/.dotnet:$PATH
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
uv run --script docs/qa/mail-desk/phase-0/a-sign-in/rig/run_proof.py docs/qa/mail-desk/phase-0/a-sign-in --runs 3
uv run --script docs/qa/mail-desk/phase-0/a-sign-in/rig/run_proof.py docs/qa/mail-desk/phase-0/a-sign-in --runs 1 --live-path
```

## What is in this folder

- `definition/` - the published definition, byte for byte except `machine:`, which the rig sets to this machine's name.
- `rig/run_proof.py` - the rig. It starts the Gateway and the Director, publishes, waits for the runs, reads the
  evidence back, and stops everything it started, pass or fail.
- `transcript.txt` - the main run (three checks). `transcript-live-path.txt` - the uv run (one check).

In the transcripts the rig folder is `<rig>`, the tree `<repo>`, the user's home `<home>` and the user's name
`<user>`. They hold no token, no cache content, no message and no address other than email@devthrottle.com. Two
things in `transcript.txt` were scrubbed by hand after the run, and the rig was then changed to do both itself (the
`--live-path` run was made with the changed rig): the user name in the Director's registration (`"User"`), and the
other cc-outlook account's row, which is now left out of the `cc-outlook accounts list` output; a scrubbing rule had
also overwritten the client id cell of the mailbox's own row, which was put back as `f575e3f4-8f62...`. The full list
of variable names the check saw is in the transcript; it includes some names inherited from the session that started
the rig (`AI_AGENT`, `CLAUDE_PID`, `SSH_CONNECTION` and others), which a Director started any other way would not
carry.

## What was NOT proven

- That the token lacks `Mail.Send` (point 3): it does not lack it.
- That screen.py can start on the owner's live Director as devlinux stands today: the evidence says uv is not on that
  Director's PATH (the finding above).
- The missing or expired sign-in failure: written, never triggered.
- Windows and macOS: only Linux ran. The Windows and macOS branches of the folder lookup were never run.
- The production Gateway and the owner's own Director: deliberately not used.
- No email was read beyond the Inbox's item count; nothing was moved, changed or sent.
