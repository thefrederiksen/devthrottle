# Control API

Every running Director hosts a REST Control API on loopback (`127.0.0.1`, port range 7879-7898, one stable port per Director). It is the programmatic surface for everything the desktop UI can do: sessions, prompts, terminal buffers, git, handovers, repositories, settings, and more. The Cockpit, the phone clients, and agents running inside sessions all drive Directors through this API.

## A session can find itself

The Director injects three environment variables into every session it spawns:

| Variable | Meaning |
|----------|---------|
| `CC_SESSION_ID` | The Director's session GUID for this session |
| `CC_DIRECTOR_API` | Base URL of the owning Director's Control API, e.g. `http://127.0.0.1:7880` |
| `CC_DIRECTOR_ID` | The owning Director's stable id |

An agent inside a session can therefore look itself up with one call:

```
GET %CC_DIRECTOR_API%/sessions/%CC_SESSION_ID%
```

and from there read its own terminal buffer, list its Director's other sessions, create handovers, or manage the repository list. Sessions spawned before the Control API finished starting (rare; the API starts at boot) only have `CC_SESSION_ID`.

## Key endpoint groups

This is the working subset most clients need. All bodies are JSON.

### Sessions

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /sessions | List sessions (`?includeExited=true` to include dead ones) |
| GET | /sessions/{sid} | One session: name, repo, activity state, idle time |
| POST | /sessions | Create a session (`repoPath`, `agent`, `args`, `resumeSessionId`, `prePrompt`, `wingmanEnabled`) |
| DELETE | /sessions/{sid} | Kill a session |
| PATCH | /sessions/{sid} | Rename a session (`name`) |
| POST | /sessions/{sid}/prompt | Send a prompt into the session |
| POST | /sessions/{sid}/interrupt | Send Ctrl+C |
| GET | /sessions/{sid}/buffer | Read the terminal buffer (`?lines=`, `?raw=true`) |
| GET | /sessions/{sid}/git | Git status for the session's repo (stage/unstage/discard/commit via POST sub-routes) |

### Handovers

Handover documents live in the Director's vault (`vault/handovers/*.md`, YAML frontmatter + markdown body) and let one session pass its context to a later one.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /handovers | List all handovers (`?repo=<path>` filters by referenced repository) |
| GET | /handovers/content?path= | Full markdown of one handover |
| POST | /handovers | Create a standalone handover document: `{ "title", "content", "repoPaths": [], "sessionName" }` |
| DELETE | /handovers?path= | Delete a handover document |
| POST | /handover | Dispatch: summarize a source session and send the context into a target session |
| GET | /sessions/{sid}/handover-context | Preview the context text a dispatch would send |

Paths passed to content/delete must live inside the handover folder; anything else is rejected.

### Repositories

The Director keeps a registry of repositories you have worked in (this backs the New Session picker).

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /repos | Registered repos: name, path, last used |
| POST | /repos | Register a repo explicitly: `{ "path", "name?" }` (400 if the directory does not exist) |
| PATCH | /repos | Rename a repo: `{ "path", "name" }` |
| DELETE | /repos?path= | Remove a repo from the registry |
| GET | /repos/overview | Everything a repositories page needs, per repo: live session count and names, resumable Claude session count, history count, last session date and summary, git branch, handover count, whether the path still exists |
| GET | /claude-sessions | Resumable Claude Code sessions (`?repo=<path>` filters to one repo) |

### Other groups

Settings (GET/PUT /settings), tools catalog and invocation (/tools, POST /tools/run - run a catalog cc-* tool with args and stream its output as NDJSON), comm dispatch (POST /dispatch - send ONE already-approved communication-queue item by id; unapproved items are refused with 409 and nothing sends), scheduler (/scheduler), workspaces (/workspaces, /history), screenshots (/screenshots), dictation and TTS (/dictate, /tts), wingman (/sessions/{sid}/wingman/...), and a WebSocket terminal stream (/sessions/{sid}/stream). Explore `GET /healthz` for the Director id and version.

## Finding a Director's port

From inside a session, use `CC_DIRECTOR_API` - no discovery needed. From outside, each Director writes `instances/{directorId}.json` under `%LOCALAPPDATA%\cc-director\config\director\` with its current port, and registers with the Gateway when one is configured.
