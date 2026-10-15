// A factory's records (Factory Definition mission, phase 6): the typed client the Cockpit's Records tab reads through.
// The types mirror src/CcDirector.Gateway.Contracts/FactoryRecordsTabDtos.cs.
//
// The tab ONLY READS, from the owner's screen routes under /gateway/factories/{factory}/records. Every word in an
// answer is the Gateway's (FactoryRecordsTabFold); the tab draws it and decides nothing. Records are written by the
// factory's sessions through the command line, never from this screen.
//
// A DOCUMENT'S NAME TRAVELS IN THE QUERY, NEVER THE PATH: it is itself a path (outreach/ledger.json).
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";
import type { FactoryTone } from "./factoryAgentsClient";

export interface FactoryRecordsJournalGroup {
  /** The seat id, or null for the journal the factory keeps as a whole. */
  seat: string | null;
  title: string;
  summaryText: string;
  openLabel: string;
}

export interface FactoryRecordsDocumentRow {
  name: string;
  versionText: string;
  whoText: string;
  whenText: string;
  sizeText: string;
  openLabel: string;
  /** Null when the document has one version only. */
  historyLabel: string | null;
}

export interface FactoryRecordsAttachmentRow {
  id: string;
  name: string;
  seatText: string;
  contentType: string;
  sizeText: string;
  whoText: string;
  whenText: string;
  downloadLabel: string;
  downloadHref: string;
}

export interface FactoryRecordsSection {
  heading: string;
  summaryText: string;
  emptyText: string | null;
}

export interface FactoryRecordsTabView {
  factory: string;
  heading: string;
  introText: string;
  journals: FactoryRecordsSection;
  journalGroups: FactoryRecordsJournalGroup[];
  documents: FactoryRecordsSection;
  documentColumns: string[];
  documentRows: FactoryRecordsDocumentRow[];
  attachments: FactoryRecordsSection;
  attachmentColumns: string[];
  attachmentRows: FactoryRecordsAttachmentRow[];
}

export interface FactoryRecordsJournalEntry {
  id: string;
  dateText: string;
  lineText: string;
  text: string;
  attachments: FactoryRecordsAttachmentRow[];
}

export interface FactoryRecordsJournalView {
  heading: string;
  summaryText: string;
  entries: FactoryRecordsJournalEntry[];
  emptyText: string | null;
  closeLabel: string;
}

export interface FactoryRecordsDocumentVersionRow {
  version: number;
  title: string;
  stateWord: string;
  stateTone: FactoryTone;
  whoText: string;
  whenText: string;
  sizeText: string;
  openLabel: string;
}

export interface FactoryRecordsDocumentHistoryView {
  name: string;
  heading: string;
  summaryText: string;
  columns: string[];
  rows: FactoryRecordsDocumentVersionRow[];
  nextBefore: number | null;
  olderLabel: string | null;
  newestLabel: string | null;
  closeLabel: string;
}

export interface FactoryRecordsDocumentView {
  name: string;
  version: number;
  title: string;
  lineText: string;
  text: string;
  historyLabel: string | null;
  closeLabel: string;
}

async function read<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await gatewayFetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await refusalFrom(res);
  // A 2XX is not proof the Gateway understood the request: a Gateway from before this phase answers an unknown path
  // with the app's own HTML shell.
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not show a factory's records - it answered with a web page instead of data. Upgrade or redeploy the Gateway.",
    );
  return (await res.json()) as T;
}

async function refusalFrom(res: Response): Promise<GatewayError> {
  let raw = "";
  let error: string | undefined;
  try {
    raw = await res.text();
    if (raw.length > 0) error = (JSON.parse(raw) as { error?: string }).error;
  } catch {
    /* not JSON - the raw text, or the status code, is what there is to say */
  }
  // The Gateway's own sentence, whole and unchanged.
  return new GatewayError(res.status, error ?? (raw.length > 0 ? raw : `The Gateway answered ${res.status}`));
}

function recordsPath(factory: string, rest: string): string {
  const base = `/gateway/factories/${encodeURIComponent(factory)}/records`;
  return rest.length === 0 ? base : `${base}/${rest}`;
}

/** The Records tab. */
export function getFactoryRecordsTab(factory: string, signal?: AbortSignal): Promise<FactoryRecordsTabView> {
  return read<FactoryRecordsTabView>(recordsPath(factory, ""), signal);
}

/** One journal, newest day first: a seat's, or with `seat` null the factory's own. */
export function getFactoryRecordsJournal(
  factory: string,
  seat: string | null,
  signal?: AbortSignal,
): Promise<FactoryRecordsJournalView> {
  const path = recordsPath(factory, "journal");
  return read<FactoryRecordsJournalView>(seat === null ? path : `${path}?seat=${encodeURIComponent(seat)}`, signal);
}

/** One version of a working document with its text; `version` null is the newest. */
export function getFactoryRecordsDocument(
  factory: string,
  name: string,
  version: number | null,
  signal?: AbortSignal,
): Promise<FactoryRecordsDocumentView> {
  const path = `${recordsPath(factory, "documents/read")}?name=${encodeURIComponent(name)}`;
  return read<FactoryRecordsDocumentView>(version === null ? path : `${path}&version=${version}`, signal);
}

/** One page of a document's versions, newest first; `before` is the `nextBefore` of the page before it. */
export function getFactoryRecordsDocumentHistory(
  factory: string,
  name: string,
  before: number | null,
  signal?: AbortSignal,
): Promise<FactoryRecordsDocumentHistoryView> {
  const path = `${recordsPath(factory, "documents/history")}?name=${encodeURIComponent(name)}`;
  return read<FactoryRecordsDocumentHistoryView>(before === null ? path : `${path}&before=${before}`, signal);
}

/** The file's name from the answer's Content-Disposition, or null when it names none. */
export function attachmentNameOf(disposition: string): string | null {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  return match === null ? null : decodeURIComponent(match[1]);
}

/** Fetch an attachment at the address the Gateway gave and hand it to the browser to save. */
export async function downloadFactoryAttachment(href: string, signal?: AbortSignal): Promise<void> {
  const res = await gatewayFetch(href, { method: "GET", headers: authHeaders(), signal });
  if (!res.ok) throw await refusalFrom(res);
  const blob = await res.blob();
  const name = attachmentNameOf(res.headers.get("Content-Disposition") ?? "");
  if (name === null)
    throw new GatewayError(502, "The Gateway sent the attachment without a file name. Upgrade or redeploy the Gateway.");
  const url = URL.createObjectURL(blob);
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}
