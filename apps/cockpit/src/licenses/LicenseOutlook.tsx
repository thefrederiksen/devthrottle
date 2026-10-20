import type {
  LicenseOutlook,
  LicenseOutlookSpanKind,
  LicenseOutlookTile,
} from "@devthrottle/client-core/licenses/licensesClient";
import { LocalTime, toneClass } from "./licenseParts";

// THE NEXT SEVEN DAYS ON THE LICENSES PAGE: the alert when the fleet is forecast to have no account with room, the
// three tiles, and the timeline - one row of bars per account. Every span, gap, sentence and tone is the Gateway's
// (LicenseDisplayFold.Outlook.cs, CLAUDE.md rule 7). This file only places a time between the axis start and end the
// Gateway sent, draws a day line at each midnight in the viewer's own time zone, and gives each kind its colour.

const KINDS: ReadonlyArray<LicenseOutlookSpanKind> = ["room", "forecast", "at-limit", "unknown"];

/** The style hook for a span kind. A kind this build does not know is drawn as not known. */
function kindClass(kind: string): string {
  return `lic-tl-${KINDS.includes(kind as LicenseOutlookSpanKind) ? kind : "unknown"}`;
}

// The drawing's own units. The SVG scales to its box; below MIN_WIDTH pixels its box scrolls sideways instead.
const WIDTH = 820;
const LABEL_WIDTH = 170;
const LEFT = 180;
const RIGHT = 810;
const TOP = 26;
const ROW = 36;
const BAR = 16;
const MIN_WIDTH = 640;

/** Every midnight in the viewer's time zone strictly after `start` and not after `end`. Layout, not a verdict. */
export function localMidnights(start: Date, end: Date): Date[] {
  const days: Date[] = [];
  const day = new Date(start.getFullYear(), start.getMonth(), start.getDate() + 1);
  while (day.getTime() <= end.getTime()) {
    days.push(new Date(day));
    day.setDate(day.getDate() + 1);
  }
  return days;
}

/** The alert, or the calm line, above everything else. */
export function LicenseOutlookHeadline({ outlook }: { outlook: LicenseOutlook }) {
  const { headline } = outlook;
  if (!headline.isAlert) {
    return (
      <p className={`lic-outlook-calm ${toneClass(headline.tone)}`} data-testid="lic-outlook-headline">
        {headline.text}
      </p>
    );
  }
  return (
    // error-report-exempt: the Gateway's forecast that no account will have room - a verdict to read, not a failure of the page
    <div className={`lic-outlook-alert ${toneClass(headline.tone)}`} role="alert" data-testid="lic-outlook-alert">
      <strong>{headline.text}</strong>
      {headline.detail !== null && <p className="lic-outlook-alert-detail">{headline.detail}</p>}
    </div>
  );
}

function Tile({ tile, testId }: { tile: LicenseOutlookTile; testId: string }) {
  return (
    <div className="lic-tile" data-testid={testId}>
      <div className="lic-tile-label">{tile.label}</div>
      <div className={`lic-tile-value ${toneClass(tile.tone)}`}>{tile.valueText}</div>
      {tile.atUtc !== null && (
        <div className="lic-tile-at">
          <LocalTime iso={tile.atUtc} />
        </div>
      )}
      <div className="lic-tile-detail">{tile.detailText}</div>
    </div>
  );
}

/** The three tiles: when the account in use runs out, the next account with room, the next renewal. */
export function LicenseOutlookTiles({ outlook }: { outlook: LicenseOutlook }) {
  return (
    <div className="lic-tiles" data-testid="lic-outlook-tiles">
      <Tile tile={outlook.runsOut} testId="lic-tile-runs-out" />
      <Tile tile={outlook.nextRoom} testId="lic-tile-next-room" />
      <Tile tile={outlook.nextRenewal} testId="lic-tile-next-renewal" />
    </div>
  );
}

