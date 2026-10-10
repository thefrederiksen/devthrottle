// Interactive desktop terminal engine (issue #971). A faithful TypeScript port of the Blazor
// Cockpit's cockpit-terminal.js into the shared client-core terminal package, for the React desktop
// Cockpit (apps/cockpit). It is the INTERACTIVE sibling of the read-only mobile mirror (stream.ts):
// both feed a real xterm.js terminal from the session's raw PTY byte stream over the same WebSocket
// (GET {gateway}/sessions/{sid}/stream, reverse-proxied by the Gateway to the owning Director), but
// this one is TYPEABLE - it forwards every keystroke to the PTY.
//
// Why a byte stream and not buffer polling: a snapshot cannot apply cursor moves, so Claude Code's
// constantly-repainting TUI stacked half-drawn frames as ghost lines. Raw bytes applied in order to
// a terminal emulator cannot desync, so the screen stays coherent.
//
// How typing reaches the PTY (the whole point of leaving Blazor): in the Blazor Cockpit every
// keystroke rode the SignalR circuit through a server-side [JSInvokable] OnInput, so a degraded
// circuit blocked typing. Here xterm's onData forwards the raw bytes DIRECTLY to the Gateway with a
// REST call - POST /sessions/{sid}/prompt with appendEnter:false (sendPrompt) - so input never
// depends on any server render channel and a slow/dropped OUTPUT stream never blocks INPUT. xterm
// does not echo locally; the rendered result comes back over the output stream.
//
// The browser only ever talks same-origin to the Gateway. A browser WebSocket cannot set an
// Authorization header, so the per-machine token rides as the cc-gateway-token cookie
// (ensureGatewayCookie), which the same-origin handshake carries and the Gateway's AuthMiddleware
// accepts - so the terminal works with global Gateway auth on or off.
//
// Rendering decisions (hard-won, do NOT regress - carried over from cockpit-terminal.js):
//  - The grid MIRRORS THE PTY EXACTLY - both cols AND rows, from the server's "size" message. Claude
//    Code's TUI redraws its bottom-docked footer with screen-height-relative cursor moves; if xterm's
//    row count differs from the PTY's, the redraw region drifts and stale footer copies pile up as
//    ghost lines. Never derive rows from the viewport height - that was the cause of the ghosting.
//  - DOM renderer, NOT the canvas addon (the xterm default is the DOM renderer). It uses the
//    platform's native text rasterization (ClearType on Windows), matching the desktop terminal; the
//    canvas addon's greyscale-AA glyph atlas reads blurry next to it at fractional display scaling.
//  - Bounded FAST reconnect WITH a visible status line, then a SLOW keepalive probe that never gives
//    up. A hung/failing socket must not retry forever at full speed with no on-screen feedback
//    (indistinguishable from a healthy idle stream / blank pane): every attempt writes a dim status
//    line, and after a run of consecutive fast failures it announces the outage and drops to a slow
//    keepalive probe (SLOW_RECONNECT_DELAY_MS). That probe keeps retrying quietly, so when the Gateway
//    comes back the stream resumes on its own - no manual re-selection and no page reload (issue
//    #1032). The counter resets the moment real stream data arrives, so a long-lived session that
//    drops occasionally is unaffected and the next outage re-announces from scratch.
//  - The {"type":"closed"} control frame is the Gateway reporting WHY the owning Director is
//    unreachable; it is NOT proof of a live stream, so it must not reset the reconnect streak.

import { Terminal as Xterm } from "@xterm/xterm";
import type { IDisposable, ILink, ILinkProvider } from "@xterm/xterm";
import { ensureGatewayCookie, gatewayErrorMessage, sendPrompt } from "../api/client";
import { errorFacts, reportClientError } from "../errors/reportClientError";
import { findLineLinks, type LineLink } from "./lineLinks";

// Match the desktop terminal (TerminalFonts.Family + TerminalControl metrics): Cascadia MONO (not
// Code - no ligatures, crisper glyphs), then the same macOS/Linux fallbacks; 14px with lineHeight 1.2.
const FONT_FAMILY =
  '"Cascadia Mono", Consolas, Menlo, "DejaVu Sans Mono", "Courier New", monospace';
