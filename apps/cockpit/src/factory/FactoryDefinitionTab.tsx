import { useCallback, useEffect, useRef, useState } from "react";
import {
  downloadFactoryDefinitionZip,
  FactoryDefinitionRefusal,
  getFactoryDefinitionHistory,
  getFactoryDefinitionTab,
  getFactoryDefinitionVersion,
  importFactoryDefinitionZip,
  publishFactoryDefinition,
  restoreFactoryDefinition,
  type FactoryDefinitionAction,
  type FactoryDefinitionChanges,
  type FactoryDefinitionFinding,
  type FactoryDefinitionHistoryView,
  type FactoryDefinitionPlan,
  type FactoryDefinitionPublished,
  type FactoryDefinitionTabView,
  type FactoryDefinitionTextDiff,
  type FactoryDefinitionVersionCard,
  type FactoryDefinitionVersionView,
} from "@devthrottle/client-core/factory/factoryDefinitionClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";
import { ToneChip } from "./FactoryParts";

const SURFACE = "cockpit-factory-definition";

// The factory page's Definition tab (Factory Definition mission, phase 6, section 5.7). It replaced the Documents tab:
// the published version, the draft, what changes, Publish, History, Restore, Export and Import.
//
// Rule 7: every word is the Gateway's (FactoryDefinitionTabFold, and the definition routes' own answers). This file
// lays the fields out and sends back exactly the versions a button named. It never decides what a version's state
// means, which version a button may act on, or what a publish will change - a button exists because the Gateway sent
// it, and what changes is the Gateway's comparison, drawn line by line.
//
// A REFUSAL IS SHOWN WHOLE. A publish or a restore the Gateway refuses closes its confirm and leaves the Gateway's
// sentence on the tab with every finding of the check by file and line, so the owner can fix each one where it is.

type Mode = { kind: "main" } | { kind: "history" } | { kind: "version"; version: number; back: "main" | "history" };

/** What the last act did, in the Gateway's words: its sentence, what it applied, and the check's notices. */
interface Outcome {
  text: string;
  applied: string[];
  notices: FactoryDefinitionFinding[];
}

/** What the last act was refused with: the reported sentence and the findings that came with it. */
interface Refused {
  lead: string;
  findings: FactoryDefinitionFinding[];
}

export interface FactoryDefinitionTabProps {
  factory: string;
}

