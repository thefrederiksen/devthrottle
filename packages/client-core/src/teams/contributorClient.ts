// A CONTRIBUTOR'S FACTORIES AND INBOX (Team implementation piece 4.1): the routes under /teams/{teamId}/contributor, as
// the contributor's two pages read them, in the Cockpit and on the phone alike.
//
// COMBINED ACROSS TEAMS. A person who is a contributor on several teams sees one Factories page and one Inbox: each of
// their teams is read on its own route, and the answers are shown together. Which teams take part is the Gateway's
// verdict - a team whose page list (from GET /teams) names the page - and the ORDER is the Gateway's too: every card and
// item carries an `order` key, and the combined list is sorted by that key alone. Every word, label and tone on screen is
// the Gateway's and is typed here as the plain value it sends; nothing in this client decides what a state means
// (repository rule 7).

import type { TeamSummary } from "./teamsClient";
import type { TeamQuestion } from "./teamQuestionsClient";
import { call } from "./invitationsClient";
import { authHeaders, GatewayError, gatewayFetch, POLL_TIMEOUT_MS } from "../api/client";
import type { FactoryWatchView } from "../factory/watch/factoryWatchTypes";

/** One factory sent to the person, as a card on the Factories page. */
export interface ContributorFactoryCard {
  teamId: string;
  /** The team's name, shown beside the factory. */
  teamLabel: string;
  factory: string;
  name: string;
  /** The newest line written for the person about what the factory is doing, or null. */
  nowText: string | null;
  /** "1 thing needs you", or null when nothing waits on them here. */
  needsYouLabel: string | null;
  /** The words of the card's "Watch your factory" button, which opens the watch view of this factory. */
  watchLabel: string;
  /** The Gateway's sort key for the combined list. */
  order: string;
}

/** One team's Factories answer. */
export interface ContributorFactories {
  teamId: string;
  teamLabel: string;
  count: number;
  /** The line under the page's title. */
  intro: string;
  emptyText: string;
  factories: ContributorFactoryCard[];
}

/** A titled part of the customer page, with the sentence it shows when it is empty. */
export interface CustomerSection<T> {
  title: string;
  emptyText: string;
  items: T[];
}

/** One factory's customer page. */
export interface CustomerPage {
  teamId: string;
  teamLabel: string;
  factory: string;
  name: string;
  writeLabel: string;
  needsYou: CustomerSection<{ reportId: string; title: string; actionLabel: string }>;
  stage: { title: string; steps: { label: string; state: string }[]; note: string | null };
  today: CustomerSection<{ text: string; atUtc: string; timeLabel: string }>;
  soFar: CustomerSection<{ label: string; href: string }>;
  sentToYou: CustomerSection<{ reportId: string; title: string; stateLabel: string; tone: string }>;
  canDo: { title: string; text: string; note: string };
}

/** One step of a conversation item: what the team's Owner did with something the person wrote. */
export interface ContributorTrailStep {
  label: string;
  by: string;
  atUtc: string;
  reason: string | null;
  sentence: string;
}

/** One Inbox item: a report sent to the person, or something they wrote to the team's Owner. */
export interface ContributorInboxItem {
  kind: "report" | "message";
  id: string;
  teamId: string;
  teamLabel: string;
  title: string;
  /** The person's own words, for a message; null for a report. */
  text: string | null;
  fromLabel: string;
  atUtc: string;
  stateLabel: string;
  /** How the state is drawn: "waiting", "new", "done" or "message". */
  tone: string;
  trail: ContributorTrailStep[];
  /** The Gateway's sentence on a message the team's Owner forwarded to the factory; null otherwise. */
  notice: string | null;
  /** A report's questions for this person, waiting first, in the order to draw them; empty for a message. */
  questions: TeamQuestion[];
  order: string;
}

/** One team's Inbox answer. */
export interface ContributorInbox {
  teamId: string;
  teamLabel: string;
  count: number;
  emptyText: string;
  /** The sentence under the page's title. */
  intro: string;
  /** The box for writing to the team's Owner: its words, all the Gateway's. */
  write: { label: string; placeholder: string; sendLabel: string; note: string };
  items: ContributorInboxItem[];
}

const base = (teamId: string) => `/teams/${encodeURIComponent(teamId)}/contributor`;

