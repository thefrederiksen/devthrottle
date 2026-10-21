import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  getLicensePlan,
  getLicenseUsage,
  setLicenseMonthlyPrice,
  type LicensePlan,
  type LicensePlanChoice,
  type LicenseUsage,
  type LicenseUsageSession,
} from "@devthrottle/client-core/licenses/licenseUsageClient";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, EmptyState, ErrorBanner, LoadingState } from "../components";
import { LICENSES_SURFACE as SURFACE, LocalTime, toneClass } from "./licenseParts";

// WHAT IS USING IT (the usage-by-session mandate of 2026-10-20): the second tab of the Licenses page. Which sessions use
// one Claude account, how fast and how much so far, in ONE unit - percent of its weekly limit - and the "Make it last"
// panel: pick a time, see whether the account lasts until then, and tick sessions to see what stopping them would do.
//
// THE TAB DECIDES NOTHING (CLAUDE.md rule 7). The verdict, the burn, the suggestion and every what-if come from the
// Gateway's planner (GET /gateway/licenses/plan): ticking a box asks it again with that box in `stop`, it never
// subtracts a rate here. The tab lays numbers out, draws a meter from two of them, and shows times in the viewer's zone.
// Ticking only asks a question: nothing here stops a session.
const POLL_MS = 30000;
const CUSTOM = "custom";

/** A percent of the weekly limit as the tab shows it; null is "not measured", never a zero. */
export function pct(value: number | null | undefined): string {
  if (value === null || value === undefined) return "not measured";
  return `${value.toFixed(value < 0.1 ? 3 : 2)}%`;
}

function rate(value: number | null | undefined): string {
  return value === null || value === undefined ? "not measured" : `${pct(value)}/h`;
}

function dollars(value: number | null | undefined): string | null {
  return value === null || value === undefined ? null : `$${value.toFixed(2)}`;
}

function hours(value: number | null | undefined): string {
  if (value === null || value === undefined) return "-";
  return value < 1 ? `${Math.round(value * 60)} min` : `${value.toFixed(1)} h`;
}

