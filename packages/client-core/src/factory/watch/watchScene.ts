// The player's drawing maths, pure (Watch your factory, piece 5). Given the view and a playback time it says where
// everything stands and what each character is doing. It decides nothing about what a step MEANS - the Gateway did
// that when it named the step's kind - it only turns each kind into its one animation, taken from the mockup
// (D:/ReposFred/_briefs/team-factory-you-can-watch/erik-replay.html):
//
//   woke             the seat's lamp goes amber, the character jumps up surprised, then hammers away at its bench
//   delivered        the lamp goes green and the character carries a crate to the Boss (or lifts it at its own
//                    bench when it is the Boss, or there is none); confetti where it lands
//   talked           the lamp goes blue and the step's own words appear in a speech bubble
//   checked-nothing  a magnifier peeks at the station with the count of checks, the character peeks, shrugs, dozes
//   hiccup           when the step's alarm is set (an owner's hiccup): the lamp blinks red, wires spark out of the
//                    character and it goes dizzy, then worried. Without the alarm: a calm stumble and a shrug, no red
//   waiting          a calm lamp pulses, an hourglass turns and the character waits, looking about - never a failure
//
// And one drawing that is not a kind: a step the view marks as NOT the contributor's own (mine === false) carries no
// words and is drawn as a plain crate lifted at the seat's bench, whatever its kind - the Gateway decided it, and the
// player draws nothing more of it.
//
// Every number here is in world pixels of an isometric drawing: a floor tile is 64 by 32.

import type { FactoryWatchCharacter, FactoryWatchStation, FactoryWatchStep, FactoryWatchStepKind, FactoryWatchView } from "./factoryWatchTypes";

export const TILE_W = 64;
export const TILE_H = 32;

/** A tile position (and a height above the floor) to a world pixel. */
export function iso(gx: number, gy: number, lift = 0): [number, number] {
  return [((gx - gy) * TILE_W) / 2, ((gx + gy) * TILE_H) / 2 - lift];
}

export interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

const SIDE_PAD = 56;
const TOP_PAD = 130;
const BOTTOM_PAD = 56;

/** The whole floor with room around it for the boxes, labels and bubbles: the "fit" camera. */
export function worldBox(view: Pick<FactoryWatchView, "floorWidth" | "floorHeight">): Box {
  const left = iso(0, view.floorHeight)[0];
  const right = iso(view.floorWidth, 0)[0];
  const bottom = iso(view.floorWidth, view.floorHeight)[1];
  return { x: left - SIDE_PAD, y: -TOP_PAD, w: right - left + SIDE_PAD * 2, h: bottom + TOP_PAD + BOTTOM_PAD };
}

/** How tall a station's box is drawn, in pixels. */
export function stationLift(station: FactoryWatchStation): number {
  return station.isBoss ? 34 : 24;
}

// ------------------------------------------------------------------------------------------------- the timeline

/** A pause before the first step, and between steps. */
export const LEAD_IN = 0.6;
export const STEP_GAP = 0.5;

/** How long one step of each kind plays at normal speed, in seconds. EVERY KIND IS IN THIS ONE MAP (and in LAMP and
 * ANIMATIONS below); a kind the view sends that is not here fails loudly instead of drawing nothing. */
export const STEP_SECONDS: Record<FactoryWatchStepKind, (step: FactoryWatchStep) => number> = {
  woke: () => 4,
  delivered: () => 5,
  talked: (step) => Math.min(6.5, Math.max(2.8, 1.6 + step.words.length * 0.05)),
  "checked-nothing": (step) => 2.8 + Math.min(2.4, step.emptyChecks * 0.15),
  hiccup: () => 4.2,
  waiting: () => 3.6,
};

/** A step that is not the contributor's own: a plain crate, the same length whatever its kind. */
export const PLAIN_CRATE_SECONDS = 3;

/** Whether the view marks this step as someone else's work (contributor audience only; absent for the owner). */
export const isPlainCrate = (step: FactoryWatchStep) => step.mine === false;

