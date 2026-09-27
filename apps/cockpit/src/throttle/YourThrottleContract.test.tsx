// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import {
  loadContractFixtures,
  servedBodyFor,
  browserRenderedAnswer,
  type BrowserRendered,
} from "@devthrottle/client-core/stats/throttleContractFixtures";

// THE CONTRACT ON THE RENDERED COCKPIT PAGE - THE WHOLE ANSWER (fix-round finding F-01, and F-08's "through
// the real consumer"). Every fixture in the product's tools/throttle-conformance/contract directory is
// served to the REAL page over the real client, and EVERYTHING the page puts in front of the reader from the
// figure is read back off the DOM: both rings' printed percent, arc length, and both counts; the denominator
// line; every surface segment's width, label, count and printed percent, and the surface table; the hour's
// split on the Activity tab; every card and every row on the Agents and Repos tabs, under both rankings.
// The result is compared, as one object, with the answer the fixture records - the same answer the mentor
// report's suite compares its rendered page against for the headline part.
//
// The previous contract read the two ring percentages and nothing else, so a page that replaced a count or
// a segment with a constant stayed green. This one cannot: replace any rendered value and the object differs.

vi.mock("@devthrottle/client-core/api/client", () => ({
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
  GatewayError: class GatewayError extends Error {
    constructor(public status: number, message: string) {
      super(message);
    }
  },
}));

import { YourThrottleView } from "./YourThrottleView";

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/your-throttle" element={<YourThrottleView />} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

const R = 42;
const C = 2 * Math.PI * R;

/** A printed count back to its number ("1,015" -> 1015). */
function count(text: string | null | undefined): number {
  return Number(String(text ?? "").replace(/,/g, "").trim());
}

/** A printed percent back to its number-or-null ("57%" -> 57, "n/a" -> null). */
function percent(text: string | null | undefined): number | null {
  const t = String(text ?? "").trim();
  return t === "n/a" ? null : Number(t.replace("%", ""));
}

function round10(n: number | null): number | null {
  return n === null ? null : Math.round(n * 1e10) / 1e10;
}

/** The arc's drawn share, back from the ring's stroke-dasharray "filled circumference". */
function arcShare(ring: Element): number | null {
  const arc = ring.querySelector(".thr-ring-arc") as HTMLElement;
  const [filled, whole] = (arc.style.strokeDasharray || "").split(" ").map(Number);
  expect(whole).toBeCloseTo(C, 6);
  return round10(filled / whole);
}

function pct(el: Element): number {
  return Number(((el as HTMLElement).style.width || "0%").replace("%", ""));
}

/** Everything the Overview tab prints from the figure. */
function overviewFromDom(container: HTMLElement) {
  const heroes = Array.from(container.querySelectorAll(".thr-hero"));
  expect(heroes).toHaveLength(2);
  const [voiceRing, phoneRing] = heroes;
  const legend = (ring: Element) => Array.from(ring.querySelectorAll(".thr-hero-leg b")).map((b) => count(b.textContent));
  const [voiceTurns, typedTurns] = legend(voiceRing);
  // The phone ring names its other side surface by surface now (owner's ask, 2026-09-06), so the legend is
  // the phone count followed by one count per remaining surface that has a turn.
  const [phoneTurns, ...restOfPhoneRing] = legend(phoneRing);
  const sub = container.querySelector(".thr-panel-sub")!.textContent ?? "";
  const denominator = count(/^(\S+) turns across every surface$/.exec(sub)?.[1]);
  const segments = Array.from(container.querySelectorAll(".thr-split-seg")).map((seg) => {
    const title = /^(.+): (\d+) turns \((.+)\)$/.exec(seg.getAttribute("title") ?? "");
    const label = seg.querySelector(".thr-split-lbl")?.textContent ?? null;
    return { label: title?.[1], turns: count(title?.[2]), percent: percent(title?.[3]), share: round10(pct(seg) / 100), printed: label === null ? null : percent(label) };
  });
  const legendSurfaces = Array.from(container.querySelectorAll(".thr-split-leg")).map((leg) => ({
    label: (leg.textContent ?? "").replace(leg.querySelector("b")?.textContent ?? "", "").trim(),
    turns: count(leg.querySelector("b")?.textContent),
  }));
  return {
    denominator,
    voiceTurns, typedTurns, phoneTurns,
    restOfPhoneRing,
    voiceShare: arcShare(voiceRing), phoneShare: arcShare(phoneRing),
    voicePercent: percent(voiceRing.querySelector(".thr-ring-pct")!.textContent),
    phonePercent: percent(phoneRing.querySelector(".thr-ring-pct")!.textContent),
    segments, legendSurfaces,
  };
}

