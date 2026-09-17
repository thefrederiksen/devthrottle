// The Fleet Manager page (the Fleet Manager mission, step 6): the cards drawn from outcome records, the live right
// panel, the rail's badge count, and answering a card.
//
// Everything here is folded on the Gateway (CLAUDE.md rule 7): every heading, sentence, age, tone, count and
// button - with the exact words it sends. This file only carries it. A refusal throws a GatewayError carrying the
// Gateway's own sentence, which the page shows as it is.
import { authHeaders, GatewayError } from "../api/client";

export interface FleetManagerQuickPrompt {
  label: string;
  /** The exact words sent to the Fleet Manager. */
  words: string;
}

export interface FleetCardFact {
  label: string;
  value: string;
}

export interface FleetCardLink {
  label: string;
  url: string;
}

export interface FleetReadyCard {
  riskLabel: string;
  riskTone: string;
  facts: FleetCardFact[];
  change: string;
  pullRequest: FleetCardLink;
}

export interface FleetFindingCard {
  answer: string;
  reason?: string | null;
  links: FleetCardLink[];
}

export interface FleetDecisionOption {
  text: string;
  recommended: boolean;
}

export interface FleetDecisionCard {
  question?: string | null;
  options: FleetDecisionOption[];
  recommendedLabel: string;
  why?: string | null;
}

export interface FleetCardAction {
  label: string;
  style: "primary" | "secondary" | "ghost";
  /** The exact words sent. Null when the button first asks for the owner's words. */
  words?: string | null;
  asksForWords: boolean;
  /** Put in front of the owner's typed words, so the Fleet Manager knows which card they answer. */
  wordsPrefix?: string | null;
  placeholder?: string | null;
  sendLabel?: string | null;
}

export interface FleetOutcomeCard {
  id: string;
  kind: "ready" | "finding" | "decision";
  tone: "ready" | "finding" | "decision";
  kindLabel: string;
  title: string;
  filedAtUtc: string;
  whoLine: string;
  ready?: FleetReadyCard | null;
  finding?: FleetFindingCard | null;
  decision?: FleetDecisionCard | null;
  answered: boolean;
  answerLabel?: string | null;
  answer?: string | null;
  actions: FleetCardAction[];
}

export interface FleetPanelItem {
  id: string;
  title: string;
  meta: string;
  /** The Wingman's label for the session the row belongs to, verbatim. */
  label?: string | null;
  age?: string | null;
  dot: "red" | "blue" | "green" | "grey";
  attention: boolean;
  sessionId?: string | null;
}

export interface FleetPanelSection {
  title: string;
  count: number;
  tone: "attention" | "plain";
  items: FleetPanelItem[];
  emptyText?: string | null;
  note?: string | null;
}

export interface FleetNotMine {
  count: number;
  lead: string;
  rest: string;
}

export interface FleetManagerPage {
  generatedAtUtc: string;
  fleetManagerSessionId?: string | null;
  /** Shown in place of the conversation when no Fleet Manager is marked. */
  noConversationText?: string | null;
  waitingCount: number;
  quickPrompts: FleetManagerQuickPrompt[];
  cards: FleetOutcomeCard[];
  waiting: FleetPanelSection;
  underWay: FleetPanelSection;
  landed: FleetPanelSection;
  notMine: FleetNotMine;
}

const PREFIX = "/gateway/fleet-manager";

/** GET /gateway/fleet-manager/page - the folded page. */
export async function getFleetManagerPage(signal?: AbortSignal): Promise<FleetManagerPage> {
  const res = await fetch(`${PREFIX}/page`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "read the Fleet Manager page");
  return (await res.json()) as FleetManagerPage;
}

/** POST /gateway/fleet-manager/outcomes/{id}/answer - close a record with the owner's words, exactly as given. A
 *  record that is already answered is refused (409) with the Gateway's sentence. */
export async function answerFleetOutcome(id: string, answer: string): Promise<void> {
  const res = await fetch(`${PREFIX}/outcomes/${encodeURIComponent(id)}/answer`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ answer }),
  });
  if (!res.ok) throw await GatewayError.from(res, "record your answer");
}
