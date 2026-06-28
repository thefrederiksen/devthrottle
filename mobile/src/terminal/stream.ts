// Live terminal mirror engine (issue #817). A faithful translation of the Android app's
// RawTerminalPage.cs into the React/TypeScript PWA: a real xterm.js terminal fed by the session's
// raw PTY byte stream over the WebSocket GET {gateway}/sessions/{sid}/stream (the same endpoint the
// Cockpit's cockpit-terminal.js and the MAUI phone client use), with the SAME fit-width / 1:1 /
// pinch-zoom mechanics and the SAME sticky-bottom auto-scroll.
//
// Why a byte stream and not buffer polling: a Label/snapshot cannot apply cursor moves, so Claude
// Code's constantly-repainting TUI stacked half-drawn frames as ghost lines. A byte stream applied
// in order to a terminal emulator cannot desync, so the screen stays coherent (RawTerminalPage.cs).
//
// The Gateway resolves the owning Director by session id and reverse-proxies the WebSocket upgrade,
// so the browser only ever talks same-origin to the Gateway (SessionWsProxyEndpoints.cs). A browser
// WebSocket cannot set an Authorization header, so the per-machine Gateway token rides as the
// cc-gateway-token cookie (ensureGatewayCookie), which the same-origin handshake carries and the
// Gateway's AuthMiddleware accepts - so the mirror works with global Gateway auth on or off.
//
// Rendering parity (do not regress): the grid is sized to the PTY's exact column count and NEVER
// fewer rows than the PTY's, so the cursor-relative redraws overwrite the input box in place
// instead of stacking ghost copies. Fit-width shrinks the FONT (a real layout change, so scroll
// geometry stays correct), 1:1 restores 13px with horizontal pan, and A-/A+/pinch set an explicit
// zoom font that overrides fit. Font range 6..48px. Auto-scroll only sticks when within 48px of the
// bottom, so it never yanks the view when the user has scrolled up to read history.

import { Terminal as Xterm } from "@xterm/xterm";
import { ensureGatewayCookie } from "../api/client";

const BASE_FONT = 13; // 1:1 (actual-size) font, in px - matches RawTerminalPage.cs
const MIN_FONT = 6;
const MAX_FONT = 48;
const RECONNECT_DELAY_MS = 1200; // ~1200ms reconnect, matching the Android app
const STICKY_SLACK_PX = 48; // auto-scroll only when within this many px of the bottom

/** Called when the fit/zoom state changes so the React shell can relabel the Fit button. */
export type FitLabelListener = (label: "Fit" | "1:1") => void;

