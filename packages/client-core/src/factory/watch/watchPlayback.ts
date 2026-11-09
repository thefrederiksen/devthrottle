// The player's playback and camera state, as one pure reducer (Watch your factory, piece 5): play, pause, replay from
// the start, slow motion, instant replay of the last step, live, zoom, pan and follow. The component only dispatches;
// every rule of how the controls behave is here, where a test can drive it without a browser.

import { type Box, type Timeline, lastStartedAt } from "./watchScene";

export type Speed = 0.25 | 1 | 3;
export const SLOW_MOTION: Speed = 0.25;
export const NORMAL: Speed = 1;
export const FAST: Speed = 3;

export const MIN_ZOOM = 1;
export const MAX_ZOOM = 6;
/** How close the camera goes when it starts following a character. */
export const FOLLOW_ZOOM = 2.4;

export interface PlaybackState {
  /** Playback time in seconds along the timeline. */
  t: number;
  playing: boolean;
  speed: Speed;
  /** Live: at the end, wait for new steps and play them as they arrive, instead of stopping. */
  live: boolean;
  /** While an instant replay runs: when it ends. */
  replayUntil: number | null;
  zoom: number;
  /** The seat the camera follows, or null. */
  follow: string | null;
  /** The camera's offset from the floor's centre, in world pixels, when it follows nobody. */
  panX: number;
  panY: number;
}

export function initialPlayback(): PlaybackState {
  return { t: 0, playing: true, speed: NORMAL, live: true, replayUntil: null, zoom: MIN_ZOOM, follow: null, panX: 0, panY: 0 };
}

export type PlaybackAction =
  | { type: "tick"; dt: number; timeline: Timeline }
  | { type: "toggle-play"; timeline: Timeline }
  | { type: "restart" }
  | { type: "speed"; speed: Speed }
  | { type: "instant-replay"; timeline: Timeline; index?: number }
  | { type: "seek"; t: number; timeline: Timeline }
  | { type: "go-live"; timeline: Timeline }
  | { type: "stop-live" }
  | { type: "zoom"; factor: number }
  | { type: "fit" }
  | { type: "follow"; seat: string | null }
  | { type: "pan"; dx: number; dy: number; world: Box };

const clampZoom = (z: number) => Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, z));

function endReplay(s: PlaybackState): PlaybackState {
  return { ...s, replayUntil: null, speed: NORMAL, follow: null, zoom: MIN_ZOOM, panX: 0, panY: 0 };
}

export function playbackReducer(s: PlaybackState, a: PlaybackAction): PlaybackState {
  switch (a.type) {
    case "tick": {
      if (!s.playing) return s;
      let next: PlaybackState = { ...s, t: s.t + a.dt * s.speed };
      if (next.replayUntil !== null && next.t >= next.replayUntil) next = endReplay(next);
      if (next.t >= a.timeline.total) next = { ...next, t: a.timeline.total, playing: next.live };
      return next;
    }
    case "toggle-play":
      if (s.playing) return { ...s, playing: false };
      return { ...s, playing: true, t: s.t >= a.timeline.total && !s.live ? 0 : s.t };
    case "restart":
      return { ...s, t: 0, playing: true, speed: NORMAL, replayUntil: null, follow: null, zoom: MIN_ZOOM, panX: 0, panY: 0 };
    case "speed":
      return { ...s, speed: a.speed, replayUntil: null };
    case "instant-replay": {
      const target = a.index !== undefined ? a.timeline.steps[a.index] : lastStartedAt(a.timeline, s.t) ?? a.timeline.steps[0];
      if (!target) return s;
      return {
        ...s,
        t: target.start,
        playing: true,
        speed: SLOW_MOTION,
        replayUntil: target.end,
        follow: target.step.seat,
        zoom: Math.max(s.zoom, FOLLOW_ZOOM),
      };
    }
    case "seek":
      return { ...s, t: Math.max(0, Math.min(a.timeline.total, a.t)), replayUntil: null };
    case "go-live": {
      const last = a.timeline.steps[a.timeline.steps.length - 1];
      return { ...s, live: true, playing: true, speed: NORMAL, replayUntil: null, t: last ? last.start : a.timeline.total };
    }
    case "stop-live":
      return { ...s, live: false };
    case "zoom": {
      const zoom = clampZoom(s.zoom * a.factor);
      return zoom === MIN_ZOOM ? { ...s, zoom, panX: 0, panY: 0 } : { ...s, zoom };
    }
    case "fit":
      return { ...s, zoom: MIN_ZOOM, follow: null, panX: 0, panY: 0 };
    case "follow":
      return a.seat === null ? { ...s, follow: null } : { ...s, follow: a.seat, zoom: Math.max(s.zoom, FOLLOW_ZOOM) };
    case "pan": {
      // A drag takes the camera off whoever it followed, and never past the edge of the floor.
      const limitX = (a.world.w - a.world.w / s.zoom) / 2;
      const limitY = (a.world.h - a.world.h / s.zoom) / 2;
      return {
        ...s,
        follow: null,
        panX: Math.max(-limitX, Math.min(limitX, s.panX + a.dx)),
        panY: Math.max(-limitY, Math.min(limitY, s.panY + a.dy)),
      };
    }
  }
}

/**
 * Where the camera looks: the floor's box shrunk by the zoom, centred on the followed character (its head, so a
 * bubble shows) or on the floor's centre plus the pan.
 */
export function cameraBox(world: Box, s: Pick<PlaybackState, "zoom" | "panX" | "panY">, followed: { x: number; y: number } | null): Box {
  const w = world.w / s.zoom;
  const h = world.h / s.zoom;
  const cx = followed ? followed.x : world.x + world.w / 2 + s.panX;
  const cy = followed ? followed.y - 50 : world.y + world.h / 2 + s.panY;
  return { x: cx - w / 2, y: cy - h / 2, w, h };
}
