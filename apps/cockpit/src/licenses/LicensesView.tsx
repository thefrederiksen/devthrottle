import { useCallback, useState } from "react";
import {
  getLicenseForecast,
  getLicenseLog,
  getLicenseOverview,
  switchLicenseNow,
  type LicenseAccount,
  type LicenseForecast,
  type LicenseLogEntry,
  type LicenseLogPage,
  type LicenseMachine,
  type LicenseOverview,
  type LicenseSwitchNowResult,
} from "@devthrottle/client-core/licenses/licensesClient";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ConfirmDialog, EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import { LicenseAccountCard } from "./LicenseAccountCard";
import { LocalTime, TonePill } from "./licenseParts";

const SURFACE = "cockpit-licenses";

// THE LICENSES PAGE (the License Switching mission, phase 3): every Claude account as a card with its two usage
// windows, the schedule (which account each machine is on, which is next and when), the switch log, and Switch now.
//
// THE PAGE DECIDES NOTHING (CLAUDE.md rule 7). Every status, tone, sentence and offer on it is folded on the
// Gateway (LicenseDisplayFold) and shown as sent. The page never compares a percentage with the switching
// threshold and never works out how far away a time is. What it does do is lay out, look a row up by its
// account or machine, and show a time in the viewer's own time zone.
//
// The three reads are polled separately, so a failure of one leaves the other two on screen. The sentences are
// true at each answer's asOfUtc; reading again every 30 seconds keeps them true.
const POLL_MS = 30000;

const readOverview = (signal?: AbortSignal) => getLicenseOverview(signal);
const readForecast = (signal?: AbortSignal) => getLicenseForecast(signal);
const readNewestLog = (signal?: AbortSignal) => getLicenseLog(null, signal);

interface Polled<S> {
  data: S | null;
  error: string | null;
  refresh: (signal?: AbortSignal) => Promise<void>;
  /** Change what is on screen from something other than a poll (an older page of the log). */
  update: (change: (current: S | null) => S | null) => void;
}

/** A read with nothing to keep from the answer before it: what is on screen is the newest answer. */
function newest<T>(_current: T | null, answer: T): T {
  return answer;
}

/**
 * One polled read. A failed poll keeps the last answer on screen and says so beside it. `fold` makes what is on
 * screen from what was there and the new answer, in the same update that takes the answer - so there is never a
 * frame that shows the new answer with the old rows.
 */
function usePolled<T, S>(
  read: (signal?: AbortSignal) => Promise<T>,
  action: string,
  fold: (current: S | null, answer: T) => S,
): Polled<S> {
  const [data, setData] = useState<S | null>(null);
  const [error, setError] = useState<string | null>(null);
  const refresh = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const answer = await read(signal);
        setData((current) => fold(current, answer));
        setError(null);
        backgroundRecovered(SURFACE, action);
      } catch (err) {
        if (signal?.aborted === true) return;
        setError(describeAndReport(SURFACE, action, err, { background: true }));
      }
    },
    [read, action, fold],
  );
  useVisiblePolling(refresh, POLL_MS);
  return { data, error, refresh, update: setData };
}

/** `first` followed by every row of `rest` it does not already hold. The order of each list is kept. */
export function mergeLog(first: ReadonlyArray<LicenseLogEntry>, rest: ReadonlyArray<LicenseLogEntry>): LicenseLogEntry[] {
  const seen = new Set(first.map((entry) => entry.entryId));
  return [...first, ...rest.filter((entry) => !seen.has(entry.entryId))];
}

/** The switch log as it is on screen: the newest page, and any older rows the person has read past it. */
interface LogView {
  isEmpty: boolean;
  emptyText: string | null;
  entries: LicenseLogEntry[];
  /** The cursor of the next older page; null when there is none. */
  cursor: string | null;
  /** The person has read past the newest page, so rows already on screen are kept when the newest page moves. */
  walked: boolean;
}

/**
 * The newest page moves as switches happen. Until the person asks for older rows the list IS that page; after,
 * the rows already read stay and the newest page is laid on top, so a new row never opens a gap in the list.
 */
export function foldNewestLogPage(current: LogView | null, page: LicenseLogPage): LogView {
  if (current !== null && current.walked) {
    return { ...current, isEmpty: page.isEmpty, emptyText: page.emptyText, entries: mergeLog(page.entries, current.entries) };
  }
  return {
    isEmpty: page.isEmpty,
    emptyText: page.emptyText,
    entries: page.entries,
    cursor: page.hasMore ? page.nextCursor : null,
    walked: false,
  };
}

