import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { describe, expect, it } from "vitest";

// One Back on every phone screen (Web UI Polish phase 3). The phone had four kinds of back control: the word "Back" in
// the shared .back-link chrome, a bare arrow on Alerts, "<- Roster" on Start a session, and a smaller file-viewer
// button on the file and report viewers. This sweep reads every app bar the phone source draws and holds each to the
// one shape, so a new screen cannot quietly ship a fifth kind.

const SRC = join(__dirname, "..");

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const path = join(dir, e.name);
    if (e.isDirectory()) return sourceFiles(path);
    return e.name.endsWith(".tsx") && !e.name.endsWith(".test.tsx") ? [path] : [];
  });
}

/** Every app bar in a source file, as its text from the opening tag to the closing one. */
function appBarsIn(text: string): string[] {
  return [...text.matchAll(/<header className="app-bar[^"]*"[\s\S]*?<\/header>/g)].map((m) => m[0]);
}

/** The visible words of each .back-link control in an app bar: the text after the opening tag, up to its close. */
export function backLabelsIn(bar: string): string[] {
  return [...bar.matchAll(/className="back-link"([\s\S]*?)<\/(?:button|Link)>/g)].map((m) => {
    const body = m[1];
    return body.substring(body.lastIndexOf(">") + 1).trim();
  });
}

/** App bars that have no way back by design. Home IS where Back goes; the report landing page only finds the
 *  report and replaces itself with the report's own page, which has the Back. */
const NO_BACK = ["pages/Home.tsx", "pages/ReportLanding.tsx"];

function bars() {
  return sourceFiles(SRC).flatMap((file) =>
    appBarsIn(readFileSync(file, "utf8")).map((bar) => ({ file: relative(SRC, file).split("\\").join("/"), bar })),
  );
}

describe("one Back button on the phone", () => {
  it("EveryAppBar_WithAWayBack_UsesTheSharedBackWithTheWordBack", () => {
    const all = bars();
    // A presence, never only an absence: the sweep must have found the app bars it exists to check.
    expect(all.length).toBeGreaterThanOrEqual(15);

    const withBack = all.filter((b) => !NO_BACK.includes(b.file));
    const wrong = withBack
      .map((b) => ({ file: b.file, labels: backLabelsIn(b.bar) }))
      .filter((b) => b.labels.length !== 1 || b.labels[0] !== "Back");
    expect(wrong).toEqual([]);
  });

  it("NoAppBar_DrawsAnotherKindOfBack", () => {
    const other = bars()
      .filter((b) => /file-view-back|&larr;|BackIcon|Roster</.test(b.bar))
      .map((b) => b.file);
    expect(other).toEqual([]);
  });

  it("backLabelsIn_ReadsTheWords_EvenAfterAnArrowFunctionInAnAttribute", () => {
    const bar = `<header className="app-bar">
      <button type="button" className="back-link" onClick={() => go("/")} aria-label="Back to sessions">
        Back
      </button>
      <Link className="back-link" to="/">&larr; Roster</Link>
    </header>`;
    expect(backLabelsIn(bar)).toEqual(["Back", "&larr; Roster"]);
  });
});
