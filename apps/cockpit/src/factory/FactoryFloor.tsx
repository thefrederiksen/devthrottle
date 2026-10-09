import { Link } from "react-router-dom";
import {
  getFactoryFloor,
  type FactoryFloorArrow,
  type FactoryFloorBay,
  type FactoryFloorView,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { useView } from "./FactoryActivityTabs";

// The factory floor (owner decision, 8 October 2026): the factory drawn flat, as production lines. The Boss's office
// runs across the top, each line is a lane of bays with their lights, the arrows are the ones the factory published,
// and the owner's desk is the far end. Nothing here decides anything (rule 7): every position, word and tone comes
// finished from the Gateway, and this component only turns them into SVG shapes.

export function FactoryFloorPanel({ factory, full = false }: { factory: string; full?: boolean }) {
  const view = useView<FactoryFloorView>((sig) => getFactoryFloor(factory, sig), factory, "load this factory's floor");
  const d = view.data;
  return (
    <section className={`fa-panel ff-panel${full ? " ff-full" : ""}`} data-testid="ff-panel">
      <h2 className="fa-section-title">Floor</h2>
      {view.error !== null ? (
        <p className="fa-dim">{view.error}</p>
      ) : d === null ? (
        <p className="fa-dim">Loading the floor...</p>
      ) : (
        <>
          <div className="ff-frame">
            <FactoryFloorDrawing view={d} />
          </div>
          {d.legend.length > 0 && (
            <ul className="ff-legend" data-testid="ff-legend">
              {d.legend.map((l) => (
                <li key={`${l.tone}-${l.line}-${l.text}`}>
                  <svg width="28" height="8" aria-hidden="true">
                    <line x1="0" y1="4" x2="28" y2="4" className={`ff-arrow ff-tone-${l.tone} ff-line-${l.line}`} />
                  </svg>
                  {l.text}
                </li>
              ))}
            </ul>
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

// Where the desk's items start and how far apart they are: the Gateway sized the desk for these two numbers.
const DESK_ITEMS_TOP = 58;
const DESK_ITEM_STEP = 50;

export function FactoryFloorDrawing({ view }: { view: FactoryFloorView }) {
  const { office, desk } = view;
  return (
    <svg
      className="ff-svg"
      viewBox={`0 0 ${view.width} ${view.height}`}
      style={{ minWidth: Math.min(view.width, 760) }}
      role="img"
      aria-label={`The floor of ${view.title}`}
      data-testid="ff-floor"
    >
      {view.lanes.map((l) => (
        <g key={l.name} className={`ff-lane ff-hue-${l.hue}`} data-testid="ff-lane">
          <rect x={l.x} y={l.y} width={l.width} height={l.height} rx={10} className="ff-lane-body" />
          <rect x={l.x} y={l.y} width={128} height={l.height} rx={10} className="ff-lane-label" />
          <text x={l.x + 12} y={l.y + 24} className="ff-lane-name">
            {l.name}
          </text>
        </g>
      ))}
      {office !== null && (
        <g data-testid="ff-office">
          <rect x={office.x} y={office.y} width={office.width} height={office.height} rx={10} className="ff-office" />
          <circle cx={office.x + 18} cy={office.y + 20} r={6} className={`ff-light ff-tone-${office.tone}`} />
          <text x={office.x + 32} y={office.y + 25} className="ff-office-title">
            {office.title}
          </text>
          <text x={office.x + 18} y={office.y + 45} className="ff-office-sub">
            {office.sub}
          </text>
        </g>
      )}
      <g data-testid="ff-desk">
        <rect x={desk.x} y={desk.y} width={desk.width} height={desk.height} rx={12} className="ff-desk" />
        <text x={desk.x + desk.width / 2} y={desk.y + 26} textAnchor="middle" className="ff-desk-title">
          {desk.title}
        </text>
        <text x={desk.x + desk.width / 2} y={desk.y + 43} textAnchor="middle" className="ff-desk-sub">
          {desk.sub}
        </text>
        {desk.items.map((item, i) => {
          const top = desk.y + DESK_ITEMS_TOP + i * DESK_ITEM_STEP;
          return (
            <g key={`${i}-${item.title}-${item.text}`} className="dt-private">
              <rect x={desk.x + 10} y={top} width={desk.width - 20} height={DESK_ITEM_STEP - 8} rx={6} className="ff-desk-item" />
              <circle cx={desk.x + 22} cy={top + 15} r={4.5} className={`ff-light ff-tone-${item.tone}`} />
              <text x={desk.x + 32} y={top + 18} className="ff-desk-who">
                {item.title}
              </text>
              <text x={desk.x + 32} y={top + 33} className="ff-desk-text">
                {item.text}
              </text>
            </g>
          );
        })}
        {desk.emptyText !== null && (
          <text x={desk.x + desk.width / 2} y={desk.y + 80} textAnchor="middle" className="ff-desk-sub">
            {desk.emptyText}
          </text>
        )}
        {desk.note !== null && (
          <text x={desk.x + 14} y={desk.y + desk.height - 18} className="ff-desk-note">
            {desk.note}
          </text>
        )}
      </g>
      {view.arrows.map((a, i) => (
        <Arrow key={`${a.from}-${a.to}-${i}`} arrow={a} />
      ))}
      {view.bays.map((b) => (
        <Bay key={b.id} bay={b} />
      ))}
      {view.arrows.map((a, i) =>
        a.label === "" ? null : (
          <text key={`label-${a.from}-${a.to}-${i}`} x={a.labelX} y={a.labelY} textAnchor="middle" className={`ff-arrow-label ff-tone-${a.tone}`}>
            {a.label}
          </text>
        ),
      )}
    </svg>
  );
}

function Arrow({ arrow }: { arrow: FactoryFloorArrow }) {
  return (
    <g className={`ff-arrow ff-tone-${arrow.tone} ff-line-${arrow.line}`} data-testid={`ff-arrow-${arrow.from}-${arrow.to}`}>
      <path d={arrow.path} fill="none" />
      <polygon points={arrow.head} />
    </g>
  );
}

function Bay({ bay }: { bay: FactoryFloorBay }) {
  const body = (
    <g className={`ff-bay${bay.dashed ? " ff-dashed" : ""}${bay.kind === "source" ? " ff-source" : ""}`} data-testid={`ff-bay-${bay.id}`}>
      <title>{bay.toneText}</title>
      <rect x={bay.x} y={bay.y} width={bay.width} height={bay.height} rx={8} />
      {bay.kind === "seat" && <circle cx={bay.x + 13} cy={bay.y + 16} r={5.5} className={`ff-light ff-tone-${bay.tone}`} />}
      <text x={bay.x + (bay.kind === "seat" ? 24 : 12)} y={bay.y + 20} className="ff-bay-title">
        {bay.title}
      </text>
      <text x={bay.x + 12} y={bay.y + 38} className="ff-bay-sub">
        {bay.sub}
      </text>
    </g>
  );
  return bay.href === null ? body : <Link to={bay.href}>{body}</Link>;
}

