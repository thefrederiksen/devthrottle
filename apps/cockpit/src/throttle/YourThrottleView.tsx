import { useEffect, useMemo, useState, type CSSProperties, type ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import {
  getThrottle,
  summarizeThrottle,
  formatPercent,
  last24HourKeys,
  hourlyChartEnd,
  throttleWindowFromSearch,
  windowSeries,
  emptyInputHour,
  emptyConcurrencyHour,
  localHourLabel,
  safeTimeZone,
  MODALITY_LABEL,
  SURFACE_LABEL,
  type ThrottleData,
  type ThrottleServed,
  type ThrottleFigure,
  type ThrottleStarters,
  type StarterKind,
  type ThrottleSummary,
  type ConcurrencyHour,
  type InputHour,
} from "@devthrottle/client-core/stats/statsClient";
import { ThrottleWindowSelector } from "@devthrottle/client-core/stats/ThrottleWindowSelector";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { ReposTab } from "./ReposTab";
import { AgentsTab } from "./AgentsTab";
import { TABS, DEFAULT_TAB, isThrottleTab, type ThrottleTab } from "./throttleTabs";

// The "Your Throttle" page: the in-Cockpit view of how the owner drives the fleet - spoken vs typed, and
// from phone vs desktop vs cockpit - over the GET /stats/data feed. Responsive (CodingStyle.md): renders
// immediately with a loading state, loads asynchronously, and on a load failure shows an explicit error
// banner (no-fallback rule). Auto-refreshes so the split visibly moves as the owner keeps driving.
//
// EVERY NUMBER HERE IS THE GATEWAY'S (mission "Clean up Your Throttle", 2026-09-05). The feed carries one
// figure computed by one definition over the submission ledger, with the window it covers and the
// population it left out stated on it. This page lays that out; it derives nothing of its own beyond share
// arithmetic over the counts it was given. A self-hosted Gateway answers with a sentence instead of a
// figure, and this page shows that sentence (rulings R1 and R6) rather than an empty dashboard.
//
// THE WINDOW COMES FROM THE URL (rulings R4 and R5). `?week=2026-W35` is what the mentor report's link
// carries, so following it asks the Gateway for exactly that week and shows the Gateway's label for it;
// `?days=N` is a selector choice; neither asks for the Gateway's default (a rolling seven days). Choosing a
// length writes it back to the URL, and the Gateway decides what every one of those means - this page never
// computes a date.
//
// The page is TABBED so the two questions the owner actually cares about are not buried under supporting
// tables (owner ask, 2026-07-13): Overview leads with the two headline percentages as big rings - how
// much do I speak vs type, and how much do I drive from my phone; Activity holds the time charts;
// Breakdown holds the supporting tables, the definition, and the honesty caveats; Repos and Agents hold
// the private splits. All five tabs read the one /stats/data snapshot this page already polls.

const REFRESH_MS = 10_000;

// The tab is sticky per browser so returning to the page lands on whatever the owner last read.
const TAB_STORAGE_KEY = "cockpit.throttleTab";

function initialTab(): ThrottleTab {
  try {
    const saved = window.localStorage.getItem(TAB_STORAGE_KEY);
    if (saved !== null && isThrottleTab(saved)) return saved;
  } catch {
    /* storage unavailable (private mode) - fall through to the default */
  }
  return DEFAULT_TAB;
}

export function YourThrottleView() {
  const [data, setData] = useState<ThrottleData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTabState] = useState<ThrottleTab>(initialTab);
  const [searchParams, setSearchParams] = useSearchParams();
  // The window the URL asks for. Stable for one URL, so the effect below re-runs only when the URL changes.
  const request = useMemo(() => throttleWindowFromSearch(searchParams), [searchParams]);
  const choose = (days: number) => setSearchParams({ days: String(days) });

  const setTab = (next: ThrottleTab) => {
    setTabState(next);
    try {
      window.localStorage.setItem(TAB_STORAGE_KEY, next);
    } catch {
      /* storage unavailable - the selection still applies this session */
    }
  };

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    // A new window is a new page: back to the loading state at once (responsive UI), never the old
    // window's numbers under the new window's selection.
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

  const served: ThrottleServed | null = data !== null && data.available ? data : null;
  const summary: ThrottleSummary | null = useMemo(
    () => (served === null ? null : summarizeThrottle(served.throttle)),
    [served],
  );

  return (
    <div className="page thr">
      <div className="page-head">
        <h1>Your Throttle</h1>
        <p className="page-sub">
          How you are driving development: spoken vs typed, and from phone vs desktop vs cockpit. A turn
          is one submitted message; one spoken utterance and one typed message each count as one turn.
        </p>
        {served !== null && <WindowStatement figure={served.throttle} timeZone={served.timeZone} />}
        {served !== null && <ThrottleWindowSelector window={served.throttle.window} onChoose={choose} />}
      </div>

      {error !== null && (
        <div className="thr-banner" role="alert">
          {error}
        </div>
      )}

      {data === null && error === null && <div className="thr-loading">Loading your throttle...</div>}

      {/* The Gateway has no figure to show here and said why, in one sentence. Rendered verbatim. */}
      {data !== null && !data.available && (
        <div className="thr-empty" role="status">
          {data.reason}
        </div>
      )}

      {served !== null && summary !== null && (
        <>
          <div className="thr-tabs" role="tablist" aria-label="Your Throttle sections">
            {TABS.map((t) => (
              <button
                key={t.key}
                type="button"
                role="tab"
                aria-selected={tab === t.key}
                className={tab === t.key ? "thr-tab active" : "thr-tab"}
                onClick={() => setTab(t.key)}
              >
                {t.label}
              </button>
            ))}
          </div>

          {tab === "overview" && <OverviewTab summary={summary} data={served} />}
          {tab === "activity" && <ActivityTab data={served} />}
          {tab === "breakdown" && <BreakdownTab summary={summary} data={served} />}
          {tab === "repos" && <ReposTab figure={served.throttle} />}
          {tab === "agents" && <AgentsTab figure={served.throttle} />}
        </>
      )}
    </div>
  );
}

