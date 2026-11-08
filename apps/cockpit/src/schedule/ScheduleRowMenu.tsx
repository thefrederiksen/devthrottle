import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Link } from "react-router-dom";

/**
 * The "..." beside Run now on a Schedule row (Web UI Polish, fix 7). Edit and Delete - or, for a factory's schedule,
 * Edit in factory - moved in here so the table fits the page at 1600 pixels without a sideways scroll. The menu
 * decides nothing: the row hands it which actions apply, exactly as the row used to draw them inline.
 *
 * The popover is portaled to document.body (like the session menu) so the table's own overflow cannot clip it,
 * and it closes on an outside click, on Escape and after any choice.
 */
export interface ScheduleRowMenuProps {
  jobName: string;
  /** Set for a factory's schedule: the one place it is changed. Edit and Delete are not offered then. */
  factoryHref?: string;
  onEdit?: () => void;
  onDelete?: () => void;
}

interface PopPos {
  top?: number;
  bottom?: number;
  right: number;
}

// One item is about 31 pixels high; the menu flips upward when two will not fit under the button.
const MENU_HEIGHT = 2 * 31 + 12;

export function ScheduleRowMenu({ jobName, factoryHref, onEdit, onDelete }: ScheduleRowMenuProps) {
  const [open, setOpen] = useState(false);
  const [popPos, setPopPos] = useState<PopPos | null>(null);
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const r = btnRef.current?.getBoundingClientRect();
      if (!r) return;
      const right = Math.max(8, window.innerWidth - r.right);
      if (window.innerHeight - r.bottom < MENU_HEIGHT + 8) {
        setPopPos({ bottom: Math.max(8, window.innerHeight - r.top + 4), right });
      } else {
        setPopPos({ top: r.bottom + 4, right });
      }
    };
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (btnRef.current?.contains(t)) return;
      if (popRef.current?.contains(t)) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const choose = (action: (() => void) | undefined) => {
    setOpen(false);
    action?.();
  };

  return (
    <>
      <button
        ref={btnRef}
        type="button"
        className="sched-rowmenu-btn"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`More actions for ${jobName}`}
        title="More actions"
        onClick={() => setOpen((o) => !o)}
      >
        <span aria-hidden="true">...</span>
      </button>
      {open &&
        popPos !== null &&
        createPortal(
          <div
            ref={popRef}
            className="session-menu-pop sched-rowmenu-pop"
            role="menu"
            aria-label={`Actions for ${jobName}`}
            style={{ position: "fixed", right: popPos.right, top: popPos.top ?? "auto", bottom: popPos.bottom ?? "auto" }}
          >
            {factoryHref !== undefined ? (
              <Link role="menuitem" className="session-menu-item" to={factoryHref} onClick={() => setOpen(false)}>
                Edit in factory
              </Link>
            ) : (
              <>
                <button type="button" role="menuitem" className="session-menu-item" onClick={() => choose(onEdit)}>
                  Edit
                </button>
                <button type="button" role="menuitem" className="session-menu-item danger" onClick={() => choose(onDelete)}>
                  Delete
                </button>
              </>
            )}
          </div>,
          document.body,
        )}
    </>
  );
}
