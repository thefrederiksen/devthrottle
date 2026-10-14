// A seat's brief (Factory Seats mission, phase 2b): the typed client the Cockpit's seat page reads and writes
// its brief panel through.
//
// TWO SETS OF ROUTES, ON PURPOSE.
//   * The VIEWS - what the editor starts from, one page of the history, one version - are the owner's screen
//     reads under /gateway/factories/{factory}/seats/{seat}/brief. Every word in them is the Gateway's
//     (FactoryBriefPanelFold, mirrored from FactorySeatBriefViewDtos.cs); the page draws them and decides nothing.
//   * The WRITES - save a draft, publish, bring back - go to the brief's own routes under
//     /factory-briefs/{factory}/{seat}, the same ones a session of the factory uses. Who is asking is read there
//     from the device key, and only a person publishes.
//
// A STALE SAVE IS NOT AN ERROR TO SWALLOW. The Gateway answers 409 with the newest version under "current", so
// the owner can merge rather than overwrite what a session wrote meanwhile. That answer is thrown as a
// FactoryBriefRefusal carrying `current`, so the editor can show it beside the owner's own text.
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";
import type { FactoryTone } from "./factoryAgentsClient";

/** One act a person confirms first - publish, or bring an older version back. Every sentence is the Gateway's. */
export interface FactoryBriefAction {
  /** The version the act is about. */
  version: number;
  /** The seat's newest version when the confirm was composed; a bring back sends it. */
  expectedVersion: number;
  label: string;
  busyLabel: string;
  confirmTitle: string;
  confirmLines: string[];
  confirmLabel: string;
  /** What the page says once the Gateway has done it. */
  doneText: string;
}

/** What the brief's editor starts from. */
export interface FactoryBriefEditorView {
  heading: string;
  /** Which version the text starts from, said plainly. */
  startText: string;
  text: string;
  /** The seat's newest version: what a save states as the version it read. 0 for a seat with no brief here. */
  expectedVersion: number;
  textLabel: string;
  limitText: string;
  waitingText: string | null;
  saveLabel: string;
  saveBusyLabel: string;
  savedText: string;
  publish: FactoryBriefAction;
  cancelLabel: string;
  conflictText: string;
  yoursLabel: string;
  theirsLabel: string;
  saveOverLabel: string;
  takeTheirsLabel: string;
}

export interface FactoryBriefHistoryRow {
  version: number;
  title: string;
  stateWord: string;
  stateTone: FactoryTone;
  whoText: string;
  whenText: string;
  sizeText: string;
  openLabel: string;
  publish: FactoryBriefAction | null;
  bringBack: FactoryBriefAction | null;
}

/** One page of a seat's history, newest first. */
export interface FactoryBriefHistoryView {
  heading: string;
  summaryText: string;
  pageText: string | null;
  waitingText: string | null;
  columns: string[];
  rows: FactoryBriefHistoryRow[];
  emptyText: string | null;
  /** What to ask for as `before` to read the next, older page; null when this page reaches the oldest version. */
  nextBefore: number | null;
  olderLabel: string | null;
  newestLabel: string | null;
  closeLabel: string;
}

/** One version, with its text. */
export interface FactoryBriefVersionView {
  version: number;
  title: string;
  lineText: string;
  text: string;
  closeLabel: string;
}

/** One stored version, as the brief's own routes answer a write (FactoryBriefDto). */
export interface FactoryBriefVersion {
  factory: string;
  seat: string;
  version: number;
  text: string;
  /** "draft" or "published". */
  state: string;
  authorKind: string;
  authorId: string | null;
  writtenAtUtc: string;
  publishedAtUtc: string | null;
  publishedBy: string | null;
  newestVersion: number;
  publishedVersion: number | null;
}

/**
 * The Gateway refused a save, a publish or a bring back. `outcome` is the store's own word for why ("Stale" when a
 * newer version exists, "AlreadyPublished", "OnlyAPersonPublishes" ...), and `current` is the version the Gateway
 * sent with the refusal - for a stale save, the newest one, which is what the owner merges against.
 */
export class FactoryBriefRefusal extends GatewayError {
  readonly outcome: string | null;
  readonly current: FactoryBriefVersion | null;

  constructor(status: number, message: string, outcome: string | null, current: FactoryBriefVersion | null) {
    // The sentence is the Gateway's reason, so gatewayErrorMessage shows it rather than a line made up from the
    // status code. A refusal of this kind is about how the brief stands, so retrying unchanged cannot help.
    super(status, message, { reason: message, code: outcome ?? undefined, retryable: false });
    this.name = "FactoryBriefRefusal";
    this.outcome = outcome;
    this.current = current;
  }
}

