import { useEffect, useRef, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import { useDismissOnBackdrop } from "../ui/useDismissOnBackdrop";
import { getSessionColourLegend, MalformedColourLegendError, type SessionColourLegend } from "./sessionColours";
import "./colourLegend.css";

// The "What do the colours mean?" legend - ONE component, mounted by the Cockpit roster rail and by the phone roster.
//
// THE LEGEND DECIDES NOTHING. Every word and every swatch comes from the Gateway (GET /gateway/session-colours), which
// writes them beside the fold that decides the colours. This renders them verbatim. A failed or malformed read shows
// what went wrong; there is no built-in copy of the words to fall back on, because a stale copy is exactly how a
// legend comes to explain a colour the product no longer paints.

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

/**
 * The legend itself. It loads when it opens. Focus moves to its Close button and stays inside while it is open, and
 * returns to whatever opened it when it closes. Escape, Close, or a press that starts outside the panel closes it; a
 * drag that starts inside - selecting a sentence - never does.
 */
export function ColourLegendDialog({ onClose }: { onClose: () => void }) {
  const [legend, setLegend] = useState<SessionColourLegend | null>(null);
  const [error, setError] = useState<string | null>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const dismiss = useDismissOnBackdrop(onClose);

  useEffect(() => {
    const controller = new AbortController();
    getSessionColourLegend(controller.signal)
      .then(setLegend)
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        // A malformed answer says what is missing; a failed request gets the shared sentence for this action.
        setError(err instanceof MalformedColourLegendError ? err.message : gatewayErrorMessage(err, "load what the session colours mean"));
      });
    return () => controller.abort();
  }, []);

  // Into the dialog on open, back to the opener on close.
  useEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    closeRef.current?.focus();
    return () => opener?.focus();
  }, []);

  // Escape closes. Tab cannot leave: Close is the only control inside, so focus stays on it.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        onClose();
      } else if (event.key === "Tab") {
        event.preventDefault();
        closeRef.current?.focus();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  return (
    <div className="colour-legend-backdrop" {...dismiss}>
      <section className="colour-legend" role="dialog" aria-modal="true" aria-labelledby="colour-legend-title">
        <header className="colour-legend-head">
          <h2 id="colour-legend-title" className="colour-legend-title">
            What the colours mean
          </h2>
          <button ref={closeRef} type="button" className="colour-legend-close" onClick={onClose}>
            Close
          </button>
        </header>

        {legend === null && error === null && <p className="colour-legend-status">Loading...</p>}

        {error !== null && (
          <p className="colour-legend-status colour-legend-error" role="alert">
            {error}
          </p>
        )}

        {legend !== null && (
          <>
            <ul className="colour-legend-list">
              {legend.entries.map((entry) => (
                <li key={entry.colour} className="colour-legend-row" data-colour={entry.colour}>
                  <span className="colour-legend-dot" style={{ backgroundColor: entry.hex }} aria-hidden="true" />
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
                <span className="colour-legend-dot" style={{ backgroundColor: legend.broken.hex }} aria-hidden="true" />
                <div className="colour-legend-text">
                  <div className="colour-legend-name">{legend.broken.title}</div>
                  <div className="colour-legend-means">{legend.broken.means}</div>
                </div>
              </li>
            </ul>
            <p className="colour-legend-note">{legend.verdictNote}</p>
          </>
        )}
      </section>
    </div>
  );
}
