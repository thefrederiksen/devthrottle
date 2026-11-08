import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { createPortal } from "react-dom";
import { Link } from "react-router-dom";

/**
 * The "..." beside Run now on a Schedule row (Web UI Polish, fix 7). Edit and Delete - or, for a factory's schedule,
 * Edit in factory - moved in here so the table fits the page at 1600 pixels without a sideways scroll. The menu
 * decides nothing: the row hands it which actions apply, exactly as the row used to draw them inline.
 *
 * The popover is portaled to document.body (like the session menu) so the table's own overflow cannot clip it,
 * and it closes on an outside click, on Escape and after any choice. From the keyboard: opening it puts focus on
 * the first item, the up and down arrows move between items, and Escape or a choice puts focus back on "...".
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

  const items = () => Array.from(popRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
  const close = (returnFocus: boolean) => {
    setOpen(false);
    if (returnFocus) btnRef.current?.focus();
  };

  // Once the popover is first placed, focus its first item so the keyboard lands inside the menu. Keyed on whether
  // it is placed, not on where: a scroll moves it, and must not pull focus back to the first item.
  const placed = popPos !== null;
  useEffect(() => {
    if (open && placed) items()[0]?.focus();
  }, [open, placed]);

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
      if (e.key === "Escape") close(true);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const choose = (action: (() => void) | undefined) => {
    close(true);
    action?.();
  };

  const onMenuKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    e.preventDefault();
    const list = items();
    if (list.length === 0) return;
    const at = list.indexOf(document.activeElement as HTMLElement);
    const step = e.key === "ArrowDown" ? 1 : -1;
    list[(at + step + list.length) % list.length].focus();
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
            onKeyDown={onMenuKeyDown}
            style={{ position: "fixed", right: popPos.right, top: popPos.top ?? "auto", bottom: popPos.bottom ?? "auto" }}
          >
            {factoryHref !== undefined ? (
              <Link role="menuitem" className="session-menu-item" to={factoryHref} onClick={() => close(false)}>
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
