import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  cloneWorkflow,
  getWorkflow,
  getWorkflowInstructions,
  setWorkflowEnabled,
  type WorkflowDefinition,
} from "@devthrottle/client-core/workflows/workflowsClient";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-workflow-detail";

// One workflow, in full (Workflows mission, phase 7). The list row answered "what exists"; this page
// answers "what does it actually say": the metadata and step summary up top (the machine-readable
// SHAPE), then the instruction markdown rendered read-only - the AUTHORITATIVE conduct, the exact
// text an agent fetches with `cc-devthrottle workflow instructions <id>` and follows. Rendered
// through the same sanitized renderer the file viewer trusts; read-only on purpose, because
// authoring is agent-driven and a half-editable page would lie about where edits really happen.
export function WorkflowDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [workflow, setWorkflow] = useState<WorkflowDefinition | null>(null);
  const [instructions, setInstructions] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pendingOff, setPendingOff] = useState(false);
  const [pendingClone, setPendingClone] = useState(false);
  // Every load claims a generation; a load that finishes after a newer one started (a mutation
  // refresh racing a route change to another workflow) drops its result instead of painting
  // workflow A's state under workflow B's URL.
  const loadGen = useRef(0);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      if (id === undefined) return;
      const gen = ++loadGen.current;
      try {
        // Sequential on purpose: the metadata names a version, and the conduct is fetched PINNED to
        // that exact version - two concurrent unpinned fetches can straddle a publish and render v1
        // steps over v2 conduct (a torn read the inspection caught). The pin also keeps this page
        // working for an OFF workflow, whose unversioned conduct read the Gateway refuses.
        const wf = await getWorkflow(id, signal);
        const md = await getWorkflowInstructions(id, wf.version, signal);
        if (gen !== loadGen.current) return;
        setWorkflow(wf);
        setInstructions(md);
        setError(null);
      } catch (err) {
        if (signal?.aborted === true || gen !== loadGen.current) return;
        setError(describeAndReport(SURFACE, "load the workflow", err));
      }
    },
    [id],
  );

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  // A failed flip is never silent: it lands in the page's error state (with Retry).
  const flip = async (enabled: boolean) => {
    if (id === undefined) return;
    try {
      await setWorkflowEnabled(id, enabled, "cockpit");
    } catch (err) {
      setError(describeAndReport(SURFACE, enabled ? "turn the workflow on" : "turn the workflow off", err));
      return;
    }
    await load();
  };

  return (
    <div className="page wf">
      <header className="ui-page-header">
        <div className="ui-page-header-text">
          <p className="wf-crumb">
            <Link to="/workflows">Workflows</Link>
          </p>
          <h1 className="ui-page-title">{workflow?.name ?? id}</h1>
          {workflow ? <p className="ui-page-subtitle">{workflow.summary}</p> : null}
        </div>
      </header>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : workflow === null || instructions === null ? (
        <LoadingState message="Loading workflow..." />
      ) : (
        <>
          <div className="wf-detail-facts">
            {workflow.isBuiltIn === true ? <span className="wf-badge wf-badge-builtin">Built-in</span> : null}
            {workflow.isBuiltIn === false ? <span className="wf-badge">Custom</span> : null}
            {typeof workflow.version === "number" ? <span className="wf-badge">v{workflow.version}</span> : null}
            {workflow.hasDraft === true ? <span className="wf-badge wf-badge-draft">Draft waiting</span> : null}
            {/* The owner's switch, on the workflow's own page too - state named, flip confirmed
                when turning off (the register's semantics, in one place per the shared client). */}
            {workflow.enabled !== undefined ? (
              <span className="wf-detail-switch">
                <button
                  className={workflow.enabled ? "wf-switch wf-switch-on" : "wf-switch"}
                  role="switch"
                  aria-checked={workflow.enabled}
                  aria-label={workflow.enabled ? "in force - turn off" : "off - turn on"}
                  onClick={() => {
                    if (workflow.enabled) setPendingOff(true);
                    else void flip(true);
                  }}
                ></button>
                <span className={workflow.enabled ? "wf-state-label wf-state-on" : "wf-state-label wf-state-off"}>
                  {workflow.enabled ? "In force" : "Off"}
                </span>
              </span>
            ) : null}
            <Button variant="secondary" onClick={() => setPendingClone(true)}>
              Clone
            </Button>
          </div>
          {/* The Gateway's editability verdict, rendered verbatim (rule 7): built-ins are
              DevThrottle-maintained and read-only; the sanctioned customization path is clone. */}
          {workflow.editable === false ? (
            <p className="wf-conduct-hint">
              This is a built-in workflow, maintained by DevThrottle and updated with the Gateway
              itself. It cannot be edited or deleted - to customize the conduct, clone it into your
              own workflow.
            </p>
          ) : null}
          {workflow.enabled === false ? (
            <p className="wf-off-banner">
              This workflow is OFF: agents will not see it in their briefings, and it cannot start
              new runs or seat new sessions. Nothing is deleted - turn it back on anytime.
            </p>
          ) : null}

          <dl className="wf-facts">
            <dt>When to use it</dt>
            <dd>{workflow.whenToUse}</dd>
            <dt>You are asked</dt>
            <dd>{workflow.humanCheckpoint}</dd>
          </dl>

          <ol className="wf-detail-steps">
            {workflow.steps.map((step, i) => (
              <li key={step.name}>
                <span className="wf-detail-step-num">{i + 1}.</span> <strong>{step.name}</strong>{" "}
                - {step.doer}
                {step.reviewer !== null ? `, reviewed by ${step.reviewer}` : ", no review"}; done when{" "}
                {step.done}
              </li>
            ))}
          </ol>

          <section className="wf-conduct">
            <h2 className="wf-conduct-title">The conduct</h2>
            <p className="wf-conduct-hint">
              This is the exact text an agent fetches and follows:{" "}
              <code>cc-devthrottle workflow instructions {workflow.id}</code>
            </p>
            <div
              className="wf-conduct-body"
              dangerouslySetInnerHTML={{ __html: markdownToHtml(instructions) }}
            />
          </section>
        </>
      )}

      <ConfirmDialog
        open={pendingOff}
        title={`Turn '${workflow?.name ?? id}' off?`}
        message={
          <>
            Agents will no longer see this workflow in their briefings, and it cannot start new runs
            or seat new sessions. Nothing is deleted - turn it back on anytime.
          </>
        }
        confirmLabel="Turn off"
        danger={false}
        onConfirm={() => flip(false)}
        onClose={() => setPendingOff(false)}
      />

      <ConfirmDialog
        open={pendingClone}
        title={`Clone '${workflow?.name ?? id}' as '${id}-copy'?`}
        message={
          <>
            The published content - steps, instructions, helper files - is copied into a new
            workflow <code>{id}-copy</code> that is yours: published, fully editable, and
            independent of the original. Agents edit it with{" "}
            <code>cc-devthrottle workflow pull {id}-copy</code>.
          </>
        }
        confirmLabel="Clone"
        danger={false}
        onConfirm={async () => {
          if (id === undefined) return;
          try {
            const clone = await cloneWorkflow(id, `${id}-copy`, "cockpit");
            navigate(`/workflows/${encodeURIComponent(clone.id)}`);
          } catch (err) {
            setError(describeAndReport(SURFACE, "clone the workflow", err));
          }
        }}
        onClose={() => setPendingClone(false)}
      />
    </div>
  );
}
