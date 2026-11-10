// THE WATCH PLAYER (Watch your factory, version one, piece 5): the mockup erik-replay.html made into one shared
// component. The Cockpit and the phone app both mount it (piece 6); it takes the finished watch view as a prop and
// fetches nothing.
//
// Critical rule 7: the Gateway decided everything in the view - where each station stands, which character stands
// for which seat and in what colour, what kind each step is, and every word. This component only DRAWS it: stations,
// halls and characters at the given tiles, and each step played as the one animation of its kind (watchScene.ts).
// Every word on the floor and in the list is the view's own; the controls are the only words of the player's.
//
// Playback (watchPlayback.ts): live - new steps play as they arrive -, replay from the start, slow motion, instant
// replay of the last step, zoom (buttons, wheel, pinch), drag to move, and follow a character (tap it, or pick it).

import { useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from "react";
import type { FactoryWatchStation, FactoryWatchView } from "./factoryWatchTypes";
import {
  type Box,
  type Pose,
  type Scene,
  type Timeline,
  buildTimeline,
  iso,
  sceneAt,
  stationLift,
  worldBox,
  wrapWords,
} from "./watchScene";
import {
  FAST,
  NORMAL,
  SLOW_MOTION,
  type Speed,
  cameraBox,
  atLiveEdge,
  initialPlayback,
  playbackReducer,
} from "./watchPlayback";
import { Crate, WatchMonster } from "./WatchMonster";
import "./factoryWatch.css";

export interface FactoryWatchPlayerProps {
  /** The finished watch view from the Gateway. Pass a new view with more steps and, live, they play as they arrive. */
  view: FactoryWatchView;
  /** True to start the replay at once, as the replay button would, when the view offers one (piece 6: pressing the
   * explainer card's "Watch your factory" starts the factory playing). Read once, when the player first draws. */
  startPlaying?: boolean;
  /** False when the shell's own frame already shows the factory's name (the phone's app bar), so it is not shown twice.
   * Layout only: the name is still the view's, and still labels the floor for a screen reader. Defaults to true. */
  showTitle?: boolean;
}

/** The hall floors, picked by the hall's index from the Gateway. */
const HALL_FILLS = ["#3a4868", "#47395f", "#2f5450", "#574631", "#57384a", "#34536a"];
const STATION_FILL = "#4b5571";
const BOSS_FILL = "#59627e";
const MONSTER_SCALE = 1.35;

const SPEEDS: ReadonlyArray<{ speed: Speed; label: string }> = [
  { speed: SLOW_MOTION, label: "Slow motion" },
  { speed: NORMAL, label: "Normal" },
  { speed: FAST, label: "Fast" },
];

const pts = (a: Array<[number, number]>) => a.map((p) => `${p[0].toFixed(1)},${p[1].toFixed(1)}`).join(" ");
const boxText = (b: Box) => `${b.x.toFixed(1)} ${b.y.toFixed(1)} ${b.w.toFixed(1)} ${b.h.toFixed(1)}`;

export function FactoryWatchPlayer({ view, startPlaying = false, showTitle = true }: FactoryWatchPlayerProps) {
  const timeline = useMemo(() => buildTimeline(view), [view]);
  const world = useMemo(() => worldBox(view), [view]);
  const [pb, dispatch] = useReducer(playbackReducer, timeline, initialPlayback);
  const scene = sceneAt(view, timeline, pb.t);
  const followed = pb.follow ? (scene.poses.find((p) => p.seat === pb.follow) ?? null) : null;
  const target = cameraBox(world, pb, followed);

  const svgRef = useRef<SVGSVGElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const timelineRef = useRef<Timeline>(timeline);
  timelineRef.current = timeline;
  const targetRef = useRef<Box>(target);
  targetRef.current = target;
  const camRef = useRef<Box | null>(null);
  const worldRef = useRef(world);
  worldRef.current = world;
  const [stageWidth, setStageWidth] = useState(0);

  // The camera starts where it looks, so the first paint is already right.
  useLayoutEffect(() => {
    if (camRef.current === null && svgRef.current) {
      camRef.current = targetRef.current;
      svgRef.current.setAttribute("viewBox", boxText(camRef.current));
    }
  });

  // Started from the explainer card: play the window from the start, exactly as the replay button does.
  const started = useRef(false);
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    if (startPlaying && view.replayText !== null) dispatch({ type: "restart" });
  }, [startPlaying, view.replayText]);

  // One loop drives both clocks: playback time (through the reducer) and the camera easing toward its target.
  useEffect(() => {
    let raf = 0;
    let last = performance.now();
    const frame = (now: number) => {
      const dt = Math.min(0.1, Math.max(0, (now - last) / 1000));
      last = now;
      dispatch({ type: "tick", dt, timeline: timelineRef.current });
      const cam = camRef.current;
      const goal = targetRef.current;
      if (cam && svgRef.current) {
        const k = 0.18;
        const next = { x: cam.x + (goal.x - cam.x) * k, y: cam.y + (goal.y - cam.y) * k, w: cam.w + (goal.w - cam.w) * k, h: cam.h + (goal.h - cam.h) * k };
        camRef.current = next;
        svgRef.current.setAttribute("viewBox", boxText(next));
      }
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
    return () => cancelAnimationFrame(raf);
  }, []);

  // The stage's width decides only how big the labels are drawn, so they stay readable on a phone.
  useEffect(() => {
    const el = stageRef.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver((entries) => setStageWidth(entries[0]?.contentRect.width ?? 0));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Wheel zoom needs a listener that may cancel the page scroll, which React's own wheel handler cannot.
  useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const onWheel = (ev: WheelEvent) => {
      ev.preventDefault();
      dispatch({ type: "zoom", factor: ev.deltaY > 0 ? 1 / 1.12 : 1.12 });
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, []);

  // Drag to move, pinch to zoom. A press that does not move is a tap, and a tap on a character follows it.
  const pointers = useRef(new Map<number, { x: number; y: number }>());
  const drag = useRef<{ moved: boolean; pinch: number | null }>({ moved: false, pinch: null });
  const worldPerPixel = () => {
    const svg = svgRef.current;
    const cam = camRef.current ?? targetRef.current;
    if (!svg) return 1;
    const r = svg.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) return 1;
    return Math.max(cam.w / r.width, cam.h / r.height);
  };
  const onPointerDown = (ev: React.PointerEvent<HTMLDivElement>) => {
    if ((ev.target as Element).closest?.("button")) return;
    pointers.current.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
    drag.current = { moved: false, pinch: pinchDistance(pointers.current) };
  };
  const onPointerMove = (ev: React.PointerEvent<HTMLDivElement>) => {
    const prev = pointers.current.get(ev.pointerId);
    if (!prev) return;
    pointers.current.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
    if (pointers.current.size >= 2) {
      const d = pinchDistance(pointers.current);
      if (d && drag.current.pinch) dispatch({ type: "zoom", factor: d / drag.current.pinch });
      drag.current = { moved: true, pinch: d };
      return;
    }
    const dx = ev.clientX - prev.x;
    const dy = ev.clientY - prev.y;
    if (!drag.current.moved && Math.abs(dx) + Math.abs(dy) < 3) {
      pointers.current.set(ev.pointerId, prev);
      return;
    }
    drag.current.moved = true;
    const s = worldPerPixel();
    dispatch({ type: "pan", dx: -dx * s, dy: -dy * s, world: worldRef.current });
  };
  const onPointerUp = (ev: React.PointerEvent<HTMLDivElement>) => {
    pointers.current.delete(ev.pointerId);
    drag.current.pinch = pinchDistance(pointers.current);
  };
  const onCharacterClick = (seat: string) => {
    if (!drag.current.moved) dispatch({ type: "follow", seat });
  };

  const labelScale = stageWidth > 0 ? Math.max(1, Math.min(1.7, (world.w / stageWidth) * 0.7)) : 1;
  const compact = labelScale > 1.3;
  const slow = pb.speed === SLOW_MOTION;
  const nameOf = new Map(view.characters.map((c) => [c.seat, c.name]));
  const colourOf = new Map(view.characters.map((c) => [c.seat, c.colour]));
  const current = scene.active ?? scene.lastStarted;

  return (
    <div
      className="fwp"
      data-playing={pb.playing ? "true" : "false"}
      data-speed={pb.speed}
      data-live={pb.live ? "true" : "false"}
      data-zoom={pb.zoom.toFixed(2)}
      data-follow={pb.follow ?? ""}
      data-t={pb.t.toFixed(2)}
    >
      <header className="fwp-head">
        {showTitle && <h2 className="fwp-title">{view.title}</h2>}
        <span className="fwp-pill">{view.window.label}</span>
        {scene.clock !== null && (
          <span className="fwp-clock" data-testid="fwp-clock">
            {scene.clock}
          </span>
        )}
      </header>
      {view.truncatedText !== null && <p className="fwp-truncated">{view.truncatedText}</p>}

      <div className="fwp-body">
        <div
          ref={stageRef}
          className={`fwp-stage${slow ? " fwp-slow" : ""}`}
          style={{ aspectRatio: `${world.w.toFixed(0)} / ${world.h.toFixed(0)}` }}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
          onPointerLeave={onPointerUp}
        >
          <svg ref={svgRef} className="fwp-svg" role="img" aria-label={view.title}>
            <Floor view={view} />
            <g className="fwp-stations">
              {[...view.stations]
                .sort((a, b) => a.gx + a.gy - (b.gx + b.gy))
                .map((s) => (
                  <StationBox key={s.id} station={s} lamp={scene.lamps[s.id] ?? "#5b6480"} />
                ))}
            </g>
            <Effects scene={scene} />
            <g className="fwp-characters">
              {[...scene.poses]
                .sort((a, b) => a.y - b.y)
                .map((p) => (
                  <Character key={p.seat} pose={p} t={pb.t} followed={pb.follow === p.seat} onClick={() => onCharacterClick(p.seat)} />
                ))}
            </g>
            {/* The words are drawn after the characters, so no crate a character lifts - plain, starred mid-jump, or the
                Boss's own delivery - ever paints over a station's name. */}
            <g className="fwp-labels">
              {view.stations.map((s) => (
                <StationLabel key={s.id} station={s} scale={labelScale} compact={compact} />
              ))}
            </g>
            <Bubble scene={scene} world={world} scale={labelScale} />
          </svg>
          <div className="fwp-vignette" />
          <div className="fwp-bars fwp-bars-top" />
          <div className="fwp-bars fwp-bars-bottom" />
          {slow && (
            <div className="fwp-slowtag" data-chrome="true">
              Slow motion
            </div>
          )}
          {view.emptyText !== null && (
            <div className="fwp-empty" data-testid="fwp-empty">
              {view.emptyText}
            </div>
          )}
          {view.replayText !== null && atLiveEdge(pb, timeline) && (
            <div className="fwp-replay-last">
              <button type="button" onClick={() => dispatch({ type: "restart" })}>
                {view.replayText}
              </button>
            </div>
          )}
          <div className="fwp-zoom">
            <button type="button" aria-label="Zoom in" onClick={() => dispatch({ type: "zoom", factor: 1.4 })}>
              +
            </button>
            <button type="button" aria-label="Zoom out" onClick={() => dispatch({ type: "zoom", factor: 1 / 1.4 })}>
              -
            </button>
            <button type="button" aria-label="Whole floor" className="fwp-fit" onClick={() => dispatch({ type: "fit" })}>
              Fit
            </button>
          </div>
        </div>

        {current && (
          <p className="fwp-caption" data-kind={current.step.kind}>
            <span className="fwp-step-time">{current.step.time}</span>{" "}
            <b style={{ color: colourOf.get(current.step.seat) }}>{nameOf.get(current.step.seat)}</b>{" "}
            {current.step.words !== "" && <span>{current.step.words}</span>}
          </p>
        )}

        <footer className="fwp-controls" data-chrome="true">
          <div className="fwp-buttons">
            <button type="button" aria-pressed={pb.playing} className="fwp-primary" onClick={() => dispatch({ type: "toggle-play", timeline })}>
              {pb.playing ? "Pause" : "Play"}
            </button>
            <button type="button" onClick={() => dispatch({ type: "restart" })}>
              From the start
            </button>
            <button type="button" disabled={timeline.steps.length === 0} onClick={() => dispatch({ type: "instant-replay", timeline })}>
              Instant replay
            </button>
            <button
              type="button"
              aria-pressed={pb.live}
              onClick={() => dispatch(pb.live ? { type: "stop-live" } : { type: "go-live", timeline })}
            >
              Live
            </button>
            <div className="fwp-speeds" role="group" aria-label="Speed">
              {SPEEDS.map((s) => (
                <button key={s.speed} type="button" aria-pressed={pb.speed === s.speed} onClick={() => dispatch({ type: "speed", speed: s.speed })}>
                  {s.label}
                </button>
              ))}
            </div>
            <label className="fwp-follow">
              <span>Follow</span>
              <select value={pb.follow ?? ""} onChange={(e) => dispatch({ type: "follow", seat: e.target.value === "" ? null : e.target.value })}>
                <option value="">Nobody</option>
                {view.characters.map((c) => (
                  <option key={c.seat} value={c.seat}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="fwp-scrub">
            {timeline.steps.map((ts) => (
              <span
                key={ts.step.key}
                className="fwp-mark"
                style={{ left: `${(ts.start / timeline.total) * 100}%`, background: colourOf.get(ts.step.seat) }}
                aria-hidden="true"
              />
            ))}
            <input
              type="range"
              aria-label="Position"
              min={0}
              max={timeline.total}
              step={0.01}
              value={pb.t}
              onChange={(e) => dispatch({ type: "seek", t: Number(e.target.value), timeline })}
            />
          </div>
        </footer>

        <aside className="fwp-side">
          <ol className="fwp-steps" aria-label="Steps">
            {timeline.steps.map((ts) => {
              const now = scene.active?.index === ts.index;
              const past = pb.t >= ts.start;
              return (
                <li key={ts.step.key} className="fwp-step" data-state={now ? "now" : past ? "past" : "future"} data-kind={ts.step.kind}>
                  <button type="button" className="fwp-step-main" onClick={() => dispatch({ type: "seek", t: ts.start, timeline })}>
                    <span className="fwp-step-time">{ts.step.time}</span>
                    <span className="fwp-step-who">
                      <i style={{ background: colourOf.get(ts.step.seat) }} aria-hidden="true" />
                      {nameOf.get(ts.step.seat)}
                    </span>
                    {ts.step.words !== "" && <span className="fwp-step-words">{ts.step.words}</span>}
                  </button>
                  <button
                    type="button"
                    className="fwp-step-replay"
                    aria-label={`Instant replay of ${ts.step.time}`}
                    onClick={() => dispatch({ type: "instant-replay", timeline, index: ts.index })}
                  >
                    Replay
                  </button>
                </li>
              );
            })}
          </ol>
          {view.notes.length > 0 && (
            <ul className="fwp-notes">
              {view.notes.map((n) => (
                <li key={n}>{n}</li>
              ))}
            </ul>
          )}
        </aside>
      </div>

    </div>
  );
}

function pinchDistance(map: Map<number, { x: number; y: number }>): number | null {
  if (map.size < 2) return null;
  const [a, b] = [...map.values()];
  return Math.hypot(a.x - b.x, a.y - b.y) || null;
}

// ------------------------------------------------------------------------------------------------- drawing

function Floor({ view }: { view: FactoryWatchView }) {
  const fw = view.floorWidth;
  const fh = view.floorHeight;
  const grid: JSX.Element[] = [];
  for (let i = 1; i < fw; i++) {
    const a = iso(i, 0);
    const b = iso(i, fh);
    grid.push(<line key={`x${i}`} x1={a[0]} y1={a[1]} x2={b[0]} y2={b[1]} stroke="#3f4862" />);
  }
  for (let j = 1; j < fh; j++) {
    const a = iso(0, j);
    const b = iso(fw, j);
    grid.push(<line key={`y${j}`} x1={a[0]} y1={a[1]} x2={b[0]} y2={b[1]} stroke="#3f4862" />);
  }
  return (
    <g className="fwp-floor">
      <rect x={-20000} y={-20000} width={40000} height={40000} fill="#1f2433" />
      <polygon points={pts([iso(0, 0), iso(fw, 0), iso(fw, fh), iso(0, fh)])} fill="#353d52" />
      {grid}
      {view.lines.map((l) => {
        const fill = HALL_FILLS[l.index % HALL_FILLS.length];
        // The name runs along the hall's far right edge, just outside it and parallel to it. Every station of a hall
        // stands at its left, and a label floats up toward the hall behind, so the far end is the one edge that no
        // station, character or label of this hall or the next one reaches.
        const [nx, ny] = iso(l.gx + l.width + 0.45, l.gy + l.height / 2);
        return (
          <g key={l.name} data-hall={l.name} data-gx={l.gx} data-gy={l.gy}>
            <polygon
              points={pts([iso(l.gx, l.gy), iso(l.gx + l.width, l.gy), iso(l.gx + l.width, l.gy + l.height), iso(l.gx, l.gy + l.height)])}
              fill={fill}
              stroke="#6b7697"
              strokeWidth={1.5}
              opacity={0.9}
            />
            <text x={nx} y={ny} fill="#d7dcea" fontSize={13} fontWeight={800} textAnchor="middle" transform={`rotate(-26.6 ${nx} ${ny})`} className="fwp-hall-name">
              {l.name}
            </text>
          </g>
        );
      })}
    </g>
  );
}

function StationBox({ station, lamp }: { station: FactoryWatchStation; lamp: string }) {
  const { gx, gy, width: sw, height: sh } = station;
  const h = stationLift(station);
  const c = station.isBoss ? BOSS_FILL : STATION_FILL;
  const top: Array<[number, number]> = [iso(gx, gy, h), iso(gx + sw, gy, h), iso(gx + sw, gy + sh, h), iso(gx, gy + sh, h)];
  const left: Array<[number, number]> = [iso(gx, gy + sh, h), iso(gx + sw, gy + sh, h), iso(gx + sw, gy + sh), iso(gx, gy + sh)];
  const right: Array<[number, number]> = [iso(gx + sw, gy, h), iso(gx + sw, gy + sh, h), iso(gx + sw, gy + sh), iso(gx + sw, gy)];
  const [cx, cy] = iso(gx + sw / 2, gy + sh / 2, h);
  return (
    <g data-station={station.id} data-gx={gx} data-gy={gy} data-width={sw} data-height={sh}>
      <polygon points={pts(top)} fill={c} />
      <polygon points={pts(left)} fill={c} />
      <polygon points={pts(left)} fill="#000" opacity={0.25} />
      <polygon points={pts(right)} fill={c} />
      <polygon points={pts(right)} fill="#000" opacity={0.45} />
      <circle cx={cx} cy={cy - 4} r={5.5} fill={lamp} data-lamp={station.id} />
    </g>
  );
}

function StationLabel({ station, scale, compact }: { station: FactoryWatchStation; scale: number; compact: boolean }) {
  // The words float above the box, clear of the character, which stands at the box's front edge.
  const [tx, ty] = iso(station.gx + station.width / 2, station.gy + station.height / 2, stationLift(station));
  const size = 12 * scale;
  const showSub = !compact && station.sub !== "";
  return (
    <g className="fwp-label" data-label-for={station.id}>
      <text x={tx} y={ty - (showSub ? 27 : 14)} fontSize={size} fontWeight={800} textAnchor="middle" fill="#eef1f7" className="fwp-outline">
        {station.label}
      </text>
      {showSub && (
        <text x={tx} y={ty - 14} fontSize={10.5} textAnchor="middle" fill="#b4bcd0" className="fwp-outline">
          {station.sub}
        </text>
      )}
    </g>
  );
}

function Character({ pose, t, followed, onClick }: { pose: Pose; t: number; followed: boolean; onClick: () => void }) {
  return (
    <g
      className="fwp-character"
      data-seat={pose.seat}
      data-animation={pose.animation ?? ""}
      data-expression={pose.expression}
      data-x={pose.x.toFixed(1)}
      data-y={pose.y.toFixed(1)}
      transform={`translate(${pose.x.toFixed(1)},${pose.y.toFixed(1)}) scale(${MONSTER_SCALE})`}
      onClick={onClick}
      role="button"
      aria-label={`Follow ${pose.name}`}
    >
      {followed && <ellipse cx={0} cy={1} rx={17} ry={6} fill="none" stroke="#ffd166" strokeWidth={1.6} />}
      <rect x={-16} y={-40} width={32} height={44} fill="transparent" />
      <WatchMonster
        colour={pose.colour}
        expression={pose.expression}
        t={t}
        crown={pose.isBoss}
        busy={pose.busy}
        wires={pose.wires}
        carry={pose.carry}
        hourglass={pose.hourglass}
        bob={pose.bob}
        squash={pose.squash}
      />
    </g>
  );
}

function Effects({ scene }: { scene: Scene }) {
  const parts: JSX.Element[] = [];
  if (scene.magnifier) {
    const m = scene.magnifier;
    const on = (scene.t * 1.6) % 2 < 1.4;
    parts.push(
      <g key="magnifier" data-effect="magnifier" transform={`translate(${m.x.toFixed(1)},${(m.y - 30).toFixed(1)})`} opacity={on ? 1 : 0.5}>
        <circle r={8} fill="#1f2433" stroke="#ffd166" strokeWidth={2.6} />
        <line x1={6} y1={6} x2={12} y2={12} stroke="#ffd166" strokeWidth={3.2} strokeLinecap="round" />
        <rect x={13} y={-20} width={10 + String(m.count).length * 7} height={17} rx={8.5} fill="#ffd166" />
        <text x={18 + String(m.count).length * 3.5} y={-7.5} fontSize={12} fontWeight={800} textAnchor="middle" fill="#1b1d26">
          {String(m.count)}
        </text>
      </g>,
    );
  }
  if (scene.smoke) {
    const s = scene.smoke;
    parts.push(
      <g key="smoke" data-effect="smoke">
        {[0, 0.7, 1.4].map((o) => {
          const k = ((s.age + o) % 2.1) / 2.1;
          return <circle key={o} cx={s.x + Math.sin(k * 6 + o) * 6} cy={s.y - 10 - k * 40} r={5 + k * 8} fill="#9aa3b8" opacity={(1 - k) * 0.6} />;
        })}
      </g>,
    );
  }
  if (scene.confetti) {
    const c = scene.confetti;
    const k = c.age;
    const bits: JSX.Element[] = [];
    for (let i = 0; i < 22; i++) {
      const a = i * 2.39;
      const sp = 40 + (i % 5) * 14;
      const x = c.x + Math.cos(a) * sp * k;
      const y = c.y - 40 - Math.sin(a) * sp * k * 0.6 - 70 * k + 60 * k * k;
      bits.push(
        <rect
          key={i}
          x={x.toFixed(1)}
          y={y.toFixed(1)}
          width={5}
          height={3}
          fill={["#ffd166", "#ff7a9a", "#5aa0ff", "#4cc38a", "#a57bf0"][i % 5]}
          transform={`rotate(${((k * 500 + i * 40) % 360).toFixed(0)} ${x.toFixed(1)} ${y.toFixed(1)})`}
          opacity={Math.max(0, 1 - k / 2.2).toFixed(2)}
        />,
      );
    }
    parts.push(
      <g key="confetti" data-effect="confetti">
        <g transform={`translate(${(c.x - 9).toFixed(1)},${(c.y - 4).toFixed(1)})`}>
          <Crate />
        </g>
        {bits}
      </g>,
    );
  }
  return <g className="fwp-effects">{parts}</g>;
}

function Bubble({ scene, world, scale }: { scene: Scene; world: Box; scale: number }) {
  const b = scene.bubble;
  if (!b) return null;
  const lines = wrapWords(b.words, 30);
  const longest = Math.max(...lines.map((l) => l.length));
  const w = Math.min(250, longest * 6.9 + 22);
  const h = 17 * lines.length + 12;
  // Laid out at scale 1, then scaled about its tail; kept inside the floor's box at its scaled size.
  const sw = w * scale;
  const left = Math.max(world.x + 6, Math.min(world.x + world.w - sw - 6, b.x - 18 * scale));
  const tail = Math.max(10, Math.min(w - 20, (b.x - left) / scale - 4));
  const s = (b.pop * scale).toFixed(3);
  return (
    <g
      className="fwp-bubble"
      data-effect="bubble"
      data-seat={b.seat}
      transform={`translate(${(left + tail * scale).toFixed(1)},${b.y.toFixed(1)}) scale(${s}) translate(${-tail},${-(h + 8)})`}
    >
      <rect width={w} height={h} rx={9} fill="#fff" />
      <path d={`M${tail} ${h - 1} l3 9 9 -9z`} fill="#fff" />
      {lines.map((l, i) => (
        <text key={i} x={11} y={20 + i * 17} fontSize={12.5} fontWeight={600} fill="#1b1d26">
          {l}
        </text>
      ))}
    </g>
  );
}
