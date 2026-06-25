---
name: fleet-comms
description: Talk to other CC Director sessions across the fleet. Use when you want to list running sessions, message another session, ask another session a question and get its answer, or open a new session - from inside a session. Triggers on "/fleet-comms", "message another session", "talk to another session", "ask another session", "list sessions", "what sessions are running", "open a session", "spawn a session", "cc-send", "cc-ask", "cc-spawn", "cc-sessions", "fleet messaging", "session intercommunication".
---

# Fleet communication between sessions

CC Director lets a session talk to other sessions running anywhere in the fleet (any machine whose
Director is attached to the same Gateway). You do this with a small set of `cc-*` command-line tools
that are already on your PATH inside a session. You never need the Gateway URL or any token - your own
Director relays for you.

Every session is launched with two environment values these tools rely on: `CC_DIRECTOR_API` (your
own Director's address) and `CC_SESSION_ID` (your own id). The tools read them automatically.

## The tools

### See who is running - `cc-sessions`
Lists every session in the fleet: a short id, name, machine, repository, and status. Your own session
is marked `(you)`. This is how you discover who exists and find the id or name to address.

```
cc-sessions
```

### Know yourself - `cc-whoami`
Prints your own short id, name, machine, and repository, plus a reminder of how to message others.

```
cc-whoami
```

### Send a one-way message - `cc-send`
Send a message to one session (by short id prefix or name), or to every other session with `all`.
The recipient sees a framed message naming you and how to reply.

```
cc-send 4c810000 "I finished the API layer - you can start the frontend."
cc-send docs "Please update the API page when you get a chance."
cc-send all "Heads up: I am about to merge to main in 5 minutes."
```

An ambiguous id prefix or name is refused with the list of candidates (nothing is sent).

### Ask a question and get the answer back - `cc-ask`
Ask one session a question and wait for its answer (the round-trip `cc-send` cannot do). Use this to
query another session's already-loaded context, for example "what is the database schema in your
repo?". Single target only.

```
cc-ask 9b2f "What database schema is loaded in your repo?"
cc-ask docs "What is the title of the API page?" --timeout-ms 60000
```

If the target does not answer within the timeout, `cc-ask` prints a clear timeout and exits non-zero;
an unknown or unreachable target exits non-zero with a clear error.

### Open a new session - `cc-spawn`
Open a session on your own Director and print its id, so you can immediately message or ask it.

```
cc-spawn D:\path\to\repo
cc-spawn D:\path\to\repo --agent ClaudeCode --prompt "Run the tests and report failures."
cc-spawn D:\path\to\repo --name "frontend" --agent RawCli --command cmd
```

### Prove it works - `cc-fleet-selftest`
A one-command health check: it spawns two throwaway sessions, lists/sends/asks between them, tears
them down, and prints PASS/FAIL. Run it if you suspect fleet messaging is not working on a machine.

```
cc-fleet-selftest
```

## How addressing works
- The stable handle is the session's id; you can use a short unique prefix of it (for example
  `9b2f`). An ambiguous prefix is refused with the candidates.
- You can also address by name (the session's display name), which is resolved to an id; an
  ambiguous name is refused.
- `cc-send all` is the only broadcast; `cc-ask` is always single-target.

## A typical flow
1. `cc-sessions` to see who is running and get a target id or name.
2. `cc-send <id> "..."` to hand off or notify, or `cc-ask <id> "..."` to query and get an answer.
3. `cc-spawn <repo>` if you need a fresh session to delegate work to, then `cc-send` / `cc-ask` it.

## Notes
- These tools only message sessions; they never expose the Gateway token to your session - the
  Director relays on your behalf.
- A message is delivered into the recipient as a prompt, framed with your identity, so the recipient
  knows who sent it and how to reply.