/** The seven-day timeline: a row of bars per account, the gaps where none has room, and now. */
export function LicenseOutlookChart({ outlook }: { outlook: LicenseOutlook }) {
  const start = new Date(outlook.axisStartUtc).getTime();
  const end = new Date(outlook.axisEndUtc).getTime();
  const span = end - start;
  const x = (iso: string) => {
    const at = new Date(iso).getTime();
    const share = span > 0 ? (at - start) / span : 0;
    return LEFT + Math.min(1, Math.max(0, share)) * (RIGHT - LEFT);
  };
  const rowsBottom = TOP + outlook.rows.length * ROW;
  const height = rowsBottom + 30;
  const days = localMidnights(new Date(start), new Date(end));

  return (
    <section className="lic-outlook" aria-label="Forecast - next 7 days" data-testid="lic-outlook">
      <div className="lic-outlook-title">Forecast - next 7 days</div>
      <div className="lic-tl-scroll">
        <svg
          className="lic-tl"
          viewBox={`0 0 ${WIDTH} ${height}`}
          width="100%"
          style={{ minWidth: MIN_WIDTH }}
          role="img"
          aria-label="Which account has room over the next seven days"
        >
          <defs>
            <pattern id="lic-tl-stripes" width="8" height="8" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
              <rect width="8" height="8" className="lic-tl-stripe-back" />
              <rect width="4" height="8" className="lic-tl-stripe" />
            </pattern>
            <clipPath id="lic-tl-labels">
              <rect x="0" y="0" width={LABEL_WIDTH} height={height} />
            </clipPath>
          </defs>

          {days.map((day) => {
            const at = x(day.toISOString());
            return (
              <g key={day.toISOString()} data-testid="lic-tl-day">
                <line className="lic-tl-grid" x1={at} y1={TOP - 6} x2={at} y2={rowsBottom} />
                <text className="lic-tl-day-label" x={at} y={14} textAnchor="middle">
                  {day.toLocaleDateString(undefined, { weekday: "short", day: "numeric" })}
                </text>
              </g>
            );
          })}

          {outlook.rows.map((row, index) => {
            const top = TOP + index * ROW + (ROW - BAR) / 2 - 6;
            return (
              <g key={row.accountEmail} data-testid="lic-tl-row" data-account={row.accountEmail}>
                <text className="lic-tl-row-label" x={0} y={top + BAR - 3} clipPath="url(#lic-tl-labels)">
                  <title>{row.accountEmail}</title>
                  {row.accountEmail}
                </text>
                {row.spans.map((s) => {
                  const left = x(s.startUtc);
                  const width = Math.max(0, x(s.endUtc) - left);
                  return (
                    <g key={s.startUtc} data-testid="lic-tl-span" data-kind={s.kind}>
                      <rect className={kindClass(s.kind)} x={left} y={top} width={width} height={BAR} rx={3}>
                        <title>{s.text}</title>
                      </rect>
                      {s.kind === "unknown" && width > 220 && (
                        <text className="lic-tl-span-text" x={left + width / 2} y={top + BAR - 4} textAnchor="middle">
                          {s.text}
                        </text>
                      )}
                    </g>
                  );
                })}
                {row.marks.map((mark) => {
                  const at = x(mark.atUtc);
                  return (
                    <g key={mark.atUtc} data-testid="lic-tl-mark">
                      <line className="lic-tl-mark" x1={at} y1={top - 4} x2={at} y2={top + BAR + 4} />
                      <text className="lic-tl-mark-text" x={at + 4} y={top + BAR + 12}>
                        {mark.text}
                      </text>
                    </g>
                  );
                })}
              </g>
            );
          })}

          {outlook.gaps.map((gap) => {
            const left = x(gap.startUtc);
            const width = Math.max(0, x(gap.endUtc) - left);
            return (
              <g key={gap.startUtc} data-testid="lic-tl-gap">
                <rect className="lic-tl-gap" x={left} y={TOP - 4} width={width} height={rowsBottom - TOP + 2}>
                  <title>{gap.text}</title>
                </rect>
                <text className="lic-tl-gap-text" x={left + width / 2} y={rowsBottom + 14} textAnchor="middle">
                  {gap.text}
                </text>
              </g>
            );
          })}

          <line className="lic-tl-now" x1={LEFT} y1={TOP - 6} x2={LEFT} y2={rowsBottom} />
          <text className="lic-tl-now-text" x={LEFT} y={rowsBottom + 26} textAnchor="middle">
            now
          </text>
        </svg>
      </div>
      <p className="lic-outlook-legend" data-testid="lic-outlook-legend">
        {outlook.legendText}
      </p>
    </section>
  );
}
