import type { LicenseTone, LicenseWindow } from "@devthrottle/client-core/licenses/licensesClient";

// The small pieces every part of the Licenses page draws with. None of them decides anything: a tone word from
// the Gateway picks a colour, a level word picks how a bar is drawn, and a time is shown in the viewer's own
// time zone. How far away a time is, and what a number means, arrive as finished sentences (CLAUDE.md rule 7).

/** The surface every error report from the Licenses page is filed under. */
export const LICENSES_SURFACE = "cockpit-licenses";

const TONES: ReadonlyArray<LicenseTone> = ["good", "warning", "bad", "neutral"];

/** The style hook for a Gateway tone. A tone word this build does not know is drawn neutral. */
export function toneClass(tone: string): string {
  return `lic-tone-${TONES.includes(tone as LicenseTone) ? tone : "neutral"}`;
}

/** A Gateway label in its tone. */
export function TonePill({ text, tone, testId }: { text: string; tone: string; testId?: string }) {
  return (
    <span className={`lic-pill ${toneClass(tone)}`} data-tone={tone} data-testid={testId}>
      {text}
    </span>
  );
}

/** A time in the viewer's own time zone, with the exact value on hover. An unreadable value is shown as given. */
export function LocalTime({ iso }: { iso: string }) {
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return <span>{iso}</span>;
  const text = parsed.toLocaleString(undefined, {
    day: "numeric",
    month: "short",
    hour: "2-digit",
    minute: "2-digit",
  });
  return (
    <time dateTime={iso} title={iso}>
      {text}
    </time>
  );
}

/**
 * One usage window as a labelled bar, drawn from the Gateway's level word alone. Only a level that says what the
 * window is NOW (`normal`, `at-limit`) is a filled meter. A window with no number, one whose reading is too old to
 * trust (`stale`) and one that has reset since the reading (`reset-since`) are drawn hatched, with the Gateway's
 * text beside them: "not read" is never drawn as an empty bar, because an empty bar reads as zero use, and an old
 * number is never drawn as today's.
 */
export function WindowBar({ label, window, testId }: { label: string; window: LicenseWindow; testId: string }) {
  const known = window.utilization !== null && (window.level === "normal" || window.level === "at-limit");
  const width = known ? Math.min(100, Math.max(0, window.utilization as number)) : 0;
  return (
    <div className="lic-window" data-testid={testId} data-level={window.level}>
      <div className="lic-window-label">
        <span>{label}</span>
        <span className="lic-window-value">
          {window.utilizationText}
          {window.resetsText !== null && window.resetsText.length > 0 && ` - ${window.resetsText}`}
        </span>
      </div>
      {known ? (
        <div
          className="lic-bar"
          role="meter"
          aria-label={label}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={width}
          aria-valuetext={window.utilizationText}
        >
          <span className={`lic-bar-fill lic-bar-${window.level}`} style={{ width: `${width}%` }} />
        </div>
      ) : (
        <div className="lic-bar lic-bar-unknown" role="img" aria-label={`${label}: ${window.utilizationText}`} />
      )}
    </div>
  );
}
