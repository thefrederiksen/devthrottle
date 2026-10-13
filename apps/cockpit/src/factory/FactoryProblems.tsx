import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  resolveRunProblem,
  startProblemTalk,
  type FactoryPageProblems,
  type FactoryProblemItem,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { Button } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-factory-problems";

// A factory's open problems (Factory Control, step 1b): the problems of its scheduled runs, from the one problems
// fold, each with Handle the issue and Resolve. Every word, and whether Handle the issue is available, is the
// Gateway's (FactoryProblemsFold); this only lays it out. Resolve sends the reason as typed - the Gateway is the one
// that refuses an empty one, in its own words, so the rule lives in one place.

export function ProblemsCard({ problems, onChanged }: { problems: FactoryPageProblems; onChanged: () => void }) {
  return (
    <section className="fa-panel" id="problems" data-testid="fa-page-problems">
      <h2 className="fa-section-title">{problems.heading}</h2>
      <p className="fa-dim">{problems.note}</p>
      {problems.emptyText !== null && <p className="fa-dim" data-testid="fa-problems-empty">{problems.emptyText}</p>}
      <div className="fa-waiting">
        {problems.items.map((item) => (
          <ProblemItem key={item.runId} item={item} onResolved={onChanged} />
        ))}
      </div>
    </section>
  );
}

function ProblemItem({ item, onResolved }: { item: FactoryProblemItem; onResolved: () => void }) {
  const navigate = useNavigate();
  const [starting, setStarting] = useState(false);
  const [resolving, setResolving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const { handle } = item;
  return (
    <div className="fa-waiting-item fa-problem" data-testid={`fa-problem-${item.runId}`}>
      <div className="fa-waiting-head">
        <strong>{item.seatText}</strong>
        <span className="fa-problem-open">{item.openForText}</span>
      </div>
      <div className="fa-problem-what">{item.what}</div>
      <div className="fa-waiting-foot">
        <span className="fa-dim">{item.whenText}</span>
        <span className="fa-dim">{item.shiftText}</span>
        {item.sessionId !== null && item.sessionLabel !== null && (
          <Link className="mono" to={`/session/${encodeURIComponent(item.sessionId)}`} data-testid="fa-problem-session">
            {item.sessionLabel}
          </Link>
        )}
        {item.noSessionText !== null && <span className="fa-dim">{item.noSessionText}</span>}
      </div>
      <div className="fa-problem-actions">
        <Button
          variant="primary"
          disabled={!handle.available || starting}
          aria-busy={starting}
          data-testid="fa-problem-handle"
          onClick={async () => {
            setStarting(true);
            setError(null);
            try {
              const started = await startProblemTalk(handle);
              navigate(started.href);
            } catch (err) {
              setError(describeAndReport(SURFACE, "start the talk", err));
              setStarting(false);
            }
          }}
        >
          {starting ? handle.busyLabel : handle.label}
        </Button>
        {!resolving && (
          <Button variant="secondary" data-testid="fa-problem-resolve" onClick={() => setResolving(true)}>
            {item.resolveLabel}
          </Button>
        )}
      </div>
      {handle.unavailableText !== null && (
        <p className="fa-dim" data-testid="fa-problem-unavailable">
          {handle.unavailableText}
        </p>
      )}
      {resolving && (
        <form
          className="fa-problem-resolve"
          onSubmit={async (e) => {
            e.preventDefault();
            setBusy(true);
            setError(null);
            try {
              await resolveRunProblem(item.runId, reason);
              onResolved();
            } catch (err) {
              setError(describeAndReport(SURFACE, "resolve it", err));
            } finally {
              setBusy(false);
            }
          }}
        >
          <label className="fa-problem-resolve-label">
            {item.resolvePrompt}
            <input
              className="fa-problem-reason"
              data-testid="fa-problem-reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              disabled={busy}
            />
          </label>
          <Button variant="primary" type="submit" disabled={busy} data-testid="fa-problem-resolve-send">
            {busy ? item.resolveBusyLabel : item.resolveConfirmLabel}
          </Button>
        </form>
      )}
      {error !== null && (
        <p className="fa-error" role="alert" data-testid="fa-problem-error">
          {error}
        </p>
      )}
    </div>
  );
}