/** The ranked rows and headline cards of the Agents or Repos tab as printed. */
function rankedTabFromDom(container: HTMLElement) {
  const cards = Array.from(container.querySelectorAll(".repos-cards .thr-stat")).map((card) => ({
    value: card.querySelector(".thr-stat-value")!.textContent,
    sub: card.querySelector(".thr-stat-sub")!.textContent,
  }));
  const rows = Array.from(container.querySelectorAll(".repo-row")).map((row) => ({
    name: row.querySelector(".repo-name")!.textContent,
    value: count(row.querySelector(".repo-val-num")!.textContent),
    share: percent(row.querySelector(".repo-val-share")!.textContent),
    meta: row.querySelector(".repo-meta")!.textContent,
    voiceWidth: round10(pct(row.querySelector(".repo-bar-voice")!) / 100),
  }));
  return { cards, rows };
}

function clickTab(name: string) {
  fireEvent.click(screen.getByRole("tab", { name }));
}

function clickRanking(container: HTMLElement, name: string) {
  fireEvent.click(within(container.querySelector(".repos-seg")! as HTMLElement).getByRole("tab", { name }));
}

describe("the Your Throttle contract, on the rendered Cockpit page - the whole answer", () => {
  for (const fixture of loadContractFixtures()) {
    it(fixture.name.replace(/-/g, " "), async () => {
      vi.stubGlobal("fetch", vi.fn(async () =>
        new Response(JSON.stringify(servedBodyFor(fixture.wire)), { status: 200, headers: { "Content-Type": "application/json" } }),
      ));
      const { container } = renderAt("/your-throttle?week=2026-W35");
      if (fixture.expected.outcome === "refused") {
        const banner = await screen.findByRole("alert");
        expect(banner.textContent).toMatch(/GET \/stats\/data answered/);
        expect(container.querySelectorAll(".thr-ring-pct")).toHaveLength(0);
        return;
      }
      if (fixture.expected.outcome === "empty") {
        await screen.findByText(/No turn counted in this window/);
        expect(container.querySelectorAll(".thr-ring-pct")).toHaveLength(0);
        clickTab("Agents");
        await screen.findByText(/No agent usage counted/);
        clickTab("Repos");
        await screen.findByText(/No turn counted in this window/);
        return;
      }
      const expected: BrowserRendered = browserRenderedAnswer(fixture.expected.rendered!);
      await waitFor(() => expect(container.querySelectorAll(".thr-ring-pct")).toHaveLength(2));

      // Overview: rings, counts, denominator, the surface split.
      const overview = overviewFromDom(container);
      expect(overview).toEqual({
        denominator: expected.denominator,
        voiceTurns: expected.voiceTurns, typedTurns: expected.typedTurns,
        phoneTurns: expected.phoneTurns,
        // Each remaining surface with a turn, named on its own - never one added-up "Desktop + Cockpit".
        restOfPhoneRing: expected.surfaces.filter((s) => s.surface !== "phone" && s.turns > 0).map((s) => s.turns),
        voiceShare: expected.voiceShare, phoneShare: expected.phoneShare,
        voicePercent: expected.voicePercent, phonePercent: expected.phonePercent,
        segments: expected.segments.map((s) => ({ label: s.label, turns: s.turns, percent: s.percent, share: s.share, printed: s.share !== null && s.share * 100 >= 8 ? s.percent : null })),
        legendSurfaces: expected.segments.map((s) => ({ label: s.label, turns: s.turns })),
      });

      // Breakdown: every surface, zero or not, with the Gateway's label and count.
      clickTab("Breakdown");
      const surfaceRows = Array.from(container.querySelectorAll(".thr-table")[0].querySelectorAll("tbody tr")).map((tr) => ({
        label: tr.children[0].textContent, turns: count(tr.children[1].textContent),
      }));
      expect(surfaceRows).toEqual(expected.surfaces.map((s) => ({ label: s.label, turns: s.turns })));

      // Activity: the one hour's split is the Gateway's share, not the counts divided.
      clickTab("Activity");
      await waitFor(() => expect(container.querySelectorAll(".thr-seg-voice").length).toBeGreaterThan(0));
      const drawn = Array.from(container.querySelectorAll(".thr-bar"))
        .map((bar) => ({
          title: bar.parentElement!.getAttribute("title"),
          voice: round10(Number((bar.querySelector(".thr-seg-voice") as HTMLElement).style.height.replace("%", "")) / 100),
          typed: round10(Number((bar.querySelector(".thr-seg-typed") as HTMLElement).style.height.replace("%", "")) / 100),
        }))
        .filter((column) => column.voice !== 0 || column.typed !== 0);
      expect(drawn).toEqual(expected.hourly.map((h) => ({
        title: expect.stringContaining(`${h.turns} turns (${h.voiceTurns} voice, ${h.typedTurns} typed)`),
        voice: h.voiceShare, typed: h.typedShare,
      })));

      // Agents: the cards are the summary's, the rows' shares are the rows' own, under both rankings.
      clickTab("Agents");
      await waitFor(() => expect(container.querySelectorAll(".repo-row").length).toBeGreaterThan(0));
      const a = expected.agentsSummary;
      const agentCards = [
        { value: String(a.agentCount), sub: `${a.totalSessions} session${a.totalSessions === 1 ? "" : "s"} in total` },
        { value: a.totalTurns.toLocaleString(), sub: expect.any(String) },
        { value: a.topPercent === null ? "n/a" : `${a.topPercent}%`, sub: a.topAgentName !== null ? `${a.topAgentName} leads` : "of your turns" },
        { value: a.voicePercent === null ? "n/a" : `${a.voicePercent}%`, sub: "of turns spoken, not typed" },
        ...(a.agentDrivenTurns > 0 ? [{ value: a.leverageText ?? "-", sub: `${a.agentDrivenTurns.toLocaleString()} turns agents drove agents` }] : []),
      ];
      const byTurns = rankedTabFromDom(container);
      expect(byTurns.cards).toEqual(agentCards);
      expect(byTurns.rows).toEqual(expected.agents.map((row) => ({
        name: row.agentName, value: row.turns, share: row.turnPercent,
        meta: `${row.sessions} session${row.sessions === 1 ? "" : "s"} - ${row.voicePercent === null ? "n/a" : row.voicePercent + "%"} voice${row.agentDrivenTurns > 0 ? ` - ${row.agentDrivenTurns.toLocaleString()} from agents` : ""}`,
        voiceWidth: row.voiceShare,
      })));
      clickRanking(container, "Sessions");
      const bySessions = rankedTabFromDom(container);
      expect(bySessions.rows.map((r) => ({ value: r.value, share: r.share }))).toEqual(expected.agents.map((row) => ({ value: row.sessions, share: row.sessionPercent })));

      // Repos: the same, for the repository split.
      clickTab("Repos");
      await waitFor(() => expect(container.querySelectorAll(".repo-row").length).toBeGreaterThan(0));
      const r = expected.reposSummary;
      const repoCards = [
        { value: String(r.repoCount), sub: `${r.totalSessions} session${r.totalSessions === 1 ? "" : "s"} in total` },
        { value: r.totalTurns.toLocaleString(), sub: expect.any(String) },
        { value: r.topPercent === null ? "n/a" : `${r.topPercent}%`, sub: r.topRepoName !== null ? `${r.topRepoName} leads` : "of your turns" },
        { value: r.voicePercent === null ? "n/a" : `${r.voicePercent}%`, sub: "of turns spoken, not typed" },
      ];
      const repos = rankedTabFromDom(container);
      expect(repos.cards).toEqual(repoCards);
      expect(repos.rows).toEqual(expected.repos.map((row) => ({
        name: row.repoName, value: row.turns, share: row.turnPercent,
        meta: `${row.sessions} session${row.sessions === 1 ? "" : "s"} - ${row.voicePercent === null ? "n/a" : row.voicePercent + "%"} voice`,
        voiceWidth: row.voiceShare,
      })));
      clickRanking(container, "Sessions");
      expect(rankedTabFromDom(container).rows.map((x) => ({ value: x.value, share: x.share }))).toEqual(expected.repos.map((row) => ({ value: row.sessions, share: row.sessionPercent })));
    });
  }
});

