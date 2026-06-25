# Fleet messaging - team guide

Talk to other sessions across the fleet from inside a session. These tools are installed on PATH
on every machine with a Director, and an agent learns them from the shipped `fleet-comms` skill.

This is the practical quick reference. For the design and how the relay works under the hood, see
[SessionIntercommunication.md](SessionIntercommunication.md).

---

## What it is

A session (a running Claude Code or other agent in a terminal) can list, message, and question other
sessions running anywhere in the fleet - any machine whose Director is attached to the same Gateway.
You do this with a handful of `cc-*` command-line tools. You never need the Gateway address or a
token: your own Director relays for you, so the fleet token never enters the agent process.

Every session is launched with `CC_DIRECTOR_API` (its own Director) and `CC_SESSION_ID` (its own id);
the tools read these automatically.

---

## The tools at a glance

| Tool | What it does |
|------|--------------|
| `cc-sessions` | List every session in the fleet (id, name, machine, repo, status). |
| `cc-whoami` | Show your own id, name, machine, and repo. |
| `cc-send <id\|name> "msg"` | Send a one-way message to a session. `cc-send all "msg"` broadcasts. |
| `cc-ask <id\|name> "q"` | Ask a session a question and wait for its answer. |
| `cc-spawn <repo>` | Open a new session on the local Director and print its id. |
| `cc-fleet-selftest` | One-command health check of the whole loop (spawn -> list -> send -> ask -> teardown). |

---

## Each tool

### cc-sessions - see who is running

```
cc-sessions
```
Lists the fleet. Your own session is marked `(you)`. Use it to find a target's id or name.

### cc-whoami - know yourself

```
cc-whoami
```
Prints your own short id, name, machine, and repo, plus a reminder of how to message others.

### cc-send - one-way message

```
cc-send 4c810000 "I finished the API layer - you can start the frontend."
cc-send docs "Please update the API page when you get a chance."
cc-send all "Heads up: I am about to merge to main in 5 minutes."
```
Address by short id prefix (preferred) or name. `all` broadcasts to every other session. The
recipient sees a framed message naming you and how to reply. An ambiguous prefix or name is refused
with the candidates - nothing is sent.

### cc-ask - ask and get the answer back

```
cc-ask 9b2f "What database schema is loaded in your repo?"
cc-ask docs "What is the title of the API page?" --timeout-ms 60000
```
Single target only. Waits for the target to finish its turn and prints its answer. A timeout prints a
clear message and exits non-zero; an unknown or unreachable target exits non-zero with a clear error.
Use this to query another session's already-loaded context instead of reloading it yourself.

### cc-spawn - open a session

```
cc-spawn D:\path\to\repo
cc-spawn D:\path\to\repo --agent ClaudeCode --prompt "Run the tests and report failures."
cc-spawn D:\path\to\repo --name "frontend" --agent RawCli --command cmd
```
Opens a session on your own Director and prints its short id and full GUID; it then appears in
`cc-sessions`, ready to `cc-send` / `cc-ask`. A non-existent repo path exits non-zero with a clear
error. Options: `--agent`, `--prompt`, `--name`, `--type`, `--command` / `--command-args` (for
`--agent RawCli`).

### cc-fleet-selftest - prove it works

```
cc-fleet-selftest
```
Spawns two throwaway sessions, lists them, sends to one, asks the other (a deterministic responder),
tears them down, and prints PASS/FAIL. Exits 0 when every check passes, non-zero otherwise, and
leaves no sessions behind. Run it as a post-deploy or "is messaging healthy on this box?" check.

---

## Addressing

- The stable handle is the session id; use a short unique prefix (e.g. `9b2f`). An ambiguous prefix
  is refused with the candidates.
- You can also address by name (the display name); it is resolved to an id, and an ambiguous name is
  refused.
- `cc-send all` is the only broadcast; `cc-ask` is always single-target.

## Typical workflows

- Hand off / notify: `cc-sessions` to find the target, then `cc-send <id> "..."`.
- Query another agent's context: `cc-ask <id> "What is X in your repo?"`.
- Delegate: `cc-spawn <repo> --prompt "..."`, then `cc-send` / `cc-ask` the new session.
- Health check: `cc-fleet-selftest`.

## How it works (one paragraph)

A session calls only its own Director (`CC_DIRECTOR_API`). For a local target the Director delivers
directly; for a remote target it relays through the Gateway using the fleet token it already holds.
Messages are delivered into the recipient as a framed prompt that names the sender. The fleet token
never reaches the agent. Full design: [SessionIntercommunication.md](SessionIntercommunication.md).

## Deployment

These tools ship in the Director's Python tool bundle and land on PATH
(`%LOCALAPPDATA%\cc-director\bin`) on every install and auto-update. The in-app tool doctor verifies
they are present, and the `fleet-comms` skill (installed to `~/.claude/skills/fleet-comms/`) teaches
agents the verbs. A guard test (`FleetToolsShipGuardTests`) prevents a tool from silently failing to
ship.

## Command-line reference

Full per-tool flags are in [cli-reference.md](cli-reference.md) under "Fleet messaging".
