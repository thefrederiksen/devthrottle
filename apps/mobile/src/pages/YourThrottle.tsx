import { useEffect, useMemo, useState, type CSSProperties } from "react";
import { Link, useSearchParams } from "react-router-dom";
import {
  getThrottle,
  summarizeThrottle,
  formatPercent,
  safeTimeZone,
  throttleWindowFromSearch,
  type ThrottleData,
  type ThrottleFigure,
  type ThrottleStarters,
  type StarterKind,
  type ThrottleSummary,
} from "@devthrottle/client-core/stats/statsClient";
import { ThrottleWindowSelector } from "@devthrottle/client-core/stats/ThrottleWindowSelector";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// Your Throttle on the phone: a compact dashboard of the MAIN stats, not the whole desktop page - many
// people drive the fleet mostly from their phone, so this is a clean, glanceable view of how they work. It
// reads the same GET /stats/data feed as the Cockpit, so the numbers are the same numbers: one figure,
// computed by the Gateway from the submission ledger, with the window it covers stated on it (mission
// "Clean up Your Throttle", 2026-09-05). Leads with the two headline rings (spoken, from phone), then
// where the turns come from, then the totals and what was left out. The hourly charts, full breakdown,
// and caveats stay on the desktop page. A self-hosted Gateway answers with a sentence, and this page shows
// that sentence (rulings R1 and R6).
// THE WINDOW COMES FROM THE URL (rulings R4 and R5): `/throttle?week=2026-W35` is what the mentor report's
// link carries, `?days=N` is a choice from the shared period selector, and neither asks for the Gateway's
// default. Choosing writes the length back to the URL; the Gateway decides what it means.
// Renders immediately with a loading state, shows an explicit error banner on failure (no-fallback rule),
// and auto-refreshes so the split moves live as the user drives by voice.
// ONLY THE SESSIONS YOU STARTED (owner's ruling, 2026-09-27): the rings, the split and the totals count turns
// in sessions a person started. A new first card, "Who runs your sessions", shows every session of the window
// by who started it - you, another session, or a schedule - so the page says what it leaves out.

const REFRESH_MS = 10_000;

const RING_VOICE = "var(--accent)";
const RING_MOBILE = "#8b5cf6";

/** One color per starter group, in the Gateway's drawing order: you bright, everything else quieter. */
const STARTER_COLOR: Record<StarterKind, string> = {
  human: "#22c55e",
  agent: "color-mix(in srgb, var(--text-dim) 70%, transparent)",
  schedule: "#f59e0b",
  notRecorded: "color-mix(in srgb, var(--text-dim) 35%, transparent)",
};

const ONLY_YOURS = "Only sessions you started";

