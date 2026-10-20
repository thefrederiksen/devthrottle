# Factory Boxes phase 1 spike - Selkies under a path prefix

Mission: Factory Boxes (issue #4258), mission document section 5, item 1 (the open design point).
Run on devlinux, 19 October 2026 (the container clocks read 20 October, Coordinated Universal Time).
Image: `lscr.io/linuxserver/webtop:ubuntu-xfce`, build `b0345a66-ls4` (built 2 October 2026), the
same image `demo-desktop-soren` runs.

## The question

Does the Selkies desktop work when it is served under a path prefix such as
`/boxes/{id}/desktop/`, or does each box need its own host name?

## The answer

**Path prefix. It works without patching any file in the image.** The page builds every address it
uses from the folder of its own address, so a reverse proxy that strips `/boxes/test-box/desktop` and
forwards the rest to the desktop's `/` is enough. The desktop rendered, opened its menu on a click, and
took a keyboard shortcut and typed text, all through the prefix. Every request the page made,
including its one WebSocket, stayed under the prefix: the proxy's catch-all for anything outside the
prefix was never hit by the page.

A host name per box is **not** needed. No wildcard certificate, no Azure custom domain, nothing to
ask the owner.

What the relay leg must handle is not the prefix. It is the **size and rate of the video stream**
(below): frames reach 244 kilobytes and a busy screen streams about 55 to 87 megabits a second, while
today's tunnel caps a frame at 48 kilobytes.

## What was run

Nothing touched `demo-desktop-soren` (127.0.0.1:3110) or `teams-test-director` (127.0.0.1:3000).
All tests used my own containers, removed at the end.

```bash
# 1. My own desktop, with the same lockdown as demo-desktop-soren (capabilities, no-new-privileges, DNS).
docker run -d --name boxes-spike-desktop -p 127.0.0.1:3120:3000 --dns 1.1.1.1 --shm-size 1g \
  --cap-drop ALL --cap-add CHOWN --cap-add DAC_OVERRIDE --cap-add FOWNER --cap-add KILL \
  --cap-add SETGID --cap-add SETUID --security-opt no-new-privileges \
  -e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC -e TITLE="Boxes spike" \
  lscr.io/linuxserver/webtop:ubuntu-xfce

# 2. A second desktop told its base path by the image's own setting (the SUBFOLDER variant).
docker run -d --name boxes-spike-desktop-subfolder -p 127.0.0.1:3121:3000 ...same flags... \
  -e SUBFOLDER=/boxes/test-box/desktop/ lscr.io/linuxserver/webtop:ubuntu-xfce

# 3. The prefix proxy: nginx on the host network, two server blocks.
#    127.0.0.1:3130 strips the prefix and forwards to 3120   (phase1-spike/prefix-proxy.conf)
#    127.0.0.1:3131 forwards the path unchanged to 3121      (phase1-spike/subfolder-proxy.conf)
docker run -d --name boxes-spike-proxy --network host \
  -v .../prefix-proxy.conf:/etc/nginx/conf.d/default.conf:ro \
  -v .../subfolder-proxy.conf:/etc/nginx/conf.d/subfolder.conf:ro nginx:1.27-alpine

# 4. Headless Chromium (Playwright core, Chromium build 1223) drives the page through the proxy.
node drive.js  http://127.0.0.1:3130/boxes/test-box/desktop/ <out> prefix     # load, click, type
node drive2.js http://127.0.0.1:3130/boxes/test-box/desktop/ <out> motion     # 1920x1080, heavy motion, upload, files
node drive.js  http://127.0.0.1:3131/boxes/test-box/desktop/ <out> subfolder  # the SUBFOLDER variant
```

The heart of the prefix proxy (`phase1-spike/prefix-proxy.conf` is the whole file):

```nginx
location = /boxes/test-box/desktop { return 301 /boxes/test-box/desktop/; }
location /boxes/test-box/desktop/ {
  proxy_pass http://127.0.0.1:3120/;          # trailing slash = strip the prefix
  proxy_http_version 1.1;
  proxy_set_header Upgrade $http_upgrade;
  proxy_set_header Connection $connection_upgrade;
  proxy_buffering off;  proxy_request_buffering off;  client_max_body_size 0;
  proxy_read_timeout 3600s;
}
location / { return 404 "outside the box prefix\n"; }   # tripwire: the page must never land here
```

I also read the page's own code (`/usr/share/selkies/web/index.html`, `assets/index-CPWh3fQ6.js`,
`assets/selkies-core-BbKps5RD.js`) for every way it forms an address, so the result does not rest
only on the paths one browser session happened to take.

