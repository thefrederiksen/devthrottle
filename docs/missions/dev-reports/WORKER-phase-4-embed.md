# Phase 4, Worker 1: the chrome-less reports page the Director pane will host

Issue #3019. Branch `mission/dev-reports-p4`, worktree `D:\ReposFred\devthrottle-dev-reports-p4`.
Brief: `BRIEF-phase-4-embed-worker.md`. Written 2026-09-17 by the worker who built it, for the phase 4 Manager.

No C# file was touched. Everything below is in `packages/client-core` and `apps/cockpit`.

## What I built

### 1. The host-key seam in client-core

`packages/client-core/src/auth/hostKey.ts` is new and tiny: `setHostSuppliedKey(key | null)` and
`hostSuppliedKey()`, over one module variable. It writes to nothing - no `localStorage`, no
`sessionStorage`, no cookie, no address. An empty string is treated as no key, so a host that supplies
nothing has supplied nothing.

`gatewayToken()` in `packages/client-core/src/api/client.ts` is the ONE place it enters:

```ts
function gatewayToken(): string {
  return hostSuppliedKey() ?? getDeviceKey();
}
```

No call site changed. That is the point of putting it there: a second place that built a Bearer of its own
would be a second place to get this wrong, and there are over a hundred calls.

**Three cookie helpers now read `getDeviceKey()` directly rather than `gatewayToken()`** -
`ensureGatewayCookie`, `adoptGatewayCookie`, `clearGatewayCookie`. The brief required this of
`ensureGatewayCookie`; I extended it to the other two for the same reason and stated it once in the code.
The cookie is THIS BROWSER's credential store and it lives for a year. A host-supplied key mirrored into
it would leave the Director's own credential on the machine, readable by any later page on this origin,
which is the exact outcome `hostKey.ts` exists to prevent. Outside an embedded pane the change is a no-op:
with no host key, `gatewayToken()` and `getDeviceKey()` are the same value.

### 2. The route

`/embed/reports/:sessionId`, in `apps/cockpit/src/main.tsx`, outside `RequireDeviceKey` and outside
`AppShell` - the same level as `/signin`. Two new files plus a stylesheet:

- `apps/cockpit/src/embed/hostBridge.ts` - the two messages and the wait, in one place so the page and the
  Director cannot drift. `HOST_READY_MESSAGE`, `HOST_KEY_MESSAGE`, `HOST_KEY_WAIT_MS`, a type guard, and
  `hostBridge(window)` which finds `window.chrome.webview` when a host put one there.
- `apps/cockpit/src/embed/EmbedReportsView.tsx` - the page.
- `apps/cockpit/src/embed/embed.css`.

It mounts the existing `DevReportList`, `DevReportViewer` and `DevReportConversation` from client-core.
There is no second viewer, no second list, no second frame host and no second Gateway client; the report's
trust rules are the ones already in `frameHost.ts`, untouched.

Behaviour, as built:

- "Loading..." is the initial render state, so it is on screen from the first paint, before any effect could
  have fetched anything.
- On mount it posts `{ kind: "dev-report-host-ready", sessionId }` through
  `window.chrome.webview.postMessage` when that exists, and listens for the answer on BOTH `window` and
  `window.chrome.webview`. (I added `sessionId` to the ready message beyond the brief's literal shape: a
  host driving more than one pane needs to know which pane asked. It is additive - a host that reads only
  `kind` is unaffected.)
- It accepts `{ kind: "dev-report-host-key", key, sessionId }` only when every one of these holds: the
  message is a well-formed object with a non-empty key, `sessionId` equals the route's session, the sender
  is the host (no source window, or this document - never a frame inside the page), and no key has been
  accepted yet on this load. Then, and only then, it calls `setHostSuppliedKey` and renders the reports.
- **No fallback.** After `HOST_KEY_WAIT_MS` (10 seconds) with no key it shows a plain-English refusal: this
  page is only for an application that embeds it, it has no sign-in of its own, the host supplied no key,
  and nothing is wrong with your account. It never reads the browser's own device key and never renders a
  report without a host key.