// The PREFERRED font size. It is the ceiling: the grid renders at this size whenever the whole PTY
// grid fits the pane. When the PTY grid (which mirrors the owning terminal EXACTLY - see fitFont) is
// bigger than the cockpit pane, the font shrinks toward MIN_FONT_SIZE so the grid still fits, rather
// than overflowing the pane. It never grows past this size (readability ceiling).
const FONT_SIZE = 14;
// The floor for the auto-fit. Below this the text is too small to read, so a grid that still would
// not fit at this size is allowed to overflow (xterm's own viewport then scrolls it) rather than
// shrinking into illegibility. In practice a normal desktop terminal fits well above this.
const MIN_FONT_SIZE = 8;
const LINE_HEIGHT = 1.2;
const SCROLLBACK = 5000;

const RECONNECT_DELAY_MS = 1200; // ~1200ms between fast reconnect attempts
const MAX_RECONNECT_ATTEMPTS = 30; // ~36s of fast dead-leg retries before dropping to the slow probe
// After the fast cap is exhausted the engine does NOT give up: it keeps a slow keepalive probe so the
// stream resumes on its own when the Gateway returns (issue #1032). 15s is slow enough to be
// effectively idle on the network yet quick enough that recovery feels automatic.
const SLOW_RECONNECT_DELAY_MS = 15000;

// How many animation frames start() will wait for the host to be laid out (non-zero size) before it
// opens the terminal anyway. ~30 frames is roughly half a second at 60fps - long enough to cover the
// pane's first layout, short enough that a permanently-hidden host still opens instead of polling
// forever (the deferred dispose keeps even that case free of the 'dimensions' error). See issue #1029.
const BRING_UP_MAX_FRAMES = 30;