export function LicensesView() {
  const overview = usePolled<LicenseOverview, LicenseOverview>(readOverview, "read the licenses", newest);
  const forecast = usePolled<LicenseForecast, LicenseForecast>(readForecast, "read the license forecast", newest);
  const log = usePolled<LicenseLogPage, LogView>(readNewestLog, "read the switch log", foldNewestLogPage);

  const [olderBusy, setOlderBusy] = useState(false);
  const [olderError, setOlderError] = useState<string | null>(null);
  const olderCursor = log.data?.cursor ?? null;

  const showOlder = async () => {
    if (olderCursor === null) return;
    setOlderBusy(true);
    setOlderError(null);
    try {
      const page = await getLicenseLog(olderCursor);
      log.update((current) =>
        current === null
          ? current
          : {
              ...current,
              entries: mergeLog(current.entries, page.entries),
              cursor: page.hasMore ? page.nextCursor : null,
              walked: true,
            },
      );
    } catch (err) {
      setOlderError(describeAndReport(SURFACE, "read older rows of the switch log", err));
    } finally {
      setOlderBusy(false);
    }
  };

  // Switch now: the account being confirmed, the target picked for it, and the Gateway's answer to the last one.
  const [confirming, setConfirming] = useState<LicenseAccount | null>(null);
  const [target, setTarget] = useState<string | null>(null);
  const [lastSwitch, setLastSwitch] = useState<LicenseSwitchNowResult | null>(null);

  const askSwitchNow = (account: LicenseAccount) => {
    setTarget(account.switchNowTargetAccountEmail);
    setConfirming(account);
  };

  const refreshAll = () => {
    void overview.refresh();
    void forecast.refresh();
    void log.refresh();
  };

  // A refusal throws with the Gateway's own sentence; the dialog stays open and shows it.
  const confirmSwitchNow = async () => {
    if (confirming === null) return;
    const result = await switchLicenseNow(confirming.accountEmail, target);
    setLastSwitch(result);
    refreshAll();
  };

  const data = overview.data;

  return (
    <div className="lic-page">
      <PageHeader
        title="Licenses"
        subtitle={
          data === null || data.isEmpty
            ? "Your Claude accounts, which machine is on which, and when they switch."
            : `${data.accounts.length} Claude account${data.accounts.length === 1 ? "" : "s"} on ${data.machines.length} machine${data.machines.length === 1 ? "" : "s"}.`
        }
        actions={
          data !== null && (
            <span className="lic-updated">
              Updated <LocalTime iso={data.asOfUtc} />
            </span>
          )
        }
      />

      {/* error-reported-by: usePolled */}
      {overview.error !== null && <ErrorBanner message={overview.error} onRetry={() => void overview.refresh()} />}

      {data === null ? (
        overview.error === null && <LoadingState />
      ) : data.isEmpty ? (
        // No machine has reported yet: the Gateway's own sentence, in place of every list.
        <EmptyState message={data.emptyText ?? ""} />
      ) : (
        <>
          <div className="lic-cards">
            {data.accounts.map((account) => (
              <LicenseAccountCard
                key={account.accountEmail}
                account={account}
                machines={data.machines}
                forecast={forecast.data?.accounts.find((a) => a.accountEmail === account.accountEmail)}
                onSwitchNow={askSwitchNow}
              />
            ))}
          </div>

          {lastSwitch !== null && <SwitchNowAnswer result={lastSwitch} onDismiss={() => setLastSwitch(null)} />}

          <h2 className="lic-h2">Schedule</h2>
          {/* error-reported-by: usePolled */}
          {forecast.error !== null && <ErrorBanner message={forecast.error} onRetry={() => void forecast.refresh()} />}
          {forecast.data !== null && (
            <p className="lic-next-switch" data-testid="lic-next-switch">
              {forecast.data.nextSwitchText}
            </p>
          )}
          <ScheduleTable machines={data.machines} forecast={forecast.data} />

          <h2 className="lic-h2">Switch log</h2>
          {/* error-reported-by: usePolled */}
          {log.error !== null && <ErrorBanner message={log.error} onRetry={() => void log.refresh()} />}
          {log.data === null ? (
            log.error === null && <LoadingState />
          ) : log.data.isEmpty ? (
            <EmptyState message={log.data.emptyText ?? ""} />
          ) : (
            <>
              <SwitchLogTable entries={log.data.entries} />
              {olderError !== null && <ErrorBanner message={olderError} />}
              {olderCursor !== null && (
                <div className="lic-older">
                  <Button variant="secondary" disabled={olderBusy} onClick={() => void showOlder()}>
                    {olderBusy ? "Loading..." : "Show older"}
                  </Button>
                </div>
              )}
            </>
          )}
        </>
      )}

      <ConfirmDialog
        open={confirming !== null}
        title={confirming === null ? "" : `Switch ${confirming.accountEmail} now?`}
        message={confirming === null ? null : <SwitchNowChoice account={confirming} target={target} onPick={setTarget} />}
        confirmLabel="Switch now"
        busyLabel="Switching..."
        // It moves every machine on the account, and it can be moved back: heavy, not destructive.
        danger={false}
        action="switch the account"
        onConfirm={confirmSwitchNow}
        onClose={() => setConfirming(null)}
      />
    </div>
  );
}

