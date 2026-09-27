// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import {
  loadContractFixtures,
  servedBodyFor,
  browserRenderedAnswer,
  type BrowserRendered,
} from "@devthrottle/client-core/stats/throttleContractFixtures";

// THE CONTRACT ON THE RENDERED PHONE PAGE - THE WHOLE ANSWER (fix-round finding F-01, and F-08's "through
// the real consumer"). Every fixture in the product's tools/throttle-conformance/contract directory is
// served to the REAL page over the real client, and everything the page prints from the figure is read
// back off the DOM: both rings' printed percent, arc length and both counts, every surface segment's
// width, label, count and percent, and the counted total. The result is compared, as one object, with the
// answer the fixture records - the same answer the Cockpit page and the mentor report are held to.

vi.mock("@devthrottle/client-core/api/client", () => ({
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
  GatewayError: class GatewayError extends Error {
    constructor(public status: number, message: string) {
      super(message);
    }
  },
}));

import { YourThrottle } from "./YourThrottle";

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/throttle" element={<YourThrottle />} />
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

function count(text: string | null | undefined): number {
  return Number(String(text ?? "").replace(/,/g, "").trim());
}

function percent(text: string | null | undefined): number | null {
  const t = String(text ?? "").trim();
  return t === "n/a" ? null : Number(t.replace("%", ""));
}

function round10(n: number | null): number | null {
  return n === null ? null : Math.round(n * 1e10) / 1e10;
}

function arcShare(ring: Element): number | null {
  const arc = ring.querySelector(".mthr-ring-arc") as HTMLElement;
  const [filled, whole] = (arc.style.strokeDasharray || "").split(" ").map(Number);
  expect(whole).toBeCloseTo(C, 6);
  return round10(filled / whole);
}

function pct(el: Element): number {
  return Number(((el as HTMLElement).style.width || "0%").replace("%", ""));
}

function pageFromDom(container: HTMLElement) {
  const metrics = Array.from(container.querySelectorAll(".mthr-metric"));
  expect(metrics).toHaveLength(2);
  const [voiceRing, phoneRing] = metrics;
  const legend = (ring: Element) => Array.from(ring.querySelectorAll(".mthr-leg b")).map((b) => count(b.textContent));
  const [voiceTurns, typedTurns] = legend(voiceRing);
  // Broken out surface by surface, exactly as on the Cockpit (owner's ask, 2026-09-06).
  const [phoneTurns, ...restOfPhoneRing] = legend(phoneRing);
  const segments = Array.from(container.querySelectorAll(".mthr-split-seg")).map((seg) => {
    const title = /^(.+): (\d+) \((.+)\)$/.exec(seg.getAttribute("title") ?? "");
    return { label: title?.[1], turns: count(title?.[2]), percent: percent(title?.[3]), share: round10(pct(seg) / 100) };
  });
  const legendSurfaces = Array.from(container.querySelectorAll(".mthr-split-legend .mthr-leg")).map((leg) => ({
    label: (leg.textContent ?? "").replace(leg.querySelector("b")?.textContent ?? "", "").trim(),
    turns: count(leg.querySelector("b")?.textContent),
  }));
  return {
    denominator: count(container.querySelector(".mthr-stat-value")!.textContent),
    voiceTurns, typedTurns, phoneTurns,
    restOfPhoneRing,
    voiceShare: arcShare(voiceRing), phoneShare: arcShare(phoneRing),
    voicePercent: percent(voiceRing.querySelector(".mthr-ring-pct")!.textContent),
    phonePercent: percent(phoneRing.querySelector(".mthr-ring-pct")!.textContent),
    segments, legendSurfaces,
  };
}

