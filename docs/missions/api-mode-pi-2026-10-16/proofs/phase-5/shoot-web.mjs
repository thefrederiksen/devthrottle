// Photographs the Cockpit and phone New Session screens and rosters against a STUBBED Gateway (API mode mission #4070,
// phase 5). The stub serves the agent list as the Gateway does with the one switch ON: Pi offered Terminal and API mode.
//
// Run: start `npx vite --port 5181 --strictPort` in apps/cockpit and `npx vite --port 5182 --strictPort` in apps/mobile,
// then `PLAYWRIGHT_NODE_MODULES=<a node_modules folder holding playwright-core> CHROME=<a Chrome executable>
// node shoot-web.mjs <output folder>`. Every Gateway call is answered by the stub below; nothing reaches a real Gateway.
import { createRequire } from "node:module";
import { writeFileSync } from "node:fs";
const require = createRequire(process.env.PLAYWRIGHT_NODE_MODULES.replace(/\/?$/, "/"));
const { chromium } = require("playwright-core");

const OUT = process.argv[2];
const NOW = new Date().toISOString();
// The Gateway's sentence for Pi's API mode choice, word for word as ConnectionModes.DescriptionOf("api", "Pi") writes it.
const PI_API_MODE = "Prompts are handed to Pi as messages, and each one is confirmed or refused with a reason. " +
  "The Chat shows the conversation live. You can take the wheel to use Pi's own terminal, then hand it back.";
const directors = [{ directorId: "north-1", machineName: "SOREN_NORTH", displayName: "North", version: "2.20.0", startedAt: "2026-10-16T08:00:00Z", lastSeen: NOW, controlEndpoint: "", sessions: 2 }];
const agents = [
  { type: "Pi", displayName: "Pi", defaultModel: "glm-5.3", modelLabel: "GLM-5.3", connections: [{ mode: "terminal", label: "Terminal", description: null }, { mode: "api", label: "API mode", description: PI_API_MODE } ] },
  { type: "ClaudeCode", displayName: "Claude Code", defaultModel: "opus", modelLabel: "Opus", connections: [{ mode: "terminal", label: "Terminal" }] },
];
const repos = [
  { name: "devthrottle", path: "/home/soren/ReposFred/devthrottle", lastUsed: "2026-10-16T09:00:00Z", neverOpened: false },
  { name: "cc-consult", path: "/home/soren/ReposFred/cc-consult", lastUsed: "2026-10-12T09:00:00Z", neverOpened: false },
];
function row(id, number, name, extra) {
  return { sessionId: id, number, name, directorId: "north-1", machineName: "SOREN_NORTH", repoPath: "/home/soren/ReposFred/devthrottle",
    agent: "Pi", agentToolDisplay: "Pi", activityState: "WaitingForInput", status: "Running", effectiveColor: "grey",
    effectiveColorHex: "#6b7280", stateLabel: "Ready", triageBucket: "active", createdAt: NOW, lastActivityAt: NOW,
    machineReachable: true, ...extra };
}
const sessions = [
  row("aaaaaaaa-0000-4000-8000-000000000001", 101, "Prompt Delivery - Pi in API mode", { connectionMode: "api", wheelHolder: "protocol", connectionTag: "API mode" }),
  row("aaaaaaaa-0000-4000-8000-000000000002", 102, "Prompt Delivery - Pi in its terminal", { connectionMode: "terminal", wheelHolder: "none", connectionTag: null }),
];

async function stub(page) {
  await page.route("**/*", async (route) => {
    const req = route.request();
    const type = req.resourceType();
    if (type !== "fetch" && type !== "xhr" && type !== "eventsource") return route.continue();
    const url = new URL(req.url());
    const p = url.pathname;
    const json = (body, status = 200) => route.fulfill({ status, contentType: "application/json", headers: { "X-Gateway-Time": new Date().toISOString() }, body: JSON.stringify(body) });
    if (p === "/directors") return json(directors);
    if (p === "/gateway/director-restart-requests") return json({ requests: [] });
    if (p === "/gateway/secrets/transfers") return json({ transfers: [], finishedWithinHours: 24 });
    if (/^\/directors\/[^/]+\/agents$/.test(p)) return json(agents);
    if (/^\/directors\/[^/]+\/known-repositories$/.test(p)) return json(repos);
    if (p === "/sessions") return json(url.searchParams.get("envelope") === "true"
      ? { sessions, machineErrors: [], directors: [{ directorId: "north-1", state: "online" }], unreachableBanner: null } : sessions);
    return json({ error: "not stubbed in this proof" }, 404);
  });
}

