# Gateway Cron API - reference for a Claude Code session

> Proposed surface from issue #479 (NOT YET BUILT). Paste this into a session to have it
> create/inspect cron jobs once the feature ships. All calls hit the always-on Gateway.
> `<BASE>` = the Gateway base URL (default `http://127.0.0.1:7878`). If the Gateway runs with
> auth enabled, add `-H "Authorization: Bearer <gateway-token>"` to every call.

## Endpoints

| Method + path | Purpose |
|---|---|
| `POST   <BASE>/cron/jobs` | Create a job (validates cron; 400 on invalid) |
| `GET    <BASE>/cron/jobs` | List all jobs |
| `GET    <BASE>/cron/jobs/{id}` | Get one job (includes computed `nextRunUtc`) |
| `PUT    <BASE>/cron/jobs/{id}` | Update a job |
| `DELETE <BASE>/cron/jobs/{id}` | Delete a job |
| `POST   <BASE>/cron/jobs/{id}/run` | Run now (fire immediately, ignore schedule) |
| `GET    <BASE>/cron/jobs/{id}/runs` | Read run history |

## The exact scenario: "tonight at midnight, run the loop over a list of work items"

### 1. Make sure the work list exists and holds the items (shipped #273/#274 surface)
```bash
curl -X POST http://127.0.0.1:7878/lists -d '{"name":"Tonight"}'
curl -X POST http://127.0.0.1:7878/lists/Tonight/items -d '{"source":"github","id":"312"}'
curl -X POST http://127.0.0.1:7878/lists/Tonight/items -d '{"source":"github","id":"318"}'
curl -X POST http://127.0.0.1:7878/lists/Tonight/items -d '{"source":"github","id":"401"}'
curl -X POST http://127.0.0.1:7878/lists/Tonight/items -d '{"source":"github","id":"455"}'
```

### 2. Create the one-shot cron job
```bash
curl -X POST http://127.0.0.1:7878/cron/jobs \
  -H "Content-Type: application/json" \
  -d '{
        "name": "Tonight - drain work list",
        "scheduleKind": "oneOff",
        "runAt": "2026-06-17T00:00:00",
        "timeZoneId": "America/Chicago",
        "target": { "directorId": "workstation-A" },
        "action": {
          "repoPath": "D:\\ReposFred\\devthrottle",
          "seed": "/work-list run Tonight"
        }
      }'
```

Response (201):
```json
{
  "id": "cj_7fa3b1",
  "name": "Tonight - drain work list",
  "enabled": true,
  "scheduleKind": "oneOff",
  "runAt": "2026-06-17T00:00:00",
  "timeZoneId": "America/Chicago",
  "nextRunUtc": "2026-06-17T05:00:00Z",
  "target": { "directorId": "workstation-A" },
  "action": { "repoPath": "D:\\ReposFred\\devthrottle", "seed": "/work-list run Tonight" },
  "lastFiredUtc": null,
  "lastStatus": null
}
```

### 3. (Optional) Fire it right now instead of waiting
```bash
curl -X POST http://127.0.0.1:7878/cron/jobs/cj_7fa3b1/run
```

### 4. In the morning, read what happened
```bash
curl http://127.0.0.1:7878/cron/jobs/cj_7fa3b1/runs
```
```json
[
  { "scheduledUtc":"2026-06-17T05:00:00Z", "firedUtc":"2026-06-17T05:00:03Z",
    "targetDirectorId":"workstation-A", "sessionId":"8c1e...",
    "infraStatus":"started", "taskStatus":"completed" }
]
```

## Recurring variant ("every night at midnight")
Swap the schedule fields - everything else identical:
```json
"scheduleKind": "recurring",
"cronExpression": "0 0 * * *",
"timeZoneId": "America/Chicago"
```

## Run a skill/prompt instead of a work list
Change only `action.seed` to any skill or prompt the session should run, e.g.:
```json
"action": { "repoPath": "D:\\ReposFred\\devthrottle", "seed": "/implementation-loop 312" }
```

## Field reference (job create body)

| Field | Required | Notes |
|---|---|---|
| `name` | yes | Human label |
| `scheduleKind` | yes | `oneOff` or `recurring` |
| `runAt` | oneOff only | Local timestamp interpreted in `timeZoneId` |
| `cronExpression` | recurring only | Standard 5-field cron (`min hour dom mon dow`) |
| `timeZoneId` | yes | IANA/Windows zone; times computed in UTC internally |
| `target.directorId` | yes | Which Director/machine runs it (from `GET /directors`) |
| `action.repoPath` | yes | Working directory for the session |
| `action.seed` | yes | Skill or prompt text the session runs |
| `enabled` | no | Defaults true; set false to pause without deleting |
| `preventOverlap` | no | Defaults true; skip a fire while a prior run is in flight |

## Behavior contract (so a session knows what to expect)
- A one-shot fires once and auto-disables.
- A missed fire (Gateway was down) fires AT MOST ONCE on recovery - never a backlog replay.
- An invalid `cronExpression` returns HTTP 400 and is not stored.
- `infraStatus` ("did the session start") is reported separately from `taskStatus`
  ("did the work finish") - a started job is not a succeeded job.
