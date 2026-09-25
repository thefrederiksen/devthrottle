import { Link } from "react-router-dom";
import type { FactoryMapEdge, FactoryMapNode, FactoryMapView } from "@devthrottle/client-core/factory/factoryAgentsClient";
import { ToneChip, toneClass } from "./FactoryParts";

// The factory map (issue #3383), drawn from the Gateway's finished view. Nothing here decides anything (rule 7):
// the positions are the factory's own layout (Graphviz, in the factory's tool), the words, tones, line styles and
// paths are the Gateway's. This component only turns them into SVG shapes - as React elements, never as markup
// handed in from outside - and shows the picked box's spec beside the map. The map is read-only: a factory is
// changed by talking to an agent, never by dragging a box.

const PAD = 8;

export function FactoryMapDrawing({
  view,
  selected,
  onSelect,
}: {
  view: FactoryMapView;
  selected: string | null;
  onSelect: (id: string) => void;
}) {
  return (
    <div className="fa-map-frame">
      <svg
        className="fa-map"
        viewBox={`${-PAD} ${-PAD} ${view.width + 2 * PAD} ${view.height + 2 * PAD}`}
        role="img"
        aria-label={`Map of ${view.title}`}
        data-testid="fa-map"
      >
        {view.edges.map((e, i) => (
          <MapEdge key={`${e.from}-${e.to}-${i}`} edge={e} />
        ))}
        {view.nodes.map((n) => (
          <MapNode key={n.id} node={n} selected={n.id === selected} onSelect={onSelect} />
        ))}
      </svg>
    </div>
  );
}

function MapEdge({ edge }: { edge: FactoryMapEdge }) {
  return (
    <g className={`fa-map-edge fa-map-line-${edge.line} ${toneClass(edge.tone)}`} data-testid={`fa-edge-${edge.from}-${edge.to}`}>
      <path d={edge.path} fill="none" />
      {edge.head !== null && <polygon points={edge.head} />}
      {edge.labelX !== null && edge.labelY !== null && (
        <text x={edge.labelX} y={edge.labelY} textAnchor="middle" className="fa-map-edge-label">
          {edge.label.split("\n").map((line, i) => (
            <tspan key={i} x={edge.labelX ?? 0} dy={i === 0 ? 0 : 11}>
              {line}
            </tspan>
          ))}
        </text>
      )}
    </g>
  );
}

function MapNode({ node, selected, onSelect }: { node: FactoryMapNode; selected: boolean; onSelect: (id: string) => void }) {
  const left = node.x - node.width / 2;
  const top = node.y - node.height / 2;
  // The box is the size the factory's layout gave it for the title and its lines; the status word sits just
  // under the box, so it never crowds the lines the layout measured.
  const lines = node.lines;
  const lineHeight = 12;
  const firstY = node.y - ((lines.length + 1) * lineHeight) / 2 + lineHeight - 2;
  const pick = () => onSelect(node.id);
  return (
    <g
      className={`fa-map-node fa-map-kind-${node.kind} ${toneClass(node.tone)}${node.dashed ? " fa-map-dashed" : ""}${selected ? " fa-map-selected" : ""}`}
      role="button"
      tabIndex={0}
      aria-label={`${node.title}${node.statusWord !== null ? `, ${node.statusWord}` : ""}`}
      aria-pressed={selected}
      onClick={pick}
      onKeyDown={(ev) => {
        if (ev.key === "Enter" || ev.key === " ") {
          ev.preventDefault();
          pick();
        }
      }}
      data-testid={`fa-node-${node.id}`}
    >
      {node.kind === "owner" ? (
        <ellipse cx={node.x} cy={node.y} rx={node.width / 2} ry={node.height / 2} />
      ) : (
        <rect x={left} y={top} width={node.width} height={node.height} rx={6} ry={6} />
      )}
      <text x={node.x} y={firstY} textAnchor="middle" className="fa-map-title">
        {node.title}
      </text>
      {lines.map((line, i) => (
        <text key={i} x={node.x} y={firstY + (i + 1) * lineHeight} textAnchor="middle" className="fa-map-line">
          {line}
        </text>
      ))}
      {node.statusWord !== null && (
        <text x={node.x} y={top + node.height + 11} textAnchor="middle" className="fa-map-status">
          {node.statusWord}
        </text>
      )}
    </g>
  );
}

/** The picked box's spec: the Gateway's live rows first, then what the factory's files say about it. */
export function FactoryMapSpec({ node }: { node: FactoryMapNode | null }) {
  if (node === null)
    return (
      <aside className="fa-map-side fa-dim" data-testid="fa-map-spec">
        Pick a box to see what it reads, what it makes and what it may not do.
      </aside>
    );
  return (
    <aside className="fa-map-side" data-testid="fa-map-spec">
      <h3 className="fa-map-side-title">
        {node.title} {node.statusWord !== null && <ToneChip word={node.statusWord} tone={node.tone} />}
      </h3>
      <dl className="fa-map-spec">
        {node.spec.map((r, i) => (
          <div key={`${r.label}-${i}`}>
            <dt>{r.label}</dt>
            <dd>{r.text}</dd>
          </div>
        ))}
      </dl>
      {node.href !== null && (
        <Link className="fa-link" to={node.href}>
          Open {node.title}
        </Link>
      )}
    </aside>
  );
}

export function FactoryMapLegend({ view }: { view: FactoryMapView }) {
  return (
    <ul className="fa-map-legend">
      {view.legend.map((l) => (
        <li key={l.text}>
          <svg width="30" height="10" aria-hidden="true" className={`fa-map-edge fa-map-line-${l.line} ${toneClass(l.tone)}`}>
            <path d="M 1 5 L 29 5" fill="none" />
          </svg>
          {l.text}
        </li>
      ))}
    </ul>
  );
}
