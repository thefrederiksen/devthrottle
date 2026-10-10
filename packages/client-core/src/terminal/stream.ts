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
import type { IDisposable, ILink, ILinkProvider } from "@xterm/xterm";
import { ensureGatewayCookie } from "../api/client";
import { reportGatewayReachable, reportGatewayUnreachable } from "../connection/health";
import { findLineLinks, type LineLink } from "./lineLinks";

const BASE_FONT = 13; // 1:1 (actual-size) font, in px - matches RawTerminalPage.cs
const MIN_FONT = 6;
const MAX_FONT = 48;
// The smallest font auto fit-width is allowed to choose. A desktop-width PTY (e.g. 120 columns) can
// only be made to fit a phone by shrinking the font to a few pixels - unreadable, and it STILL
// overflows. So fit-width shrinks only down to this readable floor; past it the columns overflow and
// the horizontal pan takes over (show as much width as stays readable, pan for the rest). This floor
// applies ONLY to automatic fit; an explicit pinch / A- zoom can still go down to MIN_FONT.
const FIT_MIN_FONT = 11;
const RECONNECT_DELAY_MS = 1200; // ~1200ms reconnect, matching the Android app
const STICKY_SLACK_PX = 48; // auto-scroll only when within this many px of the bottom

/** Called when the fit/zoom state changes so the React shell can relabel the Fit button. */
export type FitLabelListener = (label: "Fit" | "1:1") => void;

// The live-stream connection state, surfaced to the React shell so it can show a small "reconnecting"
// note while the socket is down (mobile-resilience mission, Phase 3). "connecting" is the first attempt
// before any open; "live" once a socket is open and replaying; "reconnecting" whenever the socket has
// dropped and the ~1200ms retry loop is trying again. The terminal content stays on screen the whole
// time - the note is the only sign of trouble, never a blank screen.
export type TerminalStreamStatus = "connecting" | "live" | "reconnecting";
export type StreamStatusListener = (status: TerminalStreamStatus) => void;