export function YourThrottle() {
  const [data, setData] = useState<ThrottleData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [searchParams, setSearchParams] = useSearchParams();
  // The window the URL asks for. Stable for one URL, so the effect below re-runs only when the URL changes.
  const request = useMemo(() => throttleWindowFromSearch(searchParams), [searchParams]);
  const choose = (days: number) => setSearchParams({ days: String(days) });

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    // A new window is a new page: back to the loading state at once, never the old window's numbers under
    // the new window's selection.
    setData(null);
    setError(null);

    const tick = async () => {
      try {
        const fresh = await getThrottle(controller.signal, request);
        if (controller.signal.aborted) return;
        setData(fresh);
        setError(null);
      } catch (err) {
        if (controller.signal.aborted) return;
        setError(gatewayErrorMessage(err));
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(() => void tick(), REFRESH_MS);
      }
    };
    void tick();

    return () => {
      controller.abort();
      if (timer !== undefined) clearTimeout(timer);
    };
  }, [request]);

  const figure: ThrottleFigure | null = data !== null && data.available ? data.throttle : null;
  const timeZone = data !== null && data.available ? safeTimeZone(data.timeZone) : "UTC";
  const summary: ThrottleSummary | null = figure === null ? null : summarizeThrottle(figure);

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Your Throttle</h1>
      </header>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {error}
        </div>
      )}

      {data === null && error === null && <div className="thr-note">Loading your throttle...</div>}

      {/* The Gateway has no figure to show here and said why, in one sentence. Rendered verbatim. */}
      {data !== null && !data.available && (
        <div className="thr-note" role="status">
          {data.reason}
        </div>
      )}

      {figure !== null && <WindowNote figure={figure} timeZone={timeZone} />}
      {figure !== null && <ThrottleWindowSelector window={figure.window} onChoose={choose} />}

      {figure !== null && figure.starters.hasData && <WhoRunsYourSessions starters={figure.starters} />}

      {figure !== null && summary !== null && !summary.hasData && (
        <div className="thr-note">
          No turn counted in this window. Send a turn from the phone, desktop, or cockpit and it will show up
          here.
        </div>
      )}

      {figure !== null && summary !== null && summary.hasData && (
        <div className="mthr">
          <div className="mthr-metrics">
            <MetricRing
              title="Voice vs typing"
              share={summary.voiceShare}
              percent={summary.voicePercent}
              color={RING_VOICE}
              primary={{ label: "Voice", count: summary.voiceTurns }}
              secondary={{ label: "Typed", count: summary.typedTurns }}
              note={ONLY_YOURS}
            />
            <MetricRing
              title="Mobile vs desktop"
              share={summary.phoneShare}
              percent={summary.phonePercent}
              color={RING_MOBILE}
              primary={{ label: "Phone", count: summary.turnsBySurface.phone }}
              // Broken out, exactly as on the Cockpit (owner's ask, 2026-09-06): the desk and the Cockpit
              // are named separately rather than added together, so the two surfaces say the same thing.
              rest={summary.surfaces.filter((s) => s.surface !== "phone" && s.turns > 0)
                .map((s) => ({ label: s.label, count: s.turns }))}
              note={ONLY_YOURS}
            />
          </div>

          <SurfaceSplitBar summary={summary} />

          <div className="mthr-stats">
            <div className="mthr-stat">
              <div className="mthr-stat-value">{summary.totalTurns.toLocaleString()}</div>
              <div className="mthr-stat-label">Your turns</div>
            </div>
            <div className="mthr-stat">
              <div className="mthr-stat-value">{figure.sessions.toLocaleString()}</div>
              <div className="mthr-stat-label">Sessions you started</div>
            </div>
          </div>

          <ExcludedNote figure={figure} />

          <p className="mthr-foot">
            The full hourly charts and breakdown live on Your Throttle in the desktop Cockpit.
          </p>
        </div>
      )}
    </div>
  );
}

/** Format an ISO instant as a short local date in the display zone, or the raw text if it does not parse. */
function localDate(iso: string, timeZone: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return iso;
  return new Intl.DateTimeFormat("en-US", { day: "numeric", month: "short", timeZone }).format(new Date(t));
}

// Which stretch of time the numbers describe: the Gateway's label and dates, rendered in the display zone.
function WindowNote({ figure, timeZone }: { figure: ThrottleFigure; timeZone: string }) {
  const { window: w, ledger } = figure;
  const recordStartsLate =
    ledger.earliestUtc !== null && w.fromUtc !== "" && Date.parse(ledger.earliestUtc) > Date.parse(w.fromUtc);
  return (
    <p className="thr-note" data-testid="mthr-window">
      <b>{w.label}</b>: {localDate(w.fromUtc, timeZone)} to {localDate(w.toUtc, timeZone)}, in submitted
      turns.
      {recordStartsLate && ledger.earliestUtc !== null && (
        <> Your record begins {localDate(ledger.earliestUtc, timeZone)}.</>
      )}
    </p>
  );
}

