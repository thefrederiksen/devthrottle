import { chromium } from "playwright-core";
const out = process.argv[2];
const exe = process.env.LOCALAPPDATA + "/ms-playwright/chromium-1234/chrome-win64/chrome.exe";
const browser = await chromium.launch({ executablePath: exe });
try {
  for (const role of ["owner", "manager", "developer", "collaborator"]) {
    const page = await browser.newPage({ viewport: { width: 1360, height: 820 } });
    await page.addInitScript((r) => {
      const id = "acct-" + r;
      localStorage.setItem("cc.accounts", JSON.stringify([{ id, label: r + "@example.com", email: r + "@example.com", deviceKey: "key-" + r, installId: "install-" + r }]));
      localStorage.setItem("cc.activeAccount", id);
      localStorage.setItem("devthrottle.currentTeam." + id, "team-dt");
    }, role);
    await page.goto("http://127.0.0.1:5317/skills", { waitUntil: "domcontentloaded" });
    if (role === "collaborator") await page.getByRole("note").first().waitFor({ timeout: 30000 });
    else await page.getByText("release-checklist").first().waitFor({ timeout: 30000 });
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${out}/s5-${role}.png` });
    console.log("captured", role);
    await page.close();
  }
} finally {
  await browser.close();
}
