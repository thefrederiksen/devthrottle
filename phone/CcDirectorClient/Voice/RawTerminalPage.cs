using System.Text.Json;

namespace CcDirectorClient.Voice;

/// <summary>
/// Builds the self-contained HTML page the Terminal tab hosts in a WebView. The page
/// runs a real xterm.js terminal emulator fed by the session's raw PTY byte stream
/// over the WebSocket <c>GET {director}/sessions/{sid}/stream</c> -- the exact
/// mechanism the web client (session-view.html "Raw terminal" tab) uses.
///
/// Why a WebView and not a native Label: the old Terminal tab polled the server's
/// ANSI-cleaned grid snapshot (<c>/buffer?raw=false</c>) and dumped it into a MAUI
/// Label. A Label cannot apply cursor moves, so Claude Code's constantly-repainting
/// TUI stacked half-drawn frames as overlapping ghost lines. A byte stream applied in
/// order to a terminal emulator can't desync, so the screen stays coherent.
///
/// The xterm assets (xterm.js / xterm.css / xterm-addon-canvas.js) are loaded from the
/// owning Director, which already serves them next to the stream endpoint -- no CDN
/// (the phone reaches the Director over Tailscale and may have no public-internet
/// path) and nothing to bundle in the app. The page is read-only: typing still flows
/// through the existing POST /prompt control buttons, never from here.
///
/// Layout (issue #244): the WebView is sized by MAUI to fill the whole tab area (no
/// fixed height, no outer ScrollView), so this page is the ONLY scroll container and
/// owns both axes -- vertical scrollback via xterm's viewport, horizontal pan via
/// #wrap. A "Fit width" toggle shrinks the xterm FONT (real layout change, so the
/// scroll geometry stays correct) until the PTY's full column width maps onto the
/// WebView width; one tap returns to 1:1 for full-resolution reading. A/-A+ buttons
/// and two-finger pinch set an explicit zoom font that overrides fit-width (same
/// font-not-scale mechanism), so the text can be enlarged for readability and panned.
///
/// Pure (no MAUI/Android dependency) so it is unit tested off-device.
/// </summary>
public static class RawTerminalPage
{
    /// <summary>
    /// Build the terminal page for <paramref name="sessionId"/> on the Director at
    /// <paramref name="directorBase"/> (e.g. <c>http://10.0.2.2:7883</c> on the
    /// emulator, <c>https://host.tailnet.ts.net:7883</c> in production). The WebSocket
    /// scheme is derived from the base scheme (http-&gt;ws, https-&gt;wss).
    /// </summary>
    public static string BuildHtml(string directorBase, string sessionId)
    {
        var baseUrl = (directorBase ?? "").TrimEnd('/');
        // JSON-encode the values before embedding them in the inline script so a
        // hostile/odd base URL or id can never break out of the string literal.
        var baseJson = JsonSerializer.Serialize(baseUrl);
        var sidJson = JsonSerializer.Serialize(sessionId ?? "");

        return Template
            .Replace("__BASE__", baseUrl)
            .Replace("__BASE_JSON__", baseJson)
            .Replace("__SID_JSON__", sidJson);
    }

