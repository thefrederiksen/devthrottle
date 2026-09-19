---
name: dev-throttle
description: DevThrottle - "Mission Control for Claude Code". A desktop app (binary cc-director.exe) that runs and supervises multiple Claude Code sessions and ships cc-* CLI tools on PATH. Agents drive the fleet through the cc-devthrottle command, not by calling the Director over HTTP. Triggers on "/dev-throttle", "/devthrottle", "/cc-director", "what cc tools", "list tools", "available tools", "devthrottle api", "session manager", "mission control".
---
# Dev Throttle

DevThrottle is a desktop application positioned as "Mission Control for Claude Code" - one place to run, observe, and orchestrate multiple Claude Code sessions side by side. It also installs a suite of `cc-*` command-line tools onto your PATH.

This skill orients a Claude Code session to what is available after DevThrottle is installed. It is written for the installed product, not for building from source.

Naming note: DevThrottle is the product/brand. The application binary installed and launched on a machine is `cc-director.exe`, and the bundled command-line tools keep their `cc-*` names. So you will see the product called DevThrottle while the concrete on-disk app, process, and tools still carry `cc-` names.

## What DevThrottle is, in three parts

1. **Desktop app (`cc-director.exe`)** - Windows (primary), with experimental Mac/Linux support. Runs and supervises multiple Claude Code sessions, one per repo, with real-time activity tracking, terminal buffers, and voice input.
2. **The Gateway** - one machine on the fleet runs a Gateway process that every machine's Director connects OUT to over a persistent two-way stream (internally, "the tunnel"). The Gateway is the single front door for the web Cockpit and the phone app, and the coordination point the whole fleet talks through. Individual Directors are never dialled over the network - they dial out to the Gateway, and everything travels down that stream.
3. **`cc-*` tool suite** - CLI tools installed on PATH when DevThrottle is set up. Each tool supports `--help` for its full command syntax. The fleet command is `cc-devthrottle`.

## The cc-* tools (installed on PATH)

All tools are on PATH after install. For exact flags and examples, run any tool with `--help`.

### Documents
`cc-pdf`, `cc-html`, `cc-word`, `cc-excel`, `cc-powerpoint` - convert markdown to PDF / HTML / Word / Excel / PowerPoint with themed templates (boardroom, paper, terminal, blueprint, thesis, spark, obsidian).

### Email
`cc-gmail`, `cc-outlook` - read, send, and search Gmail and Outlook from the CLI.

### Web
`cc-crawl4ai` (clean markdown extraction for RAG), `cc-websiteaudit`, `cc-brandingrecommendations`.

### Browsers you are signed in to
`cc-devthrottle browser` owns this machine's drivable browser profiles - a real Chrome the user signed into ONCE, which any agent can then drive as the user. `browser-harness` drives the page.

**Never launch a browser yourself** - not `chrome.exe` with a debugging switch, not a hand-written CDP state file, and not `cc-playwright` (retired). Those all succeed into a browser signed in as NOBODY, which surfaces as a login page and tempts the next agent into typing the user's password.

```bash
cc-devthrottle browser list --json                      # what exists, and whose account each one is
cc-devthrottle browser start centerconsulting           # launch it if down
eval "$(cc-devthrottle browser attach 'centerconsulting')"   # BU_NAME / BU_CDP_URL for the harness
cc-devthrottle browser stop centerconsulting            # done; the login is kept
```

Pick the profile by its `account` field, never by name or port. **The browsers skill is the full reference** - read it before driving one.

### Desktop automation
`cc-click` (Windows UI: click, type, screenshot, OCR), `cc-trisight` (3-tier UI element detection: UIA + OCR + pixel), `cc-computer` (AI desktop agent with screenshot-in-the-loop).

### Media
`cc-image`, `cc-voice` (text-to-speech), `cc-whisper` (audio transcription / translation), `cc-video`, `cc-transcribe`, `cc-photos`, `cc-youtube-info`.