function kindEntry<T>(map: Record<FactoryWatchStepKind, T>, step: FactoryWatchStep): T {
  if (!Object.prototype.hasOwnProperty.call(map, step.kind))
    throw new Error(`The watch view sent a step of kind '${step.kind}', which the player has no animation for. Add it to watchScene.ts.`);
  return map[step.kind];
}

/** How long one step plays at normal speed, in seconds. */
export function stepDuration(step: FactoryWatchStep): number {
  const seconds = kindEntry(STEP_SECONDS, step);
  return isPlainCrate(step) ? PLAIN_CRATE_SECONDS : seconds(step);
}

export interface TimedStep {
  step: FactoryWatchStep;
  index: number;
  start: number;
  end: number;
}

export interface Timeline {
  steps: TimedStep[];
  /** Where playback ends: the end of the last step plus one gap, so a step appended later starts exactly here. */
  total: number;
}

/** The steps, one after another. Pure; appending steps never moves the earlier ones, which is what makes live work. */
export function buildTimeline(view: Pick<FactoryWatchView, "steps">): Timeline {
  const steps: TimedStep[] = [];
  let t = LEAD_IN;
  view.steps.forEach((step, index) => {
    const end = t + stepDuration(step);
    steps.push({ step, index, start: t, end });
    t = end + STEP_GAP;
  });
  return { steps, total: steps.length === 0 ? LEAD_IN : t };
}

/** The step playing at time t, or null in a gap. */
export function activeStepAt(timeline: Timeline, t: number): TimedStep | null {
  for (const s of timeline.steps) if (t >= s.start && t < s.end) return s;
  return null;
}

/** The last step that has started by time t, or null before the first. */
export function lastStartedAt(timeline: Timeline, t: number): TimedStep | null {
  let found: TimedStep | null = null;
  for (const s of timeline.steps) if (s.start <= t) found = s;
  return found;
}

// ------------------------------------------------------------------------------------------------- the scene

export type Expression =
  | "idle"
  | "sleep"
  | "happy"
  | "surprised"
  | "focused"
  | "talking"
  | "dizzy"
  | "cool"
  | "worried"
  | "peek"
  | "shrug"
  | "waiting";

export const IDLE_LAMP = "#5b6480";
export const LAMP: Record<FactoryWatchStepKind, string> = {
  woke: "#ffb020",
  delivered: "#4cc38a",
  talked: "#5aa0ff",
  "checked-nothing": IDLE_LAMP,
  hiccup: "#ff4d4d",
  waiting: "#8d9cff",
};

/** What a character is drawn doing: the animation of a step kind, or the plain crate of someone else's step. */
export type Animation = FactoryWatchStepKind | "plain-crate";

export interface Pose {
  seat: string;
  name: string;
  colour: string;
  isBoss: boolean;
  /** World pixels, feet on the floor, after any jump. */
  x: number;
  y: number;
  expression: Expression;
  /** What this character is animating right now, or null. */
  animation: Animation | null;
  busy: boolean;
  wires: boolean;
  /** The crate it holds: the starred crate of a delivery, a plain one for someone else's step, or none. */
  carry: "crate" | "plain" | null;
  /** An hourglass over its head, while it waits. */
  hourglass: boolean;
  walking: boolean;
  bob: number;
  squash: number;
}

export interface Scene {
  t: number;
  active: TimedStep | null;
  lastStarted: TimedStep | null;
  poses: Pose[];
  lamps: Record<string, string>;
  /** A speech bubble; x and y are where its tail points. */
  bubble: { seat: string; words: string; x: number; y: number; pop: number } | null;
  magnifier: { station: string; x: number; y: number; count: number } | null;
  confetti: { x: number; y: number; age: number } | null;
  smoke: { x: number; y: number; age: number } | null;
  /** The time words of the step on screen (or the last one), from the view. */
  clock: string | null;
}

const ease = (k: number) => (k < 0.5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2);
const lerp = (a: number, b: number, k: number) => a + (b - a) * k;
const clamp01 = (k: number) => Math.max(0, Math.min(1, k));

function jumpOffset(sinceJump: number): number {
  return sinceJump >= 0 && sinceJump < 0.6 ? -Math.sin((sinceJump / 0.6) * Math.PI) * 16 : 0;
}

