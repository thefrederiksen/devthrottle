# Phase 2 plan - the Gateway report record and delivery (Manager)

Issue #2958. Mandate: `HANDOFF-phase-2.md`. Rulings 1-9 in `STATE.md` bind. Item shapes: `packages/client-core/src/devreports/CONTRACT.md`
section 3 - stored as the contract defines them, never a second shape.

## Who is the owner

The Gateway has no user inside an account: a device key and the machine token belong to one tenant, and
every session runs in one tenant. So **the owner of a session is its tenant**, and "a second user" is a
device key of another tenant. Every store read runs under the caller's tenant (the global query filter), so
another tenant's report is simply not found and answers **404** - its existence does not leak.

A **session key** is never an owner: it may call only the session routes, and only for its own session id.
A device key or the machine token may call only the owner routes.

## Routes

All JSON. Errors are `{ "error": "<sentence>", "code": "<code>" }` plus extra fields named below.

### Session routes (session key only, and `{sid}` must be the calling session - otherwise 403)

| Verb and path | Body | Answer |
|---|---|---|
| `POST /sessions/{sid}/dev-reports` | `{ "key": "<stable report key>", "html": "<the file>" }` | 200 `{ "report": ReportSummary, "created": bool }`; 422 `{ code: "shape_check_failed", errors: [..] }`; 413 `{ code: "report_too_large" }` over 10 MB of UTF-8 |
| `GET /sessions/{sid}/dev-reports` | - | 200 `{ "count": n, "reports": [ReportSummary] }` newest update first |
| `GET /sessions/{sid}/dev-reports/{reportId}` | - | 200 `ReportDetail`; 404 |
| `POST /sessions/{sid}/dev-reports/{reportId}/replies` | `{ "text": "..." }` | 200 `{ "reply": Reply }`; 404; 400 empty or over 20000 characters |

The key is the tool's choice (phase 2: the file's full path, lower-cased on Windows). Publishing the same
key again for the same session makes a NEW VERSION of the same report; publishing identical bytes again
still makes a new version (the owner asked for a reload). Title is the document `<title>`, else the header
marker's text, else the key's file name - computed on the Gateway from the parsed document.

### Owner routes (device key or machine token; a session key gets 403)

| Verb and path | Body | Answer |
|---|---|---|
| `GET /dev-reports?sessionId=` | - | 200 `{ "count": n, "reports": [ReportSummary] }` |
| `GET /dev-reports/{reportId}` | - | 200 `ReportDetail`; 404 |
| `GET /dev-reports/{reportId}/html?version=` | - | 200 `text/plain; charset=utf-8` raw bytes of that version (latest when omitted), header `X-Dev-Report-Version`; 404. Plain text on purpose: the host writes it into the frame (contract section 4 rule 2); it is never served as a page. |
| `POST /dev-reports/{reportId}/send` | `{ "items": [item, ...] }` (contract section 3) | 200 `{ "updates": [ { id, status, statusLabel } ] }` - one per item, in order, exactly the contract's `status` payload; 400 a malformed item (nothing is applied); 404 |

### Shapes

- `ReportSummary`: `id` (GUID), `sessionId`, `key`, `title`, `status` (the shape check's
  `waiting-on-you | agent-working | done`), `version`, `publishedAtUtc` (first), `updatedAtUtc` (latest
  version), `sessionEnded` (bool, Gateway verdict), `openItems` (queued + held count).
- `ReportDetail`: `report` ReportSummary, `items` [item + `status`, `statusLabel`, `sentAtUtc`,
  `deliveredAtUtc`], `replies` [ `{ id, text, at }` ] (contract reply shape).

## Delivery - the state machine (Gateway owns every word)

States and the exact label each carries are ONE fold (`DevReportItemStates`), tested:

| status | statusLabel | when |
|---|---|---|
| `held` | `Delivered when the agent finishes its turn` | session is working, or its machine is not connected |
| `delivered` | `Delivered to the session` | the Director accepted the prompt |
| `delivered` | `Sent to the session, not confirmed` | the send left the Gateway but no answer confirmed it (never retried - never twice) |
| `replaced` | `Replaced by a later answer` | a held answer to the same question was superseded by a newer one in a later send |
| `refused` | `This session has ended` | send to an ended session |
| `queued` | `Accepted` | transient, only while the send request is deciding |

