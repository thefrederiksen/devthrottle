---
name: fleet-comms
description: Talk to other DevThrottle sessions across the fleet. Use when you want to list running sessions, rename this session, message another session, ask another session a question and get its answer, or open a new session - from inside a session. Triggers on "/fleet-comms", "message another session", "talk to another session", "ask another session", "rename this session", "rename session", "list sessions", "what sessions are running", "open a session", "spawn a session", "cc-devthrottle", "fleet messaging", "session intercommunication".
---

# Fleet communication between sessions

DevThrottle lets a session talk to other sessions running anywhere in the fleet, meaning any
machine whose Director is attached to the same Gateway. Use the single `cc-devthrottle` command.
You never need the Gateway URL or any token; your own Director relays for you.

Every session is launched with two environment values the command relies on:
`CC_DIRECTOR_API` is your own Director's address, and `CC_SESSION_ID` is your own id.
`cc-devthrottle` reads them automatically.

## Discover actions

Use this first when mapping a user task to a command.

```
cc-devthrottle actions --json
```

## Sessions

```
cc-devthrottle session list
cc-devthrottle session whoami
cc-devthrottle session rename "Dev Throttle Review"
cc-devthrottle session rename 9b2f "Frontend Review"
cc-devthrottle session spawn D:\path\to\repo --purpose "implement #799"
cc-devthrottle session spawn D:\path\to\repo --name "Frontend review"
cc-devthrottle session spawn D:\path\to\repo --purpose "run the test suite" --agent ClaudeCode --prompt "Run the tests and report failures."
cc-devthrottle session spawn D:\path\to\repo --name "frontend" --agent RawCli --command cmd
```

Always name your session. On this fleet many sessions run in the SAME checkout, so a session with
no name displays as the bare folder name and is impossible to tell apart. Lead with `--name`
(an explicit display name) or `--purpose` (a short description of what the session is FOR, e.g.
`implement #799`); spawn warns when you give neither. A blank name, or a name equal to the bare
repository folder name, is rejected - pass something meaningful or a purpose.

`session rename "name"` renames the current session using `CC_SESSION_ID`.
`session rename <target> "name"` renames another session selected by id prefix or exact name.

## Messages

```
cc-devthrottle message send 4c810000 "I finished the API layer - you can start the frontend."
cc-devthrottle message send docs "Please update the API page when you get a chance."
cc-devthrottle message send all "Heads up: I am about to merge to main in 5 minutes."
cc-devthrottle message ask 9b2f "What database schema is loaded in your repo?"
cc-devthrottle message ask docs "What is the title of the API page?" --timeout-ms 60000
```

`message send all` is the only broadcast form. `message ask` is always single-target and waits for
the target's answer.

## Health check

```
cc-devthrottle selftest
```

This spawns two throwaway local sessions, proves list/send/ask works, then tears them down.

## Related surfaces

The same binary also owns Gateway schedules and local setup diagnostics:

```
cc-devthrottle schedule list
cc-devthrottle setup status
```

## Rules

- Address a session by a short id prefix or by exact name.
- For a simple current-session rename, run `cc-devthrottle session rename "New Name"` directly.
- If a target is ambiguous, rerun with a longer id prefix.
- If a command says `CC_DIRECTOR_API` is missing, you are outside a DevThrottle-launched session.
- The command never exposes the Gateway token to the session.
