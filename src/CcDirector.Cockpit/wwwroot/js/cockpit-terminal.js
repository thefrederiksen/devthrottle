// Cockpit terminal: a real xterm.js terminal fed by a WebSocket opened DIRECT to the
// owning Director's /sessions/{sid}/stream (never through the Cockpit's SignalR channel),
// so the latency-sensitive byte stream stays fast. Raw PTY bytes are applied in order, so
// the constantly-repainting Claude Code TUI renders coherently (no ghost-stacked frames).
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

// Directors built before the 2026-05-31 registration fix advertise their endpoint as
// http://127.0.0.1:{port}. That host only resolves on the Director's own machine - from a
// laptop on the tailnet it points at the LAPTOP and the terminal stays blank. Those old
// Directors run for weeks (sessions never die), so map loopback to the host the browser
// reached the Cockpit through: Tailscale Serve fronts every Director port on the same
// machine name. New Directors advertise the real tailnet URL and pass through untouched.
function resolveWsUrl(wsUrl) {
  let u;
  try { u = new URL(wsUrl); } catch (e) { return wsUrl; }
  if (u.hostname !== "127.0.0.1" && u.hostname !== "localhost") return wsUrl;
  u.hostname = location.hostname;
  if (location.protocol === "https:") u.protocol = "wss:";
  return u.toString();
}

export function connect(id, hostEl, wsUrl, dotNetRef) {
  dispose(id);
  wsUrl = resolveWsUrl(wsUrl);
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

  const state = { term, ws: null, wantOpen: true, host: hostEl, wsUrl, reconnectTimer: null, lastCols: 0, lastRows: 0 };
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
  let ws;
  try { ws = new WebSocket(state.wsUrl); }
  catch (e) { t.write("\r\n[cannot open stream: " + e.message + "]\r\n"); return; }
  ws.binaryType = "arraybuffer";
  state.ws = ws;

  ws.onmessage = (ev) => {
    if (typeof ev.data === "string") {
      let m; try { m = JSON.parse(ev.data); } catch { return; }
      if (m.type === "size" && m.cols > 0 && m.rows > 0) {
        state.lastCols = m.cols; state.lastRows = m.rows; fit(state);
      }
      else if (m.type === "closed") t.write("\r\n[stream closed: " + (m.reason || "") + "]\r\n");
      return;
    }
    t.write(new Uint8Array(ev.data));
  };
  ws.onclose = () => {
    if (state.ws === ws) state.ws = null;
    if (state.wantOpen && !state.reconnectTimer)
      state.reconnectTimer = setTimeout(() => {
        state.reconnectTimer = null;
        if (state.wantOpen) openWs(state);
      }, 1200);
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
