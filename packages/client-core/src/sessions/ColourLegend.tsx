import { useEffect, useState } from "react";
import { BROKEN_NOTE, COLOUR_LEGEND, VERDICT_NOTE, swatchHex } from "./colourMeanings";
import "./colourLegend.css";

// The "What do the colours mean?" legend - ONE component, mounted by the Cockpit roster rail and by the phone roster,
// so a person gets the same explanation on either screen. The words and the swatches come from colourMeanings.ts; each
// shell only places the button.

/** The small link-style button that opens the legend. `className` lets a shell tune its placement. */
export function ColourLegendButton({ className }: { className?: string }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button
        type="button"
        className={className ? `colour-legend-btn ${className}` : "colour-legend-btn"}
        aria-haspopup="dialog"
        onClick={() => setOpen(true)}
      >
        What do the colours mean?
      </button>
      {open && <ColourLegendDialog onClose={() => setOpen(false)} />}
    </>
  );
}

/** The legend itself. Escape, the Close button, or a tap outside the panel closes it. */
export function ColourLegendDialog({ onClose }: { onClose: () => void }) {
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  return (
    <div className="colour-legend-backdrop" role="presentation" onClick={onClose}>
      <section
        className="colour-legend"
        role="dialog"
        aria-modal="true"
        aria-labelledby="colour-legend-title"
        onClick={(event) => event.stopPropagation()}
      >
        <header className="colour-legend-head">
          <h2 id="colour-legend-title" className="colour-legend-title">
            What the colours mean
          </h2>
          <button type="button" className="colour-legend-close" onClick={onClose}>
            Close
          </button>
        </header>
        <ul className="colour-legend-list">
          {COLOUR_LEGEND.map((entry) => (
            <li key={entry.colour} className="colour-legend-row" data-colour={entry.colour}>
              <span className="colour-legend-dot" style={{ backgroundColor: swatchHex(entry) }} aria-hidden="true" />
              <div className="colour-legend-text">
                <div className="colour-legend-name">
                  {entry.title}
                  <span className="colour-legend-asks">Asks for you: {entry.asksForYou}</span>
                </div>
                <div className="colour-legend-means">{entry.means}</div>
              </div>
            </li>
          ))}
          <li className="colour-legend-row" data-colour="broken">
            <span className="colour-legend-dot" style={{ backgroundColor: BROKEN_NOTE.hex }} aria-hidden="true" />
            <div className="colour-legend-text">
              <div className="colour-legend-name">{BROKEN_NOTE.title}</div>
              <div className="colour-legend-means">{BROKEN_NOTE.means}</div>
            </div>
          </li>
        </ul>
        <p className="colour-legend-note">{VERDICT_NOTE}</p>
      </section>
    </div>
  );
}
