// One open dev report: the Gateway record, the frame host, and the owner's unsent draft, joined up.
//
// Framework-free, like the host, so the whole loop - load, restore, send, refresh, republish - is tested
// without React. The React viewer only subscribes to it.
//
// What it decides, and what it does not:
//   - It never decides what a state means. Status and statusLabel go to the page exactly as the Gateway sent
//     them, and the conversation panel renders the Gateway's record of the items and replies.
//   - When a send request itself fails - no answer, a network error, a Gateway error - the Gateway ruled on
//     nothing. Every item is answered "refused" with the failure as its label, so it stays queued and can be
//     sent again (CONTRACT.md section 3, "Send"). That is the contract's word for "the host does not have
//     this", not an invented Gateway state.
//   - When the Gateway's version rises above the loaded one, the new page is fetched and loaded in place,
//     with a fresh nonce and token, and the draft carried across (handoff ruling 4).

import { gatewayErrorMessage, GatewayError } from "../api/client";
import type { DevReportDetail, DevReportHtml, DevReportSendUpdate } from "./devReportsClient";
import { DevReportFrameHost } from "./frameHost";
import {
  emptyPageState,
  type DevReportItem,
  type DevReportNoteMode,
  type DevReportNoteModeRequest,
  type DevReportPageState,
  type DevReportStatusUpdate,
} from "./protocol";
import type { DevReportStateStore } from "./stateStore";
import type { DevReportTheme } from "./theme";

/** The Gateway calls the controller makes. The owner routes in production; fakes in tests. */
export interface DevReportApi {
  getDetail(reportId: string, signal?: AbortSignal): Promise<DevReportDetail | null>;
  getHtml(reportId: string, version: number, signal?: AbortSignal): Promise<DevReportHtml | null>;
  send(reportId: string, items: DevReportItem[]): Promise<DevReportSendUpdate[]>;
}

export interface DevReportSnapshot {
  /** The Gateway's record, as last read. Null until the first read answers. */
  detail: DevReportDetail | null;
  /** The Gateway answered 404: the report does not appear. */
  notFound: boolean;
  /** Why the last read failed, in words, or null. */
  loadError: string | null;
  /** The version the frame shows, or null before the first page has loaded. */
  loadedVersion: number | null;
  /** The page's own state as the host holds it: the queue, drafts and scroll. */
  pageState: DevReportPageState;
  /** The frame's page is connected over its port. */
  connected: boolean;
  /** A send request is in flight. */
  sending: boolean;
  /** Why the last send request failed, or null. */
  sendError: string | null;
  /**
   * Where note-taking stands in the page (issue #3077). The note controls are in the app's panel, so the
   * panel draws itself from this: `picking` is true while the page is waiting for the reader to click what
   * the note is about, and `selectionQuote` is the text they have selected in the report.
   */
  noteMode: DevReportNoteMode;
}

export interface DevReportControllerOptions {
  reportId: string;
  api: DevReportApi;
  store: DevReportStateStore;
  container: HTMLElement;
  window: Window;
  script: string;
  theme: DevReportTheme;
  /** Passed to the host, for tests. */
  random?: () => string;
  onRefused?: (why: string) => void;
}

function isAbort(err: unknown): boolean {
  return err instanceof Error && err.name === "AbortError";
}

/** The words a failed send request is answered with. A Gateway sentence when there is one. */
function sendFailureLabel(err: unknown): string {
  if (err instanceof GatewayError) return gatewayErrorMessage(err, "send these notes");
  const detail = err instanceof Error && err.message ? ` (${err.message})` : "";
  return `Not sent: the Gateway could not be reached${detail}. Press Send to try again.`;
}

export class DevReportController {
  readonly host: DevReportFrameHost;
  private readonly options: DevReportControllerOptions;
  private snapshot: DevReportSnapshot = {
    detail: null,
    notFound: false,
    loadError: null,
    loadedVersion: null,
    pageState: emptyPageState(),
    connected: false,
    sending: false,
    sendError: null,
    noteMode: { picking: false, selectionQuote: null },
  };
  private readonly listeners = new Set<() => void>();
  private loadingVersion: number | null = null;
  private sendsInFlight = 0;
  private disposed = false;

