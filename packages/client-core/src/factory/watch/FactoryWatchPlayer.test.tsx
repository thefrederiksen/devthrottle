import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FactoryWatchPlayer } from "./FactoryWatchPlayer";
import fixture from "./factoryWatchView.fixture.json";
import { FACTORY_WATCH_STEP_KINDS, type FactoryWatchView } from "./factoryWatchTypes";
import { iso } from "./watchScene";

const samples = fixture as unknown as Record<string, FactoryWatchView>;
const hub = samples.hub;

// The player's clock is requestAnimationFrame. Here a frame runs only when a test asks for one, so every assertion
// sees an exact playback time and nothing depends on the machine's speed.
let frames: FrameRequestCallback[] = [];
let now = 0;
function runFrame(ms: number) {
  now += ms;
  const due = frames;
  frames = [];
  act(() => due.forEach((f) => f(now)));
}

beforeEach(() => {
  frames = [];
  now = 0;
  vi.stubGlobal("requestAnimationFrame", (f: FrameRequestCallback) => frames.push(f));
  vi.stubGlobal("cancelAnimationFrame", () => undefined);
  vi.spyOn(performance, "now").mockImplementation(() => now);
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const root = (c: HTMLElement) => c.querySelector(".fwp") as HTMLElement;
const button = (name: string) => screen.getByRole("button", { name, exact: true } as never);

describe("FactoryWatchPlayer", () => {
  it("draws every station at the tiles the view gives", () => {
    const { container } = render(<FactoryWatchPlayer view={hub} />);
    for (const s of hub.stations) {
      const g = container.querySelector(`[data-station="${s.id}"]`)!;
      expect(g).not.toBeNull();
      expect([g.getAttribute("data-gx"), g.getAttribute("data-gy"), g.getAttribute("data-width"), g.getAttribute("data-height")]).toEqual(
        [s.gx, s.gy, s.width, s.height].map(String),
      );
      // The box's top-left corner on the floor is drawn where the tile says.
      const [x, y] = iso(s.gx, s.gy);
      expect(g.querySelector("polygon")!.getAttribute("points")).toContain(`${x.toFixed(1)},${(y - (s.isBoss ? 34 : 24)).toFixed(1)}`);
    }
  });

  it("draws every character at its home spot, in its own colour", () => {
    const { container } = render(<FactoryWatchPlayer view={hub} />);
    for (const c of hub.characters) {
      const g = container.querySelector(`.fwp-character[data-seat="${c.seat}"]`)!;
      const [x, y] = iso(c.homeGx, c.homeGy);
      expect(g.getAttribute("data-x")).toBe(x.toFixed(1));
      expect(g.getAttribute("data-y")).toBe(y.toFixed(1));
      expect(g.innerHTML).toContain(`fill="${c.colour}"`);
    }
  });

  it("draws the line halls where the view puts them", () => {
    const lines = samples.lines;
    const { container } = render(<FactoryWatchPlayer view={lines} />);
    for (const l of lines.lines) {
      const g = container.querySelector(`[data-hall="${l.name}"]`)!;
      expect([g.getAttribute("data-gx"), g.getAttribute("data-gy")]).toEqual([String(l.gx), String(l.gy)]);
    }
  });

  for (const kind of FACTORY_WATCH_STEP_KINDS) {
    it(`a ${kind} step plays its animation on the seat it names`, () => {
      // Arrange
      const view = Object.values(samples).find((v) => v.steps.some((s) => s.kind === kind))!;
      const step = view.steps.find((s) => s.kind === kind)!;
      const { container } = render(<FactoryWatchPlayer view={view} />);

      // Act: jump to the step in the list, and let a moment of it play.
      fireEvent.click(container.querySelector(`.fwp-step[data-kind="${kind}"] .fwp-step-main`)!);
      runFrame(16);
      runFrame(100);

      // Assert
      const animating = [...container.querySelectorAll(".fwp-character")].filter((g) => g.getAttribute("data-animation") !== "");
      expect(animating.map((g) => [g.getAttribute("data-seat"), g.getAttribute("data-animation")])).toEqual([[step.seat, kind]]);
      const effect = { talked: "bubble", "checked-nothing": "magnifier", hiccup: "smoke" } as Record<string, string>;
      if (effect[kind]) expect(container.querySelector(`[data-effect="${effect[kind]}"]`)).not.toBeNull();
    });
  }

  it("the controls change the playback state", () => {
    const { container } = render(<FactoryWatchPlayer view={hub} />);
    const state = () => ({ ...root(container).dataset });

    fireEvent.click(button("Pause"));
    expect(state().playing).toBe("false");
    fireEvent.click(button("Play"));
    expect(state().playing).toBe("true");

    fireEvent.click(button("Slow motion"));
    expect(state().speed).toBe("0.25");
    expect(container.querySelector(".fwp-stage")!.className).toContain("fwp-slow");
    fireEvent.click(button("Fast"));
    expect(state().speed).toBe("3");

    fireEvent.click(button("Zoom in"));
    expect(Number(state().zoom)).toBeGreaterThan(1);
    fireEvent.click(button("Whole floor"));
    expect(state().zoom).toBe("1.00");

    fireEvent.change(container.querySelector(".fwp-follow select")!, { target: { value: "writer" } });
    expect(state().follow).toBe("writer");

    // Tapping a character follows it.
    fireEvent.click(container.querySelector('.fwp-character[data-seat="checker"]')!);
    expect(state().follow).toBe("checker");

    runFrame(16);
    runFrame(2000);
    fireEvent.click(button("Instant replay"));
    expect(state()).toMatchObject({ speed: "0.25", follow: hub.steps[0].seat });
    expect(Number(state().zoom)).toBeGreaterThan(1);

    fireEvent.click(button("From the start"));
    expect(state()).toMatchObject({ t: "0.00", playing: "true", speed: "1", follow: "", zoom: "1.00" });

    fireEvent.click(button("Live"));
    expect(state().live).toBe("false");
    fireEvent.click(button("Live"));
    expect(state().live).toBe("true");
  });

  it("live: steps added to the view appear in the list as they arrive", () => {
    const first = { ...hub, steps: hub.steps.slice(0, 3) };
    const { container, rerender } = render(<FactoryWatchPlayer view={first} />);
    expect(container.querySelectorAll(".fwp-step")).toHaveLength(3);

    rerender(<FactoryWatchPlayer view={hub} />);

    expect(container.querySelectorAll(".fwp-step")).toHaveLength(hub.steps.length);
  });

  it("an empty view shows the view's own empty sentence", () => {
    const empty = samples.empty;
    render(<FactoryWatchPlayer view={empty} />);
    expect(screen.getByTestId("fwp-empty").textContent).toBe(empty.emptyText);
    expect(button("Instant replay")).toHaveProperty("disabled", true);
  });

  it("the truncated sentence, when the view sends one", () => {
    const { container } = render(<FactoryWatchPlayer view={samples.lines} />);
    expect(container.querySelector(".fwp-truncated")!.textContent).toBe(samples.lines.truncatedText);
  });

  for (const name of Object.keys(samples)) {
    it(`${name}: draws no word that is not in the view (only the controls are the player's own)`, () => {
      const view = samples[name];
      const { container } = render(<FactoryWatchPlayer view={view} />);
      // Play a talked step, so a bubble is on the floor too.
      const talked = container.querySelector('.fwp-step[data-kind="talked"] .fwp-step-main');
      if (talked) fireEvent.click(talked);
      runFrame(16);
      runFrame(400);

      const allowed = [
        view.title,
        view.window.label,
        view.emptyText ?? "",
        view.truncatedText ?? "",
        ...view.notes,
        ...view.stations.flatMap((s) => [s.label, s.sub]),
        ...view.lines.map((l) => l.name),
        ...view.characters.map((c) => c.name),
        ...view.steps.flatMap((s) => [s.time, s.words, String(s.emptyChecks)]),
      ].filter((x) => x !== "");

      const drawn: string[] = [];
      const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
      for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        const text = n.textContent!.trim();
        if (text === "") continue;
        if ((n.parentElement as Element).closest("button, select, [data-chrome]")) continue;
        drawn.push(text);
      }
      expect(drawn.length).toBeGreaterThan(0);
      for (const text of drawn) expect(allowed.some((a) => a.includes(text)), `"${text}" is not in the view`).toBe(true);
    });
  }
});