- It opens no stream, makes no enrollment call, sets no cookie, and never moves its own address.
- It clears the host key on unmount, and lets go of both listeners.

Layout: the report fills the pane. The conversation is capped at `clamp(260px, 30%, 380px)` beside it, so
the report keeps at least two thirds of the width at every size; under 900 pixels the viewer stacks and the
conversation goes below it. Phase 3's 200-pixel squeeze happened because the Reports tab sits inside the
rail and the session detail regions and only the leftovers reached the report - here the pane is the whole
window and the conversation cannot take more than a third of it.

### For Worker 2, the two things that will bite

1. **The key must arrive as an OBJECT.** Send it with `PostWebMessageAsJson`, not
   `PostWebMessageAsString`. The string form arrives as a string, the type guard refuses it, and on screen
   that looks exactly like a host that never answered. This is written into `hostBridge.ts` as well.
2. **WebView2 delivers the host's message on `window.chrome.webview`, not on `window`.** The page listens
   on both, so either works; do not conclude from the brief's wording that a `window` listener alone is the
   channel.

## What is proven, and how

All of it locally, all green on the current tree:

| Run | Result |
|---|---|
| `npm test --workspace @devthrottle/client-core` | 111 files, 1281 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 42 files, 361 tests passed |
| `npm test --workspace @devthrottle/mobile` | 13 files, 78 tests passed |
| `npm test --workspace @devthrottle/cc-assistant` | 8 files, 106 tests passed |
| `npm run typecheck` (workspace root) | clean, all four projects |
| `npm run build --workspace @devthrottle/cockpit` | built; `embed/reports/:sessionId` and `dev-report-host-ready` are both in the emitted bundle |
| `npm run lint` (workspace root) | 3 errors, all pre-existing, all "rule definition not found" in files I did not touch (`apps/cockpit/src/push/sw.test.ts`, `apps/mobile/src/pages/Recorder.tsx`, `apps/mobile/src/pages/VoiceMode.tsx`). My files are clean. |

`.\scripts\test-local.ps1` was not run: no C# file changed, and the brief says it is not needed for this slice.

New tests, 16 in two files:

`packages/client-core/src/auth/hostKey.test.ts` (6) - the seam, read back through the REAL `authHeaders()`
and `ensureGatewayCookie()`, not a copy of their logic:

- the host key is the Bearer once the host supplies it;
- it outranks a device key the browser happens to hold;
- it is never in `localStorage`, `sessionStorage` or a cookie, not even after `ensureGatewayCookie()` runs;
- `ensureGatewayCookie()` still mirrors the browser's OWN device key (the guard did not turn the cookie off
  for the Cockpit and the phone, which need it for the terminal stream);
- clearing it returns the Bearer to the device key;
- an empty string is no key.

`apps/cockpit/src/embed/EmbedReportsView.test.tsx` (10) - the page, with the global `fetch` stubbed so every
assertion is about the request that actually went out:

- "Loading..." on the first paint, no Gateway call, and the ready message posted with this session named;
- the host's key is the Bearer on the report calls - checked on the real `/dev-reports?sessionId=...`
  request, and on every request the page made, not only the first;
