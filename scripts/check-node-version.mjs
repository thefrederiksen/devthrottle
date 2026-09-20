// THE NODE VERSION THIS WORKSPACE'S TESTS ARE RUN ON, CHECKED BEFORE THEY RUN.
//
// Why this exists. On 2026-09-19 the Cockpit suite reported 38 failures across 7 files on a developer
// machine, and every one of them was a lie: the machine had Node 26 while continuous integration pins
// Node 22, and the jsdom this workspace depends on predates Node 26. What broke was `window.localStorage`
// (undefined, so every test touching it threw) and `AbortSignal` (an undici change, so every test that
// navigates threw) - two surfaces wide enough that the failures looked like product defects and narrow
// enough that plenty of other tests still passed. An agent lost most of an hour proving the failures were
// not its change before anyone thought to look at the runtime.
//
// A wrong answer delivered confidently is worse than no answer, so this refuses to run at all rather than
// hand anyone a red suite it cannot vouch for. The version comes from the same place continuous
// integration reads it - .nvmrc at the root - so there is ONE number and no second copy to drift.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const wanted = Number(readFileSync(join(repoRoot, ".nvmrc"), "utf8").trim());
const running = Number(process.versions.node.split(".")[0]);

if (!Number.isInteger(wanted)) {
  console.error(`[check-node-version] .nvmrc does not hold a major version number. Fix it before running tests.`);
  process.exit(1);
}

if (running !== wanted) {
  console.error(
    [
      ``,
      `  These tests are not run on this version of Node, and running them here would tell you nothing.`,
      ``,
      `    wanted:  Node ${wanted}   (.nvmrc, and what continuous integration uses)`,
      `    running: Node ${running}   (${process.execPath})`,
      ``,
      `  On Node 26 this workspace's jsdom leaves window.localStorage undefined and rejects the AbortSignal`,
      `  that fetch is given, so dozens of tests fail for reasons that have nothing to do with the code.`,
      ``,
      `  On a Mac with Homebrew:`,
      `    brew install node@${wanted}`,
      `    PATH="/opt/homebrew/opt/node@${wanted}/bin:$PATH" npm test`,
      ``,
      `  Or make it the default for this shell:`,
      `    export PATH="/opt/homebrew/opt/node@${wanted}/bin:$PATH"`,
      ``,
    ].join("\n"),
  );
  process.exit(1);
}
