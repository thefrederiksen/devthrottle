import { useState } from "react";
import {
  runLicenseFireDrill,
  setLicenseFireDrillMachine,
  type LicenseFireDrill,
  type LicenseMachine,
} from "@devthrottle/client-core/licenses/licensesClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";
import { LICENSES_SURFACE, toneClass } from "./licenseParts";

// THE FIRE DRILL CARD (mission "Licenses - Always Ready, Loud When Not", the follow-up to step 7): the drill's newest
// line, the drill machine and where to choose it, and Run now. The daily drill does nothing until a drill machine is
// chosen, and this card is where the owner chooses it.
//
// THE CARD DECIDES NOTHING (CLAUDE.md rule 7). The line is the Gateway's, folded by LicenseFireDrillFold and drawn as
// given - including how many drills passed in a row. Its `passed` picks the colour and nothing else. Which machines may
// be chosen is the Gateway's to say: every machine on the page is offered, and one the Gateway will not take is
// refused in its own sentence. Run now is offered when the Gateway's answer names a drill machine.

/** The value of the picker's "none chosen" option. A machineId is never empty. */
const NONE = "";

export function LicenseFireDrillCard({
  drill,
  error,
  onRetry,
  machines,
  onAnswer,
}: {
  /** The newest answer of the fire drill routes; null until the first read has answered. */
  drill: LicenseFireDrill | null;
  /** Why the last read failed; null when it did not. */
  error: string | null;
  onRetry: () => void;
  /** The overview's machines: the ones the drill machine is chosen from, and the labels of the one chosen. */
  machines: ReadonlyArray<LicenseMachine>;
  /** A write's answer, which is the read as it now stands. */
  onAnswer: (answer: LicenseFireDrill) => void;
}) {
  const [choosing, setChoosing] = useState(false);
  const [chooseError, setChooseError] = useState<string | null>(null);
  const [confirmingRun, setConfirmingRun] = useState(false);

  // The sentence of the answer before is taken off the screen first: beside a new choice it would read as its outcome.
  const choose = async (value: string) => {
    setChoosing(true);
    setChooseError(null);
    try {
      onAnswer(await setLicenseFireDrillMachine(value === NONE ? null : value));
    } catch (err) {
      setChooseError(describeAndReport(LICENSES_SURFACE, "choose the drill machine", err));
    } finally {
      setChoosing(false);
    }
  };

  // A refusal throws with the Gateway's own sentence; the confirmation stays open and shows it.
  const confirmRun = async () => {
    onAnswer(await runLicenseFireDrill());
  };

  const chosen = drill?.machineId ?? null;
  const chosenMachine = chosen === null ? undefined : machines.find((machine) => machine.machineId === chosen);
  // The drill machine as the page calls it; a chosen machine that has left the page is named by its identifier.
  const chosenLabel = chosen === null ? null : (chosenMachine?.label ?? chosen);

  return (
    <section className="lic-drill" aria-label="Fire drill" data-testid="lic-fire-drill">
      {/* error-reported-by: usePolled */}
      {error !== null && <ErrorBanner message={error} onRetry={onRetry} />}
      {drill === null ? (
        error === null && <LoadingState />
      ) : (
        <>
          {drill.line !== null && (
            <p
              className={`lic-drill-line ${toneClass(drill.line.passed ? "good" : "warning")}`}
              data-testid="lic-fire-drill-line"
              data-passed={drill.line.passed}
            >
              {drill.line.text}
            </p>
          )}
          <div className="lic-drill-controls">
            <label className="lic-field">
              Drill machine
              <select
                className="lic-input"
                value={chosen ?? NONE}
                disabled={choosing}
                onChange={(event) => void choose(event.target.value)}
                data-testid="lic-fire-drill-machine"
              >
                <option value={NONE}>No drill machine</option>
                {machines.map((machine) => (
                  <option key={machine.machineId} value={machine.machineId}>
                    {machine.label}
                  </option>
                ))}
                {chosen !== null && chosenMachine === undefined && <option value={chosen}>{chosen}</option>}
              </select>
            </label>
            {chosen !== null && (
              <Button variant="secondary" disabled={choosing} onClick={() => setConfirmingRun(true)} data-testid="lic-fire-drill-run">
                Run the drill now
              </Button>
            )}
            {choosing && <span className="lic-dim">Saving...</span>}
          </div>
          {chooseError !== null && (
            <p className="lic-drill-error" role="alert" data-testid="lic-fire-drill-error">
              {chooseError}
            </p>
          )}
        </>
      )}

      <ConfirmDialog
        open={confirmingRun}
        title={chosenLabel === null ? "" : `Run the fire drill on ${chosenLabel} now?`}
        message={
          chosenLabel === null ? null : (
            <p className="lic-choice-text">
              {`This switches ${chosenLabel} out to a spare account and back again, each switch checked by a usage read, and checks that every machine could switch now. It takes as long as the two switches take.`}
            </p>
          )
        }
        confirmLabel="Run the drill"
        busyLabel="Running the drill..."
        // It moves one machine and moves it back: heavy, not destructive.
        danger={false}
        action="run the fire drill"
        onConfirm={confirmRun}
        onClose={() => setConfirmingRun(false)}
      />
    </section>
  );
}
