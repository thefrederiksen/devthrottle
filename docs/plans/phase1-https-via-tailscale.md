# Phase 1 -- HTTPS-Only Remote Access via Tailscale Serve

**Status:** Draft for review (revised 2026-05-22 after design correction)
**Goal:** Make the Director's web UI reachable from a phone over HTTPS, and make plain-HTTP remote access **impossible by construction**. Tailscale Serve terminates TLS in front of a loopback-only Kestrel.
**Out of scope:** mobile landing page (Phase 2), raw terminal polish (Phase 3), Speak / pending-question banner (Phase 4), QR / short-link (Phase 5).

---

## 1. The design correction

The original Phase 1 draft accepted plain-HTTP-remote as a valid runtime state and proposed a "Microphone disabled" banner that told users to fix it. That was wrong: a banner treats a forbidden state as recoverable. The actual requirement is that a phone cannot reach the Director over plain HTTP at all.

The structural fix: **bind Kestrel to `127.0.0.1` instead of `0.0.0.0`**. Everything follows from that.

- Localhost on the same machine still works on plain HTTP (browsers treat `localhost` and `127.0.0.1` as secure contexts, so dictation from a desktop browser keeps working).
- Tailscale Serve still works (the Tailscale daemon runs on the same machine and forwards via loopback).
- Direct access from another LAN computer or a phone over plain HTTP **stops working**: the connection is refused at the TCP layer. There is no UI to gate, no banner to dismiss, no scheme to check.

This is a real behavior break for anyone who today bookmarks `http://<lan-ip>:7879/` from another computer. The new path for those users is to add Tailscale to that machine and hit the `https://...ts.net/` URL. Documented in `REMOTE_ACCESS.md`.

---

## 2. Why Tailscale Serve

Tailscale already issues a trusted Let's Encrypt cert for every node's `<host>.<tailnet>.ts.net` DNS name and auto-renews it. The cert chain is trusted by every modern phone OS without a profile install. Verified on this machine:

- `C:\Program Files\Tailscale\tailscale.exe`, version 1.98.2.
- This node's DNS name: `<host>.<tailnet>.ts.net`.
- `tailscale serve status`: "No serve config" (clean slate, no pre-existing mapping to migrate).

Rejected alternatives:

- **Kestrel-native TLS.** Two cert lifecycles (Tailscale + your own), self-signed certs require a profile install on iOS, the LAN IP can churn and break the cert SAN. More work, worse UX.
- **mkcert + manually trusted root.** Same iOS profile-install pain. No off-tailnet story.

---

## 3. The one unavoidable complication: port 443 is single-tenant

The user runs 3+ Director instances at once. Only one of them can own `--https=443` on this machine at a time. Two choices:

- **v1 (chosen): one Director claims port 443, others remain loopback-only.** The user picks a "remote-facing" Director. Others are reachable only from the host machine. Matches reality: the user usually only talks to one Director from the couch.
- **Deferred: path-based fan-out (`--set-path=/d1`, `--set-path=/d2`).** Breaks every root-relative `fetch('/sessions/...')` in the web pages. Real work, tracked separately, not Phase 1.

When you click "Run it for me" on Director #2 while Director #1 already owns 443, we **refuse and require explicit confirmation** ("switch HTTPS from Director #1 (`<repo>`, slot 1) to this Director?"). Decided 2026-05-22.

---

## 4. Deliverables

### 4.1 Server: bind Kestrel to loopback

**File:** `src/CcDirector.ControlApi/ControlApiHost.cs:104`

```csharp
// before
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, Port));

// after
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, Port));
```

Also update the comment at line 99 ("we're on plain HTTP (loopback/Tailscale)") to reflect the new model: "loopback only; remote access goes through Tailscale Serve which forwards to this loopback port."

No `ForwardedHeaders` middleware is needed. We are no longer gating any UI behavior on the request scheme, so there is nothing to spoof.

### 4.2 Server: `GET /setup/https` (read-only JSON)

**New file:** `src/CcDirector.ControlApi/SetupEndpoints.cs`

Computes the bootstrap state on each call:

```json
{
  "directorId": "a4f1...",
  "directorPort": 7879,
  "tailscale": {
    "installed": true,
    "running": true,
    "magicDnsName": "<host>.<tailnet>.ts.net",
    "tailscaleExePath": "C:\\Program Files\\Tailscale\\tailscale.exe",
    "currentServeConfig": [
      { "https": 443, "backend": "http://localhost:7880", "ownedByThisDirector": false }
    ]
  },
  "recommended": {
    "command": "tailscale serve --bg --https=443 http://localhost:7879",
    "phoneUrl": "https://<host>.<tailnet>.ts.net/"
  },
  "conflicts": [
    {
      "kind": "port443ClaimedByOther",
      "currentBackendPort": 7880,
      "message": "tailscale serve --https=443 is currently mapped to http://localhost:7880 (another Director). Running the recommended command will move HTTPS away from that Director."
    }
  ]
}
```

Implementation:

- Locate `tailscale.exe`: `where tailscale`, fall back to `C:\Program Files\Tailscale\tailscale.exe`. Cache the path.
- `tailscale status --json`, parse `Self.DNSName` (strip trailing dot), 200 ms timeout. If Tailscale is not installed or `BackendState != "Running"`, return `installed/running: false` and `recommended.command: null`.
- `tailscale serve status --json` to populate `currentServeConfig` and detect conflicts.
- `ownedByThisDirector` is true when the backend port in serve config matches this Director's Kestrel port.

The endpoint is read-only and never reveals secrets, so it does not require auth. It is also only reachable from localhost (per Section 4.1).

### 4.3 Server: `POST /setup/https/apply`

