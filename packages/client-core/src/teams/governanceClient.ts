// The team's Governance tab (Teams v1): the typed, same-origin client the Cockpit's Governance tab reads.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Every row's words, whether the caller may change the rules and the sentence for
// one who may not, the "who may read what" answers, how each limit shows and each line of the record of changes arrive
// here finished. The tab renders them verbatim; it never decides from a role what the caller may do.
import { call } from "./invitationsClient";

/** One on/off rule. */
export interface GovernanceSwitch {
  id: string;
  label: string;
  detail: string | null;
  on: boolean;
}

/** One skill or workflow the team names as Required or Suggested. `gone` is set once it has left the team's library. */
export interface GovernanceItem {
  kind: string;
  id: string;
  name: string;
  level: string;
  gone: string | null;
}

/** A skill or workflow of the team's library that is not on the list yet. Empty for a caller who may not change it. */
export interface GovernanceChoice {
  kind: string;
  id: string;
  name: string;
}

/** One "who may read what" row. */
export interface GovernanceReadAccess {
  label: string;
  who: string;
}

/** One limit: `value` null is no limit; `display` is how it shows; a change keeps to `min`..`max`. */
export interface GovernanceLimit {
  id: string;
  label: string;
  detail: string | null;
  value: number | null;
  display: string;
  min: number;
  max: number;
}

/** One line of the record of changes, finished: "peter@acme.example switched on ...", and when. */
export interface GovernanceChangeLine {
  id: string;
  sentence: string;
  when: string;
}

/** The whole tab, as the Gateway sends it. */
export interface TeamGovernance {
  teamName: string;
  summary: string;
  canChange: boolean;
  /** Why the controls are read-only; null for a caller who may change them. */
  note: string | null;
  review: GovernanceSwitch[];
  library: {
    note: string;
    items: GovernanceItem[];
    choices: GovernanceChoice[];
    emptyLibraryNote: string | null;
  };
  agents: GovernanceSwitch[];
  readAccess: GovernanceReadAccess[];
  limits: GovernanceLimit[];
  changes: GovernanceChangeLine[];
}

/** A change: only what is named changes. A limit of null removes it; an item's level "None" takes it off the list. */
export interface GovernanceChange {
  review?: Record<string, boolean>;
  agents?: Record<string, boolean>;
  items?: { kind: string; id: string; level: "Required" | "Suggested" | "None" }[];
  limits?: Record<string, number | null>;
}

const governance = (teamId: string) => `/teams/${encodeURIComponent(teamId)}/governance`;

/** GET /teams/{teamId}/governance - the tab for the caller. */
export function getTeamGovernance(teamId: string, signal?: AbortSignal): Promise<TeamGovernance> {
  return call<TeamGovernance>("GET", governance(teamId), "load the team's rules", undefined, signal);
}

/** PUT /teams/{teamId}/governance - change the rules; answers the tab as it now stands. */
export function changeTeamGovernance(teamId: string, change: GovernanceChange, signal?: AbortSignal): Promise<TeamGovernance> {
  return call<TeamGovernance>("PUT", governance(teamId), "change the team's rules", change, signal);
}
