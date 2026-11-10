// THE EXPLAINER CARD (Watch your factory, version one, piece 6): what a contributor reads before their first watch, as
// the mockup erik-replay.html shows it - a light card on the dark floor, the three paragraphs, one button, and three of
// the factory's characters beside it.
//
// Critical rule 7: every word is the Gateway's (the view's `explainer`), approved by the owner word for word. This
// component writes none of its own, and whether it is shown at all is the Gateway's verdict too: the shell draws it
// exactly when the view carries one.

import type { FactoryWatchExplainer } from "./factoryWatchTypes";
import { WatchMonster } from "./WatchMonster";
import "./watchExplainer.css";

export interface WatchExplainerCardProps {
  explainer: FactoryWatchExplainer;
  /** The factory's name from the same view, as the card's heading. */
  title: string;
  /** False when the shell's own frame already shows the factory's name (the phone's app bar), so it is not shown twice.
   *  The name still labels the card for a screen reader. Defaults to true. */
  showTitle?: boolean;
  /** Called when the person presses the button. */
  onWatch: () => void;
}

/** Three characters for the card, in the mockup's colours: a Boss with its crown and two workers. A drawing, no words. */
const TRIO: ReadonlyArray<{ colour: string; expression: "happy" | "focused" | "cool"; crown: boolean; x: number }> = [
  { colour: "#f0a03c", expression: "happy", crown: true, x: 0 },
  { colour: "#5aa0ff", expression: "focused", crown: false, x: 36 },
  { colour: "#7fcf9a", expression: "cool", crown: false, x: 72 },
];

export function WatchExplainerCard({ explainer, title, showTitle = true, onWatch }: WatchExplainerCardProps) {
  return (
    <div className="fwx" data-testid="watch-explainer">
      <div
        className="fwx-card"
        role="dialog"
        aria-modal="false"
        {...(showTitle ? { "aria-labelledby": "fwx-title" } : { "aria-label": title })}
      >
        {showTitle && (
          <h2 id="fwx-title" className="fwx-title">
            {title}
          </h2>
        )}
        {explainer.paragraphs.map((p, i) => (
          <p key={i} className="fwx-paragraph" data-testid="watch-explainer-paragraph">
            {p}
          </p>
        ))}
        <div className="fwx-row">
          <button type="button" className="fwx-go" onClick={onWatch} data-testid="watch-explainer-go" autoFocus>
            {explainer.buttonText}
          </button>
          <svg className="fwx-trio" width="110" height="50" viewBox="-20 -50 110 52" aria-hidden="true">
            {TRIO.map((m) => (
              <g key={m.colour} transform={`translate(${m.x},0)`}>
                <WatchMonster colour={m.colour} expression={m.expression} t={0} crown={m.crown} />
              </g>
            ))}
          </svg>
        </div>
      </div>
    </div>
  );
}