export function FactoryDefinitionTab({ factory }: FactoryDefinitionTabProps) {
  const [view, setView] = useState<FactoryDefinitionTabView | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);
  const [mode, setMode] = useState<Mode>({ kind: "main" });
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [refused, setRefused] = useState<Refused | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [pending, setPending] = useState<{ kind: "publish" | "restore"; action: FactoryDefinitionAction } | null>(null);
  const [exporting, setExporting] = useState(false);
  const [importing, setImporting] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    setLoadError(null);
    getFactoryDefinitionTab(factory, ctrl.signal).then(setView, (err: unknown) => {
      if (!ctrl.signal.aborted) setLoadError(describeAndReport(SURFACE, "read this factory's definition", err));
    });
    return () => ctrl.abort();
  }, [factory, nonce]);

  const clearMessages = () => {
    setOutcome(null);
    setRefused(null);
    setActionError(null);
  };

  /** The owner moves to another view: what the last act said belongs to the view it was said on. */
  const go = (next: Mode) => {
    clearMessages();
    setMode(next);
  };

  /** A refusal that carries the Gateway's findings stays on the tab; anything else fails the confirm, which shows it. */
  const keepRefusal = useCallback((err: unknown, what: string): boolean => {
    if (!(err instanceof FactoryDefinitionRefusal)) return false;
    setRefused({ lead: describeAndReport(SURFACE, what, err), findings: err.findings });
    return true;
  }, []);

  const act = useCallback(async () => {
    if (pending === null) return;
    const { kind, action } = pending;
    const what = kind === "publish" ? `publish version ${action.version}` : `restore version ${action.version}`;
    clearMessages();
    let done: FactoryDefinitionPublished;
    try {
      done =
        kind === "publish"
          ? await publishFactoryDefinition(factory, action)
          : await restoreFactoryDefinition(factory, action);
    } catch (err) {
      if (keepRefusal(err, what)) {
        setMode({ kind: "main" });
        reload();
        return;
      }
      throw err;
    }
    setOutcome({ text: action.doneText, applied: done.applied ?? [], notices: done.findings });
    setMode({ kind: "main" });
    reload();
  }, [pending, factory, keepRefusal, reload]);

  const exportZip = useCallback(async (href: string) => {
    clearMessages();
    setExporting(true);
    try {
      await downloadFactoryDefinitionZip(href);
    } catch (err) {
      setActionError(describeAndReport(SURFACE, "export the definition", err));
    } finally {
      setExporting(false);
    }
  }, []);

  const importZip = useCallback(
    async (file: File) => {
      if (view === null) return;
      clearMessages();
      setImporting(true);
      try {
        const imported = await importFactoryDefinitionZip(view.import.href, file);
        setOutcome({ text: imported.doneText, applied: [], notices: imported.findings });
        setMode({ kind: "main" });
        reload();
      } catch (err) {
        if (!keepRefusal(err, "import the folder")) setActionError(describeAndReport(SURFACE, "import the folder", err));
      } finally {
        setImporting(false);
        if (fileInput.current !== null) fileInput.current.value = "";
      }
    },
    [view, keepRefusal, reload],
  );

  // error-reported-by: describeAndReport above
  if (loadError !== null) return <ErrorBanner message={loadError} onRetry={reload} />;
  if (view === null) return <LoadingState />;

  const messages = (
    <>
      {outcome !== null && (
        <div className="fa-notice" role="status" data-testid="fa-def-outcome">
          <p>{outcome.text}</p>
          {outcome.applied.length > 0 && (
            <ul className="fa-def-list">
              {outcome.applied.map((line, i) => (
                <li key={i}>{line}</li>
              ))}
            </ul>
          )}
          <Findings findings={outcome.notices} />
        </div>
      )}
      {refused !== null && (
        <div className="fa-memory-conflict" data-testid="fa-def-refused">
          <p className="fa-memory-conflict-lead" role="alert">
            {refused.lead}
          </p>
          <Findings findings={refused.findings} />
        </div>
      )}
      {actionError !== null && <ErrorBanner message={actionError} />}
    </>
  );

  return (
    <section className="fa-panel fa-def" data-testid="fa-definition">
      <div className="fa-def-head">
        <div>
          <h2 className="fa-section-title">{view.heading}</h2>
          <p data-testid="fa-def-state">{view.stateText}</p>
        </div>
        <div className="fa-inline-action">
          {view.historyLabel !== null && (
            <Button variant="secondary" data-testid="fa-def-history" onClick={() => go({ kind: "history" })}>
              {view.historyLabel}
            </Button>
          )}
          <Button
            variant="secondary"
            data-testid="fa-def-export"
            disabled={exporting}
            onClick={() => void exportZip(view.export.href)}
          >
            {exporting ? "Exporting..." : view.export.label}
          </Button>
          <Button
            variant="secondary"
            data-testid="fa-def-import"
            disabled={importing}
            onClick={() => fileInput.current?.click()}
          >
            {importing ? view.import.busyLabel : view.import.label}
          </Button>
          <input
            ref={fileInput}
            type="file"
            accept=".zip,application/zip"
            hidden
            data-testid="fa-def-import-file"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file !== undefined) void importZip(file);
            }}
          />
        </div>
      </div>

      {messages}

      {mode.kind === "history" && (
        <DefinitionHistory
          factory={factory}
          onClose={() => go({ kind: "main" })}
          onOpen={(version) => go({ kind: "version", version, back: "history" })}
          onExport={(href) => void exportZip(href)}
          onAct={(kind, action) => setPending({ kind, action })}
        />
      )}

      {mode.kind === "version" && (
        <DefinitionVersion
          factory={factory}
          version={mode.version}
          onExport={(href) => void exportZip(href)}
          onClose={() => go(mode.back === "history" ? { kind: "history" } : { kind: "main" })}
        />
      )}

      {mode.kind === "main" && (
        <>
          {view.emptyText !== null && (
            <p className="fa-def-empty" data-testid="fa-def-empty">
              {view.emptyText}
            </p>
          )}

          <div className="fa-def-cards">
            {view.published !== null && (
              <VersionCard card={view.published} onOpen={() => go({ kind: "version", version: view.published!.version, back: "main" })} />
            )}
            {view.draft !== null && (
              <VersionCard card={view.draft} onOpen={() => go({ kind: "version", version: view.draft!.version, back: "main" })} />
            )}
          </div>

          {view.changes !== null && (
            <div className="fa-def-changes" data-testid="fa-def-changes">
              <h3 className="fa-seat-subtitle">{view.changes.heading}</h3>
              {view.changes.cannotCompareText !== null && <div className="fa-warn">{view.changes.cannotCompareText}</div>}
              {view.changes.changes !== null && <ChangesView changes={view.changes.changes} />}
              {view.changes.plan !== null && <PlanView plan={view.changes.plan} />}
            </div>
          )}

          {view.publish !== null && (
            <div className="fa-inline-action">
              <Button
                variant="primary"
                data-testid="fa-def-publish"
                onClick={() => setPending({ kind: "publish", action: view.publish! })}
              >
                {view.publish.label}
              </Button>
            </div>
          )}

          <div className="fa-def-moves">
            <p className="fa-dim" data-testid="fa-def-export-note">
              {view.export.noteText}
            </p>
            {view.export.leavesOut.length > 0 && (
              <ul className="fa-def-list" data-testid="fa-def-leaves-out">
                {view.export.leavesOut.map((line, i) => (
                  <li key={i} className="mono">
                    {line}
                  </li>
                ))}
              </ul>
            )}
            <p className="fa-dim" data-testid="fa-def-import-note">
              {view.import.noteText}
            </p>
          </div>
        </>
      )}

      <p className="fa-footnote">{view.footnoteText}</p>

      <ConfirmDialog
        open={pending !== null}
        title={pending?.action.confirmTitle ?? ""}
        message={
          <div className="fa-confirm-lines" data-testid="fa-confirm-lines">
            {(pending?.action.confirmLines ?? []).map((line, i) => (
              <p key={i}>{line}</p>
            ))}
          </div>
        }
        confirmLabel={pending?.action.confirmLabel}
        busyLabel={pending?.action.busyLabel}
        danger={false}
        action={pending?.kind === "restore" ? "restore this version" : "publish this version"}
        onConfirm={act}
        onClose={() => setPending(null)}
      />
    </section>
  );
}

