// Screenshots sample-emails.html (the two contributor notice emails, written from the real Gateway composer
// ContributorNoticeEmail) so the pull request can show them. Run: PW_CHANNEL=msedge node shoot-email.mjs
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { createRequire } from "node:module";

const requireCjs = createRequire(import.meta.url);
const { chromium } = requireCjs(
  process.env.PLAYWRIGHT_PATH || "C:/Users/soren/AppData/Roaming/npm/node_modules/@playwright/cli/node_modules/playwright",
);
const here = dirname(fileURLToPath(import.meta.url));
const browser = await chromium.launch(process.env.PW_CHANNEL ? { channel: process.env.PW_CHANNEL } : {});
try {
  const page = await browser.newPage({ viewport: { width: 720, height: 900 } });
  await page.goto(pathToFileURL(join(here, "sample-emails.html")).href);
  await page.locator("details").evaluateAll((all) => all.forEach((d) => d.setAttribute("open", "")));
  await page.screenshot({ path: join(here, "evidence-sample-emails.png"), fullPage: true });
  console.log("[contributor-notice-proof] wrote evidence-sample-emails.png");
} finally {
  await browser.close();
}
