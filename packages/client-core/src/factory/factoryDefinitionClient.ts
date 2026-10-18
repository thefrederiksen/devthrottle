// A factory's definition (Factory Definition mission, phase 6): the typed client the Cockpit's Definition tab reads
// and writes through. The types mirror src/CcDirector.Gateway.Contracts/FactoryDefinitionTabDtos.cs and, for what
// changes, FactoryDefinitionChangesDtos.cs.
//
// THREE SETS OF ROUTES, ON PURPOSE.
//   * The VIEWS - the tab, one page of the history, one version - are the owner's screen reads under
//     /gateway/factories/{factory}/definition. Every word in them is the Gateway's (FactoryDefinitionTabFold); the
//     tab draws them and decides nothing.
//   * PUBLISH and RESTORE go to the definition's own routes under /factory-definitions/{factory}, the same ones the
//     command line uses. Who is asking is read there from the device key; a factory's own session may do neither. The answer
//     carries what changed and what the Gateway applied; a refusal carries the Gateway's sentence and, when the check
//     refused it, every finding by file and line.
//   * EXPORT and IMPORT move a zip of the folder through /gateway/factories/{factory}/definition/export and /import.
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";
import type { FactoryTone } from "./factoryAgentsClient";

// ---------- what changes (FactoryDefinitionChangesDtos.cs) ----------

export interface FactoryDefinitionDiffLine {
  /** "context", "added", "removed" or "skipped". */
  kind: string;
  marker: string;
  text: string;
  oldLine: number | null;
  newLine: number | null;
}

export interface FactoryDefinitionTextDiff {
  added: number;
  removed: number;
  lines: FactoryDefinitionDiffLine[];
  linesNotShown: number;
  note: string | null;
}

export interface FactoryDefinitionFieldChange {
  field: string;
  sentence: string;
  diff: FactoryDefinitionTextDiff | null;
}

export interface FactoryDefinitionSeatChange {
  seat: string;
  change: string;
  sentence: string;
  details: string[];
  briefDiff: FactoryDefinitionTextDiff | null;
}

export interface FactoryDefinitionStartChange {
  seat: string;
  change: string;
  sentence: string;
  details: string[];
}

export interface FactoryDefinitionRuleChange {
  ruleSet: string | null;
  seat: string | null;
  direction: string;
  sentence: string;
}

export interface FactoryDefinitionItemChange {
  id: string;
  change: string;
  sentence: string;
  details: string[];
}

export interface FactoryDefinitionFileChange {
  path: string;
  kind: string;
  change: string;
  sentence: string;
  diff: FactoryDefinitionTextDiff;
}

export interface FactoryDefinitionChanges {
  summary: string;
  nothingChanges: boolean;
  firstPublish: boolean;
  factory: FactoryDefinitionFieldChange[];
  seats: FactoryDefinitionSeatChange[];
  starts: FactoryDefinitionStartChange[];
  rules: FactoryDefinitionRuleChange[];
  grants: FactoryDefinitionItemChange[];
  files: FactoryDefinitionFileChange[];
}

export interface FactoryDefinitionFinding {
  /** "error" or "notice". */
  severity: string;
  file: string;
  line: number;
  message: string;
}

export interface FactoryDefinitionPlan {
  version: number;
  act: string;
  heading: string;
  steps: string[];
  refused: string | null;
  findings: FactoryDefinitionFinding[];
}

// ---------- the tab (FactoryDefinitionTabDtos.cs) ----------

/** Publish or restore, and the confirm it asks first. Every sentence is the Gateway's. */
export interface FactoryDefinitionAction {
  version: number;
  expectedVersion: number;
  /** The published version the confirm was composed against; sent as `replaces`. */
  replaces: number | null;
  label: string;
  busyLabel: string;
  confirmTitle: string;
  confirmLines: string[];
  confirmLabel: string;
  doneText: string;
}

