// The team's Fleet Map (devthrottle_internal#2312): GET /teams/{teamId}/fleet-map, as the Cockpit's Fleet Map reads it
// when a team is on screen.
//
// THE GATEWAY DECIDES EVERYTHING HERE (rule 7). Which Directors are in the answer - every one on the team for the
// Owner and a Manager, only their own for a Developer, none for a Collaborator - is cut ON THE SERVER. So are the
// layouts offered, the sentence under the heading, the sentence for an empty map and each session's status word. The
// page lays out what arrives; it never filters by role.
//
// The answer is an allow-list: names and status only. It carries no session id and no Director id, so nothing on the
// page can open, read or type into a teammate's session.
import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS } from "../api/client";

/** One of the three words a session's status can be, exactly as the Gateway folds it. */
export type TeamSessionStatus = "working" | "waiting" | "done";

/** A layout the Gateway offers this person. */
export type TeamFleetMapLayout = "by-person" | "by-director";

export interface TeamFleetMapSession {
  name: string;
  status: TeamSessionStatus;
}

export interface TeamFleetMapDirector {
  name: string;
  machine: string;
  sessions: TeamFleetMapSession[];
}

export interface TeamFleetMapPerson {
  /** A display label - the person's email. Never their account id. */
  person: string;
  isYou: boolean;
  directors: TeamFleetMapDirector[];
}

export interface TeamFleetMap {
  teamId: string;
  teamName: string;
  /** The signed-in person's role in the team. */
  role: string;
  /** "everyone" for the Owner and a Manager; "own" for a Developer. */
  scope: "everyone" | "own";
  /** The sentence under the heading. */
  summary: string;
  /** The layouts offered, the first being where the map opens. */
  layouts: TeamFleetMapLayout[];
  /** What to say when `people` is empty. */
  emptyText: string;
  people: TeamFleetMapPerson[];
}

const STATUSES: ReadonlyArray<TeamSessionStatus> = ["working", "waiting", "done"];
const LAYOUTS: ReadonlyArray<TeamFleetMapLayout> = ["by-person", "by-director"];

function unreadable(what: string): GatewayError {
  return new GatewayError(502, `The Gateway sent a team Fleet Map the Cockpit cannot read: ${what}.`);
}

/**
 * The team's Fleet Map as the signed-in person may see it. A role with no Fleet Map (a Collaborator) gets a
 * GatewayError with status 403 carrying the Gateway's sentence; a team that is not theirs, 404.
 */
export async function getTeamFleetMap(teamId: string, signal?: AbortSignal): Promise<TeamFleetMap> {
  const res = await gatewayFetch(
    `/teams/${encodeURIComponent(teamId)}/fleet-map`,
    { headers: { ...authHeaders(), Accept: "application/json" }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (!res.ok) throw await GatewayError.from(res, "read the team's Fleet Map");
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json") throw unreadable(`it answered with ${type || "an unlabelled body"}`);
  return readMap(await res.json());
}

function readMap(raw: unknown): TeamFleetMap {
  const m = raw as Record<string, unknown>;
  if (
    typeof m.teamId !== "string" ||
    typeof m.teamName !== "string" ||
    typeof m.role !== "string" ||
    (m.scope !== "everyone" && m.scope !== "own") ||
    typeof m.summary !== "string" ||
    typeof m.emptyText !== "string" ||
    !Array.isArray(m.layouts) ||
    !Array.isArray(m.people)
  ) {
    throw unreadable("it is missing the team, the role, the scope, the sentences, the layouts or the people");
  }
  const layouts = m.layouts.map((l) => {
    if (!LAYOUTS.includes(l as TeamFleetMapLayout)) throw unreadable(`it offers a layout called "${String(l)}"`);
    return l as TeamFleetMapLayout;
  });
  if (layouts.length === 0) throw unreadable("it offers no layout");
  return {
    teamId: m.teamId,
    teamName: m.teamName,
    role: m.role,
    scope: m.scope,
    summary: m.summary,
    emptyText: m.emptyText,
    layouts,
    people: m.people.map(readPerson),
  };
}

function readPerson(raw: unknown): TeamFleetMapPerson {
  const p = raw as Record<string, unknown>;
  if (typeof p.person !== "string" || typeof p.isYou !== "boolean" || !Array.isArray(p.directors)) {
    throw unreadable("a person is missing their label or their Directors");
  }
  return { person: p.person, isYou: p.isYou, directors: p.directors.map(readDirector) };
}

function readDirector(raw: unknown): TeamFleetMapDirector {
  const d = raw as Record<string, unknown>;
  if (typeof d.name !== "string" || typeof d.machine !== "string" || !Array.isArray(d.sessions)) {
    throw unreadable("a Director is missing its name, its machine or its sessions");
  }
  return { name: d.name, machine: d.machine, sessions: d.sessions.map(readSession) };
}

function readSession(raw: unknown): TeamFleetMapSession {
  const s = raw as Record<string, unknown>;
  if (typeof s.name !== "string" || !STATUSES.includes(s.status as TeamSessionStatus)) {
    throw unreadable("a session is missing its name or has a status other than working, waiting or done");
  }
  // Only the two allowed fields are kept, whatever else arrives: nothing that could open a session reaches the page.
  return { name: s.name, status: s.status as TeamSessionStatus };
}