function bossOf(view: FactoryWatchView): FactoryWatchCharacter | null {
  const boss = view.stations.find((s) => s.isBoss);
  if (!boss) return null;
  return view.characters.find((c) => c.station === boss.id) ?? null;
}

function stationTop(station: FactoryWatchStation): [number, number] {
  return iso(station.gx + station.width / 2, station.gy + station.height / 2, stationLift(station));
}

/**
 * Everything on the floor at playback time t. Pure: the same view and t always draw the same picture, so scrubbing,
 * slow motion and instant replay are only different ways of choosing t.
 */
export function sceneAt(view: FactoryWatchView, timeline: Timeline, t: number): Scene {
  const stationsById = new Map(view.stations.map((s) => [s.id, s]));
  const lamps: Record<string, string> = {};
  for (const s of view.stations) lamps[s.id] = IDLE_LAMP;
  const nothingEver = view.steps.length === 0;

  const poses: Pose[] = view.characters.map((c) => {
    const [x, y] = iso(c.homeGx, c.homeGy);
    const station = stationsById.get(c.station);
    return {
      seat: c.seat,
      name: c.name,
      colour: c.colour,
      isBoss: station?.isBoss ?? false,
      x,
      y,
      expression: nothingEver ? "sleep" : "idle",
      animation: null,
      busy: false,
      wires: false,
      carry: null,
      hourglass: false,
      walking: false,
      bob: Math.sin(t * 2 + x * 0.01) * 1,
      squash: 1,
    };
  });

  const active = activeStepAt(timeline, t);
  const lastStarted = lastStartedAt(timeline, t);
  const scene: Scene = {
    t,
    active,
    lastStarted,
    poses,
    lamps,
    bubble: null,
    magnifier: null,
    confetti: null,
    smoke: null,
    clock: (active ?? lastStarted)?.step.time ?? null,
  };
  if (!active) return scene;

  const pose = poses.find((p) => p.seat === active.step.seat);
  const character = view.characters.find((c) => c.seat === active.step.seat);
  const station = character ? stationsById.get(character.station) : undefined;
  if (!pose || !character || !station) {
    throw new Error(`The watch view has a step for seat '${active.step.seat}' but no character or station for it.`);
  }
  const u = t - active.start;
  const length = active.end - active.start;
  const context = { view, scene, pose, character, station, step: active.step, u, length, t };
  if (isPlainCrate(active.step)) {
    pose.animation = "plain-crate";
    plainCrate(context);
    return scene;
  }
  pose.animation = active.step.kind;
  lamps[station.id] = kindEntry(LAMP, active.step);
  kindEntry(ANIMATIONS, active.step)(context);
  return scene;
}

interface AnimationContext {
  view: FactoryWatchView;
  scene: Scene;
  pose: Pose;
  character: FactoryWatchCharacter;
  station: FactoryWatchStation;
  step: FactoryWatchStep;
  /** Seconds since the step started, and its whole length. */
  u: number;
  length: number;
  t: number;
}

function jump(pose: Pose, since: number) {
  pose.y += jumpOffset(since);
  if (since >= 0 && since < 0.15) pose.squash = 0.82;
  else if (since >= 0.55 && since < 0.7) pose.squash = 0.86;
}