### Data and utilities
`cc-vault` (contacts, tasks, goals, docs, RAG), `cc-hardware`, `cc-docgen` (C4 architecture diagrams from YAML).

### DevThrottle fleet, schedules, and setup
`cc-devthrottle` is the unified command for fleet/session operations, inter-session messages, settings, Gateway schedules, and setup:

```
cc-devthrottle actions --json
cc-devthrottle session list
cc-devthrottle message send <target|all> "message"
cc-devthrottle settings get screenshots.source_directory
cc-devthrottle schedule list
cc-devthrottle setup status
```

`cc-ship` takes a finished change from THIS session all the way to merged on origin/main: a review by
a session running a different agent, a check that the change works in the running product, a risk
reading, then the pull request and the merge. Every answer carries the run's one state and the next
step. `cc-ship --help` is the whole command list.

`cc-dev-reports` publishes one HTML file as this session's report for the owner - it lands on his
phone or in the Cockpit, inside the report rather than on a list - and `cc-dev-reports reply` answers
him in it.

A handful of tools are registered but not yet built (`cc-twitter`, `cc-facebook`, `cc-youtube`, `cc-posthog`). If a tool isn't on PATH, it likely isn't built yet.

## How an agent talks to the fleet: use `cc-devthrottle`, not raw HTTP

The way to list, message, create, rename, and close sessions from inside a session is the
`cc-devthrottle` command. It already knows how to reach the fleet: every session is launched with
`CC_GATEWAY_URL` (the Gateway's address), `CC_GATEWAY_SESSION_KEY` (this session's own credential
for it), `CC_DIRECTOR_ID` (which Director the session belongs to) and `CC_SESSION_ID` (its own id)
in the environment, and `cc-devthrottle` reads them automatically. There is nothing to configure.

Do NOT try to drive anything by dialling the Director. THE DIRECTOR HAS NO HTTP SURFACE AT ALL -
the remove-the-network-port mission deleted its listener, so there is no port, no loopback floor,
and no route that could answer. Everything an agent does goes through the Gateway: the command line
presents the session key, the Gateway rules on it, and commands travel the Director's own outbound
tunnel to the machine that owns the session. Stopping a Director is a named signal
(`Local\cc-director-shutdown-<directorId>`), because it has to work when nothing is listening on a
socket - which is the state an update needs it in. See CLAUDE.md rule 0b.

The session key is least-privilege: it is bound to this session, limited to the agent surface, and
revoked when the session ends. It cannot enrol devices or touch account identity. No Gateway
connection means no agent tooling - that is the designed trade, and the tools say so in plain words
when it happens.

For the full fleet-messaging and session-spawning command reference, see the **fleet-comms** skill.

## Creating a session correctly (always name it)

Create sessions with `cc-devthrottle session spawn`, not a raw HTTP call. Whichever way you create a
session, get these right every time - an underspecified session is unnamed, blocked on permission
prompts, or on the wrong model.

1. **Always give it a meaningful name.** A session is how a human finds work in Mission Control. On a
   fleet where many sessions run in the SAME repo, an unnamed session falls back to the bare repo
   folder name (e.g. "devthrottle") and is indistinguishable from every other session in that repo.
   Pass `--name` (an explicit display name) or `--purpose` (what the session is FOR, e.g.
   `implement #806`); the name must describe the work and must not be blank or equal to the bare repo
   folder name. `spawn` warns when you give neither.
2. **Carry the normal permission preset and model in `--args`.** The desktop New Session dialog uses
   the "Automatic (skip permissions)" preset. When you pass no `--args`, `spawn` applies the same
   default. When you DO pass `--args`, you override it entirely, so include the whole line, e.g.
   `--args "--dangerously-skip-permissions --model opus[1m]"` - otherwise the session can stall at a
   "Do you want to proceed?" prompt or run on the wrong model/window.
3. **Use `--prompt`** for the session's first task (dispatched once the agent is ready). For a long
   instruction, write it to a file and make `--prompt` a short "read and follow <path>" pointer.