// What the definition left out, beside the share (rulings R7 and R17). The counts are the Gateway's.
function ExcludedNote({ figure }: { figure: ThrottleFigure }) {
  const { excluded, agentDrivenTurns } = figure;
  if (excluded.unresolved === 0 && agentDrivenTurns === 0) return null;
  return (
    <p className="thr-note" data-testid="mthr-excluded">
      {excluded.unresolved > 0 && (
        <>
          {excluded.unresolved.toLocaleString()} submission{excluded.unresolved === 1 ? "" : "s"} of yours could
          not be placed on a surface and {excluded.unresolved === 1 ? "is" : "are"} outside every number here.{" "}
        </>
      )}
      {agentDrivenTurns > 0 && (
        <>
          {agentDrivenTurns.toLocaleString()} turn{agentDrivenTurns === 1 ? " was" : "s were"} other sessions
          prompting yours; those are never in your share.
        </>
      )}
    </p>
  );
}

// WHO RUNS YOUR SESSIONS: a ring of the window's sessions by who started them (the arc for each group is the
// Gateway's session share, the number in the middle its rounded share for you), the groups named with their
// session counts, and a bar of the turns that went into each. Groups with no session are left out of the
// legend; "Started by you" is always shown. Every number is a served field.
function WhoRunsYourSessions({ starters }: { starters: ThrottleStarters }) {
  const R = 42;
  const C = 2 * Math.PI * R;
  const shown = starters.groups.filter((g) => g.kind === "human" || g.sessions > 0);
  let offset = 0;
  const arcs = starters.groups.map((g) => {
    const length = (g.sessionShare ?? 0) * C;
    const arc = { kind: g.kind, length, offset };
    offset += length;
    return arc;
  });
  return (
    <section className="mthr-who" aria-label="Who runs your sessions" data-testid="mthr-who">
      <div className="mthr-who-top">
        <div
          className="mthr-who-ring"
          role="img"
          aria-label={`Who runs your sessions: you started ${formatPercent(starters.humanPercent)} (${starters.groups[0].sessions} of ${starters.sessions} sessions)`}
        >
          <svg viewBox="0 0 100 100" className="mthr-who-svg">
            <circle className="mthr-ring-track" cx="50" cy="50" r={R} />
            {arcs.filter((a) => a.length > 0).map((a) => (
              <circle
                key={a.kind}
                className="mthr-who-arc"
                cx="50"
                cy="50"
                r={R}
                style={{ stroke: STARTER_COLOR[a.kind], strokeDasharray: `${a.length} ${C}`, strokeDashoffset: -a.offset }}
              />
            ))}
          </svg>
          <div className="mthr-who-pct">{formatPercent(starters.humanPercent)}</div>
        </div>
        <div className="mthr-metric-body">
          <div className="mthr-metric-title">Who runs your sessions</div>
          <div className="mthr-metric-legend">
            {shown.map((g) => (
              <span key={g.kind} className="mthr-who-leg" data-kind={g.kind}>
                <span className="mthr-dot" style={{ background: STARTER_COLOR[g.kind] }} />
                {g.label} <b>{g.sessions.toLocaleString()}</b>
              </span>
            ))}
          </div>
        </div>
      </div>
      <div className="mthr-who-turns-title">Turns in them</div>
      <div className="mthr-split-bar">
        {starters.groups.filter((g) => g.turns > 0).map((g) => (
          <div
            key={g.kind}
            className="mthr-who-seg"
            style={{ flexGrow: g.turns, background: STARTER_COLOR[g.kind] }}
            title={`${g.label}: ${g.turns} turns`}
          />
        ))}
      </div>
      <div className="mthr-split-legend">
        {shown.map((g) => (
          <span key={g.kind} className="mthr-who-leg">
            <span className="mthr-dot" style={{ background: STARTER_COLOR[g.kind] }} />
            {g.kind === "human" ? "Yours" : g.label} <b>{g.turns.toLocaleString()}</b>
          </span>
        ))}
      </div>
    </section>
  );
}