describe("the Your Throttle contract, on the rendered phone page - the whole answer", () => {
  for (const fixture of loadContractFixtures()) {
    it(fixture.name.replace(/-/g, " "), async () => {
      vi.stubGlobal("fetch", vi.fn(async () =>
        new Response(JSON.stringify(servedBodyFor(fixture.wire)), { status: 200, headers: { "Content-Type": "application/json" } }),
      ));
      const { container } = renderAt("/throttle?week=2026-W35");
      if (fixture.expected.outcome === "refused") {
        const banner = await screen.findByRole("alert");
        expect(banner.textContent).toMatch(/GET \/stats\/data answered/);
        expect(container.querySelectorAll(".mthr-ring-pct")).toHaveLength(0);
        return;
      }
      if (fixture.expected.outcome === "empty") {
        await screen.findByText(/No turn counted in this window/);
        expect(container.querySelectorAll(".mthr-ring-pct")).toHaveLength(0);
        return;
      }
      const expected: BrowserRendered = browserRenderedAnswer(fixture.expected.rendered!);
      await waitFor(() => expect(container.querySelectorAll(".mthr-ring-pct")).toHaveLength(2));
      expect(pageFromDom(container)).toEqual({
        denominator: expected.denominator,
        voiceTurns: expected.voiceTurns, typedTurns: expected.typedTurns,
        phoneTurns: expected.phoneTurns,
        restOfPhoneRing: expected.surfaces.filter((s) => s.surface !== "phone" && s.turns > 0).map((s) => s.turns),
        voiceShare: expected.voiceShare, phoneShare: expected.phoneShare,
        voicePercent: expected.voicePercent, phonePercent: expected.phonePercent,
        segments: expected.segments.map((s) => ({ label: s.label, turns: s.turns, percent: s.percent, share: s.share })),
        legendSurfaces: expected.segments.map((s) => ({ label: s.label, turns: s.turns })),
      });
    });
  }
});

// WHO RUNS YOUR SESSIONS (owner's ask, 2026-09-27), read off the rendered card. The fixture's starter block is
// hostile: its groups' session shares (30, 60, 5, 5) are not what their counts (64, 150, 16, 8 of 238) divide
// to, so an arc drawn from the counts is caught. Every number is the served field.
describe("who runs your sessions, on the rendered phone page", () => {
  it("prints the served groups, draws the served shares, and scopes the rings to the sessions you started", async () => {
    const fixture = loadContractFixtures().find((f) => f.name === "the-headline-is-rendered-not-the-counts")!;
    const starters = (fixture.wire as { starters: { humanPercent: number; groups: { label: string; sessions: number; turns: number; sessionShare: number }[] } }).starters;
    vi.stubGlobal("fetch", vi.fn(async () =>
      new Response(JSON.stringify(servedBodyFor(fixture.wire)), { status: 200, headers: { "Content-Type": "application/json" } }),
    ));
    const { container } = renderAt("/throttle?week=2026-W35");
    const card = await screen.findByTestId("mthr-who");

    expect(card.querySelector(".mthr-who-pct")!.textContent).toBe(`${starters.humanPercent}%`);
    const [sessionsLegend, turnsLegend] = [
      Array.from(card.querySelectorAll(".mthr-metric-legend .mthr-who-leg")),
      Array.from(card.querySelectorAll(".mthr-split-legend .mthr-who-leg")),
    ];
    expect(sessionsLegend.map((l) => count(l.querySelector("b")!.textContent))).toEqual(starters.groups.map((g) => g.sessions));
    expect(sessionsLegend.map((l) => (l.textContent ?? "").replace(l.querySelector("b")!.textContent ?? "", "").trim()))
      .toEqual(starters.groups.map((g) => g.label));
    expect(turnsLegend.map((l) => count(l.querySelector("b")!.textContent))).toEqual(starters.groups.map((g) => g.turns));
    const arcs = Array.from(card.querySelectorAll(".mthr-who-arc")).map((a) => round10(Number(((a as HTMLElement).style.strokeDasharray).split(" ")[0]) / C));
    expect(arcs).toEqual(starters.groups.map((g) => round10(g.sessionShare)));

    // The rings, the split and the tiles say they cover only the sessions you started.
    expect(container.querySelectorAll(".mthr-metric-note")).toHaveLength(3);
    expect(Array.from(container.querySelectorAll(".mthr-stat-label")).map((l) => l.textContent)).toEqual(["Your turns", "Sessions you started"]);
  });
});
