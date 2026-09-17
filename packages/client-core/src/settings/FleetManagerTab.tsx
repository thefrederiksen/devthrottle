import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  type FleetManagerAction,
  type FleetManagerPlacement,
  getFleetManagerPlacement,
  moveFleetManager,
  restartFleetManager,
  saveFleetManagerPlacement,
  startFleetManager,
} from "./fleetManagerClient";
import { ACCOUNT_SCOPE, CardHead, errText } from "./settingsShared";
import { TurnVerdictCard } from "./TurnVerdictCard";
import "./settings.css";

// ---- "Fleet Manager" tab: where the account's Fleet Manager runs (the Fleet Manager mission, step 5) ---------
//
// The Fleet Manager is a real session, so it needs an agent and a computer. This tab shows whether it is running,
// lets the owner choose where it runs, and starts, restarts or moves it.
//
// THE GATEWAY DECIDES EVERYTHING SHOWN HERE (CLAUDE.md rule 7): the running sentence, each computer's state line,
// which computers can be chosen, which buttons are offered and what they say, and the confirmation text. This file
// lays them out and sends the owner's choice. The one thing it keeps for itself is the selection the owner has not
// saved yet.
//
// Restart and move close the running Fleet Manager, so both ask first. The shared library has no dialog component
// (the Cockpit's ConfirmDialog lives in the Cockpit), so the question is asked in place, the way the restart
// requests panel and the accounts panel already do it.
//
// The Wingman's turn verdict switches live here too: the Fleet Manager is built on those verdicts, and the
// Assistant tab they used to sit on is hidden.

/** How often the tab reads the Gateway's answer again while it is open. */
export const REFRESH_MS = 15_000;

export interface FleetManagerTabProps {
  /** The mounting surface's route to a session. Omitted means this surface has none, and "Open it" is not shown -
   *  never a link to a route that does not exist. */
  sessionHref?: (sessionId: string) => string;
}

export function FleetManagerTab({ sessionHref }: FleetManagerTabProps) {
  return (
    <>
      <FleetManagerPlacementCard sessionHref={sessionHref} />
      <TurnVerdictCard />
    </>
  );
}

type Pending = { kind: "restart"; action: FleetManagerAction } | { kind: "move"; action: FleetManagerAction };

