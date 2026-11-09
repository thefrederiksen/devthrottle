// One character of the watch player, drawn as the mockup's little monster (erik-replay.html). Pure drawing: its
// colour, its hat and what it is doing all come in as props. It draws no words - every glyph the mockup wrote as text
// ("!", "z") is a shape here, so nothing appears on the floor that the view did not send.

import type { Expression } from "./watchScene";

const INK = "#1b1d26";
const GOLD = "#ffd166";

export interface WatchMonsterProps {
  colour: string;
  expression: Expression;
  /** Playback time, for the looping bits (blinks, hammer swings, sparks). */
  t: number;
  crown: boolean;
  busy?: boolean;
  wires?: boolean;
  carry?: boolean;
  bob?: number;
  squash?: number;
}

function Eyes({ white, dx = 0, dy = 0 }: { white: number; dx?: number; dy?: number }) {
  return (
    <>
      <circle cx={-4.5} cy={-19} r={white} fill="#fff" />
      <circle cx={4.5} cy={-19} r={white} fill="#fff" />
      <circle cx={-4 + dx} cy={-18.5 + dy} r={2} fill={INK} />
      <circle cx={5 + dx} cy={-18.5 + dy} r={2} fill={INK} />
    </>
  );
}

const shut = <path d="M-8 -19 q3.5 3 7 0 M1 -19 q3.5 3 7 0" stroke={INK} strokeWidth={1.6} fill="none" />;
const happyEyes = <path d="M-8 -18 q3.5 -4 7 0 M1 -18 q3.5 -4 7 0" stroke={INK} strokeWidth={1.8} fill="none" />;

function Face({ expression, t }: { expression: Expression; t: number }) {
  switch (expression) {
    case "sleep":
      return (
        <>
          {shut}
          <ellipse cx={0} cy={-10} rx={2} ry={1.5 + Math.sin(t * 2) * 0.8} fill={INK} />
        </>
      );
    case "happy":
      return (
        <>
          {happyEyes}
          <path d="M-5 -11 q5 6 10 0 z" fill={INK} />
        </>
      );
    case "surprised":
      return (
        <>
          <Eyes white={5.5} dy={-0.5} />
          <ellipse cx={0} cy={-8.5} rx={3} ry={4} fill={INK} />
        </>
      );
    case "focused":
      return (
        <>
          <Eyes white={4.2} dx={1.5} dy={1} />
          <path d="M-3 -10 h6" stroke={INK} strokeWidth={1.6} />
          <path d="M3 -10 q3 0 2.5 3.5 q-2.5 0 -2.5 -3.5" fill="#ff7a9a" />
        </>
      );
    case "talking":
      return (
        <>
          <Eyes white={4.2} />
          <ellipse cx={0} cy={-10} rx={3.2} ry={0.8 + Math.abs(Math.sin(t * 11)) * 2.6} fill={INK} />
        </>
      );
    case "dizzy":
      return (
        <>
          <path d="M-8 -22 l6 6 M-2 -22 l-6 6 M2 -22 l6 6 M8 -22 l-6 6" stroke={INK} strokeWidth={1.8} />
          <path d="M-4 -9 q4 -4 8 0" stroke={INK} strokeWidth={1.6} fill="none" />
        </>
      );
    case "cool":
      return (
        <>
          <rect x={-10} y={-23} width={9} height={6} rx={2} fill="#111" />
          <rect x={1} y={-23} width={9} height={6} rx={2} fill="#111" />
          <path d="M-1 -21 h2" stroke="#111" strokeWidth={2} />
          <path d="M-4 -11 q4 4 8 0" stroke={INK} strokeWidth={1.7} fill="none" />
        </>
      );
    case "worried":
      return (
        <>
          <Eyes white={4.2} dy={0.5} />
          <path d="M-9 -25 l6 2 M9 -25 l-6 2" stroke={INK} strokeWidth={1.5} />
          <path d="M-4 -9 q4 -3 8 0" stroke={INK} strokeWidth={1.6} fill="none" />
        </>
      );
    case "peek":
      return (
        <>
          <Eyes white={4.2} dx={-1.5} dy={-1.5} />
          <path d="M-2 -10 h4" stroke={INK} strokeWidth={1.5} />
        </>
      );
    case "shrug":
      return (
        <>
          <Eyes white={4.2} />
          <path d="M-4 -10 q2 1 4 0 q2 -1 4 0" stroke={INK} strokeWidth={1.5} fill="none" />
        </>
      );
    case "idle": {
      const blink = Math.floor(t * 1.3) % 7 === 0 && (t * 1.3) % 1 < 0.15;
      return (
        <>
          {blink ? shut : <Eyes white={4.2} />}
          <path d="M-3 -11 q3 2.5 6 0" stroke={INK} strokeWidth={1.4} fill="none" />
        </>
      );
    }
  }
}

