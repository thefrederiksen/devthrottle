// The Mission RECORDS on the Gateway - the first-class objects sessions attach to (see
// docs/new_architecture/fleet.html). This is what makes a mission exist on a
// screen INDEPENDENTLY of whether any session happens to be attached to it right now: a mission the owner
// created and has not staffed yet is still a mission, and it has to be visible or there is nowhere to drop
// a session onto.
//
// Before this client existed the Cockpit never read these records at all - it inferred "missions" by
// pattern-matching session NAMES, so a real mission with no name-matching session simply did not appear,
// and eleven live missions rendered as two. The records are the truth; the roster says who is on them.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/**
 * One Mission as the Gateway serves it (GET /missions). Hand-written rather than taken from `schema.ts`
 * for the same reason as `ModelDisplay` in the api client: the committed generated schema declares the
 * /missions responses as `never`, so there is no generated type to import. Delete this in favor of the
 * generated one when the schema is next regenerated properly - the two agree.
 */
export interface MissionDto {
  /** Stable identity of the Mission. This is the value sessions attach by, and the grouping identity. */
  missionId: string;
  /** Human-friendly name of the Mission. */
  missionName: string;
  /** WHY this mission exists, in the owner's own words. Empty (or absent) means UNSET - the card shows its
   *  loud "no why set" flag rather than a blank. Carried on the mission itself and keyed by its id. */
  why?: string | null;
  /** When the WHY was last set (ISO-8601 UTC), or null if never. */
  whyUpdatedAt?: string | null;
  /** "active", "complete", or "removed". Absent from an older Gateway, which reads as active.
   *  `listMissions` returns only active missions unless asked otherwise, so this is "active" on every
   *  mission the default view draws - it matters when asking for the archive. */
  state?: string | null;
  /** When the state last changed (ISO-8601 UTC), or null while it has only ever been active. */
  stateChangedAt?: string | null;
}

/**
 * Every Mission this account owns, newest ordering left to the Gateway. Tenant-scoped server-side from the
 * authenticated device key - the client neither sends nor can influence which account's missions it gets.
 *
 * Throws GatewayError on a non-2xx so the caller decides what to show. There is deliberately NO fallback to
 * an empty list here: a failed read means "we do not know what missions exist", which is a different thing
 * from "there are none", and a caller that cannot tell them apart will render the second while the first is
 * true.
 */
export async function listMissions(signal?: AbortSignal): Promise<MissionDto[]> {
  const res = await gatewayFetch("/missions", {
    method: "GET",
    headers: { Accept: "application/json" as const, ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "load the mission list");

  // A 200 carrying something that is not an array of missions is a BROKEN response, not an empty fleet of
  // missions, and the two must not arrive at the same screen looking alike. Coercing the malformed case to
  // `[]` would render "no missions" over a Gateway that is answering wrongly - the caller would show a
  // confident, complete-looking, false answer. Throw instead and let the board say it does not know.
  const body: unknown = await res.json();
  if (!Array.isArray(body)) {
    throw new GatewayError(res.status, "The mission list came back in a shape this app cannot read.");
  }
  for (const m of body) {
    const id = (m as MissionDto | null)?.missionId;
    if (typeof id !== "string" || id.trim().length === 0) {
      throw new GatewayError(res.status, "The mission list contains a mission with no id.");
    }
  }
  return body as MissionDto[];
}

/**
 * Set (or clear) one mission's WHY, keyed by its ID.
 *
 * A blank `why` CLEARS it and the card returns to its "no why set" flag - the same "empty means unset" rule
 * the screen has always had. Returns the updated mission so the caller renders what the Gateway stored
 * rather than what it hoped it stored.
 *
 * This replaces `PUT /gateway/missions/notes`, which keyed the WHY by the mission's LOWER-CASED NAME. That
 * meant two missions sharing a name shared one WHY, and renaming a mission would have silently orphaned it -
 * the card just falling back to its flag with no error anywhere. Keying by id is what makes rename safe.
 */
export async function setMissionWhy(
  missionId: string,
  why: string,
  signal?: AbortSignal,
): Promise<MissionDto> {
  const res = await gatewayFetch(`/missions/${encodeURIComponent(missionId)}`, {
    method: "PATCH",
    headers: {
      "Content-Type": "application/json" as const,
      Accept: "application/json" as const,
      ...authHeaders(),
    },
    body: JSON.stringify({ why }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "save the mission's why");
  return (await res.json()) as MissionDto;
}
