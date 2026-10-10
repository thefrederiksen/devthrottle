import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  cloneWorkflow,
  createWorkflow,
  getWorkflowInstructions,
  getWorkflowRuns,
  getWorkflows,
  setWorkflowEnabled,
  suggestWorkflowId,
  type WorkflowDefinition,
  type WorkflowRunSummary,
} from "@devthrottle/client-core/workflows/workflowsClient";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { Button, ConfirmDialog, ErrorBanner, LoadingState, useDismissOnBackdrop } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { ClipboardRefusedError } from "../components/clipboardFailure";

const SURFACE = "cockpit-workflows";

// The Workflows REGISTER (register redesign, approved mockup direction A). Workflows are the rules
// this fleet works by, and the page reads with that weight: a ledger, not cards. One row per
// workflow with a colored state spine (in force / draft waiting / off), the owner's switch on every
// row - built-ins included - provenance and activity columns, and the standing lifecycle strip that
// answers "how does this reach my agents" as page furniture rather than a buried help link.
//
// The switch is the owner ruling verbatim: off removes the workflow from every agent's briefing and
// refuses new runs and seats - and deletes nothing. Turning something off is consequential enough
// to confirm; turning it back on is not.
export function WorkflowsView() {
  const [workflows, setWorkflows] = useState<WorkflowDefinition[] | null>(null);
  const [runs, setRuns] = useState<WorkflowRunSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [pendingOff, setPendingOff] = useState<WorkflowDefinition | null>(null);
  const [explainerOpen, setExplainerOpen] = useState(false);
  // Preview + clone straight from the register (owner ask, 2026-07-24): reading a workflow's actual
  // conduct and taking a copy of it are the two acts the register exists to invite, so both live on
  // every row - built-ins included, because cloning IS the way to customize a built-in.
  const [preview, setPreview] = useState<WorkflowDefinition | null>(null);
  const [pendingClone, setPendingClone] = useState<WorkflowDefinition | null>(null);
  const navigate = useNavigate();

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      // The catalog IS the page (no fallback to an empty register); the runs read only feeds the
      // activity column, so its failure costs the numbers and says so in place - an older Gateway
      // without the run routes must not blank the whole register.
      const fresh = await getWorkflows(signal);
      setWorkflows(fresh);
      setError(null);
      try {
        setRuns(await getWorkflowRuns(200, signal));
      } catch {
        if (signal?.aborted !== true) setRuns(null);
      }
    } catch (err) {
      if (signal?.aborted === true) return;
      setError(describeAndReport(SURFACE, "load the workflows", err));
    }
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  const activity = useMemo(() => {
    const byWorkflow = new Map<string, { count: number; newestUtc: string }>();
    for (const run of runs ?? []) {
      const entry = byWorkflow.get(run.workflowId);
      if (entry === undefined) {
        byWorkflow.set(run.workflowId, { count: 1, newestUtc: run.createdUtc });
      } else {
        entry.count += 1;
        if (run.createdUtc > entry.newestUtc) entry.newestUtc = run.createdUtc;
      }
    }
    return byWorkflow;
  }, [runs]);

  // A failed flip is never silent: the error lands in the page's error state (with Retry), and the
  // register re-renders from the Gateway's truth rather than an optimistic guess.
  const flip = async (workflow: WorkflowDefinition, enabled: boolean) => {
    try {
      await setWorkflowEnabled(workflow.id, enabled, "cockpit");
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
          <p className="wf-eyebrow">Fleet governance</p>
          <h1 className="ui-page-title">Workflows</h1>
          <p className="ui-page-subtitle">
            The rules your fleet works by. Every agent on every machine reads these from the Gateway
            - the same catalog every session is briefed with at launch.
          </p>
        </div>
        <Button variant="primary" onClick={() => setAdding(true)}>Add workflow</Button>
      </header>

      <div className="wf-lifecycle">
        <span className="wf-lc-step"><span className="wf-lc-who">any agent</span> authors</span>
        <span className="wf-lc-arrow">-&gt;</span>
        <span className="wf-lc-step">draft</span>
        <span className="wf-lc-arrow">-&gt;</span>
        <span className="wf-lc-step">publish</span>
        <span className="wf-lc-arrow">-&gt;</span>
        <span className="wf-lc-step wf-lc-live">in force everywhere, instantly</span>
        <span className="wf-lc-tail">
          nothing to deploy - no restart - running missions keep their pinned version
        </span>
      </div>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : workflows === null ? (
        <LoadingState message="Loading the register..." />
      ) : (
        <div className="wf-register">
          <div className="wf-reg-head" aria-hidden="true">
            <div className="wf-spine"></div>
            <div>Workflow</div>
            <div>State</div>
            <div>Provenance</div>
            <div>Recent activity</div>
            <div>Actions</div>
          </div>
          {workflows.map((wf) => (
            <RegisterRow
              key={wf.id}
              workflow={wf}
              activity={activity.get(wf.id)}
              runsLoaded={runs !== null}
              onFlip={(enabled) => {
                if (!enabled) setPendingOff(wf);
                else void flip(wf, true);
              }}
              onPreview={() => setPreview(wf)}
              onClone={() => setPendingClone(wf)}
            />
          ))}
          <div className="wf-reg-foot">
            <span>
              Agents read and author these with <code>cc-devthrottle workflow ...</code>
            </span>
            <button
              className="wf-linklike"
              aria-expanded={explainerOpen}
              aria-controls="wf-explainer-panel"
              onClick={() => setExplainerOpen((open) => !open)}
            >
              How workflows reach your agents
            </button>
          </div>
        </div>
      )}

      {explainerOpen ? <Explainer /> : null}

      {adding ? (
        <AddWorkflowDialog onClose={() => setAdding(false)} onCreated={() => void load()} />
      ) : null}

      <ConfirmDialog
        open={pendingOff !== null}
        title={`Turn '${pendingOff?.name ?? ""}' off?`}
        message={
          <>
            Agents will no longer see this workflow in their briefings, and it cannot start new runs
            or seat new sessions. Nothing is deleted - history and versions stay, and you can turn
            it back on anytime.
          </>
        }
        confirmLabel="Turn off"
        danger={false}
        onConfirm={async () => {
          if (pendingOff !== null) await flip(pendingOff, false);
        }}
        onClose={() => setPendingOff(null)}
      />

      {preview !== null ? (
        <WorkflowPreviewDialog
          workflow={preview}
          onClose={() => setPreview(null)}
          onClone={() => {
            setPendingClone(preview);
            setPreview(null);
          }}
        />
      ) : null}

      <ConfirmDialog
        open={pendingClone !== null}
        title={`Clone '${pendingClone?.name ?? ""}' as '${pendingClone?.id ?? ""}-copy'?`}
        message={
          <>
            The published content - steps, instructions, helper files - is copied into a new
            workflow <code>{pendingClone?.id}-copy</code> that is yours: published, fully editable,
            and independent of the original.
          </>
        }
        confirmLabel="Clone"
        danger={false}
        onConfirm={async () => {
          if (pendingClone === null) return;
          try {
            const clone = await cloneWorkflow(pendingClone.id, `${pendingClone.id}-copy`, "cockpit");
            navigate(`/workflows/${encodeURIComponent(clone.id)}`);
          } catch (err) {
            setError(describeAndReport(SURFACE, "clone the workflow", err));
          }
        }}
        onClose={() => setPendingClone(null)}
      />
    </div>
  );
}

