# Factory Boxes phase 1 - the desktop relay leg

Mission: Factory Boxes (issue #4258), mission document section 5 item 1, and section 7 (the relay check and the
allow-list check). Built on devlinux, 19 to 20 October 2026, on branch `boxes/phase1-relay-leg`. The requirements list
is the spike's section "What the relay leg must do as a result" (`phase1-spike-path-prefix.md`), plus the Tech Lead's
and the Delivery Lead's additions during the build (the desktop host, the ticket, the spend line).

## The answer, in short

A browser reaches a box's desktop at `/boxes/{boxId}/desktop/` **on a separate desktop host**, carried Gateway to
Director over the Director's own outbound tunnel, and from the Director to `127.0.0.1:<port>` - the one port the
Director's own hosted-desktop list holds for that box id, and nothing else. HTTP (streamed both ways, 64-megabyte
uploads) and the desktop's WebSocket (messages of 268 kilobytes and more, split into 48-kilobyte tunnel frames and put
back together) both work, proven end to end with headless Chromium through a scratch Director and a local **hosted**
Gateway (the production launcher, hosted mode, PostgreSQL, a real account boundary), and before that through a
self-host one.

A busy desktop cannot starve the terminal streams and commands on the same Director connection: each desktop stream
has its own one-megabyte credit window, so it never fills the connection's shared stream buffer. Each box is held to
8 megabits a second toward the browser. Measured with that ceiling on: an idle desktop costs **0.004 gigabytes an
hour**, typing in a terminal **0.05**, browsing a site **1.6**, and the worst case (a full-screen flood) **3.6**, which is
the ceiling itself.

## What was built

| Where | What |
|---|---|
| `Gateway.Contracts/DesktopRelayMessages.cs` | The wire: the `open-desktop-stream` command, the `DesktopRelay` hub method, the frame (Head, Data, Credit, Closed), and every bound as a named constant (`DesktopRelayLimits`). |
| `Core/Boxes/DesktopRelayPump.cs` | The engine that runs at both ends: three loops, the credit window, the end of a stream in every direction, the idle timeout, the periodic usage line. |
| `Core/Boxes/DesktopRelayWindow.cs`, `DesktopRateLimiter.cs`, `DesktopRelayUsage.cs` | The send window, the per-box rate ceiling (on an injected clock), the byte counts per stream and per box. |
| `Core/Boxes/WebSocketRelayEndpoint.cs` | A WebSocket as one end (the browser's on the Gateway, the box's on the Director): splits messages into frames, puts them back together, keeps text and binary apart, passes close codes on. |
| `ControlApi/Boxes/DirectorDesktopRelayHandler.cs` | The Director's half: the allow-list check, the connection limits, opening the box's socket or request on loopback, and calling the hub to carry the stream. |
| `ControlApi/Boxes/DesktopBoxConnector.cs` | The one place a box address is made: `127.0.0.1` and the listed port, the caller supplies only the path, and anything that would leave the box is refused. |
| `ControlApi/Boxes/BoxHttpRelayEndpoint.cs` | One HTTP request to the box, its body streamed in, its response streamed out. |
| `Gateway/Streaming/DesktopRelayRegistry.cs`, `DirectorHub.DesktopRelay` | The Gateway's open streams and the hub method; only the Director a stream was opened on, in its tenant, may attach. |
| `Gateway/Boxes/DesktopRelayLegs.cs`, `BrowserHttpRelayEndpoint.cs`, `BoxDesktopHeaders.cs` | The browser-facing leg: open, refuse with a status, relay, and which headers cross. |
| `Gateway/Boxes/BoxDesktopEndpoints.cs`, `BoxDesktopOrigin.cs`, `BoxDesktopTickets.cs` | The desktop host, the ticket route, the ticket and the box-only desktop session. |

## How it works

**The desktop host.** The setting `CC_BOXES_DESKTOP_ORIGIN` names the separate site the desktop is served on (in
production `https://devthrottle-gw.azurewebsites.net`, the Gateway's own Azure name: azurewebsites.net is on the public
suffix list, so a browser treats it as a different site from gateway.devthrottle.com). Unset - the default - no desktop
is served anywhere and nothing else changes. Set:

- `/boxes/{id}/desktop/...` answers **only** on the desktop host; on the main host it is 404.
- The desktop host answers **nothing else** - no Cockpit, no phone app, no account, device or session route, not the
  ticket route. Each is a 404 whose body says in plain words that this address serves only box desktops and that the
  Gateway is at its configured public address, named (`CC_GATEWAY_PUBLIC_URL`, `https://gateway.devthrottle.com` in
  production). Each refusal is logged with its method, path and user agent (`desktop host REFUSED POST
  /director-stream/negotiate user-agent="..."`), so after deploy the Delivery Lead can spot a stray Director or phone
  still configured with the Azure name. The one exception is `GET /healthz`, accepted by the Tech Lead, because the
  deploy, rollback and staff-email workflows read the live commit from
  `https://devthrottle-gw.azurewebsites.net/healthz`.