    // The page mirrors the web client's Raw-terminal logic (connectRawStream / fitRawTerm
    // from session-view.html), trimmed to a single full-viewport terminal that connects
    // on load. The PTY column count is whatever the desktop pane set it to (reported in a
    // size frame). In "fit width" mode (default) the font shrinks so all columns fit the
    // WebView width with no horizontal pan; tapping the toggle restores 1:1 (full size,
    // #wrap pans horizontally). xterm's own viewport owns vertical scrollback in both.
    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<link rel="stylesheet" href="__BASE__/xterm.css">
<style>
  html, body { margin: 0; padding: 0; background: #1e1e1e; height: 100%; overflow: hidden; }
  /* The single scroll container. The grid renders at the PTY's exact geometry (see
     applyFont/fit); when that grid is wider or TALLER than this WebView, #wrap scrolls
     to reveal the rest. The row count is never shrunk below the PTY's, so Claude Code's
     cursor-relative redraws overwrite the input box in place instead of stacking ghost
     copies down the history. In fit-width mode the font is small enough that no
     horizontal scroll is needed. */
  /* touch-action pan-x pan-y lets #wrap still scroll by drag but hands two-finger
     pinches to our JS zoom handler instead of the WebView's own page zoom. */
  #wrap {
    position: absolute; inset: 0;
    overflow-x: auto; overflow-y: auto;
    -webkit-overflow-scrolling: touch;
    touch-action: pan-x pan-y;
  }
  /* Bottom padding reserves a strip for the floating controls (#zoom / #fit) so the
     live input row scrolls clear of them instead of hiding underneath. fit() subtracts
     this padding from the available height, so the grid never extends under the buttons. */
  #xterm { padding: 6px 0 56px 8px; width: max-content; }
  #msg {
    position: absolute; left: 8px; top: 8px;
    color: #8a93a6; font-family: monospace; font-size: 12px;
  }
  /* In-page controls (self-contained; no MAUI round-trip). They sit over the bottom
     corners so they never steal terminal real estate, and are big enough to thumb-tap.
     #fit toggles fit-width<->1:1; #zoom is the manual A-/A+ pair (pinch also works). */
  #fit {
    position: absolute; right: 12px; bottom: 12px; z-index: 5;
    background: rgba(20,27,46,0.92); color: #e6eaf2;
    border: 1px solid #2a3550; border-radius: 9px;
    font-family: monospace; font-size: 13px; padding: 9px 13px;
    -webkit-user-select: none; user-select: none; touch-action: manipulation;
  }
  #zoom { position: absolute; left: 12px; bottom: 12px; z-index: 5; display: flex; gap: 8px; }
  .zbtn {
    background: rgba(20,27,46,0.92); color: #e6eaf2;
    border: 1px solid #2a3550; border-radius: 9px;
    font-family: monospace; font-size: 16px; line-height: 1; padding: 8px 15px;
    -webkit-user-select: none; user-select: none; touch-action: manipulation;
  }