/** THE ONE ANIMATION OF EACH STEP KIND. */
export const ANIMATIONS: Record<FactoryWatchStepKind, (c: AnimationContext) => void> = {
  woke: ({ pose, u, t }) => {
    if (u < 1.2) {
      pose.expression = "surprised";
      jump(pose, u - 0.1);
    } else {
      pose.expression = "focused";
      pose.busy = true;
      pose.bob = Math.sin(t * 14) * 1.6;
    }
  },

  delivered: ({ view, scene, pose, character, u }) => {
    const boss = bossOf(view);
    const [hx, hy] = iso(character.homeGx, character.homeGy);
    const walks = boss !== null && boss.seat !== character.seat;
    const [tx, ty] = walks ? iso(boss.homeGx + 0.8, boss.homeGy + 0.2) : [hx, hy];
    pose.expression = "happy";
    if (u < 0.4) {
      jump(pose, u);
    } else if (u < 2.0) {
      const k = ease(clamp01((u - 0.4) / 1.6));
      pose.x = lerp(hx, tx, k);
      pose.y = lerp(hy, ty, k);
      pose.carry = "crate";
      pose.walking = walks;
      pose.bob = walks ? -Math.abs(Math.sin(u * 9)) * 4 : pose.bob;
    } else if (u < 2.9) {
      pose.x = tx;
      pose.y = ty;
      pose.expression = "cool";
      jump(pose, u - 2.0);
      scene.confetti = { x: tx, y: ty, age: u - 2.0 };
    } else {
      const k = ease(clamp01((u - 2.9) / 1.7));
      pose.x = lerp(tx, hx, k);
      pose.y = lerp(ty, hy, k);
      pose.walking = walks && k < 1;
      if (pose.walking) pose.bob = -Math.abs(Math.sin(u * 9)) * 4;
      if (u - 2.0 < 2.2) scene.confetti = { x: tx, y: ty, age: u - 2.0 };
    }
  },

  talked: ({ scene, pose, station, step, u, length }) => {
    pose.expression = "talking";
    const pop = Math.min(1, u / 0.18) * Math.min(1, (length - u) / 0.18);
    // The bubble's tail points at the speaker's head, and the bubble sits above the station's own label.
    const [, sy] = stationTop(station);
    scene.bubble = { seat: step.seat, words: step.words, x: pose.x, y: Math.min(pose.y - 58, sy - 44), pop: clamp01(pop) };
  },

  "checked-nothing": ({ scene, pose, station, step, u, length }) => {
    const k = u / length;
    pose.expression = k < 0.45 ? "peek" : k < 0.8 ? "shrug" : "sleep";
    const [sx, sy] = stationTop(station);
    if (k < 0.8) scene.magnifier = { station: station.id, x: sx, y: sy, count: step.emptyChecks };
  },

  hiccup: ({ scene, pose, station, step, u, t }) => {
    if (!step.alarm) {
      // The Gateway raised no alarm: a calm stumble - a small lurch that settles, then a shrug. No red, no wires.
      scene.lamps[station.id] = IDLE_LAMP;
      pose.expression = u < 1.2 ? "surprised" : "shrug";
      pose.x += Math.sin(u * 9) * 5 * Math.max(0, 1 - u / 1.2);
      return;
    }
    scene.lamps[station.id] = Math.sin(t * 14) > -0.2 ? LAMP.hiccup : IDLE_LAMP;
    if (u < 2.6) {
      pose.expression = "dizzy";
      pose.wires = true;
      pose.x += Math.sin(t * 60) * 2;
      const [sx, sy] = stationTop(station);
      scene.smoke = { x: sx, y: sy, age: u };
    } else {
      pose.expression = "worried";
    }
  },

  waiting: ({ scene, pose, station, u, t }) => {
    // Calm: the lamp breathes slowly, the character looks about and taps its foot under a turning hourglass.
    scene.lamps[station.id] = Math.sin(u * 2.4) > -0.3 ? LAMP.waiting : IDLE_LAMP;
    pose.expression = "waiting";
    pose.hourglass = true;
    pose.bob = Math.sin(t * 8) > 0 ? -1 : 0;
  },
};

/** Someone else's step, for a contributor: no words, no lamp, a plain crate lifted at the bench. */
function plainCrate({ pose, u }: AnimationContext) {
  pose.carry = u > 0.3 && u < 2.6 ? "plain" : null;
  pose.expression = "idle";
  jump(pose, u - 0.3);
}

/** Text of a bubble, broken into lines of at most `width` characters, never splitting a word. */
export function wrapWords(words: string, width: number): string[] {
  const out: string[] = [];
  let line = "";
  for (const word of words.split(" ")) {
    if (line && (line + " " + word).length > width) {
      out.push(line);
      line = word;
    } else {
      line = line ? line + " " + word : word;
    }
  }
  if (line) out.push(line);
  return out;
}