4. **Know what will close it before you open it.** A session is a commitment as much as a resource:
   whoever spawns it owns driving it to completion. Decide at spawn time where its output has to end
   up - merged into YOUR worktree if it shares yours, or on `origin/main` if it has its own - and put
   that in the brief. A session whose exit conditions were never stated does not acquire them later;
   it just stays open. See **Closing a session** below, which applies to the sessions you started as
   much as to yourself.

```
# Create a properly-named, autonomous-ready session
cc-devthrottle session spawn D:\Repos\myrepo \
  --controlled-by self \
  --name "myrepo - fix auth bug #123" \
  --args "--dangerously-skip-permissions --model opus[1m]" \
  --prompt "Fix the bug in auth.js"
```

`--controlled-by self` says YOU own it, so it reports back to you and stays out of the user's
queue. Say `--standalone --why "<reason>"` instead when the USER owns it. From inside a session
one of the two is required - the spawn is refused until you say which.

The Director names the session at birth and returns the final id and name. See the **fleet-comms**
skill for the full flag set (`--agent`, `--role`, `--mission`, `--machine`, `--controlled-by`, and the
display-name convention).

### Opening a session on ONE particular Director

`--machine` names a computer, and one computer runs several named Director instances - so it lands on
whichever the Gateway lists first. When you were told to use a specific Director, name it:

```
cc-devthrottle director list          # names, machines, and the Director id to use
cc-devthrottle session spawn D:\Repos\myrepo \
  --controlled-by self \
  --director 6f0a2b41-1c33-4f9e-9a10-2b7d5e8c1234 \
  --name "myrepo - fix auth bug #123"
```

`--director` takes the Director id or its display name and needs no `--machine` - a Director
identifies the computer it runs on. Prefer the id: it survives a rename and cannot collide with a
second Director sharing a name. A person handing you a Director will usually paste its toolbar Copy
output, which is three lines - `Director:`, `Director ID:`, `Machine:`. Take the id from there and
use it verbatim.

An unregistered name fails loudly, and a name matching two Directors fails listing both. Neither
falls back to another Director - if you get one of those errors, run `director list` and pick, rather
than dropping the flag and spawning wherever.

## Closing a session - yours and the ones you started

A session can close itself. When an agent has finished its work and nothing is waiting on the
user, it should reap its own session rather than leaving an idle entry in Mission Control.

**Use `cc-devthrottle session done`.** It flags THIS session (via `CC_SESSION_ID`) for graceful
asynchronous removal: the owning Director's deletion reaper removes it on its next sweep, once a short
grace has passed and the session is no longer working. It does NOT kill the caller's process
mid-request, so you can flag yourself and then finish your turn normally.

```
# Close THIS session gracefully (run at the very end of your turn, on an unattended run)
cc-devthrottle session done
```

To reap a DIFFERENT session, pass its id or name: `cc-devthrottle session done <target>`.

Do not self-close while something still needs the user (a pending decision, an approval, an
unanswered question). Reap only when the queue is truly empty.

**Closing the sessions YOU spawned is your job, not theirs.** A child session that has finished its
work and gone idle is not done - it is unfinished work sitting where nobody is looking. As its parent
you drive it to all four conditions:

1. **reviewed** by a session OTHER than the one that wrote it;
2. **output safe** - merged into your worktree if it shares yours, otherwise on `origin/main`,
   because a worktree of its own will be deleted and anything left in it dies with it;
3. **worktree removed**, if it had its own;
4. **session reaped**, with `cc-devthrottle session done <target>`.

**The failure this prevents.** A definition of "done" that means only "the pull request merged"
leaves a branch, a worktree and a live session behind EVERY time. Those accumulate faster than
anything removes them, and the cost stays invisible until somebody counts. One coordinator running
this fleet finished many tasks that way and left 27 worktrees, 7 open pull requests and branches four
days old - not through any single bad decision, but because nothing in the definition of done
required cleanup.

**Note that `session done` reaches a session that is not answering.** It flags the target through the
Director and does not depend on fleet messaging, which can fail. If a session you started has stopped
responding, you can still close it.

