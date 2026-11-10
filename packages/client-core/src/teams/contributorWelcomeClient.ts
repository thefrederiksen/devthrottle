// THE CONTRIBUTOR'S WELCOME (Team implementation piece 4.2): the two routes under /teams/{teamId}/contributor/welcome, as
// the welcome page reads them, in the Cockpit and on the phone alike.
//
// Every word on the page is the Gateway's - the owner's approved words, held once on the Gateway (ContributorWelcome) -
// and is typed here as the plain value it sends. Whether the page is shown, and where the app opens once it has been
// seen, is the Gateway's verdict too (the page list from GET /teams). Nothing here decides either (repository rule 7).

import { call } from "./invitationsClient";

/** One numbered step. `yours` is the step the person does themselves; `watch` carries the small factory drawing. */
export interface ContributorWelcomeStep {
  number: number;
  title: string;
  text: string;
  yours: boolean;
  watch: boolean;
}

/** The welcome page, for one team. */
export interface ContributorWelcomePage {
  teamId: string;
  /** The team's name, in the label above the heading. */
  teamLabel: string;
  title: string;
  lead: string;
  steps: ContributorWelcomeStep[];
  /** The words beside the drawing on the step that has one. */
  watchLabel: string;
  /** The line under the steps; pressing it asks for permission to notify. */
  notifications: string;
  question: string;
  /** The button, and the address it opens: the way to write to the team's Owner. */
  button: { label: string; href: string };
}

const base = (teamId: string) => `/teams/${encodeURIComponent(teamId)}/contributor/welcome`;

/** GET /teams/{teamId}/contributor/welcome - the welcome page's words, for this team. */
export function getContributorWelcome(teamId: string, signal?: AbortSignal): Promise<ContributorWelcomePage> {
  return call<ContributorWelcomePage>("GET", base(teamId), "load the welcome", undefined, signal);
}

/** POST /teams/{teamId}/contributor/welcome/seen - the person has seen the welcome, so their app opens on Factories from
 *  their next sign-in. Marking again changes nothing. */
export function markContributorWelcomeSeen(teamId: string, signal?: AbortSignal): Promise<{ seen: boolean }> {
  return call<{ seen: boolean }>("POST", `${base(teamId)}/seen`, "remember that you saw the welcome", undefined, signal);
}
