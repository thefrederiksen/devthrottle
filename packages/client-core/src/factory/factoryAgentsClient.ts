// The Factory Agents area of the Gateway (Website Business Factory, product track): the typed, same-origin
// client the Cockpit's Factory Agents pages read.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Every word, number, grouping and status on these pages is folded once on
// the Gateway (FactoryAgentsFold) and arrives here finished. These types mirror
// src/CcDirector.Gateway.Contracts/FactoryAgentsViewDtos.cs; a page renders them verbatim and never counts,
// groups, colours or decides what a state means. A "tone" is the one word a page maps to a colour class.
import { authHeaders, GatewayError } from "../api/client";

export type FactoryTone =
  | "ok"
  | "working"
  | "idle"
  | "paused"
  | "amber"
  | "red"
  | "grey"
  | "neutral"
  | "blue";

export interface FactoryAgentsSwitch {
  enabled: boolean;
}

export interface FactoryTab {
  key: string;
  label: string;
}

export interface FactoryChoice {
  value: string;
  label: string;
}

export interface FactoryWindow {
  key: string;
  label: string;
  fromUtc: string;
  toUtc: string;
  fromLocal: string;
  toLocal: string;
  choices: FactoryChoice[];
}

export interface FactoryNumber {
  text: string;
  tone: FactoryTone;
  target: "waiting" | "activity" | null;
  outcome: string | null;
  /** The Cockpit route the number opens, or null when it opens nothing. */
  href: string | null;
}

export interface FactoryPause {
  action: "pause" | "resume";
  label: string;
  busyLabel: string;
  confirm: string | null;
}

export interface FactoryAgentRow {
  factoryId: string;
  factoryTitle: string;
  agentId: string;
  name: string;
  wokenBy: string;
  lastRun: string;
  statusWord: string;
  statusTone: FactoryTone;
  href: string;
}

export interface FactoryCard {
  id: string;
  title: string;
  statusWord: string;
  statusTone: FactoryTone;
  subtitle: string;
  faultText: string | null;
  numbers: FactoryNumber[];
  agents: FactoryAgentRow[];
  pause: FactoryPause | null;
  waitingHref: string;
}

export interface FactoriesView {
  title: string;
  subtitle: string;
  tabs: FactoryTab[];
  window: FactoryWindow;
  factories: FactoryCard[];
  allAgents: FactoryAgentRow[];
  emptyText: string | null;
}

export interface FactoryWokenBy {
  triggerId: string;
  text: string;
  statusWord: string;
  statusTone: FactoryTone;
  statusText: string | null;
  lastCheck: string;
}

export interface FactoryActivityLine {
  key: string;
  time: string;
  factoryTitle: string;
  who: string;
  what: string;
  subject: string | null;
  outcomeWord: string;
  outcomeTone: FactoryTone;
  collapsed: boolean;
  sessionId: string | null;
  sessionLabel: string | null;
  link: string | null;
  note: string | null;
}

export interface FactoryAgentPage {
  factoryId: string;
  factoryTitle: string;
  agentId: string;
  name: string;
  statusWord: string;
  statusTone: FactoryTone;
  definitionText: string;
  wokenBy: FactoryWokenBy[];
  wokenByEmptyText: string | null;
  lastCheck: string | null;
  last7DaysTitle: string;
  last7Days: FactoryNumber[];
  pause: FactoryPause | null;
  pauseUnavailableText: string | null;
  askLabel: string;
  askHref: string;
  recentTitle: string;
  recent: FactoryActivityLine[];
  recentEmptyText: string | null;
}

export interface FactoryFilters {
  factory: string | null;
  agent: string | null;
  outcome: string | null;
  factoryChoices: FactoryChoice[];
  agentChoices: FactoryChoice[];
  outcomeChoices: FactoryChoice[];
}

export interface FactoryActivityView {
  window: FactoryWindow;
  filters: FactoryFilters;
  rows: FactoryActivityLine[];
  faults: string[];
  emptyText: string | null;
  truncatedText: string | null;
  footnote: string;
  csvHref: string;
  saveLabel: string;
}

export interface FactoryWaitingItem {
  id: string;
  word: string;
  tone: FactoryTone;
  factoryTitle: string;
  subject: string | null;
  what: string;
  by: string;
  sessionId: string | null;
  sessionLabel: string | null;
  link: string | null;
  linkLabel: string | null;
  handledLabel: string | null;
  handledBusyLabel: string | null;
}

export interface FactoryWaitingView {
  factoryId: string | null;
  title: string;
  summary: string;
  items: FactoryWaitingItem[];
  emptyText: string | null;
  truncatedText: string | null;
}

export interface FactoryReportRow {
  key: string;
  name: string;
  cells: string[];
}

export interface SavedFactoryReport {
  id: string;
  name: string;
  description: string;
  href: string;
  savedText: string;
}