Same file. Runs the recommended command on behalf of the user.

```
POST /setup/https/apply
  body: { "confirmStealPort": true | false }
  200: { "ok": true,  "stdout": "...", "stderr": "..." }
  409: { "ok": false, "reason": "port 443 claimed by another Director, set confirmStealPort=true to override" }
  500: { "ok": false, "reason": "tailscale command failed: <stderr>" }
```

- If `conflicts` contains `port443ClaimedByOther` and `confirmStealPort` is not true, return 409.
- Otherwise spawn `tailscale.exe serve --bg --https=443 http://localhost:{Port}` with a 5 s timeout, capture stdout and stderr, return them.
- Auth: relies on the loopback-only binding from Section 4.1 as the trust boundary. Anyone who can reach this endpoint already has local-machine access. If `authEnabled` is on globally, it still applies via the existing middleware.

### 4.4 Web: `GET /setup` page

**New file:** `src/CcDirector.ControlApi/Web/setup.html`

One screen, mobile-first style for consistency with the other pages, but in practice usually opened from the desktop browser (because that is where you'd be the first time and is the only place you can reach the Director before Serve is configured):

- Header: "Remote Access -- this Director: slot N, repo `<path>`, port 7879."
- Tailscale section:
  - If not installed: link to install instructions, no command shown.
  - If installed but not running: instructions to sign in.
  - If running: shows the magic DNS name and the recommended command in a copy-button monospace box.
- "Run it for me" button: POSTs to `/setup/https/apply`. Shows stdout / stderr inline.
- Conflicts panel (only when conflicts exist): shows which Director currently owns 443, with a checkbox "Move HTTPS from `<other-director-port>` to this Director" gating the Run button.
- Result section: after success, big link "Open from your phone: `https://<host>.<tailnet>.ts.net/`" with copy-button.
- "Other Directors" callout: one paragraph noting that only one Director can be the remote-facing one in v1, others remain loopback-only. Link to the deferred path-routing plan.

Vanilla HTML/CSS/JS matching the other pages in `Web/`.

### 4.5 Desktop: a discoverable entry point in the Director window

Add a "Remote Access" row in whichever Avalonia panel currently exposes the Control API URL (TBD when starting work; not investigated for this plan). Behavior:

- Shows: this Director's port, current serve state ("HTTPS configured -- yours" | "HTTPS configured -- different Director" | "Not configured"), and a button "Open setup" that launches the default browser to `http://localhost:{port}/setup`.
- One-line subtitle: "To use the microphone from your phone, click Open setup."

This is the only desktop-side change.

### 4.6 Docs

**New file:** `docs/REMOTE_ACCESS.md`. Short. Sections:

- Why HTTPS is required for the phone (microphone secure-context).
- Why Tailscale Serve specifically.
- **Behavior change in v1.0.X:** plain-HTTP access from non-loopback clients now refuses connection. If you were using `http://<lan-ip>:7879/` from another LAN computer, add Tailscale to that machine and use the HTTPS URL.
- The exact command, with `{port}` and `{magicDns}` variables explained.
- Multi-Director: one Director gets 443, others remain loopback-only.
- Tailscale Funnel: short addendum for off-tailnet phones (e.g. cellular without the Tailscale app installed). Not the main path.
- Verifying: open the URL on the phone, the Voice tab should ask for mic permission.
- Removing: `tailscale serve --https=443 off`.

CLAUDE.md gets one line under the existing "remote" section pointing at `docs/REMOTE_ACCESS.md`.

---

## 5. Acceptance criteria

Phase 1 is done if all are true on a fresh checkout:

- AC1. `netstat -an | findstr :7879` shows `127.0.0.1:7879` listening, not `0.0.0.0:7879`. Direct connection from another LAN computer to `http://<lan-ip>:7879/` is refused.
- AC2. From a phone on the same tailnet, opening `https://<host>.<tailnet>.ts.net/` shows the Director UI with no browser cert warning.
- AC3. The Voice tab on `session-view.html` records and the dictation transcript appears, end to end, from the phone.
- AC4. With Tailscale not installed or not running, `/setup` still loads from `http://localhost:{port}/setup` on the desktop and explains what is missing instead of crashing.
- AC5. With another Director already owning port 443, `/setup` shows the conflict and refuses the apply until the user checks the "Move HTTPS from ..." box.
- AC6. The desktop user (sitting at the host machine) can still reach `http://localhost:{port}/` on plain HTTP and the dictation flow works there. (localhost is a secure context per browser policy.)
- AC7. Zero behavior change for users who never open `/setup`: the loopback URL still works exactly as today.

---

## 6. Estimated effort

Half a day.

| Task | Estimate |
|---|---|
| 4.1 Bind Kestrel to loopback + update comment + test | 20 min |
| 4.2 `/setup/https` GET + `tailscale status --json` parsing | 60 min |
| 4.3 `/setup/https/apply` POST + conflict guard | 45 min |
| 4.4 `setup.html` page | 60 min |
| 4.5 Desktop "Open setup" button | 30 min |
| 4.6 `REMOTE_ACCESS.md` | 30 min |
| Manual verification against AC1..AC7 from a phone | 30 min |

---

## 7. Open questions for the user

Answered so far:

- **Port 443 conflict policy: refuse + confirm.** (Decided 2026-05-22.)
- **Banner on HTTP pages: dropped.** Plain-HTTP-remote is impossible by construction; no UI needed. (Decided 2026-05-22.)

Still open:

1. **Desktop discoverability.** Where should the "Remote Access" row live in the Director window? An existing settings or about panel, or a new top-level menu item? Need to inspect the Avalonia tree before committing.
