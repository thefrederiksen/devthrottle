import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type MouseEvent as ReactMouseEvent,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from "react";
import type { MissionButtonDisplay } from "../api/client";
import { reportShownError } from "../errors/reportClientError";
import "./missionButton.css";

// THE MISSION BUTTON ON THE WEB - shared by the Cockpit session header and the phone's session app bar, so the two
// surfaces cannot drift (repository rule 8). It is the web twin of the desktop's MissionButtonPresenter.
//
// IT RENDERS THE GATEWAY'S VERDICT AND NOTHING ELSE (repository rule 7). The Gateway's MissionButtonFold stamps
// `missionButton` on every session row: the words on the button (always "Mission"), a colour key, an icon key, the
// finished tooltip (the Mission's name and goal) and the address of the Mission's document. This component shows the
// label, draws the colour and the icon the keys name, shows the tooltip, and opens the address in a new tab when there
// is one. It never reads `verdict`, never composes a word, and never builds an address of its own.
//
// THE ONLY MAPPINGS IT DOES are colour key to drawing (COLOUR_CLASS) and icon key to drawing (ICONS), which are
// layout. A key it does not know is a broken contract between this client and the Gateway: it is reported centrally
// and a visible problem note is shown in place of the button - never a guessed colour or icon, and never nothing,
// because nothing reads exactly like "the Gateway sent no verdict". The same holds for a document address that is not
// a path on the Gateway.
//
// A null or absent `missionButton` renders nothing: the Gateway claims nothing (an older Gateway, or a fold that was
// given no missions to read).
//
// THE TOOLTIP BUBBLE. A `title` attribute never shows on a phone, so the same words also show in a small bubble under
// the button. A long press by a finger shows it on any button. A button with no document to open shows it on a click
// by ANY pointer - mouse, pen or finger - because that click has nothing else to do; on a mouse surface it repeats the
// hover words, which is harmless. A long press that showed the bubble does not also open the document. The bubble is
// kept inside the screen: after it is drawn it is measured and slid sideways if it would cross either edge.

const SURFACE = "mission button";

/** The Gateway's colour key to the class that draws it. The three keys are the C# MissionButtonDisplay.Color* values. */
const COLOUR_CLASS: Readonly<Record<string, string>> = {
  mission: "mb-mission",
  "mission-outline": "mb-mission-outline",
  warning: "mb-warning",
};

/** The icons, inline (no icon font, no characters), drawn before the word. The three keys are the C#
 *  MissionButtonDisplay.Icon* values; "none" draws the word alone. */
const ICONS: Readonly<Record<string, ReactNode>> = {
  document: (
    <svg className="mb-icon" data-icon="document" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} aria-hidden="true">
      <path d="M4 1.5h5.5L12.5 4.5v10h-8.5z" />
      <path d="M9.5 1.5v3h3" />
      <path d="M6 8h4.5M6 10.5h4.5" />
    </svg>
  ),
  none: null,
  warning: (
    <svg className="mb-icon" data-icon="warning" viewBox="0 0 16 16" fill="none" stroke="#EAB308" strokeWidth={1.6} strokeLinejoin="round" aria-hidden="true">
      <path d="M8 1.8L15 14H1z" />
      <path d="M8 6.2v3.8" />
      <circle cx="8" cy="12" r=".6" fill="#EAB308" />
    </svg>
  ),
};

/** The gap kept between the touch bubble and the edge of the screen. */
const SCREEN_GAP_PX = 8;

/** How long a finger must rest on the button before the tooltip shows instead of the click. */
export const LONG_PRESS_MS = 500;

/** The class that draws a colour key, or null for a key this build does not know. */
export function missionButtonColourClass(colorKey: string | null | undefined): string | null {
  if (typeof colorKey !== "string") return null;
  return Object.prototype.hasOwnProperty.call(COLOUR_CLASS, colorKey) ? COLOUR_CLASS[colorKey] : null;
}

/** True when this build knows how to draw the icon key. */
export function missionButtonKnowsIcon(iconKey: string | null | undefined): boolean {
  return typeof iconKey === "string" && Object.prototype.hasOwnProperty.call(ICONS, iconKey);
}

/**
 * How far to slide a bubble sideways so it stays inside a screen this wide, given where it was drawn. Zero when it
 * already fits. A bubble wider than the screen is pinned to the left gap, so its start is always readable.
 */
export function bubbleShift(left: number, right: number, screenWidth: number): number {
  if (left < SCREEN_GAP_PX) return SCREEN_GAP_PX - left;
  if (right > screenWidth - SCREEN_GAP_PX) return Math.max(screenWidth - SCREEN_GAP_PX - right, SCREEN_GAP_PX - left);
  return 0;
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
  if (!missionButtonKnowsIcon(display.iconKey)) {
    return `The Gateway sent a Mission button icon this app does not know ("${String(display.iconKey)}"), so no Mission button is shown. Update the app or the Gateway so they agree.`;
  }
  const address = display.documentAddress;
  if (address !== null && address !== undefined && !isGatewayPath(address)) {
    return `The Gateway sent a Mission document address this app cannot open ("${String(address)}"): it must be a path on the Gateway, starting with one slash.`;
  }
  return null;
}

/**
 * True when the address is a path on this page's own origin - the Gateway that served it. A string check alone is
 * not enough: a browser resolves "/\evil.example/r/1" to another site, so the address is resolved the way the link
 * will be, and the origin it lands on must be this one.
 */
function isGatewayPath(address: unknown): boolean {
  if (typeof address !== "string" || !address.startsWith("/") || address.startsWith("//")) return false;
  return new URL(address, window.location.origin).origin === window.location.origin;
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
  const tipRef = useRef<HTMLSpanElement | null>(null);

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

  // Keep the touch bubble inside the screen. It is drawn under the button, right edges aligned; on a phone a button
  // that is not at the very right of the bar pushes a wide bubble past the left edge, so it is measured before the
  // frame is painted and slid back in.
  useLayoutEffect(() => {
    const tip = tipRef.current;
    if (!tipOpen || tip === null) return;
    tip.style.transform = "";
    const box = tip.getBoundingClientRect();
    const shift = bubbleShift(box.left, box.right, document.documentElement.clientWidth || window.innerWidth);
    if (shift !== 0) tip.style.transform = `translateX(${shift}px)`;
  }, [tipOpen]);

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
  const icon = ICONS[display.iconKey];

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
          data-icon-key={display.iconKey}
          onClick={(e) => {
            // The press already showed the tooltip: that was the gesture's whole answer.
            if (pressShowedTip.current) {
              e.preventDefault();
              pressShowedTip.current = false;
            }
          }}
          {...touchHandlers}
        >
          {icon}
          <span className="mb-label">{display.label}</span>
        </a>
      ) : (
        <button
          type="button"
          className={className}
          title={display.tooltip}
          data-color-key={display.colorKey}
          data-icon-key={display.iconKey}
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
          {icon}
          <span className="mb-label">{display.label}</span>
        </button>
      )}
      {tipOpen && (
        <span className="mb-tip" role="tooltip" ref={tipRef}>
          {display.tooltip}
        </span>
      )}
    </span>
  );
}
