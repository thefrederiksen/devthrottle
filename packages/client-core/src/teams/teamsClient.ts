// The person's teams (Teams, first version - devthrottle_internal#2300 and #2312): the typed, same-origin read
// of GET /teams that the team switcher lists.
//
// THE GATEWAY DECIDES WHO IS IN WHICH TEAM AND IN WHAT ROLE (rule 7). This client carries the Gateway's answer
// verbatim - the role is the Gateway's own label - and never works out a role, a permission or a member count
// itself.
//
// TEAMS ARE NOT ALWAYS OFFERED, and that is an answer, not a failure. Two Gateways have no teams to list:
//   - one where Teams is still dark (CC_GATEWAY_TEAMS is not 1). It maps no /teams route, so a read falls
//     through to the Cockpit's page fallback and comes back as HTTP 200 with the app's own HTML shell;
//   - a self-hosted Gateway, which holds one account and answers 404 with a sentence saying so.
// Both are reported as `not-offered`, and the switcher then shows nothing - a person on such a Gateway sees no
// change anywhere. Anything else that is not a JSON team list is a real failure and is thrown.
import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS } from "../api/client";

/** One team as the signed-in person sees it. `role` is THEIR role in that team, exactly as the Gateway labels it -
 *  shown verbatim, never compared: the Gateway owns the list of roles (review finding F5). */
export interface TeamSummary {
  id: string;
  name: string;
  role: string;
  memberCount: number;
  /** The Gateway's own wording of the member count: "1 person", "5 people". */
  people: string;
  /** What this person's Cockpit is in this team - the Gateway's verdict from the role table (devthrottle_internal#2306).
   *  The Cockpit renders it; it never works the page list out from `role`. */
  app: TeamApp;
}

/** One page a role may open in a team, as the Gateway names it. */
export interface TeamAppPage {
  id: string;
  label: string;
  path: string;
  /** Where the number waiting on this person for this page is read (devthrottle_internal#2307 review F8: the count
   *  beside Questions), or null for a page with no count. The Gateway names it; the Cockpit never builds one. */
  countPath: string | null;
}

/** The Gateway's page verdict for one person in one team: the whole Cockpit, or only some pages. */
export type TeamApp = FullTeamApp | TeamPagesApp;

/** The whole Cockpit. `pages` are the team pages this person may also open. */
export interface FullTeamApp {
  full: true;
  pages: TeamAppPage[];
  landing: null;
  elsewhere: null;
}

/** Only `pages`, and nothing else: the Cockpit opens on `landing`, and every other address shows `elsewhere`. */
export interface TeamPagesApp {
  full: false;
  pages: TeamAppPage[];
  landing: string;
  elsewhere: string;
}

/** Where a browser that has never chosen starts - the Gateway's verdict (devthrottle_internal#2306): the person's own
 *  account, one team, or the team chooser (screen S11). */
export type TeamStart = { where: "own-account" } | { where: "team"; teamId: string } | { where: "choose" };

/** What GET /teams answered. */
export type MyTeamsAnswer =
  | { kind: "teams"; teams: TeamSummary[]; start: TeamStart }
  | { kind: "not-offered"; reason: string };

/** Why a dark Gateway offers no teams, in the words the store keeps. */
export const TEAMS_NOT_RELEASED_REASON =
  "This Gateway has not turned Teams on, so there are no teams to choose from.";

/** How long the list of teams may take before it is treated as failed, in milliseconds - the poll reads' limit. */
export const TEAMS_READ_TIMEOUT_MS = POLL_TIMEOUT_MS;