function FleetManagerPlacementCard({ sessionHref }: FleetManagerTabProps) {
  const [placement, setPlacement] = useState<FleetManagerPlacement | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busyLabel, setBusyLabel] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [agent, setAgent] = useState("");
  const [machine, setMachine] = useState("");
  // True once the owner has changed the selection and not saved it: a timed refresh must not undo that.
  const edited = useRef(false);
  const busyRef = useRef(false);

  const apply = useCallback((next: FleetManagerPlacement, resetSelection: boolean) => {
    setPlacement(next);
    if (resetSelection || !edited.current) {
      edited.current = false;
      setAgent(next.agent ?? "");
      setMachine(next.machine ?? "");
    }
  }, []);

  const load = useCallback(async () => {
    try {
      const next = await getFleetManagerPlacement();
      setLoadError(null);
      apply(next, false);
    } catch (e) {
      setLoadError(errText(e));
    }
  }, [apply]);

  useEffect(() => {
    void load();
    const timer = setInterval(() => {
      if (!busyRef.current) void load();
    }, REFRESH_MS);
    return () => clearInterval(timer);
  }, [load]);

  // Every action: immediate feedback in the Gateway's words, the Gateway's own sentence on failure, and the
  // refreshed answer the Gateway returns on success. This is the event-handler entry point, hence the try.
  const run = async (label: string, act: () => Promise<FleetManagerPlacement>) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusyLabel(label);
    setActionError(null);
    setPending(null);
    try {
      apply(await act(), true);
    } catch (e) {
      setActionError(errText(e));
      void load();
    } finally {
      busyRef.current = false;
      setBusyLabel(null);
    }
  };

  if (placement === null) {
    return loadError !== null ? (
      <div className="settings-error" role="alert">
        Could not load the Fleet Manager setting: {loadError}
      </div>
    ) : (
      <p className="settings-loading">Loading the Fleet Manager setting...</p>
    );
  }

  const { status, save } = placement;
  const busy = busyLabel !== null;
  const chosen = placement.machines.find((m) => m.machine === machine);
  const changed = agent !== (placement.agent ?? "") || machine !== (placement.machine ?? "");
  const canSave =
    save.offered && !busy && agent !== "" && chosen !== undefined && chosen.selectable && (changed || placement.isDefault);

  const onSave = () => {
    if (save.verb === "move") {
      setPending({ kind: "move", action: save });
      return;
    }
    void run(save.busyLabel, () => saveFleetManagerPlacement(agent, machine));
  };

  const confirm = () => {
    if (pending === null) return;
    if (pending.kind === "restart") void run(pending.action.busyLabel, restartFleetManager);
    else void run(pending.action.busyLabel, () => moveFleetManager(agent, machine));
  };

  const barNote = status.start.offered ? status.start.note : status.restart.note ?? status.start.note;

  return (
    <>
      <section className={`fm-now fm-tone-${status.tone}`} aria-label="Fleet Manager status">
        <span className="fm-dot" aria-hidden="true" />
        <span className="fm-now-text">{status.sentence}</span>
        <div className="fm-now-actions">
          {status.open.offered && sessionHref !== undefined && status.sessionId && (
            <Link className="settings-btn" to={sessionHref(status.sessionId)}>
              {status.open.label}
            </Link>
          )}
          {status.start.offered && (
            <button
              type="button"
              className="settings-btn primary"
              disabled={busy}
              onClick={() => void run(status.start.busyLabel, startFleetManager)}
            >
              {status.start.label}
            </button>
          )}
          {status.restart.offered && (
            <button
              type="button"
              className="settings-btn"
              disabled={busy}
              onClick={() => setPending({ kind: "restart", action: status.restart })}
            >
              {status.restart.label}
            </button>
          )}
        </div>
      </section>
      {barNote && <p className="settings-hint fm-bar-note">{barNote}</p>}

      {pending !== null && (
        <div className="fm-confirm" role="alertdialog" aria-label={pending.action.confirmTitle ?? pending.action.label}>
          <div className="fm-confirm-title">{pending.action.confirmTitle}</div>
          <p className="fm-confirm-message">{pending.action.confirmMessage}</p>
          <div className="settings-actions">
            <button type="button" className="settings-btn primary" onClick={confirm}>
              {pending.action.label}
            </button>
            <button type="button" className="settings-btn" onClick={() => setPending(null)}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {busyLabel !== null && (
        <p className="settings-msg fm-busy" role="status">
          {busyLabel}
        </p>
      )}
      {actionError !== null && (
        <div className="settings-error fm-action-error" role="alert">
          {actionError}
        </div>
      )}

      <section className="settings-card">
        <CardHead title="Where the Fleet Manager runs" scope={ACCOUNT_SCOPE} />
        <p className="settings-hint">
          The Fleet Manager is a real session, so it needs an agent and a computer. The sessions it starts can run
          anywhere; this is only where the Fleet Manager itself runs.
        </p>
        {placement.defaultNote && <p className="settings-hint fm-default-note">{placement.defaultNote}</p>}

        <div className="fm-field">
          <label className="fm-label" htmlFor="fm-agent">
            Agent
          </label>
          <div className="fm-value">
            <select
              id="fm-agent"
              className="settings-select fm-select"
              value={agent}
              disabled={busy}
              onChange={(e) => {
                edited.current = true;
                setAgent(e.target.value);
              }}
            >
              {agent === "" && <option value="">Choose an agent</option>}
              {placement.agents.map((a) => (
                <option key={a.value} value={a.value}>
                  {a.displayName}
                </option>
              ))}
            </select>
            <div className="settings-inline-msg fm-note">{placement.agentNote}</div>
          </div>
        </div>

        <div className="fm-field">
          <div className="fm-label">
            Computer
            <small>{placement.machineNote}</small>
          </div>
          <div className="fm-machines" role="radiogroup" aria-label="Computer">
            {placement.machines.map((m) => {
              const selected = m.machine === machine;
              return (
                <label
                  key={m.machine}
                  className={`fm-machine${selected ? " selected" : ""}${m.selectable ? "" : " off"}`}
                >
                  <input
                    type="radio"
                    name="fm-machine"
                    value={m.machine}
                    checked={selected}
                    disabled={!m.selectable || busy}
                    onChange={() => {
                      edited.current = true;
                      setMachine(m.machine);
                    }}
                  />
                  <span className="fm-machine-body">
                    <span className="fm-machine-name">{m.machine}</span>
                    <span className="fm-machine-state">
                      <span className={`fm-state fm-tone-${m.tone}`}>{m.stateLabel}</span>
                      {m.detail ? ` ${m.detail}` : ""}
                    </span>
                  </span>
                </label>
              );
            })}
          </div>
        </div>

        <div className="fm-savebar">
          <button type="button" className="settings-btn primary" disabled={!canSave} onClick={onSave}>
            {save.label}
          </button>
          {save.note && <span className="fm-save-note">{save.note}</span>}
        </div>
      </section>
    </>
  );
}
