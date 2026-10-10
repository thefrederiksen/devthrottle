import { useCallback, useEffect, useState } from "react";
import {
  cloneSkill,
  createSkill,
  getSkillBody,
  getSkills,
  getSkillVersionDetail,
  publishSkill,
  readFileForSkill,
  setSkillEnabled,
  suggestSkillId,
  updateSkillDraft,
  type SkillDefinition,
  type SkillFile,
  type SkillVersionDetail,
} from "@devthrottle/client-core/skills/skillsClient";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { Button, ConfirmDialog, ErrorBanner, LoadingState, useDismissOnBackdrop } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-skills";

// The Skills REGISTER - the central skill library (devthrottle_internal issue 995). Built as the
// Workflows register's twin and sharing its stylesheet on purpose: they are two lists on one shelf,
// and a user who has learned one has learned the other. It sits BESIDE Workflows in the main
// navigation rather than inside Settings, because Workflows is already a page of its own and a
// Settings tab would drag the phone's settings contract along with it.
//
// What the page is FOR: skills used to be copied onto every machine by the installer, so fixing one
// word needed a release. Here, publishing IS the deployment - and the two controls that matter are
// the owner's switch (off = left out of every briefing, nothing deleted) and clone (the only way to
// customize a read-only built-in).
export function SkillsView() {
  const [skills, setSkills] = useState<SkillDefinition[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [pendingOff, setPendingOff] = useState<SkillDefinition | null>(null);
  const [explainerOpen, setExplainerOpen] = useState(false);
  const [preview, setPreview] = useState<SkillDefinition | null>(null);
  const [pendingClone, setPendingClone] = useState<SkillDefinition | null>(null);
  const [editingFiles, setEditingFiles] = useState<SkillDefinition | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      // The register IS the page: no fallback to an empty list, which would read as "you have no
      // skills" when the truth is "the Gateway could not be reached".
      const fresh = await getSkills(signal);
      setSkills(fresh);
      setError(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      setError(describeAndReport(SURFACE, "load the skills", err));
    }
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  // A failed flip is never silent: the error lands in the page's error state (with Retry), and the
  // register re-renders from the Gateway's truth rather than an optimistic guess.
  const flip = async (skill: SkillDefinition, enabled: boolean) => {
    try {
      await setSkillEnabled(skill.id, enabled, "cockpit");
    } catch (err) {
      setError(describeAndReport(SURFACE, enabled ? "turn the skill on" : "turn the skill off", err));
      return;
    }
    await load();
  };

  return (
    <div className="page wf">
      <header className="ui-page-header">
        <div className="ui-page-header-text">
          <p className="wf-eyebrow">Fleet capability</p>
          <h1 className="ui-page-title">Skills</h1>
          <p className="ui-page-subtitle">
            Capabilities your agents can reach for. Every agent on every machine reads these from the
            Gateway - named in the briefing at launch, fetched in full only when one is used.
          </p>
        </div>
        <Button variant="primary" onClick={() => setAdding(true)}>Add skill</Button>
      </header>

      <div className="wf-lifecycle">
        <span className="wf-lc-step"><span className="wf-lc-who">any agent</span> authors</span>
        <span className="wf-lc-arrow">-&gt;</span>
        <span className="wf-lc-step">draft</span>
        <span className="wf-lc-arrow">-&gt;</span>
        <span className="wf-lc-step">publish</span>
        <span className="wf-lc-arrow">-&gt;</span>
        <span className="wf-lc-step wf-lc-live">available everywhere, instantly</span>
        <span className="wf-lc-tail">
          nothing installed - nothing to update - an agent fetches a skill only when it uses one
        </span>
      </div>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : skills === null ? (
        <LoadingState message="Loading the register..." />
      ) : (
        <div className="wf-register">
          <div className="wf-reg-head" aria-hidden="true">
            <div className="wf-spine"></div>
            <div>Skill</div>
            <div>State</div>
            <div>Provenance</div>
            <div>Triggers</div>
            <div>Actions</div>
          </div>
          {skills.map((skill) => (
            <RegisterRow
              key={skill.id}
              skill={skill}
              onFlip={(enabled) => {
                if (!enabled) setPendingOff(skill);
                else void flip(skill, true);
              }}
              onPreview={() => setPreview(skill)}
              onClone={() => setPendingClone(skill)}
              onEditFiles={() => setEditingFiles(skill)}
            />
          ))}
          <div className="wf-reg-foot">
            <span>
              Agents read and author these with <code>cc-devthrottle skill ...</code>
            </span>
            <button
              className="wf-linklike"
              aria-expanded={explainerOpen}
              aria-controls="sk-explainer-panel"
              onClick={() => setExplainerOpen((open) => !open)}
            >
              How skills reach your agents
            </button>
          </div>
        </div>
      )}

      {explainerOpen ? <Explainer /> : null}

      {adding ? (
        <AddSkillDialog onClose={() => setAdding(false)} onCreated={() => void load()} />
      ) : null}

      <ConfirmDialog
        open={pendingOff !== null}
        title={`Turn '${pendingOff?.name ?? ""}' off?`}
        message={
          <>
            Agents will no longer see this skill in their briefings, and fetching it will be refused
            with a message saying you switched it off. Nothing is deleted - versions and history stay,
            and you can turn it back on anytime.
          </>
        }
        confirmLabel="Turn off"
        danger={false}
        onConfirm={async () => {
          if (pendingOff !== null) await flip(pendingOff, false);
        }}
        onClose={() => setPendingOff(null)}
      />

      {editingFiles !== null ? (
        <SkillFilesDialog
          skill={editingFiles}
          onClose={() => setEditingFiles(null)}
          onSaved={() => void load()}
        />
      ) : null}

      {preview !== null ? (
        <SkillPreviewDialog
          skill={preview}
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
            The published content - the summary, triggers, body, and supporting files - is copied into
            a new skill <code>{pendingClone?.id}-copy</code> that is yours: published, fully editable,
            and independent of the original.
          </>
        }
        confirmLabel="Clone"
        danger={false}
        onConfirm={async () => {
          if (pendingClone === null) return;
          try {
            await cloneSkill(pendingClone.id, `${pendingClone.id}-copy`, "cockpit");
            await load();
          } catch (err) {
            setError(describeAndReport(SURFACE, "clone the skill", err));
          }
        }}
        onClose={() => setPendingClone(null)}
      />
    </div>
  );
}

// The body preview: what does this skill actually TELL an agent? Rendered through the same sanitized
// markdown renderer the rest of the Cockpit trusts, and fetched PINNED to the version the row
// reported - an unpinned read racing a publish could pair one version's summary with another's body.
//
// This is the ONLY place the Cockpit pulls a body. The register itself carries none, exactly as an
// agent's briefing carries none.
function SkillPreviewDialog({
  skill,
  onClose,
  onClone,
}: {
  skill: SkillDefinition;
  onClose: () => void;
  onClone: () => void;
}) {
  const [body, setBody] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    getSkillBody(skill.id, skill.version, ctrl.signal).then(
      (text) => setBody(text),
      (err) => {
        if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, "read the skill", err));
      },
    );
    return () => ctrl.abort();
  }, [skill.id, skill.version]);

  // Closes on a backdrop click, but never on a drag that started inside - reading a skill means
  // selecting its text with the mouse, which must not dismiss the preview.
  const dismiss = useDismissOnBackdrop(onClose);

  return (
    <div className="wf-dialog-backdrop" role="presentation" {...dismiss}>
      <div
        className="wf-dialog wf-preview"
        role="dialog"
        aria-modal="true"
        aria-label={`Preview of ${skill.name}`}
      >
        <h2 className="wf-dialog-title">
          {skill.name}
          {skill.isBuiltIn === true ? <span className="wf-badge wf-badge-builtin">Built-in</span> : null}
          {typeof skill.version === "number" ? <span className="wf-badge">v{skill.version}</span> : null}
        </h2>
        <p className="wf-dialog-hint">{skill.summary}</p>
        {skill.triggers !== undefined && skill.triggers.length > 0 ? (
          <p className="wf-preview-fact"><b>Triggers on:</b> {skill.triggers.join(", ")}</p>
        ) : null}
        {typeof skill.fileCount === "number" && skill.fileCount > 0 ? (
          <p className="wf-preview-fact">
            <b>Supporting files:</b> {skill.fileCount} - fetched with the skill when an agent uses it.
          </p>
        ) : null}
        {skill.editable === false ? (
          <p className="wf-preview-fact">
            Built in and read-only, so it can never drift from what DevThrottle ships. To change it,
            clone it into a skill of your own.
          </p>
        ) : null}
        <div className="wf-preview-body">
          {error !== null ? (
            <p className="wf-dialog-error">{error}</p>
          ) : body === null ? (
            <LoadingState message="Loading the skill..." />
          ) : (
            <div
              className="wf-conduct-body"
              dangerouslySetInnerHTML={{ __html: markdownToHtml(body) }}
            />
          )}
        </div>
        <div className="wf-dialog-actions">
          <Button variant="secondary" onClick={onClose}>Close</Button>
          <Button variant="primary" onClick={onClone}>Clone</Button>
        </div>
      </div>
    </div>
  );
}

