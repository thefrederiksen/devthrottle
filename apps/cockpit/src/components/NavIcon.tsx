// The left-rail icons (issue #1617 follow-up).
//
// The rail's section labels were removed because they were not chunking the list. What actually
// carries the scan in a flat rail is SHAPE: every row gets a distinct silhouette, so you hit the
// destination you want by outline before you read the word. That only works if the shapes are
// genuinely distinct from each other - which is the constraint that picked this set.
//
// These are hand-drawn inline SVG rather than an icon package. The Cockpit has four dependencies
// (react, react-dom, react-router-dom, client-core); adding a library and its build weight for
// fourteen glyphs is not a trade worth making, and inline paths cannot go missing at runtime.
//
// Every icon is drawn on the same 24x24 grid with the same 2px stroke and round caps, and paints in
// currentColor - so a row's icon takes the nav link's color for free, including the dim/hover/active
// states, and the set reads as one family rather than fourteen separate drawings.

export type NavIconName =
  | "fleet-manager"
  | "fleet-map"
  | "assistant"
  | "sessions"
  | "history"
  | "directors"
  | "schedule"
  | "workflows"
  | "skills"
  | "dictionary"
  | "voice-recorder"
  | "transcription"
  | "network"
  | "account"
  | "phone"
  | "throttle"
  | "settings"
  | "injected-text"
  | "about"
  | "help";

// The shapes, keyed by name. Each value is the icon's paint - the <svg> wrapper (grid, stroke, size)
// is applied once below, so no glyph can drift off the shared geometry.
const PAINT: Record<NavIconName, JSX.Element> = {
  // A ring around a centre, with four sighting ticks: the one place everything is watched from (the Fleet
  // Manager, step 6). The ticks keep it distinct from the plain circles (network, about, help) below it.
  "fleet-manager": (
    <>
      <circle cx="12" cy="12" r="8" />
      <circle cx="12" cy="12" r="3" />
      <path d="M12 1v3" />
      <path d="M12 20v3" />
      <path d="M1 12h3" />
      <path d="M20 12h3" />
    </>
  ),
  // A folded map: the fleet's spatial picture.
  "fleet-map": (
    <>
      <path d="M14.1 5.55a2 2 0 0 1-1.79 0L8.1 3.45a2 2 0 0 0-1.79 0L3.55 4.83A1 1 0 0 0 3 5.72v12.76a1 1 0 0 0 1.45.9l2.86-1.43a2 2 0 0 1 1.79 0l4.21 2.11a2 2 0 0 0 1.79 0l3.66-1.83a1 1 0 0 0 .55-.9V4.62a1 1 0 0 0-1.45-.9z" />
      <path d="M9 3.24v15" />
      <path d="M15 5.76v15" />
    </>
  ),
  // A speech bubble with text lines: the fleet-level chat + voice assistant. Rounded outline keeps
  // it distinct from the square-cornered terminal beside it.
  assistant: (
    <>
      <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
      <path d="M8 8h8" />
      <path d="M8 12h5" />
    </>
  ),
  // A terminal prompt: the thing a session actually is.
  sessions: (
    <>
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="m7 11 2-2-2-2" />
      <path d="M11 13h4" />
    </>
  ),
  // A clock winding back: what was worked on. The counter-clockwise arrow keeps it distinct from
  // the plain circles (network, about, help) elsewhere in the rail.
  history: (
    <>
      <path d="M3 12a9 9 0 1 0 2.64-6.36" />
      <path d="M3 4v4h4" />
      <path d="M12 7v5l3 3" />
    </>
  ),
  // A monitor: one machine running the fleet.
  directors: (
    <>
      <rect x="2" y="3" width="20" height="14" rx="2" />
      <path d="M8 21h8" />
      <path d="M12 17v4" />
    </>
  ),
  // A calendar: what runs when.
  schedule: (
    <>
      <rect x="3" y="4" width="18" height="18" rx="2" />
      <path d="M16 2v4" />
      <path d="M8 2v4" />
      <path d="M3 10h18" />
    </>
  ),
  // Two boxes joined by a branching path: how work runs. Deliberately unlike the calendar next to it.
  workflows: (
    <>
      <rect x="3" y="3" width="8" height="8" rx="2" />
      <rect x="13" y="13" width="8" height="8" rx="2" />
      <path d="M7 11v4a2 2 0 0 0 2 2h4" />
    </>
  ),
  // A wrench: a capability an agent reaches for mid-task.
  skills: (
    <>
      <path d="M14.7 6.3a4 4 0 0 0 5 5l-8.4 8.4a2.1 2.1 0 0 1-3-3Z" />
      <path d="M14.7 6.3 17.5 3.5" />
    </>
  ),
  // A book.
  dictionary: (
    <>
      <path d="M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H19a1 1 0 0 1 1 1v18a1 1 0 0 1-1 1H6.5a2.5 2.5 0 0 1 0-5H20" />
    </>
  ),
  // A microphone.
  "voice-recorder": (
    <>
      <path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3z" />
      <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
      <path d="M12 19v3" />
    </>
  ),
  // A waveform: speech turned into text.
  transcription: (
    <>
      <path d="M2 12h2" />
      <path d="M6 8v8" />
      <path d="M10 5v14" />
      <path d="M14 8v8" />
      <path d="M18 5v14" />
      <path d="M22 12h-2" />
    </>
  ),
  // A globe: how the machines reach each other.
  network: (
    <>
      <circle cx="12" cy="12" r="10" />
      <path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20" />
      <path d="M2 12h20" />
    </>
  ),
  // A person.
  account: (
    <>
      <circle cx="12" cy="7" r="4" />
      <path d="M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2" />
    </>
  ),
  // A handset: getting DevThrottle onto your phone. A tall rounded rectangle with a home bar, which is
  // the one silhouette in this set that is taller than it is wide - so it is found by outline alone,
  // even sitting between the person-shape of Account and the gauge of Your Throttle.
  phone: (
    <>
      <rect x="7" y="2" width="10" height="20" rx="2" />
      <path d="M10.5 18h3" />
    </>
  ),
  // A gauge: your throttle.
  throttle: (
    <>
      <path d="M3.34 19a10 10 0 1 1 17.32 0" />
      <path d="m12 14 4-4" />
    </>
  ),
  // A gear.
  settings: (
    <>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </>
  ),
  // Bracketed lines of text: the injected preamble, whose editable points are square-bracket tokens.
  "injected-text": (
    <>
      <path d="M8 4H5v16h3" />
      <path d="M16 4h3v16h-3" />
      <path d="M9 10h6" />
      <path d="M9 14h6" />
    </>
  ),
  // An information mark.
  about: (
    <>
      <circle cx="12" cy="12" r="10" />
      <path d="M12 16v-4" />
      <path d="M12 8h.01" />
    </>
  ),
  // A question mark: help / documentation. A curl rather than the "i" stroke keeps it distinct in
  // silhouette from the About info mark that sits beside it in the bottom list.
  help: (
    <>
      <circle cx="12" cy="12" r="10" />
      <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" />
      <path d="M12 17h.01" />
    </>
  ),
};

export interface NavIconProps {
  name: NavIconName;
}

/**
 * One rail icon. Decorative: the nav link's text is the accessible name, so the icon is hidden from
 * screen readers rather than repeating that word.
 */
export function NavIcon({ name }: NavIconProps) {
  return (
    <svg
      className="nav-icon"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {PAINT[name]}
    </svg>
  );
}
