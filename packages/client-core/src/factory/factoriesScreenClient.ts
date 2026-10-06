// The Factories screen (Factories screen mission, phase D): the typed, same-origin client the Cockpit's Factories
// pages read - the list of factories, one factory's page, its Seats tab, and the Talk button.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. These types mirror src/CcDirector.Gateway.Contracts/FactoriesScreenDtos.cs
// (the views, folded by FactoriesScreenFold) and the Talk answer in FactoryRegistryDtos.cs. Every word, tone and order
// arrives finished; a page renders it verbatim and never counts, sorts or decides what a status means.
import { authHeaders, GatewayError } from "../api/client";
import type { FactoryTab, FactoryTone, FactoryWaitingItem } from "./factoryAgentsClient";

/** What a Talk button starts: the Cockpit sends the two ids back and never works out which agent a button means. */
export interface FactoryTalkTarget {
  /** "Talk to Nora Hale", "Talk to the CEO", or "Talk" on a seat row. */
  label: string;
  factoryId: string;
  seatId: string;
}

export interface FactoryListRow {
  id: string;
  title: string;
  /** Exactly one of FAILING, NEEDS YOU, PAUSED, RUNNING. */
  statusWord: string;
  statusTone: FactoryTone;
  statusReason: string;
  /** "1 question", "2 decisions", or "-". */
  waitingText: string;
  href: string;
  talk: FactoryTalkTarget | null;
  /** "No CEO" when talk is null. */
  noCeoText: string | null;
}

export interface FactoriesListView {
  title: string;
  subtitle: string;
  /** Factories, Activity, Reports. */
  tabs: FactoryTab[];
  /** "Factory", "Waiting on you", "Status". */
  columns: string[];
  /** Worst first, as the Gateway sorted them. */
  rows: FactoryListRow[];
  footerText: string | null;
  emptyText: string | null;
  truncatedText: string | null;
}

export interface FactoryGoalCard {
  heading: string;
  text: string | null;
  note: string | null;
  emptyText: string | null;
}

export interface FactoryGoalNumberCard {
  heading: string;
  valueText: string | null;
  asOfText: string | null;
  linkHref: string | null;
  linkLabel: string | null;
  emptyText: string | null;
}

export interface FactoryPageWaiting {
  heading: string;
  items: FactoryWaitingItem[];
  emptyText: string | null;
}

export interface FactoryCeoLatest {
  heading: string;
  lines: string[];
  emptyText: string | null;
  allLabel: string | null;
  allHref: string | null;
}

export interface FactoryLastTalk {
  heading: string;
  text: string;
}

export interface FactoryPageView {
  id: string;
  title: string;
  /** "Factories / WarmForward". */
  crumb: string;
  crumbHref: string;
  statusWord: string;
  statusTone: FactoryTone;
  statusReason: string;
  /** "CEO Nora Hale" or "No CEO". */
  ceoText: string;
  /** "4 seats". */
  seatCountText: string;
  /** "runs on SOREN_NORTH". */
  computerText: string;
  /** "change - coming": a label, never a control. */
  computerChangeText: string;
  talk: FactoryTalkTarget | null;
  /** Overview, Seats (n), Activity, Reports, Memory, Documents. */
  tabs: FactoryTab[];
  goal: FactoryGoalCard;
  goalNumber: FactoryGoalNumberCard;
  waiting: FactoryPageWaiting;
  ceoLatest: FactoryCeoLatest;
  lastTalk: FactoryLastTalk;
  /** What the Documents tab says: the definitions are not on the Gateway yet. */
  documentsText: string;
  truncatedText: string | null;
}

export interface FactorySeatRow {
  seatId: string;
  name: string;
  role: string;
  whenText: string;
  lastRunText: string;
  lastRunTone: FactoryTone;
  computerText: string;
  /** "change - coming": a label, never a control. */
  computerChangeText: string;
  talk: FactoryTalkTarget;
}

export interface FactorySeatsView {
  factoryId: string;
  title: string;
  /** "Factories / WarmForward / Seats". */
  crumb: string;
  /** "Seat", "When it runs", "Last run", "Computer". */
  columns: string[];
  rows: FactorySeatRow[];
  note: string;
}

/** The Gateway's answer to a Talk: the top-level session it started, and where the Cockpit opens it. */
export interface FactoryTalkStarted {
  sessionId: string;
  sessionName: string;
  /** "/session/<id>". */
  href: string;
  factory: string;
  seat: string;
  computer: string;
  directorId: string;
}

const PREFIX = "/gateway/factories";
const TALK_PREFIX = "/gateway/factory-agents/factories";

// A 2XX is not proof the Gateway understood the request: a Gateway from before this screen answers unknown paths with
// the app's own HTML shell. Every read asserts it got JSON. A refusal keeps the Gateway's own sentence.
async function getJson<T>(path: string, what: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await GatewayError.from(res, what);
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not serve the Factories screen - it answered with a web page instead of data. Upgrade or redeploy the Gateway.",
    );
  return (await res.json()) as T;
}

export function getFactoriesList(signal?: AbortSignal): Promise<FactoriesListView> {
  return getJson<FactoriesListView>(PREFIX, "load the factories", signal);
}

export function getFactoryPage(factory: string, signal?: AbortSignal): Promise<FactoryPageView> {
  return getJson<FactoryPageView>(`${PREFIX}/${encodeURIComponent(factory)}`, "load this factory", signal);
}

export function getFactorySeats(factory: string, signal?: AbortSignal): Promise<FactorySeatsView> {
  return getJson<FactorySeatsView>(`${PREFIX}/${encodeURIComponent(factory)}/seats`, "load this factory's seats", signal);
}

/**
 * Press Talk: the Gateway starts a new top-level session owned by the person who pressed it, seated as that seat.
 * A refusal (no Director on the seat's computer, an unknown seat, the switch off) throws a GatewayError carrying the
 * Gateway's own sentence.
 */
export async function startFactoryTalk(target: FactoryTalkTarget, signal?: AbortSignal): Promise<FactoryTalkStarted> {
  const path = `${TALK_PREFIX}/${encodeURIComponent(target.factoryId)}/seats/${encodeURIComponent(target.seatId)}/talk`;
  const res = await fetch(path, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "start the talk");
  return (await res.json()) as FactoryTalkStarted;
}
