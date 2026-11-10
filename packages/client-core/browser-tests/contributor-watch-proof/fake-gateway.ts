// A FAKE GATEWAY FOR THE PROOF (Watch your factory, piece 6): window.fetch answers the routes the two contributor pages
// read, in the Gateway's shapes. Every word it answers with comes from window.__PROOF__, which the driver (run-proof.mjs)
// read out of the Gateway's own C# source - the explainer card's paragraphs and button, the page's intro and the page
// label - so no approved word is typed here. The watch view is the Gateway-written contributor fixture.
//
// POST .../watch/explainer-seen is remembered, as the Gateway does: after it the view carries no explainer.

import contributorFixture from "../../src/factory/watch/factoryWatchView.contributor.fixture.json";

interface ProofWords {
  paragraphs: string[];
  watchLabel: string;
  factoriesLabel: string;
  inboxLabel: string;
  factoriesIntro: string;
  factoriesEmpty: string;
  elsewhere: string;
}

declare global {
  interface Window {
    __PROOF__: ProofWords;
    __PROOF_LOG__: string[];
  }
}

const words = window.__PROOF__;
window.__PROOF_LOG__ = [];
let seen = false;

const TEAM = "7a1d2c9e-0000-4000-8000-000000000006";
const team = {
  id: TEAM,
  name: "Demo team",
  role: "Collaborator",
  memberCount: 2,
  people: "2 people",
  app: {
    full: false,
    pages: [
      { id: "factories", label: words.factoriesLabel, path: "/contributor/factories", countPath: null },
      { id: "inbox", label: words.inboxLabel, path: "/contributor/inbox", countPath: null },
    ],
    landing: "/contributor/factories",
    elsewhere: words.elsewhere,
  },
};

const own = (contributorFixture as unknown as Record<string, Record<string, unknown>>).own;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.pathname + input.search : input.url;
  const method = (init?.method ?? "GET").toUpperCase();
  window.__PROOF_LOG__.push(`${method} ${url}`);
  const path = url.split("?")[0];

  if (path === "/teams") return json({ count: 1, teams: [team], start: { where: "team", teamId: TEAM } });
  if (path === `/teams/${TEAM}/contributor/factories`)
    return json({
      teamId: TEAM,
      teamLabel: team.name,
      count: 1,
      intro: words.factoriesIntro,
      emptyText: words.factoriesEmpty,
      factories: [
        {
          teamId: TEAM,
          teamLabel: team.name,
          factory: "photo-factory",
          name: own.title,
          nowText: null,
          needsYouLabel: null,
          watchLabel: words.watchLabel,
          order: "1|0|photo factory|" + TEAM,
        },
      ],
    });
  if (path === `/teams/${TEAM}/contributor/factories/photo-factory/watch`)
    return json({ ...own, explainer: seen ? null : { paragraphs: words.paragraphs, buttonText: words.watchLabel } });
  if (path === `/teams/${TEAM}/contributor/factories/photo-factory/watch/explainer-seen` && method === "POST") {
    seen = true;
    return new Response(null, { status: 204 });
  }
  // Everything else the shells read around the page (alerts, license alarms) has nothing to say in this proof.
  return json({ error: "Not part of this proof." }, 404);
};