Rules:

1. **Idempotent on (report, client item id).** An id already stored returns its CURRENT state and is never
   stored or delivered again - even if the text differs.
2. **A later answer to the same question replaces an earlier one.** Earlier answer still `held` -> it becomes
   `replaced` and only the newer one goes. Earlier answer already `delivered` -> the newer one is delivered as
   a change ("changes your earlier answer").
3. **Ended session -> every new item `refused`, "This session has ended"**, nothing stored as deliverable.
   Ended = the session is not live on the pushed roster AND its session history row carries an ending (or the
   roster shows it Exited). A session whose Director is merely offline is NOT ended - its items are held.
4. **Send while the session is Working (or its Director is not connected) -> `held`. Never interrupts.**
   Send while idle -> delivered right away, in the same request, as ONE prompt.
5. **Turn end drains.** The Gateway's existing turn-end boundary (`TurnEndWatcher` -> `GatewayHost` turn-end
   handler, the same one Session Rules hang off) drains every held item for that session - across all its
   reports - as ONE prompt. This is the Gateway's existing idle detection; there is no second wait. It is used
   instead of the request-scoped `WaitForIdle` poll because held items live in the database and must survive a
   restart: after a restart the watcher's catch-up fires for a session first seen already idle, which drains
   anything held.
6. **One drain at a time per session.** Deciding held-or-deliver and draining run under one per-(tenant,
   session) lock, so a send racing a turn end cannot deliver an item twice or strand it.

## The prompt - one fold (`DevReportPromptFold`)

Pure function of (report title, key, version, items in send order). Tested byte-for-byte. The owner's words are
quoted verbatim (never trimmed, reworded or shortened). Shape:

```
The owner answered your dev report "<title>" (version <n>).

Note 1 - on a table cell (row "Gateway", column "Failures"), which reads "42":
  This number is wrong

Note 2 - on the diagram part "Queue":
  ...

Answer to "When should we deploy?": Tonight - quiet traffic (tonight)
  Comment: ...

Reply in the report with: cc-dev-reports reply --report <reportId> "<your reply>"
Then update the report file and run cc-dev-reports open <file> again.
```

(Exact wording is the fold's; the fold's tests are the source of truth.) Several reports held for one session go in
one prompt, one block per report.

Provenance: the prompt is the OWNER's turn. It goes through the ordinary prompt verb with provenance marking it
as the owner's (device identity), not fleet-message or framework traffic.

## Storage (EF, both providers, tenant-scoped)

- `dev_reports`: id, tenant, session_id, key, title, status, version, published_at, updated_at. Unique
  (tenant, session_id, key).
- `dev_report_versions`: report id, version, html (text), byte hash (SHA-256 hex), byte length, published_at,
  status, title. Unique (report, version).
- `dev_report_items`: report id, client item id, kind, text/anchor/question fields as the contract (anchor as
  JSON text), version the owner was reading when it was sent is NOT known to the Gateway (not in the contract),
  status, status label, sent_at, delivered_at, replaced_by. Unique (report, client item id).
- `dev_report_replies`: id, report id, text, at.

The migration slot: `mission/fleet-manager` holds unlanded migrations. Generate ours on origin/main's chain head;
whoever lands last regenerates.

## The smallest tool (`tools/cc-dev-reports`)

- `cc-dev-reports open <file>` - reads the file, POSTs it with key = its full path. Shape errors: prints every
  error, exit 1. Success: prints report id, version, title, status, and where to view it (the Cockpit's Reports
  view arrives in phase 3; print the report id and the owner route).
- `cc-dev-reports reply "<text>" [--report <id>]` - without `--report`, the session's most recently updated
  report; none -> error.
- Session key and Gateway URL from `CC_GATEWAY_SESSION_KEY` / `CC_GATEWAY_URL` / `CC_SESSION_ID`, like
  `cc-devthrottle`. Unknown flag is an error. Every HTTP call has a timeout. `--json` output keeps one shape.
