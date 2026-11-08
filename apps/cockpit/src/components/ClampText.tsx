import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";

/**
 * Text held to a few lines, with a "More" link when it does not fit (Web UI Polish, fix 8: one machine's row on
 * Directors was a whole screen tall because its status ran on in a narrow column). Layout only: every word is
 * still drawn, verbatim, and "More" shows all of it in place. The link appears only when the text is actually cut
 * off, which is measured in the browser - so a short status never carries a "More" that reveals nothing.
 */
export function ClampText({
  children,
  lines = 2,
  className,
  testId,
}: {
  children: ReactNode;
  lines?: number;
  className?: string;
  testId?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [cut, setCut] = useState(false);

  const measure = () => {
    const el = ref.current;
    if (el === null || open) return;
    const next = el.scrollHeight > el.clientHeight + 1;
    setCut((prev) => (prev === next ? prev : next));
  };

  // Every render can change the words (the Gateway's fold refreshes), so measure after each one; setCut is a no-op
  // when nothing changed. A window resize changes the column width, so it is measured then too.
  useLayoutEffect(measure);
  useEffect(() => {
    window.addEventListener("resize", measure);
    return () => window.removeEventListener("resize", measure);
  });

  return (
    <div className={className} data-testid={testId}>
      <div
        ref={ref}
        className={open ? "clamp-text" : "clamp-text clamped"}
        style={open ? undefined : { WebkitLineClamp: lines }}
      >
        {children}
      </div>
      {(cut || open) && (
        <button
          type="button"
          className="clamp-text-more"
          aria-expanded={open}
          onClick={(e) => {
            e.stopPropagation();
            setOpen((o) => !o);
          }}
        >
          {open ? "Less" : "More"}
        </button>
      )}
    </div>
  );
}
