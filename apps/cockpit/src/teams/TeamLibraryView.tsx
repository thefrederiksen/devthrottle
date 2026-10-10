import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import {
  addTeamSkill,
  addTeamWorkflowFrom,
  changeTeamItem,
  getStartingWorkflows,
  getTeamItemText,
  getTeamLibrary,
  newAddProgress,
  removeTeamItem,
  type StartingWorkflow,
  type TeamLibrary,
  type TeamLibraryItem,
} from "@devthrottle/client-core/teams/teamLibraryClient";
import { suggestSkillId } from "@devthrottle/client-core/skills/skillsClient";
import { GatewayError } from "@devthrottle/client-core/api/client";
import { Button, ConfirmDialog, ErrorBanner, LoadingState, useDismissOnBackdrop } from "../components";
import "./teams.css";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-team-library";

// THE TEAM'S SKILLS AND WORKFLOWS (Teams 6 - devthrottle_internal#2304, screen S5). With a team picked in the team
// switcher, the Skills and the Workflows entries both open this page: one list of everything the team shares, which
// every session started on that team can use. With the person's own account on screen they open the pages they always
// did - someone who never joins a team sees no change.
//
// THE GATEWAY DECIDES WHAT THIS PERSON MAY DO (rule 7). Whether the Add buttons show, whether a row can be changed or
// removed, the sentence a Developer reads instead, and who changed each item all come from GET /teams/{id}/library.
// A Collaborator is refused the list by the Gateway, and the page shows the Gateway's own sentence. The server refuses
// a change whatever this page shows; hiding a button here is presentation, not the rule.

/**
 * The Skills and Workflows routes: the team's page when a team is on screen, otherwise the person's own page. A page
 * that only exists for the own library (one workflow's detail) passes `team` to say where a team on screen goes
 * instead, so no page acts on the own library while the rail names a team.
 */
export function TeamOrOwn({ own, team }: { own: ReactNode; team?: ReactNode }) {
  const { current, resolving, status, error } = useCurrentTeam();
  // A remembered team the Gateway has not confirmed yet: never flash the person's own library in its place.
  if (resolving) {
    return status === "error" && error !== null
      ? <div className="page"><ErrorBanner message={error} /></div>
      : <div className="page"><LoadingState message="Loading your team..." /></div>;
  }
  if (current === null) return <>{own}</>;
  return team !== undefined ? <>{team}</> : <TeamLibraryView key={current.id} team={current} />;
}

/** The code the Gateway's team gate puts on every refusal (TeamEndpointGate.RefusalCode). */
const TEAM_REFUSAL_CODE = "team_action_refused";

type Dialog =
  | { kind: "view"; item: TeamLibraryItem }
  | { kind: "change"; item: TeamLibraryItem }
  | { kind: "add-skill" }
  | { kind: "add-workflow" };

