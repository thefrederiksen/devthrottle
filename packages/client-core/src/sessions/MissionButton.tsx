import { useEffect, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from "react";
import type { MissionButtonDisplay } from "../api/client";
import { reportShownError } from "../errors/reportClientError";
import "./missionButton.css";

// THE MISSION BUTTON ON THE WEB - shared by the Cockpit session header and the phone's session app bar, so the two
// surfaces cannot drift (repository rule 8). It is the web twin of the desktop's MissionButtonPresenter.
//
// IT RENDERS THE GATEWAY'S VERDICT AND NOTHING ELSE (repository rule 7). The Gateway's MissionButtonFold stamps
// `missionButton` on every session row: the words on the button, a colour key, the finished tooltip and the address
// of the Mission's document. This component shows the label, draws the colour the key names, shows the tooltip, and
// opens the address in a new tab when there is one. It never reads `verdict`, never composes a word, and never builds
// an address of its own.
//
// THE ONLY MAPPING IT DOES is colour key to drawing (COLOUR_CLASS below), which is layout. A key it does not know is
// a broken contract between this client and the Gateway: it is reported centrally and a visible problem note is shown
// in place of the button - never a guessed colour, and never nothing, because nothing reads exactly like "the Gateway
// sent no verdict". The same holds for a document address that is not a path on the Gateway.
//
// A null or absent `missionButton` renders nothing: the Gateway claims nothing (an older Gateway, or a fold that was
// given no missions to read).
//
// THE TOOLTIP ON A TOUCH SCREEN. A `title` attribute never shows on a phone, so a long press (and, when there is no
// document to open, a plain tap) shows the same words in a small bubble under the button. A long press that showed
// the bubble does not also open the document.

const SURFACE = "mission button";

/** The Gateway's colour key to the class that draws it. The three keys are the C# MissionButtonDisplay.Color* values. */
const COLOUR_CLASS: Readonly<Record<string, string>> = {
  mission: "mb-mission",
  "mission-outline": "mb-mission-outline",
  warning: "mb-warning",
};

/** How long a finger must rest on the button before the tooltip shows instead of the click. */
export const LONG_PRESS_MS = 500;

/** The class that draws a colour key, or null for a key this build does not know. */
export function missionButtonColourClass(colorKey: string | null | undefined): string | null {
  if (typeof colorKey !== "string") return null;
  return Object.prototype.hasOwnProperty.call(COLOUR_CLASS, colorKey) ? COLOUR_CLASS[colorKey] : null;
}

/**
 * Why the Gateway's verdict cannot be drawn, or null when it can. The sentence names the value the Gateway sent, so
 * the note on screen and the central report both say what broke.
 */
export function missionButtonProblem(display: MissionButtonDisplay | null | undefined): string | null {
  if (display === null || display === undefined) return null;
  if (missionButtonColourClass(display.colorKey) === null) {
    return `The Gateway sent a Mission button colour this app does not know ("${String(display.colorKey)}"), so no Mission button is shown. Update the app or the Gateway so they agree.`;
  }
  const address = display.documentAddress;
  if (address !== null && address !== undefined && (typeof address !== "string" || !address.startsWith("/") || address.startsWith("//"))) {
    return `The Gateway sent a Mission document address this app cannot open ("${String(address)}"): it must be a path on the Gateway, starting with one slash.`;
  }
  return null;
}

export interface MissionButtonProps {
  /** The session's `missionButton`, exactly as the Gateway stamped it. */
  display: MissionButtonDisplay | null | undefined;
  /** The session it belongs to, for the central report when the verdict cannot be drawn. */
  sessionId?: string;
}

export function MissionButton({ display, sessionId }: MissionButtonProps) {
  const [tipOpen, setTipOpen] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const pressTimer = useRef<number | null>(null);
  const pressShowedTip = useRef(false);
  const rootRef = useRef<HTMLSpanElement | null>(null);

  const found = missionButtonProblem(display);

  // Report a verdict this client cannot draw. In an effect, not in the render, so a poll that hands back the same
  // broken verdict every few seconds reports it once (background: reported again only when the sentence changes).
  useEffect(() => {
    setProblem(found === null ? null : reportShownError(SURFACE, "draw the Mission button", found, { sessionId, background: true }));
  }, [found, sessionId]);

  // Close the touch tooltip on a tap anywhere else, or Escape.
  useEffect(() => {
    if (!tipOpen) return;
    const onDown = (e: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setTipOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setTipOpen(false);
    };
    document.addEventListener("pointerdown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [tipOpen]);

  useEffect(
    () => () => {
      if (pressTimer.current !== null) window.clearTimeout(pressTimer.current);
    },
    [],
  );

  if (display === null || display === undefined) return null;

  // Only the reported sentence is shown, so nothing reaches the screen that the central log did not also get. The
  // effect above sets it straight after the first render, so the note is missing for at most one frame.
  if (found !== null) {
    return <span className="mb-root">{problem !== null && <span className="mb-problem" role="alert">{problem}</span>}</span>;
  }

  // missionButtonProblem returned null, so the key is known and the address, when present, is a Gateway path.
  const colourClass = missionButtonColourClass(display.colorKey) as string;
  const address = typeof display.documentAddress === "string" ? display.documentAddress : null;
  const className = `mb-btn ${colourClass}`;

  const cancelPress = () => {
    if (pressTimer.current !== null) {
      window.clearTimeout(pressTimer.current);
      pressTimer.current = null;
    }
  };
  const onPointerDown = (e: ReactPointerEvent) => {
    pressShowedTip.current = false;
    if (e.pointerType !== "touch") return;
    cancelPress();
    pressTimer.current = window.setTimeout(() => {
      pressTimer.current = null;
      pressShowedTip.current = true;
      setTipOpen(true);
    }, LONG_PRESS_MS);
  };
  const touchHandlers = {
    onPointerDown,
    onPointerUp: cancelPress,
    onPointerLeave: cancelPress,
    onPointerCancel: cancelPress,
    // A long press on a link opens the phone's own link menu; the bubble is the answer to a long press here.
    onContextMenu: (e: ReactMouseEvent) => {
      if (pressShowedTip.current || pressTimer.current !== null) e.preventDefault();
    },
  };

  return (
    <span className="mb-root" ref={rootRef}>
      {address !== null ? (
        <a
          className={className}
          href={address}
          target="_blank"
          rel="noopener noreferrer"
          title={display.tooltip}
          data-color-key={display.colorKey}
          onClick={(e) => {
            // The press already showed the tooltip: that was the gesture's whole answer.
            if (pressShowedTip.current) {
              e.preventDefault();
              pressShowedTip.current = false;
            }
          }}
          {...touchHandlers}
        >
          {display.label}
        </a>
      ) : (
        <button
          type="button"
          className={className}
          title={display.tooltip}
          data-color-key={display.colorKey}
          aria-expanded={tipOpen}
          onClick={() => {
            if (pressShowedTip.current) {
              pressShowedTip.current = false;
              return;
            }
            // Nothing to open, so a tap shows the words a mouse would see on hover.
            setTipOpen((v) => !v);
          }}
          {...touchHandlers}
        >
          {display.label}
        </button>
      )}
      {tipOpen && (
        <span className="mb-tip" role="tooltip">
          {display.tooltip}
        </span>
      )}
    </span>
  );
}
