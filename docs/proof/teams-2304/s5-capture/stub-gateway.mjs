// A stand-in Gateway for the S5 screenshots ONLY. It answers /teams and /teams/{id}/library in exactly the shapes
// the real Gateway's #3532 routes answer (see HostedTeamLibraryTests and docs/proof/teams-2304/README.md); the role
// comes from the device key the page sends. Everything else answers 404.
import http from "node:http";
const roles = { "key-owner": "Owner", "key-manager": "Manager", "key-developer": "Developer", "key-collaborator": "Collaborator" };
const refusal = (role, words) => `In this team you are a ${role}, and a ${role} may not ${words}.`;
const items = (canChange) => [
  { id: "dev-reports", name: "dev-reports", summary: "A report for the owner is one HTML page, never a file path.", kind: "Skill", enabled: true, version: 4, changedAtUtc: "2026-09-21T15:00:00Z", changedBy: "soren@example.com", canChange },
  { id: "release-checklist", name: "release-checklist", summary: "The steps every release follows, in order.", kind: "Skill", enabled: true, version: 2, changedAtUtc: "2026-10-02T15:00:00Z", changedBy: "priya@example.com", canChange },
  { id: "standalone-with-review", name: "standalone-with-review", summary: "One session does the work, a second one reviews it.", kind: "Workflow", enabled: true, version: 1, changedAtUtc: "2026-09-28T15:00:00Z", changedBy: "soren@example.com", canChange },
];
http.createServer((req, res) => {
  const key = (req.headers.authorization ?? "").replace("Bearer ", "");
  const role = roles[key];
  const send = (status, body) => { res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" }); res.end(JSON.stringify(body)); };
  if (req.url === "/teams") return send(200, { count: 1, teams: [{ id: "team-dt", name: "DevThrottle", role, memberCount: 4, people: "4 people" }] });
  if (req.url === "/teams/team-dt/library") {
    if (role === "Collaborator") return send(403, { error: refusal(role, "use the team's shared skills and workflows"), code: "team_action_refused" });
    const canChange = role === "Owner" || role === "Manager";
    return send(200, {
      team: { id: "team-dt", name: "DevThrottle", role }, canChange,
      changeRefusal: canChange ? null : refusal(role, "change the team's shared skills and workflows"),
      builtInNote: "DevThrottle's own built-in skills and workflows are available to every session as well. They are not listed here and cannot be changed.",
      count: 3, items: items(canChange),
    });
  }
  send(404, { error: "Not part of the S5 stand-in." });
}).listen(5299, "127.0.0.1", () => console.log("stub gateway on 5299"));