export function TeamLibraryView({ team }: { team: TeamSummary }) {
  const [library, setLibrary] = useState<TeamLibrary | null>(null);
  const [error, setError] = useState<string | null>(null);
  // The Gateway's refusal of the list itself (a Collaborator): an answer, not a failure, so it is shown as a note
  // with no retry - in the Gateway's own words.
  const [refusal, setRefusal] = useState<string | null>(null);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [pendingRemove, setPendingRemove] = useState<TeamLibraryItem | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const fresh = await getTeamLibrary(team.id, signal);
      setLibrary(fresh);
      setError(null);
      setRefusal(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      if (err instanceof GatewayError && err.code === TEAM_REFUSAL_CODE && err.serverReason !== undefined) {
        setRefusal(describeAndReport(SURFACE, "read the team's skills and workflows", err));
        return;
      }
      setError(describeAndReport(SURFACE, "read the team's skills and workflows", err));
    }
  }, [team.id]);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  const done = () => {
    setDialog(null);
    void load();
  };

  return (
    <div className="page wf">
      <header className="ui-page-header">
        <div className="ui-page-header-text">
          <p className="wf-eyebrow">Shared by the team</p>
          <h1 className="ui-page-title">Skills and workflows</h1>
          {refusal === null ? (
            <p className="ui-page-subtitle">
              Shared by the {team.name} team. Every session you start on this team can use them.
            </p>
          ) : null}
        </div>
        {library?.canChange === true ? (
          <div className="tl-header-actions">
            <Button variant="secondary" onClick={() => setDialog({ kind: "add-workflow" })}>Add workflow</Button>
            <Button variant="primary" onClick={() => setDialog({ kind: "add-skill" })}>Add skill</Button>
          </div>
        ) : null}
      </header>

      {refusal !== null ? (
        <p className="tl-hint" role="note">{refusal}</p>
      ) : error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : library === null ? (
        <LoadingState message="Loading the team's skills and workflows..." />
      ) : (
        <>
          {library.items.length === 0 ? (
            <p className="tl-empty">The {library.team.name} team has no skills or workflows of its own yet.</p>
          ) : (
            <table className="tl-table">
              <thead>
                <tr><th>Name</th><th>Kind</th><th>Changed</th><th><span className="tl-sr">Actions</span></th></tr>
              </thead>
              <tbody>
                {library.items.map((item) => (
                  <tr key={`${item.kind}:${item.id}`}>
                    <td>
                      <div className="tl-name">{item.name}</div>
                      <div className="tl-summary">{item.summary}</div>
                    </td>
                    <td className="tl-dim">{item.kind}</td>
                    <td className="tl-dim">{item.changedBy}, {shortDate(item.changedAtUtc)}</td>
                    <td className="tl-actions">
                      <button className="wf-linklike" onClick={() => setDialog({ kind: "view", item })} aria-label={`View ${item.name}`}>View</button>
                      {item.canChange ? (
                        <>
                          <button className="wf-linklike" onClick={() => setDialog({ kind: "change", item })} aria-label={`Change ${item.name}`}>Change</button>
                          <button className="wf-linklike" onClick={() => setPendingRemove(item)} aria-label={`Remove ${item.name}`}>Remove</button>
                        </>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {library.changeRefusal !== null ? <p className="tl-hint" role="note">{library.changeRefusal}</p> : null}
          {library.builtInNote !== "" ? <p className="tl-hint">{library.builtInNote}</p> : null}
        </>
      )}

      {dialog?.kind === "view" ? <ViewDialog teamId={team.id} item={dialog.item} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === "change" ? <ChangeDialog teamId={team.id} item={dialog.item} onClose={() => setDialog(null)} onDone={done} /> : null}
      {dialog?.kind === "add-skill" ? <AddSkillDialog teamId={team.id} onClose={() => setDialog(null)} onDone={done} /> : null}
      {dialog?.kind === "add-workflow" ? <AddWorkflowDialog teamId={team.id} onClose={() => setDialog(null)} onDone={done} /> : null}

      <ConfirmDialog
        open={pendingRemove !== null}
        title={`Remove '${pendingRemove?.name ?? ""}' from the team?`}
        message={<>Sessions on the {team.name} team will no longer get this {pendingRemove?.kind.toLowerCase()}. Its history is kept.</>}
        confirmLabel="Remove"
        action="remove it from the team"
        onConfirm={async () => {
          if (pendingRemove === null) return;
          await removeTeamItem(team.id, pendingRemove);
          await load();
        }}
        onClose={() => setPendingRemove(null)}
      />
    </div>
  );
}

function shortDate(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

function DialogFrame({ label, busy, onClose, children }: { label: string; busy: boolean; onClose: () => void; children: ReactNode }) {
  const dismiss = useDismissOnBackdrop(busy ? undefined : onClose);
  return (
    <div className="wf-dialog-backdrop" role="presentation" {...dismiss}>
      <div className="wf-dialog tl-dialog" role="dialog" aria-modal="true" aria-label={label}>
        <h2 className="wf-dialog-title">{label}</h2>
        {children}
      </div>
    </div>
  );
}

function ViewDialog({ teamId, item, onClose }: { teamId: string; item: TeamLibraryItem; onClose: () => void }) {
  const [text, setText] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const ctrl = new AbortController();
    getTeamItemText(teamId, item, ctrl.signal).then(setText, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, `read the team ${item.kind.toLowerCase()}`, err));
    });
    return () => ctrl.abort();
  }, [teamId, item]);
  return (
    <DialogFrame label={item.name} busy={false} onClose={onClose}>
      <p className="wf-dialog-hint">{item.summary}</p>
      {error !== null ? <p className="wf-dialog-error">{error}</p> : text === null ? <LoadingState /> : <pre className="tl-text">{text}</pre>}
      <div className="wf-dialog-actions"><Button variant="primary" onClick={onClose}>Close</Button></div>
    </DialogFrame>
  );
}

function ChangeDialog({ teamId, item, onClose, onDone }: { teamId: string; item: TeamLibraryItem; onClose: () => void; onDone: () => void }) {
  const [summary, setSummary] = useState(item.summary);
  const [text, setText] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const ctrl = new AbortController();
    getTeamItemText(teamId, item, ctrl.signal).then(setText, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, `read the team ${item.kind.toLowerCase()}`, err));
    });
    return () => ctrl.abort();
  }, [teamId, item]);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await changeTeamItem(teamId, item, { summary: summary.trim(), text: text ?? "" });
      onDone();
    } catch (err) {
      setError(describeAndReport(SURFACE, `change the team ${item.kind.toLowerCase()}`, err));
      setBusy(false);
    }
  };

  return (
    <DialogFrame label={`Change ${item.name}`} busy={busy} onClose={onClose}>
      <label className="wf-field">
        <span>What it does - one line</span>
        <input type="text" value={summary} onChange={(e) => setSummary(e.target.value)} />
      </label>
      <label className="wf-field">
        <span>{item.kind === "Workflow" ? "Instructions" : "What the agent reads"}</span>
        {text === null ? <LoadingState /> : <textarea className="tl-textarea" value={text} onChange={(e) => setText(e.target.value)} />}
      </label>
      {error !== null ? <p className="wf-dialog-error">{error}</p> : null}
      <div className="wf-dialog-actions">
        <Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button>
        {/* Empty words are never offered: the Gateway accepts them as a draft and then refuses to publish them. */}
        <Button variant="primary" onClick={() => void save()} disabled={busy || text === null || text.trim() === "" || summary.trim() === ""}>
          {busy ? "Saving..." : "Save for the team"}
        </Button>
      </div>
    </DialogFrame>
  );
}

