// Screenshots of the Factories screen through the read-only relay. Usage: node shoot.js <prefix> [path...]
// The browser carries a placeholder account so the Cockpit renders; the relay adds the real session key itself.
const { chromium } = require("/home/soren/ReposFred/rea/node_modules/playwright-core");

const prefix = process.argv[2];
const paths = process.argv.slice(3);
const sizes = [
  { name: "desktop", width: 1400, height: 1500 },
  { name: "phone", width: 390, height: 2400 },
];

(async () => {
  const browser = await chromium.launch({
    executablePath: process.env.HOME + "/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome",
    args: ["--no-sandbox"],
  });
  for (const size of sizes) {
    const ctx = await browser.newContext({ viewport: { width: size.width, height: size.height }, colorScheme: "dark" });
    await ctx.addInitScript(() => {
      const account = { id: "shots", label: "Screenshots", email: null, deviceKey: "relay-adds-the-key", installId: "shots-install" };
      localStorage.setItem("cc.accounts", JSON.stringify([account]));
      localStorage.setItem("cc.activeAccount", "shots");
    });
    const page = await ctx.newPage();
    for (const p of paths.length ? paths : ["/factories"]) {
      await page.goto("http://127.0.0.1:5271" + p, { waitUntil: "networkidle" });
      await page.waitForTimeout(1500);
      // At phone width the side rail would take most of the screen; collapse it, as a person would.
      if (size.name === "phone") {
        const collapse = page.getByRole("button", { name: "Collapse the menu" });
        if (await collapse.count()) await collapse.click();
        await page.waitForTimeout(500);
      }
      const tag = p.includes("tab=manager") ? "-manager" : "";
      const file = `${prefix}${tag}-${size.name}.png`;
      await page.screenshot({ path: file, fullPage: true });
      console.log("wrote", file);
    }
    await ctx.close();
  }
  await browser.close();
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