// ---- The window, stated on the page ---------------------------------------------------------------

/** Format an ISO instant as a short local date and time in the display zone, or the raw text if it does
 *  not parse - never a made-up date. */
function localStamp(iso: string, timeZone: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return iso;
  return new Intl.DateTimeFormat("en-US", {
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
    timeZone,
  }).format(new Date(t));
}

// Which stretch of time every number on this page describes. The label is the Gateway's; the dates are
// the Gateway's, rendered in the display zone. When the ledger's record begins after the window opens,
// that is said too, so a quiet first week is never read as a quiet week.
function WindowStatement({ figure, timeZone }: { figure: ThrottleFigure; timeZone: string }) {
  const zone = safeTimeZone(timeZone);
  const { window: w, ledger } = figure;
  const recordStartsLate =
    ledger.earliestUtc !== null &&
    w.fromUtc !== "" &&
    Date.parse(ledger.earliestUtc) > Date.parse(w.fromUtc);
  return (
    <p className="page-sub" data-testid="thr-window">
      Showing <b>{w.label}</b>: {localStamp(w.fromUtc, zone)} to {localStamp(w.toUtc, zone)} ({friendlyZone(zone)}
      ). Counted in {figure.unit} from the submission ledger, which keeps {ledger.retentionDays} days.
      {recordStartsLate && ledger.earliestUtc !== null && (
        <> Your record begins {localStamp(ledger.earliestUtc, zone)}; there is nothing before that.</>
      )}
      {ledger.earliestUtc === null && <> The ledger holds no submissions for you yet.</>}
    </p>
  );
}

// ---- Overview: the landing dashboard - the two headline percentages first, big and clear ------------

/** One color per starter group, in the Gateway's drawing order: you bright, everything else quieter. */
const STARTER_COLOR: Record<StarterKind, string> = {
  human: "#22c55e",
  agent: "color-mix(in srgb, var(--text-dim) 70%, transparent)",
  schedule: "#f59e0b",
  notRecorded: "color-mix(in srgb, var(--text-dim) 35%, transparent)",
};

const ONLY_YOURS = "Only sessions you started";