// One metric as a compact card: a donut ring (the share) on the left, the title and the two named counts
// on the right. role="img" + aria-label so the number is announced; the counts carry identity so it is
// never color-alone.
// The ring draws the Gateway's share as its arc and prints the Gateway's rounded percent as its number
// (final inspection finding F-01). It rounds nothing: the number it prints is the number the Cockpit and the
// mentor report print, because all three read the same headline field.
function MetricRing({
  title,
  share,
  percent,
  color,
  primary,
  secondary,
  rest,
  note,
}: {
  title: string;
  share: number | null;
  percent: number | null;
  color: string;
  primary: { label: string; count: number };
  /** The single remainder, for a ring whose other side is one thing ("Typed"). */
  secondary?: { label: string; count: number };
  /** The remainder broken out, each surface named with its own count. */
  rest?: { label: string; count: number }[];
  note?: string;
}) {
  const R = 42;
  const C = 2 * Math.PI * R;
  const filled = share === null ? 0 : share * C;
  const pctText = formatPercent(percent);
  const others = rest ?? (secondary === undefined ? [] : [secondary]);
  const othersTotal = others.reduce((t, e) => t + e.count, 0);

  return (
    <section className="mthr-metric">
      <div
        className="mthr-ring"
        role="img"
        aria-label={`${title}: ${primary.label} ${pctText} (${primary.count} of ${primary.count + othersTotal} turns)`}
      >
        <svg viewBox="0 0 100 100" className="mthr-ring-svg" style={{ ["--mthr-arc" as string]: color } as CSSProperties}>
          <circle className="mthr-ring-track" cx="50" cy="50" r={R} />
          <circle className="mthr-ring-arc" cx="50" cy="50" r={R} style={{ strokeDasharray: `${filled} ${C}` }} />
        </svg>
        <div className="mthr-ring-pct">{pctText}</div>
      </div>
      <div className="mthr-metric-body">
        <div className="mthr-metric-title">{title}</div>
        <div className="mthr-metric-legend">
          <span className="mthr-leg">
            <span className="mthr-dot" style={{ background: color }} />
            {primary.label} <b>{primary.count.toLocaleString()}</b>
          </span>
          {others.map((entry) => (
            <span key={entry.label} className="mthr-leg">
              <span className="mthr-dot mthr-dot-muted" />
              {entry.label} <b>{entry.count.toLocaleString()}</b>
            </span>
          ))}
        </div>
        {note !== undefined && <div className="mthr-metric-note">{note}</div>}
      </div>
    </section>
  );
}

// A single horizontal stacked bar of turns by surface (largest first, brightest first), plus a legend -
// the compact "where you drive from" the phone version keeps.
// Every width, percentage and label here is the Gateway's own headline surface entry (finding F-01).
function SurfaceSplitBar({ summary }: { summary: ThrottleSummary }) {
  const segments = summary.surfaces.filter((seg) => seg.turns > 0).sort((a, b) => b.turns - a.turns);
  const fill = (i: number) => {
    const o = [100, 66, 42, 26][Math.min(i, 3)];
    return `color-mix(in srgb, var(--accent) ${o}%, transparent)`;
  };

  return (
    <section className="mthr-split" aria-label="Where you drive from">
      <div className="mthr-split-title">Where you drive from</div>
      <div className="mthr-metric-note mthr-split-note">{ONLY_YOURS}</div>
      <div className="mthr-split-bar">
        {segments.map((seg, i) => {
          const width = seg.share === null ? 0 : seg.share * 100;
          return (
            <div
              key={seg.surface}
              className="mthr-split-seg"
              style={{ width: `${width}%`, background: fill(i) }}
              title={`${seg.label}: ${seg.turns} (${formatPercent(seg.percent)})`}
            />
          );
        })}
      </div>
      <div className="mthr-split-legend">
        {segments.map((seg, i) => (
          <span key={seg.surface} className="mthr-leg">
            <span className="mthr-dot" style={{ background: fill(i) }} />
            {seg.label} <b>{seg.turns.toLocaleString()}</b>
          </span>
        ))}
      </div>
    </section>
  );
}
