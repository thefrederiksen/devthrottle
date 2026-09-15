import { useCallback, useEffect, useState } from "react";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  getDirectorUpdateStatus,
  getFleetMachines,
  type DirectorUpdateView,
  type FleetMachines,
} from "@devthrottle/client-core/fleet/machinesClient";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { useNow } from "@devthrottle/client-core/polling/useNow";
import { relativeTime } from "./format";
import { MachineActions, type ActionOutcome } from "./MachineActions";
import { VersionPill } from "./MachinesPanel";
import { machineOf, toneClass } from "./machinesFormat";

// The "Version and updates" card on the Director page (fleet maintenance, devthrottle_internal#2021, #2022): this
// Director's version against the newest release, whether its machine has a newer build downloaded, what the
// launcher can do, and the update / restart / start buttons. All of it is the Gateway's fold.
const MACHINES_POLL_MS = 10_000;
const STATUS_POLL_WHILE_INSTALLING_MS = 15_000;

export function DirectorUpdateCard({ directorId, machineName }: { directorId: string; machineName: string }) {
  const now = useNow();
  const [view, setView] = useState<FleetMachines | null>(null);
  const [viewError, setViewError] = useState<string | null>(null);
  const [status, setStatus] = useState<DirectorUpdateView | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<ActionOutcome | null>(null);

  const refreshMachines = useCallback(async (signal?: AbortSignal) => {
    try {
      setView(await getFleetMachines(signal));
      setViewError(null);
    } catch (err) {
      if (signal?.aborted !== true) setViewError(gatewayErrorMessage(err));
    }
  }, []);
  useVisiblePolling(refreshMachines, MACHINES_POLL_MS);

  const machine = machineOf(view, machineName);
  const director = machine?.directors.find((d) => d.directorId.toLowerCase() === directorId.toLowerCase()) ?? null;
  // The Gateway says whether this launcher can be asked at all; an older one is never sent the question, so the
  // card does not carry an error line for a launcher that is simply older than the command.
  const canReport = machine?.canReportUpdateStatus === true;

  const refreshStatus = useCallback(async () => {
    if (!canReport) return;
    try {
      setStatus(await getDirectorUpdateStatus(machineName));
      setStatusError(null);
    } catch (err) {
      setStatusError(gatewayErrorMessage(err));
    }
  }, [machineName, canReport]);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

  // While an update is installing, look again until it is not - the new version also arrives on its own
  // through the Director's registration, and this is what shows the result sentence.
  const installing = status?.inProgress === true;
  useEffect(() => {
    if (!installing) return;
    const timer = window.setInterval(() => void refreshStatus(), STATUS_POLL_WHILE_INSTALLING_MS);
    return () => window.clearInterval(timer);
  }, [installing, refreshStatus]);

  return (
    <section className="ddet-sec fm-card">
      <div className="ddet-sec-head"><h2>Version and updates</h2></div>
      {view === null ? (
        <div className="ddet-quiet">{viewError ?? "Loading..."}</div>
      ) : machine === null ? (
        <div className="ddet-quiet">This Director's machine ({machineName || "unnamed"}) is not in the machine list.</div>
      ) : (
        <>
          <dl className="ddet-kv">
            <dt>Running</dt>
            <dd>
              <span className="dmono">{director?.version ?? "-"}</span>{" "}
              {director !== null && <VersionPill state={director.versionState} />}
            </dd>
            <dt>Newest</dt>
            <dd className="dmono" title={view.newestRelease.detail ?? undefined}>{view.newestRelease.label}</dd>
            {canReport && (
              <>
                <dt>Downloaded</dt>
                <dd>{status?.downloaded ?? (statusError !== null ? "not known" : "...")}</dd>
              </>
            )}
            <dt>Launcher</dt>
            <dd>
              {(machine.launcherVersion ?? "").length > 0 && <span className="dmono">{machine.launcherVersion} </span>}
              <span className={toneClass(machine.reachTone)}>{machine.reachLabel}</span>
            </dd>
            <dt>Sessions</dt>
            <dd>{director?.sessions ?? 0}</dd>
          </dl>
          {(machine.reachDetail ?? "").length > 0 && <div className="fm-why">{machine.reachDetail}</div>}
          {!machine.update.offered && (machine.update.reason ?? "").length > 0 && (
            <div className="fm-why">
              <span className="fm-why-label">{machine.update.label}:</span> {machine.update.reason}
            </div>
          )}
          <MachineActions
            machine={machine}
            showUpdateReason={false}
            onOutcome={(o) => {
              setOutcome(o);
              void refreshMachines();
              void refreshStatus();
            }}
          />
          {outcome !== null && <div className={`fm-result ${toneClass(outcome.tone)}`}>{outcome.text}</div>}
          {status !== null && (outcome === null || status.inProgress) && (
            <div className={`fm-result ${toneClass(status.tone)}`}>
              {status.headline}
              {(status.lastResultAt ?? null) !== null && !status.inProgress && (
                <span className="ddim"> ({relativeTime(status.lastResultAt, { withAgo: true, now })})</span>
              )}
            </div>
          )}
          {statusError !== null && <div className="fm-why">Update status: {statusError}</div>}
        </>
      )}
    </section>
  );
}
