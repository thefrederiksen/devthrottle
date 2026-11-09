// HOW A CONTRIBUTOR IS TOLD THAT SOMETHING IS WAITING (Team implementation piece 4.4). The Gateway folds every word of
// the card (GET/PUT /teams/notifications); this file only carries it. Throws on a non-2xx with the Gateway's own
// sentence (no-fallback rule), except the one answer that means "there is no such setting here": 404, from a Gateway
// where Teams is not released or that has no teams at all. Then there is nothing to show, and the card is absent.
import { authHeaders, GatewayError } from "../api/client";

/** One of the three choices, worded by the Gateway. */
export interface ContributorNoticeOption {
  value: "now" | "daily" | "never";
  label: string;
  selected: boolean;
}

/** The card, every word and verdict finished on the Gateway. */
export interface ContributorNoticeCard {
  /** Whether the person is a contributor on any team: only then is anything emailed, so only then is the card shown. */
  applies: boolean;
  title: string;
  explanation: string;
  mode: "now" | "daily" | "never";
  options: ContributorNoticeOption[];
  showHour: boolean;
  hourLabel: string;
  dailyHour: number;
  hours: { value: number; label: string }[];
  timeZoneLine: string;
  note: string;
  /** The Gateway's line after a change was stored; null on a read. */
  savedLine: string | null;
}

const PATH = "/teams/notifications";

/** The card, or null when this Gateway has no such setting (Teams not released, or no teams here). */
export async function getContributorNoticeCard(signal?: AbortSignal): Promise<ContributorNoticeCard | null> {
  const res = await fetch(PATH, { headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (res.status === 404) return null;
  if (!res.ok) throw await GatewayError.from(res, "load how you are told about new things");
  return (await res.json()) as ContributorNoticeCard;
}

/** Store the person's choice; answers the card again, with the Gateway's saved line. */
export async function setContributorNoticeChoice(mode: string, dailyHour: number): Promise<ContributorNoticeCard> {
  const res = await fetch(PATH, {
    method: "PUT",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ mode, dailyHour }),
  });
  if (!res.ok) throw await GatewayError.from(res, "save how you are told about new things");
  return (await res.json()) as ContributorNoticeCard;
}