function AddSkillDialog({ teamId, onClose, onDone }: { teamId: string; onClose: () => void; onDone: () => void }) {
  const [name, setName] = useState("");
  const [summary, setSummary] = useState("");
  const [body, setBody] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // How far an earlier attempt got: once the skill exists, its id is fixed and a second attempt finishes it.
  const progress = useRef(newAddProgress());
  const [createdId, setCreatedId] = useState<string | null>(null);
  const id = createdId ?? suggestSkillId(name);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await addTeamSkill(teamId, { id, name: name.trim(), summary: summary.trim(), bodyMarkdown: body }, progress.current);
      onDone();
    } catch (err) {
      setCreatedId(progress.current.createdId);
      setError(describeAndReport(SURFACE, "add the skill", err));
      setBusy(false);
    }
  };

  return (
    <DialogFrame label="Add skill" busy={busy} onClose={onClose}>
      <label className="wf-field">
        <span>Name</span>
        <input type="text" value={name} autoFocus onChange={(e) => setName(e.target.value)} placeholder="Release checklist" />
      </label>
      {id !== "" ? <p className="wf-dialog-hint">Id: <code>{id}</code></p> : null}
      <label className="wf-field">
        <span>What it does - one line, and this is the line every session sees</span>
        <input type="text" value={summary} onChange={(e) => setSummary(e.target.value)} placeholder="The steps every release follows." />
      </label>
      <label className="wf-field">
        <span>What the agent reads</span>
        <textarea className="tl-textarea" value={body} onChange={(e) => setBody(e.target.value)} placeholder={"# Release checklist\n\n1. ..."} />
      </label>
      {error !== null ? <p className="wf-dialog-error">{error}</p> : null}
      <div className="wf-dialog-actions">
        <Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="primary" onClick={() => void save()} disabled={busy || id === "" || summary.trim() === "" || body.trim() === ""}>
          {busy ? "Adding..." : "Add to the team"}
        </Button>
      </div>
    </DialogFrame>
  );
}

function AddWorkflowDialog({ teamId, onClose, onDone }: { teamId: string; onClose: () => void; onDone: () => void }) {
  const [starts, setStarts] = useState<StartingWorkflow[] | null>(null);
  const [source, setSource] = useState("");
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // How far an earlier attempt got: once the copy exists, its id and starting point are fixed and a second attempt
  // finishes naming it.
  const progress = useRef(newAddProgress());
  const [createdId, setCreatedId] = useState<string | null>(null);
  const id = createdId ?? suggestSkillId(name);

  useEffect(() => {
    const ctrl = new AbortController();
    getStartingWorkflows(teamId, ctrl.signal).then(
      (list) => {
        setStarts(list);
        if (list.length > 0) setSource(list[0].id);
      },
      (err: unknown) => {
        if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, "read the workflows to start from", err));
      },
    );
    return () => ctrl.abort();
  }, [teamId]);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await addTeamWorkflowFrom(teamId, source, { id, name: name.trim() }, progress.current);
      onDone();
    } catch (err) {
      setCreatedId(progress.current.createdId);
      setError(describeAndReport(SURFACE, "add the workflow", err));
      setBusy(false);
    }
  };

  return (
    <DialogFrame label="Add workflow" busy={busy} onClose={onClose}>
      <p className="wf-dialog-hint">
        A team workflow starts as a copy of one of DevThrottle&apos;s. The copy is the team&apos;s: change its words
        afterwards with Change.
      </p>
      {starts === null && error === null ? <LoadingState /> : null}
      {starts !== null ? (
        <>
          <label className="wf-field">
            <span>Start from</span>
            <select value={source} onChange={(e) => setSource(e.target.value)} disabled={createdId !== null}>
              {starts.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
            </select>
          </label>
          <label className="wf-field">
            <span>Name</span>
            <input type="text" value={name} onChange={(e) => setName(e.target.value)} placeholder="Our review" />
          </label>
          {id !== "" ? <p className="wf-dialog-hint">Id: <code>{id}</code></p> : null}
        </>
      ) : null}
      {error !== null ? <p className="wf-dialog-error">{error}</p> : null}
      <div className="wf-dialog-actions">
        <Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="primary" onClick={() => void save()} disabled={busy || source === "" || id === "" || name.trim() === ""}>
          {busy ? "Adding..." : "Add to the team"}
        </Button>
      </div>
    </DialogFrame>
  );
}