export interface FactoryDefinitionVersionCard {
  version: number;
  title: string;
  lineText: string;
  noteText: string | null;
  stateWord: string;
  stateTone: FactoryTone;
  openLabel: string;
}

export interface FactoryDefinitionTabChanges {
  heading: string;
  changes: FactoryDefinitionChanges | null;
  plan: FactoryDefinitionPlan | null;
  cannotCompareText: string | null;
}

export interface FactoryDefinitionExportAction {
  label: string;
  busyLabel: string;
  href: string;
  noteText: string;
  leavesOut: string[];
}

export interface FactoryDefinitionImportAction {
  label: string;
  noteText: string;
  href: string;
  busyLabel: string;
}

export interface FactoryDefinitionTabView {
  factory: string;
  heading: string;
  stateText: string;
  emptyText: string | null;
  published: FactoryDefinitionVersionCard | null;
  draft: FactoryDefinitionVersionCard | null;
  changes: FactoryDefinitionTabChanges | null;
  publish: FactoryDefinitionAction | null;
  historyLabel: string | null;
  export: FactoryDefinitionExportAction;
  import: FactoryDefinitionImportAction;
  footnoteText: string;
}

export interface FactoryDefinitionHistoryRow {
  version: number;
  title: string;
  stateWord: string;
  stateTone: FactoryTone;
  whoText: string;
  whenText: string;
  noteText: string | null;
  sizeText: string;
  openLabel: string;
  exportLabel: string;
  exportHref: string;
  publish: FactoryDefinitionAction | null;
  restore: FactoryDefinitionAction | null;
}

export interface FactoryDefinitionHistoryView {
  heading: string;
  summaryText: string;
  pageText: string | null;
  columns: string[];
  rows: FactoryDefinitionHistoryRow[];
  emptyText: string | null;
  nextBefore: number | null;
  olderLabel: string | null;
  newestLabel: string | null;
  closeLabel: string;
}

export interface FactoryDefinitionFileView {
  path: string;
  sizeText: string;
  text: string;
}

export interface FactoryDefinitionVersionView {
  version: number;
  title: string;
  lineText: string;
  noteText: string | null;
  files: FactoryDefinitionFileView[];
  exportLabel: string;
  exportHref: string;
  closeLabel: string;
}

/** What a publish or a restore that went through answers (FactoryDefinitionDto), as far as the tab shows it. */
export interface FactoryDefinitionPublished {
  factory: string;
  version: number;
  /** Notices the check found; they never refuse. */
  findings: FactoryDefinitionFinding[];
  changes: FactoryDefinitionChanges | null;
  /** One sentence per step the publish transaction took. */
  applied: string[] | null;
}

/** What an import from the tab answers. */
export interface FactoryDefinitionImported {
  factory: string;
  version: number;
  doneText: string;
  findings: FactoryDefinitionFinding[];
  /** Set by New factory: the new factory's page, on its Definition tab. */
  openHref?: string | null;
}

/**
 * The Gateway refused a publish, a restore or an import. The message is the Gateway's own sentence; `findings` are the
 * check's (or a refused plan's), each with its file and line, when the refusal came from one.
 */
export class FactoryDefinitionRefusal extends GatewayError {
  readonly outcome: string | null;
  readonly findings: FactoryDefinitionFinding[];

  constructor(status: number, message: string, outcome: string | null, findings: FactoryDefinitionFinding[]) {
    // A refusal of this kind is about how the definition stands, so retrying unchanged cannot help.
    super(status, message, { reason: message, code: outcome ?? undefined, retryable: false });
    this.name = "FactoryDefinitionRefusal";
    this.outcome = outcome;
    this.findings = findings;
  }
}

interface RefusalBody {
  error?: string;
  detail?: string;
  outcome?: string;
  findings?: FactoryDefinitionFinding[] | null;
}