export interface FactoryReportView {
  window: FactoryWindow;
  filters: FactoryFilters;
  tableTitle: string;
  columns: string[];
  rows: FactoryReportRow[];
  total: FactoryReportRow | null;
  emptyText: string | null;
  truncatedText: string | null;
  faults: string[];
  csvHref: string;
  saveLabel: string;
  savedTitle: string;
  saved: SavedFactoryReport[];
  savedEmptyText: string | null;
  openedReportName: string | null;
}

/** The "factory agent" chip the Gateway stamps on a session a factory agent started (Screen 6). */
export interface SessionFactoryAgent {
  label: string;
  text: string;
  title: string;
  href: string;
}

/** The filter a view is asked for. Empty strings mean "every". */
export interface FactoryQuery {
  factory?: string;
  agent?: string;
  outcome?: string;
  window?: string;
  from?: string;
  to?: string;
  report?: string;
}

const PREFIX = "/gateway/factory-agents";

/** Build the query string the Gateway reads; empty values are left out. */
export function factoryQueryString(q: FactoryQuery): string {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(q)) {
    if (typeof v === "string" && v.length > 0) params.set(k, v);
  }
  const s = params.toString();
  return s.length === 0 ? "" : `?${s}`;
}

async function errorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string };
        detail = body.error ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// A 2XX is not proof the Gateway understood the request: a Gateway from before this area answers unknown paths
// with the app's own HTML shell. Every read asserts it got JSON.
async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await errorFrom(res, `GET ${path}`);
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not serve the Factory Agents area - it answered with a web page instead of data. Upgrade or redeploy the Gateway.",
    );
  return (await res.json()) as T;
}

async function postJson<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw await errorFrom(res, `POST ${path}`);
  return (await res.json()) as T;
}

export function getFactoryAgentsSwitch(signal?: AbortSignal): Promise<FactoryAgentsSwitch> {
  return getJson<FactoryAgentsSwitch>(`${PREFIX}/switch`, signal);
}

export function getFactories(q: FactoryQuery, signal?: AbortSignal): Promise<FactoriesView> {
  return getJson<FactoriesView>(`${PREFIX}/factories${factoryQueryString(q)}`, signal);
}

export function getFactoryAgent(factory: string, agent: string, signal?: AbortSignal): Promise<FactoryAgentPage> {
  return getJson<FactoryAgentPage>(
    `${PREFIX}/factories/${encodeURIComponent(factory)}/agents/${encodeURIComponent(agent)}`,
    signal,
  );
}

export function getFactoryActivity(q: FactoryQuery, signal?: AbortSignal): Promise<FactoryActivityView> {
  return getJson<FactoryActivityView>(`${PREFIX}/activity${factoryQueryString(q)}`, signal);
}

export function getFactoryWaiting(factory: string | undefined, signal?: AbortSignal): Promise<FactoryWaitingView> {
  return getJson<FactoryWaitingView>(`${PREFIX}/waiting${factoryQueryString({ factory })}`, signal);
}

export function getFactoryReports(q: FactoryQuery, signal?: AbortSignal): Promise<FactoryReportView> {
  return getJson<FactoryReportView>(`${PREFIX}/reports${factoryQueryString(q)}`, signal);
}

export async function markFactoryItemHandled(id: string, signal?: AbortSignal): Promise<void> {
  await postJson<unknown>(`${PREFIX}/waiting/${encodeURIComponent(id)}/handled`, undefined, signal);
}

export function saveFactoryReport(
  input: { name: string; factory?: string; agent?: string; outcome?: string; window?: string; fromUtc?: string; toUtc?: string },
  signal?: AbortSignal,
): Promise<SavedFactoryReport> {
  return postJson<SavedFactoryReport>(`${PREFIX}/reports`, input, signal);
}

/** Pause or resume a factory (every trigger it has), or one factory agent (every trigger that wakes it). */
export async function setFactoryPaused(
  action: "pause" | "resume",
  factory: string,
  agent?: string,
  signal?: AbortSignal,
): Promise<void> {
  const path =
    agent === undefined
      ? `${PREFIX}/factories/${encodeURIComponent(factory)}/${action}`
      : `${PREFIX}/factories/${encodeURIComponent(factory)}/agents/${encodeURIComponent(agent)}/${action}`;
  await postJson<unknown>(path, undefined, signal);
}

/** Fetch the CSV the Gateway built (every row, never the collapsed lines) and hand it to the browser to save. */
export async function downloadFactoryCsv(href: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch(href, { method: "GET", headers: { Accept: "text/csv", ...authHeaders() }, signal });
  if (!res.ok) throw await errorFrom(res, "Export CSV");
  const blob = await res.blob();
  const disposition = res.headers.get("Content-Disposition") ?? "";
  const match = /filename="([^"]+)"/.exec(disposition);
  const url = URL.createObjectURL(blob);
  try {
    const a = document.createElement("a");
    a.href = url;
    a.download = match?.[1] ?? "factory-activity.csv";
    document.body.appendChild(a);
    a.click();
    a.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}
