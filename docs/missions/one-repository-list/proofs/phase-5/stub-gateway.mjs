// A stand-in Gateway for the phase 5 screenshot proof, and nothing else.
//
// It answers the four routes the phone's New Session flow reads, with ONE machine whose repository
// list is deliberately a MIX: three repositories that have been worked in, and two the Gateway found
// under a registered root folder that nobody has ever opened. The one list arrives in the order the
// real Gateway serves it (KnownRepositoryStore.OrderOneList): most recently used first, then the
// never-opened ones by name.
//
// It also answers GET /directors/{id}/repos - the Director's own registry - with a DIFFERENT order
// and a repository the catalogue does not hold. That route is what the phone read before phase 5.
// Serving it here is what makes the before-and-after screenshots mean something: on the BEFORE shot
// those rows are the ones on screen, and on the AFTER shot the route is not called at all.
//
// Run it with:  node docs/missions/one-repository-list/proofs/phase-5/stub-gateway.mjs 5599
import { createServer } from "node:http";

const port = Number(process.argv[2] ?? 5599);

// The one list, in the Gateway's order.
const knownRepositories = [
  { name: "devthrottle", path: "/Users/soren/ReposFred/devthrottle", lastUsed: "2026-09-20T05:40:00Z", neverOpened: false },
  { name: "devthrottle-internal", path: "/Users/soren/ReposFred/devthrottle_internal", lastUsed: "2026-09-19T18:05:00Z", neverOpened: false },
  { name: "mindzie-studio", path: "/Users/soren/ReposFred/mindzie-studio", lastUsed: "2026-09-12T11:20:00Z", neverOpened: false },
  { name: "atlas-reporting", path: "/Users/soren/ReposFred/atlas-reporting", lastUsed: null, neverOpened: true },
  { name: "zephyr-tools", path: "/Users/soren/ReposFred/zephyr-tools", lastUsed: null, neverOpened: true },
];

// The Director's own registry: a second record of the same machine, in its own order, holding a
// repository the catalogue does not, and missing the two nobody has opened.
const registryRepositories = [
  { name: "mindzie-studio", path: "/Users/soren/ReposFred/mindzie-studio", lastUsed: "2026-09-12T11:20:00Z" },
  { name: "old-experiment", path: "/Users/soren/ReposFred/old-experiment", lastUsed: "2026-09-11T08:00:00Z" },
  { name: "devthrottle", path: "/Users/soren/ReposFred/devthrottle", lastUsed: "2026-09-20T05:40:00Z" },
];

const directors = [{
  directorId: "north",
  machineName: "SORENS-MAC-MINI",
  displayName: "Sorens Mac mini",
  version: "2.6.0",
  startedAt: "2026-09-20T04:00:00Z",
  lastSeen: "2026-09-20T06:00:00Z",
  controlEndpoint: "",
}];

const agents = [
  { type: "ClaudeCode", displayName: "Claude Code", defaultModel: "opus", modelLabel: "Opus" },
  { type: "Codex", displayName: "Codex", defaultModel: "gpt", modelLabel: "Configured default model" },
];

const routes = new Map([
  ["/directors", directors],
  ["/directors/north/agents", agents],
  ["/directors/north/known-repositories", knownRepositories],
  ["/directors/north/repos", registryRepositories],
]);

// The failure case, switched on with FAIL_KNOWN=1: the one repository list cannot be served. There is
// no second list to fall back to and there is not meant to be one, so the screen has to SAY so.
const failKnown = process.env.FAIL_KNOWN === "1";

createServer((request, response) => {
  const path = new URL(request.url ?? "/", "http://localhost").pathname;
  if (failKnown && path === "/directors/north/known-repositories") {
    console.log(`[stub-gateway] ${request.method} ${path} -> 503 (FAIL_KNOWN)`);
    response.writeHead(503, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ error: "Known repository storage is not available." }));
    return;
  }
  const body = routes.get(path);
  console.log(`[stub-gateway] ${request.method} ${path} -> ${body ? 200 : 404}`);
  if (!body) {
    response.writeHead(404, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ error: "not served by the stub Gateway" }));
    return;
  }
  response.writeHead(200, { "Content-Type": "application/json" });
  response.end(JSON.stringify(body));
}).listen(port, () => console.log(`[stub-gateway] listening on ${port}`));