**Getting in.** The Cockpit, signed in on the main host, calls `POST /boxes/{boxId}/desktop-ticket`. Inside the Boxes
gate, for a box a connected Director of **the caller's own account** hosts, it answers
`{"url": "https://<desktop host>/boxes/{boxId}/desktop/?ticket=...", "expiresAtUtc": ...}`; the Cockpit opens the url
verbatim. The ticket is single-use and lives 60 seconds. The desktop host redeems it for a desktop session and sets
it in a cookie `cc-box-desktop` that is HttpOnly, Secure, SameSite=Strict, scoped to `/boxes/{boxId}/desktop/`, and
good for 8 hours. It then moves on to the address without the ticket through a tiny page of its own (a
`<meta http-equiv="refresh">`) rather than a redirect, because a SameSite=Strict cookie is not sent on any request of a
redirect chain that began on another site - the Cockpit's - but is sent on a navigation the desktop host itself starts.
Every desktop request and the WebSocket upgrade are checked against that cookie: the session must exist, be unexpired
and be for this box. The Gateway's own sign-in (its cookie, its Authorization header) opens nothing on the desktop host,
and is never forwarded to a box; neither is the box cookie. Tickets and sessions are held in memory (one Gateway
instance on the Basic plan); a Gateway restart means opening the box again from the Cockpit. Neither ever reaches the
log: the access log writes `?[redacted: desktop ticket]`.

**One relay stream per browser request or WebSocket.** The Gateway registers a fresh stream id with its owner (the
account and the Director), sends `open-desktop-stream` down the tunnel, and the Director answers it: it resolves the box
id through its own hosted-desktop list - a box id the list does not hold is **refused at the Director and logged**,
whatever the Gateway believed - checks its connection limits, opens the box's WebSocket (or starts the HTTP request) on
`127.0.0.1:<port>`, and calls the hub method `DesktopRelay` once. That one call carries the Director's frames up (a
client-to-server stream) and returns the Gateway's frames down (a server-to-client stream). Every connection still
starts at the Director.

**Flow control, and why it is not the terminal's.** SignalR gives all of one connection's up-streams one shared receive
loop. When one stream's small buffer is full the hub stops reading the WHOLE connection. The terminal stream relies on
that to push back - fine for a few kilobytes of terminal output, fatal for a desktop: a stalled phone would stop every
terminal frame and every command reply behind it. So each side of a desktop stream may have at most one megabyte
outstanding at the other side; the receiver grants bytes back as Credit frames once it has delivered them. A receiver
always has room for what arrives, so it reads the tunnel at once and the shared buffer never fills; a slow browser
stops the credit, the Director stops reading the box, and Selkies' own 50-millisecond acknowledgements turn that into a
lower frame rate. Slow the desktop, never the terminal.

**Messages larger than a frame.** A WebSocket message is read in pieces of at most 48 kilobytes (the same bound as
every other frame on the tunnel, deliberately not raised), each marked text or binary and the last marked as the end;
the far side puts the message back together and sends it whole. The largest message in the end-to-end run was 268,025
bytes.