/** The body of the Switch now confirmation: the Gateway's sentence for its own choice, and the targets to pick from. */
function SwitchNowChoice({
  account,
  target,
  onPick,
}: {
  account: LicenseAccount;
  target: string | null;
  onPick: (accountEmail: string) => void;
}) {
  return (
    <div className="lic-choice">
      {/* The sentence describes the Gateway's own choice, so it is shown only while that choice is the one picked. */}
      {target === account.switchNowTargetAccountEmail && <p className="lic-choice-text">{account.switchNowText}</p>}
      {account.switchTargets.length > 0 && (
        <fieldset className="lic-choice-targets">
          <legend>Move to</legend>
          {account.switchTargets.map((option) => (
            <label key={option.accountEmail} className={`lic-choice-target${option.canChoose ? "" : " disabled"}`}>
              <input
                type="radio"
                name="lic-switch-target"
                value={option.accountEmail}
                checked={target === option.accountEmail}
                disabled={!option.canChoose}
                onChange={() => onPick(option.accountEmail)}
              />
              <span className="lic-choice-email">{option.accountEmail}</span>
              {option.isAutomaticChoice && <span className="lic-choice-note">The Gateway's choice</span>}
              {option.unavailableText !== null && <span className="lic-choice-note">{option.unavailableText}</span>}
            </label>
          ))}
        </fieldset>
      )}
    </div>
  );
}

/** What the Gateway answered to the last Switch now: each machine's row in its own sentence, and who was left out. */
function SwitchNowAnswer({ result, onDismiss }: { result: LicenseSwitchNowResult; onDismiss: () => void }) {
  return (
    <div className="lic-answer" role="status" data-testid="lic-switch-answer">
      <ul className="lic-answer-list">
        {result.switches.map((machine) => (
          <li key={machine.switchId}>
            <TonePill text={machine.stateText} tone={machine.stateTone} /> {machine.summary}
          </li>
        ))}
        {result.skipped.map((machine) => (
          <li key={machine.machineName}>
            {machine.machineName}: {machine.why}
          </li>
        ))}
      </ul>
      <Button variant="ghost" onClick={onDismiss}>
        Dismiss
      </Button>
    </div>
  );
}

/** The schedule: one row per machine - the account it is on, the one it moves to next, and the Gateway's sentence. */
function ScheduleTable({ machines, forecast }: { machines: ReadonlyArray<LicenseMachine>; forecast: LicenseForecast | null }) {
  return (
    <div className="lic-table-wrap">
    <table className="lic-table" data-testid="lic-schedule">
      <thead>
        <tr>
          <th>Machine</th>
          <th>Status</th>
          <th>On now</th>
          <th>Next account</th>
          <th>Expected switch</th>
        </tr>
      </thead>
      <tbody>
        {machines.map((machine) => {
          const next = forecast?.machines.find((m) => m.machineId === machine.machineId);
          return (
            <tr key={machine.machineId} data-testid="lic-schedule-row">
              <td className="lic-nowrap">
                <span className="lic-strong">{machine.label}</span>
              </td>
              <td>
                <TonePill text={machine.statusText} tone={machine.statusTone} />
                <div className="lic-dim">{machine.lastReportText}</div>
              </td>
              <td className="lic-nowrap">{machine.activeAccountEmail}</td>
              <td className="lic-nowrap">{next?.nextAccountEmail}</td>
              <td>
                {machine.switchInProgress !== null && <div>{machine.switchInProgress.text}</div>}
                {next !== undefined && <div>{next.text}</div>}
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
    </div>
  );
}

/** The switch log, newest first. A kind or a state this build does not know is still a row: its words are shown. */
function SwitchLogTable({ entries }: { entries: ReadonlyArray<LicenseLogEntry> }) {
  return (
    <div className="lic-table-wrap">
    <table className="lic-table" data-testid="lic-log">
      <thead>
        <tr>
          <th>When</th>
          <th>What</th>
          <th>Machine</th>
          <th>From</th>
          <th>To</th>
          <th>Why</th>
          <th>Result</th>
        </tr>
      </thead>
      <tbody>
        {entries.map((entry) => (
          <tr key={entry.entryId} data-testid="lic-log-row" data-state={entry.state}>
            <td className="lic-nowrap">
              <LocalTime iso={entry.atUtc} />
            </td>
            <td className="lic-nowrap">{entry.kindText}</td>
            <td className="lic-nowrap">{entry.label}</td>
            <td className="lic-nowrap">{entry.fromAccountEmail}</td>
            <td className="lic-nowrap">{entry.toAccountEmail}</td>
            <td className="lic-nowrap">{entry.whyText}</td>
            <td>
              <TonePill text={entry.stateText} tone={entry.stateTone} testId="lic-log-state" />
              <div>{entry.summary}</div>
              {entry.reasonText !== null && <div className="lic-dim">{entry.reasonText}</div>}
              {entry.timingText !== null && <div className="lic-dim">{entry.timingText}</div>}
              {entry.verifiedText !== null && <div className="lic-dim">{entry.verifiedText}</div>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
    </div>
  );
}
