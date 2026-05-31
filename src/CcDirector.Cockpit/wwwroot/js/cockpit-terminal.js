// Cockpit terminal: a real xterm.js terminal fed by a WebSocket opened DIRECT to the
// owning Director's /sessions/{sid}/stream (never through the Cockpit's SignalR channel),
// so the latency-sensitive byte stream stays fast. Raw PTY bytes are applied in order, so
// the constantly-repainting Claude Code TUI renders coherently (no ghost-stacked frames).
//
// Loaded as an ES module via Blazor JS interop. xterm.js + the canvas addon are classic
// scripts that set window.Terminal / window.CanvasAddon.

const terms = new Map(); // id -> state

export function connect(id, hostEl, wsUrl, dotNetRef) {
  dispose(id);
  if (typeof window.Terminal === "undefined") {
    hostEl.textContent = "Terminal renderer (xterm.js) failed to load.";
    return;
  }
  const term = new window.Terminal({
    fontFamily: '"Cascadia Code", Consolas, "Courier New", monospace',
    fontSize: 13, lineHeight: 1.0, scrollback: 5000, cursorBlink: false,
    disableStdin: false,           // interactive: keystrokes are forwarded via onData below
    convertEol: false,
    theme: { background: "#1e1e1e", foreground: "#d4d4d8" },
  });
  term.open(hostEl);
  try {
    if (window.CanvasAddon && window.CanvasAddon.CanvasAddon)
      term.loadAddon(new window.CanvasAddon.CanvasAddon());
  } catch (e) { /* DOM renderer stays active */ }

  // Forward every keystroke (raw bytes incl. Esc/Ctrl/arrows) to the owning Director's PTY.
  // xterm does NOT echo locally - the rendered result comes back over the output stream.
  if (dotNetRef) {
    term.onData((data) => { try { dotNetRef.invokeMethodAsync("OnInput", data); } catch (e) {} });
  }

  const state = { term, ws: null, wantOpen: true, host: hostEl, wsUrl, reconnectTimer: null, lastCols: 0, ro: null };
  terms.set(id, state);

  const ro = new ResizeObserver(() => fit(state));
  ro.observe(hostEl);
  state.ro = ro;

  openWs(state);
}

// Mirror the PTY's column count (so the TUI wraps exactly as drawn) but fit the ROW count to
// our viewport, handing vertical scrolling to xterm's own scrollback.
function fit(state) {
  const t = state.term;
  if (!t || state.lastCols <= 0) return;
  const el = t.element;
  if (!el || t.rows <= 0) return;
  const cellH = el.getBoundingClientRect().height / t.rows;
  if (cellH <= 0) return;
  const rows = Math.max(1, Math.floor(state.host.clientHeight / cellH));
  if (t.cols !== state.lastCols || t.rows !== rows) {
    const buf = t.buffer.active;
    const atBottom = buf.viewportY >= buf.baseY;
    try { t.resize(state.lastCols, rows); } catch (e) {}
    if (atBottom) { try { t.scrollToBottom(); } catch (e) {} }
  }
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
      if (m.type === "size" && m.cols > 0) { state.lastCols = m.cols; fit(state); }
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
  if (s.ro) { try { s.ro.disconnect(); } catch (e) {} }
  if (s.term) { try { s.term.dispose(); } catch (e) {} }
  terms.delete(id);
}
