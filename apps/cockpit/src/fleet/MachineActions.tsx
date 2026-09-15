import { useState } from "react";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  restartDirectorIfEmpty,
  startDirector,
  updateDirectorNow,
  type FleetAction,
  type FleetMachine,
  type FleetTone,
} from "@devthrottle/client-core/fleet/machinesClient";
import { ConfirmDialog } from "../components";

// The update, restart and start buttons for one machine (fleet maintenance, devthrottle_internal#2021). Which
// buttons exist and what they say comes from the Gateway's fold; this component only asks for a confirm, sends
// the command, and hands the outcome back. Shared by the Machines tab and the Director page's update card.

export interface ActionOutcome {
  text: string;
  tone: FleetTone;
}

type Kind = "update" | "restart" | "start";

export function MachineActions({
  machine,
  onOutcome,
  showUpdateReason = true,
}: {
  machine: FleetMachine;
  onOutcome: (outcome: ActionOutcome) => void;
  /** Show why Update is not offered, under the buttons. The update card shows it itself. */
  showUpdateReason?: boolean;
}) {
  const [confirming, setConfirming] = useState<Kind | null>(null);
  const [busy, setBusy] = useState<Kind | null>(null);

  const actions: [Kind, FleetAction][] = [
    ["update", machine.update],
    ["restart", machine.restart],
    ["start", machine.start],
  ];

  const run = async (kind: Kind) => {
    setBusy(kind);
    try {
      if (kind === "update") {
        const view = await updateDirectorNow(machine.machine);
        onOutcome({ text: view.headline, tone: view.tone });
      } else if (kind === "restart") {
        await restartDirectorIfEmpty(machine.machine);
        onOutcome({ text: "The Director restarted.", tone: "ok" });
      } else {
        await startDirector(machine.machine);
        onOutcome({ text: "The launcher was asked to start the Director.", tone: "ok" });
      }
    } catch (err) {
      onOutcome({ text: gatewayErrorMessage(err, `${kind} the Director on ${machine.machine}`), tone: "bad" });
    } finally {
      setBusy(null);
    }
  };

  const pending = confirming === null ? null : machine[confirming];
  const offered = actions.filter(([, a]) => a.offered);

  return (
    <div className="fm-actions">
      {offered.length > 0 && (
        <div className="fm-buttons">
          {offered.map(([kind, a]) => (
            <button
              key={kind}
              type="button"
              className={`ddet-btn${kind === "update" ? " ddet-btn-primary" : ""}`}
              title={a.reason ?? undefined}
              disabled={busy !== null}
              onClick={(e) => {
                e.stopPropagation();
                setConfirming(kind);
              }}
            >
              {busy === kind ? "Working..." : a.label}
            </button>
          ))}
        </div>
      )}
      {showUpdateReason && !machine.update.offered && (machine.update.reason ?? "").length > 0 && (
        <div className="fm-why">
          <span className="fm-why-label">{machine.update.label}:</span> {machine.update.reason}
        </div>
      )}
      <ConfirmDialog
        open={pending !== null}
        title={pending === null ? "" : `${pending.label} on ${machine.machine}?`}
        message={pending?.reason ?? ""}
        confirmLabel={pending?.label ?? ""}
        cancelLabel="Cancel"
        onConfirm={() => {
          if (confirming !== null) void run(confirming);
        }}
        onClose={() => setConfirming(null)}
      />
    </div>
  );
}
