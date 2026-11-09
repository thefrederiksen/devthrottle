import type {
  LicenseAccount,
  LicenseMachine,
  LicenseOverview,
  LicenseTodo,
  LicenseVerdict,
} from "@devthrottle/client-core/licenses/licensesClient";
import { Button } from "../components";
import { LocalDate, LocalTime, toneClass } from "./licenseParts";

// THE OVERVIEW OF THE LICENSES PAGE (the owner's redesign, 8 November 2026): one status card that says whether every
// machine is covered, one tile per license, the Machines table ending in "Ready for next?", and a short list of grey
// to-dos. Everything else is one click away: a machine's sign-ins, spares and Remove in its drawer, the forecast, the
// history and the approvers on their own tabs.
//
// THE PAGE DECIDES NOTHING (CLAUDE.md rule 7). The card's colour is the Gateway's tone, judged by the same rule as the
// one license alarm; every sentence, label and tone below is drawn as the Gateway sent it. What happens here is layout,
// and showing a time in the viewer's own time zone.

/** The tabs a to-do or a link can open. */
export type LicensesTabLink = "forecast" | "history" | "approvers";

export interface LicenseOverviewTabProps {
  overview: LicenseOverview;
  /** Switch now for one license: opens the existing confirmation. */
  onSwitchNow: (account: LicenseAccount) => void;
  /** The status card's fix: a sign-in on one machine. */
  onFix: (verdict: LicenseVerdict) => void;
  onOpenMachine: (machine: LicenseMachine) => void;
  onOpenTab: (tab: LicensesTabLink) => void;
}

export function LicenseOverviewTab({ overview, onSwitchNow, onFix, onOpenMachine, onOpenTab }: LicenseOverviewTabProps) {
  const openTodo = (todo: LicenseTodo) => {
    if (todo.opens === "approvers") {
      onOpenTab("approvers");
      return;
    }
    const machine = overview.machines.find((m) => m.machineId === todo.machineId);
    if (machine !== undefined) onOpenMachine(machine);
  };
  return (
    <>
      {overview.verdict !== null && (
        <StatusCard
          verdict={overview.verdict}
          switchAccount={overview.accounts.find((a) => a.accountEmail === overview.verdict?.switchNowAccountEmail) ?? null}
          onSwitchNow={onSwitchNow}
          onFix={onFix}
          onShowMachine={() => {
            const machine = overview.machines.find((m) => m.machineId === overview.verdict?.machine?.machineId);
            if (machine !== undefined) onOpenMachine(machine);
          }}
        />
      )}

      <div className="lic-section-head">
        <h2 className="lic-section-title">Licenses</h2>
        <span className="lic-section-note">the 80% line is marked on each bar</span>
      </div>
      <div className="lic-tiles" data-testid="lic-tiles">
        {overview.accounts.map((account) => (
          <LicenseTile key={account.accountEmail} account={account} />
        ))}
      </div>

      <div className="lic-section-head">
        <h2 className="lic-section-title">Machines</h2>
      </div>
      <MachinesTable machines={overview.machines} onOpenMachine={onOpenMachine} />

      {overview.todos.length > 0 && (
        <>
          <div className="lic-section-head">
            <h2 className="lic-section-title">To-do - not urgent</h2>
            <span className="lic-section-note">nothing here stops a switch</span>
          </div>
          <ul className="lic-todos" data-testid="lic-todos">
            {overview.todos.map((todo, index) => (
              <li key={`${todo.kind}-${todo.machineId ?? ""}-${index}`} className="lic-todo" data-testid="lic-todo" data-kind={todo.kind}>
                <span className="lic-todo-text">{todo.text}</span>
                {todo.mattersByUtc !== null && (
                  <span className="lic-todo-by">
                    matters before <LocalDate iso={todo.mattersByUtc} />
                  </span>
                )}
                <Button variant="secondary" onClick={() => openTodo(todo)}>
                  {todo.actionLabel}
                </Button>
              </li>
            ))}
          </ul>
        </>
      )}

      <nav className="lic-tab-links" aria-label="More about licenses">
        <button type="button" className="lic-tab-link" onClick={() => onOpenTab("forecast")}>
          <strong>Forecast</strong> - 7-day chart of every license
        </button>
        <button type="button" className="lic-tab-link" onClick={() => onOpenTab("history")}>
          <strong>History</strong> - switches, sign-ins and alarms
        </button>
        <button type="button" className="lic-tab-link" onClick={() => onOpenTab("approvers")}>
          <strong>Approvers &amp; spares</strong> - set-up, per machine
        </button>
      </nav>
    </>
  );
}

