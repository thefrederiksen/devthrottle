import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FactoryWatchPlayer } from "./FactoryWatchPlayer";
import fixture from "./factoryWatchView.fixture.json";
import contributorFixture from "./factoryWatchView.contributor.fixture.json";
import { FACTORY_WATCH_STEP_KINDS, type FactoryWatchView } from "./factoryWatchTypes";
import { iso } from "./watchScene";

const samples = fixture as unknown as Record<string, FactoryWatchView>;
const hub = samples.hub;
const contributorSamples = contributorFixture as unknown as Record<string, FactoryWatchView>;
const own = contributorSamples.own;
const shared = contributorSamples.shared;
const allViews: Record<string, FactoryWatchView> = { ...samples, "contributor own": own, "contributor shared": shared };

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

  for (const [name, view] of Object.entries(allViews)) {
    it(`${name}: every station label is drawn after every character, so no lifted crate covers a label`, () => {
      const { container } = render(<FactoryWatchPlayer view={view} />);
      const labels = [...container.querySelectorAll("[data-label-for]")];
      expect(labels.length).toBe(view.stations.length);
      for (const character of container.querySelectorAll(".fwp-character")) {
        for (const label of labels) {
          // In SVG, what comes later in the document paints on top.
          expect(character.compareDocumentPosition(label) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        }
      }
    });
  }

  it("contributor: the lifted plain crate is drawn before the label of the station it stands at", () => {
    // Arrange
    const index = shared.steps.findIndex((s) => s.plain && s.kind === "talked");
    const step = shared.steps[index];
    const { container } = render(<FactoryWatchPlayer view={shared} />);

    // Act: a frame advances playback by at most a tenth of a second; eight of them bring the crate up.
    fireEvent.click(container.querySelectorAll(".fwp-step .fwp-step-main")[index]);
    for (let i = 0; i < 8; i++) runFrame(100);

    // Assert
    const crate = container.querySelector('[data-glyph="plain-crate"]')!;
    expect(crate).not.toBeNull();
    const station = shared.characters.find((c) => c.seat === step.seat)!.station;
    const label = container.querySelector(`[data-label-for="${station}"]`)!;
    expect(label).not.toBeNull();
    expect(crate.compareDocumentPosition(label) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  for (const kind of FACTORY_WATCH_STEP_KINDS) {
    it(`a ${kind} step plays its animation on the seat it names`, () => {
      // Arrange
      const view = Object.values(allViews).find((v) => v.steps.some((s) => s.kind === kind && !s.plain))!;
      const index = view.steps.findIndex((s) => s.kind === kind && !s.plain);
      const step = view.steps[index];
      const { container } = render(<FactoryWatchPlayer view={view} />);

      // Act: jump to the step in the list, and let a moment of it play.
      fireEvent.click(container.querySelectorAll(".fwp-step .fwp-step-main")[index]);
      runFrame(16);
      runFrame(100);

      // Assert
      const animating = [...container.querySelectorAll(".fwp-character")].filter((g) => g.getAttribute("data-animation") !== "");
      expect(animating.map((g) => [g.getAttribute("data-seat"), g.getAttribute("data-animation")])).toEqual([[step.seat, kind]]);
      const effect: Record<string, string> = {
        woke: '[data-glyph="exclamation"]',
        talked: '[data-effect="bubble"]',
        "checked-nothing": '[data-effect="magnifier"]',
        hiccup: '[data-effect="smoke"]',
        waiting: '[data-glyph="hourglass"]',
      };
      if (effect[kind]) expect(container.querySelector(effect[kind])).not.toBeNull();
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

    // The player opens at the live edge, so an instant replay replays the last step.
    fireEvent.click(button("Instant replay"));
    expect(state()).toMatchObject({ speed: "0.25", follow: hub.steps[hub.steps.length - 1].seat });
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

  it("contributor: a step the view marks plain is a plain crate with no words, in the list and on the floor", () => {
    const contributor = shared;
    const { container } = render(<FactoryWatchPlayer view={contributor} />);
    const index = contributor.steps.findIndex((s) => s.plain && s.kind === "talked");

    fireEvent.click(container.querySelectorAll(".fwp-step .fwp-step-main")[index]);
    // A frame advances playback by at most a tenth of a second; eight of them bring the crate up.
    for (let i = 0; i < 8; i++) runFrame(100);

    const row = container.querySelectorAll(".fwp-step")[index];
    expect(row.querySelector(".fwp-step-words")).toBeNull();
    const g = container.querySelector(`.fwp-character[data-seat="${contributor.steps[index].seat}"]`)!;
    expect(g.getAttribute("data-animation")).toBe("plain-crate");
    expect(g.querySelector('[data-glyph="plain-crate"]')).not.toBeNull();
    expect(container.querySelector('[data-effect="bubble"]')).toBeNull();
  });

  it("an owner step is never a plain crate, though the owner view sends mine false on every step", () => {
    const { container } = render(<FactoryWatchPlayer view={hub} />);
    const index = hub.steps.findIndex((s) => s.kind === "delivered");
    expect(hub.steps.every((s) => !s.mine && !s.plain)).toBe(true);

    fireEvent.click(container.querySelectorAll(".fwp-step .fwp-step-main")[index]);
    for (let i = 0; i < 8; i++) runFrame(100);

    expect(container.querySelector('[data-glyph="plain-crate"]')).toBeNull();
    expect(container.querySelector(`.fwp-character[data-seat="${hub.steps[index].seat}"]`)!.getAttribute("data-animation")).toBe("delivered");
  });

  it("a hiccup with no alarm draws no smoke and no wires on the floor", () => {
    const contributor = own;
    const { container } = render(<FactoryWatchPlayer view={contributor} />);
    const index = contributor.steps.findIndex((s) => s.kind === "hiccup");
    expect(contributor.steps[index].alarm).toBe(false);

    fireEvent.click(container.querySelectorAll(".fwp-step .fwp-step-main")[index]);
    for (let i = 0; i < 5; i++) runFrame(100);

    const g = container.querySelector(`.fwp-character[data-seat="${contributor.steps[index].seat}"]`)!;
    expect(g.getAttribute("data-animation")).toBe("hiccup");
    expect(container.querySelector('[data-effect="smoke"]')).toBeNull();
    expect(container.querySelector('[data-glyph="wires"]')).toBeNull();
  });

  it("contributor: no notes and no truncated sentence are drawn when the view has none", () => {
    const { container } = render(<FactoryWatchPlayer view={own} />);
    expect(container.querySelector(".fwp-notes")).toBeNull();
    expect(container.querySelector(".fwp-truncated")).toBeNull();
  });

  it("an empty view shows the view's own empty sentence, and offers no replay", () => {
    const empty = samples.empty;
    const { container } = render(<FactoryWatchPlayer view={empty} />);
    expect(screen.getByTestId("fwp-empty").textContent).toBe(empty.emptyText);
    expect(button("Instant replay")).toHaveProperty("disabled", true);
    expect(empty.replayText).toBeNull();
    expect(container.querySelector(".fwp-replay-last")).toBeNull();
  });

  for (const name of ["hub", "contributor own"]) {
    it(`${name}: opens on the live floor at rest, with the view's replay button; the button plays from the start`, () => {
      // Arrange
      const view = allViews[name];
      const { container } = render(<FactoryWatchPlayer view={view} />);
      const state = () => ({ ...root(container).dataset });
      runFrame(16);

      // Assert: live, at the end of the timeline, nobody animating, the button in the view's own words.
      expect(state()).toMatchObject({ live: "true", playing: "true" });
      expect(Number(state().t)).toBeGreaterThan(0);
      expect([...container.querySelectorAll(".fwp-character")].every((g) => g.getAttribute("data-animation") === "")).toBe(true);
      const replay = button(view.replayText!);
      expect(replay.closest(".fwp-replay-last")).not.toBeNull();

      // Act
      fireEvent.click(replay);

      // Assert: back to the start, and the button steps aside while the replay plays.
      expect(state().t).toBe("0.00");
      expect(container.querySelector(".fwp-replay-last")).toBeNull();
    });
  }

  it("the replay button's words are the view's, never the player's own", () => {
    render(<FactoryWatchPlayer view={{ ...hub, replayText: "See the last run again" }} />);
    expect(button("See the last run again")).not.toBeNull();
    expect(screen.queryByText("Watch what happened last")).toBeNull();
  });

  it("the truncated sentence, when the view sends one", () => {
    const { container } = render(<FactoryWatchPlayer view={samples.lines} />);
    expect(container.querySelector(".fwp-truncated")!.textContent).toBe(samples.lines.truncatedText);
  });

  for (const name of Object.keys(allViews)) {
    it(`${name}: draws no word that is not in the view (only the controls are the player's own)`, () => {
      const view = allViews[name];
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
        view.replayText ?? "",
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
