// The owner's lessons and standing preferences (issue #3559): keep a lesson from "That was a mistake", confirm a lesson
// the Fleet Manager kept, rewrite one, remove one. These are the owner's own device's calls; the Gateway refuses the
// edit and the confirm to every session key. Every sentence, success or refusal, is the Gateway's; a refusal throws a
// GatewayError carrying it, which the page shows as it is.
import { authHeaders, GatewayError } from "../api/client";
import type { FleetManagerAction } from "../settings/fleetManagerClient";

/** One lesson or standing preference: the owner's words verbatim, and what can be done with it. */
export interface FleetStandingRow {
  id: string;
  kind: "lesson" | "preference";
  /** The words, exactly as kept. */
  text: string;
  /** "What went wrong: ...", or null. */
  mistakeLine?: string | null;
  /** "Kept 5 October 2026 by you". */
  keptLine: string;
  /** The most characters the edit box allows for this row. */
  maxLength: number;
  /** What removing it is, in the owner's terms - the words a failed removal is reported with. */
  removeAction: string;
  /** A lesson's confirmation, or null for a preference. */
  statusLine?: string | null;
  tone: "confirmed" | "waiting" | "preference";
  confirm: FleetManagerAction;
  edit: FleetManagerAction;
  remove: FleetManagerAction;
}

export interface FleetStandingSection {
  title: string;
  count: number;
  note?: string | null;
  emptyText?: string | null;
  rows: FleetStandingRow[];
}

/** The owner's lessons and standing preferences, and the "That was a mistake" action, folded on the Gateway. */
export interface FleetStanding {
  mistake: FleetManagerAction;
  boxTitle: string;
  boxNote: string;
  boxPlaceholder: string;
  maxLessonLength: number;
  keepLabel: string;
  keepBusyLabel: string;
  keptSentence: string;
  saveLabel: string;
  saveBusyLabel: string;
  cancelLabel: string;
  showLabel: string;
  hideLabel: string;
  waitingNote?: string | null;
  lessons: FleetStandingSection;
  preferences: FleetStandingSection;
}

const PREFERENCES = "/gateway/fleet-manager/preferences";

/** GET /gateway/fleet-manager/standing - the owner's list, folded. Apart from the page answer, which is polled every
 *  few seconds wherever the Cockpit is open; this is read only on the Fleet Manager page. */
export async function getFleetStanding(signal?: AbortSignal): Promise<FleetStanding> {
  const res = await fetch("/gateway/fleet-manager/standing", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "read your lessons and preferences");
  return (await res.json()) as FleetStanding;
}

async function send(method: string, path: string, action: string, body?: unknown): Promise<void> {
  const res = await fetch(path, {
    method,
    headers: {
      Accept: "application/json",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...authHeaders(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw await GatewayError.from(res, action);
}

/** POST /gateway/fleet-manager/preferences with kind "lesson": the owner's correction, in their words, exactly. Kept by
 *  the owner it is confirmed at once, and the Gateway tells the Fleet Manager in the same save. */
export function keepLesson(text: string): Promise<void> {
  return send("POST", PREFERENCES, "keep the lesson", { text, kind: "lesson" });
}

/** POST /gateway/fleet-manager/preferences/{id}/confirm - one press confirms a lesson the Fleet Manager kept. */
export function confirmLesson(id: string): Promise<void> {
  return send("POST", `${PREFERENCES}/${encodeURIComponent(id)}/confirm`, "confirm the lesson");
}

/** PUT /gateway/fleet-manager/preferences/{id} - the owner rewrites a lesson or a preference in their own words. */
export function editStanding(id: string, text: string): Promise<void> {
  return send("PUT", `${PREFERENCES}/${encodeURIComponent(id)}`, "save the new words", { text });
}

/** DELETE /gateway/fleet-manager/preferences/{id} - remove a lesson or a preference. */
export function removeStanding(id: string): Promise<void> {
  return send("DELETE", `${PREFERENCES}/${encodeURIComponent(id)}`, "remove it");
}
