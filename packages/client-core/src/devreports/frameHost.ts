// The frame host: what an app must do to show a dev report, CONTRACT.md section 4, rules 1 to 6.
//
// It is framework-free on purpose, so the trust rules are tested on their own and not through React. It is a
// port of the test host in browser-tests/dev-report-notes-proof/index.html, whose browser proof watched each
// rule fail with the rule removed; the logic is the same, not a new design.
//
//   1. The report is shown in <iframe sandbox="allow-scripts">, never with allow-same-origin.
//   2. The host writes the frame's head first - the policy and the injected script - and then the report's
//      bytes untouched. It never searches the report for a place to insert anything.
//   3. A Content-Security-Policy with a fresh nonce per load lets only the injected script run.
//   4. A fresh token per load. On the window, only a ready from the frame, with the current token and
//      exactly one port, is accepted - and then the token is forgotten (one ready per load).
//   5. Everything after that goes over that port, both ways. Nothing is ever posted to the frame's window.
//   6. A frame load the host did not cause closes the port, and the frame is ignored until the host loads
//      the report again.

import {
  hostEnvelope,
  parsePortMessage,
  parseReady,
  type DevReportItem,
  type DevReportPageReply,
  type DevReportPageState,
  type DevReportStatusUpdate,
} from "./protocol";
import type { DevReportTheme } from "./theme";

/** The parts of a MessagePort the host uses. */
export interface HostPort {
  postMessage(message: unknown): void;
  close(): void;
  onmessage: ((event: MessageEvent) => void) | null;
}

/** The parts of a window message event the host reads. */
export interface HostMessageEvent {
  source: unknown;
  data: unknown;
  ports: readonly unknown[];
}

export interface DevReportFrameHostOptions {
  /** The element the host puts its frame in. The host creates the frame itself (see load). */
  container: HTMLElement;
  /** The frame's accessible title. */
  title: string;
  /** The app window the frame posts its ready to. */
  window: Window;
  /** The note-taking script's source (dev-report-notes.js, bundled from its one file). */
  script: string;
  theme: DevReportTheme;
  /** The state to hand back in restore after every ready. */
  restoreState: () => DevReportPageState;
  onStateChanged: (state: DevReportPageState) => void;
  onSend: (items: DevReportItem[]) => void;
  onConnectedChange?: (connected: boolean) => void;
  /** Told about every message the host refused, for tests and diagnostics. */
  onRefused?: (why: string) => void;
  /** A fresh random string per call. Defaults to 128 random bits as hexadecimal. */
  random?: () => string;
}

export const DEV_REPORT_POLICY = (nonce: string) =>
  `default-src 'none'; script-src 'nonce-${nonce}'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'`;

