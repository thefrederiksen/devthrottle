# Manager ruling - how the one address actually lands, signed in and signed out

Settled 2026-09-17 by the phase 3b Manager, BEFORE the Workers reached it, because the obvious design is
broken in three places and two Workers depend on the answer. This supersedes the redirect targets named in
`BRIEF-phase-3b-gateway-worker.md` and `BRIEF-phase-3b-apps-worker.md`. Build to THIS.

## What is broken about the obvious design

The obvious design is: `/r/{id}` is authenticated, looks the report up, and redirects to
`/session/{sessionId}?tab=reports&report={id}`. Signed out, `AuthMiddleware` sends the browser to
`/signin?next=/r/{id}` and the round trip comes back. Read the code and none of that works:

1. **`next` is followed by the ROUTER, not the browser.** `DeviceCallback` ends with
   `navigate(takeEnrollNext())`. A `next` of `/r/{id}` is therefore resolved inside the shell: the Cockpit
   has no `/r/:id` route, so it lands on Not found; the phone's router has a basename of `/mobile`, so it
   asks for `/mobile/r/{id}`, which is also nothing. The Gateway route is never requested again. The
   signed-out case cannot work with a Gateway path in `next`, on either shell.
2. **The mobile redirect eats the sign-in screen.** `MobileRedirect` sends every phone HTML navigation that
   is not already under `/mobile` to `/mobile/` **with no query string**. A phone bounced to
   `/signin?next=...` is therefore sent to the mobile home page and `next` is thrown away.
3. **The phone's own gate never remembers where you were going.** `RequireDeviceKey` in
   `apps/mobile/src/main.tsx` is `<Navigate to="/signin" replace />` - no `next` at all, unlike the
   Cockpit's, which carries `location.pathname + location.search`. So even reached correctly, a signed-out
   phone deep link lands on the phone's home screen.

Each one alone loses the report. All three are in the signed-out path at once.

## The ruling

**`GET /r/{reportId}` is a PUBLIC, tenant-free redirect that only picks the shell.** It is registered
BEFORE the authentication middleware and BEFORE the mobile redirect, and it does not look the report up:

- a phone User-Agent gets a 302 to `/mobile/report/{reportId}`
- anything else gets a 302 to `/report/{reportId}`

It needs no account because it decides nothing about the report - it echoes back an identifier the caller
already held and names which app should open it. There is nothing to leak and nothing to authorise, so the
sign-in problem simply stops existing at this layer.

**Both shells gain a route `/report/:reportId`** - `/report/x` in the Cockpit, `/mobile/report/x` on the
phone - which is the ONE landing that needs only a report id. It reads the report (it is inside the
device-key gate, so the call is authenticated), learns its session from the record, and lands in that
report: the Cockpit on the Reports tab of that session with the report open, the phone on its existing
`/session/:sessionId/reports/:reportId` screen. A report id that does not exist for this account shows the
"this report does not appear" state the viewer already has - not a redirect to a guess.

`/session/{sid}?tab=reports&report={rid}` is STILL built, because a person navigating inside the Cockpit
must be able to link and reload what they are looking at. It is simply not what `/r/{id}` targets.

**Signed out then works by itself, through each shell's own gate**, which is where it belongs: the gate
sends the browser to that shell's sign-in with `next=/report/{id}`, an in-shell route, so the router
navigation at the end of the round trip resolves. Two fixes are needed for that to be true:

- The phone's `RequireDeviceKey` must carry the requested route in `next`, exactly as the Cockpit's does.
  It is a one-line fix and it is the apps Worker's.
- `MobileRedirect` must not be reachable for `/r/{id}`, which the registration order above guarantees.

**`MobileRedirect` dropping the query string is a real defect** - it loses `next` for every signed-out
phone deep link, not only reports - but it is NOT in this phase's scope and the ruling above routes around
it. Note it, do not fix it here.

## Who does what

- **Gateway Worker:** the public `/r/{reportId}` route with the two 302 targets above, registered before the
  authentication middleware and before the mobile redirect. Prove the ordering both ways. The route no
  longer 404s on an unknown report, so drop that test and say why in your report; keep every test about
  device routing and ordering. The two label strings are unchanged and still yours.
- **Apps Worker:** the `/report/:reportId` route in both shells, the phone's `next` fix, and the Cockpit's
  `?tab=reports&report=` addressing. Your tests gain: landing on `/report/{id}` shows that report in each
  shell, and a signed-out mount sends the browser to that shell's sign-in with `next=/report/{id}`.