/** GET /teams/{teamId}/contributor/factories - the factories sent to the person in one team. */
export function getContributorFactories(teamId: string, signal?: AbortSignal): Promise<ContributorFactories> {
  return call<ContributorFactories>("GET", `${base(teamId)}/factories`, "load your factories", undefined, signal);
}

/** GET /teams/{teamId}/contributor/inbox - what was sent to the person in one team, and their conversation. */
export function getContributorInbox(teamId: string, signal?: AbortSignal): Promise<ContributorInbox> {
  return call<ContributorInbox>("GET", `${base(teamId)}/inbox`, "load your inbox", undefined, signal);
}

/** The Gateway's one sentence for a factory that is not the person's to see - whether it exists or not. */
export interface CustomerPageRefused {
  refused: string;
}

/** GET /teams/{teamId}/contributor/factories/{factory} - one customer page, or the Gateway's refusal sentence. */
export async function getCustomerPage(teamId: string, factory: string, signal?: AbortSignal): Promise<CustomerPage | CustomerPageRefused> {
  const res = await gatewayFetch(
    `${base(teamId)}/factories/${encodeURIComponent(factory)}`,
    { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (res.status === 404) {
    const failure = await GatewayError.from(res, "load this factory");
    if (failure.serverReason === null || failure.serverReason === undefined) throw failure;
    return { refused: failure.serverReason };
  }
  if (!res.ok) throw await GatewayError.from(res, "load this factory");
  return (await res.json()) as CustomerPage;
}

/** GET /teams/{teamId}/contributor/factories/{factory}/watch - "Watch your factory" for one factory sent to the person,
 *  carrying the explainer card until they have seen it; or the Gateway's refusal sentence, the same one the customer
 *  page gives, whatever the reason. */
export async function getContributorWatch(teamId: string, factory: string, signal?: AbortSignal): Promise<FactoryWatchView | CustomerPageRefused> {
  const res = await gatewayFetch(
    `${base(teamId)}/factories/${encodeURIComponent(factory)}/watch`,
    { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (res.status === 404) {
    const failure = await GatewayError.from(res, "load the watch view of this factory");
    if (failure.serverReason === null || failure.serverReason === undefined) throw failure;
    return { refused: failure.serverReason };
  }
  if (!res.ok) throw await GatewayError.from(res, "load the watch view of this factory");
  return (await res.json()) as FactoryWatchView;
}

/** POST /teams/{teamId}/contributor/factories/{factory}/watch/explainer-seen - the person pressed the explainer card's
 *  button. The Gateway remembers it for the person, so the card is not shown again on any team, here or on the phone. */
export async function markWatchExplainerSeen(teamId: string, factory: string): Promise<void> {
  const res = await gatewayFetch(
    `${base(teamId)}/factories/${encodeURIComponent(factory)}/watch/explainer-seen`,
    { method: "POST", headers: { Accept: "application/json", ...authHeaders() } },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (res.status !== 204) throw await GatewayError.from(res, "remember that you have read the card");
}

/** The teams whose page list names `pageId`: the teams that take part in the combined page, in the Gateway's order. */
export function teamsWithPage(teams: readonly TeamSummary[], pageId: string): TeamSummary[] {
  return teams.filter((t) => t.app.pages.some((p) => p.id === pageId));
}

/** Every team's items in one list, in the order the Gateway's keys give. */
export function combineByOrder<T extends { order: string }>(lists: readonly (readonly T[])[]): T[] {
  return lists.flat().sort((a, b) => (a.order < b.order ? -1 : a.order > b.order ? 1 : 0));
}

/** Read one page from every team that takes part, and combine the answers. A team that fails fails the whole read: a
 *  combined page that silently leaves a team out would read as "nothing was sent to you there". */
export async function readCombined<A, T extends { order: string }>(
  teams: readonly TeamSummary[],
  read: (teamId: string, signal?: AbortSignal) => Promise<A>,
  itemsOf: (answer: A) => readonly T[],
  signal?: AbortSignal,
): Promise<{ answers: A[]; items: T[] }> {
  const answers = await Promise.all(teams.map((t) => read(t.id, signal)));
  return { answers, items: combineByOrder(answers.map(itemsOf)) };
}