/** The bits that float around the head: an exclamation, sleep marks, dizzy stars, a sweat drop, shrugging arms. */
function Extra({ expression, t, colour }: { expression: Expression; t: number; colour: string }) {
  switch (expression) {
    case "surprised":
      return (
        <g data-glyph="exclamation">
          <rect x={10} y={-50} width={4} height={11} rx={1.8} fill={GOLD} />
          <circle cx={12} cy={-35} r={2.2} fill={GOLD} />
        </g>
      );
    case "sleep":
      return (
        <g data-glyph="sleep">
          {[0, 1.5].map((o) => {
            const p = ((t + o) % 3) / 3;
            return (
              <path
                key={o}
                d={`M${10 + p * 12} ${-36 - p * 24} h6 l-6 7 h6`}
                stroke="#c9d1e4"
                strokeWidth={1.8}
                fill="none"
                opacity={1 - p}
              />
            );
          })}
        </g>
      );
    case "dizzy":
      return (
        <g data-glyph="stars">
          {[0, 1, 2].map((i) => {
            const a = t * 5 + i * 2.1;
            return <circle key={i} cx={Math.cos(a) * 13} cy={-36 + Math.sin(a) * 4} r={2.4} fill={GOLD} />;
          })}
        </g>
      );
    case "worried":
      return <path d="M-14 -24 q2 4 0 6 q-2 -2 0 -6" fill="#8fd3ff" />;
    case "shrug":
      return <path d="M-11 -12 l-8 -9 M11 -12 l8 -9" stroke={colour} strokeWidth={3} strokeLinecap="round" fill="none" />;
    default:
      return null;
  }
}

function Tool({ busy, wires, t }: { busy?: boolean; wires?: boolean; t: number }) {
  if (wires) {
    const w = Math.sin(t * 11) * 10;
    return (
      <g data-glyph="wires">
        <path d={`M12 -14 q8 ${-10 + w} 14 -2 q6 8 12 ${-4 - w}`} stroke="#ff5a5a" strokeWidth={2.4} fill="none" />
        <path d={`M12 -12 q9 ${6 - w} 16 0 q5 -5 10 ${3 + w}`} stroke="#4fb0ff" strokeWidth={2.4} fill="none" />
      </g>
    );
  }
  if (busy) {
    const a = Math.sin(t * 14) * 22;
    const g = (t * 160) % 360;
    return (
      <g data-glyph="hammer">
        <g transform={`rotate(${a.toFixed(1)} 10 -12)`}>
          <line x1={10} y1={-12} x2={19} y2={-22} stroke="#cfd5e2" strokeWidth={2.4} />
          <rect x={15} y={-27} width={9} height={5} rx={1} fill="#8792a8" transform="rotate(-45 19.5 -24.5)" />
        </g>
        <g transform={`translate(-22,-30) scale(0.8) rotate(${g.toFixed(0)})`}>
          <path
            d="M-2,-9 h4 l1,3 3,-2 3,3 -2,3 3,1 v4 l-3,1 2,3 -3,3 -3,-2 -1,3 h-4 l-1,-3 -3,2 -3,-3 2,-3 -3,-1 v-4 l3,-1 -2,-3 3,-3 3,2z"
            fill={GOLD}
          />
          <circle r={3} fill="#2a3042" />
        </g>
        {Math.sin(t * 14) > 0.7 && (
          <>
            <circle cx={22} cy={-8} r={2} fill={GOLD} />
            <circle cx={26} cy={-12} r={1.5} fill={GOLD} />
          </>
        )}
      </g>
    );
  }
  return null;
}

/** A crate, the mockup's delivery: an isometric box with a star on it. */
export function Crate() {
  return (
    <g data-glyph="crate">
      <polygon points="0,-6 9,-11 18,-6 9,-1" fill="#ffb35c" />
      <polygon points="0,-6 9,-1 9,8 0,3" fill="#e8792b" />
      <polygon points="9,-1 18,-6 18,3 9,8" fill="#b85a17" />
      <path d="M9 -9 l1.4 2.6 2.8 .3 -2.1 1.9 .6 2.8 -2.7 -1.4 -2.7 1.4 .6 -2.8 -2.1 -1.9 2.8 -.3z" fill="#fff" />
    </g>
  );
}

export function WatchMonster({ colour, expression, t, crown, busy, wires, carry, bob = 0, squash = 1 }: WatchMonsterProps) {
  return (
    <>
      <ellipse cx={0} cy={1} rx={12} ry={4} fill="#000" opacity={0.3} />
      <g transform={`translate(0,${bob.toFixed(2)}) scale(${(1 / squash).toFixed(3)},${squash.toFixed(3)})`}>
        <path d="M-12 0 Q-15 -27 0 -29 Q15 -27 12 0 Q8 -4 4 0 Q0 -4 -4 0 Q-8 -4 -12 0Z" fill={colour} />
        {crown ? (
          <path d="M-7 -30 l2 -7 3 4 2 -6 2 6 3 -4 2 7z" fill={GOLD} stroke="#b8860b" strokeWidth={0.6} />
        ) : (
          <path d="M-8 -26 L-11 -34 -4 -28Z M8 -26 L11 -34 4 -28Z" fill={colour} />
        )}
        <Face expression={expression} t={t} />
        {carry && (
          <>
            <line x1={-10} y1={-14} x2={-6} y2={-29} stroke={colour} strokeWidth={3} strokeLinecap="round" />
            <line x1={10} y1={-14} x2={6} y2={-29} stroke={colour} strokeWidth={3} strokeLinecap="round" />
            <g transform="translate(-9,-38)">
              <Crate />
            </g>
          </>
        )}
        <Tool busy={busy} wires={wires} t={t} />
      </g>
      <Extra expression={expression} t={t} colour={colour} />
    </>
  );
}