  constructor(options: DevReportControllerOptions) {
    this.options = options;
    this.host = new DevReportFrameHost({
      container: options.container,
      title: "Dev report",
      window: options.window,
      script: options.script,
      theme: options.theme,
      random: options.random,
      onRefused: options.onRefused,
      restoreState: () => this.snapshot.pageState,
      onStateChanged: (state) => this.onStateChanged(state),
      onSend: (items) => void this.send(items),
      onNoteModeChanged: (noteMode) => this.update({ noteMode }),
      onConnectedChange: (connected) => {
        // A page that is not connected cannot be picking, and there is nothing selected in it that this app
        // could note. Leaving the panel saying "Cancel" after the page went away would offer an action that
        // reaches nothing.
        this.update(connected ? { connected } : { connected, noteMode: { picking: false, selectionQuote: null } });
        if (connected && this.snapshot.detail) this.pushGatewayWords(this.snapshot.detail);
      },
    });
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  getSnapshot = (): DevReportSnapshot => this.snapshot;

  dispose(): void {
    this.disposed = true;
    this.listeners.clear();
    this.host.dispose();
  }

  /** Reads the Gateway record; loads the page the first time and whenever the version rises. */
  async refresh(signal?: AbortSignal): Promise<void> {
    let detail: DevReportDetail | null;
    try {
      detail = await this.options.api.getDetail(this.options.reportId, signal);
    } catch (err) {
      if (isAbort(err) || this.disposed) return;
      this.update({ loadError: gatewayErrorMessage(err) });
      return;
    }
    if (this.disposed) return;
    if (detail === null) {
      this.update({ notFound: true, loadError: null });
      return;
    }
    this.update({ detail, notFound: false, loadError: null });
    const loaded = this.snapshot.loadedVersion;
    if (loaded === null || detail.report.version > loaded) {
      await this.loadVersion(detail.report.version, signal);
    } else {
      this.pushGatewayWords(detail);
    }
  }

  /**
   * The panel's note controls (issue #3077): arm picking, open a note on the reader's selection, or cancel.
   * The page answers with a note-mode-changed, so nothing here guesses what happened - including the case
   * where there is no page connected and the request goes nowhere.
   */
  setNoteMode(mode: DevReportNoteModeRequest): void {
    this.host.setNoteMode(mode);
  }

  /** The conversation panel's Send: everything still queued, as the page's own Send would post it. */
  sendQueued(): Promise<void> {
    const items = this.snapshot.pageState.queued.map(({ pending: _pending, statusLabel: _label, ...item }) => item as DevReportItem);
    if (items.length === 0) return Promise.resolve();
    return this.send(items);
  }

  private async loadVersion(version: number, signal?: AbortSignal): Promise<void> {
    if (this.loadingVersion !== null && this.loadingVersion >= version) return;
    this.loadingVersion = version;
    try {
      let page: DevReportHtml | null;
      try {
        page = await this.options.api.getHtml(this.options.reportId, version, signal);
      } catch (err) {
        if (isAbort(err) || this.disposed) return;
        this.update({ loadError: gatewayErrorMessage(err) });
        return;
      }
      if (this.disposed) return;
      if (page === null) {
        this.update({ notFound: true });
        return;
      }
      const current = this.snapshot.loadedVersion;
      if (current !== null && page.version <= current) return;
      const saved = this.options.store.load(this.options.reportId, page.version);
      this.update({
        loadedVersion: page.version,
        pageState: saved ?? this.snapshot.pageState,
        // A new page is a new document: nothing is armed and nothing is selected in it, whatever the last one
        // was doing. It says so itself on its first restore, but not before the panel has been drawn once.
        noteMode: { picking: false, selectionQuote: null },
      });
      this.host.load(page.html);
    } finally {
      if (this.loadingVersion === version) this.loadingVersion = null;
    }
  }

  private onStateChanged(state: DevReportPageState): void {
    const version = this.snapshot.loadedVersion;
    if (version === null) return;
    this.options.store.save(this.options.reportId, version, state);
    this.update({ pageState: state });
  }

  private async send(items: DevReportItem[]): Promise<void> {
    if (this.disposed) return;
    this.sendsInFlight++;
    this.update({ sending: true });
    let updates: DevReportStatusUpdate[];
    let sendError: string | null = null;
    try {
      updates = await this.options.api.send(this.options.reportId, items);
    } catch (err) {
      sendError = sendFailureLabel(err);
      updates = items.map((item) => ({ id: item.id, status: "refused", statusLabel: sendError as string }));
    }
    this.sendsInFlight--;
    if (this.disposed) return;
    this.host.pushStatus(updates.map((u) => ({ id: u.id, status: u.status, statusLabel: u.statusLabel })));
    this.update({ sending: this.sendsInFlight > 0, sendError });
    if (sendError === null) await this.refresh();
  }

  /** Pushes the Gateway's current words for items the page holds as sent or waiting, and every new or changed reply. */
  private pushGatewayWords(detail: DevReportDetail): void {
    if (!this.host.connected) return;
    const state = this.snapshot.pageState;
    const recorded = new Map(detail.items.map((item) => [item.id, item]));
    const updates: DevReportStatusUpdate[] = [];
    for (const sent of state.sent) {
      const g = recorded.get(sent.id);
      if (g && (g.status !== sent.status || g.statusLabel !== sent.statusLabel)) {
        updates.push({ id: g.id, status: g.status, statusLabel: g.statusLabel });
      }
    }
    for (const queued of state.queued) {
      const g = recorded.get(queued.id);
      if (g && queued.pending === true) updates.push({ id: g.id, status: g.status, statusLabel: g.statusLabel });
    }
    if (updates.length > 0) this.host.pushStatus(updates);
    for (const reply of detail.replies) {
      const held = state.replies.find((r) => r.id === reply.id);
      if (!held || held.text !== reply.text || held.at !== reply.at) {
        this.host.pushReply({ id: reply.id, text: reply.text, at: reply.at });
      }
    }
  }

  private update(patch: Partial<DevReportSnapshot>): void {
    this.snapshot = { ...this.snapshot, ...patch };
    for (const listener of this.listeners) listener();
  }
}
