import { describe, expect, it } from "vitest";
import fixture from "./factoryWatchView.fixture.json";
import type { FactoryWatchStep, FactoryWatchStepKind, FactoryWatchView } from "./factoryWatchTypes";
import { IDLE_LAMP, LAMP, LEAD_IN, STEP_GAP, buildTimeline, iso, sceneAt, worldBox, wrapWords } from "./watchScene";

const samples = fixture as unknown as Record<string, FactoryWatchView>;
const hub = samples.hub;
const lines = samples.lines;

/** The scene in the middle of the first step of this kind in the hub sample. */
function midStep(view: FactoryWatchView, kind: FactoryWatchStepKind, at = 0.5) {
  const timeline = buildTimeline(view);
  const ts = timeline.steps.find((s) => s.step.kind === kind)!;
  const t = ts.start + (ts.end - ts.start) * at;
  return { scene: sceneAt(view, timeline, t), ts };
}

describe("buildTimeline", () => {
  it("plays the steps one after another, in the view's order", () => {
    // Act
    const timeline = buildTimeline(hub);

    // Assert
    expect(timeline.steps.map((s) => s.step.key)).toEqual(hub.steps.map((s) => s.key));
    expect(timeline.steps[0].start).toBe(LEAD_IN);
    for (let i = 1; i < timeline.steps.length; i++) expect(timeline.steps[i].start).toBeCloseTo(timeline.steps[i - 1].end + STEP_GAP);
    expect(timeline.total).toBeCloseTo(timeline.steps[timeline.steps.length - 1].end + STEP_GAP);
  });

  it("live: a step appended later never moves the earlier ones, and starts where playback ended", () => {
    // Arrange
    const before = buildTimeline({ steps: hub.steps.slice(0, 4) });

    // Act
    const after = buildTimeline({ steps: hub.steps.slice(0, 5) });

    // Assert
    expect(after.steps.slice(0, 4).map((s) => s.start)).toEqual(before.steps.map((s) => s.start));
    expect(after.steps[4].start).toBeCloseTo(before.total);
  });

  it("throws on a step kind it has no animation for, instead of drawing nothing", () => {
    const odd = { ...hub.steps[0], kind: "teleported" } as unknown as FactoryWatchStep;
    expect(() => buildTimeline({ steps: [odd] })).toThrow(/no animation/);
  });
});

