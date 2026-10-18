import { useState } from "react";
import { Link } from "react-router-dom";
import {
  getFactoryDrawing,
  type FactoryDrawingArrow,
  type FactoryDrawingLegend,
  type FactoryDrawingNode,
  type FactoryDrawingView,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { useView } from "./FactoryActivityTabs";

// What a factory does, drawn (owner, 18 October 2026): a small slice of BPMN, never executable. Each seat is a lane
// from how it starts, through the steps its definition lists, to how it ends; a decision is a diamond with its branch
// below. The newest run is painted on the start and the end, and a click on a shape opens what it does. Nothing here
// decides anything (rule 7): every position, word and tone comes finished from the Gateway, and this component only
// turns them into SVG shapes. With a seat, only that seat's lane is drawn (the seat's own page).

export function FactoryDrawingPanel({
  factory,
  seat = null,
  showStatus = false,
}: {
  factory: string;
  seat?: string | null;
  showStatus?: boolean;
}) {
  const view = useView<FactoryDrawingView>((sig) => getFactoryDrawing(factory, sig), factory, "load what this factory does");
  const [open, setOpen] = useState<FactoryDrawingNode | null>(null);
  const cut = view.data === null ? null : seat === null ? { view: view.data, top: 0 } : onlySeat(view.data, seat);
  const d = cut?.view ?? null;
  return (
    <section className="fa-panel fd-panel" data-testid="fd-panel">
      {view.error !== null ? (
        <p className="fa-dim">{view.error}</p>
      ) : d === null ? (
        <p className="fa-dim">Loading what this factory does...</p>
      ) : (
        <>
          {showStatus && d.status !== null && (
            <div className={`fd-status fd-tone-${d.status.tone}`} role="status" data-testid="fd-status">
              <span className="fd-status-dot" aria-hidden="true" />
              <div>
                <strong>{d.status.text}</strong>
                {d.status.sub !== null && <span className="fd-status-sub dt-private">{d.status.sub}</span>}
              </div>
            </div>
          )}
          <h2 className="fa-section-title">What it does</h2>
          <div className="ff-frame">
            <FactoryDrawing view={d} top={cut!.top} openId={open?.id ?? null} onOpen={(n) => setOpen(open?.id === n.id ? null : n)} />
          </div>
          <ul className="fd-legend" data-testid="fd-legend">
            {d.legend.map((l) => (
              <li key={`${l.kind}-${l.tone}-${l.text}`}>
                <LegendShape legend={l} />
                {l.text}
              </li>
            ))}
          </ul>
          {open !== null && open.panel !== null && (
            <div className="fd-step" data-testid="fd-step">
              <div className="fd-step-head">
                <strong>{open.panel.heading}</strong>
                <button type="button" className="ui-btn ui-btn-secondary" onClick={() => setOpen(null)}>
                  Close
                </button>
              </div>
              {open.panel.lines.map((line) => (
                <p key={line} className="dt-private">
                  {line}
                </p>
              ))}
              {open.panel.runs !== null && <code className="fd-runs dt-private">{open.panel.runs}</code>}
            </div>
          )}
          {d.notes.map((n) => (
            <p key={n} className="fa-dim ff-note">
              {n}
            </p>
          ))}
        </>
      )}
    </section>
  );
}

// One seat's lane, cut out of the whole drawing: its lane, shapes and arrows, and where the view starts.
function onlySeat(view: FactoryDrawingView, seat: string): { view: FactoryDrawingView; top: number } {
  const lane = view.lanes.find((l) => l.seatId === seat);
  if (lane === undefined) return { view: { ...view, status: null, lanes: [], nodes: [], arrows: [], height: 0 }, top: 0 };
  return {
    view: {
      ...view,
      status: null,
      lanes: [lane],
      nodes: view.nodes.filter((n) => n.seatId === seat),
      arrows: view.arrows.filter((a) => a.from.startsWith(`${seat}/`)),
      height: lane.height + 40,
    },
    top: lane.y - 20,
  };
}

export function FactoryDrawing({
  view,
  top = 0,
  openId,
  onOpen,
}: {
  view: FactoryDrawingView;
  top?: number;
  openId: string | null;
  onOpen: (node: FactoryDrawingNode) => void;
}) {
  return (
    <svg
      className="ff-svg fd-svg"
      viewBox={`0 ${top} ${view.width} ${view.height}`}
      style={{ minWidth: Math.min(view.width, 760) }}
      role="img"
      aria-label={`What ${view.title} does`}
      data-testid="fd-drawing"
    >
      {view.lanes.map((l, i) => (
        <g key={l.seatId} className={`ff-lane ff-hue-${i % 4}`} data-testid={`fd-lane-${l.seatId}`}>
          <rect x={l.x} y={l.y} width={l.width} height={l.height} rx={10} className="ff-lane-body" />
          <rect x={l.x} y={l.y} width={120} height={l.height} rx={10} className="ff-lane-label" />
          <Link to={l.href}>
            <text x={l.x + 12} y={l.y + 26} className="ff-lane-name">
              {l.title}
            </text>
          </Link>
          <text x={l.x + 12} y={l.y + 44} className="ff-bay-sub">
            {l.sub}
          </text>
        </g>
      ))}
      {view.arrows.map((a, i) => (
        <Arrow key={`${a.from}-${a.to}-${i}`} arrow={a} />
      ))}
      {view.nodes.map((n) => (
        <Node key={n.id} node={n} open={n.id === openId} onOpen={onOpen} />
      ))}
      {view.arrows.map((a, i) =>
        a.label === null ? null : (
          <text key={`label-${a.from}-${a.to}-${i}`} x={a.labelX} y={a.labelY} textAnchor="middle" className={`ff-arrow-label ff-tone-${a.tone}`}>
            {a.label}
          </text>
        ),
      )}
    </svg>
  );
}

function Arrow({ arrow }: { arrow: FactoryDrawingArrow }) {
  return (
    <g className={`ff-arrow ff-tone-${arrow.tone}${arrow.dashed ? " ff-line-dashed" : ""}`} data-testid={`fd-arrow-${arrow.from}-${arrow.to}`}>
      <path d={arrow.path} fill="none" />
      <polygon points={arrow.head} />
    </g>
  );
}

function Node({ node: n, open, onOpen }: { node: FactoryDrawingNode; open: boolean; onOpen: (node: FactoryDrawingNode) => void }) {
  const cx = n.x + n.width / 2;
  const cy = n.y + n.height / 2;
  const r = n.width / 2;
  const clickable = n.panel !== null;
  const cls = `fd-node fd-${n.kind} fd-tone-${n.tone}${n.dashed ? " fd-dashed" : ""}${open ? " fd-open" : ""}${clickable ? " fd-clickable" : ""}`;
  const shape = (() => {
    switch (n.kind) {
      case "task":
        return (
          <>
            <rect x={n.x} y={n.y} width={n.width} height={n.height} rx={9} className="fd-shape" />
            <text x={cx} y={n.sub === null ? cy + 4 : cy - 3} textAnchor="middle" className="fd-title">
              {n.title}
            </text>
            {n.sub !== null && (
              <text x={cx} y={cy + 13} textAnchor="middle" className="fd-sub">
                {n.sub}
              </text>
            )}
            {n.repeats && (
              <g className="fd-repeat" aria-label="repeats">
                <path d={`M ${cx - 5} ${n.y + n.height - 6} a 6 6 0 1 1 9 0`} fill="none" />
                <polygon points={`${cx + 4},${n.y + n.height - 6} ${cx + 7},${n.y + n.height - 11} ${cx + 9},${n.y + n.height - 5}`} />
              </g>
            )}
          </>
        );
      case "decision":
        return (
          <>
            <polygon points={`${cx},${n.y} ${n.x + n.width},${cy} ${cx},${n.y + n.height} ${n.x},${cy}`} className="fd-shape" />
            <text x={cx} y={cy + 4} textAnchor="middle" className="fd-title">
              {n.title}
            </text>
          </>
        );
      default:
        return (
          <>
            <circle cx={cx} cy={cy} r={r} className="fd-shape" />
            {n.kind === "start-clock" && (
              <g className="fd-glyph">
                <circle cx={cx} cy={cy} r={r - 9} fill="none" />
                <line x1={cx} y1={cy} x2={cx} y2={cy - 8} />
                <line x1={cx} y1={cy} x2={cx + 6} y2={cy + 3} />
              </g>
            )}
            {n.kind === "start-every" && (
              <g className="fd-glyph">
                <path d={`M ${cx - 7} ${cy + 4} a 8 8 0 1 1 3 4`} fill="none" />
              </g>
            )}
            {n.kind === "start-talk" && (
              <g className="fd-glyph">
                <rect x={cx - 9} y={cy - 7} width={18} height={12} rx={3} fill="none" />
                <path d={`M ${cx - 4} ${cy + 5} l -3 5 l 7 -5`} fill="none" />
              </g>
            )}
            {n.kind === "end-stop" && <rect x={cx - 7} y={cy - 7} width={14} height={14} className="fd-stop" />}
            <text x={cx} y={n.y + n.height + 15} textAnchor="middle" className="fd-title">
              {n.title}
            </text>
            {n.sub !== null && (
              <text x={cx} y={n.y + n.height + 29} textAnchor="middle" className="fd-sub fd-sub-tone">
                {n.sub}
              </text>
            )}
          </>
        );
    }
  })();
  return (
    <g
      className={cls}
      data-testid={`fd-node-${n.id}`}
      role={clickable ? "button" : undefined}
      tabIndex={clickable ? 0 : undefined}
      aria-label={clickable ? n.panel!.heading : undefined}
      onClick={clickable ? () => onOpen(n) : undefined}
      onKeyDown={
        clickable
          ? (e) => {
              if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                onOpen(n);
              }
            }
          : undefined
      }
    >
      {shape}
    </g>
  );
}

function LegendShape({ legend: l }: { legend: FactoryDrawingLegend }) {
  return (
    <svg width="22" height="16" aria-hidden="true" className={`fd-node fd-${l.kind} fd-tone-${l.tone}`}>
      {l.kind === "task" && <rect x="1" y="2" width="20" height="12" rx="3" className="fd-shape" />}
      {l.kind === "decision" && <polygon points="11,1 21,8 11,15 1,8" className="fd-shape" />}
      {l.kind === "tone" && <rect x="4" y="2" width="14" height="12" rx="3" className="fd-shape" />}
      {l.kind !== "task" && l.kind !== "decision" && l.kind !== "tone" && <circle cx="11" cy="8" r="6.5" className="fd-shape" />}
      {l.kind === "end-stop" && <rect x="8" y="5" width="6" height="6" className="fd-stop" />}
    </svg>
  );
}