// The stream URL is ALWAYS same-origin to the Gateway that served this page (never a Director's own
// address - the Gateway resolves the owning Director and reverse-proxies the upgrade). Built from
// window.location so no absolute URL is ever hard-coded (Gateway-only-ingress lint rule, #967). This
// mirrors the same-origin construction in stream.ts; the two engines are intentionally self-contained.
function streamUrl(sessionId: string): string {
  const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${window.location.host}/sessions/${encodeURIComponent(sessionId)}/stream`;
}

/** The gateway host label shown in the status line (the path, not a Director's own address). */
function wsHostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

/**
 * A live, interactive xterm terminal for exactly one session. The React pane constructs one per
 * session (keyed by session id) and disposes it when the selection changes, so this class never has
 * to switch sessions in place.
 */
export class InteractiveTerminal {
  private readonly hostEl: HTMLElement;
  private readonly sessionId: string;
  // Local Files (Phase 2): the app supplies this to open its own file viewer when a FILE path in the
  // terminal is clicked. client-core stays app-agnostic (it must not import app UI - brief decision 6),
  // so the click routes back out through this callback. http/https URLs are opened here directly (a new
  // tab) and never go through this callback. Optional: with no callback the terminal is unchanged except
  // that URLs become clickable.
  private readonly onFileLink?: (path: string) => void;

  private term: Xterm | null = null;
  private ws: WebSocket | null = null;
  private reconnectTimer: number | null = null;
  // Watches the host for size changes (pane resize, window resize, layout shifts) so the auto-fit
  // font size is recomputed and the grid keeps fitting the pane. Disposed with the terminal.
  private resizeObserver: ResizeObserver | null = null;
  // The registered xterm link provider (Local Files, Phase 2), disposed with the terminal.
  private linkProvider: IDisposable | null = null;
  // The pending animation frame that waits for the host to be laid out before opening the terminal.
  // It is cancelled on dispose so a pane torn down before layout never opens a terminal. See
  // start()/dispose() for the lifecycle bug this fixes (issue #1029).
  private bringUpFrame: number | null = null;
  private bringUpAttempts = 0;
  private wantOpen = true;

  private lastCols = 0;
  private lastRows = 0;
  private attempts = 0; // consecutive failed connect attempts; reset on the first live byte
  private gotFirstByte = false;
  // Whether the "dropped to the slow keepalive probe" status line has already been written for the
  // CURRENT outage. It is announced once when the fast cap is first crossed and cleared by markLive so
  // the next fresh outage re-announces (issue #1032).
  private announcedSlow = false;

  // Keystroke send serialization (issue #1021). Bytes from onData are appended here in the EXACT
  // order xterm emits them; a single pump drains this buffer one POST at a time, awaiting each send
  // before starting the next, so the PTY never sees reordered or dropped input at any typing speed.
  private pendingInput = "";
  private inputPumping = false;
  // Whether the current run of failed keystroke sends has been reported; cleared by the next send that lands.
  private keystrokeFailureReported = false;

  constructor(hostEl: HTMLElement, sessionId: string, onFileLink?: (path: string) => void) {
    this.hostEl = hostEl;
    this.sessionId = sessionId;
    this.onFileLink = onFileLink;
  }

  start(): void {
    this.wantOpen = true;
    this.bringUpAttempts = 0;
    // Do NOT create/open xterm synchronously. xterm's first render reads the host element's pixel
    // size, and Terminal.open()/Viewport.reset() each queue an internal requestAnimationFrame that
    // reads the render service's `dimensions`. Under React 18 StrictMode the pane mounts, unmounts,
    // and remounts within a single tick - so a synchronous open would spin up a terminal only to
    // dispose it in the same tick, leaving that queued frame to fire against a torn-down render
    // service (the 'dimensions' TypeError). Instead, wait one animation frame for the host to be laid
    // out (non-zero size) and then open once; if this instance is disposed first, dispose() cancels
    // the pending frame so the throwaway StrictMode mount never opens a terminal at all (issue #1029).
    this.scheduleBringUp();
  }

  // Wait for the host element to be laid out (non-zero measured size) before opening the terminal,
  // re-polling one animation frame at a time. Capped at BRING_UP_MAX_FRAMES so a host that never gets
  // a size (e.g. a pane that stays hidden) still opens eventually instead of polling forever; the
  // deferred dispose keeps that case free of the 'dimensions' error too (issue #1029).
  private scheduleBringUp(): void {
    this.bringUpFrame = window.requestAnimationFrame(() => {
      this.bringUpFrame = null;
      if (!this.wantOpen) return;
      const laidOut = this.hostEl.clientWidth > 0 && this.hostEl.clientHeight > 0;
      if (!laidOut && this.bringUpAttempts < BRING_UP_MAX_FRAMES) {
        this.bringUpAttempts += 1;
        this.scheduleBringUp();
        return;
      }
      this.openTerminal();
    });
  }

  private openTerminal(): void {
    const term = new Xterm({
      fontFamily: FONT_FAMILY,
      fontSize: FONT_SIZE,
      lineHeight: LINE_HEIGHT,
      scrollback: SCROLLBACK,
      cursorBlink: false,
      disableStdin: false, // interactive: keystrokes are forwarded via onData below
      convertEol: false,
      theme: { background: "#1e1e1e", foreground: "#d4d4d8" },
    });
    term.open(this.hostEl);
    this.term = term;

    // Recompute the auto-fit font whenever the host changes size (pane resize, window resize, a
    // sibling panel opening/closing). Without this the grid only fits the size the pane had at open
    // and overflows again after any resize. ResizeObserver fires once on observe with the current
    // size, so the initial fit is covered too. Guarded by wantOpen so a torn-down instance is inert.
    this.resizeObserver = new ResizeObserver(() => {
      if (this.wantOpen) this.fitFont();
    });
    this.resizeObserver.observe(this.hostEl);

    // Forward every keystroke (raw bytes incl. Esc/Ctrl+C/arrows/the slash-command UI) to the owning
    // Director's PTY via a REST call to the Gateway - appendEnter:false writes the bytes verbatim (no
    // submit newline). xterm does not echo locally; the rendered result returns over the output stream,
    // and input does not depend on the OUTPUT stream's health, so a dropped/reconnecting stream never
    // blocks typing (the #971 goal).
    //
    // Ordering (issue #1021): the sends are NOT fire-and-forget. Firing an independent, unawaited POST
    // per keystroke let concurrent POSTs complete out of order at the Director, so fast typing reached
    // the PTY reordered or dropped ("abc...789" arrived as "...645789"; "echoTESTxyz" became
    // "echoTESTyyz"). Instead each keystroke is appended to a buffer and a single async pump drains it
    // one POST at a time - awaiting the previous send before the next - so bytes reach the PTY in the
    // exact order typed. Keystrokes that pile up while a send is in flight are coalesced into the next
    // POST, which both preserves order and cuts the request count under fast typing. The key handler
    // never blocks (enqueue is synchronous; the await happens inside the pump), so typing stays
    // responsive. Control keys are raw bytes like any other and keep their position in the stream.
    term.onData((data) => {
      if (!data) return;
      this.enqueueInput(data);
    });

    this.registerLinks(term);

    this.openWs();
  }

  // Local Files (Phase 2): make absolute file paths and http/https URLs in the terminal clickable.
  // xterm asks per line via provideLinks; we run the shared detector over that line's rendered text and
  // hand back one xterm link per detected span with its column range. A FILE path click routes to the
  // app's onFileLink (its viewer); a URL opens in a new tab here. The provider is disposed with the
  // terminal so it never outlives the render service.
  private registerLinks(term: Xterm): void {
    const provider: ILinkProvider = {
      provideLinks: (bufferLineNumber: number, callback: (links: ILink[] | undefined) => void) => {
        const bufLine = term.buffer.active.getLine(bufferLineNumber - 1);
        if (!bufLine) {
          callback(undefined);
          return;
        }
        const text = bufLine.translateToString(true);
        const found = findLineLinks(text);
        if (found.length === 0) {
          callback(undefined);
          return;
        }
        const links: ILink[] = found.map((l) => ({
          text: l.text,
          // xterm buffer ranges are 1-based and inclusive on both ends. l is a 0-based half-open
          // [start, end) column range, so the first cell is start+1 and the last cell is end.
          range: {
            start: { x: l.start + 1, y: bufferLineNumber },
            end: { x: l.end, y: bufferLineNumber },
          },
          activate: (event: MouseEvent) => this.onLinkActivate(l, event),
        }));
        callback(links);
      },
    };
    this.linkProvider = term.registerLinkProvider(provider);
  }

  // A terminal link was clicked. URLs open in a new tab (never routed to the app); a FILE path is
  // handed to the app's viewer via onFileLink when one was supplied.
  private onLinkActivate(link: LineLink, _event: MouseEvent): void {
    if (link.isUrl) {
      window.open(link.text, "_blank", "noopener,noreferrer");
      return;
    }
    if (this.onFileLink) this.onFileLink(link.text);
  }

  // Append typed bytes to the FIFO buffer and make sure the pump is running. Synchronous and
  // non-blocking so the xterm key handler returns immediately; the actual POST happens inside the
  // pump. Bytes are buffered in the exact order onData delivers them (issue #1021).
  private enqueueInput(data: string): void {
    this.pendingInput += data;
    if (this.inputPumping) return; // a pump is already draining; it will pick these bytes up
    this.inputPumping = true;
    void this.pumpInput();
  }

  // Drain the keystroke buffer one POST at a time, awaiting each send before the next so the PTY
  // receives bytes in the exact order typed (issue #1021). Bytes that arrive while a send is in
  // flight are coalesced into the following POST. A failed send is reported and the buffered bytes it
  // carried are dropped (a degraded network; the user retypes) - we do NOT re-queue, which would risk
  // duplicating bytes the Director may already have applied. The loop re-checks pendingInput after
  // every await, so anything enqueued mid-send is still sent, in order.
  private async pumpInput(): Promise<void> {
    try {
      while (this.pendingInput.length > 0) {
        if (!this.wantOpen) {
          // Disposed mid-drain: drop any not-yet-sent input rather than typing into a torn-down term.
          this.pendingInput = "";
          return;
        }
        const chunk = this.pendingInput;
        this.pendingInput = "";
        try {
          await sendPrompt(this.sessionId, chunk, false);
          this.keystrokeFailureReported = false;
        } catch (err) {
          console.debug("[cockpit-terminal] keystroke send failed", this.sessionId, err);
          this.reportKeystrokeFailure(err);
        }
      }
    } finally {
      this.inputPumping = false;
    }
  }

  // Keys typed into the terminal were dropped (the Error Logging mission, issue #3675, ruling R4). Reported once per
  // run of failures, not once per key, with the session and the Gateway's facts - and NEVER the characters: they are
  // what the person typed, and the error store must not hold them. Nothing is shown, so it is not user_visible.
  private reportKeystrokeFailure(err: unknown): void {
    if (this.keystrokeFailureReported) return;
    this.keystrokeFailureReported = true;
    reportClientError({
      surface: "terminal",
      action: "type into the terminal",
      message: gatewayErrorMessage(err, "type into the terminal"),
      user_visible: false,
      ...errorFacts(err),
      session_id: this.sessionId,
    });
  }

  dispose(): void {
    this.wantOpen = false;
    this.pendingInput = ""; // drop any keystrokes not yet sent; the pump exits on its next check
    if (this.resizeObserver !== null) {
      try {
        this.resizeObserver.disconnect();
      } catch {
        /* already gone */
      }
      this.resizeObserver = null;
    }
    if (this.bringUpFrame !== null) {
      // Disposed before the host was ever laid out (the StrictMode throwaway mount): cancel the
      // pending open so no terminal is ever created for this instance.
      window.cancelAnimationFrame(this.bringUpFrame);
      this.bringUpFrame = null;
    }
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.linkProvider !== null) {
      try {
        this.linkProvider.dispose();
      } catch {
        /* already disposed */
      }
      this.linkProvider = null;
    }
    if (this.ws) {
      try {
        this.ws.onclose = null; // do not schedule a reconnect from an intentional close
        this.ws.close();
      } catch {
        /* already closing */
      }
      this.ws = null;
    }
    // Defer the xterm teardown by exactly one animation frame. xterm's Viewport.reset() and
    // Terminal.open() each queue an internal requestAnimationFrame(syncScrollArea) whose handle xterm
    // does NOT retain - so it cannot be cancelled, and syncScrollArea reads the render service's
    // `dimensions`. Disposing synchronously tears the render service down while that frame is still
    // queued, so when it later fires it reads `dimensions` off a disposed terminal and throws the
    // uncaught `TypeError: Cannot read properties of undefined (reading 'dimensions')`. Animation-frame
    // callbacks run in registration order, so a frame scheduled here always runs AFTER any xterm frame
    // queued before it: the pending syncScrollArea flushes harmlessly on the still-live terminal, then
    // this callback disposes it. this.term is nulled now so nothing else touches the terminal in the
    // meantime (the WebSocket is already closed above), and start() is safe to call again on a fresh
    // instance (issue #1029).
    const term = this.term;
    this.term = null;
    if (term !== null) {
      window.requestAnimationFrame(() => {
        try {
          term.dispose();
        } catch {
          /* already disposed */
        }
      });
    }
  }

  // Write a dim status line into the terminal. It is wiped by the next term.reset() (on the first
  // byte of a live stream, or on the next reconnect), so it never lingers over real PTY content.
  private statusLine(text: string): void {
    if (!this.term) return;
    try {
      this.term.write("\r\n\x1b[2m[" + text + "]\x1b[0m\r\n");
    } catch {
      /* mid-dispose */
    }
  }

  // Mirror the PTY grid EXACTLY - both cols and rows from the size message. The grid dimensions must
  // match the owning terminal's or Claude Code's TUI ghosts (see the header note), so they are NEVER
  // derived from the pane; the pane is made to fit the grid instead, by scaling the FONT (fitFont).
  // Preserve the viewport's at-bottom position across the resize.
  private fit(): void {
    const t = this.term;
    if (!t || this.lastCols <= 0 || this.lastRows <= 0) return;
    if (t.cols === this.lastCols && t.rows === this.lastRows) return;
    const buf = t.buffer.active;
    const atBottom = buf.viewportY >= buf.baseY;
    try {
      t.resize(this.lastCols, this.lastRows);
    } catch {
      /* transient */
    }
    // The grid changed dimensions, so re-fit the font so the (possibly larger) grid still fits the
    // pane (pane-size changes are handled separately by the ResizeObserver). fitFont re-anchors to the
    // bottom itself when it changes the font; when it leaves the font unchanged we restore the
    // at-bottom position after the resize.
    if (!this.fitFont() && atBottom) {
      try {
        t.scrollToBottom();
      } catch {
        /* transient */
      }
    }
  }

  // Scale the font so the WHOLE PTY grid fits the pane in both axes. The grid's cols and rows mirror
  // the owning terminal exactly (fit) and must not change, so the only free variable is the font
  // size: shrink it until cols*cellWidth <= pane width AND rows*cellHeight <= pane height. This makes
  // xterm's own viewport the single, correctly-sized scroll surface - its scrollbar sits at the pane
  // edge (never overlapped by content that overflows past it) and its native "stick to the bottom on
  // new output unless the user scrolled up" behaviour works, because the viewport is no longer taller
  // than the visible pane. Without this the grid overflowed the pane: the top rows were clipped and
  // unreachable, and the scrollbar was painted over by the overflowing screen and could not be
  // grabbed (issue #1962).
  //
  // The font never grows past FONT_SIZE (readability ceiling) and never shrinks below MIN_FONT_SIZE
  // (a grid too big even then is left to overflow rather than become illegible). Cell metrics are
  // read from the rendered DOM (screen width / cols, screen height / rows) rather than xterm private
  // internals, so this stays correct across xterm versions. Returns true when it changed the font.
  private fitFont(): boolean {
    const t = this.term;
    if (!t || this.lastCols <= 0 || this.lastRows <= 0) return false;
    const screen = this.hostEl.querySelector<HTMLElement>(".xterm-screen");
    if (!screen) return false;

    // The available box is the host's content area (its padding is not usable for glyphs). A 2px
    // safety margin per axis absorbs sub-pixel rounding so the fitted grid never overflows by a
    // hair and reintroduces the scrollbar overlap.
    const style = window.getComputedStyle(this.hostEl);
    const padX = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
    const padY = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
    const availW = this.hostEl.clientWidth - padX - 2;
    const availH = this.hostEl.clientHeight - padY - 2;
    if (availW <= 0 || availH <= 0) return false;

    const currentFont = t.options.fontSize ?? FONT_SIZE;
    // Cell size AT THE CURRENT FONT, measured from what xterm actually rendered.
    const cellW = screen.scrollWidth / this.lastCols;
    const cellH = screen.scrollHeight / this.lastRows;
    if (cellW <= 0 || cellH <= 0) return false;

    // Largest font (per axis) whose grid still fits, derived by scaling the current cell metrics.
    const fontByWidth = (currentFont * (availW / this.lastCols)) / cellW;
    const fontByHeight = (currentFont * (availH / this.lastRows)) / cellH;
    let target = Math.floor(Math.min(fontByWidth, fontByHeight, FONT_SIZE));
    if (target < MIN_FONT_SIZE) target = MIN_FONT_SIZE;
    if (target === currentFont) return false;

    const buf = t.buffer.active;
    const atBottom = buf.viewportY >= buf.baseY;
    try {
      t.options.fontSize = target;
    } catch {
      return false;
    }
    // Changing the font re-lays out the grid; keep the live input box in view if we were at the bottom.
    if (atBottom) {
      try {
        t.scrollToBottom();
      } catch {
        /* transient */
      }
    }
    return true;
  }

  // The first frame of a LIVE stream (a size header or PTY bytes) proves the whole
  // browser -> Gateway -> Director path is up: wipe the "connecting..." status and clear the failure
  // streak. Replay starts at byte 0, so resetting here loses nothing.
  private markLive(): void {
    if (this.gotFirstByte) return;
    this.gotFirstByte = true;
    this.attempts = 0;
    this.announcedSlow = false; // a live stream ends the outage; the next one re-announces the slow probe
    if (this.term) {
      try {
        this.term.reset();
      } catch {
        /* mid-dispose */
      }
    }
  }

  private openWs(): void {
    if (!this.wantOpen) return;
    const t = this.term;
    if (!t) return;

    // The per-machine token rides as the cc-gateway-token cookie so the same-origin WS handshake
    // authenticates (a browser cannot set an Authorization header on a WebSocket).
    ensureGatewayCookie();

    t.reset(); // each connection replays full history from byte 0
    this.gotFirstByte = false;

    const url = streamUrl(this.sessionId);
    const wsHost = wsHostOf(url);
    // wsHost is the GATEWAY this page was served from, not the owning Director - the Gateway proxies
    // on to the Director. Word it as the path so a loopback Gateway host is never mistaken for the
    // stream's real target.
    this.statusLine(
      this.attempts > 0
        ? "stream lost, reconnecting via gateway " + wsHost + " (attempt " + (this.attempts + 1) + ")..."
        : "connecting via gateway " + wsHost + "...",
    );

    let sock: WebSocket;
    try {
      sock = new WebSocket(url);
    } catch (err) {
      this.statusLine("cannot open stream: " + (err instanceof Error ? err.message : String(err)));
      return;
    }
    sock.binaryType = "arraybuffer";
    this.ws = sock;

    sock.onmessage = (ev: MessageEvent) => {
      if (typeof ev.data === "string") {
        let m: { type?: string; cols?: number; rows?: number; reason?: string };
        try {
          m = JSON.parse(ev.data);
        } catch {
          return;
        }
        // A "closed" control frame is the Gateway reporting WHY the owning Director is unreachable -
        // it is NOT proof of a live stream, so it must not reset the reconnect streak (onclose counts
        // it). Surface the real reason instead of a bare reconnect to the Gateway's own host.
        if (m.type === "closed") {
          try {
            t.write("\r\n[stream closed: " + (m.reason || "") + "]\r\n");
          } catch {
            /* mid-dispose */
          }
          return;
        }
        this.markLive();
        if (m.type === "size" && (m.cols ?? 0) > 0 && (m.rows ?? 0) > 0) {
          this.lastCols = m.cols ?? this.lastCols;
          this.lastRows = m.rows ?? this.lastRows;
          this.fit();
        }
        return;
      }
      this.markLive();
      try {
        t.write(new Uint8Array(ev.data as ArrayBuffer));
      } catch {
        /* mid-dispose */
      }
    };

    sock.onclose = (ev: CloseEvent) => {
      if (this.ws === sock) this.ws = null;
      if (!this.wantOpen || this.reconnectTimer !== null) return;
      this.attempts += 1;
      // Past the fast cap we do NOT stop: drop to a slow keepalive probe so the stream resumes on its
      // own when the Gateway returns - no manual re-selection, no page reload (issue #1032). Announce
      // the transition once per outage; markLive clears the flag so a later outage re-announces.
      const slow = this.attempts > MAX_RECONNECT_ATTEMPTS;
      if (slow && !this.announcedSlow) {
        this.announcedSlow = true;
        this.statusLine(
          "stream via gateway " + wsHost + " is down after " + MAX_RECONNECT_ATTEMPTS +
            " attempts (last close code " + ev.code + ") - now retrying every " +
            Math.round(SLOW_RECONNECT_DELAY_MS / 1000) +
            "s; it resumes automatically when the gateway returns.",
        );
      }
      this.reconnectTimer = window.setTimeout(() => {
        this.reconnectTimer = null;
        if (this.wantOpen) this.openWs();
      }, slow ? SLOW_RECONNECT_DELAY_MS : RECONNECT_DELAY_MS);
    };

    sock.onerror = () => {
      try {
        sock.close();
      } catch {
        /* already closing */
      }
    };
  }
}