/** The value of a datetime-local input as an ISO time, read in the viewer's own zone; null when it is not a time. */
function isoFromLocal(local: string): string | null {
  const parsed = new Date(local);
  return local.length === 0 || Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/** Every session id of the choice is in `stop`: none, some, or all of them. */
function stoppedOf(choice: LicensePlanChoice, stop: ReadonlySet<string>): "none" | "some" | "all" {
  const count = choice.sessionIds.filter((id) => stop.has(id)).length;
  return count === 0 ? "none" : count === choice.sessionIds.length ? "all" : "some";
}

export function LicenseUsageTab() {
  const [account, setAccount] = useState<string | null>(null);
  const [closed, setClosed] = useState(false);
  const [target, setTarget] = useState<string | null>(null);
  const [customTime, setCustomTime] = useState("");
  const [stop, setStop] = useState<ReadonlySet<string>>(new Set());

  const [usage, setUsage] = useState<LicenseUsage | null>(null);
  const [usageError, setUsageError] = useState<string | null>(null);
  // The answer on screen carries the question it answers, so an answer to an older question is never shown as current.
  const [plan, setPlan] = useState<{ key: string; answer: LicensePlan } | null>(null);
  const [planError, setPlanError] = useState<string | null>(null);

  const readUsage = useCallback(
    async (signal?: AbortSignal) => {
      try {
        setUsage(await getLicenseUsage(account, closed, signal));
        setUsageError(null);
        backgroundRecovered(SURFACE, "read what is using the license");
      } catch (err) {
        if (signal?.aborted === true) return;
        setUsageError(describeAndReport(SURFACE, "read what is using the license", err, { background: true }));
      }
    },
    [account, closed],
  );
  useVisiblePolling(readUsage, POLL_MS);

  // One question to the planner per state of the panel: a change asks again at once, and the answer to an older
  // question is dropped when its request is cancelled.
  const until = target === CUSTOM ? isoFromLocal(customTime) : target;
  const needsTime = target === CUSTOM && until === null;
  const stopKey = [...stop].sort().join(",");
  const planKey = JSON.stringify([account, until, stopKey]);
  const readPlan = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const stopped = stopKey.length === 0 ? [] : stopKey.split(",");
        setPlan({ key: planKey, answer: await getLicensePlan({ account, until, stop: stopped }, signal) });
        setPlanError(null);
        backgroundRecovered(SURFACE, "read the plan");
      } catch (err) {
        if (signal?.aborted === true) return;
        setPlanError(describeAndReport(SURFACE, "read the plan", err, { background: true }));
      }
    },
    [account, until, stopKey, planKey],
  );
  useVisiblePolling(readPlan, POLL_MS, !needsTime);

  const chooseAccount = (email: string) => {
    setAccount(email);
    setTarget(null);
    setStop(new Set());
  };
  const toggleSession = (id: string) =>
    setStop((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const toggleChoice = (choice: LicensePlanChoice) =>
    setStop((current) => {
      const next = new Set(current);
      // The same convention as the session table: ticked means "what if it stopped". A box with some of its sessions
      // ticked ticks the rest; a fully ticked box clears them.
      const stopAll = stoppedOf(choice, current) !== "all";
      choice.sessionIds.forEach((id) => (stopAll ? next.add(id) : next.delete(id)));
      return next;
    });

  if (usage === null) {
    return usageError === null ? <LoadingState /> : <ErrorBanner message={usageError} onRetry={() => void readUsage()} />;
  }

  const shownAccount = account ?? usage.accountEmail;
  // Another account was just picked and its answer is not here yet: nothing of the old account is shown under its name.
  const usagePending = account !== null && usage.accountEmail !== account;
  return (
    <div className="lic-usage" data-testid="lic-usage">
      {/* error-reported-by: readUsage */}
      {usageError !== null && <ErrorBanner message={usageError} onRetry={() => void readUsage()} />}

      <div className="lic-usage-filters">
        {usage.accounts.length > 0 && (
          <label>
            Account{" "}
            <select
              value={shownAccount ?? ""}
              onChange={(e) => chooseAccount(e.target.value)}
              data-testid="lic-usage-account"
            >
              {usage.accounts.map((a) => (
                <option key={a.accountEmail} value={a.accountEmail}>
                  {a.label}
                </option>
              ))}
            </select>
          </label>
        )}
        <label>
          <input type="checkbox" checked={closed} onChange={(e) => setClosed(e.target.checked)} /> Show sessions that have gone
        </label>
        <span className="lic-updated">
          Updated <LocalTime iso={usage.asOfUtc} />
        </span>
      </div>

      {usagePending ? (
        <LoadingState />
      ) : usage.emptyText !== null && usage.accountEmail === null ? (
        <EmptyState message={usage.emptyText} />
      ) : (
        <>
          <section className="lic-panel lic-usage-unit" aria-label="The unit">
            <h2 className="lic-panel-title">One unit for everything: percent of the weekly limit</h2>
            {usage.unitText.map((line) => (
              <p key={line}>{line}</p>
            ))}
            <p className="lic-dim" data-testid="lic-usage-calibration">
              {usage.calibration.text}
            </p>
            {shownAccount !== null && <PriceSetting accountEmail={shownAccount} price={usage.monthlyPriceUsd} onSaved={() => void readUsage()} />}
          </section>

          <MakeItLast
            plan={plan?.answer ?? null}
            pending={needsTime || (plan !== null && plan.key !== planKey)}
            needsTime={needsTime}
            error={planError}
            onRetry={() => void readPlan()}
            target={target}
            onTarget={setTarget}
            customTime={customTime}
            onCustomTime={(value) => {
              setCustomTime(value);
              setTarget(CUSTOM);
            }}
            stopCount={stop.size}
            onSuggest={() => plan !== null && setStop(new Set(plan.answer.suggestion.sessionIds))}
            onClear={() => setStop(new Set())}
          />

          {plan !== null && plan.answer.choices.length > 0 && (
            <section className="lic-panel" aria-label="By top-level session">
              <h2 className="lic-panel-title">By top-level session - tick one to see what stopping it and every session it started would do</h2>
              <div className="lic-usage-groups" data-testid="lic-usage-groups">
                {plan.answer.choices.map((choice) => (
                  <TopLevelRow key={choice.topLevelSessionId} choice={choice} stopped={stoppedOf(choice, stop)}
                    onToggle={() => toggleChoice(choice)} widest={plan.answer.choices[0].rateNowPercentPerHour} />
                ))}
              </div>
            </section>
          )}

          <section className="lic-panel" aria-label="By session">
            <h2 className="lic-panel-title">By session - sorted by rate now</h2>
            {usage.sessions.length === 0 ? (
              <EmptyState message={usage.emptyText ?? ""} />
            ) : (
              <SessionTable sessions={usage.sessions} topLevel={usage.topLevel} stop={stop} onToggle={toggleSession} />
            )}
            <p className="lic-dim">Ticking a box only asks what would happen; it stops nothing.</p>
          </section>
        </>
      )}
    </div>
  );
}

function MakeItLast({
  plan,
  pending,
  needsTime,
  error,
  onRetry,
  target,
  onTarget,
  customTime,
  onCustomTime,
  stopCount,
  onSuggest,
  onClear,
}: {
  plan: LicensePlan | null;
  /** The answer on screen is to another question than the one the panel now shows. */
  pending: boolean;
  /** "Pick a time" is chosen and no time is entered. */
  needsTime: boolean;
  error: string | null;
  onRetry: () => void;
  target: string | null;
  onTarget: (target: string) => void;
  customTime: string;
  onCustomTime: (value: string) => void;
  stopCount: number;
  onSuggest: () => void;
  onClear: () => void;
}) {
  const whatIf = plan?.whatIf ?? null;
  const running = whatIf !== null ? whatIf.burnPercentPerHour : plan?.burnPercentPerHour ?? null;
  const tone = whatIf !== null ? whatIf.verdictTone : plan?.verdictTone ?? "neutral";
  const allowed = plan?.allowedPercentPerHour ?? null;
  const chosen = target ?? plan?.targetId ?? null;
  const scale = Math.max(running ?? 0, allowed ?? 0) * 1.15;
  const verdict = needsTime
    ? "Pick a time to ask about."
    : pending
      ? error !== null
        ? "No answer for this choice: the Gateway did not answer. Try again above."
        : "Asking the Gateway about this choice..."
      : whatIf !== null
        ? whatIf.verdict
        : plan?.verdict ?? "";
  return (
    <section className="lic-panel lic-usage-plan" aria-label="Make it last" data-testid="lic-usage-plan">
      <h2 className="lic-panel-title">Make it last</h2>
      {/* error-reported-by: readPlan */}
      {error !== null && <ErrorBanner message={error} onRetry={onRetry} />}
      {plan === null ? (
        error === null && <LoadingState />
      ) : (
        <>
          <div className="lic-usage-targets" role="radiogroup" aria-label="Until">
            {plan.targets.map((t) => (
              <label key={t.id} className={chosen === t.id ? "on" : undefined}>
                <input type="radio" name="lic-target" checked={chosen === t.id} onChange={() => onTarget(t.id)} />{" "}
                <b>
                  <LocalTime iso={t.atUtc} />
                </b>
                <small>{t.label}</small>
              </label>
            ))}
            <label className={chosen === CUSTOM || chosen === "time" ? "on" : undefined}>
              <input type="radio" name="lic-target" checked={chosen === CUSTOM} onChange={() => onTarget(CUSTOM)} /> <b>Pick a time</b>
              <small>
                <input
                  type="datetime-local"
                  value={customTime}
                  onChange={(e) => onCustomTime(e.target.value)}
                  aria-label="Make it last until"
                  data-testid="lic-usage-custom-time"
                />
              </small>
            </label>
          </div>

          <div className={pending ? "lic-usage-pending" : undefined} aria-busy={pending && !needsTime && error === null}>
          <div className="lic-usage-tiles">
            <Tile label="Left before 97%" value={pct(plan.leftPercent)}
              detail={plan.weekUsedPercent === null ? "" : `${pct(plan.weekUsedPercent)} used of the weekly limit`} />
            <Tile label="Hours to go" value={hours(plan.hoursToGo)} detail={plan.targetText} />
            <Tile label="You can run" value={rate(allowed)} detail="left / hours to go" />
            <Tile
              label="Running now"
              value={rate(running)}
              tone={tone}
              detail={whatIf !== null ? `after stopping ${whatIf.stoppedSessionIds.length} session${whatIf.stoppedSessionIds.length === 1 ? "" : "s"}, saving ${rate(whatIf.savesPercentPerHour)}` : plan.burnText}
            />
          </div>

          {running !== null && allowed !== null && scale > 0 && (
            <div className="lic-usage-meter" aria-hidden="true">
              <span className={`lic-usage-meter-fill ${toneClass(tone)}`} style={{ width: `${(running / scale) * 100}%` }} />
              <span className="lic-usage-meter-limit" style={{ left: `${(allowed / scale) * 100}%` }} />
              {/* The label is kept inside the meter's ends, so it never runs past the panel. */}
              <span className="lic-usage-meter-label" style={{ left: `${Math.min(85, Math.max(15, (allowed / scale) * 100))}%` }}>
                you can run {rate(allowed)}
              </span>
            </div>
          )}
          </div>

          <p className={`lic-usage-verdict ${pending ? "lic-tone-neutral" : toneClass(tone)}`} data-testid="lic-usage-verdict">
            {verdict}
          </p>
          {!pending && whatIf !== null && whatIf.notFound.length > 0 && (
            <p className="lic-dim">No longer running: {whatIf.notFound.length} of the ticked sessions.</p>
          )}
          {!pending && (
            <p className="lic-dim" data-testid="lic-usage-suggestion">
              {plan.suggestion.text}
            </p>
          )}
          <div className="lic-usage-buttons">
            <Button variant="primary" onClick={onSuggest} disabled={pending || plan.suggestion.sessionIds.length === 0}>
              Suggest what to stop
            </Button>
            <Button variant="ghost" onClick={onClear} disabled={stopCount === 0}>
              Clear
            </Button>
          </div>
          <p className="lic-dim">The suggestion stops waiting sessions before working ones, biggest first, until the rate fits. Untick any it chose to see the new verdict.</p>
        </>
      )}
    </section>
  );
}

function Tile({ label, value, detail, tone }: { label: string; value: string; detail: string; tone?: string }) {
  return (
    <div className="lic-usage-tile">
      <div className="lic-usage-tile-label">{label}</div>
      <div className={`lic-usage-tile-value${tone === undefined ? "" : ` ${toneClass(tone)}`}`}>{value}</div>
      <div className="lic-dim">{detail}</div>
    </div>
  );
}

function TopLevelRow({ choice, stopped, onToggle, widest }: {
  choice: LicensePlanChoice;
  stopped: "none" | "some" | "all";
  onToggle: () => void;
  widest: number | null;
}) {
  const box = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (box.current !== null) box.current.indeterminate = stopped === "some";
  }, [stopped]);
  const width = widest !== null && widest > 0 && choice.rateNowPercentPerHour !== null ? (choice.rateNowPercentPerHour / widest) * 100 : 0;
  return (
    <div className="lic-usage-group">
      <label>
        <input ref={box} type="checkbox" checked={stopped === "all"} onChange={onToggle}
          aria-label={`What if ${choice.label} and every session under it stopped`} /> {choice.label}{" "}
        <span className="lic-dim">
          ({choice.working} working, {choice.waiting} waiting)
        </span>
      </label>
      <span className="lic-usage-bar">
        <i style={{ width: `${width}%` }} />
      </span>
      <span className="lic-num">{rate(choice.rateNowPercentPerHour)}</span>
    </div>
  );
}

