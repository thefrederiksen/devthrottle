// Screenshots of Fleet -> Machines against the local seeded Gateway (Resource Monitor phase 4).
const { chromium } = require("playwright-core");
const OUT = __dirname;
const BASE = process.argv[2] || "http://127.0.0.1:7955";

(async () => {
  const browser = await chromium.launch({ executablePath: "/usr/bin/google-chrome", headless: true });
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 2 });
  await page.addInitScript(() => {
    window.localStorage.setItem("cc.accounts", JSON.stringify([{ id: "rig", label: "Local rig", email: null, deviceKey: "rig-no-auth", installId: "rig-install" }]));
    window.localStorage.setItem("cc.activeAccount", "rig");
  });
  const errors = [];
  page.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
  const notFound = [];
  page.on("response", (r) => { if (r.status() >= 400) notFound.push(`${r.status()} ${r.url()}`); });
  await page.goto(`${BASE}/directors`, { waitUntil: "networkidle" });
  await page.waitForSelector('[data-testid="fm-machine-row"]', { timeout: 20000 });
  const table = page.locator(".fm-panel");
  await table.screenshot({ path: `${OUT}/01-machines-three-machines.png` });

  await page.click('[data-testid="fm-sort-spare"]');
  await table.screenshot({ path: `${OUT}/02-machines-sorted-by-spare.png` });
  await page.click('[data-testid="fm-sort-spare"]');
  await page.click('[data-testid="fm-sort-spare"]');

  const expand = async (machine) => {
    await page.click(`[data-testid="fm-machine-row"][data-machine="${machine}"] .fm-expand`);
  };
  await expand("devlinux");
  await page.waitForSelector('[data-testid="fmr-factories"]', { timeout: 20000 });
  await page.locator(`[data-testid="fm-machine-row"][data-machine="devlinux"]`).scrollIntoViewIfNeeded();
  await table.screenshot({ path: `${OUT}/03-devlinux-expanded.png` });
  await expand("devlinux");

  await expand("demo-win11");
  await page.waitForSelector('[data-testid="fmr-chart-CPU"]', { timeout: 20000 });
  await table.screenshot({ path: `${OUT}/04-demo-win11-offline-expanded.png` });
  await expand("demo-win11");

  await expand("devthrottle-mac-mini");
  await page.waitForSelector('[data-testid="fmr-factories"]', { timeout: 20000 });
  await table.screenshot({ path: `${OUT}/05-mac-mini-expanded.png` });

  // The words of each row as the browser drew them, for the record.
  const rows = await page.$$eval('[data-testid="fm-machine-row"]', (rs) =>
    rs.map((r) => ({ machine: r.dataset.machine, text: r.innerText.replace(/\s+/g, " ").trim() })));
  console.log(JSON.stringify({ rows, consoleErrors: errors, failedResponses: notFound }, null, 2));
  await browser.close();
})().catch((e) => { console.error(e); process.exit(1); });