async function refusalFrom(res: Response): Promise<FactoryDefinitionRefusal> {
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
  const message = body.detail !== undefined ? `${reason} ${body.detail}` : reason;
  return new FactoryDefinitionRefusal(res.status, message, body.outcome ?? null, body.findings ?? []);
}

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) throw await refusalFrom(res);
  // A 2XX is not proof the Gateway understood the request: a Gateway from before this phase answers an unknown path
  // with the app's own HTML shell.
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not show a factory's definition - it answered with a web page instead of data. Upgrade or redeploy the Gateway.",
    );
  return (await res.json()) as T;
}

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
  return asJson<T>(res);
}

function viewPath(factory: string, rest: string): string {
  const base = `/gateway/factories/${encodeURIComponent(factory)}/definition`;
  return rest.length === 0 ? base : `${base}/${rest}`;
}

function definitionPath(factory: string, rest: string): string {
  return `/factory-definitions/${encodeURIComponent(factory)}/${rest}`;
}

/** The Definition tab. */
export function getFactoryDefinitionTab(factory: string, signal?: AbortSignal): Promise<FactoryDefinitionTabView> {
  return send<FactoryDefinitionTabView>("GET", viewPath(factory, ""), undefined, signal);
}

/** One page of the history, newest first; `before` is the `nextBefore` of the page before it. */
export function getFactoryDefinitionHistory(
  factory: string,
  before: number | null,
  signal?: AbortSignal,
): Promise<FactoryDefinitionHistoryView> {
  const path = viewPath(factory, "history");
  return send<FactoryDefinitionHistoryView>("GET", before === null ? path : `${path}?before=${before}`, undefined, signal);
}

/** One version, with every file. */
export function getFactoryDefinitionVersion(
  factory: string,
  version: number,
  signal?: AbortSignal,
): Promise<FactoryDefinitionVersionView> {
  return send<FactoryDefinitionVersionView>("GET", viewPath(factory, `versions/${version}`), undefined, signal);
}

/** Publish one version, as the confirm named it. The answer says what the Gateway applied. */
export function publishFactoryDefinition(
  factory: string,
  action: FactoryDefinitionAction,
  signal?: AbortSignal,
): Promise<FactoryDefinitionPublished> {
  return send<FactoryDefinitionPublished>(
    "POST",
    definitionPath(factory, "publish"),
    { version: action.version, replaces: action.replaces },
    signal,
  );
}

/** Restore an older version as a published copy, as the confirm named it. */
export function restoreFactoryDefinition(
  factory: string,
  action: FactoryDefinitionAction,
  signal?: AbortSignal,
): Promise<FactoryDefinitionPublished> {
  return send<FactoryDefinitionPublished>(
    "POST",
    definitionPath(factory, "restore"),
    { version: action.version, expectedVersion: action.expectedVersion, replaces: action.replaces },
    signal,
  );
}

/** Send a zip of a definition folder; it is stored as a draft and never published. */
export async function importFactoryDefinitionZip(
  href: string,
  zip: Blob,
  signal?: AbortSignal,
): Promise<FactoryDefinitionImported> {
  const res = await gatewayFetch(href, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/zip", ...authHeaders() },
    body: zip,
    signal,
  });
  return asJson<FactoryDefinitionImported>(res);
}

/** The zip's name from the answer's Content-Disposition, or null when it names none. */
export function zipNameOf(disposition: string): string | null {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  return match === null ? null : decodeURIComponent(match[1]);
}

/** Fetch the zip the Gateway built and hand it to the browser to save. */
export async function downloadFactoryDefinitionZip(href: string, signal?: AbortSignal): Promise<void> {
  const res = await gatewayFetch(href, { method: "GET", headers: { Accept: "application/zip", ...authHeaders() }, signal });
  if (!res.ok) throw await refusalFrom(res);
  const blob = await res.blob();
  const name = zipNameOf(res.headers.get("Content-Disposition") ?? "");
  if (name === null)
    throw new GatewayError(502, "The Gateway sent the export without a file name. Upgrade or redeploy the Gateway.");
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