function RegisterRow({
  skill,
  onFlip,
  onPreview,
  onClone,
  onEditFiles,
}: {
  skill: SkillDefinition;
  onFlip: (enabled: boolean) => void;
  onPreview: () => void;
  onClone: () => void;
  onEditFiles: () => void;
}) {
  const off = skill.enabled === false;
  const spine = off ? "wf-spine-off" : skill.hasDraft === true ? "wf-spine-draft" : "wf-spine-on";
  const stateLabel = off ? "Off" : skill.hasDraft === true ? "Draft waiting" : "Available";
  const stateClass = off ? "wf-state-off" : skill.hasDraft === true ? "wf-state-draft" : "wf-state-on";

  return (
    <div className={off ? "wf-reg-row wf-reg-row-off" : "wf-reg-row"}>
      <div className={`wf-spine ${spine}`}></div>
      <div className="wf-cell-main">
        <span className="wf-reg-id">{skill.id}</span>
        {skill.isBuiltIn === true ? <span className="wf-badge wf-badge-builtin">Built-in</span> : null}
        {skill.isBuiltIn === false ? <span className="wf-badge">Yours</span> : null}
        <div className="wf-reg-name">{skill.name}</div>
        <div className="wf-reg-sum">{skill.summary}</div>
      </div>
      <div className="wf-cell-state">
        {/* The switch renders only when the Gateway reported the enabled flag: an older Gateway
            without the switch routes must not show a control that can only fail. */}
        {skill.enabled !== undefined ? (
          <button
            className={off ? "wf-switch" : "wf-switch wf-switch-on"}
            role="switch"
            aria-checked={!off}
            aria-label={`${skill.name}: ${off ? "off - turn on" : "available - turn off"}`}
            onClick={() => onFlip(off)}
          ></button>
        ) : null}
        <span className={`wf-state-label ${stateClass}`}>{stateLabel}</span>
      </div>
      <div className="wf-cell-prov">
        {typeof skill.version === "number" ? (
          <>
            <b>v{skill.version}</b> {off ? "kept" : "available"}
            <br />
          </>
        ) : null}
        {skill.isBuiltIn === true ? "DevThrottle" : "yours"}
        {typeof skill.fileCount === "number" && skill.fileCount > 0 ? (
          <>
            <br />
            {skill.fileCount} {skill.fileCount === 1 ? "file" : "files"}
          </>
        ) : null}
      </div>
      <div className="wf-cell-activity">
        {off ? (
          <span className="wf-off-note">agents will not see or fetch this</span>
        ) : skill.triggers !== undefined && skill.triggers.length > 0 ? (
          <span>{skill.triggers.slice(0, 3).join(", ")}</span>
        ) : (
          <span className="wf-off-note">no triggers</span>
        )}
      </div>
      <div className="wf-cell-actions">
        <button className="wf-linklike" onClick={onPreview} aria-label={`Read ${skill.name}`}>
          Read
        </button>
        {/* The Gateway's editability verdict, rendered verbatim - never re-derived here. A built-in
            offers no Files button at all, because the write would be refused and a button that
            cannot work reads as broken. */}
        {skill.editable === true ? (
          <button className="wf-linklike" onClick={onEditFiles} aria-label={`Edit the files of ${skill.name}`}>
            Files
          </button>
        ) : null}
        <button className="wf-linklike" onClick={onClone} aria-label={`Clone ${skill.name}`}>
          Clone
        </button>
      </div>
    </div>
  );
}

