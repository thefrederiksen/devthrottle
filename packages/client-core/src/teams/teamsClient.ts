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
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** One team as the signed-in person sees it. `role` is THEIR role in that team, exactly as the Gateway labels it -
 *  shown verbatim, never compared: the Gateway owns the list of roles (review finding F5). */
export interface TeamSummary {
  id: string;
  name: string;
  role: string;
  memberCount: number;
  /** The Gateway's own wording of the member count: "1 person", "5 people". */
  people: string;
}

/** What GET /teams answered. */
export type MyTeamsAnswer =
  | { kind: "teams"; teams: TeamSummary[] }
  | { kind: "not-offered"; reason: string };

/** Why a dark Gateway offers no teams, in the words the store keeps. */
export const TEAMS_NOT_RELEASED_REASON =
  "This Gateway has not turned Teams on, so there are no teams to choose from.";

function contentType(res: Response): string {
  return (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
}

/**
 * The signed-in person's teams, each with their role in it. `not-offered` when the Gateway has no teams to offer
 * (Teams dark, or a self-hosted Gateway). Throws a GatewayError for every other failure.
 */
export async function getMyTeams(signal?: AbortSignal): Promise<MyTeamsAnswer> {
  const res = await gatewayFetch("/teams", {
    headers: { ...authHeaders(), Accept: "application/json" },
    signal,
  });

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
  return { kind: "teams", teams: body.teams.map(readTeam) };
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
  return { id: t.id, name: t.name, role: t.role, memberCount: t.memberCount, people: t.people };
}
