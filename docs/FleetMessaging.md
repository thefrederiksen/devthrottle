# Fleet Messaging

Fleet messaging lets one DevThrottle session list, rename, message, ask, and spawn sessions through
its own Director. The session never needs a Gateway URL or fleet token; `cc-devthrottle` talks to
`CC_DIRECTOR_API`, and the Director relays when needed.

## Canonical Command

Use one command for the fleet/session surface:

```bash
cc-devthrottle actions --json
cc-devthrottle session list
cc-devthrottle session whoami
cc-devthrottle session rename "Dev Throttle Review"
cc-devthrottle session rename 9b2f "Frontend Review"
cc-devthrottle message send 9b2f "Can you run the integration tests?"
cc-devthrottle message send all "Heads up: I am about to merge to main in 5 minutes."
cc-devthrottle message ask 9b2f "What database schema is loaded in your repo?"
cc-devthrottle session spawn D:\path\to\repo --prompt "Run the tests and report failures."
cc-devthrottle schedule list
cc-devthrottle setup status
cc-devthrottle selftest
```

## Commands

| Command | What it does |
|---------|--------------|
| `cc-devthrottle actions --json` | Lists agent-discoverable actions and exact command shapes. |
| `cc-devthrottle session list` | Lists every session in the fleet. |
| `cc-devthrottle session whoami` | Shows this session's id, name, machine, and repository. |
| `cc-devthrottle session rename "name"` | Renames the current session using `CC_SESSION_ID`. |
| `cc-devthrottle session rename <target> "name"` | Renames another session selected by id prefix or exact name. |
| `cc-devthrottle message send <target> "msg"` | Sends a one-way message to one session. |
| `cc-devthrottle message send all "msg"` | Broadcasts a one-way message to every other session. |
| `cc-devthrottle message ask <target> "question"` | Asks one session a question and waits for its answer. |
| `cc-devthrottle session spawn <repo>` | Opens a new session on the local Director. |
| `cc-devthrottle schedule list` | Lists Gateway schedules. |
| `cc-devthrottle setup status` | Shows local setup status. |
| `cc-devthrottle selftest` | Runs an end-to-end list/send/ask smoke test with throwaway sessions. |

## Targeting

A target can be a full session id, a unique id prefix, or an exact session name. If a target is
ambiguous, use a longer id prefix from `cc-devthrottle session list`.

## Typical Workflows

Rename this session:

```bash
cc-devthrottle session rename "Dev Throttle Review"
```

Hand off work:

```bash
cc-devthrottle session list
cc-devthrottle message send 9b2f "The API branch is ready for frontend wiring."
```

Ask another session for context:

```bash
cc-devthrottle message ask docs "What is the current title of the API page?" --timeout-ms 60000
```

Delegate to a fresh session:

```bash
cc-devthrottle session spawn D:\ReposFred\devthrottle --name "qa" --prompt "Run the focused QA checks."
```

## Notes

- `message ask` is single-target only.
- `message send all` is the broadcast form.
- The command exits non-zero with a clear error when `CC_DIRECTOR_API` is missing, a target is
  ambiguous, or a target cannot be found.
- The Gateway token never enters the agent process.