// THE FILES OF A SKILL. A skill is a DIRECTORY in the Agent Skills open standard - SKILL.md plus any
// files and subdirectories - and every agent DevThrottle supervises reads that same shape. So this
// dialog is a file manager for one skill: add a file at a path, edit a text file here, upload a
// binary, remove one, then save the draft and publish.
//
// Two things it deliberately does. It shows WHICH files are executable, because a skill that carries
// a program that will run on every machine in the fleet should not be able to acquire one quietly.
// And it never opens for a built-in, because the Gateway says those are not editable and this client
// renders that verdict rather than deriving its own.
function SkillFilesDialog({
  skill,
  onClose,
  onSaved,
}: {
  skill: SkillDefinition;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [detail, setDetail] = useState<SkillVersionDetail | null>(null);
  const [files, setFiles] = useState<SkillFile[]>([]);
  const [body, setBody] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [newPath, setNewPath] = useState("");
  const backdrop = useDismissOnBackdrop(onClose);

  useEffect(() => {
    const ctrl = new AbortController();
    void (async () => {
      try {
        const version = skill.version ?? 1;
        const loaded = await getSkillVersionDetail(skill.id, version, ctrl.signal);
        setDetail(loaded);
        setFiles(loaded.files);
        setBody(loaded.bodyMarkdown);
      } catch (err) {
        if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, "open the skill's files", err));
      }
    })();
    return () => ctrl.abort();
  }, [skill.id, skill.version]);

  const save = async (thenPublish: boolean) => {
    if (detail === null) return;
    setBusy(true);
    setError(null);
    try {
      await updateSkillDraft(
        skill.id,
        {
          name: detail.name,
          summary: detail.summary,
          triggers: detail.triggers,
          bodyMarkdown: body,
          files,
          license: detail.license,
          compatibility: detail.compatibility,
          allowedTools: detail.allowedTools,
          metadata: detail.metadata,
        },
        // The hash of the version this edit was made against. The Gateway refuses the write if it is
        // no longer current, so a concurrent author is never silently overwritten.
        detail.contentHash,
        "cockpit:files-editor",
      );
      if (thenPublish) await publishSkill(skill.id);
      onSaved();
      onClose();
    } catch (err) {
      setError(describeAndReport(SURFACE, "save the skill's files", err));
    } finally {
      setBusy(false);
    }
  };

  const addFiles = async (picked: FileList | null) => {
    if (picked === null) return;
    const added: SkillFile[] = [];
    for (const file of Array.from(picked)) {
      // webkitRelativePath is set when a whole folder is chosen, so dropping in a directory keeps its
      // structure instead of flattening it into a pile of loose files.
      const relative = (file as File & { webkitRelativePath?: string }).webkitRelativePath;
      const path = relative !== undefined && relative.length > 0
        ? relative.split("/").slice(1).join("/") || file.name
        : file.name;
      added.push(await readFileForSkill(file, path));
    }
    setFiles((current) => [
      ...current.filter((f) => !added.some((a) => a.fileName === f.fileName)),
      ...added,
    ]);
  };

  return (
    <div className="wf-dialog-backdrop" {...backdrop}>
      <div className="wf-dialog wf-preview" role="dialog" aria-modal="true" aria-label={`Files of ${skill.name}`}>
        <h2>{skill.name} - files</h2>
        {error !== null ? <ErrorBanner message={error} /> : null}
        {detail === null && error === null ? <LoadingState /> : null}
        {detail !== null ? (
          <>
            <p className="wf-preview-fact">
              A skill is a directory. <code>SKILL.md</code> is its instructions; everything else sits
              beside it at its own path, exactly as every agent expects to find it.
            </p>

            <label className="wf-field">
              <span>SKILL.md</span>
              <textarea
                value={body}
                rows={12}
                onChange={(e) => setBody(e.target.value)}
                aria-label="The skill's instructions"
              />
            </label>

            <ul className="wf-file-list">
              {files.length === 0 ? <li className="wf-off-note">No supporting files yet.</li> : null}
              {files.map((file) => (
                <li key={file.fileName}>
                  <code>{file.fileName}</code>
                  {file.encoding === "base64" ? <span className="wf-off-note"> binary</span> : null}
                  {file.executable === true ? <span className="wf-off-note"> executable</span> : null}
                  {file.encoding !== "base64" ? (
                    <button
                      className="wf-linklike"
                      onClick={() => setEditing(editing === file.fileName ? null : file.fileName)}
                    >
                      {editing === file.fileName ? "Done" : "Edit"}
                    </button>
                  ) : null}
                  <button
                    className="wf-linklike"
                    onClick={() => setFiles((c) => c.filter((f) => f.fileName !== file.fileName))}
                    aria-label={`Remove ${file.fileName}`}
                  >
                    Remove
                  </button>
                  {editing === file.fileName ? (
                    <textarea
                      value={file.content}
                      rows={10}
                      aria-label={`Content of ${file.fileName}`}
                      onChange={(e) =>
                        setFiles((c) =>
                          c.map((f) => (f.fileName === file.fileName ? { ...f, content: e.target.value } : f)),
                        )
                      }
                    />
                  ) : null}
                </li>
              ))}
            </ul>

            <div className="wf-dialog-actions">
              <input
                type="text"
                value={newPath}
                placeholder="references/notes.md"
                aria-label="Path of a new file"
                onChange={(e) => setNewPath(e.target.value)}
              />
              <Button
                variant="secondary"
                disabled={newPath.trim().length === 0}
                onClick={() => {
                  setFiles((c) => [...c, { fileName: newPath.trim(), content: "", encoding: "utf8" }]);
                  setEditing(newPath.trim());
                  setNewPath("");
                }}
              >
                Add a file
              </Button>
              <input
                type="file"
                multiple
                aria-label="Upload files"
                onChange={(e) => void addFiles(e.target.files)}
              />
            </div>

            <div className="wf-dialog-actions">
              <Button variant="secondary" onClick={onClose} disabled={busy}>
                Cancel
              </Button>
              <Button variant="secondary" onClick={() => void save(false)} disabled={busy}>
                Save as a draft
              </Button>
              {/* Publishing IS the deployment: from that moment every agent that fetches this skill
                  gets the new content, with nothing to install anywhere. */}
              <Button onClick={() => void save(true)} disabled={busy}>
                Save and publish
              </Button>
            </div>
          </>
        ) : null}
      </div>
    </div>
  );
}

