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
    // size frame); the grid renders at that exact width and #wrap scrolls horizontally
    // when it is wider than the phone, while xterm's own viewport owns vertical scrollback.
    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<link rel="stylesheet" href="__BASE__/xterm.css">
<style>
  html, body { margin: 0; padding: 0; background: #1e1e1e; height: 100%; overflow: hidden; }
  /* Scroll both axes. The grid is rendered at the PTY's exact geometry (see fit());
     when that grid is wider or TALLER than this WebView, #wrap scrolls to reveal the
     rest. Crucially the row count is never shrunk below the PTY's, so Claude Code's
     cursor-relative redraws overwrite the input box in place instead of stacking
     ghost copies down the history. */
  #wrap {
    position: absolute; inset: 0;
    overflow-x: auto; overflow-y: auto;
    -webkit-overflow-scrolling: touch;
  }
  #xterm { padding: 6px 0 6px 8px; width: max-content; }
  #msg {
    position: absolute; left: 8px; top: 8px;
    color: #8a93a6; font-family: monospace; font-size: 12px;
  }
</style>
</head>
<body>
<div id="wrap"><div id="xterm"></div></div>
<div id="msg">Connecting to terminal&hellip;</div>
<script src="__BASE__/xterm.js"></script>
<script src="__BASE__/xterm-addon-canvas.js"></script>
<script>
(function () {
  var BASE = __BASE_JSON__;
  var SID = __SID_JSON__;
  var WSBASE = BASE.replace(/^http/, "ws");   // http->ws, https->wss
  var hostEl = document.getElementById("xterm");
  var wrapEl = document.getElementById("wrap");
  var msgEl = document.getElementById("msg");
  var term = null, ws = null, reconnect = null, want = true, lastCols = 0, lastRows = 0;

  if (typeof Terminal === "undefined") {
    msgEl.textContent = "Terminal renderer (xterm.js) failed to load.";
    return;
  }

  term = new Terminal({
    fontFamily: '"Cascadia Code", Consolas, "Courier New", monospace',
    fontSize: 13,
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

  // Size xterm to the PTY. Columns mirror the PTY exactly so wrapping matches. Rows
  // are NEVER fewer than the PTY's: Claude Code redraws its input box with cursor-up
  // moves sized to the PTY height, so an xterm shorter than the PTY would clip those
  // moves and leave each old box behind as a duplicate. We therefore use the larger
  // of (rows that fit this WebView) and (the PTY's own row count). When the WebView is
  // tall this matches the web client (extra rows, xterm scrollback owns history); when
  // it is short the grid is taller than the box and #wrap scrolls to the live bottom.
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

  if (window.ResizeObserver) {
    var pending = false;
    new ResizeObserver(function () {
      if (pending) return;
      pending = true;
      requestAnimationFrame(function () { pending = false; fit(); });
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
        if (m.type === "size" && m.cols > 0) { lastCols = m.cols; lastRows = m.rows || lastRows; fit(); }
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