function OverviewTab({ summary, data }: { summary: ThrottleSummary; data: ThrottleServed }) {
  const figure = data.throttle;
  // Who runs your sessions (owner's ruling, 2026-09-27) leads the page, and is shown even when nothing of
  // yours was counted: a window where only other sessions worked is exactly what it exists to show.
  const who = figure.starters.hasData ? <WhoRunsYourSessions starters={figure.starters} /> : null;

  if (!summary.hasData) {
    return (
      <>
        {who}
        <div className="thr-empty">
          No turn counted in this window. Send a turn from the composer, dictation, phone, or cockpit and
          your throttle will appear here.
        </div>
        <ExcludedNote figure={figure} />
      </>
    );
  }

  const peakLive = data.concurrency?.live.allTimeMax ?? 0;

  return (
    <>
      {who}

      {/* The two hero rings: the questions the owner actually asks - how much do I speak, and how much do
          I drive from my phone. Each ring is its own metric, so each fills with its own accent against a
          muted remainder; the center is the headline. */}
      <div className="thr-heroes">
        <HeroRing
          title="Voice vs typing"
          share={summary.voiceShare}
          percent={summary.voicePercent}
          accentVar="--thr-voice"
          centerCaption="spoken"
          primary={{ label: "Voice", count: summary.voiceTurns }}
          secondary={{ label: "Typed", count: summary.typedTurns }}
          note={ONLY_YOURS}
        />
        <HeroRing
          title="Mobile vs desktop"
          share={summary.phoneShare}
          percent={summary.phonePercent}
          accentVar="--thr-mobile"
          centerCaption="from phone"
          primary={{ label: "Phone", count: summary.turnsBySurface.phone }}
          // The other side of this ring, BROKEN OUT (owner's ask, 2026-09-06). It used to read
          // "Desktop + Cockpit 257", which answers the ring's question and hides the one underneath it:
          // how much of the desk is the Cockpit. Every surface that has a turn is named on its own, so
          // the parts still add to the ring's other side and nothing is folded away.
          rest={summary.surfaces.filter((s) => s.surface !== "phone" && s.turns > 0)
            .map((s) => ({ label: s.label, count: s.turns }))}
          note={ONLY_YOURS}
        />
      </div>

      {/* Where the turns come from, in one glance - the supporting detail behind the mobile ring. */}
      <div className="thr-panel">
        <div className="thr-panel-head">
          <h2>Where you drive from</h2>
          <span className="thr-panel-sub">{summary.totalTurns} turns across every surface</span>
        </div>
        <p className="thr-scope">{ONLY_YOURS}</p>
        <SurfaceSplitBar summary={summary} />
      </div>

      {/* Supporting headline numbers. */}
      <div className="thr-stats">
        <StatTile value={summary.totalTurns.toLocaleString()} label="Your turns" sub={figure.window.label.toLowerCase()} />
        <StatTile value={figure.sessions.toLocaleString()} label="Sessions you started" />
        <StatTile value={String(peakLive)} label="Peak sessions at once" sub="all-time" />
      </div>

      <ExcludedNote figure={figure} />
    </>
  );
}

