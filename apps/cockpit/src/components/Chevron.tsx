// The collapse chevron (issue #3074): the one shape that says "this panel folds away, and folds back".
//
// Drawn on the same 24x24 grid, the same 2px stroke and the same currentColor as the rail icons (NavIcon),
// so the rail's collapse control and the session dock's are visibly the same control in two places rather
// than two drawings that happen to both be arrows.

export interface ChevronProps {
  pointing: "left" | "right" | "up" | "down";
}

const PATH: Record<ChevronProps["pointing"], string> = {
  left: "M15 6l-6 6 6 6",
  right: "M9 6l6 6-6 6",
  up: "M6 15l6-6 6 6",
  down: "M6 9l6 6 6-6",
};

/** Decorative: the control around it carries the words, so this is hidden from screen readers. */
export function Chevron({ pointing }: ChevronProps) {
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
      <path d={PATH[pointing]} />
    </svg>
  );
}