function viewPath(factory: string, seat: string, rest: string): string {
  return `/gateway/factories/${encodeURIComponent(factory)}/seats/${encodeURIComponent(seat)}/brief/${rest}`;
}

function briefPath(factory: string, seat: string, rest: string): string {
  return `/factory-briefs/${encodeURIComponent(factory)}/${encodeURIComponent(seat)}/${rest}`;
}

interface RefusalBody {
  error?: string;
  detail?: string;
  outcome?: string;
  current?: FactoryBriefVersion | null;
}

async function refusalFrom(res: Response): Promise<FactoryBriefRefusal> {
  let body: RefusalBody = {};
  let raw = "";
  try {
    raw = await res.text();
    if (raw.length > 0) body = JSON.parse(raw) as RefusalBody;
  } catch {
    /* not JSON - the raw text, or the status code, is what there is to say */
  }
  // The Gateway's own sentence, whole and unchanged: it is shown as it is.
  const reason = body.error ?? (raw.length > 0 ? raw : `The Gateway answered ${res.status}`);
  const message = body.detail !== undefined ? `${reason}. ${body.detail}` : reason;
  return new FactoryBriefRefusal(res.status, message, body.outcome ?? null, body.current ?? null);
}

// A 2XX is not proof the Gateway understood the request: a Gateway from before this mission answers an unknown
// path with the app's own HTML shell. Every answer asserts it got JSON.
async function send<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await gatewayFetch(path, {
    method,
    headers: {
      Accept: "application/json",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...authHeaders(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw await refusalFrom(res);
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not hold a seat's brief - it answered with a web page instead of data. Upgrade or redeploy the Gateway.",
    );
  return (await res.json()) as T;
}

/** What the editor starts from: the newest version, said plainly, or nothing for a first brief. */
export function getFactoryBriefEditor(factory: string, seat: string, signal?: AbortSignal): Promise<FactoryBriefEditorView> {
  return send<FactoryBriefEditorView>("GET", viewPath(factory, seat, "editor"), undefined, signal);
}

/**
 * One page of the seat's history, newest first. `before` is the `nextBefore` of the page before it; leave it out
 * for the newest page. The Gateway decides how many versions a page holds and whether an older one follows.
 */
export function getFactoryBriefHistory(
  factory: string,
  seat: string,
  before: number | null,
  signal?: AbortSignal,
): Promise<FactoryBriefHistoryView> {
  const path = viewPath(factory, seat, "history");
  return send<FactoryBriefHistoryView>("GET", before === null ? path : `${path}?before=${before}`, undefined, signal);
}

/** One version, with its text. */
export function getFactoryBriefVersion(
  factory: string,
  seat: string,
  version: number,
  signal?: AbortSignal,
): Promise<FactoryBriefVersionView> {
  return send<FactoryBriefVersionView>("GET", viewPath(factory, seat, `versions/${version}`), undefined, signal);
}

/** Save a draft, stating the version last read. A stale save throws a FactoryBriefRefusal with `current`. */
export function saveFactoryBriefDraft(
  factory: string,
  seat: string,
  text: string,
  expectedVersion: number,
  signal?: AbortSignal,
): Promise<FactoryBriefVersion> {
  return send<FactoryBriefVersion>("PUT", briefPath(factory, seat, "draft"), { text, expectedVersion }, signal);
}

/** Publish one version: from then on it is what a talk or a run of the seat is given. A person's act. */
export function publishFactoryBrief(
  factory: string,
  seat: string,
  version: number,
  signal?: AbortSignal,
): Promise<FactoryBriefVersion> {
  return send<FactoryBriefVersion>("POST", briefPath(factory, seat, "publish"), { version }, signal);
}

/**
 * Bring an older version back: a copy of it becomes the newest version, published. `expectedVersion` is the newest
 * version the person was looking at when they chose, and sending it is the point: a session can save a draft
 * between the history opening and the button being pressed, and the Gateway then refuses instead of burying it.
 */
export function bringBackFactoryBrief(
  factory: string,
  seat: string,
  version: number,
  expectedVersion: number,
  signal?: AbortSignal,
): Promise<FactoryBriefVersion> {
  return send<FactoryBriefVersion>("POST", briefPath(factory, seat, "restore"), { version, expectedVersion }, signal);
}