## Every address the page uses

From the browser capture (`phase1-spike/prefix-capture.json`, `motion-capture.json`) and the proxy log
(`phase1-spike/proxy-access-prefix.log`):

| Request | Kind | Status through the prefix |
|---|---|---|
| `/boxes/test-box/desktop` (no trailing slash) | redirect | 301 to `/boxes/test-box/desktop/` |
| `/boxes/test-box/desktop/` | page | 200 |
| `.../assets/index-CPWh3fQ6.js` | script | 200 |
| `.../assets/index-D97fjY6g.css` | style | 200 |
| `.../assets/selkies-core-BbKps5RD.js` | script | 200 |
| `.../icon.png` | icon | 200 |
| `.../manifest.json` | manifest | 200 |
| `.../api/status` | fetch (which stream mode the server is in) | 200 |
| `.../api/websockets` | **WebSocket** | 101, held open |
| `.../api/upload` | file upload, `POST`, raw body | 200, 1,048,576 bytes landed in the box's Desktop folder |
| `.../api/files/` and `.../api/files/<name>` | file browser page and download | 200, download returned all 1,048,576 bytes |
| `blob:` workers (six) | made in the page from strings | no network request |

Not requested by the page in either run, and why each is still safe under a prefix:

- **Service worker:** none. The code registers no service worker; `navigator.serviceWorker.controller`
  was empty.
- **Cookies:** none set (`document.cookie` was empty). The code sets one cookie only when a Selkies
  token is in use, and scopes it to `<page folder>/api/`, which is prefix-relative.
- **Gamepad layouts** `jsdb/<os>/<id>.json`: relative, only when a gamepad is plugged in.
- **Print** `api/print/<name>` and **TURN settings** `<page folder>/api/turn`: both prefix-relative.
- **App catalogue:** `https://raw.githubusercontent.com/linuxserver/proot-apps/...`, an outside
  address, fetched only if the user opens the Apps panel. It never goes through the relay.

The only absolute path anywhere is `"start_url": "/"` in `manifest.json`, which matters only if a user
installs the page as an app. It does not affect the desktop in a browser tab.

How the code builds addresses (both bundles have the same helper):

```js
function he(){ let e = window.location.pathname;
  return e.substring(0, e.lastIndexOf('/') + 1).replace(/\/$/, '') }   // the page's own folder
// WebSocket: (http: ? 'ws://' : 'wss://') + location.host + <folder> + '/api/websockets'
// fetches:   he() + '/api/status',  he() + '/api/turn',  new URL('api/upload', location.href)
```

**Consequence: the page must be served with the trailing slash.** At `/boxes/test-box/desktop`
(no slash) the folder would be `/boxes/test-box` and every call would miss. The route must redirect
the no-slash form to the slash form, as the proxy above does.

## The channels

There is **one WebSocket per viewer**: `ws(s)://<host>/boxes/test-box/desktop/api/websockets`. It
carries everything: video, speaker audio, keyboard and mouse, clipboard, gamepad, microphone, and
control messages. From the capture:

- First frames sent by the page are text: `_gz,1`, `SETTINGS,{...}`, `cr` (clipboard request),
  `START_AUDIO`, `r,1280x800,primary`. Video and audio come back as binary frames. The relay must keep
  each message's type (text or binary) as it is.
- **Microphone:** in WebSocket mode the page captures audio in an audio worklet and sends it as
  binary frames on the same socket (the server can answer `MICROPHONE_DISABLED` on it). Not exercised
  live, because headless Chromium has no microphone. It needs a secure page (HTTPS), which the Gateway
  is.
- **Clipboard:** on the same socket (`cr`, `REQUEST_CLIPBOARD`). The browser's clipboard access also
  needs HTTPS.
- **File upload and download:** plain HTTP to `api/upload` and `api/files/`, covered above. A file
  over 64 megabytes is sent in 64-megabyte `POST`s carrying `X-Upload-Id`, `X-Upload-Offset`,
  `X-Upload-Total` and `X-Upload-Final` headers, so the relay must allow request bodies of at least 64
  megabytes and pass those headers through.
- **WebRTC mode** (not the default, and not used here): signalling would go over a WebSocket at
  `<folder>/api/<app>/signaling/` (still prefix-relative), but the media would then travel peer to peer
  over UDP, around the Gateway. It must stay off: the image runs with
  `SELKIES_VIDEO_STREAMING_MODE=false`, and `api/status` reported `websockets`.