function contentType(res: Response): string {
  return (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
}

/**
 * The signed-in person's teams, each with their role in it. `not-offered` when the Gateway has no teams to offer
 * (Teams dark, or a self-hosted Gateway). Throws a GatewayError for every other failure.
 */
export async function getMyTeams(signal?: AbortSignal): Promise<MyTeamsAnswer> {
  // A time limit (delta review D1): a shell that remembers a team waits on this read, so a read that hangs must end
  // as an error - which the shell shows, with the own account as the way out - rather than leave the page blank.
  const res = await gatewayFetch(
    "/teams",
    { headers: { ...authHeaders(), Accept: "application/json" }, signal },
    { timeoutMs: TEAMS_READ_TIMEOUT_MS },
  );

  if (res.status === 404) {
    // The self-hosted Gateway's answer, with its own sentence. A 404 with no sentence is the same answer: there
    // is no team route here.
    const failure = await GatewayError.from(res, "read your teams");
    return { kind: "not-offered", reason: failure.serverReason ?? TEAMS_NOT_RELEASED_REASON };
  }
  if (!res.ok) throw await GatewayError.from(res, "read your teams");

  // A dark Gateway: no /teams route, so the read reached the Cockpit's page fallback and got the app shell.
  if (contentType(res) === "text/html") return { kind: "not-offered", reason: TEAMS_NOT_RELEASED_REASON };
  if (contentType(res) !== "application/json") {
    throw new GatewayError(
      502,
      `The Gateway answered the list of your teams with ${contentType(res) || "an unlabelled body"} instead of team data.`,
    );
  }

  const body = (await res.json()) as { teams?: unknown };
  if (!Array.isArray(body.teams)) {
    throw new GatewayError(502, "The Gateway's list of your teams had no teams in it, not even an empty list.");
  }
  const teams = body.teams.map(readTeam);
  return { kind: "teams", teams, start: readStart((body as { start?: unknown }).start, teams) };
}

/**
 * Create a team named `name` (POST /teams). The person who creates it becomes its Owner - the Gateway decides that,
 * and answers the new team as the person now sees it, read the same way as one row of GET /teams. A refusal (an empty
 * or over-long name, say) is thrown as a GatewayError carrying the Gateway's own sentence.
 */
export async function createTeam(name: string, signal?: AbortSignal): Promise<TeamSummary> {
  const res = await gatewayFetch("/teams", {
    method: "POST",
    headers: { ...authHeaders(), Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "create the team");
  if (contentType(res) !== "application/json") {
    throw new GatewayError(
      502,
      `The Gateway answered the new team with ${contentType(res) || "an unlabelled body"} instead of team data.`,
    );
  }
  const body = (await res.json()) as { team?: unknown };
  if (body.team === undefined || body.team === null) {
    throw new GatewayError(502, "The Gateway said the team was created but did not send the team back.");
  }
  return readTeam(body.team);
}

function readTeam(raw: unknown): TeamSummary {
  const t = raw as Partial<Record<keyof TeamSummary, unknown>>;
  if (
    typeof t.id !== "string" ||
    typeof t.name !== "string" ||
    typeof t.role !== "string" ||
    t.role.trim().length === 0 ||
    typeof t.memberCount !== "number" ||
    typeof t.people !== "string"
  ) {
    throw new GatewayError(502, "The Gateway sent a team the Cockpit cannot read: it is missing its id, name, role or size.");
  }
  return { id: t.id, name: t.name, role: t.role, memberCount: t.memberCount, people: t.people, app: readApp(t.app) };
}

const UNREADABLE_START = "The Gateway's list of your teams did not say where to start, so the Cockpit cannot open it.";

function readStart(raw: unknown, teams: TeamSummary[]): TeamStart {
  const s = (raw ?? {}) as { where?: unknown; teamId?: unknown };
  if (s.where === "own-account") return { where: "own-account" };
  if (s.where === "choose") return { where: "choose" };
  if (s.where === "team" && typeof s.teamId === "string" && teams.some((t) => t.id === s.teamId)) {
    return { where: "team", teamId: s.teamId };
  }
  throw new GatewayError(502, UNREADABLE_START);
}

const UNREADABLE_APP =
  "The Gateway sent a team without the pages its member may open, so the Cockpit cannot tell what to show in it.";

function readApp(raw: unknown): TeamApp {
  const a = (raw ?? {}) as Partial<Record<keyof TeamApp, unknown>>;
  if (typeof a.full !== "boolean" || !Array.isArray(a.pages)) throw new GatewayError(502, UNREADABLE_APP);
  const pages = a.pages.map((p: unknown) => {
    const page = (p ?? {}) as Partial<Record<keyof TeamAppPage, unknown>>;
    if (
      typeof page.id !== "string" ||
      typeof page.label !== "string" ||
      typeof page.path !== "string" ||
      !(page.countPath === null || typeof page.countPath === "string")
    ) {
      throw new GatewayError(502, UNREADABLE_APP);
    }
    return { id: page.id, label: page.label, path: page.path, countPath: page.countPath };
  });
  if (a.full) return { full: true, pages, landing: null, elsewhere: null };
  // A limited app must say where it opens and what every other address says; without both it cannot be drawn.
  if (pages.length === 0 || typeof a.landing !== "string" || typeof a.elsewhere !== "string") {
    throw new GatewayError(502, UNREADABLE_APP);
  }
  return { full: false, pages, landing: a.landing, elsewhere: a.elsewhere };
}

/**
 * The number waiting on this person for a team page - read from the `countPath` the Gateway named on that page, whose
 * answer carries the Gateway's own `count` (devthrottle_internal#2307 review F8). Throws a GatewayError when the read
 * fails or the answer has no count.
 */
export async function getPageCount(countPath: string, signal?: AbortSignal): Promise<number> {
  const res = await gatewayFetch(countPath, { headers: { ...authHeaders(), Accept: "application/json" }, signal }, { timeoutMs: TEAMS_READ_TIMEOUT_MS });
  if (!res.ok) throw await GatewayError.from(res, "read how many wait on you");
  if (contentType(res) !== "application/json") {
    throw new GatewayError(502, `The Gateway answered a page count with ${contentType(res) || "an unlabelled body"} instead of a count.`);
  }
  const body = (await res.json()) as { count?: unknown };
  if (typeof body.count !== "number" || !Number.isInteger(body.count) || body.count < 0) {
    throw new GatewayError(502, "The Gateway's answer for a page count had no count in it.");
  }
  return body.count;
}
