# Dev report viewer proof

Proves the Reports view on the BUILT Cockpit and phone app (issue #3010, phase 3 of #2936): a report cannot
reach the app around it, and a report goes from `cc-dev-reports open` to the owner's phone and back, on a real
Gateway, Director and session that this folder starts and stops itself. It prints PASS or FAIL per claim, exits
non-zero on any failure, and writes `evidence/<stage>-<date>[-<mutation>].json` plus screenshots.

Playwright is used as a library, deliberately: this is a repeatable scripted proof with assertions, the same as
the notes proof beside it. It loads Playwright from `PLAYWRIGHT_PATH` (see `../dev-report-notes-proof/README.md`).

## Run it

From this directory, in PowerShell:

```
.\rig.ps1 build      # Gateway (with the Cockpit and phone app built from this tree), launcher, Director (slot 5)
.\rig.ps1 up         # start them, isolated
.\rig.ps1 session    # open one Claude Code session on the rig Director
$env:PLAYWRIGHT_PATH = "$(npm root -g)\@playwright\cli\node_modules\playwright"
node run-proof.mjs                       # all stages; or --stage rig|frame|e2e
node run-proof.mjs --stage frame --mutation <name>   # a red run with one guard removed (mutations.mjs)
.\rig.ps1 down       # or reset, which also deletes the rig root
```

After merging a new viewer, rebuild the apps (`npm run build` in `apps/cockpit` and `apps/mobile`) and run
`.\rig.ps1 stage-shells` to put them where the running Gateway serves them; no restart is needed. If Gateway code
changed, `.\rig.ps1 build -Force` and bring the rig down and up again - claim R1 fails on any product change
since the running Gateway was built.

The `e2e` stage ends the fixture session (claim E8), so run `.\rig.ps1 session` again before running it twice.

## The rig, and why it cannot touch anything else

`rig.ps1` is a separate copy of `scripts\restart-qa-rig.ps1` with its own root, port and task names.

| | |
|---|---|
| Root | `%LOCALAPPDATA%\dev-report-proof-rig` (`CC_DIRECTOR_ROOT` for every rig process: its own database, tokens, logs, signal names) |
| Gateway | `<root>\gateway\devthrottle-gateway.exe --port 7931 --no-autostart`, `CC_GATEWAY_NO_TAILSCALE=1`, authentication on, started by the scheduled task `dev-report-proof-rig-gateway` |
| Launcher | `<root>\launcher\cc-launcher.exe --no-autostart`, scheduled task `dev-report-proof-rig-launcher` |
| Director | `<root>\app\cc-director.exe`, a slot 5 build from this tree, started by the rig launcher through the rig Gateway (so its parent is outside this agent's console) |
| Session | Claude Code on the haiku model in this worktree. Its first instruction runs `capture-session-env.py`, which writes the session's `CC_GATEWAY_URL`, `CC_GATEWAY_SESSION_KEY` and `CC_SESSION_ID` to `<root>\session.json`, so the proof runs `cc-dev-reports` AS that session. The same instruction tells it to run nothing for later messages: the prompt the Gateway composes invites the agent to reply and republish, and the proof does those steps itself. |
| Recording server | `run-proof.mjs` serves `fixtures/evil.html` and records every request on `127.0.0.1:7941` - another origin from the app's `127.0.0.1:7931`. |
| Owner sign-in | The rig Gateway's machine token stands in for a device key (the owner routes take either): it is put in the app's account store, and in the Gateway cookie the Cockpit's server-side gate reads. |

`down` stops the Director with its named shutdown signal, the launcher with its named signal, and the Gateway
with an authenticated `POST /shutdown`; it never force-kills, unregisters both tasks, and lists anything still
running from the root by image path.

## Fixtures

| File | What it is |
|---|---|
| `report-v1.html` | A well-formed report: a table with row and column headers, one question with a recommendation and a comment box, and enough detail to scroll. |
| `report-v2.html` | The same report republished: new summary, the failures cell fixed, `Report version 2`. |
| `hostile-published.html` | PASSES the shape check and attacks with what it lets through: a report `<base>` and a looser report policy, stylesheet, font, background, attribute-selector and focus beacons, images, `srcset`, SVG image, media, `<object>`, `<embed>`, nested frames (one on another origin, one `srcdoc` with a script), a form, `javascript:` and `data:` links, `_top` and `_blank` links, a plain link to another origin, and a fake notes tray with a fake Send, crafted as though a script had drawn it. |
| `hostile-refresh.html` | PASSES the shape check and takes the frame to another origin by itself with a meta refresh. |
| `hostile-direct.html` | REFUSED by the shape check (scripts, a guessed nonce, an external script, an inline handler, token snooping, forged ready and send). The proof hands it to the host anyway, by answering the app's own request for a carrier report's bytes, so what stops it is the host's policy and token and not the shape check. |
| `evil.html` | The other-origin page the frame is taken to. Its scripts run; it tries to read the app, pass as the note-taking script over a port and on the window, catch anything the host pushes, and navigate the app window, and it reports every result to the recording server. |

## The claims

**Stage rig** (needs no viewer):

- **R1** - the Gateway answering is the one built from this tree: its reported version names a commit that is an
  ancestor of this worktree's HEAD with no product file changed since, and both web shells name the same build.
- **R2** - the Gateway, launcher and Director run from the rig root, and the rig Gateway sees only the rig
  Director and its session.
- **R3** - `cc-dev-reports open` publishes as the session; the two hostile reports the check lets through publish,
  and the scripted one is refused.
- **R4** - an owner send is delivered, the prompt the Gateway composed - read from its own database
  (`gateway.db`, table `session_turns`, the user turn that follows) - names the row, the column and the chosen
  option, and a `cc-dev-reports reply` comes back verbatim on the owner route.
- **R5** - the built phone app and Cockpit load signed in against the rig; the recording server is another origin.

**Stage frame** (Cockpit at 1400 by 900 and phone at 390 by 844, each):

- **F1** - the report is in `<iframe sandbox="allow-scripts">`, has an opaque origin, and the host cannot read it.
- **F2** - the host's policy is the first element of the frame's head, and none of the direct report's scripts,
  handler, guessed nonce, external script or token snooping runs.
- **F3** - code in the frame cannot read the app's storage, cookies or page, and has no storage or cookies of its own.
- **F4a** - the direct report's forged ready (with a port) and forged sends (on the window and over its own port)
  make the app post nothing to the Gateway.
- **F4b** - the fake Send, the `javascript:` link, the form, and a focus and a pick that a stylesheet watches reach
  nothing, leave the frame on the report, and make the app post nothing.
- **F5** - nothing the published hostile report loads by itself reaches another origin.
- **F6** - the report cannot open a popup or navigate the app window.
- **F7a** - a `data:` link takes the frame away; the host drops the page (the viewer's `data-connected` goes
  false), nothing reaches another origin, and the app posts nothing.
- **F7** - after a plain link takes the frame to another origin, that page's scripts run but the host drops it,
  it gets no message from the host, its forged ready and sends are refused, and the app posts nothing.
- **F8** - the same after a meta refresh.

**Stage e2e**:

- **E1-E6, phone at 390 by 844** - publish; open; note a table cell; answer the question; Send; the Gateway's
  status words shown verbatim; the composed prompt read from the database names the row, the column and the
  option; `cc-dev-reports reply` appears; republish reloads the page in place keeping the scroll position and a
  half-typed note.
- **E7, Cockpit at 1400 by 900** - the same report shows version 2, the Gateway's labels and the reply.
- **E8** - the session is ended; Send then shows the Gateway's refusal sentence and the note stays queued.
- **E9** - a note written in a second browser (the Cockpit, after the phone) either reaches the Gateway or stays
  queued; it never leaves the queue or reads as delivered while the Gateway does not hold it.

## Red runs

`mutations.mjs` holds one guard removal per rule of CONTRACT section 4, applied to the served bundle in the
browser only (nothing on disk changes, and service workers are blocked so a cached bundle cannot skip it). Each
names the claims it must turn red, and the run prints `RED AS EXPECTED` or `NOT RED` for each claim in each app,
then `RED CONFIRMED` only when every one went red AND the removal matched in both bundles.

| Removal | Rule | Must go red |
|---|---|---|
| `sandbox-same-origin` | 1 | F1, F3 |
| `no-policy` | 3 | F2, F5 |
| `no-token-check` | 4 | F7, F8 |
| `no-policy-no-token-check` | 3 and 4 | F2, F4a |
| `load-keeps-port` | 6 | F7a, F7, F8 |

Rule 4 alone cannot turn F4a red, because the policy already stops the direct report's script; that is why it is
also run together with rule 3.

The rig's machine token (standing in for the owner's device key) and the session key are replaced with
`<rig token>` and `<session key>` in every printed line and evidence file: a red run that reads the app's storage
would otherwise print them.

## Known limit of the test browser

The proof runs the full Chromium in its new headless mode, not Playwright's default headless shell. The
headless shell's renderer crashed on the built phone app every time a session on the rig was working (three
tries in three), before any Reports code existed; the same page in a headed browser and in the new headless mode
did not crash. This is recorded as a property of the test browser, not proven to be one: no real phone was tried.