// The standing answers to "how and when do these apply" - kept on the page because the register is
// where the question gets asked.
function Explainer() {
  return (
    <section className="wf-explainer" id="sk-explainer-panel">
      <h2>How skills reach your agents</h2>
      <dl>
        <dt>put where each agent looks</dt>
        <dd>
          When a session starts, its skills are placed in the folder that agent reads, so it finds
          them the same way it finds any other skill. They are refreshed every launch and matched to
          what this page says - switch one off and it is gone from the machine the next time a
          session starts.
        </dd>
        <dt>your own skills always win</dt>
        <dd>
          A skill you already keep on a machine is never touched or replaced. This library adds to
          what a machine has; it never takes it over.
        </dd>
        <dt>every agent, not just one</dt>
        <dd>
          The same library reaches every agent family, because they all read the same kind of skill
          folder - and every session is also told, in one line per skill, what the library holds.
        </dd>
        <dt>no restart, no release</dt>
        <dd>
          Publishing is the deployment: the next fetch, on every machine, gets the new version. A
          typo fixed here is fixed everywhere a moment later.
        </dd>
        <dt>built-ins are ours</dt>
        <dd>
          The skills DevThrottle ships are read-only, so they can never drift from what we shipped.
          Clone one to make it yours - the copy is fully editable and independent.
        </dd>
        <dt>off</dt>
        <dd>
          A skill you turn off is left out of every briefing and its fetch is refused, with a message
          saying you switched it off. Nothing is deleted, and the flip is instant both ways.
        </dd>
        <dt>your own skills still work</dt>
        <dd>
          This library is an additional source, not a replacement. A machine&apos;s own skills and a
          repository&apos;s own skills are untouched, and they take precedence on a name clash.
        </dd>
      </dl>
    </section>
  );
}