describe("sceneAt: each step kind has its one animation", () => {
  it("woke: amber lamp, the character jumps up surprised, then hammers at its bench", () => {
    const early = midStep(hub, "woke", 0.1);
    const late = midStep(hub, "woke", 0.7);
    const seat = early.ts.step.seat;
    const station = hub.characters.find((c) => c.seat === seat)!.station;

    expect(early.scene.lamps[station]).toBe(LAMP.woke);
    expect(early.scene.poses.find((p) => p.seat === seat)).toMatchObject({ animation: "woke", expression: "surprised" });
    expect(late.scene.poses.find((p) => p.seat === seat)).toMatchObject({ animation: "woke", expression: "focused", busy: true });
  });

  it("delivered: green lamp, the character carries a crate to the Boss, confetti where it lands", () => {
    const walking = midStep(hub, "delivered", 0.25);
    const landed = midStep(hub, "delivered", 0.5);
    const seat = walking.ts.step.seat;
    const station = hub.characters.find((c) => c.seat === seat)!.station;
    const home = hub.characters.find((c) => c.seat === seat)!;

    const pose = walking.scene.poses.find((p) => p.seat === seat)!;
    expect(walking.scene.lamps[station]).toBe(LAMP.delivered);
    expect(pose).toMatchObject({ animation: "delivered", carry: true, walking: true });
    expect([pose.x, pose.y]).not.toEqual(iso(home.homeGx, home.homeGy));
    expect(landed.scene.confetti).not.toBeNull();
  });

  it("talked: blue lamp and the step's own words in a bubble over the speaker", () => {
    const { scene, ts } = midStep(hub, "talked");
    const station = hub.characters.find((c) => c.seat === ts.step.seat)!.station;

    expect(scene.lamps[station]).toBe(LAMP.talked);
    expect(scene.bubble).toMatchObject({ seat: ts.step.seat, words: ts.step.words });
    expect(scene.poses.find((p) => p.seat === ts.step.seat)!.expression).toBe("talking");
  });

  it("checked-nothing: a magnifier at the station with the view's count, the character peeks then shrugs", () => {
    const peek = midStep(hub, "checked-nothing", 0.2);
    const shrug = midStep(hub, "checked-nothing", 0.6);
    const seat = peek.ts.step.seat;

    expect(peek.scene.magnifier).toMatchObject({ count: peek.ts.step.emptyChecks });
    expect(peek.ts.step.emptyChecks).toBe(3);
    expect(peek.scene.poses.find((p) => p.seat === seat)!.expression).toBe("peek");
    expect(shrug.scene.poses.find((p) => p.seat === seat)!.expression).toBe("shrug");
  });

  it("hiccup: the lamp blinks red, wires spark and the character goes dizzy, then worried", () => {
    const early = midStep(hub, "hiccup", 0.2);
    const late = midStep(hub, "hiccup", 0.9);
    const seat = early.ts.step.seat;

    expect(early.scene.poses.find((p) => p.seat === seat)).toMatchObject({ animation: "hiccup", expression: "dizzy", wires: true });
    expect(early.scene.smoke).not.toBeNull();
    expect(late.scene.poses.find((p) => p.seat === seat)!.expression).toBe("worried");
    // The blink: across a second of the step, the lamp is red at some moments and dim at others.
    const timeline = buildTimeline(hub);
    const station = hub.characters.find((c) => c.seat === seat)!.station;
    const colours = new Set(Array.from({ length: 20 }, (_, i) => sceneAt(hub, timeline, early.ts.start + i * 0.05).lamps[station]));
    expect(colours).toEqual(new Set([LAMP.hiccup, IDLE_LAMP]));
  });

  it("only the seat named by the step moves; every other character rests at its home", () => {
    const { scene, ts } = midStep(hub, "delivered", 0.25);
    for (const c of hub.characters.filter((c) => c.seat !== ts.step.seat)) {
      const pose = scene.poses.find((p) => p.seat === c.seat)!;
      expect(pose.animation).toBeNull();
      expect(pose.x).toBeCloseTo(iso(c.homeGx, c.homeGy)[0]);
    }
  });

  it("between steps nothing animates, and the clock shows the last step's own time", () => {
    const timeline = buildTimeline(hub);
    const scene = sceneAt(hub, timeline, timeline.steps[0].end + STEP_GAP / 2);
    expect(scene.active).toBeNull();
    expect(scene.poses.every((p) => p.animation === null)).toBe(true);
    expect(scene.clock).toBe(hub.steps[0].time);
  });

  it("an empty view: every character asleep, no clock", () => {
    const scene = sceneAt(samples.empty, buildTimeline(samples.empty), 3);
    expect(scene.poses.every((p) => p.expression === "sleep")).toBe(true);
    expect(scene.clock).toBeNull();
  });
});

describe("drawing helpers", () => {
  it("the fit box holds every station of every sample", () => {
    for (const view of [hub, lines, samples.empty]) {
      const box = worldBox(view);
      for (const s of view.stations)
        for (const [gx, gy] of [[s.gx, s.gy], [s.gx + s.width, s.gy + s.height], [s.gx, s.gy + s.height], [s.gx + s.width, s.gy]]) {
          const [x, y] = iso(gx, gy, 40);
          expect(x).toBeGreaterThanOrEqual(box.x);
          expect(x).toBeLessThanOrEqual(box.x + box.w);
          expect(y).toBeGreaterThanOrEqual(box.y);
          expect(y).toBeLessThanOrEqual(box.y + box.h);
        }
    }
  });

  it("wrapWords keeps every word and only ever breaks between them", () => {
    const words = "The Photo Desk asked the Boss which photo goes first.";
    const out = wrapWords(words, 20);
    expect(out.join(" ")).toBe(words);
    for (const line of out) expect(words).toContain(line);
  });
});