</style>
</head>
<body>
<div id="wrap"><div id="xterm"></div></div>
<div id="msg">Connecting to terminal&hellip;</div>
<div id="zoom"><div id="zoomout" class="zbtn">A&minus;</div><div id="zoomin" class="zbtn">A+</div></div>
<div id="fit">1:1</div>
<script src="__BASE__/xterm.js"></script>
<script src="__BASE__/xterm-addon-canvas.js"></script>
<script>
(function () {
  var BASE = __BASE_JSON__;
  var SID = __SID_JSON__;
  var WSBASE = BASE.replace(/^http/, "ws");   // http->ws, https->wss
  var BASE_FONT = 13;                          // 1:1 (actual-size) font, in px
  var hostEl = document.getElementById("xterm");
  var wrapEl = document.getElementById("wrap");
  var msgEl = document.getElementById("msg");
  var fitBtn = document.getElementById("fit");
  var term = null, ws = null, reconnect = null, want = true, lastCols = 0, lastRows = 0;
  var fitWidth = true;     // default: show the whole PTY width on a narrow phone
  var baseCharW = 0;       // measured per-column pixel width at BASE_FONT (cached)
  var zoomFont = 0;        // explicit user zoom in px (pinch / A-/A+); 0 = automatic
  var MIN_FONT = 6, MAX_FONT = 48;

  if (typeof Terminal === "undefined") {
    msgEl.textContent = "Terminal renderer (xterm.js) failed to load.";
    return;
  }

  term = new Terminal({
    fontFamily: '"Cascadia Code", Consolas, "Courier New", monospace',
    fontSize: BASE_FONT,
    lineHeight: 1.0,
    scrollback: 5000,
    cursorBlink: false,
    disableStdin: true,        // read-only: typing goes through the control buttons
    convertEol: false,
    theme: { background: "#1e1e1e", foreground: "#d4d4d8" }
  });
  term.open(hostEl);
  try {
    if (window.CanvasAddon && window.CanvasAddon.CanvasAddon) {
      term.loadAddon(new window.CanvasAddon.CanvasAddon());
    }
  } catch (e) { /* DOM renderer stays active */ }

  // True per-row pixel height (font metrics included), measured from the rendered grid.
  function cellH() {
    var el = term.element;
    if (el && term.rows > 0) {
      var h = el.getBoundingClientRect().height / term.rows;
      if (h > 0) return h;
    }
    return 0;
  }

  // True grid pixel width, measured from the rendered grid.
  function gridW() {
    var el = term.element;
    return el ? el.getBoundingClientRect().width : 0;
  }

  // Choose the font size. In fit-width mode we shrink BASE_FONT just enough that all
  // lastCols columns map onto the WebView width, so the whole width is visible with no
  // horizontal pan. char width scales linearly with font, so we cache the per-column
  // width measured at BASE_FONT (baseCharW) and solve for the largest font that fits.
  // Changing the FONT (not a CSS transform) keeps xterm's layout/scroll geometry
  // correct, unlike scale() which is visual-only. Returns BASE_FONT when fit is off.
  function chooseFont() {
    if (zoomFont > 0) return zoomFont;        // explicit user zoom overrides fit/1:1
    if (!fitWidth || lastCols <= 0) return BASE_FONT;
    // Cache baseCharW the first time the grid is rendered at BASE_FONT.
    if (baseCharW <= 0 && term.options.fontSize === BASE_FONT) {
      var gw = gridW();
      if (gw > 0 && term.cols > 0) baseCharW = gw / term.cols;
    }
    if (baseCharW <= 0) return BASE_FONT;     // not measurable yet; a later pass fixes it
    var st = getComputedStyle(hostEl);
    var padX = (parseFloat(st.paddingLeft) || 0) + (parseFloat(st.paddingRight) || 0);
    var avail = wrapEl.clientWidth - padX;
    if (avail <= 0) return BASE_FONT;
    var needed = lastCols * baseCharW;         // grid width at BASE_FONT
    if (needed <= avail) return BASE_FONT;     // already fits at full size
    return Math.max(6, Math.floor(BASE_FONT * avail / needed));
  }

  // Apply the chosen font, then size the rows to match. Rows are NEVER fewer than the
  // PTY's: Claude Code redraws its input box with cursor-up moves sized to the PTY
  // height, so an xterm shorter than the PTY would clip those moves and leave each old
  // box behind as a duplicate. We use the larger of (rows that fit this WebView at the
  // current cell height) and (the PTY's own row count).
  function applyFont() {
    if (lastCols <= 0) return;
    var target = chooseFont();
    if ((term.options.fontSize || BASE_FONT) !== target) {
      try { term.options.fontSize = target; } catch (e) {}
    }
    fit();
  }

  function fit() {
    if (lastCols <= 0) return;
    var ch = cellH();
    if (ch <= 0) return;       // not rendered yet; a later fit will size it
    var st = getComputedStyle(hostEl);
    var pad = (parseFloat(st.paddingTop) || 0) + (parseFloat(st.paddingBottom) || 0);
    var avail = wrapEl.clientHeight - pad;
    var fitted = Math.max(1, Math.floor(avail / ch));
    var rows = Math.max(fitted, lastRows || 1);
    if (term.cols !== lastCols || term.rows !== rows) {
      try { term.resize(lastCols, rows); } catch (e) {}
    }
  }

  // Keep the live input box (bottom of the grid) in view when the grid is taller than
  // the WebView, unless the user has scrolled up to read history (sticky-bottom).
  function stickBottom() {
    var slack = wrapEl.scrollHeight - wrapEl.clientHeight - wrapEl.scrollTop;
    if (slack < 48) wrapEl.scrollTop = wrapEl.scrollHeight;
  }

  // Toggle fit-width <-> 1:1. Clears any manual zoom so Fit/1:1 always wins the next
  // tap. Exposed on window too so the host could drive it later.
  function setFitWidth(on) {
    fitWidth = !!on;
    zoomFont = 0;                                    // Fit/1:1 cancels manual zoom
    fitBtn.textContent = fitWidth ? "1:1" : "Fit";   // label = what a tap will DO next
    applyFont();
    requestAnimationFrame(function () { applyFont(); stickBottom(); });
  }
  window.ccSetFitWidth = setFitWidth;
  fitBtn.addEventListener("click", function () { setFitWidth(!fitWidth); });
  fitBtn.textContent = fitWidth ? "1:1" : "Fit";

  // ----- Manual zoom (A-/A+ buttons and two-finger pinch) -------------------------
  // Zoom is an explicit font in px that overrides fit-width. We change the real xterm
  // font (not a CSS transform) so the grid re-lays-out and #wrap pans correctly at any
  // size -- the same reason the fit toggle changes the font instead of scaling.
  function currentFontPx() {
    return (term && term.options.fontSize) ? term.options.fontSize : BASE_FONT;
  }
  function setZoom(px) {
    px = Math.round(px);
    if (px < MIN_FONT) px = MIN_FONT;
    if (px > MAX_FONT) px = MAX_FONT;
    if (px === zoomFont) return;                     // no change -> no re-layout churn
    zoomFont = px;
    fitWidth = false;                                // zoom is explicit, not fit-width
    fitBtn.textContent = "Fit";                      // a Fit tap returns to auto-fit
    applyFont();
    requestAnimationFrame(function () { applyFont(); stickBottom(); });
  }
  function zoomBy(factor) { setZoom((zoomFont > 0 ? zoomFont : currentFontPx()) * factor); }
  window.ccSetZoom = setZoom;
  document.getElementById("zoomout").addEventListener("click", function () { zoomBy(1 / 1.2); });
  document.getElementById("zoomin").addEventListener("click", function () { zoomBy(1.2); });

  // Pinch: scale the font from the size at gesture start by the change in finger spread.
  var pinchDist0 = 0, pinchFont0 = 0;
  function spread(t) {
    var dx = t[0].clientX - t[1].clientX, dy = t[0].clientY - t[1].clientY;
    return Math.sqrt(dx * dx + dy * dy);
  }
  wrapEl.addEventListener("touchstart", function (e) {
    if (e.touches.length === 2) {
      pinchDist0 = spread(e.touches);
      pinchFont0 = (zoomFont > 0 ? zoomFont : currentFontPx());
    }
  }, { passive: true });
  wrapEl.addEventListener("touchmove", function (e) {
    if (e.touches.length === 2 && pinchDist0 > 0) {
      e.preventDefault();                            // own the pinch; no page scroll
      setZoom(pinchFont0 * spread(e.touches) / pinchDist0);
    }
  }, { passive: false });
  wrapEl.addEventListener("touchend", function (e) {
    if (e.touches.length < 2) pinchDist0 = 0;
  }, { passive: true });

  if (window.ResizeObserver) {
    var pending = false;
    new ResizeObserver(function () {
      if (pending) return;
      pending = true;
      requestAnimationFrame(function () { pending = false; applyFont(); });
    }).observe(wrapEl);
  }

  function connect() {
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
    term.reset();   // each connection replays full history from byte 0
    var sock = new WebSocket(WSBASE + "/sessions/" + SID + "/stream");
    sock.binaryType = "arraybuffer";
    ws = sock;
    sock.onmessage = function (ev) {
      if (typeof ev.data === "string") {
        var m; try { m = JSON.parse(ev.data); } catch (e) { return; }
        if (m.type === "size" && m.cols > 0) { lastCols = m.cols; lastRows = m.rows || lastRows; applyFont(); }
        else if (m.type === "closed") { term.write("\r\n[stream closed: " + (m.reason || "") + "]\r\n"); }
        return;
      }
      term.write(new Uint8Array(ev.data), function () { requestAnimationFrame(stickBottom); });
    };
    sock.onopen = function () { msgEl.style.display = "none"; };
    sock.onclose = function () {
      if (ws === sock) ws = null;
      if (want && !reconnect) {
        reconnect = setTimeout(function () { reconnect = null; if (want) connect(); }, 1200);
      }
    };
    sock.onerror = function () {
      msgEl.style.display = "block";
      msgEl.textContent = "Connection error, retrying…";
      try { sock.close(); } catch (e) {}
    };
  }

  connect();
})();
</script>
</body>
</html>
""";
}