// The add dialog: name + one-line summary, nothing else. Submitting creates a DRAFT on the Gateway
// (invisible to the register and to every briefing until an agent fleshes it out and publishes) and
// shows the copyable handoff prompt. Errors surface inline and the dialog stays open.
function AddSkillDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [summary, setSummary] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [createdId, setCreatedId] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const id = suggestSkillId(name);
  const handoff = createdId === null
    ? ""
    : `Author the '${name.trim()}' skill (id ${createdId}) in the DevThrottle skill library. Pull it with: cc-devthrottle skill pull ${createdId} --dir <a working directory> - write its SKILL.md (the instructions an agent will follow), set the triggers in skill.json, add any supporting files under files/, then push and publish: cc-devthrottle skill push ${createdId} --dir <the directory> && cc-devthrottle skill publish ${createdId}`;

  // The backdrop dismisses only while the form is idle: dismissing DURING the create leaves the
  // draft half-born with its handoff prompt never shown, and dismissing the success state would
  // lose the prompt.
  const dismiss = useDismissOnBackdrop(createdId === null && !busy ? onClose : undefined);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const draft = await createSkill({ id, name: name.trim(), summary: summary.trim() });
      setCreatedId(draft.skillId);
      onCreated();
    } catch (err) {
      setError(describeAndReport(SURFACE, "create the skill", err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="wf-dialog-backdrop" role="presentation" {...dismiss}>
      <div className="wf-dialog" role="dialog" aria-modal="true" aria-label="Add skill">
        {createdId === null ? (
          <>
            <h2 className="wf-dialog-title">Add skill</h2>
            <p className="wf-dialog-hint">
              This creates a DRAFT. Your agents do the actual authoring - you will get the exact
              prompt to hand one. No agent sees the skill until it is published.
            </p>
            <label className="wf-field">
              <span>Name</span>
              <input
                type="text"
                value={name}
                autoFocus
                onChange={(e) => setName(e.target.value)}
                placeholder="Deploy the gateway"
              />
            </label>
            {id !== "" ? <p className="wf-dialog-hint">Id: <code>{id}</code></p> : null}
            <label className="wf-field">
              <span>What it does - one line, and this is the line every agent sees</span>
              <input
                type="text"
                value={summary}
                onChange={(e) => setSummary(e.target.value)}
                placeholder="Release the hosted Gateway and confirm it comes back healthy."
              />
            </label>
            {error !== null ? <p className="wf-dialog-error">{error}</p> : null}
            <div className="wf-dialog-actions">
              <Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button>
              <Button
                variant="primary"
                onClick={() => void submit()}
                disabled={busy || id === "" || summary.trim() === ""}
              >
                {busy ? "Creating..." : "Create draft"}
              </Button>
            </div>
          </>
        ) : (
          <>
            <h2 className="wf-dialog-title">Draft created</h2>
            <p className="wf-dialog-hint">
              <code>{createdId}</code> exists as a draft. Hand this to an agent to author it:
            </p>
            <pre className="wf-handoff">{handoff}</pre>
            <div className="wf-dialog-actions">
              <Button
                variant="secondary"
                onClick={() => {
                  void navigator.clipboard.writeText(handoff).then(() => setCopied(true));
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
