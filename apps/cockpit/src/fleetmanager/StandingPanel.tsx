import { useState } from "react";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import {
  confirmLesson,
  editStanding,
  keepLesson,
  removeStanding,
  type FleetStanding,
  type FleetStandingRow,
  type FleetStandingSection,
} from "@devthrottle/client-core/fleetmanager/standingClient";
import { Button, ConfirmDialog } from "../components";

// THE OWNER'S LESSONS AND STANDING PREFERENCES (issue #3559, part 4).
//
//   MistakeBox     "That was a mistake": an empty box for the owner's correction, and Keep. The Gateway keeps it in the
//                  owner's words, exactly, confirmed, and tells the Fleet Manager in the same save.
//   StandingPanel  one list, the lessons first and the preferences second, each row with its date, who kept it and
//                  whether it is confirmed, and its Confirm, Edit and Remove.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Every heading, sentence, label, and whether each button is offered is the
// Gateway's (FleetStandingFold); a refusal shows the Gateway's own words. After any change the page reads the Gateway
// again, so a row changes only when the Gateway says it has.

const SURFACE = "cockpit-fleet-manager";

export function MistakeBox({
  standing,
  onKept,
  onClose,
}: {
  standing: FleetStanding;
  /** Called after the Gateway kept the lesson. */
  onKept: () => void;
  onClose: () => void;
}) {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const keep = async () => {
    setBusy(true);
    setError(null);
    try {
      // The owner's words exactly as typed: never trimmed, never reworded.
      await keepLesson(text);
      onKept();
    } catch (err) {
      setError(describeAndReport(SURFACE, "keep the lesson", err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="fmp-mistake" aria-label={standing.boxTitle} data-testid="fmp-mistake">
      <h2 className="fmp-mistake-title">{standing.boxTitle}</h2>
      <div className="fmp-mistake-note">{standing.boxNote}</div>
      <textarea
        className="fmp-mistake-text"
        aria-label={standing.boxTitle}
        value={text}
        maxLength={standing.maxLessonLength}
        placeholder={standing.boxPlaceholder}
        rows={4}
        autoFocus
        onChange={(e) => setText(e.target.value)}
      />
      {error !== null && (
        <div className="fmp-inline-error" role="alert">
          {error}
        </div>
      )}
      <div className="fmp-mistake-actions">
        <Button variant="primary" disabled={busy || text.trim().length === 0} onClick={() => void keep()}>
          {busy ? standing.keepBusyLabel : standing.keepLabel}
        </Button>
        <Button disabled={busy} onClick={onClose}>
          {standing.cancelLabel}
        </Button>
      </div>
    </section>
  );
}

function StandingRow({ row, standing, onChanged }: { row: FleetStandingRow; standing: FleetStanding; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(row.text);
  const [busy, setBusy] = useState<"confirm" | "edit" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [removeAsked, setRemoveAsked] = useState(false);

  const run = async (which: "confirm" | "edit", action: string, call: () => Promise<void>) => {
    setBusy(which);
    setError(null);
    try {
      await call();
      if (which === "edit") setEditing(false);
      onChanged();
    } catch (err) {
      setError(describeAndReport(SURFACE, action, err));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className={`fmp-st-row fmp-st-${row.tone}`} data-testid={`fmp-st-${row.id}`}>
      {editing ? (
        <textarea
          className="fmp-mistake-text"
          aria-label={row.edit.label}
          value={draft}
          maxLength={row.maxLength}
          rows={3}
          onChange={(e) => setDraft(e.target.value)}
        />
      ) : (
        <div className="fmp-st-text">{row.text}</div>
      )}
      {editing && row.edit.note && <div className="fmp-st-meta">{row.edit.note}</div>}
      {row.mistakeLine && <div className="fmp-st-meta">{row.mistakeLine}</div>}
      <div className="fmp-st-meta">{row.keptLine}</div>
      {row.statusLine && <div className="fmp-st-status">{row.statusLine}</div>}
      {error !== null && (
        <div className="fmp-inline-error" role="alert">
          {error}
        </div>
      )}
      <div className="fmp-st-actions">
        {editing ? (
          <>
            <Button
              variant="primary"
              disabled={busy !== null || draft.trim().length === 0}
              onClick={() => void run("edit", "save the new words", () => editStanding(row.id, draft))}
            >
              {busy === "edit" ? standing.saveBusyLabel : standing.saveLabel}
            </Button>
            <Button
              disabled={busy !== null}
              onClick={() => {
                setEditing(false);
                setDraft(row.text);
                setError(null);
              }}
            >
              {standing.cancelLabel}
            </Button>
          </>
        ) : (
          <>
            {row.confirm.offered && (
              <Button variant="primary" disabled={busy !== null} onClick={() => void run("confirm", "confirm the lesson", () => confirmLesson(row.id))}>
                {busy === "confirm" ? row.confirm.busyLabel : row.confirm.label}
              </Button>
            )}
            {row.edit.offered && (
              <Button
                variant="ghost"
                disabled={busy !== null}
                onClick={() => {
                  setDraft(row.text);
                  setEditing(true);
                }}
              >
                {row.edit.label}
              </Button>
            )}
            {row.remove.offered && (
              <Button variant="ghost" disabled={busy !== null} onClick={() => setRemoveAsked(true)}>
                {row.remove.label}
              </Button>
            )}
          </>
        )}
      </div>
      {!row.confirm.offered && row.confirm.note && <div className="fmp-st-meta">{row.confirm.note}</div>}
      {removeAsked && row.remove.confirmTitle && row.remove.confirmMessage && (
        <ConfirmDialog
          open
          title={row.remove.confirmTitle}
          message={row.remove.confirmMessage}
          confirmLabel={row.remove.label}
          busyLabel={row.remove.busyLabel}
          action={row.removeAction}
          onConfirm={async () => {
            await removeStanding(row.id);
            onChanged();
          }}
          onClose={() => setRemoveAsked(false)}
        />
      )}
    </div>
  );
}

function StandingSection({
  section,
  name,
  standing,
  onChanged,
}: {
  section: FleetStandingSection;
  name: string;
  standing: FleetStanding;
  onChanged: () => void;
}) {
  return (
    <section className="fmp-sec" aria-label={section.title} data-testid={`fmp-st-sec-${name}`}>
      <h3 className="fmp-sec-title">
        {section.title} <span className="fmp-sec-count">{section.count}</span>
      </h3>
      {section.note && <div className="fmp-sec-note">{section.note}</div>}
      {section.rows.map((row) => (
        <StandingRow key={row.id} row={row} standing={standing} onChanged={onChanged} />
      ))}
      {section.emptyText && <div className="fmp-sec-empty">{section.emptyText}</div>}
    </section>
  );
}

export function StandingPanel({ standing, onChanged }: { standing: FleetStanding; onChanged: () => void }) {
  return (
    <aside className="fmp-side fmp-standing" aria-label={standing.showLabel} data-testid="fmp-standing">
      <StandingSection section={standing.lessons} name="lessons" standing={standing} onChanged={onChanged} />
      <StandingSection section={standing.preferences} name="preferences" standing={standing} onChanged={onChanged} />
    </aside>
  );
}