function streamUrl(sessionId: string): string {
  const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${window.location.host}/sessions/${encodeURIComponent(sessionId)}/stream`;
}

export class TerminalMirror {
  private readonly wrapEl: HTMLElement;
  private readonly hostEl: HTMLElement;
  private readonly sessionId: string;
  private readonly onFitLabel: FitLabelListener;

  private term: Xterm | null = null;
  private ws: WebSocket | null = null;
  private reconnectTimer: number | null = null;
  private resizeObserver: ResizeObserver | null = null;
  private want = true;

  private lastCols = 0;
  private lastRows = 0;
  private fitWidth = true; // default: show the whole PTY width on a narrow phone
  private baseCharW = 0; // measured per-column pixel width at BASE_FONT (cached)
  private zoomFont = 0; // explicit user zoom in px (pinch / A-/A+); 0 = automatic
  private pinchDist0 = 0;
  private pinchFont0 = 0;

  constructor(wrapEl: HTMLElement, hostEl: HTMLElement, sessionId: string, onFitLabel: FitLabelListener) {
    this.wrapEl = wrapEl;
    this.hostEl = hostEl;
    this.sessionId = sessionId;
    this.onFitLabel = onFitLabel;
  }

  start(): void {
    const term = new Xterm({
      fontFamily: '"Cascadia Mono", Consolas, Menlo, "DejaVu Sans Mono", "Courier New", monospace',
      fontSize: BASE_FONT,
      lineHeight: 1.0,
      scrollback: 5000,
      cursorBlink: false,
      disableStdin: true, // read-only: typing goes through the control buttons, never the canvas
      convertEol: false,
      theme: { background: "#1e1e1e", foreground: "#d4d4d8" },
    });
    term.open(this.hostEl);
    this.term = term;
    this.onFitLabel(this.fitWidth ? "1:1" : "Fit");

    if (window.ResizeObserver) {
      let pending = false;
      this.resizeObserver = new ResizeObserver(() => {
        if (pending) return;
        pending = true;
        window.requestAnimationFrame(() => { pending = false; this.applyFont(); });
      });
      this.resizeObserver.observe(this.wrapEl);
    }

    this.wrapEl.addEventListener("touchstart", this.onTouchStart, { passive: true });
    this.wrapEl.addEventListener("touchmove", this.onTouchMove, { passive: false });
    this.wrapEl.addEventListener("touchend", this.onTouchEnd, { passive: true });

    this.connect();
  }

  dispose(): void {
    this.want = false;
    if (this.reconnectTimer !== null) { window.clearTimeout(this.reconnectTimer); this.reconnectTimer = null; }
    if (this.ws) { try { this.ws.onclose = null; this.ws.close(); } catch { /* already closing */ } this.ws = null; }
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    this.wrapEl.removeEventListener("touchstart", this.onTouchStart);
    this.wrapEl.removeEventListener("touchmove", this.onTouchMove);
    this.wrapEl.removeEventListener("touchend", this.onTouchEnd);
    if (this.term) { try { this.term.dispose(); } catch { /* ignore */ } this.term = null; }
  }

  // ----- fit-width / zoom (public, driven by the React Fit and A-/A+ buttons) -----------------

  /** Toggle fit-width <-> 1:1. Clears any manual zoom so Fit/1:1 always wins the next tap. */
  toggleFit(): void { this.setFitWidth(!this.fitWidth); }

  private setFitWidth(on: boolean): void {
    this.fitWidth = on;
    this.zoomFont = 0; // Fit/1:1 cancels manual zoom
    this.onFitLabel(this.fitWidth ? "1:1" : "Fit"); // label = what a tap will DO next
    this.applyFont();
    window.requestAnimationFrame(() => { this.applyFont(); this.stickBottom(); });
  }

  /** Manual zoom step (A- is 1/1.2, A+ is 1.2), matching RawTerminalPage.cs. */
  zoomBy(factor: number): void {
    this.setZoom((this.zoomFont > 0 ? this.zoomFont : this.currentFontPx()) * factor);
  }

  private setZoom(px: number): void {
    let next = Math.round(px);
    if (next < MIN_FONT) next = MIN_FONT;
    if (next > MAX_FONT) next = MAX_FONT;
    if (next === this.zoomFont) return; // no change -> no re-layout churn
    this.zoomFont = next;
    this.fitWidth = false; // zoom is explicit, not fit-width
    this.onFitLabel("Fit"); // a Fit tap returns to auto-fit
    this.applyFont();
    window.requestAnimationFrame(() => { this.applyFont(); this.stickBottom(); });
  }

  private currentFontPx(): number {
    return (this.term && this.term.options.fontSize) ? this.term.options.fontSize : BASE_FONT;
  }

  // ----- geometry (a direct translation of RawTerminalPage.cs) --------------------------------

  private cellH(): number {
    const el = this.term?.element;
    if (el && this.term && this.term.rows > 0) {
      const h = el.getBoundingClientRect().height / this.term.rows;
      if (h > 0) return h;
    }
    return 0;
  }

  private gridW(): number {
    const el = this.term?.element;
    return el ? el.getBoundingClientRect().width : 0;
  }

  // Largest font (in px) that maps all lastCols columns onto the wrap width when fit-width is on;
  // BASE_FONT when fit is off or not yet measurable; the explicit zoom font when one is set.
  private chooseFont(): number {
    if (this.zoomFont > 0) return this.zoomFont;
    const term = this.term;
    if (!term || !this.fitWidth || this.lastCols <= 0) return BASE_FONT;
    if (this.baseCharW <= 0 && term.options.fontSize === BASE_FONT) {
      const gw = this.gridW();
      if (gw > 0 && term.cols > 0) this.baseCharW = gw / term.cols;
    }
    if (this.baseCharW <= 0) return BASE_FONT; // not measurable yet; a later pass fixes it
    const st = getComputedStyle(this.hostEl);
    const padX = (parseFloat(st.paddingLeft) || 0) + (parseFloat(st.paddingRight) || 0);
    const avail = this.wrapEl.clientWidth - padX;
    if (avail <= 0) return BASE_FONT;
    const needed = this.lastCols * this.baseCharW;
    if (needed <= avail) return BASE_FONT; // already fits at full size
    return Math.max(MIN_FONT, Math.floor(BASE_FONT * avail / needed));
  }

  private applyFont(): void {
    const term = this.term;
    if (!term || this.lastCols <= 0) return;
    const target = this.chooseFont();
    if ((term.options.fontSize || BASE_FONT) !== target) {
      try { term.options.fontSize = target; } catch { /* mid-dispose */ }
    }
    this.fit();
  }

  private fit(): void {
    const term = this.term;
    if (!term || this.lastCols <= 0) return;
    const ch = this.cellH();
    if (ch <= 0) return; // not rendered yet; a later fit will size it
    const st = getComputedStyle(this.hostEl);
    const pad = (parseFloat(st.paddingTop) || 0) + (parseFloat(st.paddingBottom) || 0);
    const avail = this.wrapEl.clientHeight - pad;
    const fitted = Math.max(1, Math.floor(avail / ch));
    const rows = Math.max(fitted, this.lastRows || 1); // NEVER fewer rows than the PTY
    if (term.cols !== this.lastCols || term.rows !== rows) {
      try { term.resize(this.lastCols, rows); } catch { /* transient */ }
    }
  }

  // Keep the live input box in view when the grid is taller than the wrap, UNLESS the user has
  // scrolled up to read history (within STICKY_SLACK_PX of the bottom).
  private stickBottom(): void {
    const slack = this.wrapEl.scrollHeight - this.wrapEl.clientHeight - this.wrapEl.scrollTop;
    if (slack < STICKY_SLACK_PX) this.wrapEl.scrollTop = this.wrapEl.scrollHeight;
  }

  // ----- pinch zoom ---------------------------------------------------------------------------

  private spread(touches: TouchList): number {
    const dx = touches[0].clientX - touches[1].clientX;
    const dy = touches[0].clientY - touches[1].clientY;
    return Math.sqrt(dx * dx + dy * dy);
  }

  private onTouchStart = (e: TouchEvent): void => {
    if (e.touches.length === 2) {
      this.pinchDist0 = this.spread(e.touches);
      this.pinchFont0 = this.zoomFont > 0 ? this.zoomFont : this.currentFontPx();
    }
  };

  private onTouchMove = (e: TouchEvent): void => {
    if (e.touches.length === 2 && this.pinchDist0 > 0) {
      e.preventDefault(); // own the pinch; no page scroll
      this.setZoom(this.pinchFont0 * this.spread(e.touches) / this.pinchDist0);
    }
  };

  private onTouchEnd = (e: TouchEvent): void => {
    if (e.touches.length < 2) this.pinchDist0 = 0;
  };

  // ----- the WebSocket stream -----------------------------------------------------------------

  private connect(): void {
    if (!this.want) return;
    const term = this.term;
    if (!term) return;
    if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) return;

    // The per-machine token rides as the cc-gateway-token cookie so the same-origin WS handshake
    // authenticates (browsers cannot set an Authorization header on a WebSocket).
    ensureGatewayCookie();

    term.reset(); // each connection replays full history from byte 0
    const sock = new WebSocket(streamUrl(this.sessionId));
    sock.binaryType = "arraybuffer";
    this.ws = sock;

    sock.onmessage = (ev: MessageEvent) => {
      if (typeof ev.data === "string") {
        let m: { type?: string; cols?: number; rows?: number; reason?: string };
        try { m = JSON.parse(ev.data); } catch { return; }
        if (m.type === "size" && (m.cols ?? 0) > 0) {
          this.lastCols = m.cols ?? this.lastCols;
          this.lastRows = m.rows ?? this.lastRows;
          this.applyFont();
        } else if (m.type === "closed") {
          term.write("\r\n[stream closed: " + (m.reason || "") + "]\r\n");
        }
        return;
      }
      term.write(new Uint8Array(ev.data as ArrayBuffer), () => window.requestAnimationFrame(() => this.stickBottom()));
    };

    sock.onclose = () => {
      if (this.ws === sock) this.ws = null;
      if (this.want && this.reconnectTimer === null) {
        this.reconnectTimer = window.setTimeout(() => {
          this.reconnectTimer = null;
          if (this.want) this.connect();
        }, RECONNECT_DELAY_MS);
      }
    };

    sock.onerror = () => { try { sock.close(); } catch { /* already closing */ } };
  }
}