function SessionTable({ sessions, topLevel, stop, onToggle }: {
  sessions: ReadonlyArray<LicenseUsageSession>;
  topLevel: LicenseUsage["topLevel"];
  stop: ReadonlySet<string>;
  onToggle: (id: string) => void;
}) {
  const labels = useMemo(() => new Map(topLevel.map((g) => [g.id ?? "", g.label])), [topLevel]);
  const widest = Math.max(0, ...sessions.map((s) => s.rateNowPercentPerHour ?? 0));
  return (
    <div className="lic-table-wrap">
      <table className="lic-table lic-usage-table" data-testid="lic-usage-sessions">
        <thead>
          <tr>
            <th>Stop?</th>
            <th>#</th>
            <th>Session</th>
            <th>State</th>
            <th>Model</th>
            <th className="lic-num">Rate now</th>
            <th className="lic-num">Last 4 h</th>
            <th className="lic-num">So far</th>
            <th className="lic-num">Running</th>
            <th className="lic-num">Average / h</th>
          </tr>
        </thead>
        <tbody>
          {sessions.map((s) => {
            const off = stop.has(s.sessionId);
            const top = s.topLevelSessionId === s.sessionId ? null : labels.get(s.topLevelSessionId);
            const soFarDollars = dollars(s.dollarsSoFar);
            return (
              <tr key={s.sessionId} className={off ? "lic-usage-off" : undefined} data-session-id={s.sessionId}>
                <td>
                  {s.isLive && (
                    <input type="checkbox" checked={off} onChange={() => onToggle(s.sessionId)} aria-label={`What if ${s.name} stopped`} />
                  )}
                </td>
                <td>{s.number ?? ""}</td>
                <td className="lic-usage-name">
                  {s.name}
                  {(s.missionName !== null || top !== null) && (
                    <small>
                      {[s.missionName, top === null || top === undefined ? null : `under ${top}`].filter((x) => x !== null).join(" - ")}
                    </small>
                  )}
                </td>
                <td>
                  <span className={`lic-pill${s.state === "working" ? " lic-tone-good" : ""}`}>{s.stateText}</span>
                </td>
                <td>{s.model ?? ""}</td>
                <td className="lic-num">
                  {rate(s.rateNowPercentPerHour)}
                  {/* No bar for a rate that is not measured: an empty bar reads as zero use. */}
                  {s.rateNowPercentPerHour !== null && (
                    <span className="lic-usage-bar lic-usage-bar-small">
                      <i style={{ width: `${widest > 0 ? (s.rateNowPercentPerHour / widest) * 100 : 0}%` }} />
                    </span>
                  )}
                </td>
                <td className="lic-num">{pct(s.last4HoursPercent)}</td>
                <td className="lic-num">
                  {pct(s.soFarPercent)}
                  {soFarDollars !== null && ` - ${soFarDollars}`}
                </td>
                <td className="lic-num">{hours(s.runningHours)}</td>
                <td className="lic-num">{rate(s.averagePercentPerHour)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/** What the account's subscription costs a month: the dollar figures are that share of it. The owner's own setting. */
function PriceSetting({ accountEmail, price, onSaved }: { accountEmail: string; price: number | null; onSaved: () => void }) {
  const [text, setText] = useState(price === null ? "" : String(price));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => setText(price === null ? "" : String(price)), [price, accountEmail]);
  const save = async () => {
    const trimmed = text.trim();
    const value = trimmed.length === 0 ? null : Number(trimmed);
    if (value !== null && (!Number.isFinite(value) || value < 0)) {
      setError("The price is a number of dollars, or empty to clear it.");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await setLicenseMonthlyPrice(accountEmail, value);
      onSaved();
    } catch (err) {
      setError(describeAndReport(SURFACE, "set the plan price", err));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="lic-usage-price">
      <label>
        Plan price, dollars a month{" "}
        <input type="text" inputMode="decimal" value={text} onChange={(e) => setText(e.target.value)} data-testid="lic-usage-price" />
      </label>
      <Button onClick={() => void save()} disabled={busy}>
        {busy ? "Saving..." : "Save"}
      </Button>
      {error !== null && <ErrorBanner message={error} />}
    </div>
  );
}
