// Cockpit terminal: a real xterm.js terminal fed by a WebSocket opened SAME-ORIGIN to the
// Gateway that served this page, at /sessions/{sid}/stream (issue #268; never through the
// Cockpit's SignalR channel), so the latency-sensitive byte stream stays fast. The Gateway
// resolves the owning Director by session id and reverse-proxies the upgrade to it - the
// browser never needs (and never sees) a Director's own address. Raw PTY bytes are applied in
// order, so the constantly-repainting Claude Code TUI renders coherently (no ghost-stacked
// frames). When the Gateway cannot reach the owning Director it accepts the WS and sends a
// {"type":"closed","reason":...} frame (rendered below), so a remote-Director failure shows
// its real cause instead of a bare reconnect to the Gateway's own (possibly loopback) host.
//
// Loaded as an ES module via Blazor JS interop. xterm.js is a classic script that sets
// window.Terminal.
//
// Rendering decisions (hard-won, do not regress):
// - The grid MIRRORS THE PTY EXACTLY - both cols AND rows, from the server's "size"
//   message. Claude Code's TUI redraws its bottom-docked footer with screen-height-
//   relative cursor moves; if xterm's row count differs from the PTY's, the redraw
//   region drifts and stale footer copies pile up as ghost lines (doubled prompt
//   boxes / struck-through "bypass permissions" rows). Never derive rows from the
//   viewport height - that was the cause of the ghosting.
// - DOM renderer, NOT the canvas addon. The DOM renderer uses the platform's native
//   text rasterization (ClearType on Windows), which is what the desktop terminal
//   renders with - the canvas addon's greyscale-AA glyph atlas reads blurry next to
//   it, especially at fractional display scaling.

const terms = new Map(); // id -> state

// Browser-side stream diagnostics (issue #199), OFF by default so a normal session's console
// stays quiet. Turn on from the browser console with: localStorage.setItem('cockpit.debug','1')
// (then re-select the session). Read on every call so it can be toggled live without a reload.
function debugOn() {
  try { return localStorage.getItem("cockpit.debug") === "1"; } catch (e) { return false; }
}
function dbg() {
  if (!debugOn()) return;
  try { console.log.apply(console, ["[cockpit-terminal]"].concat(Array.prototype.slice.call(arguments))); } catch (e) {}
}

// Bounded reconnect (issue #198): a hung/failing WebSocket used to retry every 1200ms FOREVER
// with no on-screen feedback, indistinguishable from a healthy idle stream (blank pane). We now
// show a visible status line on every connect attempt and stop after a run of consecutive
// failures, telling the user how to retry. The attempt counter resets the moment real stream
// data arrives, so a long-lived session that drops occasionally is unaffected - only a genuinely
// dead leg (this many failures in a row) gives up.
const RECONNECT_DELAY_MS = 1200;
const MAX_RECONNECT_ATTEMPTS = 30; // ~36s of dead-leg retries before announcing failure

// Write a dim status line into the terminal. It is wiped by the next t.reset() (on the first
// byte of a live stream, or on the next reconnect), so it never lingers over real PTY content.
function statusLine(state, text) {
  try { state.term.write("\r\n\x1b[2m[" + text + "]\x1b[0m\r\n"); } catch (e) {}
}

function wsHostOf(wsUrl) {
  try { return new URL(wsUrl).host; } catch (e) { return wsUrl; }
}

// The stream URL is ALWAYS same-origin to the Gateway that served this page (CockpitWsUrls,
// issue #268): the Gateway resolves the owning Director and reverse-proxies the upgrade, so
// the browser never receives a Director's own (possibly loopback) address. There is therefore
// nothing to rewrite here - the old loopback->location.hostname band-aid (issue #457) is gone;
// a Director's reachability is the Gateway's job and surfaces via the {"type":"closed"} frame.

export function connect(id, hostEl, wsUrl, dotNetRef) {
  dispose(id);
  if (typeof window.Terminal === "undefined") {
    hostEl.textContent = "Terminal renderer (xterm.js) failed to load.";
    return;
  }
  const term = new window.Terminal({
    // Match the desktop terminal (TerminalFonts.Family + TerminalControl metrics):
    // Cascadia MONO (not Code - no ligatures, crisper glyphs), then the same
    // macOS/Linux fallbacks; 14px with cellHeight = fontSize * 1.2.
    fontFamily: '"Cascadia Mono", Consolas, Menlo, "DejaVu Sans Mono", "Courier New", monospace',
    fontSize: 14, lineHeight: 1.2, scrollback: 5000, cursorBlink: false,
    disableStdin: false,           // interactive: keystrokes are forwarded via onData below
    convertEol: false,
    theme: { background: "#1e1e1e", foreground: "#d4d4d8" },
  });
  term.open(hostEl);

  // Forward every keystroke (raw bytes incl. Esc/Ctrl/arrows) to the owning Director's PTY.
  // xterm does NOT echo locally - the rendered result comes back over the output stream.
  if (dotNetRef) {
    term.onData((data) => { try { dotNetRef.invokeMethodAsync("OnInput", data); } catch (e) {} });
  }

  const state = { term, ws: null, wantOpen: true, host: hostEl, wsUrl, reconnectTimer: null, lastCols: 0, lastRows: 0, attempts: 0, gotFirstByte: false };
  terms.set(id, state);

  openWs(state);
}