- a key naming a different session is refused, and no Gateway call is made;
- a key posted by a frame inside the page is refused (a real `iframe`'s `contentWindow` as the source), and
  no Gateway call is made;
- the first key wins - a later message cannot replace it;
- after ten seconds with no key the refusal is on screen, in those words, and no Gateway call was made;
- with no bridge at all (an ordinary browser opening the address) the same refusal, and no Gateway call;
- the key is forgotten on unmount and the bridge listener is released;
- the key reaches no `localStorage`, `sessionStorage` or cookie;
- the page reaches only `/dev-reports` addresses, constructs no `WebSocket`, and the router address is
  unchanged.

## What I watched fail on purpose

Each mutation was made on a COMMITTED tree, run, and then restored with `git checkout --` and the tree
confirmed clean; the restored tree was re-run green. Vitest transforms from source on every run, so there is
no stale artifact to certify.

In `client.ts` / `hostKey.ts`:

1. `gatewayToken()` back to `getDeviceKey()` only - 3 red. `expected undefined to be 'Bearer host-key-odd-47'`
   and twice `expected 'Bearer device-key-odd-31' to be 'Bearer host-key-odd-47'`. The pane would have called
   the Gateway as the browser, or with no credential at all.
2. `ensureGatewayCookie()` back to `gatewayToken()` - 2 red.
   `expected 'cc-gateway-token=host-key-odd-47' not to contain 'host-key-odd-47'`. The host's credential
   written into a one-year cookie, which is the leak, named exactly.
3. `setHostSuppliedKey` also writing `sessionStorage` - 1 red.
   `expected '{"cc.hostKey":"host-key-odd-47"}' not to contain 'host-key-odd-47'`.

In `EmbedReportsView.tsx`:

4. Session check removed - 1 red: the page left the waiting state on a key minted for another session.
5. Sender guard removed - 1 red: the frame's key was accepted.
6. One-key-per-load guard removed - 1 red: `expected 'a-second-key-odd-6' to be 'host-key-odd-73'`.
7. `setHostSuppliedKey(null)` removed from cleanup - 1 red: `expected 'host-key-odd-73' to be null`.
8. The timeout setting `"ready"` instead of `"refused"` (the fallback the brief forbids) - 2 red: the
   refusal never appeared.
9. Initial phase `"ready"` instead of `"waiting"` - 6 red, including
   `expected undefined to be 'Bearer host-key-odd-73'`: the page fetched with NO Authorization header at all.
   That is the silent version of this whole defect, and it is caught.
10. The ready post removed - 1 red: nothing was posted to the host.
11. A stray `fetch("/mobile/enroll")` and a `new WebSocket(...)` added on mount - 7 red.
12. The page moving its own address with `setSearchParams` - 1 red:
    `expected '/embed/reports/session-odd-41?pane=re...' to be '/embed/reports/session-odd-41'`.

## What is NOT proven

Stated plainly, because the Manager's live run is where most of it gets settled:

- **Nothing ran in WebView2, or in any real browser.** Every test here is jsdom. The bridge in the tests is a
  fake with the shape WebView2 presents - `postMessage` plus `addEventListener("message")`, delivering a
  `MessageEvent` with no source window. Whether WebView2 actually delivers on that object, with that source,
  and whether `PostWebMessageAsJson` arrives as an object, is Worker 2's and the Manager's to prove. If
  WebView2 differs in any of those, this page will sit on "Loading..." and then show the refusal - it will
  not misbehave, but it will not work either.
- **The layout is not measured.** `clamp(260px, 30%, 380px)` is CSS arithmetic I reasoned about, not a pixel
  count observed in a pane. Nobody has seen this page at 1400 by 900, or at any size. Claim D1 and the
  report-gets-the-room intent need a real screenshot.
- **The refusal is not terminal.** The listeners stay registered after the ten seconds, so a key that
  arrives late still promotes the page to showing reports. I judged that better than a dead page, but it is
  a decision I made, not one the brief settled.
- **"Never opens a stream / never enrolls / never sets a cookie" is proven at the level of this page's own
  calls**, by the fetch and WebSocket assertions. The Cockpit bundle's module-scope startup still runs on
  this route - `ensureGatewayCookie()`, the push service worker registration, the global error reporting -
  because `main.tsx` runs them for every route. In a WebView2 profile that has never enrolled,
  `ensureGatewayCookie()` is a no-op (it now reads the device key, and there is none). The service worker
  registration is not, and I left it alone as outside this slice. If the Manager wants the embed route to
  start nothing at all, that is a `main.tsx` change and it should be asked for.
- **No hosted Gateway, and no Director.** I built and typechecked; I did not deploy and did not run a pane.
- **Nothing about whether the Director's key is the right credential for these routes.** The finding says the
  four owner routes accept it. I took that as given and did not re-verify it against
  `DevReportEndpoints.cs`.

## Commits on `mission/dev-reports-p4`

- `e6339886f` - dev reports phase 4: a host application can supply the Gateway key (#3019)
- `a46e5d8b7` - dev reports phase 4: the chrome-less reports page a host embeds (#3019)
- plus the commit carrying the extra route-scope test, the host note in `hostBridge.ts`, and this report.
