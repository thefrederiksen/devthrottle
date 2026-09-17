// The host side of the dev report protocol (CONTRACT.md section 3), in types and shape checks.
//
// The note-taking script checks every message it receives from the host whole; this file is the same
// discipline in the other direction. A message from the page that is not exactly one of the shapes below
// is ignored whole - nothing is half-applied, and nothing a report forges reaches the Gateway.

export const DEV_REPORT_CHANNEL = "devthrottle.dev-report";
export const DEV_REPORT_PROTOCOL_VERSION = 1;
const MAX_STRING = 20000;
const ANCHOR_TYPES = ["element", "text", "table-cell", "svg-part"];

export interface DevReportAnchor {
  type: "element" | "text" | "table-cell" | "svg-part";
  selector: string;
  quote: string;
  rowLabel?: string;
  columnLabel?: string;
  label?: string;
}

export interface DevReportNoteItem {
  id: string;
  kind: "note";
  text: string;
  anchor: DevReportAnchor;
}

export interface DevReportAnswerItem {
  id: string;
  kind: "answer";
  questionId: string;
  question: string;
  optionValue: string;
  optionLabel: string;
  comment: string;
}

export type DevReportItem = DevReportNoteItem | DevReportAnswerItem;

export type DevReportQueuedItem = DevReportItem & { pending?: boolean; statusLabel?: string };
export type DevReportSentItem = DevReportItem & { status: string; statusLabel: string };

export interface DevReportPageReply {
  id: string;
  text: string;
  at: string;
}

export interface DevReportPageState {
  queued: DevReportQueuedItem[];
  sent: DevReportSentItem[];
  replies: DevReportPageReply[];
  draft: { anchor: DevReportAnchor; text: string } | null;
  answerDrafts: { questionId: string; optionValue: string; comment: string }[];
  scroll: { x: number; y: number };
}

export interface DevReportStatusUpdate {
  id: string;
  status: string;
  statusLabel: string;
}

export function emptyPageState(): DevReportPageState {
  return { queued: [], sent: [], replies: [], draft: null, answerDrafts: [], scroll: { x: 0, y: 0 } };
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return v !== null && typeof v === "object" && !Array.isArray(v);
}

function isStr(v: unknown): v is string {
  return typeof v === "string" && v.length <= MAX_STRING;
}

function isOptStr(v: unknown): boolean {
  return v === undefined || isStr(v);
}

function isFiniteNumber(v: unknown): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

function everyValid(list: unknown, check: (v: unknown) => boolean): boolean {
  return Array.isArray(list) && list.every(check);
}

export function isValidAnchor(a: unknown): a is DevReportAnchor {
  return (
    isPlainObject(a) &&
    ANCHOR_TYPES.includes(a.type as string) &&
    isStr(a.selector) &&
    isStr(a.quote) &&
    isOptStr(a.rowLabel) &&
    isOptStr(a.columnLabel) &&
    isOptStr(a.label)
  );
}

export function isValidItem(item: unknown): item is DevReportItem {
  if (!isPlainObject(item) || !isStr(item.id) || item.id === "") return false;
  if (item.kind === "note") return isStr(item.text) && isValidAnchor(item.anchor);
  if (item.kind === "answer") {
    return (
      isStr(item.questionId) &&
      item.questionId !== "" &&
      isStr(item.question) &&
      isStr(item.optionValue) &&
      isStr(item.optionLabel) &&
      isStr(item.comment)
    );
  }
  return false;
}

function isValidQueuedItem(item: unknown): boolean {
  return (
    isValidItem(item) &&
    isPlainObject(item) &&
    (item.pending === undefined || typeof item.pending === "boolean") &&
    isOptStr(item.statusLabel)
  );
}

function isValidSentItem(item: unknown): boolean {
  return isValidItem(item) && isPlainObject(item) && isStr(item.status) && isStr(item.statusLabel);
}

function isValidReply(r: unknown): boolean {
  return isPlainObject(r) && isStr(r.id) && r.id !== "" && isStr(r.text) && isStr(r.at);
}

function isValidAnswerDraft(d: unknown): boolean {
  return isPlainObject(d) && isStr(d.questionId) && d.questionId !== "" && isStr(d.optionValue) && isStr(d.comment);
}

/** The same state check the note-taking script applies to a restore, so the host never keeps - or hands
 *  back - a state the page would refuse. */
export function isValidPageState(s: unknown): s is DevReportPageState {
  if (!isPlainObject(s)) return false;
  if (
    !everyValid(s.queued, isValidQueuedItem) ||
    !everyValid(s.sent, isValidSentItem) ||
    !everyValid(s.replies, isValidReply) ||
    !everyValid(s.answerDrafts, isValidAnswerDraft)
  ) {
    return false;
  }
  if (s.draft !== null && !(isPlainObject(s.draft) && isValidAnchor(s.draft.anchor) && isStr(s.draft.text))) {
    return false;
  }
  return isPlainObject(s.scroll) && isFiniteNumber(s.scroll.x) && isFiniteNumber(s.scroll.y);
}

/** A message from the page, as the host acts on it. */
export type PageMessage =
  | { type: "ready"; token: string; questionIds: string[] }
  | { type: "send"; items: DevReportItem[] }
  | { type: "state-changed"; state: DevReportPageState };

function envelopeOk(data: unknown): data is Record<string, unknown> {
  return isPlainObject(data) && data.channel === DEV_REPORT_CHANNEL && data.version === DEV_REPORT_PROTOCOL_VERSION;
}

/** Returns the message when it is exactly a `ready` (token and question ids checked), else null. */
export function parseReady(data: unknown): Extract<PageMessage, { type: "ready" }> | null {
  if (!envelopeOk(data) || data.type !== "ready" || typeof data.token !== "string") return null;
  const p = data.payload;
  if (!isPlainObject(p) || !everyValid(p.questionIds, isStr)) return null;
  return { type: "ready", token: data.token, questionIds: p.questionIds as string[] };
}

/** Returns a `send` or `state-changed` from the port when its shape is exactly right, else null. */
export function parsePortMessage(data: unknown): Extract<PageMessage, { type: "send" | "state-changed" }> | null {
  if (!envelopeOk(data)) return null;
  const p = data.payload;
  if (!isPlainObject(p)) return null;
  if (data.type === "send") {
    return everyValid(p.items, isValidItem) ? { type: "send", items: p.items as DevReportItem[] } : null;
  }
  if (data.type === "state-changed") {
    return isValidPageState(p.state) ? { type: "state-changed", state: p.state } : null;
  }
  return null;
}

export function hostEnvelope(type: "restore" | "status" | "reply", payload: unknown) {
  return { channel: DEV_REPORT_CHANNEL, version: DEV_REPORT_PROTOCOL_VERSION, type, payload };
}