// The conduct preview (owner ask, 2026-07-24): the popup that answers "what does this workflow
// actually say" without leaving the register - the shape up top (badges, when to use, steps), then
// the full instruction markdown, scrollable, through the same sanitized renderer the detail page
// trusts. It is not a dead end: the clone decision happens right here, and the full page is one
// click away. The conduct is fetched PINNED to the version the row reported, matching the detail
// page's torn-read discipline (an unpinned read racing a publish could pair v1 steps with v2 text -
// and the pinned read also keeps the preview working for an OFF workflow, whose unversioned read
// the Gateway refuses).
function WorkflowPreviewDialog({
  workflow,
  onClose,
  onClone,
}: {
  workflow: WorkflowDefinition;
  onClose: () => void;
  onClone: () => void;
}) {
  const [instructions, setInstructions] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    getWorkflowInstructions(workflow.id, workflow.version, ctrl.signal).then(
      (md) => setInstructions(md),
      (err) => {
        if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, "read the workflow", err));
      },
    );
    return () => ctrl.abort();
  }, [workflow.id, workflow.version]);

  // Closes on a backdrop click, but never on a drag that started inside - reading a workflow means
  // selecting its instructions with the mouse, which must not dismiss the preview (see
  // useDismissOnBackdrop).
  const dismiss = useDismissOnBackdrop(onClose);

  return (
    <div className="wf-dialog-backdrop" role="presentation" {...dismiss}>
      <div
        className="wf-dialog wf-preview"
        role="dialog"
        aria-modal="true"
        aria-label={`Preview of ${workflow.name}`}
      >
        <h2 className="wf-dialog-title">
          {workflow.name}
          {workflow.isBuiltIn === true ? <span className="wf-badge wf-badge-builtin">Built-in</span> : null}
          {typeof workflow.version === "number" ? <span className="wf-badge">v{workflow.version}</span> : null}
        </h2>
        <p className="wf-dialog-hint">{workflow.summary}</p>
        {workflow.whenToUse !== undefined && workflow.whenToUse !== "" ? (
          <p className="wf-preview-fact"><b>When to use it:</b> {workflow.whenToUse}</p>
        ) : null}
        {workflow.humanCheckpoint !== undefined && workflow.humanCheckpoint !== "" ? (
          <p className="wf-preview-fact"><b>You are asked:</b> {workflow.humanCheckpoint}</p>
        ) : null}
        {workflow.steps !== undefined && workflow.steps.length > 0 ? (
          <ol className="wf-preview-steps">
            {workflow.steps.map((step, i) => (
              <li key={step.name}>
                <span className="wf-detail-step-num">{i + 1}.</span> <strong>{step.name}</strong> - {step.doer}
                {step.reviewer !== null && step.reviewer !== undefined
                  ? `, reviewed by ${step.reviewer}`
                  : ", no review"}
              </li>
            ))}
          </ol>
        ) : null}
        <div className="wf-preview-body">
          {error !== null ? (
            <p className="wf-dialog-error">{error}</p>
          ) : instructions === null ? (
            <LoadingState message="Loading the conduct..." />
          ) : (
            <div
              className="wf-conduct-body"
              dangerouslySetInnerHTML={{ __html: markdownToHtml(instructions) }}
            />
          )}
        </div>
        <div className="wf-dialog-actions">
          <Link className="wf-preview-full-link" to={`/workflows/${encodeURIComponent(workflow.id)}`}>
            Open full page
          </Link>
          <Button variant="secondary" onClick={onClose}>Close</Button>
          <Button variant="primary" onClick={onClone}>Clone</Button>
        </div>
      </div>
    </div>
  );
}