// Mirror the PTY grid exactly (see header). Vertical placement within a too-tall pane is
// CSS's job (.term-host anchors the grid to the bottom); scrolling history is xterm's.
function fit(state) {
  const t = state.term;
  if (!t || state.lastCols <= 0 || state.lastRows <= 0) return;
  if (t.cols === state.lastCols && t.rows === state.lastRows) return;
  const buf = t.buffer.active;
  const atBottom = buf.viewportY >= buf.baseY;
  try { t.resize(state.lastCols, state.lastRows); } catch (e) {}
  if (atBottom) { try { t.scrollToBottom(); } catch (e) {} }
}

function openWs(state) {
  if (!state.wantOpen) return;
  const t = state.term;
  t.reset(); // each connection replays full history from byte 0
  state.gotFirstByte = false;
  const wsHost = wsHostOf(state.wsUrl);
  // wsHost is the GATEWAY this page was served from, not the owning Director - the Gateway
  // proxies on to the Director. Word it as the path so a loopback Gateway host (the Cockpit
  // opened locally) is never mistaken for the stream's real target (issue #457).
  statusLine(state, state.attempts > 0
    ? "stream lost, reconnecting via gateway " + wsHost + " (attempt " + (state.attempts + 1) + ")..."
    : "connecting via gateway " + wsHost + "...");
  let ws;
  const connectStartedAt = (typeof performance !== "undefined" ? performance.now() : Date.now());
  dbg("ws connect attempt", state.wsUrl, "attempt", state.attempts + 1);
  try { ws = new WebSocket(state.wsUrl); }
  catch (e) { statusLine(state, "cannot open stream: " + e.message); dbg("ws construct failed", e && e.message); return; }
  ws.binaryType = "arraybuffer";
  state.ws = ws;

  ws.onopen = () => { dbg("ws open", state.wsUrl, "after", Math.round((typeof performance !== "undefined" ? performance.now() : Date.now()) - connectStartedAt) + "ms"); };
  ws.onerror = () => { dbg("ws error", state.wsUrl); };

  // First frame of a LIVE stream: wipe the "connecting..." status and clear the failure
  // streak. A size header or PTY bytes prove the full browser->Gateway->Director path is up;
  // replay starts at byte 0, so resetting here loses nothing.
  function markLive(ev) {
    if (state.gotFirstByte) return;
    state.gotFirstByte = true;
    state.attempts = 0;
    var firstFrameMs = Math.round((typeof performance !== "undefined" ? performance.now() : Date.now()) - connectStartedAt);
    var firstFrameBytes = (typeof ev.data === "string") ? ev.data.length : (ev.data ? ev.data.byteLength : 0);
    dbg("ws first frame", state.wsUrl, firstFrameBytes + " bytes", firstFrameMs + "ms since connect");
    try { t.reset(); } catch (e) {}
  }

  ws.onmessage = (ev) => {
    if (typeof ev.data === "string") {
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      // A "closed" control frame is the Gateway reporting WHY the owning Director is
      // unreachable (issue #457/#461) - it is NOT proof of a live stream, so it must not
      // reset the reconnect-attempt streak (else we'd reconnect forever). onclose counts it.
      if (m.type === "closed") { t.write("\r\n[stream closed: " + (m.reason || "") + "]\r\n"); return; }
      markLive(ev);
      if (m.type === "size" && m.cols > 0 && m.rows > 0) {
        state.lastCols = m.cols; state.lastRows = m.rows; fit(state);
      }
      return;
    }
    markLive(ev);
    t.write(new Uint8Array(ev.data));
  };
  ws.onclose = (ev) => {
    if (state.ws === ws) state.ws = null;
    dbg("ws close", state.wsUrl, "code", (ev && ev.code), "reason", (ev && ev.reason) || "(none)");
    if (!state.wantOpen || state.reconnectTimer) return;
    state.attempts += 1;
    if (state.attempts > MAX_RECONNECT_ATTEMPTS) {
      statusLine(state, "stream via gateway " + wsHost + " is down - gave up after " + MAX_RECONNECT_ATTEMPTS +
        " attempts (last close code " + (ev && ev.code) + "). Re-select the session to retry.");
      dbg("ws gave up", state.wsUrl, "after", MAX_RECONNECT_ATTEMPTS, "attempts");
      return;
    }
    dbg("ws reconnect scheduled", state.wsUrl, "attempt", state.attempts + 1, "in", RECONNECT_DELAY_MS + "ms");
    state.reconnectTimer = setTimeout(() => {
      state.reconnectTimer = null;
      if (state.wantOpen) openWs(state);
    }, RECONNECT_DELAY_MS);
  };
}

export function dispose(id) {
  const s = terms.get(id);
  if (!s) return;
  s.wantOpen = false;
  if (s.reconnectTimer) clearTimeout(s.reconnectTimer);
  if (s.ws) { try { s.ws.onclose = null; s.ws.close(); } catch (e) {} }
  if (s.term) { try { s.term.dispose(); } catch (e) {} }
  terms.delete(id);
}