## Who the user is

Every session is started for one signed-in DevThrottle user. The session-start preamble names that
user - both their email and, when they set one, their chosen nickname. Treat that named person as the
user of this session: unless they explicitly say otherwise, "me", "my account", and "email me" mean
that user. Do NOT guess who the user is from usage patterns or by searching the database for a name -
use the identity the preamble gave you. If no user is named (nobody signed in), do not invent one.

## When this skill is the right thing to consult

- "What cc-* tools do I have for X?" - look in the tool list above; run the tool with `--help` for syntax.
- "How do I list / message / create / close sessions?" - use `cc-devthrottle` (details in the fleet-comms skill).
- "I need a page that requires the user's login" - do NOT launch a browser. `cc-devthrottle browser
  list --json`, then the **browsers** skill.
- "Is the app running?" - the Director binds no port, so there is nothing to curl. Read the
  instance registration the running process writes
  (`%LOCALAPPDATA%\cc-director\instances\<slug>\config\director\instances\<directorId>.json`,
  whose `Pid` names the process) or ask the fleet: `cc-devthrottle session list` shows every
  connected Director's sessions.

## What this skill does NOT do

- It does not replace `<tool> --help`, which has the authoritative flags and examples for each tool.
- It does not replace the **fleet-comms** skill, which is the full reference for `cc-devthrottle`.
- It does not replace the **browsers** skill, which is the full reference for driving a signed-in browser.


**Skill Version:** 6.1 (drivable browser profiles)
**Last Updated:** 2026-09-06
**Changes in 6.1:** Added the drivable browser profiles. `cc-devthrottle browser` registers, starts, attaches and stops a real browser the user signed into once, and `browser-harness` drives the page. This is written down because the absence of it caused a live failure on 2026-09-06: with a signed-in `centerconsulting` profile registered and stopped, an agent that never ran `browser list` hand-launched Chrome against a retired `cc-playwright` folder and handed the user a LOGIN page for the site it was asked to work in. Hand-rolling a browser does not fail loudly, it succeeds into the wrong session - so the rule is that the Director owns the profiles and an agent never launches one. Full reference: the **browsers** skill.

**Changes in 6.0:** The remove-the-network-port mission deleted the Director's listener entirely.
There is no loopback floor, no `/healthz`, no `/fleet/*` relay, no `/reconnect`, no local settings
routes, and no `CC_DIRECTOR_API` or `CC_DIRECTOR_TOKEN` in any session's environment. Agents reach
the fleet through the Gateway with the session's own key (`CC_GATEWAY_URL` +
`CC_GATEWAY_SESSION_KEY`, stamped at launch); `CC_DIRECTOR_ID` names which Director a session
belongs to. The 5.2 route-probing diagnostic is obsolete - there are no routes to probe.
**Changes in 5.0:** Rewritten for the tunnel-only fleet (release v1.1.0, the Gateway Cleanup cut). The Director no longer exposes a general HTTP Control API - it binds a small loopback-only floor, and the Gateway is the single front door the fleet connects out to over the tunnel. Removed the deleted session-driving REST endpoints (list/details/buffer/prompt/interrupt/turns/handover/chat/voice/create/request-deletion/delete) and the curl-the-Director examples. Session create / close / rename / message now documented through `cc-devthrottle` (spawn / done / rename / message), which is the agent-facing surface. Dropped retired tools `cc-browser`, `cc-reddit`, and `cc-comm-queue` from the tool list.

**Changes in 5.2 (SUPERSEDED by 6.0 - do not act on it):** it described a diagnostic that depended on the Director's loopback floor, which no longer exists. The recipe is deliberately not restated here: prose that repeats an instruction is what the next agent acts on, whatever the sentence around it says. Kept only so a reader who has seen the old text knows it was withdrawn rather than lost.
**Changes in 4.3:** Added "Who the user is" - the session-start preamble names the signed-in DevThrottle user (email + nickname); "me / my account / email me" means that user unless they say otherwise, and identity must not be guessed from usage or the database (issue #1357).