function RegisterRow({
  workflow,
  activity,
  runsLoaded,
  onFlip,
  onPreview,
  onClone,
}: {
  workflow: WorkflowDefinition;
  activity: { count: number; newestUtc: string } | undefined;
  runsLoaded: boolean;
  onFlip: (enabled: boolean) => void;
  onPreview: () => void;
  onClone: () => void;
}) {
  const off = workflow.enabled === false;
  const spine = off ? "wf-spine-off" : workflow.hasDraft === true ? "wf-spine-draft" : "wf-spine-on";
  const stateLabel = off ? "Off" : workflow.hasDraft === true ? "Draft waiting" : "In force";
  const stateClass = off ? "wf-state-off" : workflow.hasDraft === true ? "wf-state-draft" : "wf-state-on";

  return (
    <div className={off ? "wf-reg-row wf-reg-row-off" : "wf-reg-row"}>
      <div className={`wf-spine ${spine}`}></div>
      <div className="wf-cell-main">
        <Link className="wf-reg-id" to={`/workflows/${encodeURIComponent(workflow.id)}`}>
          {workflow.id}
        </Link>
        {workflow.isBuiltIn === true ? <span className="wf-badge wf-badge-builtin">Built-in</span> : null}
        {workflow.isBuiltIn === false ? <span className="wf-badge">Custom</span> : null}
        <div className="wf-reg-name">{workflow.name}</div>
        <div className="wf-reg-sum">{workflow.summary}</div>
      </div>
      <div className="wf-cell-state">
        {/* The switch renders only when the Gateway reported the enabled flag: an older Gateway
            without the switch routes must not show a control that can only fail. */}
        {workflow.enabled !== undefined ? (
          <button
            className={off ? "wf-switch" : "wf-switch wf-switch-on"}
            role="switch"
            aria-checked={!off}
            aria-label={`${workflow.name}: ${off ? "off - turn on" : "in force - turn off"}`}
            onClick={() => onFlip(off)}
          ></button>
        ) : null}
        <span className={`wf-state-label ${stateClass}`}>{stateLabel}</span>
      </div>
      <div className="wf-cell-prov">
        {typeof workflow.version === "number" ? (
          <>
            <b>v{workflow.version}</b> in force
            <br />
          </>
        ) : null}
        {workflow.updatedUtc !== undefined ? <>updated {workflow.updatedUtc.slice(0, 10)}</> : null}
      </div>
      <div className="wf-cell-activity">
        {off ? (
          <span className="wf-off-note">agents will not see or run this</span>
        ) : activity !== undefined ? (
          <>
            <b>{activity.count}</b> {activity.count === 1 ? "run" : "runs"}
            <br />
            last: {activity.newestUtc.slice(0, 10)}
          </>
        ) : runsLoaded ? (
          <span>no recent runs</span>
        ) : (
          <span className="wf-off-note">activity unavailable</span>
        )}
      </div>
      <div className="wf-cell-actions">
        <button className="wf-linklike" onClick={onPreview} aria-label={`Preview ${workflow.name}`}>
          Preview
        </button>
        <button className="wf-linklike" onClick={onClone} aria-label={`Clone ${workflow.name}`}>
          Clone
        </button>
      </div>
    </div>
  );
}

