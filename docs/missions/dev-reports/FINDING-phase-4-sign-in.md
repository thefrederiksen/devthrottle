# Phase 4 finding: how the Director pane signs in (a design fork, for the Architect)

Written 2026-09-17 by the phase 4 Manager, before any build. Issue #3019. Read against origin/main cd8b6233f.

## What I found

1. **The Director embeds no Cockpit page and signs no web page in.** Its only WebView2 uses are local files
   (`HtmlViewerControl`, `MarkdownViewerControl`, `PdfViewerControl`). The Cockpit toolbar button asks the
   Gateway for the Cockpit address and opens the owner's own browser (`MainWindow.axaml.cs` `BtnCockpit_Click`).
   Since issue #651 the Director never signs in to an account at all.
2. **No bare report route exists.** The Cockpit's reports live only inside `session/:sessionId` (the Reports tab,
   `apps/cockpit/src/sessions/ReportsTab.tsx`), wrapped in the full Cockpit frame. The Cockpit authenticates with a
   per-device key held in the browser's storage (`packages/client-core/src/auth/deviceKey.ts`), obtained by a sign-in
   on devthrottle.com and an enrollment.
3. **But the Director already holds a credential the report routes accept.** The four owner routes take "a device
   key or the machine token" (`src/CcDirector.Gateway/Api/DevReportEndpoints.cs` header), scoped to the caller's
   tenant. The Director calls the Gateway with exactly that: its own per-device key, or the local machine token
   (`GatewayConfig`). So no new sign-in is needed - the question is only how that credential reaches the page.

So option (a) of the handoff does not exist as written, and building a web sign-in is not needed either.

## The fork

**Option 1 (recommended): a bare Cockpit route, handed the Director's own key in memory.**
Add a chrome-less Cockpit route for one session's reports (list, viewer, conversation - the existing client-core
components, nothing new). The Director loads it from the Gateway in WebView2. When the page asks, the Director
posts its Gateway key to the page over the WebView2 message bridge; the page keeps it in memory only and uses it
as the Bearer for the four report calls. The key is never written to the page's storage.
Guards the Director enforces: it answers only a message from the TOP-LEVEL document whose address is that route
on the configured Gateway; it cancels any top-level navigation off that route; the report itself stays in the
sandboxed frame of CONTRACT section 4, which has no bridge and no parent access.
- Cost: the Director's Gateway key is visible to the Cockpit's own script for the life of the pane. That is the
  same authority a Cockpit device key already gives that same script in a browser, served by the same Gateway.
- Works for a local Gateway and the hosted one alike; ships with the next Gateway deploy.

**Option 2: the Director hosts the viewer bundle locally and makes the calls itself in C#.**
Build the client-core viewer into the Director, served from a local virtual host; the page asks the Director for
each Gateway call and the Director makes it with its key. The key never enters any page.
- Cost: a Node build inside the Director build, a transport seam in client-core, and the Director's copy of the
  viewer only updates when the Director updates, not when the Gateway deploys - two versions of the one viewer in
  the field.

**Option 3: mint a separate device key for the pane** (`POST /devices/enroll-signed-in`).
- Rejected: that route is loopback-only and retires the Director's existing key ("one key per device"), and it
  does not exist for the hosted Gateway.

## Recommendation

Option 1. It keeps one implementation that deploys with the Gateway, needs no sign-in, and gives the page no
authority the Cockpit does not already have. I will prove in WebView2 that the sandboxed report frame cannot reach
the bridge or receive the key, using the phase 3 hostile report.
