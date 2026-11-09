import { describe, expect, it } from "vitest";
import fixture from "./factoryWatchView.fixture.json";
import type { FactoryWatchView } from "./factoryWatchTypes";
import { FAST, FOLLOW_ZOOM, MAX_ZOOM, NORMAL, SLOW_MOTION, cameraBox, initialPlayback, playbackReducer } from "./watchPlayback";
import { buildTimeline, worldBox } from "./watchScene";

const hub = (fixture as unknown as Record<string, FactoryWatchView>).hub;
const timeline = buildTimeline(hub);
const world = worldBox(hub);

describe("playbackReducer", () => {
  it("starts playing from the start, live, at normal speed, showing the whole floor", () => {
    expect(initialPlayback()).toMatchObject({ t: 0, playing: true, live: true, speed: NORMAL, zoom: 1, follow: null });
  });

  it("tick advances time by the speed", () => {
    const s = playbackReducer({ ...initialPlayback(), speed: SLOW_MOTION }, { type: "tick", dt: 1, timeline });
    expect(s.t).toBeCloseTo(0.25);
  });

  it("pause stops time, play starts it again", () => {
    const paused = playbackReducer(initialPlayback(), { type: "toggle-play", timeline });
    expect(paused.playing).toBe(false);
    expect(playbackReducer(paused, { type: "tick", dt: 1, timeline })).toBe(paused);
    expect(playbackReducer(paused, { type: "toggle-play", timeline }).playing).toBe(true);
  });

  it("live: at the end it keeps playing, so a step that arrives later plays at once", () => {
    const end = playbackReducer({ ...initialPlayback(), t: timeline.total - 0.1 }, { type: "tick", dt: 1, timeline });
    expect(end).toMatchObject({ t: timeline.total, playing: true });
  });

  it("not live: at the end it stops, and play replays from the start", () => {
    const end = playbackReducer({ ...initialPlayback(), live: false, t: timeline.total - 0.1 }, { type: "tick", dt: 1, timeline });
    expect(end).toMatchObject({ t: timeline.total, playing: false });
    expect(playbackReducer(end, { type: "toggle-play", timeline })).toMatchObject({ t: 0, playing: true });
  });

  it("go-live jumps to the newest step; stop-live turns live off", () => {
    const live = playbackReducer({ ...initialPlayback(), live: false, playing: false }, { type: "go-live", timeline });
    expect(live).toMatchObject({ live: true, playing: true, t: timeline.steps[timeline.steps.length - 1].start });
    expect(playbackReducer(live, { type: "stop-live" }).live).toBe(false);
  });

  it("from the start: back to zero, playing, normal speed, whole floor", () => {
    const busy = { ...initialPlayback(), t: 20, playing: false, speed: FAST, zoom: 3, follow: "boss" };
    expect(playbackReducer(busy, { type: "restart" })).toMatchObject({ t: 0, playing: true, speed: NORMAL, zoom: 1, follow: null });
  });

  it("slow motion and fast set the speed", () => {
    expect(playbackReducer(initialPlayback(), { type: "speed", speed: SLOW_MOTION }).speed).toBe(SLOW_MOTION);
    expect(playbackReducer(initialPlayback(), { type: "speed", speed: FAST }).speed).toBe(FAST);
  });

  it("instant replay: the last step that started, again, in slow motion, following its seat, zoomed in", () => {
    // Arrange: playback in the middle of the fifth step.
    const fifth = timeline.steps[4];
    const s = { ...initialPlayback(), t: (fifth.start + fifth.end) / 2 };

    // Act
    const replay = playbackReducer(s, { type: "instant-replay", timeline });

    // Assert
    expect(replay).toMatchObject({ t: fifth.start, playing: true, speed: SLOW_MOTION, replayUntil: fifth.end, follow: fifth.step.seat, zoom: FOLLOW_ZOOM });
  });

  it("instant replay of a chosen step, and when it ends: normal speed, whole floor again", () => {
    const replay = playbackReducer(initialPlayback(), { type: "instant-replay", timeline, index: 2 });
    expect(replay.t).toBe(timeline.steps[2].start);

    const after = playbackReducer({ ...replay, t: timeline.steps[2].end - 0.01 }, { type: "tick", dt: 1, timeline });
    expect(after).toMatchObject({ replayUntil: null, speed: NORMAL, follow: null, zoom: 1 });
  });

  it("zoom in and out stay between the whole floor and the closest view; fit returns to the whole floor", () => {
    let s = initialPlayback();
    for (let i = 0; i < 20; i++) s = playbackReducer(s, { type: "zoom", factor: 1.4 });
    expect(s.zoom).toBe(MAX_ZOOM);
    s = playbackReducer({ ...s, follow: "boss" }, { type: "fit" });
    expect(s).toMatchObject({ zoom: 1, follow: null, panX: 0, panY: 0 });
    expect(playbackReducer(s, { type: "zoom", factor: 0.5 }).zoom).toBe(1);
  });

  it("follow picks a character and zooms in to it; a drag lets go of it and stays on the floor", () => {
    const following = playbackReducer(initialPlayback(), { type: "follow", seat: "checker" });
    expect(following).toMatchObject({ follow: "checker", zoom: FOLLOW_ZOOM });

    const dragged = playbackReducer(following, { type: "pan", dx: 1e6, dy: -1e6, world });
    expect(dragged.follow).toBeNull();
    expect(dragged.panX).toBeCloseTo((world.w - world.w / FOLLOW_ZOOM) / 2);
    expect(dragged.panY).toBeCloseTo(-(world.h - world.h / FOLLOW_ZOOM) / 2);
  });

  it("seek moves time and stays on the timeline", () => {
    expect(playbackReducer(initialPlayback(), { type: "seek", t: -5, timeline }).t).toBe(0);
    expect(playbackReducer(initialPlayback(), { type: "seek", t: 1e6, timeline }).t).toBe(timeline.total);
  });
});

describe("cameraBox", () => {
  it("at zoom 1 is the whole floor; following centres the character", () => {
    expect(cameraBox(world, { zoom: 1, panX: 0, panY: 0 }, null)).toEqual(world);
    const box = cameraBox(world, { zoom: 2, panX: 0, panY: 0 }, { x: 100, y: 300 });
    expect(box.w).toBeCloseTo(world.w / 2);
    expect(box.x + box.w / 2).toBeCloseTo(100);
  });
});
