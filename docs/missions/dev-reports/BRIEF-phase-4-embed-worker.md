# Phase 4, Worker 1: the chrome-less reports page the Director pane will host

Issue #3019 (child of #2936). Branch `mission/dev-reports-p4`, worktree `D:\ReposFred\devthrottle-dev-reports-p4`.
You are a Worker. You report to the phase 4 Manager, nobody else. Read `docs/missions/dev-reports/STATE.md`
(rulings 1-9 bind you), `packages/client-core/src/devreports/CONTRACT.md` section 4, and
`docs/missions/dev-reports/FINDING-phase-4-sign-in.md` (why this page exists at all).

## What you build

A Cockpit route that shows ONE session's dev reports with no Cockpit chrome, and which gets its Gateway
credential from the host application that embeds it - never from the browser's own store.

The Director (Worker 2, after you) will load this route in WebView2, hand it the Director's own Gateway key
over the WebView2 message bridge, and show it in a pane. Your page must work with nothing but that message.

### 1. The host-key seam in client-core

`packages/client-core/src/api/client.ts` builds every Bearer from `getDeviceKey()` (the browser's enrolled key).
Add an in-memory override, in its own small module (for example `packages/client-core/src/auth/hostKey.ts`):

- `setHostSuppliedKey(key: string | null)` and `hostSuppliedKey(): string | null`.
- It lives in a module variable ONLY. It is never written to `localStorage`, `sessionStorage`, a cookie or the
  URL, and `ensureGatewayCookie()` must not mirror it.
- `gatewayToken()` in `client.ts` returns the host key when one is set, otherwise `getDeviceKey()`. That is the
  one place it enters; no call site changes.

### 2. The route

A new ungated route in `apps/cockpit/src/main.tsx`, outside `RequireDeviceKey` and outside `AppShell` (the same
level as `/signin`): `/embed/reports/:sessionId`.

It renders the existing `DevReportList`, `DevReportViewer` and `DevReportConversation` from client-core - the
same components the Reports tab uses. Do NOT write a second viewer, a second list or a second frame host.
Layout: the report fills the pane, with the conversation beside it on a wide pane and below it on a narrow one.
Phase 3 reported that at 1400 by 900 in the Cockpit the report was squeezed into about 200 pixels beside the
conversation - do not repeat that here; the report gets the room.

Its behaviour:

- It shows "Loading..." immediately, before anything is fetched.
- On mount it posts `{"kind":"dev-report-host-ready"}` to the host with `window.chrome.webview.postMessage`
  when that exists, and listens on `window` for the host's answer
  `{"kind":"dev-report-host-key","key":"<key>","sessionId":"<id>"}`. It accepts the key only when `sessionId`
  matches the route's session, calls `setHostSuppliedKey`, and only then renders the reports.
- **No fallback.** If no host key arrives within a bounded wait (10 seconds), the page says in plain words that
  the host did not hand it a Gateway key and that this page is only for a host that embeds it. It must never
  quietly fall back to a browser device key, and it must never render reports without a host key.
- It never opens a stream, never enrolls, never sets a cookie, and never navigates itself anywhere.
- It clears the host key on unmount.

The report itself keeps every rule of CONTRACT section 4 - you get that for free by using the existing frame
host; do not touch it.

## Proof required

- `npm test --workspace @devthrottle/client-core` and `npm test --workspace @devthrottle/cockpit` green, plus
  `npm run typecheck` at the workspace root.
- New tests, named for what they prove: the host key is used as the Bearer; the host key is never written to
  `localStorage`, `sessionStorage` or a cookie; a key for a different session is refused; with no host key the
  page shows the refusal and makes no report call; the key is cleared on unmount.
- Watch each new test fail on purpose: break the thing it proves, see it red with the symptom, restore it, see
  it green. Write what you watched into your report.
- `.\scripts\test-local.ps1` is NOT needed for this slice (no C# changes). If you touch any C# file, stop and
  tell the Manager instead.

## When you are done

Commit and push to `mission/dev-reports-p4` as you go (never merge, never touch main, never open a pull request).
Write `docs/missions/dev-reports/WORKER-phase-4-embed.md`: what you built, what is proven and how, what you
watched fail, and what is NOT proven. Commit that too. Then tell the Manager in ONE line and stop.

Repository rules that bind you: no fallbacks, log-and-fail-clearly, plain English with no abbreviations, no
Unicode or emoji in any output, and nothing anywhere that names an assistant or its vendor.