## Frame sizes and rate (what the relay leg must carry)

| Run | Screen | What was on it | Largest frame received | Typical frame | Data rate |
|---|---|---|---|---|---|
| prefix | 1280x800 | idle desktop, menu, typing | 50,963 bytes | small | 674,574 bytes over the whole run (about 20 seconds) |
| motion, first run | 1920x1080 | full-screen terminal flooding text | 244,156 bytes | 180,586 bytes (median) | 102 megabytes in 10 seconds, about 82 megabits a second |
| motion, rerun (committed) | 1920x1080 | same | 231,245 bytes | 174,134 bytes (median) | 68 megabytes in 10 seconds, about 55 megabits a second |

`motion-capture.json` is the rerun. The first motion run's numbers are from the run before it, whose
capture was overwritten when the download path in `drive2.js` was corrected (see "One thing to
explain in the log" below). A real user's desktop sits between the idle and the flood rows. The flood
is the worst case on purpose.

Sent frames (browser to box) were all under 300 bytes in these runs. Uploads are separate HTTP
requests, not WebSocket frames.

## The image's own base-path setting (SUBFOLDER)

The image has one: `SUBFOLDER`. At start it is written into the image's own nginx configuration
(`/etc/s6-overlay/s6-rc.d/init-nginx/run` replaces the word `SUBFOLDER` in `/defaults/default.conf`),
so the desktop is served at that path instead of `/`. With
`SUBFOLDER=/boxes/test-box/desktop/` and a proxy that forwards the path unchanged
(`subfolder-proxy.conf`), the desktop worked the same way: the same requests, all under the prefix, the
WebSocket at `.../api/websockets`, and the desktop took the click and the typing
(`subfolder-1..3-*.png`, `subfolder-capture.json`).

It **does not change the answer**, and I recommend **not** using it:

- It writes the box id into the box at start-up. A box would then have to know its own Gateway route,
  and could never be moved or renamed without a restart.
- Without a trailing slash it answers 404 instead of redirecting, and at `/` it serves the stock
  "Welcome to nginx!" page.
- Stripping the prefix keeps the box ignorant of where it is published: the Director forwards
  `/<rest>` to `127.0.0.1:<port>`, which is exactly what the mission's allow-list (box id to local port)
  already describes.

## Things that break only under a prefix

- **The trailing slash**, above. It is the only one found.
- **Absolute redirects:** the desktop's own nginx sends none on the paths used. The proxy's own 301
  for the no-slash form must keep the Gateway's host; send a relative `Location` or the Gateway's
  public host, never `127.0.0.1`.
- **Cookie path:** not used here. If Selkies tokens are ever turned on, the cookie is prefix-relative.
- **Service worker scope:** no service worker.

## Two findings for later phases (not the prefix question)

1. **The desktop has no sign-in and accepts any origin.** The image runs with
   `SELKIES_ALLOWED_ORIGINS=*` and `SELKIES_ENABLE_BASIC_AUTH=false`. A WebSocket upgrade to
   `127.0.0.1:3120/api/websockets` got `101 Switching Protocols` with `Origin: https://gateway.devthrottle.com`,
   with `Origin: https://evil.example`, and with no `Origin` header at all. That is helpful for the
   relay (the Director's `Host: 127.0.0.1:<port>` and the browser's Gateway origin need not match), but
   it means **whoever reaches the port controls the desktop**. The only protection is that the port is
   bound to 127.0.0.1 and that the Gateway's sign-in and the Director's allow-list sit in front. For
   phase 2's network wall: boxes on one Docker network can reach each other's port 3000 directly, so
   the wall must stop box-to-box traffic too, not only box-to-house.
2. **The relay must not share the tunnel blindly.** At 55 to 87 megabits a second, one busy desktop
   would carry far more data than every terminal stream combined on the same Director connection. See
   the relay leg's requirements below.

## What the relay leg must do as a result

1. **Route:** `/boxes/{id}/desktop/` with the trailing slash; redirect `/boxes/{id}/desktop` to it
   with a relative or Gateway-host `Location`.
2. **Strip the prefix** and send the remainder (path and query string) to the Director, which forwards
   it to the `127.0.0.1:<port>` its allow-list holds for that box id. Do not set `SUBFOLDER`; nothing in
   the response needs rewriting: no HTML, no script, no headers.
3. **Paths to forward:** everything under the prefix. In practice that is the page, `assets/*`,
   `icon.png`, `manifest.json`, `api/status`, `api/websockets` (WebSocket), `api/upload` (`POST`),
   `api/files/*`, `api/print/*`, `api/turn`, `jsdb/*`. A fixed allow-list of these is possible but
   brittle across image updates. Forwarding the whole subtree of one allow-listed box is the safer
   rule.
4. **WebSocket upgrade:** the desktop needs nothing special. The Director's upstream request needs
   `Upgrade: websocket`, `Connection: Upgrade`, `Sec-WebSocket-Version`, and a `Sec-WebSocket-Key`.
   `Host` may be `127.0.0.1:<port>` and `Origin` may be anything (proven above). No subprotocol is
   requested. Keep text and binary messages distinct. The connection is long-lived: no idle timeout
   shorter than an hour (the page sends a 50-millisecond acknowledgement and 500-millisecond metrics,
   so it is never silent while open).
5. **Frame size:** messages from the box reach **at least 244 kilobytes** each; today's tunnel caps a
   frame at 48 kilobytes (`DirectorStreamLimits.MaxBinaryFrameBytes`,
   `src/CcDirector.Gateway.Contracts/DirectorUpStreamMessages.cs:78`, and the hub's
   `MaximumReceiveMessageSize` is set from it). The leg must **split one WebSocket message into several
   tunnel frames and reassemble it at the Gateway** before handing it to the browser, not raise the cap.
   Raising it would let one desktop frame block the shared connection for longer.
6. **Rate and fairness:** plan for about 90 megabits a second per busy viewer. Desktop frames must not
   starve the terminal streams and commands on the same Director connection. Give the desktop leg its own
   bounded queue and backpressure: drop or slow the desktop when the queue is full, never the terminal.
   It is worth asking in the leg's design whether the desktop should ride a second connection of its
   own. Selkies already runs its own flow control (the page sends a backpressure acknowledgement every
   50 milliseconds), so a relay that delays those acknowledgements slows the desktop rather than
   queueing without limit; that is the behaviour to preserve.
7. **Request bodies:** `POST api/upload` bodies up to 64 megabytes each, streamed, with the
   `X-Upload-*` headers passed through. Downloads from `api/files/` are plain responses of any size.
8. **Never offer WebRTC mode** through the Gateway: its media bypasses the relay over UDP.

## Evidence files (`phase1-spike/`)

| File | What it is |
|---|---|
| `prefix-proxy.conf` | the prefix-stripping proxy (ports 3130 to 3120) |
| `subfolder-proxy.conf` | the pass-through proxy for the SUBFOLDER variant (3131 to 3121) |
| `drive.js`, `drive2.js` | the Playwright scripts that drove the page and recorded every request and WebSocket frame |
| `prefix-1-desktop.png` | the Xfce desktop rendered through the prefix |
| `prefix-2-after-click.png` | the Applications menu, opened by a click through the prefix |
| `prefix-3-after-typing.png` | Alt+F2 opened Application Finder, and the text `typed-through-the-prefix` arrived |
| `motion-terminal-flood.png` | the 1920x1080 worst-case screen used for the frame-size measurement |
| `prefix-capture.json` | every request, the WebSocket and its frame counts, the page's console |
| `motion-capture.json` | frame sizes under motion, upload, file list, download |
| `subfolder-1..3-*.png`, `subfolder-capture.json` | the same three checks with `SUBFOLDER` set |
| `proxy-access-prefix.log`, `proxy-access-subfolder.log` | nginx's own log of every request through each proxy |

### One thing to explain in the log

`proxy-access-prefix.log` contains, besides the page's requests:

- one `404 GET /websocket`. I made this request on purpose to show the catch-all tripwire answers. The
  page never requested anything outside the prefix.
- one `404 GET .../api/files/Desktop/spike-upload.bin`. This was my test script's mistake: the file
  browser's root is already the Desktop folder. The corrected script (`drive2.js` as committed) fetches
  `.../api/files/spike-upload.bin` and got 200 with all 1,048,576 bytes.

## What this does not cover

- The real Gateway and the real tunnel: the proxy here is nginx on the same machine. The headers, paths
  and frame sizes carry over. Latency and throughput through Azure do not, and the relay check in
  mission section 7 still has to run from outside the house network.
- A phone browser: the runs used desktop Chromium. The page's touch and gamepad code is present but was
  not exercised.
- HTTPS: the proxy spoke plain HTTP. Under HTTPS the page switches to `wss://` on its own (the code
  picks from `location.protocol`), and microphone and clipboard access become available.