// The standing answers to "how and when do these apply" - kept on the page because the register is
// where the question gets asked.
function Explainer() {
  return (
    <section className="wf-explainer" id="wf-explainer-panel">
      <h2>How workflows reach your agents</h2>
      <dl>
        <dt>out of the box</dt>
        <dd>
          A fresh Gateway ships Mission, Standalone, and Standalone with review - working from the
          first minute on every connected machine.
        </dd>
        <dt>no restart</dt>
        <dd>
          Agents fetch the conduct from the Gateway at the moment they use it. Publishing is the
          deployment: every next read, on every machine, gets the new version.
        </dd>
        <dt>pinned runs</dt>
        <dd>
          A session seated on a mission keeps the version it started under. Publishing mid-run never
          changes the rules under a running mission.
        </dd>
        <dt>off</dt>
        <dd>
          A workflow you turn off disappears from agents&apos; briefings and cannot start runs or
          seat sessions. Nothing is deleted, and the flip is instant both ways.
        </dd>
      </dl>
    </section>
  );
}

// The add dialog: name + one-line summary, nothing else. Submitting creates a DRAFT on the Gateway
// (invisible to the fleet until an agent fleshes it out and publishes) and shows the copyable
// handoff prompt. Errors surface inline and the dialog stays open - the failure is never swallowed.
function AddWorkflowDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [summary, setSummary] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [createdId, setCreatedId] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const id = suggestWorkflowId(name);
  const handoff = createdId === null
    ? ""
    : `Author the '${name.trim()}' workflow (id ${createdId}). Pull it with: cc-devthrottle workflow pull ${createdId} --dir <a working directory> - write its instructions.md (the conduct agents will follow), fill workflow.json (steps, outcome criteria), then push and publish: cc-devthrottle workflow push ${createdId} --dir <the directory> && cc-devthrottle workflow publish ${createdId}`;

  // The backdrop dismisses only while the form is idle: dismissing DURING the create leaves the
  // draft half-born with its handoff prompt never shown, and dismissing the success state would
  // lose the prompt. It also never dismisses on a drag that started inside the form - typing a name
  // and then re-selecting part of it with the mouse must not throw the draft away (see
  // useDismissOnBackdrop).
  const dismiss = useDismissOnBackdrop(createdId === null && !busy ? onClose : undefined);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const draft = await createWorkflow({ id, name: name.trim(), summary: summary.trim() });
      setCreatedId(draft.workflowId);
      onCreated();
    } catch (err) {
      setError(describeAndReport(SURFACE, "create the workflow", err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="wf-dialog-backdrop" role="presentation" {...dismiss}>
      <div
        className="wf-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Add workflow"
      >
        {createdId === null ? (
          <>
            <h2 className="wf-dialog-title">Add workflow</h2>
            <p className="wf-dialog-hint">
              This creates a DRAFT. Your agents do the actual authoring - you will get the exact
              prompt to hand one after this step.
            </p>
            <label className="wf-dialog-field">
              <span>Name</span>
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Release train"
                autoFocus
              />
            </label>
            <label className="wf-dialog-field">
              <span>One-line summary</span>
              <input
                value={summary}
                onChange={(e) => setSummary(e.target.value)}
                placeholder="Cut, verify, and announce a release."
              />
            </label>
            {id.length > 1 ? <p className="wf-dialog-id">Id: {id}</p> : null}
            {error !== null ? <p className="wf-dialog-error">{error}</p> : null}
            <div className="wf-dialog-actions">
              <Button variant="secondary" onClick={onClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                variant="primary"
                onClick={() => void submit()}
                disabled={busy || id.length < 2 || summary.trim().length === 0}
              >
                {busy ? "Creating..." : "Create draft"}
              </Button>
            </div>
          </>
        ) : (
          <>
            <h2 className="wf-dialog-title">Draft created</h2>
            <p className="wf-dialog-hint">
              Hand this to any agent - it authors the workflow and publishes it to the whole fleet:
            </p>
            <pre className="wf-dialog-handoff">{handoff}</pre>
            {error !== null ? <p className="wf-dialog-error">{error}</p> : null}
            <div className="wf-dialog-actions">
              <Button
                variant="secondary"
                onClick={() => {
                  navigator.clipboard?.writeText(handoff).then(
                    () => setCopied(true),
                    (err: unknown) => setError(`${describeAndReport(SURFACE, "copy the prompt", new ClipboardRefusedError(err))} Select the text above and copy it manually.`),
                  ) ?? setError("Copy is unavailable here - select the text above and copy it manually."); // error-report-exempt: this page has no clipboard (an address the browser does not trust); nothing failed
                }}
              >
                {copied ? "Copied" : "Copy prompt"}
              </Button>
              <Button variant="primary" onClick={onClose}>Done</Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