// WHO RUNS YOUR SESSIONS: every session of the window by who started it. The ring's arcs are the Gateway's
// session shares and its center the Gateway's rounded share for you; the legend names each group with its
// sessions, and the bar under it splits the window's turns the same way. Groups with no session are left out
// of the legends; "Started by you" is always shown. Nothing here divides.
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
  const human = starters.groups.find((g) => g.kind === "human");
  return (
    <div className="thr-panel thr-who" data-testid="thr-who">
      <div className="thr-panel-head">
        <h2>Who runs your sessions</h2>
        <span className="thr-who-sub">{starters.sessions.toLocaleString()} sessions worked in this window</span>
      </div>
      <div className="thr-who-body">
        <div
          className="thr-who-ring"
          role="img"
          aria-label={`Who runs your sessions: you started ${formatPercent(starters.humanPercent)} (${human?.sessions ?? 0} of ${starters.sessions} sessions)`}
        >
          <svg viewBox="0 0 100 100" className="thr-ring-svg">
            <circle className="thr-ring-track" cx="50" cy="50" r={R} />
            {arcs.filter((a) => a.length > 0).map((a) => (
              <circle
                key={a.kind}
                className="thr-who-arc"
                cx="50"
                cy="50"
                r={R}
                style={{ stroke: STARTER_COLOR[a.kind], strokeDasharray: `${a.length} ${C}`, strokeDashoffset: -a.offset }}
              />
            ))}
          </svg>
          <div className="thr-ring-center">
            <div className="thr-who-pct">{formatPercent(starters.humanPercent)}</div>
            <div className="thr-ring-cap">started by you</div>
          </div>
        </div>
        <div className="thr-who-detail">
          <div className="thr-who-legend">
            {shown.map((g) => (
              <span key={g.kind} className="thr-who-leg" data-kind={g.kind}>
                <span className="thr-dot" style={{ background: STARTER_COLOR[g.kind] }} />
                {g.label}
                <b>{g.sessions.toLocaleString()}</b>
                <span className="thr-who-pctsmall">{formatPercent(g.sessionPercent)}</span>
              </span>
            ))}
          </div>
          <div className="thr-who-turns-title">Turns in them</div>
          <div className="thr-who-bar" role="img" aria-label="Turns by who started the session">
            {starters.groups.filter((g) => g.turns > 0).map((g) => (
              <div
                key={g.kind}
                className="thr-who-seg"
                style={{ flexGrow: g.turns, background: STARTER_COLOR[g.kind] }}
                title={`${g.label}: ${g.turns} turns`}
              />
            ))}
          </div>
          <div className="thr-who-legend">
            {shown.map((g) => (
              <span key={g.kind} className="thr-who-leg">
                <span className="thr-dot" style={{ background: STARTER_COLOR[g.kind] }} />
                {g.kind === "human" ? "Yours" : g.label}
                <b>{g.turns.toLocaleString()}</b>
              </span>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

// What the definition left out, said beside the share (rulings R7 and R17): a share computed over a subset
// publishes the size of the subset. The counts are the Gateway's; this only puts words around them.
function ExcludedNote({ figure }: { figure: ThrottleFigure }) {
  const { excluded, agentDrivenTurns } = figure;
  if (excluded.unresolved === 0 && agentDrivenTurns === 0 && excluded.notStartedByYou === 0) return null;
  return (
    <p className="thr-muted" data-testid="thr-excluded">
      {excluded.unresolved > 0 && (
        <>
          <b>{excluded.unresolved.toLocaleString()}</b> submission{excluded.unresolved === 1 ? "" : "s"} of
          yours could not be placed on a surface and {excluded.unresolved === 1 ? "is" : "are"} outside every
          number on this page.{" "}
        </>
      )}
      {agentDrivenTurns > 0 && (
        <>
          <b>{agentDrivenTurns.toLocaleString()}</b> turn{agentDrivenTurns === 1 ? " was" : "s were"} other
          sessions prompting yours - the fleet driving itself. Those are on the Agents tab, never in your
          share.{" "}
        </>
      )}
      {excluded.notStartedByYou > 0 && (
        <>
          <b>{excluded.notStartedByYou.toLocaleString()}</b> turn{excluded.notStartedByYou === 1 ? " went" : "s went"} into
          sessions you did not start and {excluded.notStartedByYou === 1 ? "is" : "are"} outside every number on
          this page.
        </>
      )}
    </p>
  );
}

// A big donut ring: the share as an arc, the headline percent in the center, the two sides named with
// their counts below. role="img" + aria-label so the number is announced; the legend carries identity so
// it is never color-alone.
// The ring draws the Gateway's share as its arc and prints the Gateway's rounded percent as its number
// (final inspection finding F-01). It rounds nothing: the number it prints is the number the mentor report
// prints, because both read the same headline field.
function HeroRing({
  title,
  share,
  percent,
  accentVar,
  centerCaption,
  primary,
  secondary,
  rest,
  note,
}: {
  title: string;
  share: number | null;
  percent: number | null;
  accentVar: string;
  centerCaption: string;
  primary: { label: string; count: number };
  /** The single remainder, for a ring whose other side is one thing ("Typed"). */
  secondary?: { label: string; count: number };
  /** The remainder BROKEN OUT, for a ring whose other side is several things - each named with its own
   *  count so the reader sees what the ring's one number is hiding. Exactly one of these two is given. */
  rest?: { label: string; count: number }[];
  note?: string;
}) {
  const R = 42;
  const C = 2 * Math.PI * R;
  const filled = share === null ? 0 : share * C;
  const pctText = formatPercent(percent);
  // The ring's other side, as the label a screen reader hears: one thing, or the parts named.
  const others = rest ?? (secondary === undefined ? [] : [secondary]);
  const othersTotal = others.reduce((t, e) => t + e.count, 0);

  return (
    <section className="thr-hero">
      <div className="thr-hero-title">{title}</div>
      <div
        className="thr-ring"
        role="img"
        aria-label={`${title}: ${primary.label} ${pctText} (${primary.count} of ${primary.count + othersTotal} turns)`}
      >
        <svg
          viewBox="0 0 100 100"
          className="thr-ring-svg"
          style={{ "--thr-arc": `var(${accentVar})` } as CSSProperties}
        >
          <circle className="thr-ring-track" cx="50" cy="50" r={R} />
          <circle
            className="thr-ring-arc"
            cx="50"
            cy="50"
            r={R}
            style={{ strokeDasharray: `${filled} ${C}` }}
          />
        </svg>
        <div className="thr-ring-center">
          <div className="thr-ring-pct">{pctText}</div>
          <div className="thr-ring-cap">{centerCaption}</div>
        </div>
      </div>
      <div className="thr-hero-legend">
        <span className="thr-hero-leg">
          <span className="thr-dot" style={{ background: `var(${accentVar})` }} />
          {primary.label}
          <b>{primary.count.toLocaleString()}</b>
        </span>
        {(rest ?? (secondary === undefined ? [] : [secondary])).map((entry) => (
          <span key={entry.label} className="thr-hero-leg">
            <span className="thr-dot thr-dot-muted" />
            {entry.label}
            <b>{entry.count.toLocaleString()}</b>
          </span>
        ))}
      </div>
      {note !== undefined && <div className="thr-hero-note">{note}</div>}
    </section>
  );
}

// A single horizontal stacked bar of turns by surface, each present segment labeled with its share, plus
// a legend beneath. One accent hue at descending opacity per surface (a magnitude ramp, not arbitrary
// categorical colors) so the bar reads as "one measure split by where".
// Every width, percentage and label here is the Gateway's own headline surface entry (finding F-01).
function SurfaceSplitBar({ summary }: { summary: ThrottleSummary }) {
  // Largest surface first, so the brightest accent step lands on the surface you drive from most (the
  // magnitude ramp reads big -> small in both width and color).
  const segments = summary.surfaces.filter((seg) => seg.turns > 0).sort((a, b) => b.turns - a.turns);

  return (
    <div className="thr-split">
      <div className="thr-split-bar" role="img" aria-label="Turns by surface">
        {segments.map((seg, i) => {
          const width = seg.share === null ? 0 : seg.share * 100;
          return (
            <div
              key={seg.surface}
              className="thr-split-seg"
              style={{ width: `${width}%`, background: surfaceFill(i) }}
              title={`${seg.label}: ${seg.turns} turns (${formatPercent(seg.percent)})`}
            >
              {width >= 8 && <span className="thr-split-lbl">{formatPercent(seg.percent)}</span>}
            </div>
          );
        })}
      </div>
      <div className="thr-split-legend">
        {segments.map((seg, i) => (
          <span key={seg.surface} className="thr-split-leg">
            <span className="thr-dot" style={{ background: surfaceFill(i) }} />
            {seg.label}
            <b>{seg.turns.toLocaleString()}</b>
          </span>
        ))}
      </div>
    </div>
  );
}

// The accent, stepped down in opacity for each successive surface segment - a one-hue magnitude ramp.
function surfaceFill(index: number): string {
  const opacities = [100, 66, 42, 26];
  const o = opacities[Math.min(index, opacities.length - 1)];
  return `color-mix(in srgb, var(--accent) ${o}%, transparent)`;
}

function StatTile({ value, label, sub }: { value: string; label: string; sub?: string }) {
  return (
    <div className="thr-stat">
      <div className="thr-stat-value">{value}</div>
      <div className="thr-stat-label">
        {label}
        {sub !== undefined && <span className="thr-stat-sub"> {sub}</span>}
      </div>
    </div>
  );
}

// ---- Activity: the time charts, now full-width and tall enough to read ------------------------------

function ActivityTab({ data }: { data: ThrottleServed }) {
  const hourlyTurns = data.throttle.hourlyTurns;
  const hasTurns = hourlyTurns.length > 0;
  const hasConcurrency = data.concurrency !== null && data.concurrency.hourly.length > 0;

  if (!hasTurns && !hasConcurrency) {
    return <div className="thr-empty">No hourly activity recorded in this window yet.</div>;
  }

  // Both charts render the SAME canonical 24-hour window, so they line up exactly regardless of which
  // hours each series happens to have data for. The 24 hours END AT THE SERVED WINDOW'S END (clamped to
  // now when the window is still open), not at the clock: with a past week selected, a chart of the last
  // 24 clock hours would draw nothing and read as broken. The heading says which 24 hours these are.
  // Labels are formatted in the configured display time zone, so the axis reads in local time, not UTC.
  const timeZone = safeTimeZone(data.timeZone);
  const end = hourlyChartEnd(data.throttle.window, new Date());
  const keys = last24HourKeys(end);
  const turnsWindow = windowSeries(hourlyTurns, keys, emptyInputHour);
  const concurrencyWindow = windowSeries(data.concurrency?.hourly ?? [], keys, emptyConcurrencyHour);
  const zoneLabel = friendlyZone(timeZone);
  const endStamp = localStamp(end.toISOString(), timeZone);

  return (
    <>
      {hasTurns && (
        <div className="thr-panel">
          <div className="thr-panel-head">
            <h2>Turns per hour (24 hours to {endStamp})</h2>
            <span className="thr-panel-sub">
              Your working day: how many turns you submitted each hour ({zoneLabel}), voice over typed, in
              the 24 hours ending {endStamp}. Empty hours are when you were away.
            </span>
          </div>
          <TurnsPerHourChart hourly={turnsWindow} timeZone={timeZone} />
        </div>
      )}

      {hasConcurrency && (
        <div className="thr-panel">
          <div className="thr-panel-head">
            <h2>Sessions per hour (24 hours to {endStamp})</h2>
            <span className="thr-panel-sub">
              Peak concurrent sessions in each hour ({zoneLabel}) in the 24 hours ending {endStamp}. The bar
              is loaded/running; the darker portion is actively working. Hover a bar for that hour&apos;s
              distinct sessions and machines.
            </span>
          </div>
          <ConcurrencyChart hourly={concurrencyWindow} timeZone={timeZone} />
        </div>
      )}
    </>
  );
}

// A short, human label for the display zone shown in the chart captions: the IANA id's last segment with
// underscores turned to spaces (e.g. "America/New_York" -> "New York"), so the caption reads plainly.
function friendlyZone(timeZone: string): string {
  const seg = timeZone.split("/").pop() ?? timeZone;
  return seg.replace(/_/g, " ");
}

// One column of a bar chart: the bar node itself (its height is a percent of the plot), plus its x-axis
// label and hover title.
interface BarColumn {
  key: string;
  xlabel: string;
  title: string;
  bar: ReactNode;
}

// A "nice" linear scale from 0 to a rounded ceiling >= max, with evenly spaced integer ticks (~4-5), so
// the y-axis reads in round numbers and the top bar never touches the frame. Counts only, so the step is
// clamped to a whole number.
function niceScale(max: number): { niceMax: number; ticks: number[] } {
  if (max <= 0) return { niceMax: 1, ticks: [0, 1] };
  const rawStep = max / 5;
  const pow = Math.pow(10, Math.floor(Math.log10(rawStep)));
  const n = rawStep / pow;
  const stepMul = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
  const step = Math.max(1, Math.round(stepMul * pow));
  const niceMax = Math.ceil(max / step) * step;
  const ticks: number[] = [];
  for (let t = 0; t <= niceMax; t += step) ticks.push(t);
  return { niceMax, ticks };
}

// The shared chart frame: a left y-axis of tick labels, recessive horizontal gridlines at those ticks,
// the bars scaled to the SAME nice ceiling as the gridlines (so a bar top lands on a gridline you can
// read), and an aligned x-axis row beneath. role="img" with a caption; the legend below carries series
// identity so it is never color-alone.
function BarChart({
  ariaLabel,
  ticks,
  niceMax,
  columns,
  legend,
}: {
  ariaLabel: string;
  ticks: number[];
  niceMax: number;
  columns: BarColumn[];
  legend: ReactNode;
}) {
  return (
    <>
      <div className="thr-chartframe tall" role="img" aria-label={ariaLabel}>
        <div className="thr-plotrow">
          <div className="thr-yaxis">
            {ticks.map((t) => (
              <div key={t} className="thr-ytick" style={{ bottom: `${(t / niceMax) * 100}%` }}>
                {t.toLocaleString()}
              </div>
            ))}
          </div>
          <div className="thr-plot">
            {ticks.map((t) => (
              <div key={t} className="thr-gridline" style={{ bottom: `${(t / niceMax) * 100}%` }} />
            ))}
            {columns.map((c) => (
              <div key={c.key} className="thr-col" title={c.title}>
                {c.bar}
              </div>
            ))}
          </div>
        </div>
        <div className="thr-xrow">
          {columns.map((c) => (
            <div key={c.key} className="thr-xlabel">
              {c.xlabel}
            </div>
          ))}
        </div>
      </div>
      <div className="thr-legend">{legend}</div>
    </>
  );
}

// A 24-hour chart of turns submitted per hour - the "working day" shape. Each bar is the total turns that
// hour, split voice (accent) over typed (muted), scaled against a readable y-axis. The hourly array is the
// already-windowed 24 hours; labels are the local hour in `timeZone`.
function TurnsPerHourChart({ hourly, timeZone }: { hourly: InputHour[]; timeZone: string }) {
  const { niceMax, ticks } = niceScale(Math.max(...hourly.map((h) => h.turns), 0));
  const columns: BarColumn[] = hourly.map((h, i) => {
    const heightPct = (h.turns / niceMax) * 100;
    // The split is the Gateway's own per-hour share (fix-round finding F-01); the chart divides nothing.
    const voicePortion = h.voiceShare === null ? 0 : h.voiceShare * 100;
    const typedPortion = h.typedShare === null ? 0 : h.typedShare * 100;
    const label = localHourLabel(h.hour, timeZone);
    return {
      key: h.hour,
      xlabel: i % 3 === 0 ? label : "",
      title: `${label}:00 ${timeZone} - ${h.turns} turns (${h.voiceTurns} voice, ${h.typedTurns} typed)`,
      bar: (
        <div className="thr-bar" style={{ height: `${heightPct}%` }}>
          <div className="thr-seg thr-seg-typed" style={{ height: `${typedPortion}%` }} />
          <div className="thr-seg thr-seg-voice" style={{ height: `${voicePortion}%` }} />
        </div>
      ),
    };
  });
  return (
    <BarChart
      ariaLabel={`Turns submitted per hour for the last ${hourly.length} hours`}
      ticks={ticks}
      niceMax={niceMax}
      columns={columns}
      legend={
        <>
          <span className="thr-legend-item">
            <span className="thr-swatch thr-swatch-voice" /> Voice
          </span>
          <span className="thr-legend-item">
            <span className="thr-swatch thr-swatch-typed" /> Typed
          </span>
        </>
      }
    />
  );
}

// A 24-hour chart of the hourly peak concurrent sessions. Each bar is the max loaded/running that hour;
// the darker inner portion is the max actively-working, scaled against a readable y-axis. The hourly array
// is the already-windowed 24 hours; labels are the local hour in `timeZone`.
function ConcurrencyChart({ hourly, timeZone }: { hourly: ConcurrencyHour[]; timeZone: string }) {
  const { niceMax, ticks } = niceScale(Math.max(...hourly.map((h) => h.maxLive), 0));
  const columns: BarColumn[] = hourly.map((h, i) => {
    const heightPct = (h.maxLive / niceMax) * 100;
    const workPct = h.maxLive > 0 ? (h.maxWorking / h.maxLive) * 100 : 0;
    const label = localHourLabel(h.hour, timeZone);
    return {
      key: h.hour,
      xlabel: i % 3 === 0 ? label : "",
      title: `${label}:00 ${timeZone} - ${h.maxLive} loaded, ${h.maxWorking} working, ${h.sessions} distinct sessions, ${h.machines} machine(s)`,
      bar: (
        <div className="thr-bar thr-bar-live" style={{ height: `${heightPct}%` }}>
          <div className="thr-seg thr-seg-work" style={{ height: `${workPct}%` }} />
        </div>
      ),
    };
  });
  return (
    <BarChart
      ariaLabel={`Peak concurrent sessions per hour for the last ${hourly.length} hours`}
      ticks={ticks}
      niceMax={niceMax}
      columns={columns}
      legend={
        <>
          <span className="thr-legend-item">
            <span className="thr-swatch thr-swatch-live" /> Loaded / running
          </span>
          <span className="thr-legend-item">
            <span className="thr-swatch thr-swatch-voice" /> Actively working
          </span>
        </>
      }
    />
  );
}

// ---- Breakdown: the supporting tables, the definition, and the honesty caveats ---------------------

function BreakdownTab({ summary, data }: { summary: ThrottleSummary; data: ThrottleServed }) {
  const figure = data.throttle;
  return (
    <>
      {data.concurrency !== null && (
        <div className="thr-panel">
          <div className="thr-panel-head">
            <h2>Fleet concurrency</h2>
            <span className="thr-panel-sub">
              How many sessions run at once across every machine. Loaded/running is the parallel capacity
              in flight; actively working is the subset whose agent is processing a turn this instant.
            </span>
          </div>
          <table className="thr-table">
            <thead>
              <tr>
                <th></th>
                <th className="thr-num">Now</th>
                <th className="thr-num">This week</th>
                <th className="thr-num">All-time</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Loaded / running</td>
                <td className="thr-num">{data.concurrency.live.current}</td>
                <td className="thr-num">{data.concurrency.live.weeklyMax}</td>
                <td className="thr-num">{data.concurrency.live.allTimeMax}</td>
              </tr>
              <tr>
                <td>Actively working</td>
                <td className="thr-num">{data.concurrency.working.current}</td>
                <td className="thr-num">{data.concurrency.working.weeklyMax}</td>
                <td className="thr-num">{data.concurrency.working.allTimeMax}</td>
              </tr>
            </tbody>
          </table>
        </div>
      )}

      {summary.hasData && (
        <>
          <div className="thr-panel">
            <div className="thr-panel-head">
              <h2>Turns by surface</h2>
            </div>
            <table className="thr-table">
              <thead>
                <tr>
                  <th>Surface</th>
                  <th className="thr-num">Turns</th>
                </tr>
              </thead>
              <tbody>
                {summary.surfaces.map((s) => (
                  <tr key={s.surface}>
                    <td>{s.label}</td>
                    <td className="thr-num">{s.turns}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="thr-panel">
            <div className="thr-panel-head">
              <h2>Full breakdown</h2>
            </div>
            <table className="thr-table">
              <thead>
                <tr>
                  <th>Modality</th>
                  <th>Surface</th>
                  <th className="thr-num">Turns</th>
                </tr>
              </thead>
              <tbody>
                {figure.buckets.length === 0 ? (
                  <tr>
                    <td colSpan={3} className="thr-muted">
                      No buckets yet.
                    </td>
                  </tr>
                ) : (
                  figure.buckets.map((b) => (
                    <tr key={`${b.modality}-${b.surface}`}>
                      <td>{MODALITY_LABEL[b.modality]}</td>
                      <td>{SURFACE_LABEL[b.surface]}</td>
                      <td className="thr-num">{b.turns}</td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </>
      )}

      {/* How the number is made and what it left out - the Gateway's definition, verbatim, and the size of
          the population outside it, so the share above can be checked against both. */}
      <div className="thr-panel">
        <div className="thr-panel-head">
          <h2>How this is counted</h2>
          <span className="thr-panel-sub">{figure.definition}</span>
        </div>
        <table className="thr-table">
          <thead>
            <tr>
              <th>Submissions in this window</th>
              <th className="thr-num">Count</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>Counted as your turns (carry an input origin)</td>
              <td className="thr-num">{figure.turns.toLocaleString()}</td>
            </tr>
            <tr>
              <td>Yours, but not placed on a surface - outside every number here</td>
              <td className="thr-num">{figure.excluded.unresolved.toLocaleString()}</td>
            </tr>
            <tr>
              <td>Other sessions prompting yours - the fleet driving itself (Agents tab)</td>
              <td className="thr-num">{figure.excluded.agentDriven.toLocaleString()}</td>
            </tr>
            <tr>
              <td>Text the product wrote itself (seed prompts, handovers) - nobody&apos;s turn</td>
              <td className="thr-num">{figure.excluded.framework.toLocaleString()}</td>
            </tr>
          </tbody>
        </table>
      </div>

      {data.notCaptured.length > 0 && (
        <div className="thr-caveats">
          <h2>What these numbers do not include</h2>
          <ul>
            {data.notCaptured.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        </div>
      )}
    </>
  );
}