function VersionCard({ card, onOpen }: { card: FactoryDefinitionVersionCard; onOpen: () => void }) {
  return (
    <div className="fa-def-card" data-testid={`fa-def-card-${card.stateWord}`}>
      <div className="fa-def-card-head">
        <strong>{card.title}</strong> <ToneChip word={card.stateWord} tone={card.stateTone} />
      </div>
      <p className="fa-dim">{card.lineText}</p>
      {card.noteText !== null && <p className="dt-private">{card.noteText}</p>}
      <Button variant="secondary" onClick={onOpen}>
        {card.openLabel}
      </Button>
    </div>
  );
}

/** The check's findings, each where it is: file, line, and the Gateway's sentence. */
function Findings({ findings }: { findings: FactoryDefinitionFinding[] }) {
  if (findings.length === 0) return null;
  return (
    <table className="fa-table fa-def-findings" data-testid="fa-def-findings">
      <tbody>
        {findings.map((f, i) => (
          <tr key={i} className={`fa-def-finding-${f.severity}`}>
            <td className="mono">{f.line > 0 ? `${f.file}:${f.line}` : f.file}</td>
            <td>{f.severity}</td>
            <td>{f.message}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** What changes, exactly as the Gateway compared it: one sentence at the top, then each part, then the lines. */
export function ChangesView({ changes }: { changes: FactoryDefinitionChanges }) {
  return (
    <div className="fa-def-diff" data-testid="fa-def-what-changes">
      <p className="fa-def-summary" data-testid="fa-def-summary">
        {changes.summary}
      </p>
      {changes.factory.map((c) => (
        <Change key={`f-${c.field}`} sentence={c.sentence} diff={c.diff} />
      ))}
      {changes.seats.map((c) => (
        <Change key={`s-${c.seat}`} sentence={c.sentence} details={c.details} diff={c.briefDiff} />
      ))}
      {changes.starts.map((c, i) => (
        <Change key={`st-${c.seat}-${i}`} sentence={c.sentence} details={c.details} />
      ))}
      {changes.rules.map((c, i) => (
        <Change key={`r-${i}`} sentence={c.sentence} />
      ))}
      {changes.grants.map((c) => (
        <Change key={`g-${c.id}`} sentence={c.sentence} details={c.details} />
      ))}
      {changes.files.map((c) => (
        <Change key={`p-${c.path}`} sentence={c.sentence} diff={c.diff} />
      ))}
    </div>
  );
}

function Change({ sentence, details = [], diff = null }: { sentence: string; details?: string[]; diff?: FactoryDefinitionTextDiff | null }) {
  return (
    <div className="fa-def-change">
      <p>{sentence}</p>
      {details.length > 0 && (
        <ul className="fa-def-list">
          {details.map((d, i) => (
            <li key={i}>{d}</li>
          ))}
        </ul>
      )}
      {diff !== null && diff.lines.length > 0 && (
        <pre className="fa-def-lines dt-private">
          {diff.lines.map((l, i) => (
            <div key={i} className={`fa-def-line ${l.kind}`}>
              {l.marker === "" ? l.text : `${l.marker} ${l.text}`}
            </div>
          ))}
        </pre>
      )}
      {diff !== null && diff.note !== null && <p className="fa-dim">{diff.note}</p>}
    </div>
  );
}

function PlanView({ plan }: { plan: FactoryDefinitionPlan }) {
  return (
    <div className="fa-def-plan" data-testid="fa-def-plan">
      <p>{plan.heading}</p>
      {plan.steps.length > 0 && (
        <ul className="fa-def-list">
          {plan.steps.map((s, i) => (
            <li key={i}>{s}</li>
          ))}
        </ul>
      )}
      {plan.refused !== null && (
        <div className="fa-warn" data-testid="fa-def-plan-refused">
          {plan.refused}
        </div>
      )}
      <Findings findings={plan.findings} />
    </div>
  );
}

// The history: one page at a time, newest first. Each version carries the buttons the Gateway gave it - Open and
// Export on every one, Publish on a draft above the published version, Restore on a version older than it.
function DefinitionHistory({
  factory,
  onClose,
  onOpen,
  onExport,
  onAct,
}: {
  factory: string;
  onClose: () => void;
  onOpen: (version: number) => void;
  onExport: (href: string) => void;
  onAct: (kind: "publish" | "restore", action: FactoryDefinitionAction) => void;
}) {
  const [before, setBefore] = useState<number | null>(null);
  const [view, setView] = useState<FactoryDefinitionHistoryView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    setView(null);
    getFactoryDefinitionHistory(factory, before, ctrl.signal).then(setView, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, "read the definition's history", err));
    });
    return () => ctrl.abort();
  }, [factory, before, nonce]);

  // error-reported-by: describeAndReport above
  if (error !== null) return <ErrorBanner message={error} onRetry={() => setNonce((n) => n + 1)} />;
  if (view === null) return <LoadingState />;

  return (
    <div className="fa-def-history" data-testid="fa-def-history-view">
      <h3 className="fa-seat-subtitle">{view.heading}</h3>
      <p>{view.summaryText}</p>
      {view.pageText !== null && <p className="fa-dim">{view.pageText}</p>}
      {view.emptyText !== null ? (
        <p className="fa-dim">{view.emptyText}</p>
      ) : (
        <div className="fa-scroll">
          <table className="fa-table fa-def-history-table" data-testid="fa-def-history-table">
            <thead>
              <tr>
                {view.columns.map((c) => (
                  <th key={c}>{c}</th>
                ))}
                <th aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {view.rows.map((r) => (
                <tr key={r.version} data-testid={`fa-def-row-${r.version}`}>
                  <td>{r.title}</td>
                  <td>
                    <ToneChip word={r.stateWord} tone={r.stateTone} />
                  </td>
                  <td>{r.whoText}</td>
                  <td>{r.whenText}</td>
                  <td className="dt-private">{r.noteText ?? ""}</td>
                  <td>{r.sizeText}</td>
                  <td>
                    <div className="fa-def-actions">
                      <Button variant="secondary" onClick={() => onOpen(r.version)}>
                        {r.openLabel}
                      </Button>
                      <Button variant="secondary" onClick={() => onExport(r.exportHref)}>
                        {r.exportLabel}
                      </Button>
                      {r.publish !== null && (
                        <Button variant="primary" data-testid={`fa-def-row-publish-${r.version}`} onClick={() => onAct("publish", r.publish!)}>
                          {r.publish.label}
                        </Button>
                      )}
                      {r.restore !== null && (
                        <Button variant="secondary" data-testid={`fa-def-row-restore-${r.version}`} onClick={() => onAct("restore", r.restore!)}>
                          {r.restore.label}
                        </Button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div className="fa-inline-action">
        {view.newestLabel !== null && (
          <Button variant="secondary" onClick={() => setBefore(null)}>
            {view.newestLabel}
          </Button>
        )}
        {view.olderLabel !== null && view.nextBefore !== null && (
          <Button variant="secondary" onClick={() => setBefore(view.nextBefore)}>
            {view.olderLabel}
          </Button>
        )}
        <Button variant="secondary" onClick={onClose}>
          {view.closeLabel}
        </Button>
      </div>
    </div>
  );
}

/** One version with every file, each folded until opened. */
function DefinitionVersion({
  factory,
  version,
  onExport,
  onClose,
}: {
  factory: string;
  version: number;
  onExport: (href: string) => void;
  onClose: () => void;
}) {
  const [view, setView] = useState<FactoryDefinitionVersionView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    setView(null);
    getFactoryDefinitionVersion(factory, version, ctrl.signal).then(setView, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, `open version ${version}`, err));
    });
    return () => ctrl.abort();
  }, [factory, version, nonce]);

  // error-reported-by: describeAndReport above
  if (error !== null) return <ErrorBanner message={error} onRetry={() => setNonce((n) => n + 1)} />;
  if (view === null) return <LoadingState />;

  return (
    <div className="fa-def-version" data-testid="fa-def-version-view">
      <h3 className="fa-seat-subtitle">{view.title}</h3>
      <p className="fa-dim">{view.lineText}</p>
      {view.noteText !== null && <p className="dt-private">{view.noteText}</p>}
      {view.files.map((f) => (
        <details key={f.path} className="fa-def-file">
          <summary>
            <span className="mono">{f.path}</span> <span className="fa-dim">{f.sizeText}</span>
          </summary>
          <pre className="fa-memory-text dt-private">{f.text}</pre>
        </details>
      ))}
      <div className="fa-inline-action">
        <Button variant="secondary" onClick={() => onExport(view.exportHref)}>
          {view.exportLabel}
        </Button>
        <Button variant="secondary" onClick={onClose}>
          {view.closeLabel}
        </Button>
      </div>
    </div>
  );
}