async function seed(page) {
  await page.addInitScript(() => {
    localStorage.setItem("cc.deviceKey", "proof-device-key");
    localStorage.setItem("cc.installId", "proof-install");
    localStorage.setItem("cockpit.density", "everything");
  });
}

const browser = await chromium.launch({ executablePath: process.env.CHROME, headless: true });
const results = [];
try {
  // ---- the Cockpit ----
  const cockpit = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await seed(cockpit); await stub(cockpit);
  await cockpit.goto("http://localhost:5181/");
  await cockpit.getByText("Prompt Delivery - Pi in API mode").first().waitFor({ timeout: 20000 });
  await cockpit.screenshot({ path: `${OUT}/cockpit-roster-api-mode-tag.png` });
  writeFileSync(`${OUT}/cockpit-roster-api-mode-tag.aria.txt`, await cockpit.locator("body").ariaSnapshot());
  await cockpit.getByRole("button", { name: /new session/i }).first().click();
  await cockpit.getByText("/home/soren/ReposFred/devthrottle", { exact: true }).first().click();
  await cockpit.getByRole("group", { name: "Connection" }).waitFor({ timeout: 20000 });
  const dialog = cockpit.locator(".newsess-modal");
  await dialog.screenshot({ path: `${OUT}/cockpit-new-session-pi-terminal-default.png` });
  await cockpit.getByRole("button", { name: "API mode" }).click();
  await dialog.screenshot({ path: `${OUT}/cockpit-new-session-pi-api-mode.png` });
  writeFileSync(`${OUT}/cockpit-new-session-pi-api-mode.aria.txt`, await dialog.ariaSnapshot());
  await cockpit.getByRole("button", { name: "Claude Code" }).click();
  results.push(["cockpit: Claude Code shows no Connection row", await cockpit.getByRole("group", { name: "Connection" }).count() === 0]);
  await dialog.screenshot({ path: `${OUT}/cockpit-new-session-claude-code-no-row.png` });

  // ---- the phone ----
  const phone = await browser.newPage({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await seed(phone); await stub(phone);
  await phone.goto("http://localhost:5182/mobile/");
  await phone.getByText("Prompt Delivery - Pi in API mode").first().waitFor({ timeout: 20000 });
  await phone.screenshot({ path: `${OUT}/phone-roster-api-mode-tag.png` });
  writeFileSync(`${OUT}/phone-roster-api-mode-tag.aria.txt`, await phone.locator("body").ariaSnapshot());
  await phone.goto("http://localhost:5182/mobile/new");
  await phone.getByRole("button", { name: "Select Director North" }).click();
  await phone.getByRole("button", { name: "Select agent Pi" }).click();
  await phone.getByRole("button", { name: "Select repository devthrottle" }).click();
  await phone.getByRole("group", { name: "Connection" }).waitFor({ timeout: 20000 });
  await phone.getByRole("button", { name: "API mode" }).click();
  await phone.screenshot({ path: `${OUT}/phone-new-session-pi-api-mode.png`, fullPage: true });
  writeFileSync(`${OUT}/phone-new-session-pi-api-mode.aria.txt`, await phone.locator(".newsession-review").ariaSnapshot());
  results.push(["phone: API mode pressed", (await phone.getByRole("button", { name: "API mode" }).getAttribute("aria-pressed")) === "true"]);
} finally {
  await browser.close();
}
for (const [what, ok] of results) console.log(`${ok ? "PASS" : "FAIL"} ${what}`);