**Closing.** A close from either side closes the other with its code and reason, and frees both ends (the Gateway's
stream registry, the Director's connection slot). A Director that disconnects closes every desktop socket it carried
with 1001 and "The Director hosting this box disconnected." A tunnel drop on the Director side closes every box socket.

**Limits** (each a named constant in `DesktopRelayLimits` or `BoxDesktopTickets`, each with a test):

| Limit | Value | Why |
|---|---|---|
| Desktop WebSockets per Director | 8 | One per viewer; refused with 503 and a sentence beyond it. |
| Desktop HTTP requests per Director | 32 | Bounds memory: each holds at most a window each way. |
| Idle timeout on an open desktop socket | 1 hour | The brief's floor; Selkies acknowledges every 50 milliseconds, so an hour of nothing is a socket left behind. |
| Request body | 64 megabytes | Selkies uploads a large file as 64-megabyte POSTs; over it is 413. |
| WebSocket message put back together | 16 megabytes | So reassembly is bounded; over it closes with 1009. |
| Credit window | 1 megabyte per direction per stream | 160 megabits a second at a 50-millisecond round trip, far above the ceiling. |
| Rate toward the browser | 8 megabits a second per box | The spend line (below). |
| Ticket | 60 seconds, single use | |
| Desktop session | 8 hours | A working day; Open asks for a fresh ticket each time. |

## Decision: one connection, not a second one for the desktop

**Decided: the desktop rides the Director's existing tunnel; no second connection.** Measured on the real Gateway and
Director (the local run below): while the box flooded its screen at the 8-megabit ceiling, ten Gateway-to-Director
commands on the same tunnel (`POST /directors/{id}/backfill-numbers`) took 189, 9.1, 7.9, 5.8, 6.1, 6.1, 7.8, 6.5, 10
and 5.9 milliseconds (the first is one outlier; the median is 6.8), against 3.3 to 7.5 milliseconds (median 3.7) with
the desktop quiet. The hosted run gave the same picture: 64.8, 7.5, 7.3, 6.2, 7.3, 7.1, 7.3, 6.3, 11.1 and 6.3
milliseconds during the flood (median 7.3), against 4.8 to 9.2 quiet (median 5.25). And the in-process test over a real SignalR connection with the browser stalled outright - the
worst case, which no time measurement can stand for - delivered every terminal frame and answered the command while
the desktop sat saturated (numbers below).

Why not a second connection anyway: it would need its own authentication, Hello, reconnect and teardown path,
double the hub connections a two-core Gateway carries per Director, and it would not help the one thing the window does
not cover - queueing in a router between the house and Azure, which both connections share. Revisit if the deployed
check from outside the house shows terminal latency rising under desktop load; the window and the ceiling are the
first knobs, a second connection the last.

## Spend: the ceiling, and what ordinary use costs

**Chosen: 8 megabits a second per box toward the browser, enforced by the Director** (`DesktopRelayLimits.MaxBoxBytesPerSecond`
= 1,000,000 bytes a second, one second of burst so a keyframe passes whole). The box cannot talk its way past it: it is
a token bucket on the Director's reads of the box, not a setting the page or the box can change, and it is shared by all
of a box's sockets and requests, so more sockets buy no more rate. The demo desktop (`demo-desktop-soren`) is not
reconfigured: the ceiling is the relay's.

Measured in the local self-host end-to-end run (`relay-capture.json`, "usage"), bytes toward the browser:

| Use | Megabits a second | Gigabytes an hour |
|---|---|---|
| Idle desktop, 60 seconds | 0.008 | **0.004** |
| Typing in a terminal, 63 seconds | 0.106 | **0.048** |
| Browsing a site (scrolling a Wikipedia article in the box's Chromium), 60 seconds | 3.576 | **1.609** |
| Worst case: a full-screen terminal flood, 30 seconds | 7.995 | **3.598** (the ceiling) |

The 8-megabyte download through the relay came back at the ceiling as well (8,388,608 bytes in 7.6 seconds). The hosted
run measured the same within rounding (`hosted/relay-capture.json`): 0.004, 0.049, 1.624 and 3.596 gigabytes an hour
(7.991 megabits a second at the flood), and the download in 7.5 seconds.

**The encoder's own knobs, for the phase 2 box image.** Selkies takes server-side settings that a range locks
(`/lsiopy/lib/python3.14/site-packages/selkies/settings.py` in the image): `framerate` (default "8-240", starting at
60), `video_bitrate` in kilobits a second (default "100-1000000", starting at 8000), `rate_control_mode` (default "crf",
constant quality, so the bitrate follows the screen), plus `encoder` and `jpeg_quality`. A value written as a one-value
range ("30-30") is locked, so the page's SETTINGS message cannot raise it. They are set as `SELKIES_<NAME>` environment
variables at container start, so they are a box-image setting for phase 2. Recommended there, NOT measured here:
`SELKIES_FRAMERATE="30,8-30"` (a phone gains little above 30 frames a second, and it halves the encoder's worst case),
`SELKIES_RATE_CONTROL_MODE="cbr"` with `SELKIES_VIDEO_BITRATE="6000,100-6000"` (constant bitrate under the relay's
ceiling, leaving room for the 128-kilobit audio). The point is that the encoder, not the relay, should be what keeps a
busy screen under 8 megabits: when the relay's ceiling does the work, up to one window (one megabyte, about a second
at the ceiling) of video can queue between box and browser, so a flooding screen can lag behind the mouse by up to
that much. Not measured as a latency.

## Security: the desktop is box code, so it must not share the Gateway's origin

Found while building, reported to the Tech Lead, and settled by the Tech Lead and the Delivery Lead (the desktop host
above is the result):

- **What a box's page could reach on the Gateway's own origin.** The page is served from inside the box (Selkies'
  `index.html`), so anything with root in the box can change it. On `gateway.devthrottle.com` it would run as the
  Gateway: it could read `localStorage`, where the Cockpit keeps every signed-in account's device key
  (`packages/client-core/src/auth/accountStore.ts`), read the non-HttpOnly `cc-gateway-token` cookie the phone writes
  (`packages/client-core/src/api/client.ts`), and call every Gateway route as the person viewing it. A path, a cookie
  path or a content security policy cannot separate two pages on one origin; only a separate site can.
- **Why phase 1 could have accepted it** (the owner's own box, an owner-controlled image, no agent in it) - and why it
  did not have to: the Azure name is a separate site for free.
- **Required before any box runs agent code or belongs to anyone else:** the separate desktop origin. Done here.
- **Known residue, NOT solved now** (the Delivery Lead's ruling): on one shared desktop host, box A's page can send
  requests to box B's path, and the viewer's box-B cookie goes with them (cookies are per site, and a cookie's path does
  not stop a same-site page from sending a request to that path). Acceptable while every box is the owner's own
  (mission decision D10). A host per box is the next mission's problem.
- **Old storage on the Azure name is wiped first** (the Tech Lead's addition): anyone who ever opened the Cockpit at
  `devthrottle-gw.azurewebsites.net` has device keys in that origin's `localStorage`. The ticket exchange answers with
  `Clear-Site-Data: "storage"`, so that storage is gone before any box page loads; not `"cookies"`, which would wipe the
  box cookie set on the same response (`TicketExchange_WipesTheSitesStorageButNotItsCookies`).
- Smaller points, handled: `Set-Cookie` from a box never reaches the browser (a box's cookie would land on the desktop
  site); a redirect from a box to its own address (`http://127.0.0.1:...`) or its root is kept inside its prefix; the
  desktop route is excluded from response compression.

## Tests

All in `src/CcDirector.Gateway.UnitTests/Boxes/` (the project references the Director's code too). Fakes only: in-memory
hosts (`TestServer`), an in-memory tunnel (the Gateway's command send calls the real Director handler; the Director's
relay call lands in the real Gateway registry's `Attach`, exactly as the hub method hands it over), a fake desktop
server in process, and fake clocks. No Docker, no real port, no sleep, no real clock.

| The brief asked for | Test |
|---|---|
| Signed out refused | `Ticket_SignedOut_IsRefused401`, `DesktopHost_NoCookie_IsRefused401ForThePageAndTheSocket`, `DesktopHost_TheGatewaysOwnSignIn_IsNotEnough` |
| Other tenant refused | `Ticket_AnotherAccountsBox_Is404AndNoTicketIsMinted` (a real hosted boundary, two accounts with real device keys), `DesktopHost_ACookieForOneBox_DoesNotOpenAnother`, `DesktopRelayRegistryTests.Attach_AnotherDirectorOrAccount_IsRefusedAndTheStreamUntouched` |
| Flag off gives 404 | `Ticket_BoxesFlagOff_Is404WithNoBody`, `DesktopHost_BoxesTurnedOffAfterSignIn_Is404` |
| Unknown box id refused at the Director and logged | `DirectorDesktopRelayHandlerTests.HandleAsync_BoxNotOnTheList_IsRefusedNotFoundAndLogged` (asked directly, no Gateway; checks the log line), `DesktopPage_BoxNotOnTheDirectorsList_IsRefusedAtTheDirector` (the Gateway fooled, the Director refuses) |
| Never a host or a caller's port | `HandleAsync_APathThatWouldLeaveTheBox_IsRefusedBadRequest`, `DesktopBoxAddressTests` |
| No-slash redirect and its Location | `DesktopHost_NoTrailingSlash_RedirectsToTheSlashFormOnTheSameHost`, `DesktopRedirect_StaysInsideTheBoxsPrefix_NeverLoopback` |
| HTTP round trip with streamed body | `DesktopUpload_SixtyFourMegabytes_StreamsThroughWithTheUploadHeaders` (64 megabytes, byte checked, X-Upload-* headers), `DesktopDownload_TwentyMegabytes_ArrivesWholeAndInOrder`, `DesktopRequest_CarriesPathQueryAndHeaders_ButNotTheGatewaysCredentials`, `DesktopUpload_OverSixtyFourMegabytes_Is413AndNeverOpened` |
| WebSocket text and binary round trip | `DesktopSocket_TextAndBinary_RoundTripWithTheirTypes` |
| 244 kilobytes split and reassembled byte for byte | `DesktopSocket_A244KilobyteMessage_IsSplitOnTheTunnelAndArrivesWholeByteForByte` (checks the frame count and that every frame is at most 48 kilobytes), `DesktopSocket_ALargeMessageFromTheBrowser_ReachesTheBoxWhole` |
| Close in each direction | `DesktopSocket_BrowserCloses_ClosesTheBoxAndFreesBothEnds`, `DesktopSocket_BoxCloses_ClosesTheBrowserWithTheBoxsStatus` |
| Director disconnect | `DesktopSocket_DirectorDisconnects_ClosesTheBrowserWithAClearReason`, `CancelAll_ClosesEveryBoxSocketAndFreesTheSlots` |
| Fairness | `DesktopRelayFairnessTests.TerminalFramesAndCommands_StillFlow_WhileADesktopStreamIsSaturated` |
| Limits | `HandleAsync_MoreSocketsThanTheLimit_RefusesTheNextOne`, `DesktopSocket_QuietForAnHour_IsClosed_ButNotBefore`, the 64-megabyte tests above, `DesktopRateLimiterTests` |
| The desktop host (the Delivery Lead's two refusals) | `MainHost_TheDesktopRoute_DoesNotExist`, `DesktopHost_EveryOtherRoute_IsRefused` (checks the plain refusal naming the Gateway's address), `RefusalText_NoGatewayAddress_StillSaysWhatThisAddressServes`, `DesktopHostRefusalLogTests.DesktopHost_ARefusedRoute_IsLoggedWithMethodPathAndUserAgent`, `DesktopHost_Healthz_StillAnswers_ForTheDeployWorkflows`, `BoxDesktopOriginTests` |
| The ticket | `TicketExchange_SetsAStrictBoxOnlyCookieAndMovesOnWithoutTheTicket`, `TicketExchange_WipesTheSitesStorageButNotItsCookies`, `TicketExchange_ASecondUse_IsRefused`, `TicketExchange_AfterAMinute_IsRefused`, `TicketExchange_ATicketForAnotherBox_IsRefused`, `DesktopHost_ASessionPastEightHours_IsRefused` |
| Bytes logged per box, each direction | `DesktopSocket_BytesAreCountedPerBoxInEachDirection` |
| The hosted shape (review finding 1) | The rig's command send drops a command sent with no tenant scope and finds the Director only in the scope's own account, as a hosted Gateway does, so every page, request and socket test above fails if the desktop host does not enter the box session's account (checked: with the scope removed, 18 tests fail). `DesktopPage_OnAHostedGateway_SendsTheOpenInsideTheBoxSessionsAccount` names it. |
| A close with the window exactly full (review finding 3) | `DesktopRelayPumpWindowEdgeTests.RunAsync_FarSideClosesWithTheWindowExactlyFull_TheEndpointGetsTheFarSidesCloseNotAProtocolFault`, `CostOf_ClosedAndCredit_CostNothing_DataAndHeadCostTheirBytesPlusOverhead` (checked: with the old cost, both fail) |

**The count:** 119 relay tests in `Gateway.UnitTests/Boxes`, all passing, on this branch rebased onto main and on the
tree the hosted run was built from. With the list's own tests beside them on the rebased branch: 168 in
`Gateway.UnitTests` (119 relay, 49 list), 48 in `Core.UnitTests`, 6 in `Avalonia.Tests`, all passing.

**The fairness test, with its numbers.** One REAL SignalR connection to the real `DirectorHub` with the Gateway's own hub
options (MessagePack, the shared stream buffer of 4), carrying a terminal up-stream and a desktop relay stream. The box
floods 48-kilobyte frames as fast as it is allowed; the browser never reads. Then the terminal sends its frames and the
Gateway sends the Director a command on the same connection:

```
desktop frames produced before the window closed: 21 (cost 49216 bytes each, window 1048576 bytes)
desktop frames delivered to the stalled browser: 0
terminal frames delivered while the desktop was saturated: 200 of 200
command answered while the desktop was saturated: True
```

Then the browser is released and the desktop resumes: it was slowed, not dropped. The test counts; it times nothing.

## The local end-to-end proof (`phase1-relay-leg/`)

There are two runs. The **hosted** run (20 October 2026, `phase1-relay-leg/hosted/`) is the one that proves the
production shape; it was made after review finding 1 showed that the self-host run could not have caught a defect only a
hosted Gateway has. The **self-host** run (19 October 2026, the files directly in `phase1-relay-leg/`) came first and is
kept as it was.

### The hosted run (`phase1-relay-leg/hosted/`)

**Set-up** (`hosted/rig/gateway.sh`, `hosted/rig/mint.py`; the signing key it made was deleted):

- **The Gateway** was the hosted launcher, `CcDirector.Gateway.Host` - the production entry point, the one the image
  runs, which carries the hosted-image mark and so enforces the hosted startup contract - built from the merged tree
  below, with `CC_GATEWAY_HOSTED=1`, its own scratch root and port 7996, `CC_BOXES_DESKTOP_ORIGIN=http://localhost:7996`
  and, as the contract requires, an https public address: `https://gateway.relay-proof.invalid` (a reserved name that
  goes nowhere; it is what the desktop host's refusal names).
- **Its database** was a throwaway PostgreSQL 16 in a container of its own (`boxes-relay-postgres`, on 127.0.0.1 only),
  because the hosted Gateway does not run on the local SQLite file: its account tables are not created there and every
  entitlement read fails (tried first; the log said `no such table`). The Gateway migrated the database itself. The one
  table it does not create, `gateway.entitlements` (the payment side writes it), was created by hand in the shape
  `EntitlementSchemaQualificationPostgresTests` uses, with one active hosted row for the test account. Docker served the
  run, never a test.
- **Sign-in** never reached production: a throwaway ES256 key made for this run signed account tokens for one made-up
  person on the reserved `.invalid` domain; the Gateway was given its public key set and issuer, and every website call
  (`DEVTHROTTLE_API_URL`, `DEVTHROTTLE_REFRESH_URL`) was pointed at a closed local port.
- **Two keys**, both from the Director door, `POST /devices/enroll-hosted`, for that one account (the Gateway minted the
  account's tenant on the first): the Director's own, and a second that played the Cockpit. A Cockpit's key on a hosted
  Gateway comes through `/mobile/enroll` with a device key from devthrottle.com, which this run must not reach. The
  ticket route reads only the account a key belongs to, so for the relay the second key is the Cockpit; what it does not
  exercise is the Cockpit's own sign-in and the remote-access rule on browser keys. Then the Gateway was restarted with
  that account in `DEVTHROTTLE_BOXES_TENANT_IDS`.
- **The Director** was the slot-5 Linux build from the merged tree, with its own `CC_DIRECTOR_ROOT` and `HOME`
  (`~/.local/bin` checked unchanged before and after), the Director key and the Gateway's address in its configuration,
  and a hosted-desktop list holding `relay-proof` on port 3140: my own webtop container again (`boxes-relay-desktop`, the
  same flags as before). `demo-desktop-soren` and `teams-test-director` were not touched.
- **The build** was this branch's fixes carried onto the merged tree described under "The self-host run" (this branch
  plus the list branch plus the wiring), so the run used the real list, push, `TryLocate`, Boxes switch and `BoxIdRule`.
  The relay tests passed there too, 119 of 119.

**What happened** (the same scripts as the self-host run, `drive-relay.cjs`, `refusals.sh`, `director-gone.cjs`):

- `GET /boxes` with the account's key listed `relay-proof` on the Director, online. The ticket came back 200; the
  browser crossed to the desktop host and landed on `http://localhost:7996/boxes/relay-proof/desktop/` with the cookie
  (`httpOnly`, `secure`, `SameSite=Strict`, the box's path) and `document.cookie` empty. Chromium's own console said
  `Clear-Site-Data header on '.../desktop/?ticket=<redacted>': Cleared data types: "storage".`
- `hosted/relay-1-desktop.png`, `hosted/relay-2-after-click.png`, `hosted/relay-3-after-typing.png`: the desktop rendered,
  took a click, and took typing - through the hosted Gateway.
- All 19 requests the page made answered 200; the WebSocket carried 3,604 messages, 70.6 megabytes, the largest 272,024
  bytes. A 64-megabyte upload went through in 10.0 seconds, the 8-megabyte download came back with every byte checked.
- **The Gateway's log has no `GATEWAY BUG - command DROPPED` line at all**, against 13 ticket requests and 32 streams
  opened at the Director: every desktop request and socket was relayed inside the box session's account.
- **The refusals** (`hosted/refusals.txt`): the same 15 answers as the self-host run, now with the plain sentence on the
  desktop host's 404s - "This address serves only box desktops. The DevThrottle Gateway is at
  https://gateway.relay-proof.invalid; use that address instead." - and `Clear-Site-Data: "storage"` on the exchange.
  The Gateway logged each refusal as designed: `desktop host REFUSED GET /sessions user-agent="curl/8.5.0"` and, for a
  path carrying a box id, `desktop host REFUSED POST /boxes/ (box=relay-proof) user-agent="curl/8.5.0"`.
- **The Director going away** (`hosted/director-gone.json`, `hosted/director-gone-*.png`): stopped on its named signal
  with the desktop open; 0.3 seconds later the page's WebSocket closed with **1001, "The Director hosting this box
  disconnected."**
- **The logs** (`hosted/gateway-log.txt`, `hosted/director-log.txt`): every relay and Boxes line from both processes. No
  key, ticket or database password is in any file of the folder (checked).

**One thing seen on the way, not this leg's:** my first stop request was written with a trailing newline. The Director
took the file, could not read the time in it, and dropped it without a log line, so the Director did not stop and
nothing said why. Written again without the newline, it worked. `LifecycleSignal`'s Unix listener parses the stamp with
`DateTimeStyles.RoundtripKind` alone (trailing white space refused) and returns quietly on a parse failure. Reported to
the Tech Lead, not changed here.

**Clean-up.** The Director stopped on its named signal (no force kill); the hosted Gateway was stopped with SIGTERM;
`boxes-relay-desktop` and `boxes-relay-postgres` were removed with their volumes; the scratch roots, the throwaway
signing key and the two log files the hosted launcher wrote under `/tmp/devthrottle/logs/director/` were deleted; the
owner's containers and `~/.local/bin` were unchanged.

### The self-host run (19 October 2026)

**Set-up.** A local test Gateway built from this work (self-host, its own port 7996, its own scratch root), with
`CC_BOXES_DESKTOP_ORIGIN=http://localhost:7996` (the desktop host) and `DEVTHROTTLE_BOXES_TENANT_IDS=local`; the main host
was `http://127.0.0.1:7996`, a different site to the browser. A scratch slot-5 test Director (its own `CC_DIRECTOR_ROOT`
and its own `HOME`, so `~/.local/bin` was not touched - checked before and after) connected to it, with a
hosted-desktop list holding one box, `relay-proof`, on port 3140: **my own** `lscr.io/linuxserver/webtop:ubuntu-xfce`
container (`boxes-relay-desktop`, the spike's lockdown flags, `--dns 1.1.1.1`). `demo-desktop-soren` (3110) and
`teams-test-director` (3000) were not touched.

**The build that ran is this branch merged with the hosted-desktop list branch** (`boxes/phase1-hosted-desktop-list`,
then not yet merged to main) **plus the wiring between them** - so the run used the real list, the real push, the real
`TryLocate` and the real Boxes switch. That wiring is now this branch's own last commit; see "The dependency" below.

**What the browser did** (`drive-relay.cjs`, headless Chromium build 1223, `relay-capture.json`):

- The script, signed in with the test Gateway's token like the Cockpit, asked the main host for a ticket; a page on the
  main host navigated to the ticket address on the desktop host - a cross-site navigation, the case the strict cookie
  must survive. The browser landed on `http://localhost:7996/boxes/relay-proof/desktop/` with the cookie set
  (`httpOnly: true, secure: true, sameSite: Strict, path: /boxes/relay-proof/desktop/`) and `document.cookie` empty.
- `relay-1-desktop.png`: the Xfce desktop rendered through the relay. `relay-2-after-click.png`: the Applications menu,
  opened by a click. `relay-3-after-typing.png`: Alt+F2 opened Application Finder and the text
  `typed-through-the-gateway-relay` arrived.
- Every request the page made stayed under the prefix and answered 200: the page, its three assets, `icon.png`,
  `manifest.json`, `api/status`, `api/upload`, `api/files/`, the download, and the one WebSocket
  `ws://localhost:7996/boxes/relay-proof/desktop/api/websockets` (3,781 messages received, 70.8 megabytes, the largest
  268,025 bytes).
- **Upload:** 64 megabytes (67,108,864 bytes) in one POST: 200 in 10.2 seconds; an 8-megabyte file too; both in the
  file list. **Download:** the 8-megabyte file back, 200, every byte checked, at the ceiling.
- `relay-4-terminal.png`, `relay-5-browsing.png`, `relay-6-flood.png`: the screens the usage was measured on.

**The refusals** (`refusals.sh`, `refusals.txt`), as plain requests: signed out, the ticket route 401; signed out, the
desktop page 401 and the WebSocket upgrade 401; the Gateway's own sign-in sent to the desktop host 401; an unknown box id
404; not a box id 400; the desktop route on the main host 404; the session list and the ticket route on the desktop
host 404; `/healthz` on the desktop host 200; a ticket redeemed (200, the cookie, the page that moves on without the
ticket); the same ticket again 403; the cookie opens its own box (200) but not another box's path (401); the no-slash
form redirects to `/boxes/relay-proof/desktop/?x=1` on the same host.

This run came before the Tech Lead's two additions of 20 October 2026 (the refusal's plain sentence and log line, and
`Clear-Site-Data: "storage"` on the exchange), so `refusals.txt` shows the desktop host's 404s with an empty body and the
exchange without that header; the hosted run above shows both. A self-host Gateway always works in the one local
account, so this run could not have shown review finding 1; that is what the hosted run is for.

**The Director going away mid-use** (`director-gone.cjs`, `director-gone.json`, `director-gone-*.png`): with the desktop
open, the test Director was stopped with its named signal. Half a second later a WebSocket in the page closed with
**1001, "The Director hosting this box disconnected."** The Gateway log shows its side
(`ending: status=1001, reason=The Director hosting this box disconnected.`, the stream removed, `live=0`), and the page's
own reconnect attempts then met 404 (no connected Director hosts the box). The close was not "clean" from the browser's
point of view (`wasClean: false`): the Gateway sends the close frame and ends the connection without waiting for the
browser's reply. The code and the reason arrive regardless.

**The logs** (`gateway-log.txt`, `director-log.txt`): every relay line from both processes - the opens, the
per-minute and closing usage lines per stream and per box, the list's `Resolve` lines, the refusals. The access log
line for the ticket address reads `GET /boxes/relay-proof/desktop/?[redacted: desktop ticket]`.

**Clean-up.** The Director stopped on its named signal (no force kill); the test Gateway (the one process this run
started, on its own root and port) was stopped; `boxes-relay-desktop` was removed; the scratch root was deleted; the
owner's containers and `~/.local/bin` were unchanged.

## The dependency: the hosted-desktop list

The relay was first built against narrow stand-ins, as the brief said, while `boxes/phase1-hosted-desktop-list` was
open. That branch merged as pull request #4271 (`2934dd163`), and this branch was then rebased onto main. The stand-ins
are deleted; the relay now uses the list's own code: `HostedDesktopRegistry.Resolve` on the Director (the one way from a
box id to a port), `PushedHostedDesktopStore.TryLocate` and `BoxesSwitch.IsOn` on the Gateway, `BoxIdRule` on both, and
the ticket route mapped inside `BoxesGate.Group`. The wiring is the branch's last commit, an ordinary one.

**The rebase's two conflicts** (review finding 2), resolved in the rebase itself: `GatewayStreamClient`'s and
`DirectorHub`'s constructors each gained a parameter at the same position on both branches. Both were kept - on the hub
the list's `PushedHostedDesktopStore` and the relay's `DesktopRelayRegistry`; on the client the list's push source and
the relay's `HostedDesktopRegistry`, which `ControlApiHost` passes as the same list it pushes. The interim patch file is
deleted. Between the list branch the hosted run was built on and what merged, the list's production code is identical
(only its tests changed), so the hosted run stands for the rebased branch.

One point where the list's contract and this leg differ, raised with the Tech Lead: the contract says the desktop routes
must be mapped inside `BoxesGate.Group`. They cannot be: that gate reads the account from the Gateway's sign-in, which by
design never reaches the desktop host, so it would refuse every desktop request. The desktop host asks the same
`BoxesSwitch` of the account the box session belongs to, with the same 404. **Accepted by the Tech Lead (20 October
2026):** the desktop routes stay outside `BoxesGate.Group`, with the `BoxesSwitch` ruling taken from the box session.

## For the Tech Lead and the Delivery Lead

1. **`/healthz` stays on the desktop host** - accepted by the Tech Lead. The deploy, rollback and staff-email workflows
   use `GATEWAY_URL=https://devthrottle-gw.azurewebsites.net` and read `/healthz` there.
2. **Directors on the Azure name.** The Tech Lead checked: every Director on devlinux dials gateway.devthrottle.com, and
   so does the code default. Everything else on the desktop host stays refused; the refusal names the right address and
   each one is logged with method, path and user agent (see "How it works"), which is how a stray Director or phone
   shows up after deploy.
3. **To turn it on in production:** `CC_BOXES_DESKTOP_ORIGIN=https://devthrottle-gw.azurewebsites.net` and the owner's
   account in `DEVTHROTTLE_BOXES_TENANT_IDS`, through the deploy skill only. Unset, nothing changes.
4. **For the Cockpit:** `POST /boxes/{boxId}/desktop-ticket` on the main host answers `{url, expiresAtUtc}`; open `url`
   verbatim (a new tab or the same tab; the 60-second ticket is single-use, so ask for a fresh one on every Open).

## What this does not cover

- **Azure and the real network.** Everything here ran on one machine: the latencies and throughput are loopback. The
  relay check from outside the house network is the Tech Lead's and the Delivery Lead's, after the deploy. Two things
  to watch there: that App Service passes WebSocket upgrades on the Azure name, and that its front end accepts a
  64-megabyte request body.
- **A phone.** The browser was desktop Chromium; the page's touch code was not exercised.
- **Latency under the ceiling.** When a screen produces more than 8 megabits, up to a window of video can queue; the
  lag that causes was not measured. The phase 2 encoder settings are what keep it from happening in ordinary use.
- **The Director-side refusal in the live run.** The hosted list feeds both the Gateway and the Director from one file,
  so a real setup cannot fool the Gateway; the live run shows the Gateway's 404 for an unknown box, and the Director's
  own refusal is proven by `DirectorDesktopRelayHandlerTests` asking the Director directly, and by
  `DesktopPage_BoxNotOnTheDirectorsList_IsRefusedAtTheDirector` with a fooled Gateway.
- **More than one Gateway instance.** Tickets and desktop sessions live in the one Gateway's memory.
- **The Cockpit's own sign-in on a hosted Gateway.** The hosted run asked for its tickets with a key from the Director
  door (see the hosted run's set-up), so the Cockpit's enrollment and the remote-access rule on browser keys were not
  exercised by it; the ticket route itself is tested with browser keys of two accounts in `DesktopRelayLegTests`.

## Developer's answers to the reviews

Both review files are in `reviews/`, unchanged: `phase1-relay-review.md` (Fable) and `phase1-relay-review-codex.md`
(Codex).

**Codex: no findings.** Nothing to answer.

**Fable, finding 1 - on the hosted Gateway every desktop request and WebSocket answers 503. ACCEPTED, fixed.** The
reviewer is right, and the cause is exactly as written: the desktop host runs before the sign-in and the tenant-scope
middleware, and `GatewayHost.SendCommandAsync` drops a command sent with no scope. `ServeDesktopHostAsync` now enters the
box session's account (`_tenantBoundary.EnterScope(session.Tenant)`, the `RingDirectorAsync` pattern) as soon as the
cookie is checked, so the Boxes switch, the host lookup and the whole relay run inside it. The rig's command send now
drops an unscoped command and finds the Director only in the scope's own account, as a hosted Gateway does, so the
tests pin the hosted shape: with the scope line removed, 18 of them fail (the reviewer's experiment, kept). And the
end-to-end proof was re-made on a hosted Gateway (above): no dropped command in its log, the desktop live. Commit
`c66422a3d`.

**Fable, finding 2 - the wiring patch presupposes an unrecorded conflict resolution. ACCEPTED, resolved by the plan the
Tech Lead set, and done.** #4271 merged; this branch was rebased onto main, the two conflicts were resolved in the
rebase (written down under "The dependency"), the wiring is an ordinary commit (`4f91c2831`), and the patch file is
deleted.

**Fable, finding 3 - a Closed frame is not charged to the window. ACCEPTED, fixed the second way the review offers.**
A Closed frame now costs nothing, like Credit, on both sides (`DesktopRelayLimits.CostOf`). It is the last frame of a
stream and carries no data to hold, and the sender already sends it without waiting for window, so not counting it at
the receiver makes the two sides agree. Charging it instead would have made a close wait for credit from a far side
that may be stalled, which is the very moment a close must get through. A test fills the window exactly (21 full frames
and one of 14,976 bytes, 1,048,576 bytes in all) with the browser stalled, then closes with 4001: the browser gets
4001 and the far side's reason, not 1011. With the old cost it fails. Commit `34dcfef01`.

**Fable's observations.**
- *No browser header reaches the box's WebSocket upgrade, though a comment says a Selkies token cookie "still reaches
  the box".* True; the comment now says it reaches the box on an HTTP request only, and that the upgrade carries only
  the address and the subprotocols, which Selkies does not need (the spike). The same comment still said the desktop is
  served on the Gateway's own origin; corrected too.
- *The traffic meter does not attribute desktop bytes to the account.* True, and accepted for phase 1: the relay's own
  per-stream and per-box usage lines are the record of desktop spend, as the spend line asked.
- *The ticket route applies the Boxes switch itself rather than through `BoxesGate.Group` until the wiring.* Done in
  the wiring: the ticket route is mapped inside the group; the desktop host stays outside it (accepted by the Tech Lead).

**The list review's observation 1 - a refused desktop request must log the cleaned box id, never the raw path.
ACCEPTED, done.** A box id that fails the rule is logged through `BoxIdRule.ForLog`, and the desktop host's refusal of any other route
writes, for a path under `/boxes/`, only the cleaned box id (`POST /boxes/ (box=relay-proof)`), never the path as
received. Every other box id the relay logs has already passed the rule.