function streamUrl(sessionId: string): string {
  const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${window.location.host}/sessions/${encodeURIComponent(sessionId)}/stream`;
}

export class TerminalMirror {
  private readonly wrapEl: HTMLElement;
  private readonly hostEl: HTMLElement;
  private readonly sessionId: string;
  private readonly onFitLabel: FitLabelListener;
  // Optional: the React shell's setter for the live-stream status note (Phase 3). Absent for callers
  // that do not render a note (the link-provider unit tests). Only ever called with a CHANGED status.
  private readonly onStatus?: StreamStatusListener;
  private status: TerminalStreamStatus = "connecting";
  // Local Files (Phase 3): the app supplies this to open its own file viewer when a FILE path in the
  // mirror is clicked. client-core stays app-agnostic (it must not import app UI - brief decision 6),
  // so the click routes back out through this callback. http/https URLs are opened here directly (a new
  // tab) and never go through this callback. Optional: with no callback the mirror is unchanged except
  // that URLs become clickable. This mirrors the interactive Cockpit terminal (interactive.ts).
  private readonly onFileLink?: (path: string) => void;

  private term: Xterm | null = null;
  private ws: WebSocket | null = null;
  private reconnectTimer: number | null = null;
  private resizeObserver: ResizeObserver | null = null;
  // The registered xterm link provider (Local Files, Phase 3), disposed with the terminal.
  private linkProvider: IDisposable | null = null;
  private want = true;

  private lastCols = 0;
  private lastRows = 0;
  private fitWidth = true; // default: show the whole PTY width on a narrow phone
  private baseCharW = 0; // measured per-column pixel width at BASE_FONT (cached)
  private zoomFont = 0; // explicit user zoom in px (pinch / A-/A+); 0 = automatic
  private pinchDist0 = 0;
  private pinchFont0 = 0;

  constructor(
    wrapEl: HTMLElement,
    hostEl: HTMLElement,
    sessionId: string,
    onFitLabel: FitLabelListener,
    onFileLink?: (path: string) => void,
    onStatus?: StreamStatusListener,
  ) {
    this.wrapEl = wrapEl;
    this.hostEl = hostEl;
    this.sessionId = sessionId;
    this.onFitLabel = onFitLabel;
    this.onFileLink = onFileLink;
    this.onStatus = onStatus;
  }

  // Publish a live-stream status transition to the shell (Phase 3), de-duped so the same status does
  // not re-notify. The initial "connecting" is published on start() so the note shows from the first
  // frame until the socket opens.
  private setStatus(next: TerminalStreamStatus): void {
    if (next === this.status) return;
    this.status = next;
    this.onStatus?.(next);
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

    this.registerLinks(term);

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

    // Publish the initial "connecting" so the note shows from the first frame until the socket opens.
    this.onStatus?.(this.status);
    this.connect();
  }

  dispose(): void {
    this.want = false;
    if (this.reconnectTimer !== null) { window.clearTimeout(this.reconnectTimer); this.reconnectTimer = null; }
    if (this.ws) { try { this.ws.onclose = null; this.ws.close(); } catch { /* already closing */ } this.ws = null; }
    if (this.linkProvider !== null) {
      try { this.linkProvider.dispose(); } catch { /* already disposed */ }
      this.linkProvider = null;
    }
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    this.wrapEl.removeEventListener("touchstart", this.onTouchStart);
    this.wrapEl.removeEventListener("touchmove", this.onTouchMove);
    this.wrapEl.removeEventListener("touchend", this.onTouchEnd);
    if (this.term) { try { this.term.dispose(); } catch { /* ignore */ } this.term = null; }
  }

  // ----- clickable links (Local Files, Phase 3) -----------------------------------------------

  // Make absolute file paths and http/https URLs in the mirror clickable. xterm asks per VISUAL row via
  // provideLinks. A long absolute path (e.g. D:\ReposFred\devthrottle\.temp\lf-samples\sample-image.png)
  // WRAPS across several rows on a narrow phone, so detecting per visual row would only ever see a
  // fragment and produce no link - which is why terminal file links did nothing on mobile while the wide
  // Cockpit terminal (paths on one line) worked. So we reconstruct the whole LOGICAL line first (this row
  // plus the wrapped continuation rows), run the shared detector over the joined text, then map each
  // detected span back to a possibly multi-row xterm range. A FILE path click routes to the app's
  // onFileLink (its viewer); a URL opens in a new tab. Provider disposed with the terminal.
  private registerLinks(term: Xterm): void {
    const provider: ILinkProvider = {
      provideLinks: (bufferLineNumber: number, callback: (links: ILink[] | undefined) => void) => {
        const buffer = term.buffer.active;
        const cols = term.cols;
        const reqLine = buffer.getLine(bufferLineNumber - 1);
        if (!reqLine || cols <= 0) {
          callback(undefined);
          return;
        }

        // Walk back over wrapped continuation rows to the logical line's first row.
        let startIdx = bufferLineNumber - 1; // 0-based
        while (startIdx > 0 && buffer.getLine(startIdx)?.isWrapped) startIdx--;

        // Join this row + the wrapped rows that follow, each padded to the full width so a character
        // OFFSET in the joined text maps cleanly to (row = offset / cols, column = offset % cols).
        let text = "";
        for (let j = startIdx; j < buffer.length; j++) {
          const line = buffer.getLine(j);
          if (!line) break;
          if (j > startIdx && !line.isWrapped) break;
          let row = line.translateToString(false);
          if (row.length < cols) row = row.padEnd(cols, " ");
          else if (row.length > cols) row = row.slice(0, cols);
          text += row;
        }

        const found = findLineLinks(text);
        if (found.length === 0) {
          callback(undefined);
          return;
        }

        const links: ILink[] = [];
        for (const l of found) {
          // l is a 0-based half-open [start, end) offset span in the joined text. xterm ranges are
          // 1-based and inclusive on both ends; a span can cross wrapped rows, so start/end may differ in y.
          const startY = startIdx + Math.floor(l.start / cols) + 1;
          const startX = (l.start % cols) + 1;
          const lastOffset = l.end - 1;
          const endY = startIdx + Math.floor(lastOffset / cols) + 1;
          const endX = (lastOffset % cols) + 1;
          // Only hand xterm the links that actually cover the row it asked about.
          if (bufferLineNumber < startY || bufferLineNumber > endY) continue;
          links.push({
            text: l.text,
            range: { start: { x: startX, y: startY }, end: { x: endX, y: endY } },
            activate: (event: MouseEvent) => this.onLinkActivate(l, event),
          });
        }
        callback(links.length > 0 ? links : undefined);
      },
    };
    this.linkProvider = term.registerLinkProvider(provider);
  }

  // A link was clicked. URLs open in a new tab (never routed to the app); a FILE path is handed to the
  // app's viewer via onFileLink when one was supplied.
  private onLinkActivate(link: LineLink, _event: MouseEvent): void {
    if (link.isUrl) {
      window.open(link.text, "_blank", "noopener,noreferrer");
      return;
    }
    if (this.onFileLink) this.onFileLink(link.text);
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
    return Math.max(FIT_MIN_FONT, Math.floor(BASE_FONT * avail / needed));
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
    if (this.cellH() <= 0) return; // not rendered yet; a later fit will size it
    // Mirror the PTY's grid EXACTLY: same columns, same rows. We must never use fewer rows than the
    // PTY (absolute cursor moves like ESC[35;1H would land off-grid and the TUI would collapse), but
    // we must never use MORE either - padding the grid up to the phone's height only appends empty
    // rows below the real content, which on a tall desktop PTY shown on a short phone is a large dead
    // scroll band. The .term-stage background fills any space left below the exact-size grid.
    const rows = Math.max(1, this.lastRows);
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

    // NOTE: the terminal is NOT reset here. On a bad connection connect() is retried every ~1200ms, and
    // resetting before a socket that never opens would wipe the screen and leave it blank the whole time
    // the network is down (mobile-resilience mission: never clear good data). The reset moves to a
    // SUCCESSFUL open (sock.onopen), immediately before the byte-0 history replay that a new connection
    // always sends - so a failed attempt keeps the last-known screen and only a real replay clears it.
    const sock = new WebSocket(streamUrl(this.sessionId));
    sock.binaryType = "arraybuffer";
    this.ws = sock;
    // Whether THIS socket ever opened. A close with opened=false is a failed attempt (the stream leg is
    // down) and lights the shared connection banner; a close after a successful open is a normal end or a
    // mid-stream drop the retry loop handles, and must not be mistaken for an unreachable Gateway.
    let opened = false;

    sock.onopen = () => {
      opened = true;
      term.reset(); // a new connection replays the full history from byte 0 - clear right before it
      reportGatewayReachable(); // the stream reached a healthy backend (feeds the global banner)
      this.setStatus("live");
    };

    sock.onmessage = (ev: MessageEvent) => {
      if (typeof ev.data === "string") {
        let m: { type?: string; cols?: number; rows?: number; reason?: string };
        try { m = JSON.parse(ev.data); } catch { return; }
        if (m.type === "size" && (m.cols ?? 0) > 0) {
          this.lastCols = m.cols ?? this.lastCols;
          this.lastRows = m.rows ?? this.lastRows;
          this.applyFont();
        } else if (m.type === "closed") {
          // error-report-exempt: the Gateway's own verdict on why the owning Director is unreachable, written and held by the Gateway - nothing failed in this browser
          term.write("\r\n[stream closed: " + (m.reason || "") + "]\r\n");
        }
        return;
      }
      term.write(new Uint8Array(ev.data as ArrayBuffer), () => window.requestAnimationFrame(() => this.stickBottom()));
    };

    sock.onclose = () => {
      if (this.ws === sock) this.ws = null;
      // Disposing (want=false): no reconnect, no status, no health report - a deliberate teardown.
      if (!this.want) return;
      // A socket that never opened is a failed connect attempt: the stream leg is down, so light the
      // shared connection banner. A close AFTER a successful open is a normal stream end or a brief drop
      // the retry loop handles; the next failed attempt (if the network is really down) reports it then.
      if (!opened) reportGatewayUnreachable();
      this.setStatus("reconnecting");
      if (this.reconnectTimer === null) {
        this.reconnectTimer = window.setTimeout(() => {
          this.reconnectTimer = null;
          if (this.want) this.connect();
        }, RECONNECT_DELAY_MS);
      }
    };

    sock.onerror = () => { try { sock.close(); } catch { /* already closing */ } };
  }
}