// WHO RUNS YOUR SESSIONS (owner's ask, 2026-09-27), read off the rendered Cockpit panel. The fixture's groups
// carry session shares and percents (30, 60, 5, 5) that their counts (64, 150, 16, 8 of 238) do not divide to,
// so a page that computes its own is caught. Every number is the served field.
describe("who runs your sessions, on the rendered Cockpit page", () => {
  it("prints the served groups and percents, draws the served shares, and renames the tiles", async () => {
    const fixture = loadContractFixtures().find((f) => f.name === "the-headline-is-rendered-not-the-counts")!;
    const starters = (fixture.wire as { starters: { humanPercent: number; groups: { label: string; sessions: number; turns: number; sessionShare: number; sessionPercent: number }[] } }).starters;
    vi.stubGlobal("fetch", vi.fn(async () =>
      new Response(JSON.stringify(servedBodyFor(fixture.wire)), { status: 200, headers: { "Content-Type": "application/json" } }),
    ));
    const { container } = renderAt("/your-throttle?week=2026-W35");
    const panel = await screen.findByTestId("thr-who");

    expect(panel.querySelector(".thr-who-pct")!.textContent).toBe(`${starters.humanPercent}%`);
    const legends = Array.from(panel.querySelectorAll(".thr-who-legend"));
    const sessionsLegend = Array.from(legends[0].querySelectorAll(".thr-who-leg"));
    expect(sessionsLegend.map((l) => count(l.querySelector("b")!.textContent))).toEqual(starters.groups.map((g) => g.sessions));
    expect(sessionsLegend.map((l) => percent(l.querySelector(".thr-who-pctsmall")!.textContent))).toEqual(starters.groups.map((g) => g.sessionPercent));
    expect(Array.from(legends[1].querySelectorAll(".thr-who-leg b")).map((b) => count(b.textContent))).toEqual(starters.groups.map((g) => g.turns));
    const arcs = Array.from(panel.querySelectorAll(".thr-who-arc")).map((a) => round10(Number(((a as HTMLElement).style.strokeDasharray).split(" ")[0]) / C));
    expect(arcs).toEqual(starters.groups.map((g) => round10(g.sessionShare)));

    expect(container.querySelectorAll(".thr-hero-note")).toHaveLength(2);
    expect(container.querySelector(".thr-scope")!.textContent).toBe("Only sessions you started");
    expect(screen.getByText("Your turns")).toBeTruthy();
    expect(screen.getByText("Sessions you started")).toBeTruthy();
  });
});