/** The one card at the top: green when every machine is covered, in the alarm's tone when one is not. */
function StatusCard({
  verdict,
  switchAccount,
  onSwitchNow,
  onFix,
  onShowMachine,
}: {
  verdict: LicenseVerdict;
  switchAccount: LicenseAccount | null;
  onSwitchNow: (account: LicenseAccount) => void;
  onFix: (verdict: LicenseVerdict) => void;
  onShowMachine: () => void;
}) {
  return (
    <section
      className={`lic-status ${toneClass(verdict.tone)}`}
      data-testid="lic-status"
      data-state={verdict.state}
      data-tone={verdict.tone}
      aria-label="License status"
    >
      <span className="lic-status-mark" aria-hidden="true">
        {verdict.state === "covered" ? "OK" : "!"}
      </span>
      <div className="lic-status-body">
        <h2 className="lic-status-headline" data-testid="lic-status-headline">
          {verdict.headline}
        </h2>
        <p className="lic-status-detail" data-testid="lic-status-detail">
          {verdict.detail}
        </p>
        {verdict.actionText !== null && <p className="lic-status-action">{verdict.actionText}</p>}
      </div>
      <div className="lic-status-actions">
        {verdict.fix !== null && (
          <Button variant="danger" onClick={() => onFix(verdict)} data-testid="lic-status-fix">
            {verdict.fix.label}
          </Button>
        )}
        {verdict.machine !== null && (
          <Button variant="secondary" onClick={onShowMachine} data-testid="lic-status-show">
            Show me how
          </Button>
        )}
        {switchAccount !== null && (
          <Button variant="secondary" onClick={() => onSwitchNow(switchAccount)} data-testid="lic-status-switch">
            Switch now
          </Button>
        )}
      </div>
    </section>
  );
}

/** One license: who it is, its part now, its week as a bar with the 80% line, and one sentence. */
function LicenseTile({ account }: { account: LicenseAccount }) {
  const week = account.sevenDay;
  const known = week.utilization !== null && (week.level === "normal" || week.level === "at-limit");
  const width = known ? Math.min(100, Math.max(0, week.utilization as number)) : 0;
  return (
    <article className={`lic-tile lic-tile-${account.role}`} data-testid="lic-tile" data-role={account.role}>
      <div className="lic-tile-email">{account.accountEmail}</div>
      <span className={`lic-role ${toneClass(account.roleTone)}`} data-testid="lic-tile-role">
        {account.roleText}
      </span>
      <div
        className={`lic-tile-bar${known ? "" : " lic-bar-unknown"}`}
        role="meter"
        aria-label={`${account.accountEmail} this week`}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={width}
        aria-valuetext={week.utilizationText}
      >
        <span className="lic-tile-fill" style={{ width: `${width}%` }} />
        <span className="lic-tile-line" aria-hidden="true" />
      </div>
      <div className="lic-tile-figures">
        <span>{week.utilizationText} of week</span>
        <span>{week.resetsText}</span>
      </div>
      <p className="lic-tile-text" data-testid="lic-tile-text">
        {account.tileText}
        {account.tileAtUtc !== null && (
          <span className="lic-dim">
            {" "}
            (<LocalTime iso={account.tileAtUtc} />)
          </span>
        )}
      </p>
    </article>
  );
}

/** One line per machine, ending in whether it is ready for its next license, and a button to its drawer. */
function MachinesTable({
  machines,
  onOpenMachine,
}: {
  machines: ReadonlyArray<LicenseMachine>;
  onOpenMachine: (machine: LicenseMachine) => void;
}) {
  return (
    <div className="lic-machines" data-testid="lic-machines">
      <div className="lic-machine-row lic-machine-head" aria-hidden="true">
        <span>Machine</span>
        <span>On now</span>
        <span>Next license</span>
        <span>Ready for next?</span>
        <span />
      </div>
      {machines.map((machine) => (
        <div key={machine.machineId} className="lic-machine-row" data-testid="lic-machine-row">
          <span className="lic-strong" data-label="Machine">
            {machine.label}
          </span>
          <span data-label="On now">{machine.activeAccountEmail ?? "none"}</span>
          <span data-label="Next license">{machine.readinessTargetAccountEmail ?? "none"}</span>
          <span
            className={toneClass(machine.readyForNextTone)}
            data-label="Ready for next?"
            data-testid="lic-machine-ready"
            data-tone={machine.readyForNextTone}
          >
            {machine.readyForNextText}
          </span>
          <Button
            variant="secondary"
            className="lic-machine-more"
            aria-label={`More about ${machine.label}`}
            onClick={() => onOpenMachine(machine)}
            data-testid="lic-machine-open"
          >
            ...
          </Button>
        </div>
      ))}
    </div>
  );
}