export function randomHex(): string {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

function escapeAttribute(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

/**
 * The frame's whole document: the host's own head, closed, then the report's bytes untouched (rule 2).
 * The nonce and token are hexadecimal, and the theme is escaped into its attribute.
 */
export function buildFrameDocument(script: string, reportHtml: string, nonce: string, token: string, theme: DevReportTheme): string {
  if (!/^[0-9a-f]+$/.test(nonce) || !/^[0-9a-f]+$/.test(token)) {
    throw new Error("buildFrameDocument: the nonce and the token must be hexadecimal");
  }
  if (/<\/script/i.test(script)) {
    throw new Error("buildFrameDocument: the note-taking script contains a closing script tag and cannot be injected inline");
  }
  return (
    "<!doctype html><html><head>" +
    `<meta http-equiv="Content-Security-Policy" content="${DEV_REPORT_POLICY(nonce)}">` +
    `<script nonce="${nonce}" data-dev-report-token="${token}" data-dev-report-theme="${escapeAttribute(JSON.stringify(theme))}">` +
    script +
    "</script></head>" +
    reportHtml
  );
}

export class DevReportFrameHost {
  private readonly options: DevReportFrameHostOptions;
  /** The frame this host owns. Public so a test and a shell's styles can find it; never written to by anyone else. */
  readonly frame: HTMLIFrameElement;
  private readonly random: () => string;
  private token = "";
  private port: HostPort | null = null;
  private expectingLoad = false;
  private disposed = false;

  private readonly onWindowMessage = (event: MessageEvent) => this.handleWindowMessage(event);
  private readonly onFrameLoad = () => this.handleFrameLoad();

  constructor(options: DevReportFrameHostOptions) {
    this.options = options;
    this.random = options.random ?? randomHex;
    // Rule 1. The host makes the frame itself, so no markup outside it decides the sandbox. It is not put in
    // the page until its first document is set (load), because an empty frame put in a page fires a load for
    // about:blank - in some browsers at once, in others later - and a load that could arrive either side of
    // the report's own would make rule 6 guess which one it was.
    const frame = options.container.ownerDocument.createElement("iframe");
    frame.setAttribute("sandbox", "allow-scripts");
    frame.setAttribute("title", options.title);
    frame.setAttribute("data-testid", "dev-report-frame");
    frame.className = "dev-report-frame";
    this.frame = frame;
    options.window.addEventListener("message", this.onWindowMessage);
    frame.addEventListener("load", this.onFrameLoad);
  }

  get connected(): boolean {
    return this.port !== null;
  }

  /** Loads (or reloads) the report: a fresh nonce and token, the old port closed, the document written. */
  load(reportHtml: string): void {
    if (this.disposed) throw new Error("DevReportFrameHost.load: the host has been disposed");
    const nonce = this.random();
    this.token = this.random();
    this.closePort();
    this.expectingLoad = true;
    this.frame.srcdoc = buildFrameDocument(this.options.script, reportHtml, nonce, this.token, this.options.theme);
    if (!this.frame.isConnected) this.options.container.appendChild(this.frame);
  }

  /** Pushes status words to the page. False, and nothing sent, when there is no port to the loaded report. */
  pushStatus(updates: DevReportStatusUpdate[]): boolean {
    return this.post("status", { updates });
  }

  /** Pushes a reply to the page. False, and nothing sent, when there is no port to the loaded report. */
  pushReply(reply: DevReportPageReply): boolean {
    return this.post("reply", { reply });
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.token = "";
    this.closePort();
    this.options.window.removeEventListener("message", this.onWindowMessage);
    this.frame.removeEventListener("load", this.onFrameLoad);
    this.frame.remove();
  }

  /** Rules 4 and 5. Public so the trust rules can be tested with exact events. */
  handleWindowMessage(event: HostMessageEvent): void {
    if (this.disposed) return;
    if (!this.frame.contentWindow || event.source !== this.frame.contentWindow) return;
    const ready = parseReady(event.data);
    if (!ready) return this.refuse("only a well-formed ready is accepted on the window");
    if (!this.token || ready.token !== this.token) return this.refuse(this.token ? "wrong token" : "no ready is expected");
    if (!event.ports || event.ports.length !== 1) return this.refuse("a ready must carry exactly one port");
    this.token = ""; // one ready per load
    this.closePort();
    const port = event.ports[0] as HostPort;
    port.onmessage = (e: MessageEvent) => this.handlePortMessage(port, e.data);
    this.port = port;
    // Restore first, so anything the app pushes when it hears the page is connected lands on restored state.
    this.post("restore", { state: this.options.restoreState() });
    this.options.onConnectedChange?.(true);
  }

  /** Rule 6. Public for tests. */
  handleFrameLoad(): void {
    if (this.disposed) return;
    if (this.expectingLoad) {
      this.expectingLoad = false;
      return;
    }
    this.token = "";
    this.closePort();
    this.refuse("the frame loaded something this host did not put there; the port is closed");
  }

  private handlePortMessage(port: HostPort, data: unknown): void {
    if (this.disposed || port !== this.port) return;
    const message = parsePortMessage(data);
    if (!message) return this.refuse("a port message that is not a well-formed send or state-changed");
    if (message.type === "state-changed") this.options.onStateChanged(message.state);
    else this.options.onSend(message.items);
  }

  private post(type: "restore" | "status" | "reply", payload: unknown): boolean {
    if (!this.port) return false;
    this.port.postMessage(hostEnvelope(type, payload));
    return true;
  }

  private closePort(): void {
    if (!this.port) return;
    this.port.onmessage = null;
    this.port.close();
    this.port = null;
    this.options.onConnectedChange?.(false);
  }

  private refuse(why: string): void {
    this.options.onRefused?.(why);
  }
}
